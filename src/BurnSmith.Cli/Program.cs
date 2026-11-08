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
var a = args.ToList();
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
    if (a.Contains("--list")) chosen.SceneList = true; // la page de scènes en liste, quand le modèle la décrit
    if (Opt("--credits") is { } sampleCredits) chosen.Credits = sampleCredits.Replace('|', '\n');
    foreach (var spec in OptAll("--element")) chosen.Elements.Add(MenuElement.Parse(spec));
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

// burnsmith check-disc <disc folder | .iso [folder it was made from]> : the structure of a finished disc (files present, headers, whole packets, IFO sizes), without a player; 1 when something is wrong
if (a.Count >= 2 && a[0] == "check-disc")
{
    var found = Directory.Exists(a[1]) ? DiscCheck.Folder(a[1]) : DiscCheck.Image(a[1], a.Count > 2 ? a[2] : null);
    Console.WriteLine(found.Count == 0 ? "ok   disc structure" : string.Join(Environment.NewLine, found.Select(p => "FAIL " + p)));
    return found.Count == 0 ? 0 : 1;
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
        var pictures = a.Skip(2).Where((x, i) => !x.StartsWith("--") && !(i > 0 && a[i + 1] is "--seconds" or "--music")).ToList();
        MediaMaker.Slideshow(pictures.Select(Path.GetFullPath).ToList(), double.TryParse(Opt("--seconds"), System.Globalization.CultureInfo.InvariantCulture, out var each) ? each : 5, music, made, Console.WriteLine);
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
// burnsmith check-templates [--matrix] [--only NAME] [--picture frame.jpg] (--only: the templates whose name contains NAME, no composed designs) : builds every template as film and series and checks pages, buttons, remote moves, pictures;
// --matrix = every disc shape (1..40 chapters, 2..60 episodes, 1..9 audio, 0..9 subtitles) and 200 designs the menu editor can compose
if (a.Count >= 1 && a[0] == "check-templates") return TemplateCheck.Run(Opt("--picture") is { } cp ? Path.GetFullPath(cp) : null, a.Contains("--matrix"), Opt("--only"));

// burnsmith check-highlights : les surbrillances DVD prises aux modèles (image de survol du modèle, PNG 2 bits à 4 couleurs, repli sur le cadre générique)
if (a.Count >= 1 && a[0] == "check-highlights")
{
    var hlFailed = 0;
    void Check(string what, bool ok) { if (!ok) hlFailed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
    static bool Has(byte[] hay, string ascii)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(ascii);
        for (var i = 0; i + needle.Length <= hay.Length; i++) if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    var hlContent = new MenuContent(1, null, Enumerable.Repeat<string?>(null, 4).ToList(), null, ["Français AC-3 mono", "English AC-3 mono"], ["Français"]);

    // (a) an installed DVD template names its highlighted artwork for each button that has one
    var hlModel = TemplateCatalog.Scan().Usable.Select(f => f.Design).FirstOrDefault(d => d.TemplateDir != null && d.Kind == TemplateKind.Dvd);
    if (hlModel == null) Console.WriteLine("ok   (aucun modèle DVD installé : la vérification du modèle est sautée)");
    else
    {
        var hlPages = MenuBuilder.Build(hlModel, hlContent);
        var hlButtons = hlPages.SelectMany(p => p.Buttons).Where(b => b.DvdHighlightImage != null).ToList();
        Check("le modèle nomme l'image de survol de ses boutons, et le fichier existe", hlButtons.Count > 0 && hlButtons.All(b => File.Exists(b.DvdHighlightImage!)));
        Check("le masque de forme vient du jumeau « .mask. » du modèle", hlButtons.Any(b => b.DvdHighlightMask is { } m && File.Exists(m)));
        var hlOne = hlButtons.First();
        var (hlPx, hlW, hlH) = MenuRenderer.Sta(() => MenuRenderer.Raster(hlOne.DvdHighlightImage!, hlOne.DvdHighlightMask, 120, 80));
        var hlOpaque = hlPx.Where((_, i) => i % 4 == 3).Count(alpha => alpha >= 128);
        Check("le raster a la taille du bouton et des pixels visibles (c'est ce que le lecteur affichera)",
              hlW == 120 && hlH == 80 && hlOpaque > 0);
        // l'état choisi vient du modèle : son propre marqueur (selection/0.png), pas la silhouette du survol
        var hlSel = hlButtons.Select(b => b.DvdSelectImage).FirstOrDefault(i => i != null);
        Check("le modèle nomme l'image de l'état choisi, et son marqueur donne une forme visible", hlSel != null && File.Exists(hlSel));

        // --out DOSSIER : écrit les deux images de page (survol et choisi) telles que le surlignage DVD les reçoit, pour les regarder
        if (Opt("--out") is { } hlDir)
        {
            Directory.CreateDirectory(hlDir);
            var (hw, hh) = (720, 576);
            double psx = hw / (double)MenuBuilder.W, psy = hh / (double)MenuBuilder.H;
            var hovered = new IndexedPng.Canvas(hw, hh);
            var chosen = new IndexedPng.Canvas(hw, hh);
            var page = hlPages.First(q => q.Buttons.Any(b => b.DvdHighlightImage != null));
            var shots = MenuRenderer.Sta(() => page.Buttons.Select(b =>
            {
                int bw = (int)Math.Round(b.Rect.W * psx), bh = (int)Math.Round(b.Rect.H * psy);
                (byte[] Px, int W, int H)? At(string? image, string? mask) => image == null ? null : MenuRenderer.Raster(image, mask, bw, bh);
                (byte[] Px, int W, int H)? Marker(string? image)
                {
                    if (image == null) return null;
                    var natural = MenuRenderer.Size(image) is var (iw, ih) ? ((int)Math.Round(iw * psx), (int)Math.Round(ih * psy)) : (bw, bh);
                    return MenuRenderer.Raster(image, null, Math.Min(bw, Math.Max(1, natural.Item1)), Math.Min(bh, Math.Max(1, natural.Item2)));
                }
                return (Hl: At(b.DvdHighlightImage, b.DvdHighlightMask), Sel: Marker(b.DvdSelectImage), b.Rect);
            }).ToList());
            foreach (var s in shots)
            {
                if (s.Hl is not { } hover) continue;
                int x0 = (int)(s.Rect.X * psx) & ~1, y0 = (int)(s.Rect.Y * psy) & ~1;
                hovered.Stamp(hover.Px, hover.W, hover.H, x0, y0, 1);
                var c = s.Sel ?? hover;
                var at = s.Sel != null ? (x0 + (hover.W - c.W) / 2, y0 + (hover.H - c.H) / 2) : (x0, y0);
                chosen.Stamp(c.Px, c.W, c.H, at.Item1, at.Item2, 2);
            }
            var tint = shots.Select(s => s.Hl).FirstOrDefault(x => x != null) is { } f ? IndexedPng.Average(f.Px, f.W, f.H) : new IndexedPng.Rgba(255, 255, 255, 255);
            IndexedPng.Rgba[] pal = [new(0, 0, 0, 0), tint, new(255, 255, 255, 255), new(0, 0, 0, 0)];
            File.WriteAllBytes(Path.Combine(hlDir, $"hl_{page.Id}.png"), IndexedPng.Encode(hovered, pal));
            File.WriteAllBytes(Path.Combine(hlDir, $"sel_{page.Id}.png"), IndexedPng.Encode(chosen, pal));
            Console.WriteLine($"écrit {hlDir}\\hl_{page.Id}.png et sel_{page.Id}.png (page {page.Id}, {page.Buttons.Count} boutons)");

        }
        var hlPage = new IndexedPng.Canvas(720, 576);
        hlPage.Stamp(hlPx, hlW, hlH, 200, 200, 1);
        Check($"collée sur la page, la forme du modèle est reprise telle quelle ({hlOpaque} pixels) et rien n'est ajouté ailleurs",
              hlPage.Pixels.Count(v => v == 1) == hlOpaque && hlPage.Pixels.Count(v => v != 0) == hlOpaque);
    }

    // (b) the encoder: a 2-bit indexed PNG, four colours, index 0 transparent
    var hlCanvas = new IndexedPng.Canvas(16, 8);
    var hlArt = Enumerable.Range(0, 4 * 2 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : 0)).ToArray();
    hlCanvas.Stamp(hlArt, 4, 2, 4, 3, 1);
    var hlPng = IndexedPng.Encode(hlCanvas, [new(0, 0, 0, 0), new(200, 40, 40, 255), new(255, 255, 255, 255), new(0, 0, 0, 0)]);
    Check("la surbrillance est un PNG indexé 2 bits à quatre couleurs, avec transparence",
          hlPng.Length > 26 && hlPng[24] == 2 && hlPng[25] == 3 && Has(hlPng, "tRNS") && Has(hlPng, "PLTE"));
    Check("l'artwork est tamponné à sa place, le reste de la page reste transparent",
          hlCanvas.Pixels[3 * 16 + 4] == 1 && hlCanvas.Pixels[3 * 16 + 7] == 1 && hlCanvas.Pixels[3 * 16 + 8] == 0 && hlCanvas.Pixels[0] == 0);

    // (c) a design without a model has no artwork at all: Dvd.cs keeps its four-colour frame there
    var hlPlain = TemplateCatalog.Find("Nuit d'encre") ?? new MenuDesign();
    Check("un modèle sans artwork garde le cadre à quatre couleurs (aucune image de surbrillance nommée)",
          hlPlain.TemplateDir == null && MenuBuilder.Build(hlPlain, hlContent).All(p => p.Buttons.All(b => b.DvdHighlightImage == null)));

    // the three highlight styles: a box, an underline, or the label in colour
    var hlPageRoot = MenuBuilder.Build(hlPlain, hlContent).First(q => q.Id == "root");
    var hlBtn = hlPageRoot.Buttons.First(b => b.Id == "play");
    var hlName = hlPageRoot.Ops.OfType<Label>().First(l => l.Rect == hlBtn.Rect).Value;
    var hlShots = new[] { "box", "underline", "textcolor" }.ToDictionary(st => st, st =>
    {
        var styled = hlPlain.Clone();
        styled.HighlightStyle = st;
        return MenuRenderer.Sta(() => MenuRenderer.Focus(styled, hlBtn, 1, hlName));
    });
    Check("trois styles de surbrillance distincts, tous dessinés (cadre, soulignement, texte en couleur)",
          hlShots.Values.All(v => v.Length > 0) && hlShots.Values.Select(v => v.Length).Distinct().Count() == 3);
    Check("le style « texte en couleur » ne dessine pas de cadre : son image est plus légère que celle du cadre",
          hlShots["textcolor"].Length < hlShots["box"].Length);

    // a selection colour of its own, and the fill transparency
    var hlInk = hlPlain.Clone();
    hlInk.SelectionColor = "#00FF00";
    hlInk.HighlightStyle = "underline";
    var hlInkShot = MenuRenderer.Sta(() => MenuRenderer.Focus(hlInk, hlBtn, 1, hlName));
    Check("une couleur de sélection distincte change l'image (le soulignement n'a plus la teinte d'accent)",
          !hlInkShot.SequenceEqual(hlShots["underline"]) && hlInk.SelectionInk == "#00FF00");
    var hlFaint = hlPlain.Clone();
    hlFaint.SelectionOpacity = 0;
    var hlFaintShot = MenuRenderer.Sta(() => MenuRenderer.Focus(hlFaint, hlBtn, 1, hlName));
    Check("une transparence de sélection différente change l'image (0 % ne remplit plus le cadre)",
          !hlFaintShot.SequenceEqual(hlShots["box"]));
    if (Opt("--out") is { } styleDir)
    {
        Directory.CreateDirectory(styleDir);
        foreach (var (st, png) in hlShots) File.WriteAllBytes(Path.Combine(styleDir, $"style_{st}.png"), png);
        File.WriteAllBytes(Path.Combine(styleDir, "style_custom.png"), hlInkShot);
        File.WriteAllBytes(Path.Combine(styleDir, "style_faint.png"), hlFaintShot);
        Console.WriteLine("écrit style_box.png, style_underline.png et style_textcolor.png dans " + styleDir);
    }

    // le menu Blu-ray reçoit bien l'image du style choisi (et le libellé du bouton pour « texte en couleur »)
    var hlBdj = Path.Combine(Path.GetTempPath(), "bs_hl_" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var styled = hlPlain.Clone();
        styled.HighlightStyle = "textcolor";
        MenuRenderer.Sta(() => { MenuFiles.WriteBdj(styled, [hlPageRoot], hlBdj); return 0; });
        var focus = Path.Combine(hlBdj, $"hl_root_play.png");
        Check("le menu Blu-ray reçoit l'image du style choisi, libellé compris", File.Exists(focus) && new FileInfo(focus).Length > 0
              && !File.ReadAllBytes(focus).SequenceEqual(hlShots["box"]));
    }
    finally { try { Directory.Delete(hlBdj, true); } catch (IOException) { } }

    Console.WriteLine(hlFailed == 0 ? "all ok" : $"{hlFailed} failed");
    return hlFailed == 0 ? 0 : 1;
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
if (a.Count >= 1 && a[0] == "check-elements")
{
    var elFailed = 0;
    void Check(string what, bool ok) { if (!ok) elFailed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
    static bool Throws(Action work) { try { work(); return false; } catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException) { return true; } }

    var elText = MenuElement.Parse("kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54;color=#FFE6AE;align=center;bold=1");
    Check("text element: kind, rectangle, text, size, colour, alignment, bold",
          elText.Kind == "text" && elText.Box() == new Box(100, 120, 900, 90) && elText.Text == "Bienvenue" && elText.Size == 54 && elText.Color == "#FFE6AE" && elText.Align == "center" && elText.Bold);
    Check("geometry that leaves the page is pulled back inside it", new MenuElement { X = -50, Y = 2000, W = 5000, H = 10 }.Box() == new Box(0, 1070, 1920, 10));
    Check("page targeting: all, root, scenes, bonus; a misspelt page hits nothing",
          new MenuElement { Page = "all" }.AppliesTo("settings") && new MenuElement { Page = "root" }.AppliesTo("root") && !new MenuElement { Page = "root" }.AppliesTo("scenes1")
          && new MenuElement { Page = "scenes" }.AppliesTo("scenes2") && !new MenuElement { Page = "scenes" }.AppliesTo("bonus1") && !new MenuElement { Page = "roots" }.AppliesTo("root"));
    Check("an unknown kind is refused, so a misspelt element is not silently something else", Throws(() => MenuElement.Parse("kind=vidéo;text=x")));

    // the zones the editor offers: what each template draws, nothing more; only what the user wrote reaches the page
    var zoneSeries = new MenuContent(1, null, [], [new TitleEntry("Un", 1, null), new TitleEntry("Deux", 2, null)]);
    var zonePlain = MenuBuilder.Build(new MenuDesign(), zoneSeries);
    Check("BurnSmith's own layout: the title on the menu, the videos' names on their page, no text zone",
          zonePlain[0].Zones.Any(z => z.Key == "title") && zonePlain.First(p => p.Id == "scenes1").Zones.Select(z => z.Key).SequenceEqual(["name:1", "name:2"]) && !zonePlain.SelectMany(p => p.Zones).Any(z => z.Key.StartsWith("slot:")));
    if (TemplateCatalog.Find("Streaming") is { } streaming && TemplateCatalog.Find("Birthday1") is { } noZones)
    {
        var zd = streaming.Clone();
        var blank = MenuBuilder.Build(zd, zoneSeries)[0];
        Check("Streaming declares its information line and four text zones", blank.Zones.Count(z => z.Key.StartsWith("slot:")) == 5 && blank.Zones.Any(z => z.Key == "poster"));
        Check("an empty text zone draws nothing; the information line says what the disc holds", blank.Ops.OfType<Text>().Count(t => t.Rect.X == 536 && t.Rect.Y >= 298) == 0 && blank.Ops.OfType<Text>().Any(t => t.Value.StartsWith("2 ")));
        zd.TextZones["overview1"] = "2018 | Action";
        zd.TextZones["overview4"] = "Trente ans après";
        var filled = MenuBuilder.Build(zd, zoneSeries)[0].Ops.OfType<Text>().ToList();
        Check("what the user writes is drawn in the template's zone, on several lines where it allows them",
              filled.Any(t => t.Value == "2018 | Action" && !t.Wrap && t.Rect.Y == 298) && filled.Any(t => t.Value == "Trente ans après" && t.Wrap && t.Rect.Y == 488));
        Check("a template that declares no text zone offers none", !MenuBuilder.Build(noZones.Clone(), zoneSeries).SelectMany(p => p.Zones).Any(z => z.Key.StartsWith("slot:")));
    }

    var elDesign = TemplateCatalog.Default().Clone();
    var elContent = new MenuContent(1, null, ["a", "b", "c"], null, ["Français", "English"], ["Français"]);
    var elBefore = MenuBuilder.Build(elDesign, elContent);
    elDesign.Elements.Add(MenuElement.Parse("kind=text;page=root;rect=10,10,400,60;text=Salut;size=40"));
    elDesign.Elements.Add(MenuElement.Parse("kind=box;page=scenes;rect=0,700,1920,380;fill=#000000;opacity=50;border=#FFC83D;borderw=4"));
    elDesign.Elements.Add(MenuElement.Parse("kind=text;page=credits;rect=10,10,400,60;text=Fin"));
    elDesign.Elements.Add(MenuElement.Parse("kind=image;page=all;rect=10,10,100,100;file=Z:\\absent\\logo.png"));
    var elPages = MenuBuilder.Build(elDesign, elContent);

    Check("elements change the drawing, not the pages", elPages.Select(p => p.Id).SequenceEqual(elBefore.Select(p => p.Id)));
    Check("a text on the root page is drawn there and nowhere else",
          elPages.First(p => p.Id == "root").Ops.OfType<Text>().Any(t => t.Value == "Salut") && !elPages.First(p => p.Id == "scenes1").Ops.OfType<Text>().Any(t => t.Value == "Salut"));
    var elGrids = elPages.Where(p => p.Id.StartsWith("scenes")).ToList();
    Check("a box on the scenes pages: one fill and one border on each, in its own colours",
          elGrids.Count > 0 && elGrids.All(p => p.Ops.OfType<Fill>().Count(f => f.Color == "#000000" && Math.Abs(f.Opacity - 0.5) < 1e-9) == 1
                                         && p.Ops.OfType<Border>().Count(b => b.Color == "#FFC83D" && b.Width == 4) == 1));
    Check("an element for a page the disc has not got is simply not drawn", !elPages.Any(p => p.Ops.OfType<Text>().Any(t => t.Value == "Fin")));
    Check("an image whose file is missing draws nothing (the page still builds)", !elPages.Any(p => p.Ops.OfType<Image>().Any(i => i.Path.Contains("logo.png"))));
    Check("the elements are copied with the design, not shared",
          elDesign.Clone().Elements.Count == elDesign.Elements.Count && !ReferenceEquals(elDesign.Clone().Elements[0], elDesign.Elements[0]));

    var elUnder = elDesign.Clone();
    elUnder.Elements.Add(MenuElement.Parse("kind=box;page=root;rect=0,700,1920,380;fill=#123456;opacity=100;behind=1"));
    var elOver = elDesign.Clone();
    elOver.Elements.Add(MenuElement.Parse("kind=box;page=root;rect=0,700,1920,380;fill=#123456;opacity=100"));
    static int Panel(MenuDesign dd, MenuContent cc) => MenuBuilder.Build(dd, cc).First(p => p.Id == "root").Ops.FindIndex(o => o is Fill { Color: "#123456" });
    Check("a box told to stay behind the buttons is drawn before them, otherwise over them", Panel(elUnder, elContent) < Panel(elOver, elContent));

    // a hand-placed button is a real menu button, not a drawing
    var elButton = elDesign.Clone();
    elButton.Elements.Add(MenuElement.Parse("kind=button;page=root;rect=120,600,300,100;text=Lire;target=movie"));
    var elButtonPage = MenuBuilder.Build(elButton, elContent).First(p => p.Id == "root");
    var elButtonOne = elButtonPage.Buttons.Where(b => b.Id.StartsWith("u")).ToList();
    Check("un élément bouton devient un vrai bouton du menu, avec son libellé et l'action choisie",
          elButtonOne.Count == 1 && elButtonOne[0].Action.Kind == "play" && elButtonPage.Ops.OfType<Label>().Any(l => l.Value == "Lire"));

    elButton.Elements.Add(MenuElement.Parse("kind=button;page=root;rect=520,600,300,100;text=Scènes;target=scenes1"));
    var elTwoPage = MenuBuilder.Build(elButton, elContent).First(p => p.Id == "root");
    var elTwo = elTwoPage.Buttons.Where(b => b.Id.StartsWith("u")).ToList();
    Check("deux boutons posés côte à côte se pointent l'un l'autre à la télécommande, et la page visée est celle demandée",
          elTwo.Count == 2 && elTwo[0].Right == elTwo[1].Id && elTwo[1].Left == elTwo[0].Id && elTwo[1].Action is { Kind: "page", Page: "scenes1" });

    // what "Enregistrer le modèle" writes must come back whole: the elements and the hand-placed buttons are part of the template
    var elJson = System.Text.Json.JsonSerializer.Serialize(elButton);
    var elBack = System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(elJson)!;
    Check("un modèle enregistré revient entier (éléments, bouton ajouté et sa cible)",
          elBack.Elements.Count == elButton.Elements.Count && elBack.Elements[^1] is { Kind: "button", Target: "scenes1", Text: "Scènes" }
          && elBack.Elements[0].X == elButton.Elements[0].X && elBack.Title == elButton.Title);

    // la page de scènes en liste : les lignes se posent aux rectangles que le modèle donne
    var theme = TemplateCatalog.Scan().Usable.Select(f => f.Design).FirstOrDefault(x => x.Kind == TemplateKind.Theme);
    if (theme == null) Console.WriteLine("ok   (aucun thème installé : vérification du mode liste sautée)");
    else
    {
        var ten = new MenuContent(1, null, [], Enumerable.Range(1, 10).Select(i => new TitleEntry($"Episode {i}", i, null)).ToList(), ["Français AC-3"], []);
        var grid = MenuBuilder.Build(theme.Clone(), ten).First(q => q.Id == "scenes1");
        var listed = theme.Clone();
        listed.SceneList = true;
        var listPage = MenuBuilder.Build(listed, ten).First(q => q.Id == "scenes1");
        Check("le mode liste tient dix lignes là où la grille n'en tient que quelques-unes, aux positions du thème",
              listPage.Buttons.Count(b => b.Id.StartsWith("e")) == 10 && grid.Buttons.Count(b => b.Id.StartsWith("e")) < 10
              && listPage.Buttons.Where(b => b.Id.StartsWith("e")).Select(b => b.Rect.X).Distinct().Count() == 1);
    }

    // a picture for one kind of page (the scenes pages): those are drawn from it, the root page keeps the design's own background; copies and saved models keep it
    var pbPng = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "burnsmith-pagebg.png"));
    var pbPx = new byte[16 * 9 * 4];
    for (var i = 0; i < pbPx.Length; i += 4) { pbPx[i + 2] = 255; pbPx[i + 3] = 255; } // opaque red (BGRA)
    var pbEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
    pbEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(16, 9, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pbPx, 64)));
    using (var pbFile = File.Create(pbPng)) pbEnc.Save(pbFile);
    var pbDesign = new MenuDesign();
    pbDesign.PageBackgrounds["scenes"] = pbPng;
    (int R, int B) Corner(MenuPage page) => MenuRenderer.Sta(() =>
    {
        var jpeg = new System.Windows.Media.Imaging.JpegBitmapDecoder(new MemoryStream(MenuRenderer.Background(pbDesign, page)), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var px = new byte[4];
        new System.Windows.Media.Imaging.FormatConvertedBitmap(jpeg.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(3, 3, 1, 1), px, 4, 0);
        return (px[2], px[0]);
    });
    var pbPages = MenuBuilder.Build(pbDesign, new MenuContent(1, null, Enumerable.Repeat<string?>(null, 3).ToList(), null, ["Français", "English"], ["Français"])); // no thumbnails: nothing to load but the backgrounds
    var (pbRoot, pbScenes) = (Corner(pbPages.First(p => p.Id == "root")), Corner(pbPages.First(p => p.Id == "scenes1")));
    Check("a page with its own background is drawn from it (red), the root page keeps the gradient (blue)", pbScenes.R > 120 && pbScenes.B < 60 && pbRoot.B > pbRoot.R);
    Check("the kind of a page is its id without the number or the choice", MenuDesign.PageKind("scenes12") == "scenes" && MenuDesign.PageKind("settings_1_2") == "settings" && MenuDesign.PageKind("root") == "root");
    Check("a missing picture is no background (the page still builds)", new MenuDesign { PageBackgrounds = { ["root"] = "Z:\absent.png" } }.PageBackground("root") == null);
    var pbCopy = pbDesign.Clone();
    pbCopy.PageBackgrounds.Remove("scenes");
    Check("the page backgrounds are copied with the design, not shared", pbDesign.PageBackgrounds.Count == 1 && pbCopy.PageBackgrounds.Count == 0);
    Check("a saved model keeps its page backgrounds", System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(System.Text.Json.JsonSerializer.Serialize(pbDesign))!.PageBackgrounds["scenes"] == pbPng);

    // a button drawn from a library picture, selected with its own picture (any two library pictures stand in for a button and its "sub" twin)
    var elShown = ElementLibrary.Backgrounds().Where(x => x.Source == ElementLibrary.Open).Take(2).ToList();
    Check("la bibliothèque livre des images pour les essais qui suivent", elShown.Count == 2);
    if (elShown.Count == 2)
    {
        string normalPic = elShown[0].Path, focusPic = elShown[1].Path;
        var elPic = elDesign.Clone();
        elPic.Elements.Add(MenuElement.Parse($"kind=button;page=root;rect=120,600,300,100;text=Lire;target=movie;file={normalPic};focus={focusPic}"));
        elPic.Elements.Add(MenuElement.Parse($"kind=button;page=root;rect=520,600,300,100;target=scenes1;file={normalPic}"));
        elPic.Elements.Add(MenuElement.Parse($"kind=button;page=root;rect=920,600,300,100;text=Absent;file=Z:\\absent\\bouton.png;focus={focusPic}"));
        var elPicPage = MenuBuilder.Build(elPic, elContent).First(p => p.Id == "root");
        var elPics = elPicPage.Buttons.Where(b => b.Id.StartsWith("u")).ToList();
        Check("un bouton image : son image occupe son rectangle et son libellé est écrit dessus",
              elPicPage.Ops.OfType<Image>().Any(i => i.Path == normalPic && i.Rect == new Box(120, 600, 300, 100)) && elPicPage.Ops.OfType<Label>().Any(l => l.Value == "Lire"));
        Check("son image « sub » devient l'image de sélection Blu-ray et la surbrillance DVD",
              elPics.Count == 3 && elPics[0].FocusImage == focusPic && elPics[0].DvdHighlightImage == focusPic);
        Check("sans image de sélection : le cadre du thème (rien n'est inventé)", elPics[1].FocusImage == null && elPics[1].DvdHighlightImage == null);
        Check("une image de bouton absente : le bouton garde le look du thème et ne vise pas une image de sélection absente",
              elPics[2].FocusImage == null && elPicPage.Ops.OfType<Label>().Any(l => l.Value == "Absent") && !elPicPage.Ops.OfType<Image>().Any(i => i.Path.Contains("bouton.png")));

        // its own look, without touching the design's
        var elStyled = elDesign.Clone();
        elStyled.ButtonStyle = "Text";
        elStyled.Elements.Add(MenuElement.Parse("kind=button;page=root;rect=120,600,300,100;text=Carte;look=Card"));
        elStyled.Elements.Add(MenuElement.Parse("kind=button;page=root;rect=520,600,300,100;text=Nu;look=Inconnu"));
        var elStyledOps = MenuBuilder.Build(elStyled, elContent).First(p => p.Id == "root").Ops;
        Check("un bouton peut avoir son look (Cartes) alors que le thème est en « Texte seul » ; un look inconnu est ignoré",
              elStyledOps.OfType<Fill>().Any(f => f.Rect == new Box(120, 600, 300, 100) && f.Color == elStyled.CardFill) && !elStyledOps.OfType<Fill>().Any(f => f.Rect == new Box(520, 600, 300, 100)) && elStyled.ButtonStyle == "Text");

        var elPicBack = System.Text.Json.JsonSerializer.Deserialize<MenuDesign>(System.Text.Json.JsonSerializer.Serialize(elPic))!;
        Check("un modèle enregistré garde l'image du bouton et celle de sa sélection", elPicBack.Elements[^3] is { Image: { } i0, Focus: { } f0 } && i0 == normalPic && f0 == focusPic);

        // the button library: BurnSmith's looks first, the user's imports, the template pictures; imports are copied and can be removed
        var elButtons = ElementLibrary.Buttons();
        Check("la bibliothèque de boutons commence par les looks de BurnSmith", elButtons.Take(ElementLibrary.ButtonStyles.Length).All(b => b.Source == ElementLibrary.Styles && b.Style != null && b.Normal == null));
        Check("chaque bouton image existe, et sa sélection aussi", elButtons.Where(b => b.Style == null).All(b => b.Normal != null && File.Exists(b.Normal) && (b.Focus == null || File.Exists(b.Focus))));
        Check("seuls les boutons de l'utilisateur peuvent être retirés", elButtons.Where(b => b.Removable).All(b => b.Source == ElementLibrary.MyButtons));
        var imported = ElementLibrary.ImportButton(normalPic, focusPic);
        try
        {
            var listed = ElementLibrary.Buttons().FirstOrDefault(b => b.Normal == imported.Normal);
            Check("un bouton importé est copié avec sa sélection, et listé parmi les boutons de l'utilisateur",
                  listed is { Removable: true, Source: ElementLibrary.MyButtons } && listed.Focus == imported.Focus && imported.Normal != normalPic && File.Exists(imported.Normal!) && File.Exists(imported.Focus!));
        }
        finally { ElementLibrary.RemoveButton(imported); }
        Check("retirer un bouton importé supprime l'image et sa sélection", !File.Exists(imported.Normal!) && !File.Exists(imported.Focus!));
        Check("un bouton de modèle n'est pas à la bibliothèque de le supprimer", Throws(() => ElementLibrary.RemoveButton(new ElementLibrary.ButtonItem("x", "Thème", normalPic))));
        Check("un fichier qui n'est pas une image est refusé à l'import", Throws(() => ElementLibrary.ImportButton(Path.GetFullPath("check-elements.txt"))));
    }

    // what a design stores for a picture that travels in the exe works on any PC; a plain path is left alone
    Check("une référence « lib: » revient au même fichier", ElementLibrary.Backgrounds().FirstOrDefault(x => x.Source == ElementLibrary.Open) is { } libPic && ElementLibrary.Resolve(ElementLibrary.Reference(libPic.Path)) == libPic.Path && ElementLibrary.Reference(libPic.Path).StartsWith("lib:"));
    Check("une référence « carried: » se range sous le dossier des images des modèles, ou ne mène à rien quand l'exe n'en porte pas",
          Tools.TemplatesDir() is { } carriedRoot ? ElementLibrary.Resolve("carried:x/y.png") == Path.Combine(carriedRoot, "x", "y.png") : ElementLibrary.Resolve("carried:x/y.png") == null);
    Check("un chemin ordinaire reste tel quel", ElementLibrary.Resolve(@"C:\a\b.png") == @"C:\a\b.png" && ElementLibrary.Resolve(null) == null);

    // DVDStyler's SVG buttons: each is drawn at two sizes (wide and square), shows something, and its selected state differs
    var svgButtons = ElementLibrary.Buttons().Where(b => b.Source == ElementLibrary.DvdStyler).ToList();
    var svgBad = MenuRenderer.Sta(() => svgButtons.Where(b =>
    {
        static bool Inked((byte[] Px, int W, int H) r) => Enumerable.Range(0, r.W * r.H).Any(i => r.Px[i * 4 + 3] > 0);
        try
        {
            var wide = MenuRenderer.Raster(b.Normal!, null, 300, 90);
            var square = MenuRenderer.Raster(b.Normal!, null, 120, 120);
            var focus = b.Focus == null ? null : MenuRenderer.Raster(b.Focus, null, 300, 90).Px;
            // a text button (text-circle…) is only its label until chosen: then its mark must show
            var shows = Inked(wide) && Inked(square) || focus != null && Inked((focus, 300, 90));
            return !shows || (focus != null && focus.SequenceEqual(wide.Px));
        }
        catch (InvalidDataException) { return true; }
    }).Select(b => b.Name).ToList());
    Check($"les {svgButtons.Count} boutons SVG de DVDStyler se dessinent à toute taille, avec leur état choisi{(svgBad.Count > 0 ? " — en défaut : " + string.Join(", ", svgBad) : "")}",
          svgButtons.Count >= 50 && svgBad.Count == 0);
    SettingsCheck.Model(Check);
    Console.WriteLine(elFailed == 0 ? "all ok" : $"{elFailed} failed");
    return elFailed == 0 ? 0 : 1;
}

// burnsmith check-update [--live] : which release tags count as newer (a same version never does), the refusals that protect a release, where tools are looked for; --live also asks GitHub for the newest release, which must carry a digest
if (a.Count >= 1 && a[0] == "check-update")
{
    var upFailed = 0;
    void Up(string what, bool ok) { if (!ok) upFailed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
    Up("0.1.0 is not newer than 0.1.0.0", !Updater.IsNewer("v0.1.0", new Version(0, 1, 0, 0)));
    Up("0.1.1 is newer than 0.1.0.0", Updater.IsNewer("v0.1.1", new Version(0, 1, 0, 0)));
    Up("0.2 is newer than 0.1.9", Updater.IsNewer("0.2", new Version(0, 1, 9)));
    Up("a tag that is no version is ignored", !Updater.IsNewer("nightly", new Version(0, 1, 0)));
    Up("a dev build is not installed", !Updater.IsInstalled());
    if (a.Contains("--live"))
    {
        var r = await Updater.LatestAsync(new Version(0, 0, 0));
        Console.WriteLine(r != null ? $"newest release: {r.Version} {r.Url} sha256={r.Sha256 ?? "(none given)"}" : "no release with a BurnSmith-Setup asset yet");
        Up("the newest release carries a digest, so it will not be refused", r?.Sha256 != null);
    }

    var work = Path.Combine(Path.GetTempPath(), $"burnsmith-check-update-{Environment.ProcessId}");
    var realTmp = Environment.GetEnvironmentVariable("TMP");
    var icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
    var locked = Path.Combine(work, "tools", "locked");
    Directory.CreateDirectory(locked);
    try
    {
        // the update: a server on this PC, and %TEMP% moved here so that a real download is not touched
        Environment.SetEnvironmentVariable("TMP", work);
        var updates = Path.Combine(work, "BurnSmith-update");
        Up("the update folder of this check is its own", Path.GetTempPath().TrimEnd('\\').Equals(work, StringComparison.OrdinalIgnoreCase));
        var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        port.Start();
        var url = $"http://localhost:{((System.Net.IPEndPoint)port.LocalEndpoint).Port}/";
        port.Stop();
        using var server = new System.Net.HttpListener();
        server.Prefixes.Add(url);
        server.Start();
        var body = System.Text.Encoding.UTF8.GetBytes("not an installer");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
        var requests = 0;
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                var call = await server.GetContextAsync();
                Interlocked.Increment(ref requests);
                await call.Response.OutputStream.WriteAsync(body);
                call.Response.Close();
            }
        });
        var v = new Version(9, 9, 9, 9);
        async Task<Exception?> Download(string? sha) { try { await Updater.DownloadAsync(new Updater.Release(v, url, sha)); return null; } catch (Exception e) { return e; } }
        Directory.CreateDirectory(updates);
        var older = Path.Combine(updates, "BurnSmith-Setup-0.0.1.exe");
        File.WriteAllText(older, "old");
        Up("no digest: refused", await Download(null) is IOException);
        Up("no digest: nothing asked of the server, nothing written, nothing removed", requests == 0 && Directory.GetFiles(updates).SequenceEqual([older]));
        var lockedOlder = Path.Combine(updates, "BurnSmith-Setup-0.0.2.exe");
        var sameVersion = Path.Combine(updates, $"BurnSmith-Setup-{v}.exe");
        File.WriteAllText(lockedOlder, "old");
        File.WriteAllText(sameVersion, "old");
        using (File.Open(lockedOlder, FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(sameVersion, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await Download(digest);
            Up("right digest: downloaded, locked older installers did not stop it", failure == null);
            if (failure != null) Console.WriteLine(failure.Message);
            Up("an older installer is removed when a download starts", !File.Exists(older));
            Up("a locked one is left alone", File.Exists(lockedOlder) && File.Exists(sameVersion));
        }
        Up("the download is what the server sent", Directory.GetFiles(updates, $"BurnSmith-Setup-{v}-*.exe").SingleOrDefault() is { } got && File.ReadAllBytes(got).SequenceEqual(body));
        static bool Refused(Action act) { try { act(); return false; } catch (IOException) { return true; } }
        Up("the checked download cannot be changed before it runs as administrator", Directory.GetFiles(updates, $"BurnSmith-Setup-{v}-*.exe").Single() is var checkedSetup
            && Refused(() => File.OpenWrite(checkedSetup).Dispose()) && Refused(() => File.Delete(checkedSetup)));
        Up("wrong digest: refused, the download does not stay", await Download(new string('0', 64)) is IOException && Directory.GetFiles(updates, $"BurnSmith-Setup-{v}-*.exe").Length == 0);
        server.Stop();
        Environment.SetEnvironmentVariable("TMP", realTmp);

        // the installer's end decides: an error code comes back as a refusal, and the wait lasts until it is over
        async Task<Exception?> Install(string script) { var cmd = Path.Combine(work, "fake-setup.cmd"); File.WriteAllText(cmd, script); try { await Updater.Install(cmd); return null; } catch (Exception e) { return e; } }
        var refusal = await Install("@exit /b 1");
        Up("an installer ending on an error code (a refused prompt) is told, with its code", refusal is IOException && refusal.Message.Contains("code 1"));
        var seen = Path.Combine(work, "fake-setup.txt");
        Up("an installer ending well: no error, waited for to its end, given the silent arguments and the relaunch",
           await Install($"@ping -n 2 127.0.0.1 >nul\r\n@echo %* > \"{seen}\"\r\n@exit /b 0") == null && File.Exists(seen) && File.ReadAllText(seen) is var given && given.Contains("/SILENT") && given.Contains("/relaunch=1"));

        var noTemplates = Path.Combine(work, "none.zip");
        Up("packing no template is an error", Throws(() => TemplateCatalog.Pack(noTemplates, roots: [work])));
        File.Delete(noTemplates);

        // publish.ps1: no tsMuxeR must stop it first of all, before anything is built
        var script = "publish.ps1";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, script))) { script = Path.Combine(d.FullName, script); break; }
        var refused = "";
        try { Tools.Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-TsMuxer", Path.Combine(work, "none.exe")], env: new Dictionary<string, string> { ["PSModulePath"] = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\Modules") }); } // not the modules of a PowerShell 7 this was started from
        catch (InvalidOperationException e) { refused = e.Message.Replace("\r", "").Replace("\n", ""); }
        Up("publish.ps1 refuses a missing tsMuxeR", refused.Contains("none.exe"));

        // where tools are looked for. Development first: Payload null, as here, and every checkout has a tools folder (vlctest.ps1 is committed)
        Environment.SetEnvironmentVariable("BURNSMITH_TOOLS", null);
        var beside = Path.Combine(AppContext.BaseDirectory, "tools");
        Up("a development build looks in the tools folder of the parents", Tools.SearchRoots().Any(r => r != beside));
        var devJar = Assets.Find(@"bdj\menu.jar");
        Up("a development build takes the jar of a parent's assets folder", AppContext.BaseDirectory.StartsWith(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(devJar)!, "..", "..")), StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("BURNSMITH_TOOLS", Path.Combine(work, "does-not-exist"));
        Up("a BURNSMITH_TOOLS that does not exist is passed over", !Tools.SearchRoots().Any(r => r.StartsWith(work)));
        Up("the cache can be used from several threads", typeof(Tools).GetField("cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.FieldType.Name.StartsWith("Concurrent") == true);

        var tools = Path.GetDirectoryName(locked)!;
        var onPath = Path.Combine(work, "on-path");
        Directory.CreateDirectory(onPath);
        foreach (var name in new[] { "in-both.exe", "only-path.exe", "spaced.exe" }) File.WriteAllText(Path.Combine(onPath, name), "");
        File.WriteAllText(Path.Combine(tools, "in-env.exe"), "");
        Environment.SetEnvironmentVariable("PATH", onPath);
        Environment.SetEnvironmentVariable("BURNSMITH_TOOLS", tools);
        if (!AppFiles.Root().StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) // through dotnet <dll> the fake tools below would replace those an installed copy unpacked
        {
            Up("where a release looks for tools: run BurnSmith.Cli.exe, not dotnet <dll>", false);
            return 1;
        }
        var carried = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(carried, System.IO.Compression.ZipArchiveMode.Create, true))
            foreach (var name in new[] { "in-env.exe", "in-both.exe", "only-bundled.exe" }) zip.CreateEntry(name);
        // the unpack: the same content is left alone whatever exe asks, other content replaces it, a swap's leftovers go and nothing else does
        var unpackings = 0;
        void Fill(string d) { unpackings++; File.WriteAllText(Path.Combine(d, "file"), $"{unpackings}"); }
        var unpacked = Path.Combine(AppFiles.Root(), "check-unpack");
        using (var dead = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit") { CreateNoWindow = true })!)
        {
            dead.WaitForExit();
            string[] gone = [$"{unpacked}.{dead.Id}.old", $"{unpacked}.{dead.Id}.tmp"];
            string[] kept = [$"{unpacked}.4.tmp", $"{unpacked}.mine", $"{unpacked}.{dead.Id}.old.2", $"{unpacked}2.{dead.Id}.old"]; // 4 is Windows' System process: running, so maybe swapping
            foreach (var folder in gone.Concat(kept)) { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "x"), ""); }
            AppFiles.Unpack("check-unpack", "A", Fill);
            Up("an unpack leaves the same stamp alone and redoes another one",
               unpackings == 1 && AppFiles.Unpack("check-unpack", "A", Fill) == unpacked && unpackings == 1 && AppFiles.Unpack("check-unpack", "B", Fill) == unpacked && unpackings == 2 && File.ReadAllText(Path.Combine(unpacked, "file")) == "2");
            Up("what a dead process's swap left is removed; a running process's, and folders of other names, stay", !gone.Any(Directory.Exists) && kept.All(Directory.Exists));
            foreach (var folder in kept) Directory.Delete(folder, true);
            Directory.Delete(unpacked, true);
        }
        byte[] twoBytes = [1, 2];
        var stamped = new MemoryStream(twoBytes);
        Up("a stamp comes from the content, and the stream can then be read from its start", AppFiles.Stamp(stamped) == AppFiles.Stamp(new MemoryStream(twoBytes)) && stamped.Position == 0 && AppFiles.Stamp(stamped) != AppFiles.Stamp(new MemoryStream(new byte[] { 1, 3 })));

        Tools.Payload = () => new MemoryStream(carried.ToArray()); // from here on this process is a release, with these tools "carried"
        Up("a release looks in BURNSMITH_TOOLS and no parent", Tools.SearchRoots().Where(r => r != beside).SequenceEqual([tools]));
        Tools.Run(icacls, [locked, "/deny", "*S-1-1-0:(RD)"]);
        string? Found(string exe) { try { return Tools.Find(exe); } catch (Exception e) { Console.WriteLine($"{exe}: {e.GetType().Name}: {e.Message}"); return null; } }
        Up("a locked sub-folder does not stop the search", Found("only-bundled.exe")?.StartsWith(Tools.BundledDir()!) == true);
        Up("BURNSMITH_TOOLS comes before the bundled copy", Found("in-env.exe")?.StartsWith(tools) == true);
        Up("the bundled copy comes before PATH", Found("in-both.exe")?.StartsWith(Tools.BundledDir()!) == true);
        Up("PATH comes last", Found("only-path.exe")?.StartsWith(onPath) == true);
        Environment.SetEnvironmentVariable("TMP", work); // the embedded jar is written under %TEMP%: not over a real one
        var jar = Assets.Find(@"bdj\menu.jar");
        Environment.SetEnvironmentVariable("TMP", realTmp);
        Up("a release takes the embedded jar, not the one of a parent's assets folder", jar.StartsWith(work, StringComparison.OrdinalIgnoreCase));
        using (var opened = System.IO.Compression.ZipFile.OpenRead(jar))
            Up("the jar can be used", opened.Entries.Any(e => e.Name.EndsWith(".class")));

        var cwd = Path.Combine(work, "cwd");
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, "in-cwd.exe"), "");
        Environment.SetEnvironmentVariable("PATH", $";  ; {onPath} ;");
        var home = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(cwd);
        try
        {
            string? fromCwd = null;
            try { fromCwd = Tools.Find("in-cwd.exe"); } catch (FileNotFoundException) { }
            Up("an empty PATH entry is not the current folder", fromCwd == null);
        }
        finally { Directory.SetCurrentDirectory(home); }
        Up("spaces around a PATH entry do not matter", Found("spaced.exe")?.StartsWith(onPath) == true);
        var absent = "";
        try { Tools.Find("not-anywhere.exe"); } catch (Exception e) { absent = e.Message; }
        Up("a missing tool says how to provide it", absent.Contains("BURNSMITH_TOOLS") && absent.Contains("PATH"));
    }
    finally
    {
        Environment.SetEnvironmentVariable("TMP", realTmp);
        Tools.Run(icacls, [locked, "/remove:d", "*S-1-1-0"]);
        Tools.TryDelete(work);
    }
    return upFailed == 0 ? 0 : 1;

    static bool Throws(Action act) { try { act(); return false; } catch (InvalidOperationException) { return true; } }
}

// burnsmith check-edit [--no-dvd] : the arithmetic of a cut (length, chapters shifted by the start, typed times) without any video, then what needs the tools: videos made by ffmpeg, DVDs with a start video
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
    var twoClips = new DiscTitle("x", Start: 100, End: 400, ChapterAt: [150, 250, 320], Clips: [new(100, 200), new(300, 400)]);
    Check("two clips 100-200 and 300-400 keep 200 s; a chapter in the gap is dropped, one after it moves back 100 s; a gap filter is made",
          twoClips.Length(src) == 200 && twoClips.Cut(src).Duration == 200 && Show(twoClips.ChapterList(200, 5)) == "0,50,120"
          && twoClips.OnDisc(150) == null && twoClips.OnDisc(250) == 150 && twoClips.GapFilter(true) == "aselect='not(gte(t,100)*lt(t,200))',asetpts=N/SR/TB"
          && Throws(() => new DiscTitle("x", Start: 100, End: 400, Clips: [new(100, 300), new(250, 400)]).Cut(src)));
    Check("custom times are shifted by the start, 0 added, outside dropped", Show(new DiscTitle("x", Start: 100, End: 400, ChapterAt: [50, 100, 160, 250.4, 399.5, 500]).ChapterList(300, 5)) == "0,60,150");
    var tracked = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [new(0, "dts", 6, "und", ""), new(1, "ac3", 2, "eng", "")], [new(0, "subrip", "und", "", false), new(1, "hdmv_pgs_subtitle", "fre", "", false)]);
    var relabel = new DiscTitle("x", Audio: [0, 1], Subtitles: [0, 1], External: [new ExternalSubtitle("s.srt", "ger")], TrackLanguage: new Dictionary<string, string> { ["a0"] = "fre", ["s0"] = "eng", ["s2"] = "spa" });
    var relabelled = relabel.Cut(tracked);
    Check("a language set by hand goes on the video's own audio and subtitle tracks, the others keep theirs", relabelled.Audio[0].Lang == "fre" && relabelled.Audio[1].Lang == "eng" && relabelled.Subtitles[0].Lang == "eng" && relabelled.Subtitles[1].Lang == "fre");
    Check("a subtitle file keeps its own language (its track number is not a key of the video's tracks)", relabelled.Subtitles[2].File != null && relabelled.Subtitles[2].Lang == "ger");
    var told = new List<string>();
    Check("--sub with --sub-file: four numbers for four files on a film with none of its own give four tracks, not eight, in silence",
          SubsToKeep([0, 1, 2, 3], 0, 4, "film", told.Add).SequenceEqual([0, 1, 2, 3]) && told.Count == 0);
    Check("--sub and --sub-file: its own tracks as asked, then the files, every file kept", SubsToKeep([1], 2, 2, "film", told.Add).SequenceEqual([1, 2, 3]) && SubsToKeep(null, 1, 1, "film", told.Add).SequenceEqual([1]) && told.Count == 0);
    Check("--sub with numbers no track has: told, with the numbers and the counts, and left out",
          SubsToKeep([0, 1, 2, 3], 0, 0, "film", told.Add).Count == 0 && told.Count == 1 && told[0].Contains("--sub 0,1,2,3") && told[0].Contains("0 of its own") && SubsToKeep([0, 5], 1, 1, "film", told.Add).SequenceEqual([0, 1]) && told.Count == 2 && told[1].Contains("--sub 5:"));
    Check("the disc reads the new language: kept tracks and their names", relabel.KeptAudio(relabelled)[0].Name.StartsWith("Français") && relabel.KeptSubtitles(relabelled)[0].Name == "English" && new DiscTitle("x").KeptAudio(tracked)[0].Lang == "und");
    Check("no language set: the source is returned as it is", new DiscTitle("x", TrackLanguage: new Dictionary<string, string>()).Cut(tracked) == tracked);
    Check("one video's own interval replaces the disc's; a list wins over it", Show(new DiscTitle("x", ChapterMinutes: 2).ChapterList(600, 5)) == "0,120,240,360,480" && Show(new DiscTitle("x", ChapterAt: [30], ChapterMinutes: 2).ChapterList(600, 5)) == "0,30" && Show(new DiscTitle("x").ChapterList(600, 5)) == "0,300");
    // the output folder: only what is nothing but an earlier disc is emptied; one foreign file and the whole folder is left alone
    var outRoot = Path.Combine(Path.GetTempPath(), "bs_out_" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        string OutFolder(string name, params string[] files)
        {
            var dir = Path.Combine(outRoot, name);
            Directory.CreateDirectory(dir);
            foreach (var entry in files)
            {
                var path = Path.Combine(dir, entry);
                if (entry.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }
            return dir;
        }
        static bool Refused(string dir) { try { Tools.PrepareOutput(dir); return false; } catch (IOException) { return true; } }
        var mixed = OutFolder("mixed", "VIDEO_TS/VTS_01_1.VOB", "precieux.txt", "Films/autre.mkv");
        Check("a disc folder with anything else in it is refused, and every file in it survives",
              Refused(mixed) && File.Exists(Path.Combine(mixed, "VIDEO_TS", "VTS_01_1.VOB")) && File.Exists(Path.Combine(mixed, "precieux.txt")) && File.Exists(Path.Combine(mixed, "Films", "autre.mkv")));
        var mixedBd = OutFolder("mixedbd", "BDMV/index.bdmv", "CERTIFICATE/id.bdmv", "notes.txt");
        Check("the same beside a BDMV folder", Refused(mixedBd) &&File.Exists(Path.Combine(mixedBd, "notes.txt")) && File.Exists(Path.Combine(mixedBd, "BDMV", "index.bdmv")));
        var oldDvd = OutFolder("olddvd", "VIDEO_TS/VIDEO_TS.IFO", "AUDIO_TS/");
        var oldBd = OutFolder("oldbd", "BDMV/index.bdmv", "CERTIFICATE/id.bdmv");
        Check("a real earlier DVD or Blu-ray folder is still emptied for the new build", !Refused(oldDvd) && !Refused(oldBd) && !Directory.Exists(oldDvd) && !Directory.Exists(oldBd));
        var noDisc = OutFolder("nodisc", "precieux.txt");
        var empty = OutFolder("empty");
        Check("a folder without any disc is refused; an empty or missing one is fine", Refused(noDisc) &&File.Exists(Path.Combine(noDisc, "precieux.txt")));
    }
    finally { if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true); }
    // the structure check on a disc built by hand: a tiny Blu-ray folder, then the same with something wrong
    var discDir = Path.Combine(Path.GetTempPath(), "bs_disc_" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var bdmv = Path.Combine(discDir, "BDMV");
        foreach (var sub in new[] { "BACKUP", "PLAYLIST", "CLIPINF", "STREAM" }) Directory.CreateDirectory(Path.Combine(bdmv, sub));
        foreach (var (file, magic) in new[] { ("index.bdmv", "INDX0200"), ("MovieObject.bdmv", "MOBJ0200"), ("BACKUP/index.bdmv", "INDX0200"), ("BACKUP/MovieObject.bdmv", "MOBJ0200"), ("PLAYLIST/00001.mpls", "MPLS0200 00001M2TS"), ("CLIPINF/00001.clpi", "HDMV0200") })
            File.WriteAllText(Path.Combine(bdmv, file), magic);
        var packets = new byte[192 * 50];
        for (var k = 0; k < 50; k++) packets[192 * k + 4] = 0x47;
        var stream = Path.Combine(bdmv, "STREAM", "00001.m2ts");
        File.WriteAllBytes(stream, packets);
        var good = DiscCheck.Folder(discDir);
        File.WriteAllBytes(stream, packets[..^1]);
        var shortStream = DiscCheck.Folder(discDir);
        File.WriteAllBytes(stream, packets);
        packets[192 * 7 + 4] = 0;
        File.WriteAllBytes(stream, packets);
        var noSync = DiscCheck.Folder(discDir);
        File.Delete(Path.Combine(bdmv, "CLIPINF", "00001.clpi"));
        File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", "MovieObject.bdmv"), [1]);
        var broken = DiscCheck.Folder(discDir);
        var image = new byte[300 * 2048];
        "BEA01"u8.CopyTo(image.AsSpan(16 * 2048 + 1));
        foreach (var sector in new[] { 256, 299 }) image[sector * 2048] = 2;
        var iso = Path.Combine(discDir, "t.iso");
        File.WriteAllBytes(iso, image);
        var goodImage = DiscCheck.Image(iso);
        File.WriteAllBytes(iso, image[..(299 * 2048)]);
        var cutImage = DiscCheck.Image(iso);
        File.WriteAllBytes(iso, image[..^7]);
        var oddImage = DiscCheck.Image(iso);
        Check("disc check: a tidy Blu-ray folder has no problem; a cut stream, a lost sync byte, a missing clip info and a different backup are each found; same for an image (whole sectors, UDF anchors)",
              good.Count == 0 && shortStream.Count == 1 && shortStream[0].Contains("paquets entiers") && noSync.Count == 1 && noSync[0].Contains("synchronisation")
              && broken.Count >= 2 && broken.Any(p => p.Contains("CLIPINF")) && broken.Any(p => p.Contains("BACKUP"))
              && goodImage.Count == 0 && cutImage.Count == 1 && cutImage[0].Contains("tronquée") && oddImage.Any(p => p.Contains("secteurs")) && DiscCheck.Folder(Path.Combine(discDir, "nothing")).Count == 1);
    }
    finally { Directory.Delete(discDir, true); }
    // the intro: a movie object added after the others (the film's keep their numbers), and First Playback pointing at it without moving a byte of the index
    var films = BurnSmith.Core.Disc.MovieObjectFile.Write([[BurnSmith.Core.Disc.Nav.PlayPlaylist(1)], [BurnSmith.Core.Disc.Nav.PlayPlaylist(2)]]);
    var withIntro = Bdmv.AppendObject(films, out var introObject, BurnSmith.Core.Disc.Nav.PlayPlaylist(98), BurnSmith.Core.Disc.Nav.JumpTitle(0));
    var plain = BurnSmith.Core.Disc.MovieObjectFile.Index([0, 1], 2, 3, 6, 1);
    var started = Bdmv.FirstPlayToObject(plain, 7);
    var tableAt = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(8)) + 4;
    Check("an intro adds a movie object (number 2 after the film's 0 and 1, which keep their commands) that plays playlist 98 then jumps to the top menu; the index then starts on that object and nothing else of it changes",
          introObject == 2 && System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(withIntro.AsSpan(48)) == 3 && withIntro.Length == films.Length + 4 + 24
          && Bdmv.PrependToFilm(withIntro, BurnSmith.Core.Disc.Nav.SelectStreams(subtitle: 1)).Length == withIntro.Length + 12
          && System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(started.AsSpan(tableAt + 6)) == 7 && started.Length == plain.Length
          && Enumerable.Range(0, plain.Length).Where(k => plain[k] != started[k]).All(k => k >= tableAt && k < tableAt + 12));
    var dts = new AudioTrack(0, "dts", 6, "", "");
    Check("audio copied only while it stays in step with the picture: a DTS track is kept when the title starts at 0, becomes AC-3 with a later start or another volume, and stays on a 4K disc",
          BluRay.BdAudio(dts, new DiscTitle("x"), false) == "dts" && BluRay.BdAudio(dts, new DiscTitle("x", Start: 90), false) == "ac3" && BluRay.BdAudio(dts, new DiscTitle("x", Volume: 120), false) == "ac3"
          && BluRay.BdAudio(dts, new DiscTitle("x", Start: 90), true) == "dts" && BluRay.BdAudio(dts, new DiscTitle("x", End: 500), false) == "dts");
    Check("photos and songs are told from videos by their extension", MediaMaker.IsPhoto("a.JPG") && MediaMaker.IsPhoto("b.png") && !MediaMaker.IsPhoto("c.mkv") && MediaMaker.IsSong("d.FLAC") && !MediaMaker.IsSong("e.mkv") && !MediaMaker.IsSong("f.jpg"));
    // the slideshow and the song video, made for real when ffmpeg is there: one frame per 1/25 s for every photo (a png and a jpg of different sizes), the cover or black, the length of the song
    var mediaDir = Path.Combine(Path.GetTempPath(), "bs_media_" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var ffmpeg = Tools.Find("ffmpeg.exe");
        Directory.CreateDirectory(mediaDir);
        string F(string name) => Path.Combine(mediaDir, name);
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=800x600:d=1", "-frames:v", "1", F("a.png")]);
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "smptebars=s=1280x720:d=1", "-frames:v", "1", F("b.jpg")]);
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "sine=f=440:d=3", "-metadata", "title=Premier", F("s.mp3")]);
        int Frames(string file) => int.Parse(Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-count_frames", "-select_streams", "v", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", file]).Trim());
        MediaMaker.Slideshow([F("a.png"), F("b.jpg")], 2, [F("s.mp3")], F("show.mkv"));
        var show = BluRay.Probe(F("show.mkv"));
        MediaMaker.Song(F("s.mp3"), F("b.jpg"), F("song.mkv"));
        var song = BluRay.Probe(F("song.mkv"));
        Check("slideshow: 2 photos of 2 s = 100 frames at 1920x1080 25 fps with AC-3 music; the working folder is gone; a song is 3 s with its title",
              Frames(F("show.mkv")) == 100 && show is { Width: 1920, Height: 1080, AudioCodec: "ac3" } && Math.Abs(show.Duration - 4) < 0.2 && Math.Abs(show.Fps - 25) < 0.01
              && !Directory.Exists(F("show.mkv.photos")) && Math.Abs(song.Duration - 3) < 0.2 && Frames(F("song.mkv")) == 75 && MediaMaker.Describe(F("s.mp3")).Title == "Premier");
        // an mp3 then a flac under 5 s of photos: the sound must last as long as the picture (a concat list of two codecs silently stops after the first)
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "sine=f=660:d=2", F("t.flac")]);
        MediaMaker.Slideshow([F("a.png"), F("b.jpg")], 2.5, [F("s.mp3"), F("t.flac")], F("mixed.mkv"));
        var lastSound = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-select_streams", "a", "-show_entries", "packet=pts_time", "-of", "csv=p=0", F("mixed.mkv")]).Trim().Split('\n').Last().Trim().TrimEnd(','); // ffmpeg 9 ends the last packet's line with a comma
        Check("a slideshow's music of two formats (mp3 then flac) plays until the end of the 5 s of photos", double.Parse(lastSound, System.Globalization.CultureInfo.InvariantCulture) > 4.9);
        MediaMaker.Join([F("show.mkv"), F("show.mkv")], F("twice.mkv"));
        Check("two videos with the same streams join into one of the sum of both; a different size or track list does not match",
              Frames(F("twice.mkv")) == 200 && Math.Abs(BluRay.Probe(F("twice.mkv")).Duration - 8) < 0.2 && MediaMaker.SameLayout(show, show)
              && !MediaMaker.SameLayout(show, show with { Width = 1280, Height = 720 }) && !MediaMaker.SameLayout(show, show with { Audio = [] }) && !MediaMaker.SameLayout(show, show with { Fps = 24 }));
        // same size and codec but another H.264 profile and frame reordering: a stream copy keeps only the first file's parameter sets, so the second part could not decode on a player
        foreach (var (name, settings) in new[] { ("x.mkv", new[] { "-profile:v", "main", "-bf", "0" }), ("y.mkv", new[] { "-profile:v", "high", "-bf", "3" }) })
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=640x360:d=1", "-pix_fmt", "yuv420p", "-c:v", "libx264", .. settings, F(name)]);
        Check("two clips of one size and codec but another H.264 profile or reordering are not joined; the same clip is",
              !MediaMaker.SameLayout(BluRay.Probe(F("x.mkv")), BluRay.Probe(F("y.mkv"))) && MediaMaker.SameLayout(BluRay.Probe(F("x.mkv")), BluRay.Probe(F("x.mkv"))));
        // the video editor's effects as one ffmpeg graph: turned, cropped, coloured, a watermark and a burnt subtitle from names with a space, an apostrophe, a comma, brackets and a semicolon (each special to ffmpeg's filter graph)
        File.Copy(F("b.jpg"), F("l'b.jpg"), true);
        Directory.CreateDirectory(F("d'été, [1]; b"));
        File.WriteAllText(F("d'été, [1]; b/s.srt"), "1\r\n00:00:00,000 --> 00:00:10,000\r\nBonjour\r\n");
        var fx = new VideoEffects(Rotate: 90, CropLeft: 40, CropRight: 60, CropTop: 10, Brightness: 20, WhiteBalance: 30, Look: "Sépia",
                                  Watermark: F("l'b.jpg"), WatermarkWidth: 0.25, WatermarkOpacity: 50, BurnSubtitle: F("d'été, [1]; b/s.srt"), BurnDelay: 2);
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", F("a.png"), "-vf", fx.Vf(800, 600, 3, "null"), F("fx.png")]);
        var fxOut = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", F("fx.png")]).Trim();
        Check("video effects: rotation, crop, colour, watermark and a burnt subtitle make one graph ffmpeg runs; the picture is 600x800 less the crop",
              fx.Size(800, 600) == (500, 790) && fxOut == "500,790" && new VideoEffects().IsEmpty && !fx.IsEmpty
              && new VideoEffects().Take(fx, "crop") is { Rotate: 90, CropLeft: 40, Brightness: 0 });
        // the same graph on a real video cut like a title (-ss before -i), fitted to the disc frame as Blu-ray.Prepare does: every frame comes out
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-ss", "1", "-t", "2", "-i", F("show.mkv"), "-vf", fx.Vf(1920, 1080, 1, "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1"),
            "-r", "25", "-c:v", "libx264", "-preset", "ultrafast", "-an", F("fx.mkv")]);
        Check("video effects on a cut video: 2 s give 50 frames of 1920x1080", Frames(F("fx.mkv")) == 50 && BluRay.Probe(F("fx.mkv")) is { Width: 1920, Height: 1080 });
        // two clips of 1 s (0-1 and 3-4 of the 4 s slideshow) as Blu-ray.Prepare cuts them: 50 frames, 2 s of sound, a subtitle in the gap dropped and one after it moved back
        var clipped = new DiscTitle(F("show.mkv"), Start: 0, End: 4, Clips: [new(0, 1), new(3, 4)]);
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-t", "4", "-i", F("show.mkv"), "-vf", "null," + clipped.GapFilter(false), "-af", clipped.GapFilter(true), "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "ac3", F("clips.mkv")]);
        File.WriteAllText(F("clips.srt"), "1\n00:00:00,200 --> 00:00:00,800\nA\n\n2\n00:00:01,500 --> 00:00:02,000\nB\n\n3\n00:00:03,500 --> 00:00:03,900\nC\n");
        clipped.CloseGaps(F("clips.srt"), 4);
        var clipSound = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-select_streams", "a", "-show_entries", "format=duration", "-of", "csv=p=0", F("clips.mkv")]).Trim();
        Check($"two clips of a video join into 2 s: 50 frames, the sound as long ({clipSound} s), the subtitles closed up",
              Frames(F("clips.mkv")) == 50 && Math.Abs(double.Parse(clipSound, System.Globalization.CultureInfo.InvariantCulture) - 2) < 0.15
              && File.ReadAllText(F("clips.srt")).Replace("\r", "") == "1\n00:00:00,200 --> 00:00:00,800\nA\n\n2\n00:00:01,500 --> 00:00:01,900\nC\n\n");
        // a video's own subtitle read whole and cut at 3 s: the cue at 3.5 s lands at 0.5 s (the input -ss this replaces put a real episode's cues 0.74 s late; this small file does not show it)
        File.WriteAllText(F("own.srt"), "1\n00:00:03,500 --> 00:00:04,000\nX\n");
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=320x240:d=5:r=25", "-i", F("own.srt"), "-c:v", "libx264", "-g", "50", "-c:s", "srt", F("own.mkv")]);
        BluRay.WriteOwnSubtitles(new DiscTitle(F("own.mkv"), Start: 3), [(new SubtitleTrack(0, "subrip", "fre", "", false), F("own_cut.srt"))], 2, _ => { });
        Check("a video's own subtitle is cut at the exact start", File.ReadAllText(F("own_cut.srt")).Contains("00:00:00,500 --> 00:00:01,000"));
        // a camcorder's 702x574 at 16:15 is 748 wide as seen: fitted into 1920x1080 it is 1408 wide (1080 * 748 / 574), not 1320 (the stored shape, squeezed)
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "color=white:s=702x574:d=1,setsar=16/15", "-frames:v", "1", "-vf", BluRay.Fit(1920, 1080) + ",crop=1920:1:0:540,format=gray", "-f", "rawvideo", F("row.raw")]);
        var seen = File.ReadAllBytes(F("row.raw")).Count(b => b > 128); // the white columns of the middle line
        Check($"non-square pixels are made square before the Blu-ray fit: a 702x574 16:15 picture is about 1408 wide on the disc (here {seen})", Math.Abs(seen - 1408) <= 4);
    }
    catch (FileNotFoundException e) { Check($"slideshow and song videos: {e.Message}", false); }
    finally { if (Directory.Exists(mediaDir)) Directory.Delete(mediaDir, true); }
    // a DVD with a start video, made for real with ffmpeg: the first-play command jumps to the title after the film, with and without a menu
    var introDir = Path.Combine(Path.GetTempPath(), "bs_dvdintro_" + Guid.NewGuid().ToString("N")[..8]);
    if (a.Contains("--no-dvd")) Console.WriteLine("skip a DVD's start video, the Settings page after it, an NTSC start video on a PAL film (--no-dvd)");
    else try
    {
        var ffmpeg = Tools.Find("ffmpeg.exe");
        Directory.CreateDirectory(introDir);
        foreach (var (name, source) in new[] { ("film.mkv", "testsrc2=s=320x240:d=2"), ("intro.mkv", "smptebars=s=320x240:d=2") })
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", source, "-f", "lavfi", "-i", "sine=f=440:d=2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-c:a", "ac3", Path.Combine(introDir, name)]);
        (int Titles, string LastFirstPlayCommand) FirstPlay(string disc)
        {
            var d = File.ReadAllBytes(Path.Combine(disc, "VIDEO_TS", "VIDEO_TS.IFO"));
            int U32(int o) => (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
            int U16(int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
            var (fp, titleTable) = (U32(0x84), U32(0xC4) * 2048);
            var commands = fp + U16(fp + 0xE4); // the first-play chain's command table: counts, then 8 bytes a command
            var last = commands + 8 + 8 * (U16(commands) - 1);
            return (U16(titleTable), Convert.ToHexString(d.AsSpan(last, 8)));
        }
        var titles2 = new[] { new DiscTitle(Path.Combine(introDir, "film.mkv")), new DiscTitle(Path.Combine(introDir, "intro.mkv"), Intro: true) };
        Dvd.Build(new DvdJob(titles2, Path.Combine(introDir, "plain"), null));
        var noMenu = FirstPlay(Path.Combine(introDir, "plain"));
        Dvd.Build(new DvdJob(titles2, Path.Combine(introDir, "menu"), TemplateCatalog.Default().Clone()));
        var withMenu = FirstPlay(Path.Combine(introDir, "menu"));
        Check("a DVD's start video is the title after the film, which the first-play command jumps to (title 2 of 2), with a menu or without; the discs pass the structure check",
              noMenu == (2, "3002000000020000") && withMenu == (2, "3002000000020000")
              && DiscCheck.Folder(Path.Combine(introDir, "plain")).Count == 0 && DiscCheck.Folder(Path.Combine(introDir, "menu")).Count == 0);

        // what was only ever looked at in VLC, on one disc: an NTSC start video before a PAL film with two audio tracks, a default subtitle and a menu; read back from the IFO and the VOB
        var mixedDisc = Path.Combine(introDir, "mixed");
        var (palFilm, ntscIntro, srt) = (Path.Combine(introDir, "pal.mkv"), Path.Combine(introDir, "ntsc.mkv"), Path.Combine(introDir, "pal.srt"));
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", "-f", "lavfi", "-i", "sine=f=440:d=2", "-f", "lavfi", "-i", "sine=f=880:d=2", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-c:a", "ac3", palFilm]);
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "smptebars=s=320x240:r=30000/1001:d=2", "-f", "lavfi", "-i", "sine=f=220:d=2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-c:a", "ac3", ntscIntro]);
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nBonjour\n");
        Dvd.Build(new DvdJob([new DiscTitle(palFilm, Audio: [0, 1], Subtitles: [0], External: [new ExternalSubtitle(srt, "fre")], DefaultSubtitle: 0), new DiscTitle(ntscIntro, Intro: true)], mixedDisc, new MenuDesign()));
        var vmg = File.ReadAllBytes(Path.Combine(mixedDisc, "VIDEO_TS", "VIDEO_TS.IFO"));
        var vts = File.ReadAllBytes(Path.Combine(mixedDisc, "VIDEO_TS", "VTS_01_0.IFO"));
        static int U16(byte[] d, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
        static int U32(byte[] d, int o) => (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
        static int Chain(byte[] d, int table, int number) => table + U32(d, table + 8 * number + 4); // a table of program chains: 8 bytes of head, then 8 a chain
        static string Hex(byte[] d, int o) => Convert.ToHexString(d.AsSpan(o, 8));
        static string Commands(byte[] d, int pgc, int first, int count) => string.Join(' ', Enumerable.Range(first, count).Select(i => Hex(d, pgc + U16(d, pgc + 0xE4) + 8 + 8 * i)));
        static string Pre(byte[] d, int pgc) => Commands(d, pgc, 0, U16(d, pgc + U16(d, pgc + 0xE4)));
        static string Post(byte[] d, int pgc) => Commands(d, pgc, U16(d, pgc + U16(d, pgc + 0xE4)), U16(d, pgc + U16(d, pgc + 0xE4) + 2));
        static int Sector(byte[] d, int pgc, int field) => U32(d, pgc + U16(d, pgc + 0xE8) + field); // of its first cell: 8 = first sector, 20 = last
        var titleChains = U32(vts, 0xCC) * 2048;
        var menuChains = U32(vts, 0xD0) * 2048 + U32(vts, U32(vts, 0xD0) * 2048 + 12); // the first language's
        var (filmPgc, introPgc) = (Chain(vts, titleChains, 1), Chain(vts, titleChains, 2));
        var pages = Enumerable.Range(1, U16(vts, menuChains)).Select(i => Chain(vts, menuChains, i)).Where(p => vts[p + 3] > 0).ToList(); // only the chains with a cell
        var menuVob = File.ReadAllBytes(Path.Combine(mixedDisc, "VIDEO_TS", "VTS_01_0.VOB"));
        var rootPci = Sector(vts, pages[0], 8) * 2048 + 0x2D; // its first navigation pack holds the buttons
        var rootButtons = Enumerable.Range(0, menuVob[rootPci + 0x71]).Select(i => Hex(menuVob, rootPci + 0x8E + 18 * i + 10)).ToList();

        Check("Settings after a start video: first play sets audio 1 and the default subtitle (g2, g3), and only then jumps to the start video, title 2",
              Pre(vmg, U32(vmg, 0x84)) == "7100000100000000 7100000200010000 7100000300010000 7100000404000000 7100000500000000 3002000000020000");
        Check("Settings after a start video: its end shows the default subtitle (stream 0), then calls the menu just as the film's end does",
              Post(vts, introPgc) == $"51000000C0000000 {Post(vts, filmPgc)}");
        Check("Settings after a start video: the root's button computes g6 = 64 g2 + g3 and picks one of the 4 settings pages, the disc's first state (65) being page 3",
              pages.Count == 5 && rootButtons.Contains("7101000F0002000D")
              && Post(vts, pages[0]).Contains("00A1000E00020005 3001000000000000 6100000600020000 7500000600400000 6300000600030000 20A4000600400002 20A4000600410003 20A4000600800004 20A4000600810005 2004000000000002"));
        Check("Settings after a start video: page 3 is the page of audio 1 and subtitle 1: choosing either stays on it, subtitle off goes to page 2",
              Post(vts, pages[2]).Contains("7100000200010000 5104008000000003") && Post(vts, pages[2]).Contains("7100000300010000 51040000C0000003") && Post(vts, pages[2]).Contains("7100000300000000 51040000BE000002"));

        Check("Settings, one page for each pair: choosing subtitle 1 (the 4th button: audio 1, 2, off, 1) leaves its rank for the page it stays on, whose pre takes it back, else the first button",
              Post(vts, pages[2]).Contains("7100000410000000 7100000500010000 7100000300010000 51040000C0000003") && Pre(vts, pages[2]) == "00B1000500010005 4600000000040000 7100000500000000 0001000000000006 5600000004000000 0000000000000000");

        var slice = Path.Combine(introDir, "intro-on-disc.vob");
        using (var titlesVob = File.OpenRead(Path.Combine(mixedDisc, "VIDEO_TS", "VTS_01_1.VOB")))
        {
            var bytes = new byte[(Sector(vts, introPgc, 20) - Sector(vts, introPgc, 8) + 1) * 2048];
            titlesVob.Position = Sector(vts, introPgc, 8) * 2048L;
            titlesVob.ReadExactly(bytes);
            File.WriteAllBytes(slice, bytes);
        }
        var probed = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-count_frames", "-show_entries", "stream=codec_type,width,height,r_frame_rate,nb_read_frames,sample_rate", "-of", "json", slice]);
        var streams = System.Text.Json.JsonDocument.Parse(probed).RootElement.GetProperty("streams").EnumerateArray().ToList();
        var picture = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
        var frames = int.Parse(picture.GetProperty("nb_read_frames").GetString()!);
        Check("an NTSC start video on a PAL film: the source really is 29.97 fps", Math.Abs(BluRay.Probe(ntscIntro).Fps - 29.97) < 0.01);
        Check("an NTSC start video on a PAL film: the disc's titles and menus are PAL (bits 4-5 of their video attributes)", (vts[0x200] >> 4 & 3) == 1 && (vts[0x100] >> 4 & 3) == 1);
        Check("an NTSC start video on a PAL film: its chain and the film's say 2 s at 25 fps", vts[introPgc + 6] == 2 && vts[introPgc + 7] >> 6 == 1 && vts[filmPgc + 6] == 2 && vts[filmPgc + 7] >> 6 == 1);
        Check($"an NTSC start video on a PAL film: its sectors on the disc are 720x576 at 25 fps, 2 s long ({frames} frames), with 48 kHz sound",
              picture.GetProperty("width").GetInt32() == 720 && picture.GetProperty("height").GetInt32() == 576 && picture.GetProperty("r_frame_rate").GetString() == "25/1" && frames is >= 48 and <= 52
              && streams.Any(s => s.GetProperty("codec_type").GetString() == "audio" && s.GetProperty("sample_rate").GetString() == "48000"));
        Check("that disc passes the structure check", DiscCheck.Folder(mixedDisc).Count == 0);
    }
    catch (FileNotFoundException e) { Check($"a DVD's start video: {e.Message}", false); }
    catch (InvalidOperationException e) { Check($"a DVD's start video: {e.Message.Split('\n')[0]}", false); } // a tool that failed: this disc cannot be made, which is a finding, not a crash of the check
    finally { if (Directory.Exists(introDir)) Directory.Delete(introDir, true); }
    SettingsCheck.Disc(Check, a.Contains("--no-dvd"));
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
    static byte[] Segment(byte kind, double seconds, params byte[] body) => BurnSmith.Core.Disc.PgsStream.Segment(kind, (uint)(seconds * 90000), (uint)(seconds * 90000), body);
    static byte[] Be(int value, int bytes) => [.. Enumerable.Range(0, bytes).Select(i => (byte)(value >> 8 * (bytes - 1 - i)))];
    byte[] SupSet(double at, bool shown, int x = 100, int y = 900, bool split = false)
    {
        var picture = BurnSmith.Core.Disc.PgsStream.Pack([1, 1, 1, 1, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 1, 0, 0, 2, 2, 0, 1, 2, 1, 2, 1, 2, 1, 2], 8, 4);
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
    // VobSub built by hand: pictures in four colours, run-length coded in nibbles, field by field, cut into packs of a program stream
    static byte[] SpuField(byte[] px, int w, int h, int first, bool toEnd)
    {
        var n = new List<int>();
        for (var y = first; y < h; y += 2)
        {
            for (var x = 0; x < w;)
            {
                int c = px[y * w + x], run = 1;
                while (x + run < w && px[y * w + x + run] == c) run++;
                if (x + run == w && run > 3 && toEnd) { n.AddRange([0, 0, 0, c]); break; } // 0 = to the end of the row
                run = Math.Min(run, 255);
                var code = run << 2 | c;
                for (var d = (run < 4 ? 1 : run < 16 ? 2 : run < 64 ? 3 : 4) - 1; d >= 0; d--) n.Add(code >> 4 * d & 15);
                x += run;
            }
            if (n.Count % 2 == 1) n.Add(0);
        }
        return [.. Enumerable.Range(0, n.Count / 2).Select(i => (byte)(n[2 * i] << 4 | n[2 * i + 1]))];
    }
    byte[] Spu(byte[] px, int w, int h, int x0, int y0, int stopTicks, bool toEnd)
    {
        byte[] top = SpuField(px, w, h, 0, toEnd), bottom = SpuField(px, w, h, 1, toEnd);
        var first = 4 + top.Length + bottom.Length;
        byte[] area = [(byte)(x0 >> 4), (byte)((x0 & 15) << 4 | (x0 + w - 1) >> 8), (byte)(x0 + w - 1), (byte)(y0 >> 4), (byte)((y0 & 15) << 4 | (y0 + h - 1) >> 8), (byte)(y0 + h - 1)];
        byte[] commands = [0x01, 0x03, 0x32, 0x10, 0x04, 0x8F, 0xF0, 0x05, .. area, 0x06, .. Be(4, 2), .. Be(4 + top.Length, 2), 0xFF];
        var second = first + 4 + commands.Length; // the sequence that hides it (none when stopTicks < 0: the sequence then points to itself)
        byte[] stop = stopTicks < 0 ? [] : [.. Be(stopTicks, 2), .. Be(second, 2), 0x02, 0xFF];
        return [.. Be(second + stop.Length, 2), .. Be(first, 2), .. top, .. bottom, .. Be(0, 2), .. Be(stopTicks < 0 ? first : second, 2), .. commands, .. stop];
    }
    static byte[] Pack(int substream, byte[] data, double? seconds)
    {
        var pts = seconds is { } t ? (long)(t * 90000) : 0;
        byte[] stamp = seconds == null ? [] : [(byte)(0x21 | (pts >> 30 & 7) << 1), (byte)(pts >> 22), (byte)((pts >> 15 & 0x7F) << 1 | 1), (byte)(pts >> 7), (byte)((pts & 0x7F) << 1 | 1)];
        byte[] pes = [0, 0, 1, 0xBD, .. Be(3 + stamp.Length + 1 + data.Length, 2), 0x81, (byte)(seconds == null ? 0 : 0x80), (byte)stamp.Length, .. stamp, (byte)substream, .. data];
        return [0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 1, 0x89, 0xC3, 0xF8, .. pes];
    }
    byte[] vobA = [.. Enumerable.Range(0, 12 * 6).Select(i => (byte)((i % 12 / 3 + i / 12) % 4))];
    for (var i = 24; i < 36; i++) vobA[i] = 2; // a whole row of one colour
    byte[] vobB = new byte[300 * 4];
    for (var x = 0; x < 300; x++) (vobB[x], vobB[300 + x], vobB[600 + x], vobB[900 + x]) = (1, (byte)(x < 70 ? 0 : 3), (byte)(x < 20 ? 2 : 1), (byte)(x % 2 == 0 ? 3 : 0));
    var vobDir = Path.Combine(Path.GetTempPath(), "bs_vobsub_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(vobDir);
    try
    {
        byte[] spuA = Spu(vobA, 12, 6, 100, 400, 200, false), spuB = Spu(vobB, 300, 4, 20, 30, -1, true), spuC = Spu(vobA, 12, 6, 7, 9, 100, false);
        var sub = new List<byte>();
        long at1 = sub.Count;
        sub.AddRange(Pack(0x20, spuA[..30], 1.5));                // the first subpicture in two packs, a packet of another stream between them
        long at3 = sub.Count;
        sub.AddRange(Pack(0x21, spuC, 2.0));
        sub.AddRange(Pack(0x20, spuA[30..], null));
        long at2 = sub.Count;
        sub.AddRange(Pack(0x20, spuB, 5.0));
        File.WriteAllBytes(Path.Combine(vobDir, "v.sub"), [.. sub]);
        File.WriteAllText(Path.Combine(vobDir, "v.idx"), $"# VobSub index file, v7 (do not modify this line!)\nsize: 720x480\npalette: 000000, ffffff, ff0000, 00ff00, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000\n\nid: fr, index: 0\n"
            + $"timestamp: 00:00:01:500, filepos: {at1:x9}\ntimestamp: 00:00:05:000, filepos: {at2:x9}\n\nid: xx, index: 1\ntimestamp: 00:00:02:000, filepos: {at3:x9}\n");
        var idx = VobSub.ReadIndex(Path.Combine(vobDir, "v.idx"));
        Check("VobSub index: size, 16 colours, one track per id with its language, its pictures' times and places",
              idx.Width == 720 && idx.Height == 480 && idx.Palette.Length == 16 && idx.Palette[2] == 0xFF0000 && idx.Streams.Count == 2 && idx.Streams[0].Lang == "fre" && idx.Streams[1].Lang == "und" && idx.Streams[1].Number == 1
              && idx.Streams[0].Cues.Count == 2 && idx.Streams[0].Cues[0] == (1.5, at1) && idx.Streams[0].Cues[1] == (5.0, at2));
        var decodedA = VobSub.Decode(spuA, idx.Palette);
        var decodedB = VobSub.Decode(spuB, idx.Palette);
        Check("VobSub picture: place and size, every pixel through the nibble codes of all lengths and the two fields, colours and opacity",
              decodedA is { X: 100, Y: 400, Width: 12, Height: 6 } && decodedA.Pixels.SequenceEqual(vobA) && decodedA.Colors[1] == (255, 255, 255, 255) && decodedA.Colors[2] == (255, 0, 0, 255) && decodedA.Colors[3] == (0, 255, 0, 136) && decodedA.Colors[0].A == 0
              && decodedB is { X: 20, Y: 30, Width: 300, Height: 4 } && decodedB.Pixels.SequenceEqual(vobB) && Math.Abs(decodedA.End - 200 * 1024 / 90000.0) < 1e-9 && double.IsNaN(decodedB.End));
        Check("VobSub: not a subpicture gives nothing, not an error", VobSub.Decode([1, 2, 3], idx.Palette) == null && VobSub.Decode([.. spuA[..20]], idx.Palette) == null && VobSub.ReadSpu([.. sub], 0, 0x25) == null);
        var vobPictures = VobSub.Read(Path.Combine(vobDir, "v.idx"), 0, out var vobSkipped);
        Check("VobSub: a subpicture cut into two packs with another stream's packet between them is read whole; shown at its time, hidden by its own command, else after 3 s or when the next shows",
              vobSkipped == 0 && vobPictures.Count == 2 && vobPictures[0].Pixels.SequenceEqual(vobA) && Math.Abs(vobPictures[0].Start - 1.5) < 1e-9 && Math.Abs(vobPictures[0].End - (1.5 + 200 * 1024 / 90000.0)) < 1e-6
              && Math.Abs(vobPictures[1].Start - 5.0) < 1e-9 && Math.Abs(vobPictures[1].End - 8.0) < 1e-9 && VobSub.Read(Path.Combine(vobDir, "v.idx"), 1, out _) is [{ X: 7, Y: 9 }]);
        var vobSup = VobSub.ToSup(Path.Combine(vobDir, "v.idx"), 0, 720, 480, out _);
        var vobCues = PgsSubtitles.Cues(vobSup, out var vobBad);
        Check("VobSub as PGS: same times, place and size on a 720x480 plane, white kept, the clear colour transparent",
              vobBad == 0 && vobCues.Count == 2 && Math.Abs(vobCues[0].Start - 1.5) < 0.001 && Math.Abs(vobCues[0].End - vobPictures[0].End) < 0.001 && vobCues[0] is { X: 100, Y: 400, Width: 12, Height: 6, PlaneWidth: 720, PlaneHeight: 480 }
              && Enumerable.Range(0, 72).All(i => vobCues[0].Alpha[i] == (vobA[i] == 0 ? 0 : vobA[i] == 3 ? 136 : 255)) && Enumerable.Range(0, 72).Where(i => vobA[i] == 1).All(i => vobCues[0].Luma[i] == 255)
              && vobCues[1].Width == 300 && vobCues[1].Alpha[0] == 255 && vobCues[1].Alpha[300] == 0 && PgsSubtitles.PlaneSize(vobSup) == (720, 480));
        var vobBig = PgsSubtitles.Cues(VobSub.ToSup(Path.Combine(vobDir, "v.idx"), 0, 1440, 960, out _), out _);
        Check("VobSub scaled to a bigger plane: the picture grows with the frame, its middle keeps its opacity", vobBig.Count == 2 && vobBig[0] is { X: 200, Y: 800, Width: 24, Height: 12, PlaneWidth: 1440 } && vobBig[0].Alpha[6 * 24 + 6] > 0 && vobBig[1].Width == 600);
        var idxFile = Path.Combine(vobDir, "v.idx");
        var tracks = ExternalSubtitle.ParseAll(idxFile);
        var typedTrack = ExternalSubtitle.ParseAll("ger=" + idxFile);
        var missingSub = false;
        File.Move(Path.Combine(vobDir, "v.sub"), Path.Combine(vobDir, "w.sub"));
        try { ExternalSubtitle.ParseAll(idxFile); } catch (FileNotFoundException) { missingSub = true; }
        File.Move(Path.Combine(vobDir, "w.sub"), Path.Combine(vobDir, "v.sub"));
        var nearIdx = ExternalSubtitle.Near(Path.Combine(vobDir, "v.mkv"));
        File.Move(Path.Combine(vobDir, "v.sub"), Path.Combine(vobDir, "w.sub"));
        var nearNoSub = ExternalSubtitle.Near(Path.Combine(vobDir, "v.mkv"));
        File.Move(Path.Combine(vobDir, "w.sub"), Path.Combine(vobDir, "v.sub"));
        Check("an .idx beside the video is found with its .sub (one track per stream), and ignored without it", nearIdx.Count == 2 && nearIdx[1].Stream == 1 && nearNoSub.Count == 0);
        Check("an .idx is one track per stream, in the language it gives (and a .sub chosen stands for its .idx); typed language: one track; without its .sub: refused",
              tracks is [{ Lang: "fre", Stream: 0 }, { Lang: "und", Stream: 1 }] && tracks[1].Track(5).IsPgs && typedTrack is [{ Lang: "ger", Stream: 0 }] && missingSub
              && ExternalSubtitle.ForAll(Path.Combine(vobDir, "film.mkv"), Path.Combine(vobDir, "v.sub")).Count == 2 && ExternalSubtitle.Extensions.Contains(".idx"));
        Check("a VobSub inside a video: its codec data (ffprobe's hex dump) reads as the head of an .idx, the track is carried as pictures; the second track of an .idx without language is not named like the first",
              VobSub.ExtradataText("00000000: 7369 7a65 3a20 3633 3278 3237 360a 7061  size: 632x276.pa\n00000010: 6c65 7474 653a 2066 6666 6666 66         lette: ffffff\n") == "size: 632x276\npalette: ffffff" && VobSub.ParseIndex("size: 632x276") is { Width: 632, Height: 276 }
              && new SubtitleTrack(0, "dvd_subtitle", "fre", "", false) is { IsVobSub: true, IsPicture: true, Supported: true, IsPgs: false } && !new SubtitleTrack(0, "dvb_subtitle", "fre", "", false).Supported
              && new ExternalSubtitle("a.idx", "und", Stream: 0).Track(2).Title == "a" && new ExternalSubtitle("a.idx", "und", Stream: 1).Track(3).Title == "a (2)");
        // an object too big for one segment goes in pieces and reads back whole
        var noise = new byte[1500 * 300];
        for (var i = 0; i < noise.Length; i++) noise[i] = (byte)(1 + (i * 7 + i / 3) % 200);
        var writer = new PgsSubtitles.Writer(1920, 1080);
        writer.Add(1, 2, 10, 20, 1500, 300, noise, [.. Enumerable.Range(0, 201).Select(i => ((byte)i, (byte)(255 - i), (byte)(i / 2), (byte)255))]);
        var bigSup = writer.Finish();
        var bigCues = PgsSubtitles.Cues(bigSup, out var bigBad);
        Check("PGS written in pieces: a picture bigger than one segment reads back whole", bigBad == 0 && bigCues.Count == 1 && bigCues[0] is { X: 10, Y: 20, Width: 1500, Height: 300 } && bigCues[0].Alpha.All(a => a == 255) && bigSup.Length > 2 * 65535);
    }
    finally { Directory.Delete(vobDir, true); }
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
    var movieObjects = Bdmv.PrependToFilm(BurnSmith.Core.Disc.MovieObjectFile.Write([[cmdA, cmdB], [cmdC]]), cmdC);
    Check("a command put first in the film's movie object: object 0 gains it before its own, the others stay, the file stays valid",
          movieObjects.Length == BurnSmith.Core.Disc.MovieObjectFile.Write([[cmdC, cmdA, cmdB], [cmdC]]).Length && movieObjects.SequenceEqual(BurnSmith.Core.Disc.MovieObjectFile.Write([[cmdC, cmdA, cmdB], [cmdC]]))
          && new Func<bool>(() => { try { Bdmv.PrependToFilm([1, 2, 3], cmdA); return false; } catch (InvalidDataException) { return true; } })());
    var order = DiscTitle.MainFirst([new("b1.mkv", Bonus: true), new("m1.mkv"), new("b2.mkv", Bonus: true), new("m2.mkv")]);
    Check("disc order: the feature first, then the bonus, each group in its own order", string.Join(',', order.Select(t => t.Video)) == "m1.mkv,m2.mkv,b1.mkv,b2.mkv");
    Check("a disc of bonus only is refused", Throws(() => DiscTitle.MainFirst([new("b.mkv", Bonus: true)])) && DiscTitle.MainFirst([]).Count == 0);
    Check("typed times", Timecode.Parse("90") == 90 && Timecode.Parse("1:30") == 90 && Timecode.Parse("1:02:03,5") == 3723.5 && Timecode.Parse("1:x") == null && Timecode.Parse("1.5:00") == null && Timecode.Parse("") == null);
    Check("times shown", Timecode.Format(3723) == "1:02:03" && Timecode.Format(90.5) == "0:01:30.5");
    return failed == 0 ? 0 : 1;

    static bool Throws(Action act) { try { act(); return false; } catch (ArgumentException) { return true; } }
}

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
    // the templates' own button pictures only: DVDStyler's SVG buttons and the user's are there on every machine
    var pictureButtonsOffered = libraryButtons.Any(b => b.Source is not (ElementLibrary.Styles or ElementLibrary.DvdStyler or ElementLibrary.MyButtons));
    return libraryProblems == 0 && libraryBackgrounds.Count > 0 && pictureButtonsNeeded == pictureButtonsOffered ? 0 : 1;
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

// burnsmith drives | iso <disc folder> <out.iso> [--label X] [--dvd] | burn <image.iso> <F:>   (images: IMAPI2; burning: ImgBurn when installed, else IMAPI2)
if (a.Count >= 1 && a[0] == "drives")
{
    Console.WriteLine($"ImgBurn: {Iso.ImgBurn() ?? "absent (burning through IMAPI2)"}");
    var drives = Iso.Drives();
    foreach (var d in drives)
    {
        var disc = d.Letter.Length > 0 && d.Id != d.Letter ? Imapi.Inspect(d.Letter) : null; // Id == Letter: a drive only ImgBurn reaches
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
    Console.Error.WriteLine("usage: burnsmith bd|dvd --video <file> [--trim A-B[,C-D] --volume N --chapter-at T,T|--no-chapters|--chapter-every MIN] [--video <file> ...] --out <folder> [--design NAME] [--title TEXT] [--no-menu] [--music FILE --music-start S --music-volume N --music-fade S --loop-seconds S] [--chapters MINUTES] [--audio N,N] [--sub N,N] [--sub-file FILE|LANG=FILE] [--bonus] [--credits 'A|B'] [bd: --uhd --profile NAME --quality Q --play-mode N --label NAME --titles 'fra=Titre|eng=Title']");
    return 2;
}

var design = a.Contains("--no-menu") ? null
    : (TemplateCatalog.Find(Opt("--design")) ?? (Opt("--design") is { } unknown ? throw new InvalidOperationException($"unknown template: {unknown}") : TemplateCatalog.Default())).Clone();
if (design != null && Opt("--title") is { } title) design.Title = title;
if (design != null && Opt("--highlight") is { } hlStyle) design.HighlightStyle = hlStyle; // box | underline | textcolor
if (design != null && a.Contains("--list")) design.SceneList = true; // page de scènes en liste (modèles qui la décrivent)
if (design != null && a.Contains("--animated-thumbs")) design.AnimatedThumbs = true; // vignettes animées (DVD : un extrait par case)
if (design != null && Opt("--credits") is { } credits) design.Credits = credits.Replace('|', '\n'); // --credits "Directed by A|Music by B"
// --page-bg KIND=PICTURE (repeatable; KIND = root | scenes | settings | credits | bonus): that kind of page gets its own background picture
foreach (var spec in OptAll("--page-bg"))
{
    var eq = spec.IndexOf('=');
    if (design == null || eq < 1 || !File.Exists(spec[(eq + 1)..])) throw new ArgumentException($"--page-bg: kind=picture expected (root=fond.jpg), with an existing picture and a menu: '{spec}'");
    design.PageBackgrounds[spec[..eq].Trim().ToLowerInvariant()] = Path.GetFullPath(spec[(eq + 1)..]);
}
foreach (var spec in OptAll("--element")) design?.Elements.Add(MenuElement.Parse(spec)); // --element "kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54;align=center"
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
    // --sub-file FILE | --sub-file fra=FILE (repeatable): a subtitle file kept as one more track, after the video's own and the --sub ones
    var files = own.Select((x, i) => (x, i)).Where(x => x.x == "--sub-file" && x.i + 1 < own.Count).SelectMany(x => ExternalSubtitle.ParseAll(own[x.i + 1])).ToList();
    var subs = Numbers("--sub");
    if (subs != null || files.Count > 0) subs = SubsToKeep(subs, BluRay.Probe(v).Subtitles.Count, files.Count, Path.GetFileName(v), Console.Error.WriteLine);
    int? defaultAudio = int.TryParse(Own("--default-audio"), out var da) ? da : null, defaultSub = int.TryParse(Own("--default-sub"), out var ds) ? ds : null;
    if (defaultAudio is { } wanted && !(Numbers("--audio") ?? [0]).Contains(wanted)) throw new ArgumentException($"--default-audio {wanted} is not one of the audio tracks kept (--audio)");
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
    return new DiscTitle(v, null, Numbers("--audio"), subs, Time(trim[0]) ?? 0, trim.Length > 1 ? Time(trim[1]) : null, volume, chapterAt, files.Count > 0 ? files : null, own.Contains("--bonus"), defaultAudio, defaultSub, chapterMinutes, own.Contains("--intro"), trackLanguage,
                          Own("--effects") is { } fx ? System.Text.Json.JsonSerializer.Deserialize<VideoEffects>(File.ReadAllText(fx)) : null, // --effects FILE.json: the video editor's settings (VideoEffects, by property name)
                          clipList);
}).ToList();
// --reencode (never copy the video), --crf N and --preset NAME (x264 quality of a Blu-ray), --dvd-kbps N (DVD video bit rate by hand)
// bd: --profile "BD25 720p" (BD50|BD25|BD9|BD5, 1080p/i or 720p; default BD25), --quality source|fit|15|20|30|<kb/s> (fit = "Adapter au disque"; up to 60000 with --uhd, where --reencode = "Conversion"),
//     --capacity GB (usable size, instead of the profile's), --play-mode 0|1|2 (menu then all titles | first title then all | back to the menu) [--no-rewind] (DVD too)
// dvd: --pal | --ntsc (default: as the first video), --4x3 (4:3 titles, the menu stays 16:9)
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
    Dvd.Build(new DvdJob(titles, Path.GetFullPath(outDir), design, chapters, Encode: encode with { DvdPal = a.Contains("--pal") ? true : a.Contains("--ntsc") ? false : null, DvdWide = !a.Contains("--4x3") },
                         Playback: playback), Console.WriteLine);
var problems = DiscCheck.Folder(Path.GetFullPath(outDir));
Console.WriteLine(problems.Count == 0 ? "disc structure checked: no problem" : "disc structure: " + string.Join(" ; ", problems));
return problems.Count == 0 ? 0 : 1;
