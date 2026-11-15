using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BurnSmith.Core;

/// <summary>
/// Where a ripped disc (an .iso, or a folder holding BDMV / VIDEO_TS) is damaged. A ripper fills the sectors it could not read with 0xFF (some with zeros):
/// such sectors are found in every file (zeros only in the streams, where a whole zero sector is never valid), each run placed in its file and, for a stream,
/// in time (its PCR, or the SCR of a DVD pack). The streams are also read packet by packet: lost sync, packets still encrypted (AACS / CSS not removed),
/// continuity breaks (a remark: authored discs have them too). With <c>decode</c> ffmpeg decodes each stream (short ones always, unless <c>decodeShort</c> is off):
/// a picture the decoder had to conceal is damage (a rotted still the packet scan cannot see), its other errors are a remark.
/// The image is read without mounting it: UDF (1.02 for a DVD, 2.50 with its metadata partition for a Blu-ray) is walked here.
/// </summary>
public static class DamageScan
{
    /// <summary>A file of the disc: where its bytes are in <see cref="Source"/> (At -1 = a sparse extent, zeros).</summary>
    sealed record Entry(string Name, string Source, long Size, List<(long At, long Length)> Parts, bool InImage);

    sealed class Found(Entry e)
    {
        public readonly Entry Entry = e;
        public readonly List<(long Start, long End, byte Fill)> Runs = [];
        public readonly List<(long Start, long End)> Lost = [];
        public readonly List<(long Start, long End)> NullUnits = [];
        public long LostPackets, Encrypted, EncryptedAt = -1, Cc, FfBytes, ZeroBytes;
        public readonly List<long> CcAt = [];
        public readonly List<long> TimeOff = [];
        public readonly List<double> TimeSec = [];
        public double? Duration;
        public string? Role, Playlist;
        public double PlaylistShift; // playlist time = clip time + this
        public readonly List<double> DecodeErrors = [], Concealed = [];
        public string? DecodeSample, DecodeFailed, ConcealSample;
        public bool Damaged => Runs.Count > 0 || LostPackets > 0 || Encrypted > 0 || Concealed.Count > 0 || NullDamage(this).Count > 0;
    }

    const int ShortClip = 64 << 20; // decoded by default: stills, menus, a few seconds of video
    const int S = 2048, Unit = 6144; // an AACS aligned unit: 32 packets of 192 bytes, 3 sectors

    /// <summary>
    /// An aligned unit made only of null packets (PID 0x1FFF): what a ripper puts for a unit it could not read (Judge Dredd's bonus clips). A muxer spreads its null
    /// packets among the others, so the official disc's clips have no such unit.
    /// </summary>
    internal static bool NullUnit(ReadOnlySpan<byte> unit)
    {
        for (var q = 0; q < Unit; q += 192)
            if (unit[q + 4] != 0x47 || (unit[q + 5] & 0x1F) != 0x1F || unit[q + 6] != 0xFF) return false;
        return true;
    }

    /// <summary>The report (one line per file with something to say, the clean ones on one line, a last summary line) and whether damage was found.</summary>
    public static (List<string> Lines, bool Damaged) Run(string path, bool decode = false, Action<string>? progress = null, bool decodeShort = true)
    {
        var clock = Stopwatch.StartNew();
        var entries = File.Exists(path) ? UdfFiles(path) : FolderFiles(path);
        var clips = ClipInfo(entries);
        var found = new List<Found>();
        foreach (var e in entries)
        {
            if (e.Name.EndsWith(".ssif", StringComparison.OrdinalIgnoreCase)) continue; // a 3D .ssif shares the sectors of its two .m2ts, read there
            if (e.Size > 256 << 20) progress?.Invoke($"lecture de {e.Name} ({e.Size / 1e9:0.0} Go)");
            var f = new Found(e);
            var clip = Kind(e) == 1 ? clips.GetValueOrDefault(Path.GetFileNameWithoutExtension(e.Name).ToUpperInvariant()) : null;
            if (clip != null) (f.Duration, f.Role, f.Playlist, f.PlaylistShift) = (clip.Duration, clip.Film ? "film" : null, clip.Playlist, clip.Shift);
            Scan(f, clip?.Start);
            if (decode && Kind(e) != 0) { progress?.Invoke($"décodage de {e.Name} par ffmpeg"); Decode(f, true); }
            else if (decodeShort && Kind(e) != 0 && e.Size <= ShortClip) Decode(f, false); // a still or a menu clip: decoding it costs nothing
            found.Add(f);
        }

        var said = found.Select(f => (f, Line: Say(f))).ToList();
        var lines = said.Where(x => x.Line != null).Select(x => x.Line!).ToList();
        var clean = said.Where(x => x.Line == null).Select(x => x.f).ToList();
        var streams = clean.Where(f => Kind(f.Entry) != 0).Select(f => Short(f.Entry)).ToList();
        var others = clean.Count - streams.Count;
        if (clean.Count > 0)
            lines.Add("sains : " + (streams.Count > 8 ? $"{streams.Count} flux" : string.Join(", ", streams)) + (streams.Count > 0 && others > 0 ? " et " : "") + (others > 0 ? $"{others} autres fichiers" : ""));
        var bad = found.Count(f => f.Damaged);
        var took = clock.Elapsed.TotalMinutes >= 1 ? $"{(int)clock.Elapsed.TotalMinutes} min {clock.Elapsed.Seconds:00}" : $"{clock.Elapsed.TotalSeconds:0} s";
        lines.Add(bad == 0 ? $"aucun dommage : {found.Count} fichiers lus en {took}" : $"dommages : {bad} fichier{(bad > 1 ? "s" : "")} abîmé{(bad > 1 ? "s" : "")} sur {found.Count} (lus en {took})");
        return (lines, bad > 0);
    }

    /// <summary>
    /// A made-up clip of 64 units, PCR every unit: clean, then with one unit of nulls in the middle, then with FF from unit 40; then a still and an mp3 stream (what ffmpeg takes a menu's IG for) made by ffmpeg, scrambled inside. The problems found, empty when right.
    /// </summary>
    public static List<string> SelfCheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"burnsmith-scan-{Environment.ProcessId}");
        var stream = Path.Combine(dir, "BDMV", "STREAM");
        Directory.CreateDirectory(stream);
        var problems = new List<string>();
        try
        {
            byte[] Clip(int nullUnit, int ffFrom)
            {
                var b = new byte[64 * Unit];
                for (int k = 0, cc = 0; k < b.Length / 192; k++)
                {
                    var p = k * 192;
                    var u = p / Unit;
                    if (u >= ffFrom) { b.AsSpan(p, 192).Fill(0xFF); continue; }
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), (uint)k * 100 + 1);
                    b[p + 4] = 0x47;
                    if (u == nullUnit || k % 7 == 6 && p % Unit != 0) { b[p + 5] = 0x1F; b[p + 6] = 0xFF; b[p + 7] = 0x10; b.AsSpan(p + 8, 184).Fill(0xFF); continue; } // scattered nulls are normal
                    b[p + 5] = 0x10; b[p + 6] = 0x11;
                    if (p % Unit == 0) { b[p + 7] = (byte)(0x30 | cc++ & 15); b[p + 8] = 7; b[p + 9] = 0x10; BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p + 10), (uint)(u * 90000 >> 1)); } // a PCR each second
                    else b[p + 7] = (byte)(0x10 | cc++ & 15);
                }
                return b;
            }
            var file = Path.Combine(stream, "00001.m2ts");
            void Expect(string what, byte[] clip, Func<string, bool> ok)
            {
                File.WriteAllBytes(file, clip);
                var (lines, damaged) = Run(Path.Combine(dir));
                var first = lines[0];
                if (!ok(first) || damaged != first.StartsWith("00001.m2ts")) problems.Add($"{what} : {first} (dommage : {damaged})");
            }
            Expect("clip sain", Clip(-1, 99), l => l.StartsWith("sains"));
            Expect("bloc de paquets nuls", Clip(20, 99), l => l.Contains("paquets nuls (1 bloc") && l.Contains("de 0:19 à 0:21"));
            Expect("FF jusqu'à la fin", Clip(-1, 40), l => l.Contains("illisible de 0:39 à la fin") && !l.Contains("paquets nuls"));
            // made by ffmpeg, then 6 packets in the middle scrambled past their headers: what rot does inside a picture, with no FF or null packet to see
            var ffmpeg = Tools.Find("ffmpeg.exe");
            byte[] Made(params string[] source)
            {
                Tools.Run(ffmpeg, ["-y", "-v", "error", .. source, "-mpegts_m2ts_mode", "1", "-f", "mpegts", file]);
                var b = File.ReadAllBytes(file);
                for (var k = b.Length / 384; k < b.Length / 384 + 6; k++)
                    for (var i = k * 192 + 12; i < k * 192 + 192; i++) b[i] = (byte)(i * 37);
                return b;
            }
            Expect("image fixe abîmée (décodée sans --decode)", Made("-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=25", "-frames:v", "1", "-c:v", "libx264", "-preset", "ultrafast", "-qp", "10"),
                   l => l.Contains("image abîmée") && l.Contains("vers 0:00"));
            Expect("flux pris pour du mp3 (menu IG)", Made("-f", "lavfi", "-i", "sine=d=2", "-c:a", "libmp3lame"), l => l.StartsWith("sains"));
        }
        finally { Directory.Delete(dir, true); }
        return problems;
    }

    /// <summary>0 another file, 1 a Blu-ray stream (192-byte packets), 2 a DVD stream (2048-byte packs).</summary>
    static int Kind(Entry e) => Path.GetExtension(e.Name).ToLowerInvariant() switch { ".m2ts" or ".mts" => 1, ".vob" => 2, _ => 0 };

    static string Short(Entry e) => Kind(e) != 0 ? Path.GetFileName(e.Name) : e.Name;

    static void Scan(Found f, double? clipStart)
    {
        var kind = Kind(f.Entry);
        using var s = Open(f.Entry);
        var buf = new byte[6144 * 1024]; // a whole number of sectors and of 192-byte packets
        var filled = new bool[buf.Length / S + 1];
        var cc = new Dictionary<int, int>();
        int pcrPid = -1;
        double? origin = kind == 2 ? 0 : clipStart; // a DVD's SCR as it is (it runs on from one VOB of a title to the next)
        long pos = 0;
        int n;
        void Mark(long at, double seconds)
        {
            origin ??= seconds;
            var t = Math.Max(0, seconds - origin.Value);
            if (f.TimeOff.Count == 0 || at - f.TimeOff[^1] >= 1 << 18 || Math.Abs(t - f.TimeSec[^1]) >= 0.5) { f.TimeOff.Add(at); f.TimeSec.Add(t); } // a slow bonus clip needs a mark each half second
        }
        while ((n = ReadFull(s, buf)) > 0)
        {
            if (kind != 1) // a Blu-ray stream is judged packet by packet below: its packets do not follow the sectors
                for (int o = 0; o < n; o += S)
                {
                    var sector = buf.AsSpan(o, Math.Min(S, n - o));
                    int fill = sector.IndexOfAnyExcept((byte)0xFF) < 0 ? 0xFF : kind != 0 && sector.Length == S && sector.IndexOfAnyExcept((byte)0) < 0 ? 0 : -1;
                    filled[o / S] = fill >= 0;
                    if (fill >= 0) Filled(f, pos + o, sector.Length, fill);
                }
            if (kind == 1)
                for (int u = 0; u + Unit <= n; u += Unit)
                    if (NullUnit(buf.AsSpan(u, Unit)))
                    {
                        var at = pos + u;
                        if (f.NullUnits.Count > 0 && f.NullUnits[^1].End == at) f.NullUnits[^1] = (f.NullUnits[^1].Start, at + Unit);
                        else f.NullUnits.Add((at, at + Unit));
                    }
            if (kind == 1)
                for (int p = 0; p + 192 <= n; p += 192)
                {
                    var at = pos + p;
                    // what a ripper writes for what it could not read: FF (or zeros), time stamp and sync byte included
                    var head = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p));
                    int fill = head == 0xFFFFFFFF && buf[p + 4] == 0xFF ? 0xFF : head == 0 && buf[p + 4] == 0 ? 0 : -1;
                    if (fill >= 0) { Filled(f, at, 192, fill); cc.Clear(); continue; }
                    if (buf[p + 4] != 0x47) { Lost(f, at, 192); cc.Clear(); continue; }
                    // the null packets the disc's authoring left between the unreadable ones (time 0, payload FF) belong to the same unreadable stretch
                    if (buf[p + 5] == 0x1F && buf[p + 6] == 0xFF && f.Runs.Count > 0 && f.Runs[^1].End == at) { Filled(f, at, 192, f.Runs[^1].Fill, false); continue; }
                    int b3 = buf[p + 7], pid = (buf[p + 5] & 0x1F) << 8 | buf[p + 6], afc = b3 >> 4 & 3;
                    if ((b3 & 0xC0) != 0) { if (f.Encrypted++ == 0) f.EncryptedAt = at; continue; }
                    var discontinuity = false;
                    if ((afc & 2) != 0 && buf[p + 8] > 0)
                    {
                        int flags = buf[p + 9];
                        discontinuity = (flags & 0x80) != 0;
                        if ((flags & 0x10) != 0 && buf[p + 8] >= 7 && (pcrPid < 0 || pcrPid == pid))
                        {
                            pcrPid = pid;
                            long b = (long)buf[p + 10] << 25 | (long)buf[p + 11] << 17 | (long)buf[p + 12] << 9 | (long)buf[p + 13] << 1 | (long)(buf[p + 14] >> 7);
                            Mark(at, b / 90000.0);
                        }
                    }
                    if (pid != 0x1FFF && (afc & 1) != 0)
                    {
                        int c = b3 & 15;
                        if (!discontinuity && cc.TryGetValue(pid, out var prev) && c != prev && c != (prev + 1 & 15)) { f.Cc++; if (f.CcAt.Count < 100) f.CcAt.Add(at); }
                        cc[pid] = c;
                    }
                }
            else if (kind == 2)
                for (int p = 0; p + S <= n; p += S)
                {
                    if (filled[p / S]) continue;
                    var at = pos + p;
                    if (buf[p] != 0 || buf[p + 1] != 0 || buf[p + 2] != 1 || buf[p + 3] != 0xBA) { Lost(f, at, S); continue; }
                    long scr = (long)(buf[p + 4] >> 3 & 7) << 30 | (long)(buf[p + 4] & 3) << 28 | (long)buf[p + 5] << 20 | (long)(buf[p + 6] >> 3) << 15
                             | (long)(buf[p + 6] & 3) << 13 | (long)buf[p + 7] << 5 | (long)(buf[p + 8] >> 3);
                    Mark(at, scr / 90000.0);
                    var pes = p + 14 + (buf[p + 13] & 7);
                    if (pes + 7 < p + S && buf[pes] == 0 && buf[pes + 1] == 0 && buf[pes + 2] == 1 && buf[pes + 3] is >= 0xC0 and <= 0xEF or 0xBD && (buf[pes + 6] & 0x30) != 0)
                        if (f.Encrypted++ == 0) f.EncryptedAt = at;
                }
            pos += n;
        }
        // shortcut: a clip with several STC sequences (PCR starting again) gets times after the restart wrong; none seen on retail films
        if (f.Duration == null && f.TimeSec.Count > 0 && kind == 1) f.Duration = f.TimeSec[^1];
    }

    static void Filled(Found f, long at, int size, int fill, bool counts = true)
    {
        if (counts && fill == 0xFF) f.FfBytes += size;
        else if (counts) f.ZeroBytes += size;
        if (f.Runs.Count > 0 && f.Runs[^1].End == at && f.Runs[^1].Fill == fill) f.Runs[^1] = (f.Runs[^1].Start, at + size, (byte)fill);
        else f.Runs.Add((at, at + size, (byte)fill));
    }

    /// <summary>The null units that are damage: not those of a stretch already told as FF, nor padding that ends the file.</summary>
    static List<(long Start, long End)> NullDamage(Found f) =>
        f.NullUnits.Where(x => !f.Runs.Any(r => x.End > r.Start - (1 << 20) && x.Start < r.End + (1 << 20))).ToList();

    static void Lost(Found f, long at, int size)
    {
        f.LostPackets++;
        if (f.Lost.Count > 0 && f.Lost[^1].End == at) f.Lost[^1] = (f.Lost[^1].Start, at + size);
        else f.Lost.Add((at, at + size));
    }

    /// <summary>The line about one file, null when there is nothing to say.</summary>
    static string? Say(Found f)
    {
        var e = f.Entry;
        var parts = new List<string>();
        string Where(long off)
        {
            if (f.TimeOff.Count == 0) return $"l'octet {Num(off)}";
            var i = f.TimeOff.BinarySearch(off);
            var t = f.TimeSec[i >= 0 ? i : Math.Max(0, ~i - 1)];
            return Clock(t) + (f.Playlist != null ? $" ({Clock(t + f.PlaylistShift)} dans {f.Playlist})" : "");
        }
        string From(long off) => off == 0 ? "du début" : "de " + Where(off);
        string To(long end) => end >= e.Size ? "à la fin" : "à " + Where(end);
        string Stretch(long start, long end) => start > 0 && end < e.Size && Where(start) == Where(end) ? "à " + Where(start) : From(start) + " " + To(end);
        // stretches less than 1 MB apart are one damaged place (a ripper's retries leave readable crumbs in it), with the packets that lost their sync there
        var places = new List<(long Start, long End)>();
        foreach (var (start, end, _) in f.Runs)
            if (places.Count > 0 && start - places[^1].End < 1 << 20) places[^1] = (places[^1].Start, end);
            else places.Add((start, end));
        var lost = f.Lost.Where(l => !places.Any(x => l.End > x.Start - (1 << 20) && l.Start < x.End + (1 << 20))).ToList();
        if (places.Count > 0)
        {
            var filler = string.Join(" et ", new[] { f.FfBytes > 0 ? $"{Size(f.FfBytes)} remplis de FF" : null, f.ZeroBytes > 0 ? $"{Size(f.ZeroBytes)} remplis de zéros" : null }.Where(x => x != null));
            var total = places.Sum(x => x.End - x.Start);
            var span = total > f.FfBytes + f.ZeroBytes ? $"{Size(total)} dont {filler}" : filler;
            var sectors = e.InImage ? $", secteur {Num(Sector(e, places[0].Start))} et suivants" : "";
            parts.Add(places.Count == 1 ? $"illisible {Stretch(places[0].Start, places[0].End)}, {span}{sectors}"
                : $"illisible à {places.Count} endroits, {span} : {string.Join(", ", places.Take(5).Select(x => Stretch(x.Start, x.End)))}{(places.Count > 5 ? " etc." : "")}{sectors}");
        }
        if (NullDamage(f) is { Count: > 0 } nulls)
        {
            var units = nulls.Sum(x => x.End - x.Start) / Unit;
            parts.Add($"illisible {(nulls.Count == 1 ? "à 1 endroit" : $"à {nulls.Count} endroits")}, remplacé par des paquets nuls ({Num(units)} bloc{(units > 1 ? "s" : "")} de 6 144 octets, {Size(units * Unit)}) : "
                      + string.Join(", ", nulls.Take(5).Select(x => Stretch(x.Start, x.End))) + (nulls.Count > 5 ? " etc." : "")
                      + (e.InImage ? $", secteur {Num(Sector(e, nulls[0].Start))} et suivants" : ""));
        }
        var lostPackets = lost.Sum(l => (l.End - l.Start) / (Kind(e) == 2 ? S : 192));
        if (lostPackets > 0)
            parts.Add($"synchronisation perdue sur {Num(lostPackets)} paquet{(lostPackets > 1 ? "s" : "")} ({lost.Count} zone{(lost.Count > 1 ? "s" : "")}, {Stretch(lost[0].Start, lost[0].End)}{(lost.Count > 1 ? " pour la première" : "")})");
        if (f.Encrypted > 0) parts.Add($"{Num(f.Encrypted)} paquets encore chiffrés ({(Kind(e) == 2 ? "CSS" : "AACS")} non retiré), dès {Where(f.EncryptedAt)}");
        if (f.Cc > 0) parts.Add($"à noter : {Num(f.Cc)} rupture{(f.Cc > 1 ? "s" : "")} de continuité, vers {string.Join(", ", f.CcAt.Select(Where).Distinct().Take(3))}{(f.CcAt.Select(Where).Distinct().Count() > 3 ? " etc." : "")} (souvent d'origine, pas un dommage)");
        if (f.Concealed.Count > 0)
            parts.Add($"image abîmée (le décodeur a dû masquer {f.Concealed.Count} erreur{(f.Concealed.Count > 1 ? "s" : "")}, une platine montre le défaut), vers {string.Join(", ", f.Concealed.Select(Clock).Distinct().Take(5))}{(f.Concealed.Select(Clock).Distinct().Count() > 5 ? " etc." : "")} (ex. « {f.ConcealSample} »)");
        if (f.DecodeFailed != null) parts.Add($"décodage impossible : {f.DecodeFailed}");
        if (f.DecodeErrors.Count > 0)
        {
            // errors within 5 s of each other are one place
            var spots = new List<(double From, double To, int Count)>();
            foreach (var t in f.DecodeErrors)
                if (spots.Count > 0 && t - spots[^1].To <= 5) spots[^1] = (spots[^1].From, t, spots[^1].Count + 1);
                else spots.Add((t, t, 1));
            var shown = spots.Take(5).Select(p => p.From == p.To ? Clock(p.From) : $"{Clock(p.From)}-{Clock(p.To)}");
            parts.Add($"à noter : {f.DecodeErrors.Count} erreur{(f.DecodeErrors.Count > 1 ? "s" : "")} de décodage, vers {string.Join(", ", shown)}{(spots.Count > 5 ? $" et {spots.Count - 5} autre{(spots.Count > 6 ? "s" : "")} endroit{(spots.Count > 6 ? "s" : "")}" : "")} (ex. « {f.DecodeSample} »)");
        }
        if (parts.Count == 0) return null;
        var about = new[] { f.Role, f.Duration is { } d && d > 0 ? Length(d) : null }.Where(x => x != null).ToList();
        return $"{Short(e)}{(about.Count > 0 ? $" ({string.Join(", ", about)})" : "")} : {string.Join(" ; ", parts)}";
    }

    static long Sector(Entry e, long off)
    {
        foreach (var (at, length) in e.Parts) { if (off < length) return at < 0 ? -1 : (at + off) / S; off -= length; }
        return -1;
    }

    static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    static string Num(long n) => n.ToString("N0", Fr).Replace(' ', ' ').Replace(' ', ' ');
    static string Size(long bytes) => (bytes >= 1e9 ? (bytes / 1e9).ToString("0.0", Fr) + " Go" : bytes >= 1e6 ? (bytes / 1e6).ToString("0.0", Fr) + " Mo" : Num(bytes) + " octets");
    static string Clock(double s) => $"{(int)(s / 60)}:{(int)(s % 60):00}";
    static string Length(double s) => s >= 3600 ? $"{(int)(s / 3600)} h {(int)(s % 3600 / 60):00}" : $"{Math.Max(1, (int)Math.Round(s / 60))} min";

    // ---- the clips' times (CLIPINF) and the film (the longest playlist)

    sealed record Clip(double? Start, double? Duration, bool Film, string? Playlist, double Shift);

    static Dictionary<string, Clip> ClipInfo(List<Entry> entries)
    {
        var starts = new Dictionary<string, (double Start, double End)>();
        foreach (var e in entries.Where(e => e.Name.EndsWith(".clpi", StringComparison.OrdinalIgnoreCase) && !e.Name.Contains("BACKUP", StringComparison.OrdinalIgnoreCase) && e.Size < 16 << 20))
        {
            var b = ReadAll(e);
            try
            {
                int seq = (int)Be32(b, 8);
                if (b[seq + 5] == 0) continue; // no ATC sequence
                int stc = seq + 6 + 6; // the first ATC sequence's STC sequences
                int count = b[seq + 6 + 4];
                if (count == 0) continue;
                starts[Path.GetFileNameWithoutExtension(e.Name).ToUpperInvariant()] = (Be32(b, stc + 6) / 45000.0, Be32(b, stc + (count - 1) * 14 + 10) / 45000.0);
            }
            catch (IndexOutOfRangeException) { } // a damaged clip information file: no times
        }
        // the film: the longest clip of the longest playlist
        (string Name, List<(string Clip, double In, double Out)> Items)? longest = null;
        foreach (var e in entries.Where(e => e.Name.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase) && !e.Name.Contains("BACKUP", StringComparison.OrdinalIgnoreCase) && e.Size < 16 << 20))
        {
            var b = ReadAll(e);
            var items = new List<(string, double, double)>();
            try
            {
                int pl = (int)Be32(b, 8), count = b[pl + 6] << 8 | b[pl + 7], o = pl + 10;
                for (var i = 0; i < count; i++) { items.Add((Encoding.ASCII.GetString(b, o + 2, 5), Be32(b, o + 14) / 45000.0, Be32(b, o + 18) / 45000.0)); o += 2 + (b[o] << 8 | b[o + 1]); }
            }
            catch (ArgumentException) { continue; }
            catch (IndexOutOfRangeException) { continue; }
            if (longest == null || items.Sum(x => x.Item3 - x.Item2) > longest.Value.Items.Sum(x => x.Out - x.In)) longest = (Path.GetFileName(e.Name), items);
        }
        var clips = starts.ToDictionary(kv => kv.Key, kv => new Clip(kv.Value.Start, kv.Value.End - kv.Value.Start, false, null, 0));
        if (longest is { } film && film.Items.Count > 0)
        {
            var main = film.Items.MaxBy(x => x.Out - x.In);
            var start = starts.TryGetValue(main.Clip, out var st) ? st.Start : (double?)null;
            // a film in several clips: the time in the playlist too, when the clip plays once in it
            var once = film.Items.Count(x => x.Clip == main.Clip) == 1 && film.Items.Count > 1 && start != null;
            var before = film.Items.TakeWhile(x => x != main).Sum(x => x.Out - x.In);
            clips[main.Clip] = new Clip(start, clips.TryGetValue(main.Clip, out var known) ? known.Duration : null, true, once ? film.Name : null, once ? before + start!.Value - main.In : 0);
        }
        return clips;
    }

    // ---- ffmpeg

    /// <summary>ffmpeg decodes the stream; <paramref name="asked"/> false (a short clip decoded by default): ffmpeg missing or failing is not said.</summary>
    static void Decode(Found f, bool asked)
    {
        string exe;
        try { exe = Tools.Find("ffmpeg.exe"); }
        catch (FileNotFoundException e) { if (asked) f.DecodeFailed = e.Message; return; }
        var psi = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        // shortcut: ffmpeg decodes the streams it picks (the main video and one audio), not every track; an error gets the time ffmpeg had written out then, a few seconds off
        foreach (var x in new[] { "-hide_banner", "-nostats", "-loglevel", "level+warning", "-progress", "pipe:1", "-stats_period", "0.2","-f", Kind(f.Entry) == 1 ? "mpegts" : "mpeg", "-i", "pipe:0", "-f", "null", "-" })
            psi.ArgumentList.Add(x);
        using var p = new Process { StartInfo = psi };
        double now = 0;
        string? last = null;
        var nothing = false;
        p.OutputDataReceived += (_, e) => { if (e.Data?.StartsWith("out_time_us=") == true && long.TryParse(e.Data[12..], out var us)) Volatile.Write(ref now, us / 1e6); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data?.Contains("does not contain any stream") == true) { nothing = true; return; } // no video or audio stream (a menu clip ffmpeg does not know)
            if (string.IsNullOrWhiteSpace(e.Data) || e.Data.StartsWith("[null @") || e.Data.StartsWith("[out#")) return; // the null output's own complaints (time stamps) are not the disc's
            // an interactive-graphics (menu) stream is misprobed as mp3 and fails the same on the official disc: a Blu-ray never carries mp3
            if (Kind(f.Entry) == 1 && Regex.IsMatch(e.Data, @"[\[/:]mp3(float)? @")) return;
            var said = Regex.Replace(e.Data, @" @ (0x)?[0-9a-fA-F]+\]| \[(warning|error|fatal)\]", m => m.Value.StartsWith(" @") ? "]" : "").Trim();
            lock (f.DecodeErrors)
                if (e.Data.Contains("concealing") || e.Data.Contains("corrupt decoded frame")) { f.Concealed.Add(Volatile.Read(ref now)); f.ConcealSample ??= said; }
                else if (Regex.IsMatch(e.Data, @"\[(error|fatal)\]")) { f.DecodeErrors.Add(Volatile.Read(ref now)); f.DecodeSample ??= said; last = e.Data; }
        };
        p.Start();
        Tools.TieToApp(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { using var s = Open(f.Entry); s.CopyTo(p.StandardInput.BaseStream, 1 << 20); }
        catch (IOException) { } // ffmpeg stopped reading
        finally { try { p.StandardInput.Close(); } catch (IOException) { } }
        p.WaitForExit();
        if (nothing || p.ExitCode != 0 && (f.DecodeErrors.Count <= 1 || !asked))
        {
            if (asked && !nothing) f.DecodeFailed = $"ffmpeg sort avec le code {p.ExitCode} ({last})";
            f.DecodeErrors.Clear();
        }
        f.Concealed.Sort();
        f.DecodeErrors.Sort();
    }

    // ---- reading the files, from a folder or inside an image

    sealed class Extents(FileStream f, List<(long At, long Length)> parts) : Stream
    {
        int i;
        long inPart;
        public override int Read(byte[] buffer, int offset, int count)
        {
            while (i < parts.Count && inPart >= parts[i].Length) { i++; inPart = 0; }
            if (i == parts.Count) return 0;
            var (at, length) = parts[i];
            var n = (int)Math.Min(count, length - inPart);
            if (at < 0) Array.Clear(buffer, offset, n);
            else
            {
                f.Position = at + inPart;
                n = f.Read(buffer, offset, n);
                if (n == 0) throw new EndOfStreamException("l'image est plus courte que les fichiers qu'elle annonce : elle est tronquée");
            }
            inPart += n;
            return n;
        }
        protected override void Dispose(bool disposing) { if (disposing) f.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    static Stream Open(Entry e) => new Extents(new FileStream(e.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan), e.Parts);

    static byte[] ReadAll(Entry e) { using var s = Open(e); var b = new byte[e.Size]; ReadFull(s, b); return b; }

    static int ReadFull(Stream s, byte[] b)
    {
        int total = 0, r;
        while (total < b.Length && (r = s.Read(b, total, b.Length - total)) > 0) total += r;
        return total;
    }

    static List<Entry> FolderFiles(string dir)
    {
        if (!Directory.Exists(dir)) throw new FileNotFoundException($"ni image ni dossier : {dir}");
        var root = Path.GetFullPath(dir);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => new Entry(Path.GetRelativePath(root, f), f, new FileInfo(f).Length, [(0, new FileInfo(f).Length)], false)).ToList();
    }

    static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    static uint Be32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));

    /// <summary>The files of a UDF image (ECMA-167 / OSTA UDF 1.02 to 2.60: physical, sparable and metadata partitions), with where their bytes are.</summary>
    static List<Entry> UdfFiles(string iso)
    {
        using var f = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] Sector(long n) { var b = new byte[S]; f.Position = n * S; f.ReadExactly(b); return b; }
        byte[] Read(List<(long At, long Length)> runs) { if (runs.Count == 0) throw new InvalidDataException("UDF : bloc hors de sa partition"); var b = new byte[S]; f.Position = runs[0].At; f.ReadExactly(b); return b; }
        if (f.Length < 257 * S) throw new InvalidDataException("image trop courte pour être un disque UDF");
        var anchor = Sector(256);
        if (U16(anchor, 0) != 2) throw new InvalidDataException("pas de point d'ancrage UDF au secteur 256 : pas une image de Blu-ray ou de DVD");
        var starts = new Dictionary<int, long>();
        byte[]? lvd = null;
        for (long i = 0, at = U32(anchor, 20); i < U32(anchor, 16) / S; i++)
        {
            var d = Sector(at + i);
            var tag = U16(d, 0);
            if (tag == 5) starts[U16(d, 22)] = U32(d, 188);
            else if (tag == 6) lvd ??= d;
            else if (tag == 8) break;
        }
        if (lvd == null) throw new InvalidDataException("UDF : pas de descripteur de volume logique");

        // each partition reference gives where a run of its blocks is, in bytes of the image
        var maps = new List<Func<long, long, List<(long At, long Length)>>>();
        for (int k = 0, o = 440; k < U32(lvd, 268); k++, o += lvd[o + 1])
        {
            var type = lvd[o];
            var id = type == 2 ? Encoding.ASCII.GetString(lvd, o + 5, 23).TrimEnd('\0', ' ') : "";
            var number = U16(lvd, type == 1 ? o + 4 : o + 38);
            if (!starts.TryGetValue(number, out var start)) throw new InvalidDataException($"UDF : la partition {number} n'est pas décrite");
            if (type == 1 || id == "*UDF Sparable Partition") // shortcut: a sparable partition's spared blocks are not followed (a pressed or ripped disc has none)
                maps.Add((lbn, length) => [((start + lbn) * S, length)]);
            else if (id == "*UDF Metadata Partition")
            {
                var meta = Allocation(Sector(start + U32(lvd, o + 40)), 0, (_, lbn, length) => [((start + lbn) * S, length)], Read, out _, out _);
                maps.Add((lbn, length) =>
                {
                    var runs = new List<(long At, long Length)>();
                    var at = lbn * S;
                    foreach (var (pAt, pLength) in meta)
                    {
                        if (length <= 0) break;
                        if (at >= pLength) { at -= pLength; continue; }
                        var n = Math.Min(length, pLength - at);
                        runs.Add((pAt + at, n));
                        length -= n;
                        at = 0;
                    }
                    return runs;
                });
            }
            else throw new NotSupportedException($"UDF : partition « {id} » non prise en charge (disque gravé par session ?)");
        }
        List<(long At, long Length)> Map(int part, long lbn, long length) => part < maps.Count ? maps[part](lbn, length) : throw new InvalidDataException($"UDF : référence de partition {part} inconnue");
        byte[] Block(int part, long lbn) => Read(Map(part, lbn, S));

        var fsd = Block(U16(lvd, 256), U32(lvd, 252));
        if (U16(fsd, 0) != 256) throw new InvalidDataException("UDF : pas de descripteur d'ensemble de fichiers");
        var files = new List<Entry>();
        void Walk(int part, long lbn, string path, int depth)
        {
            var fe = Block(part, lbn);
            var parts = Allocation(fe, part, Map, Read, out var size, out var embedded);
            if (fe[27] is not (4 or 13)) { files.Add(new Entry(path, iso, size, parts, true)); return; }
            if (depth > 16) return;
            byte[] data;
            if (embedded != null) data = embedded;
            else { using var s = new Extents(new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read), parts); data = new byte[size]; ReadFull(s, data); }
            for (var p = 0; p + 38 <= data.Length;)
            {
                if (U16(data, p) != 257) break;
                int characteristics = data[p + 18], nameLength = data[p + 19], iu = U16(data, p + 36);
                var name = Cs0(data, p + 38 + iu, nameLength);
                long child = U32(data, p + 24);
                int childPart = U16(data, p + 28);
                p += (38 + iu + nameLength + 3) & ~3;
                if ((characteristics & 0x0C) != 0 || name.Length == 0) continue; // parent, deleted
                Walk(childPart, child, path.Length == 0 ? name : path + "\\" + name, depth + 1);
            }
        }
        Walk(U16(fsd, 408), U32(fsd, 404), "", 0);
        return files;
    }

    /// <summary>
    /// The bytes of a (extended) file entry: its extents (short ones in its own partition <paramref name="own"/>, long ones in the partition they name), followed
    /// through allocation extent descriptors, or its data when embedded in the entry.
    /// </summary>
    static List<(long At, long Length)> Allocation(byte[] fe, int own, Func<int, long, long, List<(long At, long Length)>> map, Func<List<(long At, long Length)>, byte[]> read,
                                                   out long size, out byte[]? embedded)
    {
        var tag = U16(fe, 0);
        int ad, start;
        if (tag == 261) { ad = (int)U32(fe, 172); start = 176 + (int)U32(fe, 168); }
        else if (tag == 266) { ad = (int)U32(fe, 212); start = 216 + (int)U32(fe, 208); }
        else throw new InvalidDataException($"UDF : entrée de fichier attendue, étiquette {tag} trouvée");
        size = (long)BinaryPrimitives.ReadUInt64LittleEndian(fe.AsSpan(56));
        var type = U16(fe, 34) & 7;
        embedded = type == 3 ? fe.AsSpan(start, Math.Min(ad, (int)size)).ToArray() : null;
        var runs = new List<(long At, long Length)>();
        if (type == 3) return runs;
        var step = type == 1 ? 16 : 8;
        var b = fe;
        for (int p = start, end = start + ad; p + step <= end;)
        {
            var raw = U32(b, p);
            long length = raw & 0x3FFFFFFF, lbn = U32(b, p + 4);
            var kind = raw >> 30;
            if (length == 0) break;
            var part = type == 1 ? U16(b, p + 8) : own;
            if (kind == 3) { b = read(map(part, lbn, S)); p = 24; end = 24 + (int)U32(b, 20); continue; }
            if (kind == 0) runs.AddRange(map(part, lbn, length));
            else runs.Add((-1, length)); // allocated but not recorded: reads as zeros
            p += step;
        }
        return runs;
    }

    static string Cs0(byte[] b, int o, int length)
    {
        if (length == 0) return "";
        return b[o] == 16 ? Encoding.BigEndianUnicode.GetString(b, o + 1, length - 1) : Encoding.Latin1.GetString(b, o + 1, length - 1);
    }
}
