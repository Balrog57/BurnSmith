using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BurnSmith.Core;
using BurnSmith.Core.Menu;

namespace BurnSmith.Cli;

/// <summary>Template, media-creation, artwork and utility CLI commands.</summary>
internal static class CliTools
{
    public static int RunPreview(List<string> a)
    {
        if (a.Count < 3) return CliBuild.Usage();
        var chosen = (a[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(a[1]) ? JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(a[1])) : TemplateCatalog.Find(a[1]))
            ?? throw new InvalidOperationException($"unknown template: {a[1]}");
        Directory.CreateDirectory(a[2]);
        var pic = CliBuild.Opt(a, "--picture") is { } p ? Path.GetFullPath(p) : null;
        var episodes = int.TryParse(CliBuild.Opt(a, "--episodes"), out var ep) ? ep : 0;
        var sample = new MenuContent(1, pic, Enumerable.Repeat(pic, 7).ToList(),
            episodes > 1 ? Enumerable.Range(1, episodes).Select(i => new TitleEntry($"Episode {i}", i, pic, CliBuild.Opt(a, "--overview"))).ToList() : null,
            ["Français AC-3 mono", "English AC-3 mono", "Español AC-3 mono"], ["Français", "English"],
            int.TryParse(CliBuild.Opt(a, "--bonus"), out var extras) && extras > 0 ? Enumerable.Range(0, extras).Select(i => new TitleEntry($"Bonus {i + 1}", Math.Max(episodes, 1) + 1 + i, pic)).ToList() : null);
        if (CliBuild.Opt(a, "--menu-aspect") is { } screen) chosen = TemplateCatalog.Shaped(chosen, screen == "4:3");
        chosen.Language(CliBuild.Opt(a, "--menu-language") is "fr" or "en" ? CliBuild.Opt(a, "--menu-language")! : chosen.ButtonLanguage);
        chosen.Title = CliBuild.Opt(a, "--title") ?? (chosen.ButtonLanguage == "en" ? "My Blu-ray" : "Mon Blu-ray");
        if (a.Contains("--list")) chosen.SceneList = true;
        if (a.Contains("--scenes-only")) chosen.ScenesOnly = true;
        if (a.Contains("--bonus-text")) chosen.BonusText = true;
        if (CliBuild.Opt(a, "--button-language") is "fr" or "en") chosen.ButtonLanguage = CliBuild.Opt(a, "--button-language")!;
        if (CliBuild.Opt(a, "--credits") is { } sampleCredits) chosen.Credits = sampleCredits.Replace('|', '\n');
        foreach (var spec in CliBuild.OptAll(a, "--element")) chosen.Elements.Add(MenuElement.Parse(spec));

        byte[] Lit(MenuPage page, byte[] jpeg)
        {
            static BitmapSource Picture(byte[] data) => BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(Picture(jpeg), new Rect(0, 0, MenuBuilder.W, MenuBuilder.H));
                foreach (var b in page.Buttons.Where(b => b.FocusTexts == null || b == page.Buttons.First(o => o.FocusTexts != null)))
                {
                    var r = MenuRenderer.FocusBox(b);
                    dc.DrawImage(Picture(MenuRenderer.Focus(chosen, page, b)), new Rect(r.X, r.Y, r.W, r.H));
                }
            }
            var bmp = new RenderTargetBitmap(MenuBuilder.W, MenuBuilder.H, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        var previewPages = MenuBuilder.Build(chosen, sample);
        foreach (var w in SafeArea.Warnings(previewPages, chosen)) Console.WriteLine(w);
        var files = MenuRenderer.Sta(() => previewPages.Select(page => (page.Id, Jpeg: MenuRenderer.Background(chosen, page) is var jpeg && a.Contains("--focus") ? Lit(page, jpeg) : jpeg)).ToList());
        if (a.Contains("--safe-area")) files = MenuRenderer.Sta(() => files.Select((f, i) => (f.Id, SafeArea.Over(f.Jpeg, previewPages[i], chosen))).ToList());
        foreach (var (id, jpeg) in files) File.WriteAllBytes(Path.Combine(a[2], $"{chosen.Name}_{id}.jpg"), jpeg);
        Console.WriteLine(string.Join(", ", files.Select(f => f.Id)));
        return 0;
    }

    public static int RunDump(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        var chosen = TemplateCatalog.Find(a[1]) ?? throw new InvalidOperationException($"unknown template: {a[1]}");
        var sample = new MenuContent(1, null, Enumerable.Repeat<string?>(null, 7).ToList(), null, ["Français AC-3 mono", "English AC-3 mono"], ["Français"]);
        foreach (var page in MenuBuilder.Build(chosen, sample))
        {
            Console.WriteLine($"== {page.Id}");
            foreach (var op in page.Ops) Console.WriteLine("  " + op);
        }
        return 0;
    }

    public static int RunMediaMaker(List<string> a)
    {
        if (a.Count < 3) return CliBuild.Usage();
        var made = Path.GetFullPath(a[1]);
        var music = CliBuild.OptAll(a, "--music").Select(Path.GetFullPath).ToList();
        if (a[0] == "slideshow")
        {
            var pictures = a.Skip(2).Where((x, i) => !x.StartsWith("--") && !(i > 0 && a[i + 1] is "--seconds" or "--music" or "--transition")).ToList();
            MediaMaker.Slideshow(pictures.Select(Path.GetFullPath).ToList(),
                double.TryParse(CliBuild.Opt(a, "--seconds"), CultureInfo.InvariantCulture, out var each) ? each : 5,
                music, made, Console.WriteLine,
                CliBuild.Opt(a, "--transition") is { } tr ? MediaMaker.TransitionOf(tr) : null);
        }
        else if (a[0] == "join") MediaMaker.Join(a.Skip(2).Select(Path.GetFullPath).ToList(), made, Console.WriteLine);
        else MediaMaker.Song(Path.GetFullPath(a[2]), CliBuild.Opt(a, "--cover") is { } c ? Path.GetFullPath(c) : MediaMaker.CoverOf(a[2], Path.GetDirectoryName(made)!), made, Console.WriteLine);
        Console.WriteLine("ok   " + made);
        return 0;
    }

    public static int RunCheckVectors(List<string> a)
    {
        var vecRoot = a.Skip(1).Where((x, i) => !x.StartsWith("--") && !a[i].StartsWith("--")).FirstOrDefault()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BurnSmith", "Templates", "sources");
        var vecMax = a.IndexOf("--max") is var at and >= 0 && at + 1 < a.Count ? double.Parse(a[at + 1], CultureInfo.InvariantCulture) : 100;
        var vecWrite = a.IndexOf("--write") is var wr and >= 0 && wr + 1 < a.Count ? a[wr + 1] : null;
        var vecAlone = Directory.CreateTempSubdirectory("burnsmith-vec").FullName;
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
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, drawn, w * 4)));
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
        foreach (var r in rows) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{r.Off,6:F2} %  {r.Mean,6:F2}  {Path.GetRelativePath(vecRoot, r.Svg)}"));
        Directory.Delete(vecAlone, true);
        var over = rows.Count(r => r.Off > vecMax);
        Console.WriteLine($"{rows.Count} tracés, {over} au-delà de {vecMax} %");
        return over == 0 ? 0 : 1;
    }

    public static int RunSvgSheet(List<string> a)
    {
        if (a.Count < 3) return CliBuild.Usage();
        var files = Directory.EnumerateFiles(a[1], "*.svg").Where(f => !f.EndsWith(".sub.svg", StringComparison.OrdinalIgnoreCase)).Order().ToList();
        var png = MenuRenderer.Sta(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(70, 70, 80)), null, new Rect(0, 0, 1000, files.Count * 130));
                for (var i = 0; i < files.Count; i++)
                {
                    var sub = Path.ChangeExtension(files[i], ".sub.svg");
                    var x = 10.0;
                    foreach (var (file, w, h) in new[] { (files[i], 300, 90), (files[i], 120, 120), (sub, 300, 90), (sub, 120, 120) })
                    {
                        if (File.Exists(file))
                        {
                            var r = MenuRenderer.Raster(file, null, w, h);
                            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, r.Px, w * 4);
                            dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(x, i * 130 + 5, w, h));
                            dc.DrawImage(bmp, new Rect(x, i * 130 + 5, w, h));
                        }
                        x += w + 15;
                    }
                    dc.DrawText(new FormattedText(Path.GetFileNameWithoutExtension(files[i]), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 13, Brushes.White, 1), new Point(10, i * 130 + 108));
                }
            }
            var target = new RenderTargetBitmap(1000, files.Count * 130, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(target));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        });
        File.WriteAllBytes(a[2], png);
        Console.WriteLine($"{files.Count} SVG -> {a[2]}");
        return 0;
    }

    public static int RunPackTemplates(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        Console.WriteLine($"{TemplateCatalog.Pack(Path.GetFullPath(a[1]), Console.WriteLine)} templates packed");
        return 0;
    }

    public static int RunConvertTemplates(List<string> a)
    {
        var packRootOut = CliBuild.Opt(a, "--out") is { } packOut ? Path.GetFullPath(packOut) : null;
        var result = TemplatePack.ConvertAll(Console.WriteLine, packRootOut, a.Contains("--force"));
        Console.WriteLine($"{result.Converted} converted, {result.Skipped} kept, {result.Failed} failed");
        foreach (var message in result.Messages) Console.Error.WriteLine(message);
        return result.Failed == 0 ? 0 : 1;
    }

    public static int RunCheckConversion()
    {
        var (packs, ok, packProblems) = TemplatePack.Verify();
        foreach (var problem in packProblems) Console.WriteLine($"FAIL {problem}");
        Console.WriteLine($"{ok}/{packs} packs without a problem");
        return packProblems.Count == 0 && packs > 0 ? 0 : 1;
    }

    public static int RunCheckLibrary()
    {
        var (libraryTemplates, _) = TemplateCatalog.Scan();
        var libraryBackgrounds = ElementLibrary.Backgrounds((libraryTemplates, []));
        var libraryButtons = ElementLibrary.Buttons((libraryTemplates, []));
        var buttonHashes = libraryButtons.Where(b => b.Normal != null)
            .Select(b => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(b.Normal!))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var libraryProblems = 0;
        var packButtonPictures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in TemplatePack.Scan())
        {
            foreach (var asset in pack.Assets.Where(x => x.Role == "background"))
            {
                var file = Path.Combine(pack.Dir, asset.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) continue;
                if (libraryBackgrounds.Any(b => b.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
                Console.WriteLine($"FAIL fond absent de la bibliothèque : {pack.Category} | {pack.Name} -> {asset.Path}");
                libraryProblems++;
            }
            foreach (var asset in pack.Assets.Where(x => x.Role == "button"))
            {
                var file = Path.Combine(pack.Dir, asset.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file) || MenuRenderer.Size(file) == null) continue;
                if (asset.Focus is { } focusRelative)
                {
                    var focusFile = Path.Combine(pack.Dir, focusRelative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(focusFile) || MenuRenderer.Size(focusFile) == null) continue;
                }
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                if (!packButtonPictures.Add(hash) || buttonHashes.Contains(hash)) continue;
                Console.WriteLine($"FAIL bouton absent de la bibliothèque : {pack.Category} | {pack.Name} -> {asset.Path}");
                libraryProblems++;
            }
        }
        Console.WriteLine($"{libraryBackgrounds.Count} fonds, {libraryButtons.Count} boutons, {libraryProblems} problème(s)");
        var pictureButtonsNeeded = packButtonPictures.Count > 0;
        var pictureButtonsOffered = libraryButtons.Any(b => b.Source is not (ElementLibrary.Styles or ElementLibrary.DvdStyler or ElementLibrary.MyButtons) && !b.Source.StartsWith(TemplateLabel.Own + " · ", StringComparison.Ordinal));
        return libraryProblems == 0 && libraryBackgrounds.Count > 0 && pictureButtonsNeeded == pictureButtonsOffered ? 0 : 1;
    }

    public static int RunTemplates(List<string> a)
    {
        var (usable, skipped) = TemplateCatalog.Scan();
        foreach (var (flag, on) in new[] { ("--star", true), ("--unstar", false) })
            if (CliBuild.Opt(a, flag) is { } starred)
            {
                if (usable.FirstOrDefault(f => f.Design.Name.Equals(starred, StringComparison.OrdinalIgnoreCase) || f.Label.Equals(starred, StringComparison.OrdinalIgnoreCase)) is not { } hit)
                { Console.Error.WriteLine($"unknown template: {starred} (burnsmith templates lists them)"); return 2; }
                TemplatePicks.Star(hit.Design.Name, on);
            }
        var text = a.Count > 1 && !a[1].StartsWith("--") ? a[1] : null;
        var use = CliBuild.Opt(a, "--use");
        if (use != null && !TemplateUsage.All.Any(u => TemplatePicks.Contains(u, use))) { Console.Error.WriteLine($"unknown use: {use} ({string.Join(", ", TemplateUsage.All)})"); return 2; }
        var filtered = text != null || use != null;
        foreach (var f in TemplatePicks.Sorted(usable.Where(f => TemplatePicks.Matches(f, text) && (use == null || TemplatePicks.Contains(f.Usage, use)))))
            Console.WriteLine($"OK    {f.Design.Category} | {f.Design.Name} | {f.Usage}{(f.Favorite ? " | favori" : "")}");
        foreach (var mine in Templates.List().Where(m => use == null && (text == null || TemplatePicks.Contains(m.Name, text)))) Console.WriteLine($"OK    Mes modèles | {mine.Name}");
        foreach (var s in skipped.Where(_ => !filtered)) Console.WriteLine($"SKIP  {s.Name} | {s.Why} | {s.Dir}");
        foreach (var r in TemplatePicks.Recent()) Console.WriteLine($"RECENT {r}");
        Console.WriteLine($"{usable.Count} usable, {skipped.Count} skipped");
        return 0;
    }

    public static int RunDesignExport(List<string> a)
    {
        if (a.Count < 3) return CliBuild.Usage();
        var named = a[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(a[1])) : TemplateCatalog.Find(a[1]);
        if (named == null) { Console.Error.WriteLine($"unknown template: {a[1]} (burnsmith templates lists them)"); return 2; }
        File.WriteAllText(a[2], JsonSerializer.Serialize(named.Clone(), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"template '{a[1]}' written to {a[2]}");
        return 0;
    }

    public static int RunTemplateSave(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        var kept = a[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(a[1]) ? JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(a[1])) : TemplateCatalog.Find(a[1])?.Clone();
        if (kept == null) { Console.Error.WriteLine($"unknown template: {a[1]} (burnsmith templates lists them)"); return 2; }
        if (a.Count >= 3) kept.Name = a[2];
        Templates.Save(kept);
        Console.WriteLine($"template '{kept.Name}' saved in {Templates.Dir}");
        return 0;
    }

    public static int RunImportAsset(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        try
        {
            if (a[0] == "import-background") foreach (var picture in a.Skip(1)) Console.WriteLine(ElementLibrary.Import(Path.GetFullPath(picture)).Path);
            else if (ElementLibrary.ImportButton(Path.GetFullPath(a[1]), a.Count >= 3 ? Path.GetFullPath(a[2]) : null) is var button)
                Console.WriteLine(button.Normal + (button.Focus == null ? "" : Environment.NewLine + button.Focus));
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        return 0;
    }

    public static int RunLabelFromScan(List<string> a)
    {
        if (a.Count < 3) return CliBuild.Usage();
        double Mm(string name, string fallback) => double.Parse(CliBuild.Opt(a, name) ?? fallback, CultureInfo.InvariantCulture);
        var lfs = DiscLabel.Make(a[1], a[2], Mm("--diameter-mm", "118"), Mm("--dpi", "300"), CliBuild.Opt(a, "--hole-mm") is null or "auto" ? null : Mm("--hole-mm", "0"), CliBuild.Opt(a, "--debug"));
        Console.WriteLine($"centre {lfs.CenterX:0.0}, {lfs.CenterY:0.0} ({(lfs.OnHub ? $"moyeu, rayon {lfs.HubRadius:0.0} px" : "bord extérieur")}) ; bord extérieur {lfs.OuterX:0.0}, {lfs.OuterY:0.0} rayon {lfs.OuterRadius:0.0} px ; trou rayon {lfs.HoleRadius:0} px ; {lfs.ScanDpi:0} ppp");
        foreach (var note in lfs.Notes) Console.WriteLine("attention : " + note);
        return 0;
    }

    public static int RunHoldTool(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc=s=64x64:r=10:d=60", "-progress", a[1], "-f", "null", "-"], outlivesApp: a.Contains("--free"));
        return 0;
    }
}
