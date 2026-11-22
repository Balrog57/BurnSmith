using BurnSmith.Cli;
using BurnSmith.Core;
using BurnSmith.Core.Menu;

// burnsmith bd|dvd --video <file> [--video <file2> ...] --out <folder> [--design Midnight] [--title "My Disc"] [--no-menu]
//                  [--chapters 5] [--audio 0,1] [--sub 0] [bd only: --uhd --label X]
// --audio / --sub: 0-based track numbers of each source, kept in that order (default: first audio, no subtitles). Several --video need a menu.
// --sub-file: a subtitle file (srt, ass, ssa, vtt, sup = PGS pictures, or idx = VobSub with its .sub, one track per language it holds) of the video written before it, kept after those tracks;
//             its language comes from the name ("film.fr.srt") or "fra=film.srt" (for an .idx: only that language)
// --track-lang a0=fre,s1=eng: after a --video, the language of one of that file's own tracks (a = audio, s = subtitle, numbered as in --audio / --sub), for one left "und" or mislabelled;
// --default-audio N / --default-sub N: after a --video, the audio track that plays / the subtitle that shows when the disc starts (numbers as in --audio / --sub, so among the ones kept);
//                  the default audio goes first on the disc, the default subtitle is selected by the disc (a menu's settings page shows it chosen)
// --intro: after a --video, that video plays first when the disc starts, before the menu (before the film when there is no menu); not a title of the menu (Blu-ray and DVD)
// --bonus: after a --video, that video is an extra (trailer, making-of): it goes after the main videos, off "Play" / "Play all", on the menu's Bonus page (needs another, main, video)
// --trial [SECONDS], --save-project FILE, --sub-delay N=S (after a --video), --sub-font / --sub-size / --sub-color / --sub-position: see where they are read below; burnsmith project FILE.burnsmith builds a saved project
var a = args.ToList();
if (a.Count == 0 || a[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        BurnSmith CLI — authoring DVD / Blu-ray / 4K UHD / AVCHD

        Fabrication :
          burnsmith bd|dvd --video <fichier> [options vidéo...] --out <dossier> [options disque...]
          burnsmith project <fichier.burnsmith> [--out <dossier>] [--trial [S]] [--max-compat] [--recap] [--shutdown]
          burnsmith volumes <fichier.burnsmith> [--split N1,N2,...] [--out <dossier>] [--build]
          burnsmith queue <a.burnsmith> <b.burnsmith> ... [--trial [S]] [--shutdown]

        Options vidéo (après chaque --video) :
          --name TEXTE | --trim A-B[,C-D] | --volume N | --bonus | --intro
          --audio N,N | --default-audio N | --audio-file [LANG=]FICHIER | --audio-delay N=S | --audio-volume N=DB
          --sub N,N|none | --default-sub N | --sub-file [LANG=]FICHIER | --sub-delay N=S | --track-lang a0=fre,s1=eng
          --chapter-at T,T | --chapters-file FICHIER(.txt|.xml) | --chapter-names "A|B" | --chapter-every MIN | --no-chapters

        Options disque (bd / dvd) :
          --uhd | --avchd | --profile NOM | --capacity GO | --quality fit|source|N | --reencode | --cpu | --fps R
          --menu-mode bdj|hdmv | --design NOM|FICHIER.json | --title TEXTE | --no-menu | --popup | --loop | --music FICHIER
          --loudnorm | --hdr-nits N | --no-lossless | --no-copy-eac3 | --play-mode 0|1|2 | --auto-start S | --remember-position
          --sub-font NOM | --sub-size % | --sub-color RRGGBB | --sub-position 0-100
          --preview [--safe-area] | --recap | --trial [S] | --save-project FICHIER | --max-compat | --shutdown

        ISO, gravure & outils :
          burnsmith drives | iso <dossier> <out.iso> [--label X] [--dvd] | burn <image.iso> <F:> [--speed N] [--no-verify] [--eject] [--erase]
          burnsmith verify <image.iso> <F:> | erase <F:> | play <dossier|iso> | probe <vidéo>
          burnsmith check-disc <dossier|iso> | check-navigation <dossier|iso> | scan-damage <dossier|iso>
          burnsmith slideshow|song|join ... | templates | preview <modèle> <out> | make-qualif-pack --out <dossier>
        """);
    return 0;
}
if (a[0] == "make-qualif-pack") return QualifPack.Run(Opt("--out") ?? throw new ArgumentException("make-qualif-pack requiert --out <dossier>"));
string? Opt(string name) => a.IndexOf(name) is var i and >= 0 && i + 1 < a.Count ? a[i + 1] : null;
IEnumerable<string> OptAll(string name) { for (var i = 0; i + 1 < a.Count; i++) if (a[i] == name) yield return a[i + 1]; } // --element can repeat
List<int>? Numbers(string name) => Opt(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

// --sub numbers the video's own tracks, then its --sub-file ones, which are kept whatever it says, once. A number with no track is told and left out: one video of a series may have fewer, so it stops nothing.
static List<int> SubsToKeep(List<int>? asked, int own, int files, string video, Action<string> tell)
{
    var missing = (asked ?? []).Where(i => i < 0 || i >= own + files).Distinct().ToList();
    if (missing.Count > 0) tell($"warning: --sub {string.Join(',', missing)}: {video} has no such subtitle ({own} of its own, {files} from --sub-file)");
    return [.. (asked ?? []).Except(missing).Union(Enumerable.Range(own, files))];
}
var videos = a.Select((v, i) => (v, i)).Where(x => x.v == "--video" && x.i + 1 < a.Count).Select(x => BluRay.MainClip(Path.GetFullPath(a[x.i + 1]))).ToList(); // a Blu-ray given whole (.iso, BDMV folder): its film

// burnsmith preview <template name or folder> <out folder> [--picture frame.jpg] [--episodes N] [--bonus N] [--focus] [--menu-aspect 16:9|4:3] [--safe-area]
//   draws every page of a menu template with sample content (3 audio, 2 subtitles), to compare with the vendor's own thumbnail
if (a.Count >= 3 && a[0] == "preview")
{
    // a catalogue name, a template folder, or a design saved as .json (design-export, « Enregistrer le modèle »); --overview TEXT: each episode's summary
    var chosen = (a[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(a[1]) ? System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(a[1])) : TemplateCatalog.Find(a[1]))
        ?? throw new InvalidOperationException($"unknown template: {a[1]}");
    Directory.CreateDirectory(a[2]);
    var pic = Opt("--picture") is { } p ? Path.GetFullPath(p) : null;
    var episodes = int.TryParse(Opt("--episodes"), out var ep) ? ep : 0;
    var sample = new MenuContent(1, pic, Enumerable.Repeat(pic, 7).ToList(),
        episodes > 1 ? Enumerable.Range(1, episodes).Select(i => new TitleEntry($"Episode {i}", i, pic, Opt("--overview"))).ToList() : null,
        ["Français AC-3 mono", "English AC-3 mono", "Español AC-3 mono"], ["Français", "English"],
        int.TryParse(Opt("--bonus"), out var extras) && extras > 0 ? Enumerable.Range(0, extras).Select(i => new TitleEntry($"Bonus {i + 1}", Math.Max(episodes, 1) + 1 + i, pic)).ToList() : null);
    if (Opt("--menu-aspect") is { } screen) chosen = TemplateCatalog.Shaped(chosen, screen == "4:3"); // as bd and dvd: the template's 4:3 or 16:9 version
    chosen.Language(Opt("--menu-language") is "fr" or "en" ? Opt("--menu-language")! : chosen.ButtonLanguage); // as bd and dvd: French unless asked or the design says "en"
    chosen.Title = Opt("--title") ?? (chosen.ButtonLanguage == "en" ? "My Blu-ray" : "Mon Blu-ray");
    if (a.Contains("--list")) chosen.SceneList = true; // la page de scènes en liste, quand le modèle la décrit
    if (a.Contains("--scenes-only")) chosen.ScenesOnly = true;
    if (a.Contains("--bonus-text")) chosen.BonusText = true;
    if (Opt("--button-language") is "fr" or "en") chosen.ButtonLanguage = Opt("--button-language")!;
    if (Opt("--credits") is { } sampleCredits) chosen.Credits = sampleCredits.Replace('|', '\n');
    foreach (var spec in OptAll("--element")) chosen.Elements.Add(MenuElement.Parse(spec));
    // --focus: every button drawn as it is under the remote (one at a time on the disc), to see the selected state of a template
    byte[] Lit(MenuPage page, byte[] jpeg)
    {
        static System.Windows.Media.Imaging.BitmapSource Picture(byte[] data) => System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(data), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(Picture(jpeg), new System.Windows.Rect(0, 0, MenuBuilder.W, MenuBuilder.H));
            // every button lit at once; of those that write a text when selected (an episode's summary), only the first, or the texts would pile up
            foreach (var b in page.Buttons.Where(b => b.FocusTexts == null || b == page.Buttons.First(o => o.FocusTexts != null)))
            {
                var r = MenuRenderer.FocusBox(b);
                dc.DrawImage(Picture(MenuRenderer.Focus(chosen, page, b)), new System.Windows.Rect(r.X, r.Y, r.W, r.H));
            }
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(MenuBuilder.W, MenuBuilder.H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
    var previewPages = MenuBuilder.Build(chosen, sample);
    foreach (var w in SafeArea.Warnings(previewPages, chosen)) Console.WriteLine(w); // what a TV may crop, with or without --safe-area
    var files = MenuRenderer.Sta(() => previewPages.Select(page => (page.Id, Jpeg: MenuRenderer.Background(chosen, page) is var jpeg && a.Contains("--focus") ? Lit(page, jpeg) : jpeg)).ToList());
    if (a.Contains("--safe-area")) files = MenuRenderer.Sta(() => files.Select((f, i) => (f.Id, SafeArea.Over(f.Jpeg, previewPages[i], chosen))).ToList()); // the action-safe / title-safe frames drawn over each page
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

// burnsmith check-plan : automated checks for BD-J jar bytecode, manual season split (+30 % suggestion), submenus, directional links, auto-start and remember-position
if (a.Count >= 1 && a[0] == "check-plan") return PlanChecks.Run();
// burnsmith check-disc <disc folder | .iso [source folder]> : disc structure and Blu-ray chapter/subtitle menu targets, without a player; 1 when something is wrong
if (a.Count >= 2 && a[0] == "check-disc")
{
    var found = Directory.Exists(a[1]) ? DiscCheck.Folder(a[1]) : DiscCheck.Image(a[1], a.Count > 2 ? a[2] : null);
    Console.WriteLine(found.Count == 0 ? "ok   disc structure" : string.Join(Environment.NewLine, found.Select(p => "FAIL " + p)));
    return found.Count == 0 ? 0 : 1;
}
// burnsmith check-navigation <disc folder | .iso> [--audio N] [--sub N] : trace des menus BD-J BurnSmith
if (a.Count >= 2 && a[0] == "check-navigation")
{
    try
    {
        var audio = Opt("--audio") is { } audioText ? int.Parse(audioText) : 0;
        var sub = Opt("--sub") is { } subText ? int.Parse(subText) : -1;
        var (trace, problems) = DiscCheck.Navigation(a[1], audio, sub);
        foreach (var line in trace) Console.WriteLine(line);
        foreach (var problem in problems) Console.Error.WriteLine("FAIL " + problem);
        return problems.Count == 0 ? 0 : 1;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or UnauthorizedAccessException or FormatException or OverflowException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}
// burnsmith scan-damage <file.iso | disc folder> [--decode | --no-decode] : where a ripped disc is damaged (sectors filled with FF or zeros, lost sync, packets still encrypted,
//   each placed in its file and in time), read without mounting; short clips (stills, menus) are decoded by ffmpeg too, --decode decodes every stream,
//   --no-decode none; a picture the decoder had to conceal is damage; 1 when damage is found
if (a.Count >= 2 && a[0] == "scan-damage")
{
    var (report, damaged) = DamageScan.Run(a[1], a.Contains("--decode"), line => Console.Error.WriteLine(line), !a.Contains("--no-decode"));
    foreach (var line in report) Console.WriteLine(line);
    return damaged ? 1 : 0;
}
// burnsmith check-scan-damage : scan-damage on made-up clips (clean, a unit of null packets, FF to the end, a scrambled still, a scrambled mp3 stream); 1 when one is misjudged
if (a.Count >= 1 && a[0] == "check-scan-damage")
{
    var wrong = DamageScan.SelfCheck();
    Console.WriteLine(wrong.Count == 0 ? "ok   scan-damage" : string.Join(Environment.NewLine, wrong.Select(p => "FAIL " + p)));
    return wrong.Count == 0 ? 0 : 1;
}
// burnsmith slideshow <out.mkv> [--seconds 5] [--music song]... <photo>... : photos shown one after the other (with the songs under them) as a video, to give to --video
// burnsmith song <out.mkv> <audio> [--cover picture] : a song as a video (its cover, or black, for as long as it plays)
// burnsmith join <out.mkv> <video> <video>... : videos with the same streams end to end in one file (streams copied)
if (a.Count >= 3 && a[0] is "slideshow" or "song" or "join")
{
    var made = Path.GetFullPath(a[1]);
    var music = OptAll("--music").Select(Path.GetFullPath).ToList();
    if (a[0] == "slideshow")
    {
        var pictures = a.Skip(2).Where((x, i) => !x.StartsWith("--") && !(i > 0 && a[i + 1] is "--seconds" or "--music" or "--transition")).ToList();
        MediaMaker.Slideshow(pictures.Select(Path.GetFullPath).ToList(), double.TryParse(Opt("--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var each) ? each : 5, music, made, Console.WriteLine,
                             Opt("--transition") is { } tr ? MediaMaker.TransitionOf(tr) : null); // --transition fondu|glissement|volet|cercle|mélange|aucune
    }
    else if (a[0] == "join") MediaMaker.Join(a.Skip(2).Select(Path.GetFullPath).ToList(), made, Console.WriteLine);
    else MediaMaker.Song(Path.GetFullPath(a[2]), Opt("--cover") is { } c ? Path.GetFullPath(c) : MediaMaker.CoverOf(a[2], Path.GetDirectoryName(made)!), made, Console.WriteLine);
    Console.WriteLine("ok   " + made);
    return 0;
}
// --no-dvd: check-bd, check-edit and check-reliability name and pass over the real DVD builds (slow)
// burnsmith check-bd [--no-dvd] : short clips through the real builds, then what a player needs of them read with ffprobe (sound rate, bit depth, level, sync), the DVD refusals, the work folder, damaged PGS
if (a.Count >= 1 && a[0] == "check-bd") return BdCheck.Run(a.Contains("--no-dvd"));
// burnsmith check-reliability [--no-dvd] : Cancel on real Blu-ray and DVD encodes (how fast, no process left, no work folder, not a tool failure), half an image, a source gone, the error log, closing
if (a.Count >= 1 && a[0] == "check-reliability") return ReliabilityCheck.Run(a.Contains("--no-dvd"));
// burnsmith check-recap : « À savoir avant de démarrer » on made-up sources (no video read): copied / converted, sound remade, how full, what plays at the start
if (a.Count >= 1 && a[0] == "check-recap") return RecapCheck.Run();
// burnsmith check-templates [--matrix] [--only NAME] [--picture frame.jpg] (--only: the templates whose name contains NAME, no composed designs) : builds every template as film and series and checks pages, buttons, remote moves, pictures;
// --matrix = every disc shape (1..40 chapters, 2..60 episodes, 1..9 audio, 0..9 subtitles) and 200 designs the menu editor can compose
if (a.Count >= 1 && a[0] == "check-templates") return TemplateCheck.Run(Opt("--picture") is { } cp ? Path.GetFullPath(cp) : null, a.Contains("--matrix"), Opt("--only"));

// burnsmith check-highlights : les surbrillances DVD prises aux modèles (image de survol du modèle, PNG 2 bits à 4 couleurs, repli sur le cadre générique)
// burnsmith check-highlights : les surbrillances DVD prises aux modèles (image de survol du modèle, PNG 2 bits à 4 couleurs, repli sur le cadre générique)
if (a.Count >= 1 && a[0] == "check-highlights") return HighlightsCheck.Run(Opt("--out"));

// burnsmith check-vectors [templates folder] [--max percent] [--write folder] : every picture that has a traced .svg beside it (tools\vectorize-buttons.py), drawn both ways at the picture's size.
// Prints, worst first, the share of pixels the trace changes by more than 40 levels (colour or transparency) and the mean change; fails when a trace is over --max (default 100: it only measures).
if (a.Count >= 1 && a[0] == "check-vectors")
{
    var vecRoot = a.Skip(1).Where((x, i) => !x.StartsWith("--") && !a[i].StartsWith("--")).FirstOrDefault() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BurnSmith", "Templates", "sources");
    var vecMax = a.IndexOf("--max") is var at and >= 0 && at + 1 < a.Count ? double.Parse(a[at + 1], System.Globalization.CultureInfo.InvariantCulture) : 100;
    var vecWrite = a.IndexOf("--write") is var wr and >= 0 && wr + 1 < a.Count ? a[wr + 1] : null; // the traces as BurnSmith draws them, one PNG each: to look at
    var vecAlone = Directory.CreateTempSubdirectory("burnsmith-vec").FullName; // each picture away from its drawing, which is drawn in its place wherever it stands beside it
    var rows = MenuRenderer.Sta(() => Directory.EnumerateFiles(vecRoot, "*.svg", SearchOption.AllDirectories)
        .Select(svg => (Svg: svg, Png: Path.ChangeExtension(svg, ".png"))).Where(p => File.Exists(p.Png))
        .Select((p, n) => { var alone = Path.Combine(vecAlone, $"{n}.png"); File.Copy(p.Png, alone); return (p.Svg, Png: alone); }).Where(p => MenuRenderer.Size(p.Png) != null)
        .Select(p =>
        {
            var (w, h) = MenuRenderer.Size(p.Png)!.Value;
            byte[] drawn = MenuRenderer.Raster(p.Svg, null, w, h).Px, picture = MenuRenderer.Raster(p.Png, null, w, h).Px;
            if (vecWrite != null)
            {
                var file = Path.Combine(vecWrite, Path.ChangeExtension(Path.GetRelativePath(vecRoot, p.Svg), ".png"));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, drawn, w * 4)));
                using var stream = File.Create(file);
                encoder.Save(stream);
            }
            long off = 0, sum = 0;
            for (var i = 0; i < picture.Length; i += 4)
            {
                var worst = 0;
                for (var c = 0; c < 4; c++) { var d = Math.Abs(drawn[i + c] - picture[i + c]); sum += d; worst = Math.Max(worst, d); }
                if (worst > 40) off++;
            }
            return (Off: 100.0 * off / (w * h), Mean: sum / (double)picture.Length, p.Svg);
        }).OrderByDescending(r => r.Off).ToList());
    foreach (var r in rows) Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.Off,6:F2} %  {r.Mean,6:F2}  {Path.GetRelativePath(vecRoot, r.Svg)}"));
    Directory.Delete(vecAlone, true);
    var over = rows.Count(r => r.Off > vecMax);
    Console.WriteLine($"{rows.Count} tracés, {over} au-delà de {vecMax} %");
    return over == 0 ? 0 : 1;
}

// burnsmith svg-sheet <dir> <out.png> : every SVG of a folder drawn wide (300x90) and square (120x120), normal then selected, one row each — to look at
if (a.Count >= 3 && a[0] == "svg-sheet")
{
    var files = Directory.EnumerateFiles(a[1], "*.svg").Where(f => !f.EndsWith(".sub.svg", StringComparison.OrdinalIgnoreCase)).Order().ToList();
    var png = MenuRenderer.Sta(() =>
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 70, 80)), null, new System.Windows.Rect(0, 0, 1000, files.Count * 130));
            for (var i = 0; i < files.Count; i++)
            {
                var sub = Path.ChangeExtension(files[i], ".sub.svg");
                var x = 10.0;
                foreach (var (file, w, h) in new[] { (files[i], 300, 90), (files[i], 120, 120), (sub, 300, 90), (sub, 120, 120) })
                {
                    if (File.Exists(file))
                    {
                        var r = MenuRenderer.Raster(file, null, w, h);
                        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, r.Px, w * 4);
                        dc.DrawRectangle(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Gray, 1), new System.Windows.Rect(x, i * 130 + 5, w, h));
                        dc.DrawImage(bmp, new System.Windows.Rect(x, i * 130 + 5, w, h));
                    }
                    x += w + 15;
                }
                dc.DrawText(new System.Windows.Media.FormattedText(Path.GetFileNameWithoutExtension(files[i]), System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Segoe UI"), 13, System.Windows.Media.Brushes.White, 1), new System.Windows.Point(10, i * 130 + 108));
            }
        }
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(1000, files.Count * 130, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        target.Render(visual);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    });
    File.WriteAllBytes(a[2], png);
    Console.WriteLine($"{files.Count} SVG -> {a[2]}");
    return 0;
}

// burnsmith check-elements :the free elements of the menu (reading a command-line element, which pages it goes on, what it draws) without a video or a window
// burnsmith check-elements : the free elements of the menu without a video or a window
if (a.Count >= 1 && a[0] == "check-elements") return ElementsCheck.Run();

// burnsmith check-update [--live] : release tag comparison, updater safeguards and tool lookup
if (a.Count >= 1 && a[0] == "check-update") return await UpdateCheck.Run(a.Contains("--live"));

// burnsmith check-edit [--no-dvd] : cut arithmetic, chapters, subtitles, tracks and DVD edit checks
if (a.Count >= 1 && a[0] == "check-edit") return EditCheck.Run(a.Contains("--no-dvd"), SubsToKeep);

// burnsmith pack-templates <out.zip> : zips the template pictures and layouts found on this PC (publish.ps1 embeds the zip in the exe)
if (a.Count >= 2 && a[0] == "pack-templates")
{
    Console.WriteLine($"{TemplateCatalog.Pack(Path.GetFullPath(a[1]), Console.WriteLine)} templates packed");
    return 0;
}

// burnsmith convert-templates [--out DIR] [--force] : converts every template found on this PC to the single pack format
// (template.json + PNG backgrounds / buttons / images, in Documents\BurnSmith\Templates\converted), then checks every pack
if (a.Count >= 1 && a[0] == "convert-templates")
{
    var packRootOut = Opt("--out") is { } packOut ? Path.GetFullPath(packOut) : null;
    var result = TemplatePack.ConvertAll(Console.WriteLine, packRootOut, a.Contains("--force"));
    Console.WriteLine($"{result.Converted} converted, {result.Skipped} kept, {result.Failed} failed");
    foreach (var message in result.Messages) Console.Error.WriteLine(message);
    return result.Failed == 0 ? 0 : 1;
}
// burnsmith check-conversion : every pack is readable, its pictures are there, and each has at least one background and one button
if (a.Count >= 1 && a[0] == "check-conversion")
{
    var (packs, ok, packProblems) = TemplatePack.Verify();
    foreach (var problem in packProblems) Console.WriteLine($"FAIL {problem}");
    Console.WriteLine($"{ok}/{packs} packs without a problem");
    return packProblems.Count == 0 && packs > 0 ? 0 : 1;
}
// burnsmith check-fidelity [--picture frame.jpg] [--only NAME] : each source template and the pack converted from it draw the same menu
if (a.Count >= 1 && a[0] == "check-fidelity") return TemplateCheck.Fidelity(Opt("--picture") is { } fp ? Path.GetFullPath(fp) : null, Opt("--only"));

// burnsmith check-library : every background and button picture of every converted pack is offered by the library
if (a.Count >= 1 && a[0] == "check-library")
{
    var (libraryTemplates, _) = TemplateCatalog.Scan();
    var libraryBackgrounds = ElementLibrary.Backgrounds((libraryTemplates, []));
    var libraryButtons = ElementLibrary.Buttons((libraryTemplates, []));
    var buttonHashes = libraryButtons.Where(b => b.Normal != null)
        .Select(b => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(b.Normal!))))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var libraryProblems = 0;
    var packButtonPictures = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // the pictures the packs offer: the library must give each back
    foreach (var pack in TemplatePack.Scan())
    {
        foreach (var asset in pack.Assets.Where(x => x.Role == "background"))
        {
            var file = Path.Combine(pack.Dir, asset.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) continue; // already reported by check-conversion
            if (libraryBackgrounds.Any(b => b.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            Console.WriteLine($"FAIL fond absent de la bibliothèque : {pack.Category} | {pack.Name} -> {asset.Path}");
            libraryProblems++;
        }
        foreach (var asset in pack.Assets.Where(x => x.Role == "button"))
        {
            var file = Path.Combine(pack.Dir, asset.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file) || MenuRenderer.Size(file) == null) continue; // absent or damaged: the library leaves it out too
            if (asset.Focus is { } focusRelative)
            {
                var focusFile = Path.Combine(pack.Dir, focusRelative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(focusFile) || MenuRenderer.Size(focusFile) == null) continue;
            }
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
            if (!packButtonPictures.Add(hash) || buttonHashes.Contains(hash)) continue; // shared with another pack, or already given back
            Console.WriteLine($"FAIL bouton absent de la bibliothèque : {pack.Category} | {pack.Name} -> {asset.Path}");
            libraryProblems++;
        }
    }
    Console.WriteLine($"{libraryBackgrounds.Count} fonds, {libraryButtons.Count} boutons, {libraryProblems} problème(s)");
    // a machine whose templates carry pictures only (the open library) has no button picture to give back: the styles alone are then the whole
    // button list, and asking for one more would fail that machine for having fewer templates, not for losing a picture
    var pictureButtonsNeeded = packButtonPictures.Count > 0;
    // the templates' own button pictures only: DVDStyler's SVG buttons, BurnSmith's own sets (« BurnSmith · Or »…) and the user's are there on every machine
    var pictureButtonsOffered = libraryButtons.Any(b => b.Source is not (ElementLibrary.Styles or ElementLibrary.DvdStyler or ElementLibrary.MyButtons) && !b.Source.StartsWith(TemplateLabel.Own + " · ", StringComparison.Ordinal));
    return libraryProblems == 0 && libraryBackgrounds.Count > 0 && pictureButtonsNeeded == pictureButtonsOffered ? 0 : 1;
}

// burnsmith templates [TEXT] [--use USAGE] [--star NAME] [--unstar NAME] : every template found, and the ones that cannot be drawn with the reason;
//   TEXT: only those with it in their name, use or group (the gallery's search box), --use: one of the gallery's groups (Film, Série, Mariage…),
//   case and accents aside; favourites first, marked « | favori »; --star / --unstar: the gallery's star; RECENT: the last used in the menu editor
if (a.Count >= 1 && a[0] == "templates")
{
    var (usable, skipped) = TemplateCatalog.Scan();
    foreach (var (flag, on) in new[] { ("--star", true), ("--unstar", false) })
        if (Opt(flag) is { } starred)
        {
            if (usable.FirstOrDefault(f => f.Design.Name.Equals(starred, StringComparison.OrdinalIgnoreCase) || f.Label.Equals(starred, StringComparison.OrdinalIgnoreCase)) is not { } hit)
            { Console.Error.WriteLine($"unknown template: {starred} (burnsmith templates lists them)"); return 2; }
            TemplatePicks.Star(hit.Design.Name, on);
        }
    var text = a.Count > 1 && !a[1].StartsWith("--") ? a[1] : null;
    var use = Opt("--use");
    if (use != null && !TemplateUsage.All.Any(u => TemplatePicks.Contains(u, use))) { Console.Error.WriteLine($"unknown use: {use} ({string.Join(", ", TemplateUsage.All)})"); return 2; }
    var filtered = text != null || use != null;
    foreach (var f in TemplatePicks.Sorted(usable.Where(f => TemplatePicks.Matches(f, text) && (use == null || TemplatePicks.Contains(f.Usage, use)))))
        Console.WriteLine($"OK    {f.Design.Category} | {f.Design.Name} | {f.Usage}{(f.Favorite ? " | favori" : "")}"); // the program it came from, its name, its use (the gallery's groups)
    foreach (var mine in Templates.List().Where(m => use == null && (text == null || TemplatePicks.Contains(m.Name, text)))) Console.WriteLine($"OK    Mes modèles | {mine.Name}"); // saved from the menu editor or by template-save
    foreach (var s in skipped.Where(_ => !filtered)) Console.WriteLine($"SKIP  {s.Name} | {s.Why} | {s.Dir}");
    foreach (var r in TemplatePicks.Recent()) Console.WriteLine($"RECENT {r}");
    Console.WriteLine($"{usable.Count} usable, {skipped.Count} skipped");
    return 0;
}

// burnsmith check-drives : the burner list's wording and the "disc too small" rule, then the burners of this PC (read only: nothing is written or ejected)
if (a.Count >= 1 && a[0] == "check-drives")
{
    var bad = 0;
    void DriveCheck(string what, bool ok) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) bad++; }
    Imapi.Media bdr = new("BD-R", true, 25_025_314_816, 25_025_314_816), bdre = new("BD-RE", false, 0, 25_025_314_816),
                closed = new("BD-R", false, 0, 20_000_000_000), dvd = new("DVD+R", true, 4_700_372_992, 4_700_372_992);
    DriveCheck("a blank BD-R reads « BD-R vierge, 25 Go »", Iso.DiscState(bdr, true) == "BD-R vierge, 25 Go");
    DriveCheck("a written BD-RE reads « BD-RE déjà écrit »", Iso.DiscState(bdre, true) == "BD-RE déjà écrit");
    DriveCheck("an empty drive reads « pas de disque », a pressed disc « disque non inscriptible »", Iso.DiscState(null, false) == "pas de disque" && Iso.DiscState(null, true) == "disque non inscriptible");
    DriveCheck("a BD-50 project on a blank BD-25 is too big, a BD-25 one fits", Iso.TooSmall(bdr, 46.5) && !Iso.TooSmall(bdr, 23.3));
    DriveCheck("a written BD-RE is judged on its size once erased", Iso.TooSmall(bdre, 46.5) && !Iso.TooSmall(bdre, 23.3));
    DriveCheck("a closed BD-R is not judged (it says only what it holds)", !Iso.TooSmall(closed, 46.5));
    DriveCheck("a DVD-9 project on a DVD+R is too big, a DVD-5 one fits", Iso.TooSmall(dvd, 7.6) && !Iso.TooSmall(dvd, 4.2));
    var listed = Iso.Drives();
    foreach (var d in listed) Console.WriteLine($"     {d.Letter} {d.State} - {d.Name}");
    DriveCheck("this PC: only drives that write are listed (no « Msft Virtual DVD-ROM »)", listed.All(d => d.Writes && !d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)));
    DriveCheck("this PC: each burner IMAPI2 reaches says what is in it", listed.All(d => d.Id == d.Letter || d.State.Length > 0));
    Console.WriteLine(bad == 0 ? "check-drives: all ok" : $"check-drives: {bad} failed");
    return bad == 0 ? 0 : 1;
}

// burnsmith drives | iso <disc folder> <out.iso> [--label X] [--dvd] | burn <image.iso> <F:>   (images: IMAPI2; burning: ImgBurn when installed, else IMAPI2)
if (a.Count >= 1 && a[0] == "drives")
{
    Console.WriteLine($"ImgBurn: {Iso.ImgBurn() ?? "absent (burning through IMAPI2)"}");
    var drives = Iso.Drives();
    // the app's burner list: writers only (no mounted image), each with its disc; Id == Letter: a drive only ImgBurn reaches, its disc unknown
    foreach (var d in drives) Console.WriteLine($"{d.Letter,-3} {(d.State.Length > 0 ? d.State : "disque inconnu (ImgBurn seul)")} - {d.Name}");
    if (drives.Count == 0) Console.WriteLine("no disc burner visible to Windows");
    if (Opt("--capacity") is { } need && double.TryParse(need, System.Globalization.CultureInfo.InvariantCulture, out var needGb))
        foreach (var d in drives)
            if (Iso.Disc(d.Letter) is { } disc && Iso.TooSmall(disc, needGb)) Console.WriteLine($"warning: the {disc.Kind} in {d.Letter} holds {disc.TotalBytes / 1e9:0.0} GB, less than the {needGb:0.0} GB asked");
    Imapi.SelfTest();
    Console.WriteLine("IMAPI2: typed interfaces and progress events OK");
    return 0;
}
// burnsmith erase <F:> : erases a rewritable disc quickly (the app's "Effacer")
if (a.Count >= 2 && a[0] == "erase") { Imapi.Erase(a[1], Console.WriteLine); return 0; }
if (a.Count >= 3 && a[0] == "verify")
{
    try { Imapi.Verify(a[1], a[2], Console.WriteLine, new Progress<double>(p => Console.Write($"\r{p:P0}  "))); return 0; }
    catch (IOException e) { Console.Error.WriteLine(e.Message); return 1; } // no disc, or a disc that differs from the image
}
// burnsmith hold-tool FILE [--free] : ffmpeg for a minute, its progress in FILE; check-reliability ends this process by force and looks for that ffmpeg
if (a.Count >= 2 && a[0] == "hold-tool")
{
    Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc=s=64x64:r=10:d=60", "-progress", a[1], "-f", "null", "-"], outlivesApp: a.Contains("--free"));
    return 0;
}
// burnsmith design-export NAME|FILE.json OUT.json : a template as the JSON --design reads (what "Enregistrer le modèle" saves), to edit and pass back
if (a.Count >= 3 && a[0] == "design-export")
{
    var named = a[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(a[1]))
        : TemplateCatalog.Find(a[1]);
    if (named == null) { Console.Error.WriteLine($"unknown template: {a[1]} (burnsmith templates lists them)"); return 2; }
    File.WriteAllText(a[2], System.Text.Json.JsonSerializer.Serialize(named.Clone(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"template '{a[1]}' written to {a[2]}");
    return 0;
}
// burnsmith template-save NAME|design.json [NEW NAME] : keeps a design in « Mes modèles » (Documents\BurnSmith\Templates\mine), as the editor's
// « Enregistrer le modèle… » does; the editor lists it, and preview, design-export and --design find it by its name
if (a.Count >= 2 && a[0] == "template-save")
{
    var kept = a[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(a[1]) ? System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(a[1])) : TemplateCatalog.Find(a[1])?.Clone();
    if (kept == null) { Console.Error.WriteLine($"unknown template: {a[1]} (burnsmith templates lists them)"); return 2; }
    if (a.Count >= 3) kept.Name = a[2];
    Templates.Save(kept);
    Console.WriteLine($"template '{kept.Name}' saved in {Templates.Dir}");
    return 0;
}
// burnsmith import-background PICTURE... | import-button PICTURE [SELECTED PICTURE] : copies pictures (jpg, png, bmp, svg) into the user's library,
// as the editor's « Importer… » does (Fonds, Boutons); prints where each copy is, the path a design or --element takes
if (a.Count >= 2 && a[0] is "import-background" or "import-button")
{
    try
    {
        if (a[0] == "import-background") foreach (var picture in a.Skip(1)) Console.WriteLine(ElementLibrary.Import(Path.GetFullPath(picture)).Path);
        else if (ElementLibrary.ImportButton(Path.GetFullPath(a[1]), a.Count >= 3 ? Path.GetFullPath(a[2]) : null) is var button)
            Console.WriteLine(button.Normal + (button.Focus == null ? "" : Environment.NewLine + button.Focus));
    }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
    return 0;
}
// burnsmith probe VIDEO : the video and its tracks as BurnSmith reads them (JSON); "Index" is the number --audio / --sub take, "Name" what the menu shows
if (a.Count >= 2 && a[0] == "probe")
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(BluRay.Probe(a[1]), new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return 0;
}
// burnsmith repair-top-menu FOLDER : a BD-J menu disc made before 1.0.32 (the Top Menu key refused during an episode) put on today's start and xlet, its menu kept; then "burnsmith iso" for a new image
if (a.Count >= 2 && a[0] == "repair-top-menu") { var fixedIt = BluRay.RepairTopMenu(a[1]); Console.WriteLine(fixedIt ? "repaired: the Top Menu is the menu, each video plays in its own title" : "nothing to repair (not a BD-J menu disc of the old kind)"); return 0; }
// burnsmith replace-film DISC(folder|.iso) --mkv FILM.mkv --out FOLDER [--iso OUT.iso] [--label X] [--playlist N] [--cut-start S] [--map A1=mkv:2,PG2=mkv:7] [--no-align] [--measure-only]
//   a damaged Blu-ray's film replaced by a clean MKV, menus, bonuses and every other file kept byte for byte: the same sound and subtitle slots (A1, A2..., PG1...)
//   filled from the MKV's tracks of those languages (full or forced; --map N = the MKV stream as ffprobe numbers it, 0 = the video), the MKV put on the disc's clock
//   (scene cuts, sound offsets, subtitle drift: corrected), the chapters mapped; --playlist: the film's playlist (default: the longest); --cut-start: the MKV's first
//   seconds removed, from the keyframe at or after S; --no-align: no measure, no correction; --measure-only: measures and prints, writes nothing. A check of the result ends it
if (a.Count >= 2 && a[0] == "replace-film")
{
    var map = OptAll("--map").SelectMany(m => m.Split(',', StringSplitOptions.RemoveEmptyEntries)).Select(m => m.Split('='))
        .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p.Length == 2 && int.TryParse(p[1].Trim().Replace("mkv:", ""), out var n) ? n : throw new ArgumentException($"--map {string.Join('=', p)} : A1=mkv:N attendu"));
    if (Opt("--mkv") is not { } mkv || Opt("--out") is not { } outFolder) { Console.Error.WriteLine("replace-film : --mkv et --out sont nécessaires"); return 2; }
    return ReplaceFilm.Run(new ReplaceFilmJob(Path.GetFullPath(a[1]), Path.GetFullPath(mkv), Path.GetFullPath(outFolder), Opt("--iso") is { } isoOut ? Path.GetFullPath(isoOut) : null,
        int.TryParse(Opt("--playlist"), out var plNumber) ? plNumber : null,
        double.TryParse(Opt("--cut-start"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cutAt) ? cutAt : null,
        map, a.Contains("--no-align"), a.Contains("--measure-only"), Opt("--label")), Console.WriteLine);
}
// burnsmith check-film-sync : replace-film's measuring pieces on made-up data (scene-cut matching, sound cross-correlation, subtitle drift, PGS re-timing)
if (a.Count >= 1 && a[0] == "check-film-sync")
{
    var problems = FilmSync.SelfCheck();
    Console.WriteLine(problems.Count == 0 ? "ok   film sync" : string.Join(Environment.NewLine, problems.Select(p => "FAIL " + p)));
    return problems.Count == 0 ? 0 : 1;
}
// burnsmith label-from-scan SCAN.png|jpg OUT.png [--diameter-mm 118] [--dpi 300] [--hole-mm auto|N] [--debug OVERLAY.png] : le scan de la face imprimée
// d'un disque devient une étiquette à imprimer (carré blanc hors du disque et au trou, centré sur le moyeu comme l'impression) ; --debug : le scan avec les cercles trouvés
if (a.Count >= 3 && a[0] == "label-from-scan")
{
    double Mm(string name, string fallback) => double.Parse(Opt(name) ?? fallback, System.Globalization.CultureInfo.InvariantCulture);
    var lfs = DiscLabel.Make(a[1], a[2], Mm("--diameter-mm", "118"), Mm("--dpi", "300"), Opt("--hole-mm") is null or "auto" ? null : Mm("--hole-mm", "0"), Opt("--debug"));
    Console.WriteLine($"centre {lfs.CenterX:0.0}, {lfs.CenterY:0.0} ({(lfs.OnHub ? $"moyeu, rayon {lfs.HubRadius:0.0} px" : "bord extérieur")}) ; bord extérieur {lfs.OuterX:0.0}, {lfs.OuterY:0.0} rayon {lfs.OuterRadius:0.0} px ; trou rayon {lfs.HoleRadius:0} px ; {lfs.ScanDpi:0} ppp");
    foreach (var note in lfs.Notes) Console.WriteLine("attention : " + note);
    return 0;
}
if (a.Count >= 3 && a[0] == "iso") { Iso.Build(a[1], a[2], Opt("--label") ?? "BURNSMITH", a.Contains("--dvd"), Console.WriteLine); return 0; }
// burnsmith burn IMAGE.iso F: [--speed 4] [--no-verify] [--eject] [--erase] : the speed is × 1x, absent = the drive's fastest (one disc: a second one is a second run);
//   --no-verify skips reading the disc back, --eject opens the tray at the end, --erase erases a rewritable disc that already holds data (refused without it)
//   [--max-compat] : « Compatibilité maximale »: without --speed, the slowest speed offered
// Exit 0 also when the disc is written but ImgBurn did not verify it. The log reports the warning.
if (a.Count >= 3 && a[0] == "burn" && File.Exists(a[1]) && Iso.Disc(a[2]) is { } inDrive && Iso.TooSmall(inDrive, new FileInfo(a[1]).Length / 1e9))
    Console.WriteLine($"warning: the image ({new FileInfo(a[1]).Length / 1e9:0.0} GB) is larger than the {inDrive.Kind} in {a[2]} ({inDrive.TotalBytes / 1e9:0.0} GB)");
if (a.Count >= 3 && a[0] == "burn")
{
    Iso.Burn(a[1], a[2], Console.WriteLine, new Progress<double>(p => Console.Write($"\r{p:P0}  ")), speed: int.TryParse(Opt("--speed"), out var sp) ? sp : a.Contains("--max-compat") ? Iso.Speeds[^1] : 0,
             verify: !a.Contains("--no-verify"), eject: a.Contains("--eject"), erase: a.Contains("--erase"));
    return 0;
}
// burnsmith play FOLDER|IMAGE.iso : the disc opened in the VLC installed on this PC (« Lire sur l'ordinateur »); 1 when VLC is not installed
if (a.Count >= 2 && a[0] == "play")
{
    Console.WriteLine($"vlc {Vlc.Arguments(a[1])}");
    if (!Vlc.Play(a[1])) { Console.WriteLine($"VLC n'est pas installé sur cet ordinateur : {Vlc.Site}"); return 1; }
    if (!Vlc.Arguments(a[1]).StartsWith("\"dvd:")) Console.WriteLine(Vlc.JavaNote);
    return 0;
}
// burnsmith check-play : VLC's command line for a disc (bluray:/// or dvd:///, quoted, an ISO as it is), without starting VLC
if (a.Count >= 1 && a[0] == "check-play")
{
    var playFailed = 0;
    void Pl(string what, bool ok, string shown) { if (!ok) playFailed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what} : {shown}"); }
    var playRoot = Path.Combine(Path.GetTempPath(), "bs_play " + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var bd = Directory.CreateDirectory(Path.Combine(playRoot, "JUDGE DREDD", "BDMV")).Parent!.FullName;
        var dvdDir = Directory.CreateDirectory(Path.Combine(playRoot, "Épisodes #1 à 100%", "VIDEO_TS")).Parent!.FullName;
        var image = Path.Combine(playRoot, "JUDGE DREDD.iso");
        File.WriteAllBytes(image, []);
        var url = "bluray:///" + bd.Replace('\\', '/');
        // one argument for Windows: a quote at each end, none inside, no backslash before the last (it would escape it)
        static bool One(string s) => s.Length > 2 && s[0] == '"' && s[^1] == '"' && s.Count(c => c == '"') == 2 && s[^2] != '\\';
        var b = Vlc.Arguments(bd);
        Pl("a Blu-ray folder with a space is bluray:/// and one argument", b == $"\"{url}\"" && One(b), b);
        Pl("its BDMV folder, or a trailing \\, give the same", Vlc.Arguments(Path.Combine(bd, "BDMV")) == b && Vlc.Arguments(bd + "\\") == b, Vlc.Arguments(Path.Combine(bd, "BDMV")));
        var d = Vlc.Arguments(dvdDir);
        Pl("a DVD folder is dvd:///, # and % escaped (VLC reads # as title:chapter)", d == $"\"dvd:///{dvdDir.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23")}\"" && One(d) && !d.Contains('#'), d);
        var i = Vlc.Arguments(image);
        Pl("an ISO is passed as it is, quoted", i == $"\"{image}\"" && One(i), i);
        var refused = false;
        try { Vlc.Arguments(playRoot); } catch (InvalidOperationException) { refused = true; }
        Pl("a folder with no disc is refused before VLC starts", refused, playRoot);
        var exe = Vlc.Find();
        Pl("VLC found is the installed one, never the test copy under tools\\vlc", exe == null || !exe.Contains(@"\tools\vlc\", StringComparison.OrdinalIgnoreCase), exe ?? "VLC not installed here (the window then offers videolan.org)");
    }
    finally { Directory.Delete(playRoot, true); }
    Console.WriteLine(playFailed == 0 ? "all ok" : $"{playFailed} failed");
    return playFailed == 0 ? 0 : 1;
}

// --trial [SECONDS] (bd, dvd, project): a trial disc, each video cut to SECONDS in all (300 = five minutes), a passage at each chapter: the whole menu, made in minutes, to try in VLC before the real build
// --menu-mode bdj|hdmv (bd): the Blu-ray menu as the BD-J program (default: everything the menu can do) or as native HDMV navigation (every player runs it
// without Java; still pages, no pop-up menu: see HdmvMenu)
// --max-compat (bd, dvd, project, burn): « Compatibilité maximale », what every player reads: the HDMV menu, every sound in AC-3 with a Dolby Digital track first,
//   the slowest burn speed (see Project.Compatible). Before every build, the warnings that apply (a 100 GB disc) and, on a Blu-ray, the PS5 note are printed
bool Hdmv() => Opt("--menu-mode") switch { null or "bdj" => false, "hdmv" => true, var other => throw new ArgumentException($"--menu-mode: bdj or hdmv expected, not '{other}'") };
int Trial() => !a.Contains("--trial") ? 0 : int.TryParse(Opt("--trial"), out var seconds) && seconds > 0 ? seconds : 300;
// what the builds below share: the folder made, then its structure checked against the disc it must fit
int Make(string kind, List<DiscTitle> list, string folder, MenuDesign? menu, string label, int minutes, EncodeOptions enc, double usableGb, PlaybackOptions? play, SubtitleStyle? look, int trial, double checkGb)
{
    list = Project.OnDisc(list, minutes, trial, BluRay.Probe);
    var hdmv = Hdmv();
    if (a.Contains("--max-compat")) (list, enc, hdmv) = Project.Compatible(list, enc, BluRay.Probe);
    if (Project.Warning(kind, usableGb) is { } warning) Console.WriteLine("warning: " + warning);
    if (kind != "dvd") Console.WriteLine("note: " + Project.Ps5Note);
    if (trial > 0) Console.WriteLine($"trial disc: {trial} s of each video");
    var avchd = kind != "dvd" && a.Contains("--avchd");
    try
    {
        if (kind == "dvd") Dvd.Build(new DvdJob(list, folder, menu, minutes, usableGb, enc, play, look), Console.WriteLine);
        else BluRay.Build(new BluRayJob(list, folder, menu, kind == "uhd", label, minutes, enc, usableGb, play, look, hdmv, avchd), Console.WriteLine);
    }
    catch (InvalidDataException e) { Console.Error.WriteLine(e.Message); return 1; }
    var checkFolder = avchd && !Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).Equals("AVCHD", StringComparison.OrdinalIgnoreCase)
        ? Path.Combine(folder, "AVCHD") : folder;
    var found = DiscCheck.Folder(checkFolder, checkGb);
    Console.WriteLine(found.Count == 0 ? "disc structure checked: no problem" : "disc structure: " + string.Join(" ; ", found));
    return found.Count == 0 ? 0 : 1;
}
// burnsmith project FILE.burnsmith [--out FOLDER] [--trial [SECONDS]] : builds the disc of a project saved by the app (« Enregistrer le projet ») or by --save-project,
//   as its Démarrer button would: its videos, menu and options; --out replaces the project's folder, --trial its own trial setting,
//   --max-compat turns its « Compatibilité maximale » on
int BuildProject(string file)
{
    var p = Project.Load(file) is var saved && a.Contains("--max-compat") ? saved with { MaxCompat = true } : saved;
    var found = p.Build(Opt("--out") is { } folder ? Path.GetFullPath(folder) : null, a.Contains("--trial") ? Trial() : p.Trial, Console.WriteLine);
    Console.WriteLine(found.Count == 0 ? "disc checked: no problem" : "disc check: " + string.Join(" ; ", found));
    return found.Count == 0 ? 0 : 1;
}
// --shutdown (bd, dvd, project, queue): the app's « Éteindre le PC à la fin »: once the command has made its disc(s), good or not, Windows shuts down a minute later (`shutdown /a` stops it)
int Finished(int code)
{
    if (!a.Contains("--shutdown")) return code;
    Tools.ShutdownSoon();
    Console.WriteLine("shutdown: Windows shuts down in 60 s (shutdown /a stops it)");
    return code;
}
// burnsmith project FILE.burnsmith --recap [--trial [SECONDS]] : only « À savoir avant de démarrer », what the build would do, nothing built (as bd / dvd --recap)
if (a.Count >= 2 && a[0] == "project" && a.Contains("--recap")) { var recapped = Project.Load(a[1]); recapped.Recap(a.Contains("--trial") ? Trial() : recapped.Trial).ForEach(Console.WriteLine); return 0; }
if (a.Count >= 2 && a[0] == "project") return Finished(BuildProject(a[1]));
// burnsmith volumes FILE.burnsmith [--split N1,N2,...] [--out FOLDER] [--build] : preview or build manually split ordered volumes; without --split, reports size and split suggestion (+30 %).
if (a.Count >= 2 && a[0] == "volumes")
{
    Project loaded;
    try { loaded = Project.Load(a[1]); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    var splitArg = Opt("--split");
    if (splitArg == null)
    {
        if (a.Contains("--build"))
        {
            Console.Error.WriteLine("La répartition est manuelle : indiquez le nombre de vidéos par disque avec --split N1,N2,... (ex. --split 5,5).");
            return 2;
        }
        var suggest = loaded.ShouldSuggestSplit(out var estimatedGb, out var usableGb);
        var episodes = (loaded.Titles ?? []).Where(t => !t.Intro).ToList();
        Console.WriteLine(usableGb > 0
            ? $"Taille estimée avant compression : {estimatedGb:0.00} Go / {usableGb:0.#} Go utilisables ({episodes.Count} vidéos)."
            : $"Taille estimée avant compression : {estimatedGb:0.00} Go ({episodes.Count} vidéos).");
        if (suggest)
        {
            var overPct = (int)Math.Round((estimatedGb / usableGb - 1) * 100);
            var discs = Math.Clamp((int)Math.Ceiling(estimatedGb / usableGb), 2, episodes.Count);
            Console.WriteLine($"Suggestion : la saison dépasse le disque de {overPct} %. Une répartition manuelle sur {discs} disques limiterait la perte de qualité (ex. --split …).");
        }
        for (var i = 0; i < episodes.Count; i++) Console.WriteLine($"  {i + 1}. {episodes[i].DisplayName}");
        return 0;
    }
    var parts = splitArg.Split(',', StringSplitOptions.TrimEntries);
    var counts = new List<int>(parts.Length);
    foreach (var part in parts)
    {
        if (!int.TryParse(part, out var n) || n <= 0)
        {
            Console.Error.WriteLine("--split attend des entiers positifs séparés par des virgules (ex. --split 5,5).");
            return 2;
        }
        counts.Add(n);
    }
    List<(Project Disc, double EstimatedGb)> volumes;
    try { volumes = loaded.Volumes(Opt("--out"), counts); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    foreach (var (volume, estimate) in volumes)
    {
        Console.WriteLine($"{volume.Out} ({estimate:0.00} Go estimés) :");
        foreach (var episode in volume.Titles!) Console.WriteLine($"  {episode.DisplayName}");
    }
    if (!a.Contains("--build")) return 0;
    foreach (var (volume, _) in volumes)
    {
        var problems = volume.Build(null, 0, Console.WriteLine);
        if (problems.Count == 0) continue;
        foreach (var problem in problems) Console.Error.WriteLine(problem);
        return 1;
    }
    return 0;
}
// burnsmith queue A.burnsmith B.burnsmith ... [--trial [SECONDS]] : the app's « File d'attente »: the projects built one after the other, each in its own folder
//   (with its ISO when the project asks for one, nothing burnt); one that fails does not stop the next; exit 1 when any failed
if (a.Count >= 2 && a[0] == "queue")
{
    var files = a.Skip(1).Where(f => f.EndsWith(Project.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
    var failed = new List<string>();
    foreach (var (file, n) in files.Select((f, n) => (f, n + 1)))
    {
        Console.WriteLine($"queue {n}/{files.Count}: {file}");
        try { if (BuildProject(file) != 0) failed.Add(file); }
        catch (Exception e) when (e is not OperationCanceledException) { failed.Add(file); Console.Error.WriteLine($"failed: {file}: {e.Message}"); }
    }
    Console.WriteLine(failed.Count == 0 ? $"queue: {files.Count} disc(s) made" : $"queue: {files.Count - failed.Count} of {files.Count} disc(s) made, failed: {string.Join(", ", failed)}");
    return Finished(failed.Count == 0 && files.Count > 0 ? 0 : 1);
}

if (a.Count == 0 || a[0] is not ("bd" or "dvd") || videos.Count == 0 || (Opt("--out") ?? Opt("--save-project")) is not { } outDir)
{
    Console.Error.WriteLine("usage: burnsmith bd|dvd --video <file> [--trim A-B[,C-D] --volume N --chapter-at T,T|--no-chapters|--chapter-every MIN --chapter-names 'A|B' --sub-delay N=S --audio-delay N=S --audio-volume N=DB --audio-file [LANG=]FILE] [--video <file> ...] --out <folder> [--design NAME] [--menu-aspect 16:9|4:3] [--title TEXT] [--no-menu] [--background-blur 0-40] [--music FILE --music-start S --music-volume N --music-fade S --loop-seconds S|music] [--chapters MINUTES] [--audio N,N] [--sub N,N] [--sub-file FILE|LANG=FILE] [--sub-font NAME --sub-size PERCENT --sub-color RRGGBB --sub-position 0-100] [--bonus] [--credits 'A|B'] [--preview [--safe-area]] [--trial [SECONDS]] [--shutdown] [--save-project FILE] [--max-compat] [--cpu] [--fps R] [bd: --menu-mode bdj|hdmv --no-lossless --no-copy-eac3 --popup [--popup-main TEXT --popup-resume TEXT] --uhd --profile NAME --quality Q --play-mode N --label NAME --titles 'fra=Titre|eng=Title']  |  burnsmith project FILE.burnsmith [--out <folder>] [--trial [SECONDS]] [--shutdown] [--max-compat]  |  burnsmith queue A.burnsmith B.burnsmith ... [--trial [SECONDS]] [--shutdown]  |  burnsmith burn IMAGE.iso F: [--speed N|--max-compat] [--no-verify] [--eject] [--erase] | burnsmith repair-top-menu FOLDER | burnsmith scan-damage IMAGE.iso|FOLDER [--decode|--no-decode]  |  burnsmith replace-film DISC|IMAGE.iso --mkv FILM.mkv --out FOLDER [--iso OUT.iso] [--label X] [--playlist N] [--cut-start S] [--map A1=mkv:N,PG2=mkv:N] [--no-align] [--measure-only]  |  burnsmith check-film-sync  |  burnsmith label-from-scan SCAN.png|jpg OUT.png [--diameter-mm 118] [--dpi 300] [--hole-mm auto|N] [--debug OVERLAY.png]");
    return 2;
}

var design = a.Contains("--no-menu") ? null
    : Opt("--design") is { } saved && saved.EndsWith(".json", StringComparison.OrdinalIgnoreCase) // a template saved from the menu editor (Mes modèles)
        ? System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(saved)) ?? throw new InvalidOperationException($"not a template: {saved}")
    : (TemplateCatalog.Find(Opt("--design")) ?? (Opt("--design") is { } unknown ? throw new InvalidOperationException($"unknown template: {unknown}") : TemplateCatalog.DefaultFor(MainVideos()))).Clone();
// no --design: the app's rule (TemplateCatalog.DefaultFor), from the videos that are neither --bonus nor --intro (the flags written after their --video)
List<string> MainVideos()
{
    var at = a.Select((v, i) => (v, i)).Where(x => x.v == "--video").Select(x => x.i).Append(a.Count).ToList();
    return [.. videos.Where((_, k) => videos.Count == 1 || !a.Skip(at[k]).Take(at[k + 1] - at[k]).Any(f => f is "--bonus" or "--intro"))];
}
if (design != null && Opt("--design") == null) Console.WriteLine($"menu template: {design.Name} (none given: the last one chosen in the app, else one that suits the videos)");
// --menu-aspect 16:9|4:3: the version of the template for that screen, when it has both (« Autumn 16-9 » / « Autumn 4-3 »); one made for a single shape stays
if (design != null && Opt("--menu-aspect") is { } shape)
    design = shape is "4:3" or "16:9" ? TemplateCatalog.Shaped(design, shape == "4:3") is var twin && twin != design ? twin.Clone() : design
        : throw new ArgumentException($"--menu-aspect: 4:3 or 16:9 expected, not '{shape}'");
// the menu's language: --menu-language fr|en, else the design's own (French unless its file says "ButtonLanguage": "en"): its written words
// (a word of one's own in the design's file is kept) and those drawn in a template's buttons
if (design != null) design.Language(Opt("--menu-language") is not { } spoken ? design.ButtonLanguage
    : spoken is "fr" or "en" ? spoken : throw new ArgumentException($"--menu-language: fr or en expected, not '{spoken}'"));
if (design != null && Opt("--title") is { } title) design.Title = title;
if (design != null && Opt("--highlight") is { } hlStyle) design.HighlightStyle = hlStyle; // box | underline | textcolor
if (design != null && a.Contains("--list")) design.SceneList = true; // page de scènes en liste (modèles qui la décrivent)
if (design != null && a.Contains("--animated-thumbs")) design.AnimatedThumbs = true; // vignettes animées (DVD : un extrait par case)
if (design != null && a.Contains("--popup")) design.Popup = true; // menu pop-up Blu-ray (sans effet sur un DVD)
if (design != null && a.Contains("--scenes-only")) design.ScenesOnly = true; // la première page de scènes est le menu
if (design != null && a.Contains("--skip-first-scene")) design.SkipFirstScene = true; // série : pas de case pour la première vidéo
if (design != null && a.Contains("--bonus-text")) design.BonusText = true; // page Bonus en texte
if (design != null && Opt("--popup-main") is { } popupMain) design.MainMenuLabel = popupMain; // son bouton « menu principal »
if (design != null && Opt("--popup-resume") is { } popupResume) design.ResumeLabel = popupResume;
if (design != null && Opt("--credits") is { } credits) design.Credits = credits.Replace('|', '\n'); // --credits "Directed by A|Music by B"
// --page-bg KIND=PICTURE (repeatable; KIND = root | scenes | settings | credits | bonus): that kind of page gets its own background picture
foreach (var spec in OptAll("--page-bg"))
{
    var eq = spec.IndexOf('=');
    if (design == null || eq < 1 || !File.Exists(spec[(eq + 1)..])) throw new ArgumentException($"--page-bg: kind=picture expected (root=fond.jpg), with an existing picture and a menu: '{spec}'");
    design.PageBackgrounds[spec[..eq].Trim().ToLowerInvariant()] = Path.GetFullPath(spec[(eq + 1)..]);
}
if (design != null && Opt("--button-language") is { } words) // fr (default) | en: the template buttons whose word is drawn in their picture, remade in French or as the template has them
    design.ButtonLanguage = words is "fr" or "en" ? words : throw new ArgumentException($"--button-language: fr or en expected, not '{words}'");
if (design != null && int.TryParse(Opt("--background-blur"), out var blurred)) design.BackgroundBlur = blurred; // 0-40: the background picture blurred, so the text over it reads well
foreach (var spec in OptAll("--element")) design?.Elements.Add(MenuElement.Parse(spec)); // --element "kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54;align=center"
if (design != null && Opt("--titles") is { } byLanguage) design.TitlesByLanguage = byLanguage.Replace('=', ':').Replace('|', '\n'); // --titles "fra=Mon disque|eng=My disc"
if (design != null && a.Contains("--loop")) // moving menu: --loop [--loop-file video] [--loop-start S]
{
    design.Loop = true;
    design.LoopFile = Opt("--loop-file") is { } lf ? Path.GetFullPath(lf) : null;
    design.LoopStart = int.TryParse(Opt("--loop-start"), out var ls) ? ls : 0;
}
// menu music (with or without --loop): --music audio [--music-start S] [--music-volume PERCENT] [--music-fade S] [--loop-seconds S|music = how long the loop lasts, 30 by default; music = the song's whole length]
if (design != null && Opt("--loop-seconds") is { } lsecs) design.LoopSeconds = lsecs == "music" ? 0 : int.TryParse(lsecs, out var lsec) ? lsec : design.LoopSeconds;
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
// --chapter-at T,T,... (chapter times in the source video, replacing "one every N minutes") | --no-chapters | --chapter-every MIN (this video's own "one every N minutes")
var videoAt = a.Select((v, i) => (v, i)).Where(x => x.v == "--video").Select(x => x.i).ToList();
var titles = videos.Select((v, k) =>
{
    var own = videoAt.Count == 1 ? a : a.Skip(videoAt[k]).Take(k + 1 < videoAt.Count ? videoAt[k + 1] - videoAt[k] : a.Count).ToList();
    string? Own(string name) => own.IndexOf(name) is var i and >= 0 && i + 1 < own.Count ? own[i + 1] : null;
    double? Time(string text) => text.Length == 0 ? null : Timecode.Parse(text) ?? throw new ArgumentException($"not a time: '{text}'");
    // --trim A-B,C-D: several parts kept (output clips), the gaps between them left out
    var clips = Own("--trim")?.Split(',').Select(r => r.Split('-', 2)).ToList() ?? [["", ""]];
    var trim = clips[0].Length > 1 ? [clips[0][0], clips[^1][1]] : clips[0];
    List<Clip>? clipList = clips.Count > 1 ? [.. clips.Select(c => new Clip(Time(c[0]) ?? throw new ArgumentException("--trim: each clip needs its start"), (c.Length > 1 ? Time(c[1]) : null) ?? throw new ArgumentException("--trim: each clip needs its end")))] : null;
    var volume = int.TryParse(Own("--volume"), out var vol) ? Math.Clamp(vol, 10, 300) : 100;
    IReadOnlyList<double>? chapterAt = own.Contains("--no-chapters") ? [] : Own("--chapter-at")?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => Time(t.Trim()) ?? 0).ToList();
    int? chapterMinutes = int.TryParse(Own("--chapter-every"), out var every) ? Math.Clamp(every, 1, 600) : null;
    // as the app: a file with chapters of its own (an mkv's) keeps them, unless one of the chapter flags above, or --chapters for the whole disc, says otherwise
    IReadOnlyList<string?>? chapterNames = null;
    if (Own("--chapters-file") is { } chapFile)
    {
        var loaded = ChapterFiles.Load(chapFile);
        chapterAt = [.. loaded.Select(c => c.Time)];
        chapterNames = loaded.Any(c => c.Name != null) ? [.. loaded.Select(c => c.Name)] : null;
    }
    if (chapterAt == null && chapterMinutes == null && Opt("--chapters") == null) (chapterAt, chapterNames) = (BluRay.Probe(v).Chapters, BluRay.Probe(v).ChapterNames);
    // --chapter-names "Prologue|La rencontre||Final" (this video): the names the menu writes instead of "Chapitre N", in the order of --chapter-at or of the file's own chapters
    //   (the first names the chapter at 0 when the list has one; an empty name leaves "Chapitre N"). The file's own names are kept without this flag.
    if (Own("--chapter-names") is { } named)
        chapterNames = chapterAt is { Count: > 0 } ? [.. named.Split('|').Select(n => n.Trim().Length > 0 ? n.Trim() : null)]
            : throw new ArgumentException("--chapter-names names the chapters of --chapter-at, or those the file holds: this video has neither");
    // --audio-delay 0=0.2,1=-0.1 (this video): audio track N (numbered as --audio) is heard that many seconds later, or earlier when negative
    Dictionary<int, double>? audioDelay = null;
    foreach (var pair in Own("--audio-delay")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
        (audioDelay ??= [])[pair.Split('=') is [var n, var s] && int.TryParse(n, out var track) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var late)
            ? track : throw new ArgumentException($"--audio-delay: an audio track number and seconds are expected (0=0.2), not '{pair}'")] = late;
    // --sub-delay 0=1.5,2=-0.4 (this video): subtitle N (numbered as --sub) shows that many seconds later, or earlier when negative
    Dictionary<int, double>? subDelay = null;
    foreach (var pair in Own("--sub-delay")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
        (subDelay ??= [])[pair.Split('=') is [var n, var s] && int.TryParse(n, out var track) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var late)
            ? track : throw new ArgumentException($"--sub-delay: a subtitle number and seconds are expected (0=1.5), not '{pair}'")] = late;
    // --sub-file FILE | --sub-file fra=FILE (repeatable): a subtitle file kept as one more track, after the video's own and the --sub ones
    var files = own.Select((x, i) => (x, i)).Where(x => x.x == "--sub-file" && x.i + 1 < own.Count).SelectMany(x => ExternalSubtitle.ParseAll(own[x.i + 1])).ToList();
    // --sub after a --video is that video's own choice ("none": only its --sub-file ones), else the one for every video
    List<int>? subs = Own("--sub") is "none" ? [] : Own("--sub") is { } ownSubs ? [.. ownSubs.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)] : Opt("--sub") is "none" ? null : Numbers("--sub"); // another video's "none" is its own, not a number for this one
    if (subs != null || files.Count > 0) subs = SubsToKeep(subs, BluRay.Probe(v).Subtitles.Count, files.Count, Path.GetFileName(v), Console.Error.WriteLine);
    int? defaultAudio = int.TryParse(Own("--default-audio"), out var da) ? da : null, defaultSub = int.TryParse(Own("--default-sub"), out var ds) ? ds : null;
    // --audio after a --video is that video's own choice, as --sub; else the one for every video
    List<int>? audioKept = Own("--audio") is { } ownAudio ? [.. ownAudio.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)] : Numbers("--audio");
    // --audio-file FILE | --audio-file fra=FILE (repeatable, this video): a sound file (a dub found apart) kept as one more audio track, numbered after the video's own,
    //   so --audio-delay, --audio-volume and --track-lang name it by that number (the first one of a video with 2 tracks is 2)
    var audioFiles = own.Select((x, i) => (x, i)).Where(x => x.x == "--audio-file" && x.i + 1 < own.Count).Select(x =>
    {
        var spec = own[x.i + 1];
        var eq = spec.IndexOf('=');
        var lang = eq is 2 or 3 ? Languages.Code(spec[..eq]) : null;
        var file = lang == null ? spec : spec[(eq + 1)..];
        if (!File.Exists(file)) throw new ArgumentException($"--audio-file: file not found: {file}");
        var track = AudioTrack.FromFile(v, file);
        return lang == null ? track : track with { Lang = lang };
    }).ToList();
    if (audioFiles.Count > 0) // kept whatever --audio says, after the tracks it names (by default the video's first)
    {
        var videoAudio = BluRay.Probe(v).Audio;
        audioKept = [.. (audioKept ?? [.. videoAudio.Take(1).Select(t => t.Index)]).Union(audioFiles.Select((_, k) => videoAudio.Count + k))];
    }
    // --audio-volume 0=-3,2=6 (this video): audio track N (numbered as --audio) that many dB louder, or quieter when negative (-30 to 30); a track copied as it is is then made again
    Dictionary<int, double>? audioVolume = null;
    foreach (var pair in Own("--audio-volume")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
        (audioVolume ??= [])[pair.Split('=') is [var n, var s] && int.TryParse(n, out var track) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db) && Math.Abs(db) <= 30
            ? track : throw new ArgumentException($"--audio-volume: an audio track number and dB from -30 to 30 are expected (0=-3), not '{pair}'")] = db;
    if (defaultAudio is { } wanted && !(audioKept ?? [0]).Contains(wanted)) throw new ArgumentException($"--default-audio {wanted} is not one of the audio tracks kept (--audio)");
    if (defaultSub is { } wantedSub && !(subs ?? []).Contains(wantedSub)) throw new ArgumentException($"--default-sub {wantedSub} is not one of the subtitles kept (--sub / --sub-file)");
    // --track-lang a0=fre,s1=eng (this video): the language of one of the file's own tracks (a = audio, s = subtitle, numbered as --audio / --sub), for a track left "und" or mislabelled
    Dictionary<string, string>? trackLanguage = null;
    foreach (var pair in Own("--track-lang")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
    {
        var kv = pair.Split('=', 2);
        var code = kv.Length == 2 ? (kv[1].Trim() == "und" ? "und" : Languages.Code(kv[1].Trim())) : null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(kv[0].Trim(), "^[as][0-9]+$") || code == null) throw new ArgumentException($"--track-lang: a track and a language are expected (a0=fre,s1=eng), not '{pair}'");
        (trackLanguage ??= [])[kv[0].Trim()] = code;
    }
    // --name: the video's name on the menu, as renamed in the app
    return new DiscTitle(v, Own("--name"), audioKept, subs, Time(trim[0]) ?? 0, trim.Length > 1 ? Time(trim[1]) : null, volume, chapterAt, files.Count > 0 ? files : null, own.Contains("--bonus"), defaultAudio, defaultSub, chapterMinutes, own.Contains("--intro"), trackLanguage,
                          Own("--effects") is { } fx ? System.Text.Json.JsonSerializer.Deserialize<VideoEffects>(File.ReadAllText(fx)) : null, // --effects FILE.json: the video editor's settings (VideoEffects, by property name)
                          clipList,
                          Own("--thumb-at") is { } thumbAt ? Time(thumbAt) : null, Own("--thumb") is { } thumb ? Path.GetFullPath(thumb) : null, // the menu thumbnail: a moment of the video, or a picture
                          Own("--overview"), // the video's summary on the menu pages that show one (Streaming)
                          subDelay, audioDelay, chapterNames, audioFiles.Count > 0 ? audioFiles : null, audioVolume);
}).ToList();
// --reencode (never copy the video), --crf N and --preset NAME (x264 quality of a Blu-ray), --dvd-kbps N (DVD video bit rate by hand)
// --cpu (encode and decode on the processor; default: the NVIDIA card when there is one), --fps 23.976|24|25|29.97|50|59.94 (bd: the disc's rate; default: as the source)
// bd: --profile "BD25 720p" (BD50|BD25|BD9|BD5|MiniBD, 1080p/i or 720p; default BD25), --quality source|fit|15|20|30|<kb/s> (default fit = "Adapter au disque"; up to 60000 with --uhd, where --reencode = "Conversion"),
//     --capacity GB (usable size, instead of the profile's), --play-mode 0|1|2 (menu then all titles, the default | first title then all | back to the menu) [--no-rewind] (DVD too)
// dvd: --pal | --ntsc (default: as the first video), --4x3 (4:3 titles, the menu stays 16:9)
var profile = BdProfile.Find(Opt("--profile") ?? "BD25");
var quality = Opt("--quality") ?? "fit"; // as the app: "Adapter au disque"
if (quality is not ("fit" or "source") && !int.TryParse(quality, out _)) throw new ArgumentException($"--quality: source, fit or a number is expected, not '{quality}'"); // "Fit" would silently mean "source"
var rate = int.TryParse(quality, out var q) ? (q < 1000 ? q * 1000 : q) : 0; // "15" = 15 Mb/s
var encode = new EncodeOptions(a.Contains("--reencode"), int.TryParse(Opt("--crf"), out var crf) ? crf : 17, Opt("--preset") ?? "medium", int.TryParse(Opt("--dvd-kbps"), out var kbps) ? kbps : 0,
                               rate, quality == "fit", profile.Hd720, Gpu: !a.Contains("--cpu"),
                               Fps: double.TryParse(Opt("--fps"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps) ? fps : null,
                               Lossless: !a.Contains("--no-lossless"), CopyEac3: !a.Contains("--no-copy-eac3"), NormalizeAudio: a.Contains("--loudnorm"));
var capacity = double.TryParse(Opt("--capacity"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : profile.UsableGb;
var playback = int.TryParse(Opt("--play-mode"), out var mode) ? PlaybackOptions.FromMode(mode, !a.Contains("--no-rewind")) : a.Contains("--no-rewind") ? new PlaybackOptions(Rewind: false) : null;
if (int.TryParse(Opt("--auto-start"), out var autoStart))
    playback = (playback ?? new PlaybackOptions()) with { AutoStartSeconds = Math.Clamp(autoStart, 0, 300) };
if (a.Contains("--remember-position")) playback = (playback ?? new PlaybackOptions()) with { RememberPosition = true };
var hdrNits = int.TryParse(Opt("--hdr-nits"), out var nits) ? nits : 203;
if (design != null && Opt("--hdr-nits") != null) design.HdrGraphicsNits = hdrNits;
// the text/image subtitles of the disc: --sub-font NAME, --sub-size PERCENT, --sub-color RRGGBB, --sub-position 0-100, --hdr-nits N
var subtitleLook = Opt("--sub-font") == null && Opt("--sub-size") == null && Opt("--sub-color") == null && Opt("--sub-position") == null && Opt("--hdr-nits") == null ? null
    : new SubtitleStyle(Opt("--sub-font"), int.TryParse(Opt("--sub-size"), out var subSize) ? subSize : 100, Opt("--sub-color")?.TrimStart('#') ?? "FFFFFF", int.TryParse(Opt("--sub-position"), out var subPosition) ? subPosition : 0, hdrNits);

// --preview: only the menu pages, as JPEGs in --out (menu_root.jpg...), nothing encoded: to check a menu before the hours of a build; --safe-area draws the action-safe (93 %) and title-safe (90 %) frames on them
if (a.Contains("--preview"))
{
    BluRay.PreviewMenu(new BluRayJob(titles, Path.GetFullPath(outDir), design, a.Contains("--uhd"), Opt("--label") ?? "BURNSMITH", chapters, encode, capacity, playback), Path.GetFullPath(outDir), Console.WriteLine, a.Contains("--safe-area"));
    return 0;
}
var dvdDisc = a[0] == "dvd";
if (dvdDisc) (encode, capacity) = (encode with { DvdPal = a.Contains("--pal") ? true : a.Contains("--ntsc") ? false : null, DvdWide = !a.Contains("--4x3") }, Opt("--capacity") != null ? capacity : 4.2); // --capacity 7.6: a DVD-9
var discKind = dvdDisc ? "dvd" : a.Contains("--uhd") ? "uhd" : "bd";
// --recap: only « À savoir avant de démarrer », in everyday French (videos copied or converted and about how long, sound remade, how full the disc is, what plays at the start); nothing built
if (a.Contains("--recap"))
{
    var recapTitles = Project.OnDisc(titles, chapters, Trial(), BluRay.Probe);
    (dvdDisc ? Recap.For(new DvdJob(recapTitles, "", design, chapters, capacity, encode, playback, subtitleLook))
             : Recap.For(new BluRayJob(recapTitles, "", design, discKind == "uhd", "X", chapters, encode, capacity, playback, subtitleLook, Hdmv(), a.Contains("--avchd")))).ForEach(Console.WriteLine);
    return 0;
}
// --save-project FILE.burnsmith: nothing is built; the disc these flags describe is written as a project the app opens (« Ouvrir un projet ») and `burnsmith project` builds
if (Opt("--save-project") is { } projectFile)
{
    new Project(discKind, titles, design, design != null, Opt("--label"), chapters, encode, playback, subtitleLook, null, Opt("--capacity") != null && capacity <= 0 ? -1 : capacity, Opt("--out") is { } kept ? Path.GetFullPath(kept) : null, false, Trial(), Hdmv(), MaxCompat: a.Contains("--max-compat"), Avchd: a.Contains("--avchd")).Save(projectFile);
    Console.WriteLine("ok   " + Path.GetFullPath(projectFile));
    return 0;
}
// the disc the folder must fit: the profile's, or the one --capacity names by its usable size (a BD-100 for --uhd, a DVD-9)
// --capacity 0: no size limit (a folder or an image for a hard disk): nothing is fitted to a disc and the size is not checked
return Finished(Make(discKind, titles, Path.GetFullPath(outDir), design, Opt("--label") ?? "BURNSMITH", chapters, encode, capacity, playback, subtitleLook, Trial(),
            Opt("--capacity") != null && capacity <= 0 ? 0 : Opt("--capacity") != null ? capacity / 0.89 : dvdDisc ? 4.7 : profile.Gb));
