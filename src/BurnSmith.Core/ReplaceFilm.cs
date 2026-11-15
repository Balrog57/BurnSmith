using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BurnSmith.Core;

/// <summary>
/// What replace-film is asked: the disc (folder or .iso), the clean film (.mkv), the folder to write; <see cref="Map"/> forces a slot ("A1", "PG2")
/// to an MKV stream (ffprobe's index, 0 = the video).
/// </summary>
public sealed record ReplaceFilmJob(string Disc, string Mkv, string Out, string? Iso = null, int? Playlist = null, double? CutStart = null,
                                    IReadOnlyDictionary<string, int>? Map = null, bool NoAlign = false, bool MeasureOnly = false, string? Label = null);

/// <summary>
/// replace-film: a Blu-ray whose film is damaged gets the film of a clean MKV in its place, its menus, bonuses and every other file kept as they are.
/// The movie objects choose sound and subtitles by number, so the new film offers the same slots in the same order (A1, A2..., PG1, PG2...), each
/// taken from the MKV track of that language (full or forced); then the new film is put on the old one's clock: scene cuts give the picture mapping
/// (a PAL disc runs 25/23.976 faster), cross-correlation each sound's offset (tsMuxeR timeshift), subtitle starts each subtitle track's drift (the
/// .sup re-timed), and the chapters are the old marks mapped and put on frames. tsMuxeR writes the clip, its info and the playlist at the old numbers.
/// A sub-path of the old playlist (a pop-up menu clip at 25 fps) is not carried over: on a 24p film it froze the picture on a real player.
/// shortcut: one play item, one video, AVC/HEVC/VC-1/MPEG-2 with DTS/AC-3/LPCM sound; a film made of several clips or TrueHD from the MKV is refused.
/// </summary>
public static class ReplaceFilm
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string Ms(double s) => (s * 1000).ToString("+0;-0;0", Inv) + " ms";
    static string Clock(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"hh\:mm\:ss\.fff", Inv);

    /// <summary>A stream of the MKV as ffprobe gives it.</summary>
    public sealed record MkvStream(int Index, string Type, string Codec, string Profile, string Lang, string Title, bool Forced)
    {
        public override string ToString() => $"flux {Index} {Codec}{(Profile.Length > 0 ? " " + Profile : "")} {Lang}{(Title.Length > 0 ? $" « {Title} »" : "")}{(Forced ? " (forcé)" : "")}";
    }

    /// <summary>One sound or subtitle slot of the disc's film and what fills it.</summary>
    sealed class Slot(string name, Mpls.Stream original)
    {
        public readonly string Name = name;
        public readonly Mpls.Stream Original = original;
        public bool Forced;
        public List<double> OriginalStarts = [];
        public MkvStream? Source;
        public string? Sup;            // the re-timed .sup muxed for a subtitle slot
        public int ShiftMs;            // tsMuxeR timeshift for a sound slot
    }

    public static int Run(ReplaceFilmJob job, Action<string> log)
    {
        var (root, mounted) = Open(job.Disc);
        if (File.Exists(job.Disc)) log($"Image {(mounted ? "ouverte en lecture seule" : "déjà ouverte, laissée ouverte à la fin")} sur {root}");
        try { return Work(job, root, log); }
        finally { if (mounted) Close(job.Disc, log); }
    }

    static int Work(ReplaceFilmJob job, string root, Action<string> log)
    {
        var bdmv = Path.Combine(root, "BDMV");
        var playlists = Path.Combine(bdmv, "PLAYLIST");
        if (!Directory.Exists(playlists)) throw new InvalidDataException($"{job.Disc} : pas de dossier BDMV\\PLAYLIST, ce n'est pas un Blu-ray");
        var lists = Directory.GetFiles(playlists, "*.mpls").Select(f => (File: f, Number: int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : -1, List: TryRead(f)))
                             .Where(p => p.List != null && p.Number >= 0).ToList();
        var chosen = job.Playlist is { } wanted ? lists.FirstOrDefault(p => p.Number == wanted) : lists.OrderByDescending(p => p.List!.Seconds).FirstOrDefault();
        if (chosen.List is not { } pl) throw new InvalidDataException(job.Playlist is { } w ? $"pas de liste de lecture {w:00000}.mpls" : "aucune liste de lecture lisible");
        if (pl.Items.Count != 1) throw new NotSupportedException($"{chosen.Number:00000}.mpls joue {pl.Items.Count} clips : seul un film d'un seul clip est remplacé");
        var item = pl.Items[0];
        var clip = int.Parse(item.Clip, Inv);
        var original = Path.Combine(bdmv, "STREAM", item.Clip + ".m2ts");
        var fpsO = item.Video.Count > 0 ? Mpls.Fps(item.Video[0].Rate) : 0;
        log($"Film du disque : {chosen.Number:00000}.mpls → {item.Clip}.m2ts, {Clock(item.Seconds)}, entrée {item.In / 45000.0:0.000} s, {fpsO:0.###} i/s, " +
            $"{item.Audio.Count} son(s), {item.Pg.Count} sous-titre(s), {pl.Chapters.Count} chapitres");
        foreach (var (s, i) in item.Audio.Select((s, i) => (s, i))) log($"  A{i + 1}  PID 0x{s.Pid:X4} {Mpls.CodingName(s.Coding)} {s.Lang}");
        foreach (var (s, i) in item.Pg.Select((s, i) => (s, i))) log($"  PG{i + 1} PID 0x{s.Pid:X4} {s.Lang}");
        if (pl.SubPathTypes.Count > 0)
            log($"  non repris : {pl.SubPathTypes.Count} sous-chemin(s) (type {string.Join(", ", pl.SubPathTypes)}{(pl.SubPathTypes.Contains(3) ? ", menu pop-up" : "")}) : sur un film 24p, un menu pop-up à 25 i/s fige l'image d'une vraie platine");
        if (item.Ig.Count > 0) log($"  non repris : {item.Ig.Count} piste(s) IG (le menu pop-up du film)");
        foreach (var other in lists.Where(p => p.Number != chosen.Number && p.List!.Items.Any(i => i.Clip == item.Clip)))
            log($"  attention : {other.Number:00000}.mpls joue aussi {item.Clip}.m2ts ; ses temps ne correspondront plus au nouveau film");

        var work = Path.GetFullPath(job.Out).TrimEnd('\\') + "_travail";
        Tools.FreshFolder(work);
        var ffprobeStart = StartTime(original);

        // the MKV
        var streams = Probe(job.Mkv, out var fpsN, out var frames);
        var video = streams.FirstOrDefault(s => s.Type == "video") ?? throw new InvalidDataException("pas d'image dans le MKV");
        // the same pictures at another speed: a PAL disc plays film 25/24 faster
        double[] filmRates = [24000 / 1001.0, 24, 25];
        var k = fpsO > 0 && filmRates.Any(r => Math.Abs(r - fpsO) < 0.01) && filmRates.Any(r => Math.Abs(r - fpsN) < 0.01) ? fpsO / fpsN : 1;
        log($"MKV : {Path.GetFileName(job.Mkv)}, {fpsN:0.###} i/s{(Math.Abs(k - 1) > 1e-6 ? $" ; le disque passe {k:0.00000} fois plus vite (vitesse PAL)" : "")}");

        // the slots, the old subtitles read to tell full from forced
        var audio = item.Audio.Select((s, i) => new Slot($"A{i + 1}", s)).ToList();
        var subs = item.Pg.Select((s, i) => new Slot($"PG{i + 1}", s)).ToList();
        if (subs.Count > 0)
        {
            log("Lecture des sous-titres du disque...");
            var files = Extract(original, true, [.. subs.Select(s => $"i:0x{s.Original.Pid:X}")], Path.Combine(work, "disque"));
            foreach (var (slot, file) in subs.Zip(files))
            {
                var sup = File.Exists(file) ? File.ReadAllBytes(file) : [];
                slot.OriginalStarts = PgsSubtitles.Starts(sup);
                var (objects, forced) = PgsSubtitles.ForcedObjects(sup);
                slot.Forced = objects > 0 && forced * 2 > objects;
            }
            foreach (var slot in subs.Where(s => !s.Forced)) // no flag: a track far shorter than another of its language is its forced one
                slot.Forced = subs.Any(o => o != slot && Languages.Same(o.Original.Lang, slot.Original.Lang) && slot.OriginalStarts.Count * 5 < o.OriginalStarts.Count);
        }

        // each slot gets its MKV track: --map, else the language (and full or forced), the same kind of sound first
        if (job.Map?.Keys.FirstOrDefault(n => !audio.Concat(subs).Any(s => s.Name == n)) is { } unknown)
            throw new ArgumentException($"--map {unknown} : le film du disque n'a pas cette piste ({string.Join(", ", audio.Concat(subs).Select(s => s.Name))})");
        var used = new HashSet<int>();
        foreach (var slot in audio.Concat(subs))
        {
            if (job.Map?.TryGetValue(slot.Name, out var forcedIndex) == true)
                slot.Source = streams.FirstOrDefault(s => s.Index == forcedIndex) ?? throw new ArgumentException($"--map {slot.Name}=mkv:{forcedIndex} : le MKV n'a pas de flux {forcedIndex}");
            else
            {
                var isSub = slot.Name.StartsWith("PG", StringComparison.Ordinal);
                slot.Source = streams.Where(s => !used.Contains(s.Index) && s.Type == (isSub ? "subtitle" : "audio") && Languages.Same(Lang(s.Lang), Lang(slot.Original.Lang))
                                              && (!isSub || s.Codec == "hdmv_pgs_subtitle" && Forced(s) == slot.Forced))
                                     .OrderByDescending(s => isSub ? (Regex.IsMatch(s.Title, "SDH|sourds|malentendant|CC", RegexOptions.IgnoreCase) ? 0 : 1) : Score(slot.Original.Coding, s))
                                     .ThenBy(s => s.Index).FirstOrDefault()
                              ?? throw new InvalidDataException($"{slot.Name} ({slot.Original.Lang}{(slot.Forced ? ", forcé" : "")}) : aucune piste du MKV ne convient ; choisissez-la avec --map {slot.Name}=mkv:N\n" +
                                                                string.Join('\n', streams.Select(s => "  " + s)));
            }
            used.Add(slot.Source.Index);
            if (!slot.Name.StartsWith("PG", StringComparison.Ordinal) && Muxed(slot.Source) is null)
                throw new NotSupportedException($"{slot.Name} : {slot.Source} ne peut pas aller tel quel sur le disque (DTS, AC-3, E-AC-3 ou LPCM seulement)");
        }
        log("Correspondance des pistes :");
        foreach (var slot in audio.Concat(subs))
            log($"  {slot.Name,-4} {slot.Original.Lang} {(slot.Name.StartsWith("PG", StringComparison.Ordinal) ? slot.Forced ? "forcés" : "complets" : Mpls.CodingName(slot.Original.Coding))} ← {slot.Source}");
        foreach (var s in streams.Where(s => s.Type is "audio" or "subtitle" && !used.Contains(s.Index))) log($"  non repris : {s}");

        // the unwanted start cut off at a keyframe
        var film = job.Mkv;
        if (job.CutStart is { } cut)
        {
            film = Path.Combine(work, "film.mkv");
            Cut(job.Mkv, cut, frames, film, log);
        }
        var startN = StartTime(film);

        // the subtitles taken out of the MKV as .sup files, muxed as files so that their times can be corrected; a full track loses any forced flag
        if (subs.Count > 0)
        {
            var files = Extract(film, false, [.. subs.Select(s => s.Source!.Index.ToString(Inv))], Path.Combine(work, "mkv"));
            foreach (var (slot, file) in subs.Zip(files))
            {
                var sup = File.ReadAllBytes(file);
                if (PgsSubtitles.Retime(sup, t => t, clearForced: !slot.Forced) is var cleared and > 0)
                    log($"  {slot.Name} : {cleared} image(s) marquée(s) « forcée » dans une piste complète : marque retirée (elles se montraient sous-titres coupés)");
                slot.Sup = Path.Combine(work, $"{slot.Name}.sup");
                File.WriteAllBytes(slot.Sup, sup);
            }
        }

        string Mux(string dir, IEnumerable<double> marks)
        {
            var meta = new List<string>
            {
                $"MUXOPT --blu-ray --new-audio-pes --vbr --label=\"{Label(job)}\" --custom-chapters={string.Join(';', marks.Select(Clock))} --mplsOffset={chosen.Number} --m2tsOffset={clip}",
                $"{VideoCodec(video)}, \"{Slash(film)}\"{(video.Codec == "h264" ? ", insertSEI, contSPS" : "")}, track={video.Index + 1}",
            };
            meta.AddRange(audio.Select(s => $"{Muxed(s.Source!)}, \"{Slash(film)}\", track={s.Source!.Index + 1}, lang={s.Original.Lang}{(s.ShiftMs != 0 ? $", timeshift={s.ShiftMs}ms" : "")}"));
            meta.AddRange(subs.Select(s => $"S_HDMV/PGS, \"{Slash(s.Sup!)}\", lang={s.Original.Lang}"));
            var metaFile = dir + ".meta";
            File.WriteAllLines(metaFile, meta);
            Tools.Run(Tools.Find("tsMuxeR.exe"), [metaFile, dir], line => { if (line.StartsWith("Mux successful", StringComparison.Ordinal) || line.Contains("rror")) log("  tsMuxeR : " + line); });
            return dir;
        }
        string Stream(string dir) => Path.Combine(dir, "BDMV", "STREAM", $"{clip:00000}.m2ts");
        Mpls.Item Played(string dir) => Mpls.Read(Path.Combine(dir, "BDMV", "PLAYLIST", $"{chosen.Number:00000}.mpls")).Items[0];
        // chapters: the old marks on the new film's clock, put on frames; the first at 0; as many as before (the menu's chapter buttons use their numbers)
        List<double> Chapters(Func<double, double> fromStart) =>
            [.. pl.Chapters.Where(m => m.Item == 0).Select((m, i) => i == 0 ? 0 : Math.Max(0, Math.Round(fromStart(m.Time / 45000.0) * fpsN) / fpsN))];

        // the mapping clip = A + K × old, on the clock of the clip tsMuxeR writes: it does not keep the MKV's own timing of the picture against the
        // sound and subtitles (two frames on the remux seen), so everything is measured on a first mux made without correction, then muxed again corrected
        double a;
        var anchors = new List<(double At, List<double> Cuts)>();
        var nominal = startN - k * item.In / 45000.0; // the MKV's clock, its start on the old film's
        List<double> chapters;
        string muxed;
        if (job.NoAlign)
        {
            chapters = Chapters(o => nominal + k * o - startN);
            log($"--no-align : les débuts posés l'un sur l'autre, sans mesure ni correction ; chapitres : {string.Join(" ", chapters.Select(Clock))}");
            if (job.MeasureOnly) { Tools.TryDelete(work); return 0; }
            log("tsMuxeR...");
            muxed = Mux(Path.Combine(work, "mux"), chapters);
            a = nominal - startN + Played(muxed).In / 45000.0;
        }
        else
        {
            log("Premier multiplexage, sans correction, pour mesurer sur le clip tel que tsMuxeR l'écrit...");
            var trial = Mux(Path.Combine(work, "essai"), [0]);
            var raw = Stream(trial);
            var rawItem = Played(trial);
            var startD = StartTime(raw);
            var shift = rawItem.In / 45000.0 - startN; // the MKV's clock to the clip's, as the times say
            log("Alignement de l'image (changements de plan)...");
            var found = new List<double>();
            double? guess = null;
            foreach (var f in new[] { 0.08, 0.25, 0.42, 0.58, 0.75, 0.92 })
            {
                var at = item.In / 45000.0 + f * item.Seconds;
                var oc = FilmSync.Cuts(original, ffprobeStart, at, 60);
                if (oc.Count < 3) { log($"  {Clock(at - item.In / 45000.0)} : pas assez de changements de plan lisibles sur le disque ({oc.Count}), fenêtre laissée"); continue; }
                // the first window looks two minutes around where the film would be; the next ones a few seconds around the first answer
                var expected = (guess ?? nominal + shift) + k * oc[0];
                var margin = guess == null ? 120 : 4;
                var nc = FilmSync.Cuts(raw, startD, expected - margin, k * (oc[^1] - oc[0]) + 2 * margin);
                if (FilmSync.Match(oc, nc, k) is not { } m) { log($"  {Clock(at - item.In / 45000.0)} : changements de plan sans correspondance ({oc.Count} sur le disque, {nc.Count} dans le nouveau film)"); continue; }
                guess ??= m.A;
                found.Add(m.A);
                anchors.Add((at, oc));
                log($"  {Clock(at - item.In / 45000.0)} : {m.Matched}/{m.Of} changements de plan alignés, nouveau = {m.A:0.000} + {k:0.00000} × ancien, résidu max {Ms(m.Residuals.Max(Math.Abs))}");
            }
            if (found.Count == 0) throw new InvalidDataException("image du MKV impossible à aligner sur celle du disque (aucun changement de plan commun) ; --no-align pour la poser sans mesure");
            a = FilmSync.Median(found);
            var spread = found.Max() - found.Min();
            log($"  mise en correspondance : nouveau = {a:0.000} s + {k:0.00000} × ancien ({found.Count} fenêtres, écart entre elles {Ms(spread)})");
            if (spread > 1.5 / fpsN) log("  attention : les fenêtres ne donnent pas toutes le même décalage (montage différent ?) ; la médiane est prise");
            double Map(double o) => a + k * o;

            log("Décalage du son (corrélation avec le son du disque)...");
            foreach (var (slot, i) in audio.Select((s, i) => (s, i)))
            {
                var (offset, shown) = Offset(original, ffprobeStart, slot.Original.Pid, raw, startD, rawItem.Audio[i].Pid, anchors.Take(4), Map, k, work);
                if (offset is not { } o) { log($"  {slot.Name} : pas de mesure sûre ({shown}) : laissé tel quel"); continue; }
                slot.ShiftMs = Math.Abs(o) >= 0.010 ? (int)Math.Round(-o * 1000) : 0;
                log($"  {slot.Name} : le son du MKV est {(o < 0 ? "en avance" : "en retard")} de {Math.Abs(o) * 1000:0} ms ({shown}){(slot.ShiftMs != 0 ? $" : décalé de {slot.ShiftMs} ms" : " : rien à corriger")}");
            }

            // subtitles: the starts of each track against the disc's, mapped; a drift is a line taken off every time of the .sup
            if (subs.Count > 0)
            {
                log("Synchronisation des sous-titres...");
                var files = Extract(raw, true, [.. rawItem.Pg.Select(s => $"i:0x{s.Pid:X}")], Path.Combine(work, "essai"));
                foreach (var (slot, file) in subs.Zip(files))
                {
                    var sup = File.ReadAllBytes(slot.Sup!);
                    var onDisc = File.Exists(file) ? PgsSubtitles.Starts(File.ReadAllBytes(file)) : [];
                    // where tsMuxeR put the file's times: its clock moved by the start of the clip, give or take
                    var moved = shift + FilmSync.Median(PgsSubtitles.Starts(sup).Select(t => FilmSync.Nearest(onDisc, t + shift)).Where(x => Math.Abs(x) < 0.5).DefaultIfEmpty(0));
                    var d = FilmSync.Drift(FilmSync.Pair(slot.OriginalStarts, onDisc, Map));
                    var (begin, end) = (d.A + d.B * rawItem.In / 45000.0, d.A + d.B * rawItem.Out / 45000.0);
                    var fix = d.Count > 0 && Math.Max(Math.Abs(begin), Math.Abs(end)) >= 0.020;
                    if (fix) { PgsSubtitles.Retime(sup, t => t - (d.A + d.B * (t + moved))); File.WriteAllBytes(slot.Sup!, sup); }
                    var what = d.Count == 0 ? "aucun sous-titre commun avec le disque, laissé tel quel"
                             : Math.Abs(end - begin) >= 0.010 ? $"écart {Ms(begin)} au début → {Ms(end)} à la fin (rms {d.Rms * 1000:0} ms)"
                             : $"écart constant {Ms(begin)} (rms {d.Rms * 1000:0} ms)";
                    log($"  {slot.Name} : {d.Count}/{slot.OriginalStarts.Count} débuts appariés, {what}{(fix ? " : corrigé" : d.Count > 0 ? " : rien à corriger" : "")}");
                }
            }
            chapters = Chapters(o => Map(o) - rawItem.In / 45000.0);
            log($"Chapitres ({chapters.Count}) : {string.Join(" ", chapters.Select(Clock))}");
            Tools.TryDelete(trial); // as big as the film
            if (job.MeasureOnly) { log("--measure-only : rien n'est écrit"); Tools.TryDelete(work); return 0; }
            log("Multiplexage corrigé...");
            muxed = Mux(Path.Combine(work, "mux"), chapters);
        }

        // the disc copied, the three files of the film replaced (and their BACKUP copies)
        var output = Path.GetFullPath(job.Out);
        Tools.PrepareOutput(output);
        string Rel(params string[] parts) => Path.Combine(["BDMV", .. parts]);
        var replaced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Rel("STREAM", $"{clip:00000}.m2ts")] = Path.Combine(muxed, "BDMV", "STREAM", $"{clip:00000}.m2ts"),
            [Rel("CLIPINF", $"{clip:00000}.clpi")] = Path.Combine(muxed, "BDMV", "CLIPINF", $"{clip:00000}.clpi"),
            [Rel("PLAYLIST", $"{chosen.Number:00000}.mpls")] = Path.Combine(muxed, "BDMV", "PLAYLIST", $"{chosen.Number:00000}.mpls"),
        };
        foreach (var made in replaced.Values) if (!File.Exists(made)) throw new InvalidOperationException($"tsMuxeR n'a pas écrit {made}");
        log("Copie du disque...");
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var backup = rel.StartsWith(Rel("BACKUP") + "\\", StringComparison.OrdinalIgnoreCase) ? Rel(rel[(Rel("BACKUP").Length + 1)..]) : null;
            if (replaced.ContainsKey(rel) || backup != null && replaced.ContainsKey(backup)) continue;
            var to = Path.Combine(output, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
            File.SetAttributes(to, FileAttributes.Normal); // read-only on a mounted image
            if (new FileInfo(to).Length != new FileInfo(file).Length) throw new IOException($"copie incomplète : {rel}");
            copied++;
        }
        foreach (var (rel, made) in replaced)
        {
            File.Move(made, Path.Combine(output, rel));
            var backup = Path.Combine(output, "BDMV", "BACKUP", rel[5..]);
            if (!rel.Contains("STREAM") && Directory.Exists(Path.GetDirectoryName(backup))) File.Copy(Path.Combine(output, rel), backup);
        }
        log($"  {copied} fichiers du disque copiés tels quels ; {item.Clip}.m2ts, {item.Clip}.clpi et {chosen.Number:00000}.mpls remplacés (et leur copie BACKUP)");

        var failed = Verify(output, chosen.Number, item, audio, subs, anchors, chapters, fpsN, k, a, original, ffprobeStart, work, log);
        if (job.Iso is { } iso)
        {
            log("Image ISO...");
            Iso.Build(output, iso, Label(job), false, log);
            log($"  {iso}");
        }
        Tools.TryDelete(work);
        log(failed == 0 ? "Terminé : vérification sans problème" : $"Terminé, mais {failed} point(s) de vérification hors tolérance");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>The finished disc measured again against the old one: playlist streams and marks, picture, sound and subtitle residuals. Returns the points out of tolerance.</summary>
    static int Verify(string output, int number, Mpls.Item item, List<Slot> audio, List<Slot> subs, List<(double At, List<double> Cuts)> anchors, List<double> chapters,
                      double fpsN, double k, double a, string original, double ffprobeStart, string work, Action<string> log)
    {
        var bad = 0;
        void Check(bool ok, string what) { if (!ok) bad++; log($"  {(ok ? "ok  " : "HORS")} {what}"); }
        log("Vérification du disque écrit...");
        var pl = Mpls.Read(Path.Combine(output, "BDMV", "PLAYLIST", $"{number:00000}.mpls"));
        var fresh = pl.Items[0];
        Check(fresh.Clip == item.Clip, $"la liste joue {fresh.Clip}.m2ts");
        Check(fresh.Audio.Select(s => s.Lang).SequenceEqual(item.Audio.Select(s => s.Lang)) && fresh.Audio.Select(s => s.Coding).SequenceEqual(item.Audio.Select(s => s.Coding)),
              $"sons : {string.Join(", ", fresh.Audio.Select((s, i) => $"A{i + 1} {Mpls.CodingName(s.Coding)} {s.Lang}"))}");
        Check(fresh.Pg.Select(s => s.Lang).SequenceEqual(item.Pg.Select(s => s.Lang)), $"sous-titres : {string.Join(", ", fresh.Pg.Select((s, i) => $"PG{i + 1} {s.Lang}"))}");
        var marks = pl.Chapters.Select(m => ((double)m.Time - fresh.In) / 45000.0).ToList();
        Check(marks.Count == chapters.Count && marks.Zip(chapters).All(p => Math.Abs(p.First - p.Second) <= 1 / fpsN),
              $"{marks.Count} chapitres (le disque en avait {chapters.Count}), écart max {Ms(marks.Zip(chapters).Select(p => Math.Abs(p.First - p.Second)).DefaultIfEmpty().Max())}");
        if (anchors.Count == 0) return bad;

        var m2ts = Path.Combine(output, "BDMV", "STREAM", item.Clip + ".m2ts");
        var startD = StartTime(m2ts);
        double OnDisc(double o) => a + k * o;
        var residuals = new List<double>();
        foreach (var (at, oc) in anchors.Take(3))
        {
            var nc = FilmSync.Cuts(m2ts, startD, OnDisc(oc[0]) - 2, k * (oc[^1] - oc[0]) + 4);
            residuals.AddRange(oc.Select(o => FilmSync.Nearest(nc, OnDisc(o))).Where(d => Math.Abs(d) < 0.5));
        }
        Check(residuals.Count > 0 && Math.Abs(FilmSync.Median(residuals)) <= 0.5 / fpsN,
              $"image : {residuals.Count} changements de plan, résidu médian {(residuals.Count > 0 ? Ms(FilmSync.Median(residuals)) : "?")}, max {(residuals.Count > 0 ? Ms(residuals.Max(Math.Abs)) : "?")}");
        foreach (var (slot, i) in audio.Select((s, i) => (s, i)))
        {
            var (offset, shown) = Offset(original, ffprobeStart, slot.Original.Pid, m2ts, startD, fresh.Audio[i].Pid, anchors.Take(3), OnDisc, k, work);
            if (offset is not { } o) log($"  ?    {slot.Name} : pas de mesure sûre ({shown})");
            else Check(Math.Abs(o) <= 0.020, $"{slot.Name} : décalage {Ms(o)} ({shown})");
        }
        if (subs.Count > 0)
        {
            var files = Extract(m2ts, true, [.. fresh.Pg.Select(s => $"i:0x{s.Pid:X}")], Path.Combine(work, "verif"));
            foreach (var (slot, file) in subs.Zip(files))
            {
                var pairs = FilmSync.Pair(slot.OriginalStarts, File.Exists(file) ? PgsSubtitles.Starts(File.ReadAllBytes(file)) : [], OnDisc);
                if (pairs.Count == 0) { Check(slot.OriginalStarts.Count == 0, $"{slot.Name} : aucun sous-titre commun"); continue; }
                var median = FilmSync.Median(pairs.Select(p => p.Diff));
                var max = pairs.Max(p => Math.Abs(p.Diff));
                Check(Math.Abs(median) <= 0.5 / fpsN, $"{slot.Name} : {pairs.Count} débuts, écart médian {Ms(median)}, " +
                      $"90 % sous {Ms(pairs.Select(p => Math.Abs(p.Diff)).OrderBy(x => x).ElementAt((int)(pairs.Count * 0.9)))}, max {Ms(max)}");
            }
        }
        return bad;
    }

    /// <summary>
    /// How late the sound of PID <paramref name="pidD"/> of a new clip is against the old disc's PID <paramref name="pidO"/> at the same pictures (median of the windows
    /// whose correlation peak is clear), and the measures shown; null when none is clear.
    /// </summary>
    static (double? Offset, string Shown) Offset(string original, double startO, int pidO, string disc, double startD, int pidD,
                                                 IEnumerable<(double At, List<double> Cuts)> windows, Func<double, double> map, double k, string work)
    {
        var measured = new List<(double Offset, double Peak)>();
        foreach (var (at, _) in windows)
        {
            var o = FilmSync.Samples(original, startO, $"0:i:0x{pidO:X}", at + 5, 40 / k, k, work);
            var n = FilmSync.Samples(disc, startD, $"0:i:0x{pidD:X}", map(at + 5) - 2, 44, 1, work);
            var (lag, peak) = FilmSync.Locate(o, n);
            if (!double.IsNaN(lag)) measured.Add((lag - 2, peak));
        }
        var trusted = measured.Where(m => m.Peak >= 0.2).ToList();
        var shown = string.Join(", ", measured.Select(m => $"{Ms(m.Offset)} (pic {m.Peak:0.00})"));
        // windows that disagree measure a different mix, not an offset (the VO of a remux against a disc's: −59 to +98 ms seen)
        if (trusted.Count > 0 && trusted.Max(m => m.Offset) - trusted.Min(m => m.Offset) > 0.020) return (null, shown + " : mesures dispersées, mixage différent ?");
        return (trusted.Count > 0 ? FilmSync.Median(trusted.Select(m => m.Offset)) : null, shown);
    }

    static Mpls? TryRead(string file) { try { return Mpls.Read(file); } catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException) { return null; } }

    static string Slash(string path) => Path.GetFullPath(path).Replace('\\', '/');
    static string Lang(string l) => l is "fre" ? "fra" : l is "ger" ? "deu" : l;
    static string Label(ReplaceFilmJob job) => (job.Label ?? Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(job.Disc)).ToUpperInvariant()) is var n && n.Length > 32 ? n[..32] : n;
    static bool Forced(MkvStream s) => s.Forced || Regex.IsMatch(s.Title, "forc", RegexOptions.IgnoreCase);

    /// <summary>How well an MKV sound fits a disc slot coded <paramref name="coding"/>: the same codec family, then lossless for lossless.</summary>
    static int Score(int coding, MkvStream s)
    {
        var family = coding switch { 0x82 or 0x85 or 0x86 => "dts", 0x81 or 0x84 => "ac3", 0x80 => "pcm", _ => "" };
        var codec = s.Codec.StartsWith("pcm", StringComparison.Ordinal) ? "pcm" : s.Codec == "eac3" ? "ac3" : s.Codec;
        return (codec == family ? 2 : 0) + ((coding is 0x86 or 0x80) == (s.Profile.Contains("MA") || codec == "pcm") ? 1 : 0);
    }

    static string? Muxed(MkvStream s) => s.Codec switch { "dts" => "A_DTS", "ac3" or "eac3" => "A_AC3", _ when s.Codec.StartsWith("pcm_", StringComparison.Ordinal) => "A_LPCM", _ => null };

    static string VideoCodec(MkvStream v) => v.Codec switch
    {
        "h264" => "V_MPEG4/ISO/AVC", "hevc" => "V_MPEGH/ISO/HEVC", "vc1" => "V_MS/VFW/WVC1", "mpeg2video" => "V_MPEG-2",
        _ => throw new NotSupportedException($"image {v.Codec} : pas une image de Blu-ray"),
    };

    /// <summary>The streams of the MKV, its frame rate and (when mkvmerge wrote it) its number of frames.</summary>
    public static List<MkvStream> Probe(string file, out double fps, out long frames)
    {
        using var doc = JsonDocument.Parse(Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-of", "json", "-show_streams", file]));
        static string Tag(JsonElement s, string name) => s.TryGetProperty("tags", out var t) && t.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        static string Str(JsonElement s, string name) => s.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        var list = new List<MkvStream>();
        (fps, frames) = (0, 0);
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = Str(s, "codec_type");
            if (type == "video" && fps == 0)
            {
                var rate = Str(s, "avg_frame_rate") is { Length: > 0 } r && r != "0/0" ? r : Str(s, "r_frame_rate");
                var p = rate.Split('/');
                fps = double.Parse(p[0], Inv) / (p.Length > 1 ? double.Parse(p[1], Inv) : 1);
                frames = long.TryParse(Tag(s, "NUMBER_OF_FRAMES") is { Length: > 0 } n ? n : Tag(s, "NUMBER_OF_FRAMES-eng"), out var f) ? f : 0;
            }
            list.Add(new MkvStream(s.GetProperty("index").GetInt32(), type, Str(s, "codec_name"), Str(s, "profile"), Tag(s, "language"), Tag(s, "title"),
                                   s.TryGetProperty("disposition", out var d) && d.TryGetProperty("forced", out var fo) && fo.GetInt32() == 1));
        }
        return list;
    }

    static double StartTime(string file) =>
        double.TryParse(Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "format=start_time", "-of", "csv=p=0", file]).Trim(), NumberStyles.Float, Inv, out var t) ? t : 0;


    /// <summary>Subtitle streams copied out as .sup files in one pass (times of the file's own clock); <paramref name="maps"/> are ffmpeg stream specifiers ("i:0x1200", "6").</summary>
    static List<string> Extract(string file, bool byPid, List<string> maps, string prefix)
    {
        var outputs = maps.Select((m, i) => $"{prefix}_{i}.sup").ToList();
        var args = new List<string> { "-hide_banner", "-v", "error", "-copyts" };
        if (byPid) args.AddRange(["-probesize", "50M", "-analyzeduration", "30M"]); // a Blu-ray clip can start its subtitles late
        args.AddRange(["-i", file]);
        foreach (var (map, output) in maps.Zip(outputs)) args.AddRange(["-map", "0:" + map, "-c", "copy", "-f", "sup", "-y", output]);
        try { Tools.Run(Tools.Find("ffmpeg.exe"), args); }
        catch (InvalidOperationException) when (outputs.All(File.Exists)) { } // a damaged end of the disc: what was read before it is kept
        return outputs;
    }

    /// <summary>
    /// The MKV from the first keyframe at or after <paramref name="at"/>, every stream copied. ffmpeg's input seek lands on the keyframe at or before
    /// the time asked, so it is asked between that keyframe and the next one; the frames removed are counted against what the MKV says it holds.
    /// </summary>
    static void Cut(string mkv, double at, long frames, string output, Action<string> log)
    {
        var keys = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-select_streams", "v:0", "-read_intervals", $"%+{(at + 15).ToString("0.###", Inv)}",
                                                         "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", mkv])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(',')).Where(p => p.Length >= 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out _))
            .Select(p => (Pts: double.Parse(p[0], Inv), Key: p[1].StartsWith('K'))).ToList();
        var key = keys.Where(p => p.Key && p.Pts >= at - 0.0005).Select(p => p.Pts).DefaultIfEmpty(double.NaN).First();
        if (double.IsNaN(key)) throw new InvalidDataException($"--cut-start {at} : pas d'image clé dans les 15 s qui suivent");
        var next = keys.Where(p => p.Key && p.Pts > key + 0.001).Select(p => p.Pts).DefaultIfEmpty(key + 0.3).First();
        var removed = keys.FindIndex(p => p.Key && p.Pts == key); // the packets before it in decoding order (B frames shown after it come before it in the file)
        var ask = key + Math.Min(0.15, (next - key) / 2);
        log($"Coupe du début : image clé à {key:0.000} s (demandé {at:0.000} s), ffmpeg -ss {ask:0.000}...");
        var counted = 0L;
        Tools.Run(Tools.Find("ffmpeg.exe"), ["-hide_banner", "-ss", ask.ToString("0.###", Inv), "-i", mkv, "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-y", output],
                  line => { if (Regex.Match(line, @"frame=\s*(\d+)") is { Success: true } m) counted = long.Parse(m.Groups[1].Value, Inv); });
        if (frames > 0)
        {
            var ok = frames - removed == counted;
            log($"  {frames} images − {removed} retirées = {frames - removed} ; ffmpeg en a écrit {counted}{(ok ? " : conforme" : " : ÉCART")}");
            if (!ok) throw new InvalidDataException("la coupe n'est pas tombée sur l'image clé attendue");
        }
        else log($"  {removed} images retirées, {counted} écrites");
    }

    /// <summary>The disc's root: the folder itself, or an .iso opened read-only by Windows (and whether this call opened it, to close it after).</summary>
    static (string Root, bool Mounted) Open(string disc)
    {
        if (Directory.Exists(disc)) return (Directory.Exists(Path.Combine(disc, "BDMV")) ? disc : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(disc))!, false);
        if (!File.Exists(disc)) throw new FileNotFoundException($"{disc} : introuvable");
        var quoted = Path.GetFullPath(disc).Replace("'", "''");
        var answer = Tools.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
            $"$i = Get-DiskImage -ImagePath '{quoted}'; $m = 0; if (-not $i.Attached) {{ $i = Mount-DiskImage -ImagePath '{quoted}' -Access ReadOnly -PassThru; $m = 1 }}; \"$m\" + ($i | Get-Volume).DriveLetter"]).Trim();
        if (answer.Length != 2) throw new InvalidDataException($"{Path.GetFileName(disc)} : Windows n'a pas pu ouvrir l'image");
        return (answer[1] + @":\", answer[0] == '1');
    }

    static void Close(string iso, Action<string> log)
    {
        try { Tools.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", $"Dismount-DiskImage -ImagePath '{Path.GetFullPath(iso).Replace("'", "''")}' | Out-Null"]); log("Image refermée"); }
        catch (InvalidOperationException e) { log($"attention : l'image n'a pas pu être refermée ({e.Message.Split('\n')[0]})"); }
    }
}
