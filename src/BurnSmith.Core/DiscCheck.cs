using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace BurnSmith.Core;

/// <summary>
/// "Test before burning": what can be told from the files of a finished disc folder (and its ISO) without a player. Each problem is one line; an empty list means
/// the structure looks right. It catches a build that stopped half way, a file that went missing, a stream that is not cut in whole packets, a DVD whose IFO
/// no longer matches its VOB: not whether a player will like the disc.
/// </summary>
public static class DiscCheck
{
    /// <summary>The problems of a disc folder (BDMV, AVCHD\BDMV, or VIDEO_TS inside it); with <paramref name="mediaGb"/> (the nominal size of the disc: 25 for a BD25) files that do not fit it.</summary>
    public static List<string> Folder(string dir, double mediaGb = 0)
    {
        var problems = new List<string>();
        if (Directory.Exists(Path.Combine(dir, "BDMV"))) CheckBluRay(Path.Combine(dir, "BDMV"), problems);
        else if (Directory.Exists(Path.Combine(dir, "AVCHD", "BDMV"))) CheckBluRay(Path.Combine(dir, "AVCHD", "BDMV"), problems);
        else if (Directory.Exists(Path.Combine(dir, "VIDEO_TS"))) CheckDvd(Path.Combine(dir, "VIDEO_TS"), problems);
        else problems.Add("ni BDMV ni VIDEO_TS dans le dossier");
        if (mediaGb > 0 && Directory.Exists(dir)) Fits(new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length), "les fichiers du disque", mediaGb, problems);
        return problems;
    }

    /// <summary>
    /// Bytes over what the disc holds. The nominal size is a floor of the real one (BD25 25.03 GB, DVD+R 4.70 GB, DVD-R DL 8.54 GB, BD50 50.05 GB), so what passes here fits
    /// any disc of that name.
    /// </summary>
    static void Fits(long bytes, string what, double mediaGb, List<string> problems)
    {
        if (bytes > mediaGb * 1e9) problems.Add($"{what} ({bytes / 1e9:0.00} Go) dépassent la capacité du disque ({mediaGb:0.##} Go)");
    }

    /// <summary>The problems of an image: sectors, volume descriptors, Blu-ray playlists and menu targets read from UDF, and whether the files fit.</summary>
    public static List<string> Image(string file, string? folder = null, double mediaGb = 0)
    {
        var problems = new List<string>();
        if (!File.Exists(file)) return ["l'image ISO n'existe pas"];
        using var f = File.OpenRead(file);
        if (f.Length % 2048 != 0) problems.Add("la taille de l'image n'est pas un nombre entier de secteurs de 2048 octets");
        if (mediaGb > 0) Fits(f.Length, "l'image ISO", mediaGb, problems);
        var head = new byte[8];
        var udf = false;
        f.Position = 16 * 2048 + 1;
        if (f.Length < 17 * 2048 || f.Read(head) < 5 || Encoding.ASCII.GetString(head, 0, 5) is not ("CD001" or "BEA01")) problems.Add("pas de descripteur de volume (ISO 9660 ou UDF) au secteur 16");
        if (f.Length > 257 * 2048 + 2)
        {
            f.Position = 256 * 2048;
            var anchor = new byte[2];
            if (f.Read(anchor) < 2 || anchor[0] != 2 || anchor[1] != 0) problems.Add("pas de point d'ancrage UDF au secteur 256");
            else
            {
                udf = true;
                f.Position = f.Length - 2048; // the second anchor is in the last sector: an image cut short has none
                if (f.Read(anchor) < 2 || anchor[0] != 2 || anchor[1] != 0) problems.Add("pas de second point d'ancrage UDF en fin d'image : l'image est tronquée");
            }
        }
        if (folder != null && Directory.Exists(folder))
        {
            var content = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length);
            if (f.Length < content) problems.Add($"l'image ({f.Length:N0} octets) est plus petite que les fichiers du disque ({content:N0} octets)");
        }
        if (udf && problems.Count == 0)
        {
            try { CheckImageBluRay(file, problems); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException or OverflowException)
            { problems.Add($"lecture du contenu UDF : {ex.Message}"); }
        }
        return problems;
    }

    static void CheckImageBluRay(string file, List<string> problems)
    {
        var entries = DamageScan.UdfFiles(file).ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        if (!entries.Keys.Any(n => n.StartsWith("BDMV\\", StringComparison.OrdinalIgnoreCase))) return;
        static byte[] Metadata(DamageScan.Entry entry, int limit)
        {
            if (entry.Size < 0 || entry.Size > limit) throw new InvalidDataException($"{entry.Name} : taille de fichier de navigation invalide");
            return DamageScan.ReadAll(entry);
        }
        Stream? Open(string name) => entries.TryGetValue("BDMV\\" + name, out var e)
            ? new MemoryStream(Metadata(e, 128 << 20), writable: false) : null;
        var lists = entries.Values.Where(e => e.Name.StartsWith("BDMV\\PLAYLIST\\", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(e.Name).Equals(".mpls", StringComparison.OrdinalIgnoreCase)).ToList();
        if (lists.Count == 0) problems.Add("aucune liste de lecture dans l'image Blu-ray");
        var clips = new Dictionary<string, ClipTracks>();
        foreach (var e in lists)
        {
            var bytes = Metadata(e, 16 << 20);
            var problem = !bytes.AsSpan().StartsWith("MPLS"u8) ? "en-tête MPLS invalide" : Mpls.StructuralProblem(bytes);
            if (problem != null) problems.Add($"{e.Name} : {problem}");
            else AddClipTracks(bytes, e.Name[5..], clips);
        }
        CheckClipTracks(clips, name => entries.TryGetValue("BDMV\\" + name, out var e) ? (e.Size, DamageScan.Open(e)) : null, problems);
        foreach (var name in entries.Keys.Where(n => n.StartsWith("BDMV\\JAR\\", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(n).Equals(".jar", StringComparison.OrdinalIgnoreCase)))
            CheckMenuNavigation(name[5..], Open, problems);
    }


    sealed class ClipTracks
    {
        public HashSet<string> Playlists { get; } = [];
        public HashSet<int> Pids { get; } = [];
    }

    static void AddClipTracks(byte[] playlist, string shown, Dictionary<string, ClipTracks> clips)
    {
        ClipTracks Clip(string id)
        {
            if (!clips.TryGetValue(id, out var tracks)) clips[id] = tracks = new();
            tracks.Playlists.Add(shown);
            return tracks;
        }

        // The references also include sub-paths (HDMV graphics) and alternate angles.
        foreach (Match match in Regex.Matches(Encoding.Latin1.GetString(playlist), @"(\d{5})M2TS"))
            Clip(match.Groups[1].Value);
        if (Mpls.StructuralProblem(playlist) != null) return;
        Mpls parsed;
        try { parsed = Mpls.Read(playlist); }
        catch (NotSupportedException) { return; } // multi-angle: the CLPI of each referenced clip still supplies its declared PIDs
        foreach (var item in parsed.Items)
            Clip(item.Clip).Pids.UnionWith(item.Video.Concat(item.Audio).Concat(item.Pg).Concat(item.Ig)
                .Where(track => track.EntryType == 1).Select(track => track.Pid));
    }

    static void CheckClipTracks(Dictionary<string, ClipTracks> clips,
        Func<string, (long Size, Stream Data)?> open, List<string> problems)
    {
        foreach (var (clip, declared) in clips)
        {
            var shown = $"{string.Join(", ", declared.Playlists)}, STREAM\\{clip}.m2ts";
            try
            {
                var programPids = new HashSet<int>();
                var metadata = open($"CLIPINF\\{clip}.clpi");
                if (metadata is { } clpi)
                {
                    using var info = clpi.Data;
                    if (clpi.Size is < 16 or > 16 << 20) throw new InvalidDataException("taille CLPI invalide");
                    var bytes = new byte[(int)clpi.Size];
                    info.ReadExactly(bytes);
                    if (!bytes.AsSpan().StartsWith("HDMV"u8)) throw new InvalidDataException("en-tête CLPI invalide");
                    declared.Pids.UnionWith(ClipProgramPids(bytes, programPids));
                }
                else problems.Add($"{shown} : CLIPINF\\{clip}.clpi manque");

                var stream = open($"STREAM\\{clip}.m2ts");
                if (stream is not { } media) { problems.Add($"{shown} manque (une liste de lecture l'utilise)"); continue; }
                using var data = media.Data;
                if (media.Size == 0 || media.Size % 192 != 0)
                { problems.Add($"{shown} n'est pas fait de paquets entiers de 192 octets ({media.Size:N0} octets)"); continue; }

                CheckTransport(data, declared.Pids, programPids, shown, problems);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            { problems.Add($"{shown} : {ex.Message}"); }
        }
    }

    static IEnumerable<int> ClipProgramPids(byte[] clpi, HashSet<int> programPids)
    {
        uint address = BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan(12));
        if (address > clpi.Length - 6 || address < 16) throw new InvalidDataException("adresse ProgramInfo CLPI invalide");
        var start = (int)address + 4;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan((int)address));
        if (length < 2 || length > clpi.Length - start) throw new InvalidDataException("taille ProgramInfo CLPI invalide");
        var end = start + (int)length;
        var programs = clpi[start + 1];
        var at = start + 2;
        for (var p = 0; p < programs; p++)
        {
            if (at > end - 8) throw new InvalidDataException("programme CLPI tronqué");
            programPids.Add(BinaryPrimitives.ReadUInt16BigEndian(clpi.AsSpan(at + 4)));
            var tracks = clpi[at + 6];
            at += 8;
            for (var s = 0; s < tracks; s++)
            {
                if (at > end - 3 || clpi[at + 2] == 0 || clpi[at + 2] > end - at - 3)
                    throw new InvalidDataException("piste CLPI tronquée");
                yield return BinaryPrimitives.ReadUInt16BigEndian(clpi.AsSpan(at));
                at += 3 + clpi[at + 2];
            }
        }
    }

    sealed class ProgramMap
    {
        readonly byte[] section = new byte[1024];
        int filled;

        public void Read(ReadOnlySpan<byte> payload, bool start, HashSet<int> unlisted)
        {
            if (start)
            {
                var pointer = payload[0];
                if (pointer >= payload.Length) { filled = 0; return; }
                if (pointer > 0 && filled > 0) Append(payload.Slice(1, pointer), unlisted);
                filled = 0;
                payload = payload[(pointer + 1)..];
                if (payload.IsEmpty || payload[0] != 2) return; // table_id: Program Map Table
            }
            else if (filled == 0) return;
            Append(payload, unlisted);
        }

        void Append(ReadOnlySpan<byte> payload, HashSet<int> unlisted)
        {
            var take = Math.Min(payload.Length, section.Length - filled);
            payload[..take].CopyTo(section.AsSpan(filled));
            filled += take;
            if (filled < 3) return;
            var length = 3 + ((section[1] & 15) << 8 | section[2]);
            if (length < 16 || length > section.Length) { filled = 0; return; }
            if (filled < length) return;
            filled = 0;
            if ((section[5] & 1) == 0) return; // a future table version
            var end = length - 4; // CRC
            for (var at = 12 + ((section[10] & 15) << 8 | section[11]); at <= end - 5;)
            {
                var next = at + 5 + ((section[at + 3] & 15) << 8 | section[at + 4]);
                if (next > end) break;
                unlisted.Remove((section[at + 1] & 31) << 8 | section[at + 2]);
                at = next;
            }
        }
    }

    static void CheckTransport(Stream media, HashSet<int> declared, HashSet<int> programPids, string shown, List<string> problems)
    {
        var missing = new HashSet<int>(declared);
        var unlisted = new HashSet<int>(declared);
        var programs = programPids.ToDictionary(pid => pid, _ => new ProgramMap());
        var buffer = new byte[192 * 4096];
        bool Pending() => missing.Count > 0 || programs.Count > 0 && unlisted.Count > 0;
        for (int read; Pending() && (read = media.ReadAtLeast(buffer, buffer.Length, false)) != 0;)
        {
            if (read % 192 != 0) throw new InvalidDataException("paquet M2TS tronqué");
            for (var at = 4; at < read && Pending(); at += 192)
            {
                if (buffer[at] != 0x47) throw new InvalidDataException("paquet M2TS sans octet de synchronisation");
                if ((buffer[at + 1] & 0x80) != 0 || (buffer[at + 3] & 0x10) == 0) continue;
                var pid = ((buffer[at + 1] & 31) << 8) | buffer[at + 2];
                var start = (buffer[at + 1] & 0x40) != 0;
                if ((!start || !missing.Contains(pid)) && !programs.ContainsKey(pid)) continue;
                var offset = at + 4;
                if ((buffer[at + 3] & 0x20) != 0) offset += 1 + buffer[offset];
                if (offset >= at + 188) continue;
                var payload = buffer.AsSpan(offset, at + 188 - offset);
                if (programs.TryGetValue(pid, out var map)) map.Read(payload, start, unlisted);
                // A payload-unit start with a PES prefix proves the clip carries the declared stream.
                if (start && payload.Length >= 7 && payload.StartsWith("\0\0\u0001"u8)) missing.Remove(pid);
            }
        }
        foreach (var pid in missing.Order()) problems.Add($"{shown} : PID 0x{pid:X4} déclaré sans paquet utile");
        if (programs.Count > 0)
            foreach (var pid in unlisted.Order()) problems.Add($"{shown} : PID 0x{pid:X4} déclaré absent de la PMT");
    }

    public static (List<string> Trace, List<string> Problems) Navigation(string path, int audio = 0, int subtitle = -1)
    {
        var trace = new List<string>();
        var problems = new List<string>();
        if (Directory.Exists(path))
        {
            var bdmv = Path.Combine(path, "BDMV");
            var jars = Path.Combine(bdmv, "JAR");
            if (Directory.Exists(jars))
                foreach (var jar in Directory.EnumerateFiles(jars, "*.jar"))
                    CheckMenuNavigation(Path.Combine("JAR", Path.GetFileName(jar)), name =>
                        File.Exists(Path.Combine(bdmv, name)) ? File.OpenRead(Path.Combine(bdmv, name)) : null,
                        problems, trace, audio, subtitle);
        }
        else
        {
            var entries = DamageScan.UdfFiles(path).ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
            Stream? Open(string name)
            {
                if (!entries.TryGetValue("BDMV\\" + name, out var entry)) return null;
                if (entry.Size > 128 << 20) throw new InvalidDataException($"{name} : fichier trop volumineux");
                return new MemoryStream(DamageScan.ReadAll(entry), writable: false);
            }
            foreach (var name in entries.Keys.Where(name => name.StartsWith("BDMV\\JAR\\", StringComparison.OrdinalIgnoreCase)
                         && Path.GetExtension(name).Equals(".jar", StringComparison.OrdinalIgnoreCase)))
                CheckMenuNavigation(name[5..], Open, problems, trace, audio, subtitle);
        }
        if (trace.Count == 0 && problems.Count == 0) problems.Add("aucun menu BD-J BurnSmith pris en charge dans ce disque");
        return (trace, problems);
    }

    static void CheckMenuNavigation(string jar, Func<string, Stream?> open, List<string> problems,
                                    List<string>? trace = null, int audioChoice = 0, int subtitleChoice = -1)
    {
        try
        {
            using var stream = open(jar);
            if (stream == null) return;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.GetEntry("org/burnsmith/menu/Menu.class") == null) return;
            if (zip.GetEntry("menu.txt") is not { } config || config.Length > 4 << 20)
                throw new InvalidDataException("menu.txt absent ou trop volumineux");
            using var reader = new StreamReader(config.Open());
            var rows = reader.ReadToEnd().Split('\n')
                .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Where(r => r.Length != 0).ToList();
            var playback = rows.FirstOrDefault(r => r[0] == "playback");
            var first = playback is { Length: > 4 } && int.TryParse(playback[4], out var start) ? start : BluRay.MoviePlaylist;
            var playlists = new Dictionary<int, Mpls?>();
            Mpls? Playlist(int number)
            {
                if (playlists.TryGetValue(number, out var cached)) return cached;
                using var file = open($"PLAYLIST\\{number:00000}.mpls");
                if (file == null) return playlists[number] = null;
                if (file.Length > 16 << 20) throw new InvalidDataException($"liste de lecture {number:00000} trop volumineuse");
                using var bytes = new MemoryStream();
                file.CopyTo(bytes);
                var data = bytes.ToArray();
                if (Mpls.StructuralProblem(data) is { } problem) throw new InvalidDataException($"{number:00000}.mpls : {problem}");
                return playlists[number] = Mpls.Read(data);
            }
            void Problem(string text) => problems.Add($"{jar} : {text}");
            if (trace != null) BdjNavigation.Trace(rows, Playlist, trace, Problem, audioChoice, subtitleChoice);
            var popupSettings = rows.Where(row => row.Length >= 14 && row is ["button", _, "settings", ..] && row[12] == "page")
                .Where(row => row[1].StartsWith("pop", StringComparison.Ordinal) && int.TryParse(row[1][3..], out _))
                .ToLookup(row => row[13], row => int.Parse(row[1][3..]));
            foreach (var row in rows)
            {
                if (row is ["button", var page, var id, ..] && row.Length >= 13 && row[12] == "chapter")
                {
                    if (row.Length < 15 || !int.TryParse(row[13], out var title) || !int.TryParse(row[14], out var mark)
                        || Playlist(title) is not { } list || mark < 0 || mark >= list.Chapters.Count)
                        Problem($"bouton {page}/{id} : chapitre inexistant");
                }
                else if (row is ["subtracks", var titleText, ..])
                {
                    if (!int.TryParse(titleText, out var title) || Playlist(title) is not { } list
                        || row.Skip(2).Any(s => !int.TryParse(s, out var n) || n <= 0 || list.Items.Any(i => n > i.Pg.Count)))
                        Problem($"pistes de sous-titres inexistantes dans la liste {titleText}");
                }
                else if (row is ["button", var setup, var choice, ..] && row.Length >= 13 && row[12] is "audio" or "sub")
                {
                    var audio = row[12] == "audio";
                    IEnumerable<int> targets = popupSettings.Contains(setup) ? popupSettings[setup] : [first];
                    foreach (var target in targets)
                        if (row.Length < 14 || !int.TryParse(row[13], out var number) || number < (audio ? 1 : 0)
                            || Playlist(target) is not { } list || list.Items.Any(i => number > (audio ? i.Audio.Count : i.Pg.Count)))
                            Problem($"bouton {setup}/{choice}, titre {target:00000} : piste {(audio ? "audio" : "de sous-titres")} inexistante");
                }
                else if (row[0] is "forced" or "forced-track")
                {
                    var perTitle = row[0] == "forced-track";
                    var at = perTitle ? 2 : 1;
                    var title = first;
                    if (row.Length <= at + 1 || perTitle && !int.TryParse(row[1], out title)
                        || !int.TryParse(row[at], out var audio) || !int.TryParse(row[at + 1], out var sub)
                        || Playlist(title) is not { } list || audio <= 0 || sub <= 0
                        || list.Items.Any(i => audio > i.Audio.Count || sub > i.Pg.Count))
                        Problem($"association audio/sous-titres forcés invalide ({string.Join(' ', row)})");
                    else if (list.Items.Any(i => !Languages.Same(i.Audio[audio - 1].Lang, i.Pg[sub - 1].Lang)))
                        Problem($"sous-titres forcés {sub} du titre {title:00000} : langue différente de la piste audio {audio}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        { problems.Add($"{jar} : navigation BD-J illisible ({ex.Message})"); }
    }

    static bool Starts(string file, string magic, string shown, List<string> problems)
    {
        if (!File.Exists(file)) { problems.Add($"{shown} manque"); return false; }
        using var f = File.OpenRead(file);
        var head = new byte[magic.Length];
        if (f.Read(head) < head.Length || Encoding.ASCII.GetString(head) != magic) { problems.Add($"{shown} n'est pas un fichier {magic}"); return false; }
        return true;
    }

    static void Same(string a, string b, string shown, List<string> problems)
    {
        if (!File.Exists(b)) problems.Add($"la copie de secours {shown} manque");
        else if (File.Exists(a) && !File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b))) problems.Add($"la copie de secours {shown} diffère de l'original");
    }

    static void CheckBluRayIndex(string bdmv, List<string> problems)
    {
        var movieObject = Path.Combine(bdmv, "MovieObject.bdmv");
        int? objects = null;
        if (Starts(movieObject, "MOBJ", "BDMV\\MovieObject.bdmv", problems))
        {
            try { objects = Bdmv.ReadObjects(File.ReadAllBytes(movieObject)).Count; }
            catch (InvalidDataException ex) { problems.Add($"BDMV\\MovieObject.bdmv : {ex.Message}"); }
        }
        var index = Path.Combine(bdmv, "index.bdmv");
        if (!Starts(index, "INDX", "BDMV\\index.bdmv", problems)) return;
        var bytes = File.ReadAllBytes(index);
        const string shown = "BDMV\\index.bdmv";
        var start = bytes.Length >= 12 ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)) : 0;
        if (start < 78 || start > bytes.Length - 4)
        {
            problems.Add($"{shown} : adresse de la table des titres hors du fichier");
            return;
        }
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)start));
        if (length < 26 || length > bytes.Length - (int)start - 4)
        {
            problems.Add($"{shown} : table des titres tronquée");
            return;
        }
        var table = bytes.AsSpan((int)start + 4, (int)length);
        var titles = BinaryPrimitives.ReadUInt16BigEndian(table[24..]);
        if (titles > (table.Length - 26) / 12)
        {
            problems.Add($"{shown} : table des titres tronquée");
            return;
        }
        for (var i = 0; i < titles + 2; i++)
        {
            var entry = table[(i * 12 + (i >= 2 ? 2 : 0))..];
            var title = i switch { 0 => "Première lecture", 1 => "Menu principal", _ => $"Titre {i - 1}" };
            switch (entry[0] >> 6)
            {
                case 1:
                    var id = BinaryPrimitives.ReadUInt16BigEndian(entry[6..]);
                    if (objects is int count && id >= count && !(i < 2 && id == ushort.MaxValue))
                        problems.Add($"{shown} : {title}, objet HDMV {id} inexistant");
                    break;
                case 2:
                    var name = Encoding.ASCII.GetString(entry.Slice(6, 5));
                    if (!name.All(char.IsAsciiDigit) || !File.Exists(Path.Combine(bdmv, "BDJO", name + ".bdjo")))
                        problems.Add($"{shown} : {title}, descripteur BD-J {name}.bdjo absent ou invalide");
                    break;
                default:
                    problems.Add($"{shown} : {title}, type d'objet inconnu");
                    break;
            }
        }
    }

    static void CheckBluRay(string bdmv, List<string> problems)
    {
        CheckBluRayIndex(bdmv, problems);
        foreach (var name in new[] { "index.bdmv", "MovieObject.bdmv" }) Same(Path.Combine(bdmv, name), Path.Combine(bdmv, "BACKUP", name), "BACKUP\\" + name, problems);
        var lists = Directory.Exists(Path.Combine(bdmv, "PLAYLIST")) ? Directory.GetFiles(Path.Combine(bdmv, "PLAYLIST"), "*.mpls") : [];
        if (lists.Length == 0) problems.Add("aucune liste de lecture (PLAYLIST\\*.mpls)");
        var clips = new Dictionary<string, ClipTracks>();
        foreach (var list in lists)
        {
            var shown = "PLAYLIST\\" + Path.GetFileName(list);
            if (!Starts(list, "MPLS", shown, problems)) continue;
            var bytes = File.ReadAllBytes(list);
            if (Mpls.StructuralProblem(bytes) is { } problem) problems.Add($"{shown} : {problem}");
            try { AddClipTracks(bytes, shown, clips); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            { problems.Add($"{shown} : pistes MPLS illisibles ({ex.Message})"); }
        }
        CheckClipTracks(clips, name =>
        {
            var info = new FileInfo(Path.Combine(bdmv, name));
            return info.Exists ? (info.Length, info.OpenRead()) : null;
        }, problems);
        foreach (var clip in clips.Keys)
        {
            var stream = Path.Combine(bdmv, "STREAM", clip + ".m2ts");
            var size = Tools.SizeOf(stream);
            if (size > 0 && size % 192 == 0)
                Packets(stream, 192, 4, $"STREAM\\{clip}.m2ts", problems);
        }
        // a player starts a signed BD-J application only with the disc's certificates and a signature they vouch for
        var jars = Directory.Exists(Path.Combine(bdmv, "JAR")) ? Directory.GetFiles(Path.Combine(bdmv, "JAR"), "*.jar") : [];
        var cert = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bdmv))!, "CERTIFICATE");
        if (jars.Length == 0) return;
        var bdjoDir = Path.Combine(bdmv, "BDJO");
        var bdjos = Directory.Exists(bdjoDir) ? Directory.GetFiles(bdjoDir, "*.bdjo") : [];
        if (bdjos.Length == 0) problems.Add("le menu BD-J (JAR) n'a pas de fichier BDJO");
        Span<byte> header = stackalloc byte[48];
        foreach (var bdjo in bdjos)
        {
            var shown = "BDJO\\" + Path.GetFileName(bdjo);
            using var f = File.OpenRead(bdjo);
            if (f.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length || !header[..4].SequenceEqual("BDJO"u8))
            {
                problems.Add($"{shown} : en-tête BD-J absent ou tronqué");
                continue;
            }
            var applications = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
            if (applications < header.Length || applications > f.Length - 6)
            {
                problems.Add($"{shown} : adresse de la table d'applications BD-J hors du fichier");
                continue;
            }
            f.Position = applications;
            Span<byte> size = header[..4];
            f.ReadExactly(size);
            var length = BinaryPrimitives.ReadUInt32BigEndian(size);
            if (length < 2 || length > f.Length - f.Position)
                problems.Add($"{shown} : table d'applications BD-J tronquée ou invalide");
        }
        foreach (var name in new[] { "app.discroot.crt", "bu.discroot.crt", "id.bdmv" })
            if (!File.Exists(Path.Combine(cert, name))) problems.Add($"CERTIFICATE\\{name} manque : une platine refusera le menu BD-J");
            else Same(Path.Combine(cert, name), Path.Combine(cert, "BACKUP", name), "CERTIFICATE\\BACKUP\\" + name, problems);
        if (File.Exists(Path.Combine(cert, "app.discroot.crt")) && File.Exists(Path.Combine(cert, "id.bdmv")))
            foreach (var jar in jars) problems.AddRange(BdjSign.Verify(jar, File.ReadAllBytes(Path.Combine(cert, "app.discroot.crt")), File.ReadAllBytes(Path.Combine(cert, "id.bdmv"))));
        foreach (var jar in jars)
            CheckMenuNavigation(Path.Combine("JAR", Path.GetFileName(jar)),
                name => File.Exists(Path.Combine(bdmv, name)) ? File.OpenRead(Path.Combine(bdmv, name)) : null, problems);
    }

    /// <summary>A transport stream is a run of whole packets (192 bytes with the time stamp), each starting with the sync byte 0x47.</summary>
    static void Packets(string file, int size, int sync, string shown, List<string> problems)
    {
        var info = new FileInfo(file);
        if (info.Length == 0 || info.Length % size != 0) { problems.Add($"{shown} n'est pas fait de paquets entiers de {size} octets ({info.Length:N0} octets)"); return; }
        using var f = File.OpenRead(file);
        var count = info.Length / size;
        var buffer = new byte[1];
        foreach (var k in Enumerable.Range(0, (int)Math.Min(count, 40)).Select(i => (long)i).Concat([count / 2, count - 1]))
        {
            f.Position = k * size + sync;
            if (f.Read(buffer) < 1 || buffer[0] != 0x47) { problems.Add($"{shown} : le paquet {k} ne commence pas par l'octet de synchronisation"); return; }
        }
    }

    static void CheckDvd(string video, List<string> problems)
    {
        var all = Directory.GetFiles(video);
        long Sectors(string pattern) => all.Where(f => Regex.IsMatch(Path.GetFileName(f), pattern, RegexOptions.IgnoreCase)).Sum(f => new FileInfo(f).Length) / 2048 - 1;
        long LastSector(string ifo) { using var f = File.OpenRead(ifo); if (f.Length < 16) return -1; f.Position = 12; var b = new byte[4]; f.ReadExactly(b); return b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]; }
        if (Starts(Path.Combine(video, "VIDEO_TS.IFO"), "DVDVIDEO-VMG", "VIDEO_TS.IFO", problems))
        {
            Same(Path.Combine(video, "VIDEO_TS.IFO"), Path.Combine(video, "VIDEO_TS.BUP"), "VIDEO_TS.BUP", problems);
            if (LastSector(Path.Combine(video, "VIDEO_TS.IFO")) != Sectors(@"^VIDEO_TS\.(IFO|BUP|VOB)$")) problems.Add("VIDEO_TS.IFO ne donne pas la taille des fichiers VIDEO_TS");
        }
        var sets = all.Select(f => Regex.Match(Path.GetFileName(f), @"^VTS_(\d\d)_0\.IFO$", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        if (sets.Count == 0) problems.Add("aucun titre (VTS_nn_0.IFO)");
        foreach (var n in sets)
        {
            var ifo = Path.Combine(video, $"VTS_{n}_0.IFO");
            if (!Starts(ifo, "DVDVIDEO-VTS", $"VTS_{n}_0.IFO", problems)) continue;
            Same(ifo, Path.Combine(video, $"VTS_{n}_0.BUP"), $"VTS_{n}_0.BUP", problems);
            if (LastSector(ifo) != Sectors($@"^VTS_{n}_\d\.(IFO|BUP|VOB)$")) problems.Add($"VTS_{n}_0.IFO ne donne pas la taille des fichiers du titre {n}");
            var vtsBytes = File.ReadAllBytes(ifo);
            if (vtsBytes.Length >= 0x256)
            {
                if (vtsBytes[0x203] > 8) problems.Add($"VTS_{n}_0.IFO : {vtsBytes[0x203]} pistes audio déclarées (maximum DVD : 8)");
                if (vtsBytes[0x255] > 32) problems.Add($"VTS_{n}_0.IFO : {vtsBytes[0x255]} pistes de sous-titres déclarées (maximum DVD : 32)");
            }
            if (!File.Exists(Path.Combine(video, $"VTS_{n}_1.VOB"))) problems.Add($"VTS_{n}_1.VOB manque");
            foreach (var vob in all.Where(f => Regex.IsMatch(Path.GetFileName(f), $@"^VTS_{n}_\d\.VOB$", RegexOptions.IgnoreCase)))
            {
                var info = new FileInfo(vob);
                if (info.Length == 0 || info.Length % 2048 != 0) { problems.Add($"{info.Name} n'est pas fait de secteurs entiers de 2048 octets"); continue; }
                using var f = File.OpenRead(vob);
                var head = new byte[4];
                if (f.Read(head) < 4 || !head.AsSpan().SequenceEqual(new byte[] { 0, 0, 1, 0xBA })) problems.Add($"{info.Name} ne commence pas par un en-tête de paquet MPEG");
            }
        }
    }
}
