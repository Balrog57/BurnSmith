using BurnSmith.Core;
using BurnSmith.Core.Menu;

// burnsmith bd|dvd --video <file> [--video <file2> ...] --out <folder> [--design Midnight] [--title "My Disc"] [--no-menu]
//                  [--chapters 5] [--audio 0,1] [--sub 0] [bd only: --uhd --label X]
// --audio / --sub: 0-based track numbers of each source, kept in that order (default: first audio, no subtitles). Several --video need a menu.
// --sub-file: a subtitle file (srt, ass, ssa, vtt, or sup = PGS pictures) of the video written before it, kept after those tracks; its language comes from the name ("film.fr.srt") or "fra=film.srt"
// --default-audio N / --default-sub N: after a --video, the audio track that plays / the subtitle that shows when the disc starts (numbers as in --audio / --sub, so among the ones kept);
//                  the default audio goes first on the disc, the default subtitle is selected by the disc (a menu's settings page shows it chosen)
// --bonus: after a --video, that video is an extra (trailer, making-of): it goes after the main videos, off "Play" / "Play all", on the menu's Bonus page (needs another, main, video)
var a = args.ToList();
string? Opt(string name) => a.IndexOf(name) is var i and >= 0 && i + 1 < a.Count ? a[i + 1] : null;
List<int>? Numbers(string name) => Opt(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
var videos = a.Select((v, i) => (v, i)).Where(x => x.v == "--video" && x.i + 1 < a.Count).Select(x => Path.GetFullPath(a[x.i + 1])).ToList();

// burnsmith preview <template name or folder> <out folder> [--picture frame.jpg] [--episodes N] [--bonus N]
//   draws every page of a menu template with sample content (3 audio, 2 subtitles), to compare with the vendor's own thumbnail
if (a.Count >= 3 && a[0] == "preview")
{
    var chosen = TemplateCatalog.Find(a[1]) ?? throw new InvalidOperationException($"unknown template: {a[1]}");
    Directory.CreateDirectory(a[2]);
    var pic = Opt("--picture") is { } p ? Path.GetFullPath(p) : null;
    var episodes = int.TryParse(Opt("--episodes"), out var ep) ? ep : 0;
    var sample = new MenuContent(1, pic, Enumerable.Repeat(pic, 7).ToList(),
        episodes > 1 ? Enumerable.Range(1, episodes).Select(i => new TitleEntry($"Episode {i}", i, pic)).ToList() : null,
        ["Français AC-3 mono", "English AC-3 mono", "Español AC-3 mono"], ["Français", "English"],
        int.TryParse(Opt("--bonus"), out var extras) && extras > 0 ? Enumerable.Range(0, extras).Select(i => new TitleEntry($"Bonus {i + 1}", Math.Max(episodes, 1) + 1 + i, pic)).ToList() : null);
    chosen.Title = "My Blu-ray";
    if (Opt("--credits") is { } sampleCredits) chosen.Credits = sampleCredits.Replace('|', '\n');
    var files = MenuRenderer.Sta(() => MenuBuilder.Build(chosen, sample).Select(page => (page.Id, Jpeg: MenuRenderer.Background(chosen, page))).ToList());
    foreach (var (id, jpeg) in files) File.WriteAllBytes(Path.Combine(a[2], $"{chosen.Name}_{id}.jpg"), jpeg);
    Console.WriteLine(string.Join(", ", files.Select(f => f.Id)));
    return 0;
}

// burnsmith dump <template name> : every drawing operation of every page, with its rectangle (to compare a template's XML with what is drawn)
if (a.Count >= 2 && a[0] == "dump")
{
    var chosen = TemplateCatalog.Find(a[1]) ?? throw new InvalidOperationException($"unknown template: {a[1]}");
    var sample = new MenuContent(1, null, Enumerable.Repeat<string?>(null, 7).ToList(), null, ["Français AC-3 mono", "English AC-3 mono"], ["Français"]);
    foreach (var page in MenuBuilder.Build(chosen, sample))
    {
        Console.WriteLine($"== {page.Id}");
        foreach (var op in page.Ops) Console.WriteLine("  " + op);
    }
    return 0;
}

// burnsmith check-templates [--matrix] [--only NAME] [--picture frame.jpg] (--only: the templates whose name contains NAME, no composed designs) : builds every template as film and series and checks pages, buttons, remote moves, pictures;
// --matrix = every disc shape (1..40 chapters, 2..60 episodes, 1..9 audio, 0..9 subtitles) and 200 designs the menu editor can compose
if (a.Count >= 1 && a[0] == "check-templates") return TemplateCheck.Run(Opt("--picture") is { } cp ? Path.GetFullPath(cp) : null, a.Contains("--matrix"), Opt("--only"));

// burnsmith check-edit : the arithmetic of a cut (length, chapters shifted by the start, typed times) without any video
if (a.Count >= 1 && a[0] == "check-edit")
{
    var failed = 0;
    void Check(string what, bool ok) { if (!ok) failed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
    string Show(IEnumerable<double> times) => string.Join(',', times);
    var src = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [], []);
    var whole = new DiscTitle("x.mkv");
    var cutBoth = new DiscTitle("x.mkv", Start: 100, End: 400);
    Check("whole file keeps 600 s", whole.Length(src) == 600 && whole.Cut(src) == src);
    Check("start only: 500 s", new DiscTitle("x", Start: 100).Cut(src).Duration == 500);
    Check("end only: 400 s", new DiscTitle("x", End: 400).Cut(src).Duration == 400);
    Check("both: 300 s", cutBoth.Cut(src).Duration == 300);
    Check("end past the file is the end of the file", new DiscTitle("x", Start: 100, End: 9999).Length(src) == 500);
    Check("empty cut is refused", Throws(() => new DiscTitle("x", Start: 700).Cut(src)) && Throws(() => new DiscTitle("x", Start: 100, End: 100.5).Cut(src)));
    Check("disc setting: one every 5 min from 0", Show(whole.ChapterList(600, 5)) == "0,300");
    Check("no chapters: only 0", Show(new DiscTitle("x", ChapterAt: []).ChapterList(600, 5)) == "0");
    Check("custom times are shifted by the start, 0 added, outside dropped", Show(new DiscTitle("x", Start: 100, End: 400, ChapterAt: [50, 100, 160, 250.4, 399.5, 500]).ChapterList(300, 5)) == "0,60,150");
    Check("custom times in whole seconds, unique", Show(new DiscTitle("x", ChapterAt: [10.2, 10.4, 5]).ChapterList(600, 5)) == "0,5,10");
    var dir = Path.Combine(Path.GetTempPath(), "bs_extsub_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try
    {
        foreach (var f in new[] { "v.mkv", "v.srt", "v.fr.srt", "v.English.forced.ass", "v10.srt", "w.srt", "v.txt", "Dr.No.1962.en.srt", "film.fr.forced.srt", "Do It Now.srt", "2_English.srt" }) File.WriteAllText(Path.Combine(dir, f), "");
        var near = ExternalSubtitle.Near(Path.Combine(dir, "v.mkv"));
        Check("sidecar files: name, language, forced, other videos' files left alone", string.Join('|', near.Select(n => $"{Path.GetFileName(n.File)}={n.Lang}{(n.Forced ? "!" : "")}")) == "v.English.forced.ass=eng!|v.fr.srt=fre|v.srt=und");
        string Lang(string f, string video = "v.mkv") { var e = ExternalSubtitle.For(Path.Combine(dir, video), Path.Combine(dir, f)); return e.Lang + (e.Forced ? "!" : ""); }
        Check("language at the end of the name: Dr.No.1962.en = English not Norwegian, forced flag, a title with a language word has none, 2_English", Lang("Dr.No.1962.en.srt") == "eng" && Lang("film.fr.forced.srt") == "fre!" && Lang("Do It Now.srt") == "und" && Lang("2_English.srt") == "eng");
        Check("a file named after the video is read from what follows its name", Lang("v.fr.srt") == "fre" && Lang("v.English.forced.ass") == "eng!" && Lang("v.srt") == "und");
        Check("language typed by hand", ExternalSubtitle.Parse("ger=" + Path.Combine(dir, "v.fr.srt")).Lang == "ger" && ExternalSubtitle.Parse(Path.Combine(dir, "v.fr.srt")).Lang == "fre");
    }
    finally { Directory.Delete(dir, true); }
    var two = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [], [new(0, "subrip", "eng", "", false), new(1, "hdmv_pgs_subtitle", "fre", "", false)]);
    var withFiles = new DiscTitle("x", Start: 100, External: [new("a.fr.srt", "fre"), new("b.ass")]).Cut(two);
    Check("subtitle files come after the video's tracks, numbered on, the cut keeps them", withFiles.Subtitles.Select(t => $"{t.Index}{(t.File != null ? t.Codec : "")}").SequenceEqual(["0", "1", "2subrip", "3ass"]) && withFiles.Duration == 500);
    var cues = "1\r\n00:00:01,000 --> 00:00:03,000\r\nbefore\r\n\r\n2\r\n00:00:05,000 --> 00:00:08,500\r\nstraddles\r\nthe start\r\n\r\n3\r\n00:00:12,000 --> 00:00:14,000\r\nkept\r\n\r\n4\r\n00:00:17,000 --> 00:00:19,000\r\nafter\r\n\r\n";
    Check("cut cues: before dropped, straddling cue shortened and moved, later ones dropped, renumbered",
          ExternalSubtitle.Cut(cues, 6, 8) == "1\n00:00:00,000 --> 00:00:02,500\nstraddles\nthe start\n\n2\n00:00:06,000 --> 00:00:08,000\nkept\n\n" && ExternalSubtitle.Cut(cues, 9, 2) == "" && ExternalSubtitle.Cut(cues, 0, 100).Contains("4\n00:00:17,000 --> 00:00:19,000\nafter"));
    // a PGS stream built by hand: 8x4 picture (1 = white, 2 = black, 0 = nothing), shown at 1 s and 5 s, cleared at 3 s and 8 s
    static byte[] Segment(byte kind, double seconds, params byte[] body) => BurnSmith.Core.Hdmv.Pgs.Segment(kind, (uint)(seconds * 90000), (uint)(seconds * 90000), body);
    static byte[] Be(int value, int bytes) => [.. Enumerable.Range(0, bytes).Select(i => (byte)(value >> 8 * (bytes - 1 - i)))];
    byte[] SupSet(double at, bool shown, int x = 100, int y = 900, bool split = false)
    {
        var picture = BurnSmith.Core.Hdmv.Pgs.Rle([1, 1, 1, 1, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 1, 0, 0, 2, 2, 0, 1, 2, 1, 2, 1, 2, 1, 2], 8, 4);
        byte[] placed = shown ? [.. Be(0, 2), 0, 0, .. Be(x, 2), .. Be(y, 2)] : [];
        byte[] composition = [.. Be(1920, 2), .. Be(1080, 2), 0x10, .. Be(1, 2), (byte)(shown ? 0x80 : 0), 0, 0, (byte)(shown ? 1 : 0), .. placed];
        var set = new List<byte[]> { Segment(0x16, at, composition), Segment(0x17, at, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0) };
        if (shown)
        {
            set.Add(Segment(0x14, at, [0, 0, 1, 235, 128, 128, 255, 2, 16, 128, 128, 255]));
            if (!split) set.Add(Segment(0x15, at, [.. Be(0, 2), 0, 0xC0, .. Be(picture.Length + 4, 3), .. Be(8, 2), .. Be(4, 2), .. picture]));
            else // the picture in two pieces: first (0x80) and last (0x40)
            {
                set.Add(Segment(0x15, at, [.. Be(0, 2), 0, 0x80, .. Be(picture.Length + 4, 3), .. Be(8, 2), .. Be(4, 2), .. picture[..10]]));
                set.Add(Segment(0x15, at, [.. Be(0, 2), 0, 0x40, .. picture[10..]]));
            }
        }
        set.Add(Segment(0x80, at));
        return [.. set.SelectMany(s => s)];
    }
    byte[] sup = [.. SupSet(1, true), .. SupSet(3, false), .. SupSet(5, true, 200), .. SupSet(8, false)];
    string Times(IEnumerable<PgsSubtitles.Cue> list) => string.Join('|', list.Select(c => FormattableString.Invariant($"{c.Start:0.##}-{c.End:0.##}")));
    var pictures = PgsSubtitles.Cues(sup, out var undecoded);
    Check("PGS pictures: shown until cleared, placed, 8x4 on a 1920x1080 plane, white and black kept, nothing transparent",
          Times(pictures) == "1-3|5-8" && undecoded == 0 && pictures[1].X == 200 && pictures[0].Y == 900 && pictures[0].Width == 8 && pictures[0].Height == 4 && pictures[0].PlaneWidth == 1920 && PgsSubtitles.PlaneSize(sup) == (1920, 1080)
          && pictures[0].Luma[0] == 255 && pictures[0].Luma[8] == 0 && pictures[0].Alpha[0] == 255 && pictures[0].Alpha[4] == 0);
    Check("PGS cut: a picture on screen at the start stays from 0, later ones move back, the clear after the end is dropped, the last lasts 3 s",
          Times(PgsSubtitles.Cues(PgsSubtitles.Cut(sup, 2, 4), out _)) == "0-1|3-6");
    Check("PGS cut: starting right after a clear keeps nothing old; nothing to show gives an empty file; the whole stream is unchanged",
          Times(PgsSubtitles.Cues(PgsSubtitles.Cut(sup, 3.5, 5), out _)) == "1.5-4.5" && PgsSubtitles.Cut(sup, 9, 5).Length == 0 && PgsSubtitles.Cut(sup, 0, 100).SequenceEqual(sup));
    var pieces = PgsSubtitles.Cues([.. SupSet(1, true, split: true), .. SupSet(3, false)], out _);
    Check("PGS picture sent in two pieces reads the same as in one", pieces.Count == 1 && pieces[0].Luma.SequenceEqual(pictures[0].Luma) && pieces[0].Alpha.SequenceEqual(pictures[0].Alpha));
    var notSup = false;
    try { PgsSubtitles.Cues([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14], out _); } catch (InvalidDataException) { notSup = true; }
    var pgsFile = new ExternalSubtitle("a.fr.sup", "fre").Track(2);
    Check("a .sup is a PGS track the disc can carry, named after its language; text tracks are not PGS", notSup && pgsFile.IsPgs && pgsFile.Supported && ExternalSubtitle.Extensions.Contains(".sup") && !new ExternalSubtitle("a.srt").Track(0).IsPgs);
    var choices = Languages.Choices.ToList();
    Check("languages to choose from: one per language, each code read back as itself, a name for each", choices.Count >= 20 && choices.Select(c => c.Name).Distinct().Count() == choices.Count
          && choices.All(c => c.Name.Length > 0 && Languages.Code(c.Code) == c.Code) && choices.Any(c => c.Code == "fre" && c.Name == "Français"));
    Check("same language: fre = fra, not ger, and an unknown language is only itself", Languages.Same("fre", "fra") && !Languages.Same("fre", "ger") && Languages.Same("und", "und") && !Languages.Same("und", "fre") && Languages.Same("xyz", "xyz"));
    Check("language words", Languages.Code("fr") == "fre" && Languages.Code("fra") == "fra" && Languages.Code("français") == "fre" && Languages.Code("German") == "ger" && Languages.Code("2017") == null && Languages.Code("it") == "ita");
    var three = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [new(0, "ac3", 2, "fre", ""), new(1, "ac3", 2, "eng", ""), new(2, "dts", 6, "ger", "")], []);
    string Kept(DiscTitle t) => string.Join(',', t.KeptAudio(three).Select(a => a.Index));
    Check("default audio goes first, the others keep their order; a default that is not kept, null or already first change nothing",
          Kept(new DiscTitle("x", Audio: [0, 1, 2], DefaultAudio: 2)) == "2,0,1" && Kept(new DiscTitle("x", Audio: [0, 2], DefaultAudio: 1)) == "0,2" && Kept(new DiscTitle("x", Audio: [0, 1])) == "0,1"
          && Kept(new DiscTitle("x", Audio: [0, 1], DefaultAudio: 0)) == "0,1" && Kept(new DiscTitle("x", DefaultAudio: 1)) == "0");
    SubtitleTrack[] carried = [new(0, "subrip", "eng", "", false), new(2, "hdmv_pgs_subtitle", "fre", "", false)];
    Check("default subtitle: its 1-based number among the tracks on the disc, 0 when none, not chosen or not carried",
          new DiscTitle("x", DefaultSubtitle: 2).DefaultSubtitleNumber(carried) == 2 && new DiscTitle("x", DefaultSubtitle: 0).DefaultSubtitleNumber(carried) == 1 && new DiscTitle("x").DefaultSubtitleNumber(carried) == 0 && new DiscTitle("x", DefaultSubtitle: 1).DefaultSubtitleNumber(carried) == 0);
    byte[] cmdA = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], cmdB = [2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2], cmdC = [3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3];
    var movieObjects = Bdmv.PrependToFilm(BurnSmith.Core.Hdmv.HdmvNav.MovieObjects([[cmdA, cmdB], [cmdC]]), cmdC);
    Check("a command put first in the film's movie object: object 0 gains it before its own, the others stay, the file stays valid",
          movieObjects.Length == BurnSmith.Core.Hdmv.HdmvNav.MovieObjects([[cmdC, cmdA, cmdB], [cmdC]]).Length && movieObjects.SequenceEqual(BurnSmith.Core.Hdmv.HdmvNav.MovieObjects([[cmdC, cmdA, cmdB], [cmdC]]))
          && new Func<bool>(() => { try { Bdmv.PrependToFilm([1, 2, 3], cmdA); return false; } catch (InvalidDataException) { return true; } })());
    var order = DiscTitle.MainFirst([new("b1.mkv", Bonus: true), new("m1.mkv"), new("b2.mkv", Bonus: true), new("m2.mkv")]);
    Check("disc order: the feature first, then the bonus, each group in its own order", string.Join(',', order.Select(t => t.Video)) == "m1.mkv,m2.mkv,b1.mkv,b2.mkv");
    Check("a disc of bonus only is refused", Throws(() => DiscTitle.MainFirst([new("b.mkv", Bonus: true)])) && DiscTitle.MainFirst([]).Count == 0);
    Check("typed times", Timecode.Parse("90") == 90 && Timecode.Parse("1:30") == 90 && Timecode.Parse("1:02:03,5") == 3723.5 && Timecode.Parse("1:x") == null && Timecode.Parse("1.5:00") == null && Timecode.Parse("") == null);
    Check("times shown", Timecode.Format(3723) == "1:02:03" && Timecode.Format(90.5) == "0:01:30.5");
    return failed == 0 ? 0 : 1;

    static bool Throws(Action act) { try { act(); return false; } catch (ArgumentException) { return true; } }
}

// burnsmith pack-templates <out.zip> : zips the DVDFab / Leawo template pictures and layouts found on this PC (publish.ps1 embeds the zip in the exe)
if (a.Count >= 2 && a[0] == "pack-templates")
{
    Console.WriteLine($"{TemplateCatalog.Pack(Path.GetFullPath(a[1]), Console.WriteLine)} templates packed");
    return 0;
}

// burnsmith templates : every template found, and the ones that cannot be drawn with the reason
if (a.Count >= 1 && a[0] == "templates")
{
    var (usable, skipped) = TemplateCatalog.Scan();
    foreach (var f in usable) Console.WriteLine($"OK    {f.Design.Category} | {f.Design.Name}");
    foreach (var s in skipped) Console.WriteLine($"SKIP  {s.Name} | {s.Why} | {s.Dir}");
    Console.WriteLine($"{usable.Count} usable, {skipped.Count} skipped");
    return 0;
}

// burnsmith drives | iso <disc folder> <out.iso> [--label X] [--dvd] | burn <image.iso> <F:>   (Windows' own IMAPI2, nothing to install)
if (a.Count >= 1 && a[0] == "drives")
{
    var drives = Imapi.Drives();
    foreach (var d in drives)
    {
        var disc = d.Letter.Length > 0 ? Imapi.Inspect(d.Letter) : null;
        Console.WriteLine($"{(d.Letter.Length > 0 ? d.Letter : "?"),-3} {d.Name} - {(disc == null ? "no usable disc" : $"{disc.Kind}, {(disc.Blank ? "blank" : "not blank")}, {disc.FreeBytes / 1e9:0.0} GB free")}");
    }
    if (drives.Count == 0) Console.WriteLine("no disc burner visible to Windows");
    Imapi.SelfTest();
    Console.WriteLine("IMAPI2: typed interfaces and progress events OK");
    return 0;
}
if (a.Count >= 3 && a[0] == "verify") { Imapi.Verify(a[1], a[2], Console.WriteLine, new Progress<double>(p => Console.Write($"\r{p:P0}  "))); return 0; }
if (a.Count >= 3 && a[0] == "iso") { Iso.Build(a[1], a[2], Opt("--label") ?? "BURNSMITH", a.Contains("--dvd"), Console.WriteLine); return 0; }
if (a.Count >= 3 && a[0] == "burn") { Iso.Burn(a[1], a[2], Console.WriteLine, new Progress<double>(p => Console.Write($"\r{p:P0}  "))); return 0; }

if (a.Count == 0 || a[0] is not ("bd" or "dvd") || videos.Count == 0 || Opt("--out") is not { } outDir)
{
    Console.Error.WriteLine("usage: burnsmith bd|dvd --video <file> [--trim A-B --volume N --chapter-at T,T|--no-chapters] [--video <file> ...] --out <folder> [--design NAME] [--title TEXT] [--no-menu] [--music FILE --music-start S --music-volume N --music-fade S --loop-seconds S] [--chapters MINUTES] [--audio N,N] [--sub N,N] [--sub-file FILE|LANG=FILE] [--bonus] [--credits 'A|B'] [bd: --uhd --profile NAME --quality Q --play-mode N --label NAME --titles 'fra=Titre|eng=Title']");
    return 2;
}

var design = a.Contains("--no-menu") ? null
    : (TemplateCatalog.Find(Opt("--design")) ?? (Opt("--design") is { } unknown ? throw new InvalidOperationException($"unknown template: {unknown}") : TemplateCatalog.Default())).Clone();
if (design != null && Opt("--title") is { } title) design.Title = title;
if (design != null && Opt("--credits") is { } credits) design.Credits = credits.Replace('|', '\n'); // --credits "Directed by A|Music by B"
if (design != null && Opt("--titles") is { } byLanguage) design.TitlesByLanguage = byLanguage.Replace('=', ':').Replace('|', '\n'); // --titles "fra=Mon disque|eng=My disc"
if (design != null && a.Contains("--loop")) // moving menu: --loop [--loop-file video] [--loop-start S]
{
    design.Loop = true;
    design.LoopFile = Opt("--loop-file") is { } lf ? Path.GetFullPath(lf) : null;
    design.LoopStart = int.TryParse(Opt("--loop-start"), out var ls) ? ls : 0;
}
// menu music (with or without --loop): --music audio [--music-start S] [--music-volume PERCENT] [--music-fade S] [--loop-seconds S = how long the loop lasts, 30 by default]
if (design != null && int.TryParse(Opt("--loop-seconds"), out var lsec)) design.LoopSeconds = lsec;
if (design != null && Opt("--music") is { } mu)
{
    design.Music = Path.GetFullPath(mu);
    if (int.TryParse(Opt("--music-start"), out var ms)) design.MusicStart = ms;
    if (int.TryParse(Opt("--music-volume"), out var mv)) design.MusicVolume = mv;
    if (int.TryParse(Opt("--music-fade"), out var mf)) design.MusicFade = mf;
}
var chapters = int.TryParse(Opt("--chapters"), out var m) ? m : 5;
// cut, volume and chapters belong to one video: the flags written after a --video (up to the next one) are that video's; with a single video they may be anywhere
// --trim START-END (seconds or h:mm:ss, either side may stay empty: --trim 90- | --trim -1:30:00), --volume PERCENT (100 = unchanged),
// --chapter-at T,T,... (chapter times in the source video, replacing "one every N minutes") | --no-chapters
var videoAt = a.Select((v, i) => (v, i)).Where(x => x.v == "--video").Select(x => x.i).ToList();
var titles = videos.Select((v, k) =>
{
    var own = videoAt.Count == 1 ? a : a.Skip(videoAt[k]).Take(k + 1 < videoAt.Count ? videoAt[k + 1] - videoAt[k] : a.Count).ToList();
    string? Own(string name) => own.IndexOf(name) is var i and >= 0 && i + 1 < own.Count ? own[i + 1] : null;
    double? Time(string text) => text.Length == 0 ? null : Timecode.Parse(text) ?? throw new ArgumentException($"not a time: '{text}'");
    var trim = Own("--trim")?.Split('-', 2) ?? ["", ""];
    var volume = int.TryParse(Own("--volume"), out var vol) ? Math.Clamp(vol, 10, 300) : 100;
    IReadOnlyList<double>? chapterAt = own.Contains("--no-chapters") ? [] : Own("--chapter-at")?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => Time(t.Trim()) ?? 0).ToList();
    // --sub-file FILE | --sub-file fra=FILE (repeatable): a subtitle file kept as one more track, after the video's own and the --sub ones
    var files = own.Select((x, i) => (x, i)).Where(x => x.x == "--sub-file" && x.i + 1 < own.Count).Select(x => ExternalSubtitle.Parse(own[x.i + 1])).ToList();
    var subs = Numbers("--sub");
    if (files.Count > 0) subs = [.. subs ?? [], .. Enumerable.Range(BluRay.Probe(v).Subtitles.Count, files.Count)];
    int? defaultAudio = int.TryParse(Own("--default-audio"), out var da) ? da : null, defaultSub = int.TryParse(Own("--default-sub"), out var ds) ? ds : null;
    if (defaultAudio is { } wanted && !(Numbers("--audio") ?? [0]).Contains(wanted)) throw new ArgumentException($"--default-audio {wanted} is not one of the audio tracks kept (--audio)");
    if (defaultSub is { } wantedSub && !(subs ?? []).Contains(wantedSub)) throw new ArgumentException($"--default-sub {wantedSub} is not one of the subtitles kept (--sub / --sub-file)");
    return new DiscTitle(v, null, Numbers("--audio"), subs, Time(trim[0]) ?? 0, trim.Length > 1 ? Time(trim[1]) : null, volume, chapterAt, files.Count > 0 ? files : null, own.Contains("--bonus"), defaultAudio, defaultSub);
}).ToList();
// --reencode (never copy the video), --crf N and --preset NAME (x264 quality of a Blu-ray), --dvd-kbps N (DVD video bit rate by hand)
// bd: --profile "BD25 720p" (BD50|BD25|BD9|BD5, 1080p/i or 720p; default BD25), --quality source|fit|15|20|30|<kb/s> (fit = "Adapter au disque"),
//     --capacity GB (usable size, instead of the profile's), --play-mode 0|1|2 (menu then all titles | first title then all | back to the menu) [--no-rewind]
var profile = BdProfile.Find(Opt("--profile") ?? "BD25");
var quality = Opt("--quality") ?? "source";
var rate = int.TryParse(quality, out var q) ? (q < 1000 ? q * 1000 : q) : 0; // "15" = 15 Mb/s
var encode = new EncodeOptions(a.Contains("--reencode"), int.TryParse(Opt("--crf"), out var crf) ? crf : 17, Opt("--preset") ?? "medium", int.TryParse(Opt("--dvd-kbps"), out var kbps) ? kbps : 0,
                               rate, quality == "fit", profile.Hd720);
var capacity = double.TryParse(Opt("--capacity"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : profile.UsableGb;
var playback = int.TryParse(Opt("--play-mode"), out var mode) ? PlaybackOptions.FromMode(mode, !a.Contains("--no-rewind")) : null;

if (a[0] == "bd")
    BluRay.Build(new BluRayJob(titles, Path.GetFullPath(outDir), design, a.Contains("--uhd"), Opt("--label") ?? "BURNSMITH", chapters, encode, capacity, playback), Console.WriteLine);
else
    Dvd.Build(new DvdJob(titles, Path.GetFullPath(outDir), design, chapters, Encode: encode), Console.WriteLine);
return 0;
