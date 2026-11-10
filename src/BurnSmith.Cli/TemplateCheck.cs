using System.Windows.Media;
using System.Windows.Media.Imaging;
using BurnSmith.Core.Menu;

/// <summary>
/// burnsmith check-templates [--picture frame.jpg]: builds the menu of every template found here, as a film (9 chapters) and as a
/// series (14 episodes), with 3 audio tracks and 2 subtitles, and checks what can go wrong without a player: pages exist, every button is
/// on the screen, remote-control moves reach every button, every page can be reached from the root and leads back to it, every picture
/// the page draws exists, every scene / episode has its button, and the rendered pages are neither black nor blank.
/// </summary>
static class TemplateCheck
{
    public static int Run(string? picture, bool matrix = false, string? only = null)
    {
        var (usable, skipped) = TemplateCatalog.Scan();
        var designs = usable.Where(f => only == null || f.Design.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).Select(f => (f.Design, Composed: false)).ToList();
        if (matrix && only == null) designs.AddRange(ComposedDesigns().Select(d => (d, true)));
        var failed = 0;
        // the built-in and open-library templates are in git, so even a machine with no installed template has them: without them nothing, or little, would be checked
        if (designs.Count == 0 || only == null && !(usable.Any(f => f.Design.Kind == TemplateKind.BuiltIn) && usable.Any(f => f.Design.Kind == TemplateKind.Library)))
        {
            Console.WriteLine($"FAIL {designs.Count} modèles trouvés, il manque les modèles intégrés ou ceux de la bibliothèque libre, ou --only ne désigne rien");
            return 1;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (design, composed) in designs)
        {
            var issues = new List<string>();
            var shapes = matrix ? Shapes(picture).ToList() : Basic(picture).ToList();
            // matrix: every design twice, the second time with a credits page and as a moving menu (transparent overlay pages)
            foreach (var variant in matrix ? new[] { design, Credited(design) } : [design])
                foreach (var (shape, content, render) in shapes)
                {
                    var label = variant == design ? shape : "crédits " + shape;
                    // composed designs are drawn only for 13 chapters / 14 episodes: their pages share one layout code
                    try { Check(variant, content, label, issues, render && (!composed || shape.StartsWith("film13") || shape.StartsWith("série14"))); }
                    catch (Exception e) { issues.Add($"{label}: exception {e.GetType().Name}: {e.Message}"); }
                }
            // overlapping buttons are only reported: skewed thumbnails, arrows over a carousel and DVD star slots overlap by design
            var notes = issues.Where(i => i.Contains("se chevauchent")).Distinct().ToList();
            var errors = issues.Except(notes).Distinct().ToList();
            if (errors.Count > 0) failed++;
            if (composed && errors.Count == 0) continue; // 200 composed designs: only the failures are worth a line
            Console.WriteLine($"{(errors.Count == 0 ? "OK  " : "FAIL")} {design.Category} | {design.Name}{(errors.Count == 0 ? "" : " -> " + string.Join(" ; ", errors.Take(6)))}{(notes.Count == 0 ? "" : $"   [à voir : {notes.Count} chevauchement(s), ex. {notes[0]}]")}");
        }
        Console.WriteLine($"{designs.Count - failed} sans problème, {failed} avec problèmes, {skipped.Count} refusés{(matrix ? $", {designs.Count} modèles x {Shapes(picture).Count()} formes de disque en {watch.Elapsed.TotalSeconds:0} s" : "")}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// burnsmith check-fidelity [--only NAME]: every source template is converted to a pack in a temporary folder, then drawn from its own files
    /// and from that pack: same pages, same buttons and moves, same pixels for every page and every selected button.
    /// </summary>
    public static int Fidelity(string? picture, string? only)
    {
        var work = Path.Combine(Path.GetTempPath(), "burnsmith-fidelity-" + Guid.NewGuid().ToString("N"));
        var (count, failed) = (0, 0);
        try
        {
            foreach (var source in TemplateCatalog.Scan(includePacks: false).Usable.Select(f => f.Design)
                         .Where(d => d.TemplateDir != null && (only == null || d.Name.Contains(only, StringComparison.OrdinalIgnoreCase))))
            {
                var issues = new List<string>();
                try
                {
                    var dir = Path.Combine(work, (++count).ToString());
                    Directory.CreateDirectory(dir);
                    TemplatePack.ConvertOne(source.Name, source.Category, source.TemplateDir!, source, dir).Save(dir);
                    var pack = TemplatePack.TryRead(dir) ?? throw new InvalidDataException("paquet illisible après conversion");
                    if (!pack.HasLayout) throw new InvalidDataException("paquet sans mise en page");
                    issues.AddRange(pack.Problems());
                    var converted = pack.Design.Clone(); // as TemplateCatalog.Scan makes it
                    (converted.Kind, converted.TemplateDir) = (pack.KindValue, dir);
                    foreach (var (shape, content, _) in Basic(picture).Take(2))
                    {
                        var (a, b) = (MenuBuilder.Build(source, content), MenuBuilder.Build(converted, content));
                        if (!a.Select(Shape).SequenceEqual(b.Select(Shape)))
                        {
                            var i = Enumerable.Range(0, Math.Min(a.Count, b.Count)).FirstOrDefault(i => Shape(a[i]) != Shape(b[i]), Math.Min(a.Count, b.Count));
                            issues.Add($"{shape}: pages différentes ({a.Count} / {b.Count}), première : {(i < a.Count ? a[i].Id : b[i].Id)}");
                            continue;
                        }
                        var differ = MenuRenderer.Sta(() => a.Zip(b).SelectMany(p => new[] { (p.First.Id, Same: MenuRenderer.Background(source, p.First).AsSpan().SequenceEqual(MenuRenderer.Background(converted, p.Second))) }
                            .Concat(p.First.Buttons.Zip(p.Second.Buttons).Select(q => (Id: $"{p.First.Id}/{q.First.Id} sélectionné", Same: MenuRenderer.Focus(source, q.First).AsSpan().SequenceEqual(MenuRenderer.Focus(converted, q.Second))))))
                            .Where(r => !r.Same).Select(r => r.Id).ToList());
                        if (differ.Count > 0) issues.Add($"{shape}: {differ.Count} image(s) différente(s), ex. {string.Join(", ", differ.Take(3))}");
                    }
                }
                catch (Exception e) { issues.Add($"exception {e.GetType().Name}: {e.Message}"); }
                if (issues.Count > 0) failed++;
                Console.WriteLine($"{(issues.Count == 0 ? "OK  " : "FAIL")} {source.Category} | {source.Name}{(issues.Count == 0 ? "" : " -> " + string.Join(" ; ", issues))}");
            }
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        Console.WriteLine($"{count - failed} fidèles, {failed} différents");
        return failed == 0 && count > 0 ? 0 : 1;
    }

    /// <summary>What a page is apart from its picture files: its drawing steps by kind, its buttons, where they go and which have artwork of their own.</summary>
    static string Shape(MenuPage p) => p.Id + " " + string.Join(",", p.Ops.Select(o => o.GetType().Name)) + " " + string.Join(";", p.Buttons.Select(b =>
        $"{b.Id} {b.Rect} {b.Action} {b.Left} {b.Right} {b.Up} {b.Down} {b.FocusSlot} {b.FocusImage != null}{b.DvdHighlightImage != null}{b.DvdHighlightMask != null}{b.DvdSelectImage != null}"));

    static MenuDesign Credited(MenuDesign d)
    {
        var c = d.Clone();
        c.Credits = "Un film de Marc\nMusique : Clara D.\nMerci à toute la famille";
        c.Loop = true;
        return c;
    }

    static readonly string[] Langs = ["Français", "English", "Español", "Deutsch", "Italiano", "Português", "Nederlands", "Svenska", "日本語"];

    /// <summary>A film of 9 chapters and a series of 14 episodes, 3 audio tracks and 2 subtitles.</summary>
    static IEnumerable<(string, MenuContent, bool)> Basic(string? picture)
    {
        yield return ("film", Film(picture, 9, 3, 2), true);
        yield return ("série", Series(picture, 14, 3, 2), true);
        yield return ("film + 3 bonus", Film(picture, 9, 3, 2, 3), true);
        yield return ("série + 7 bonus", Series(picture, 14, 3, 2, 7), true);
    }

    /// <summary>Every kind of disc: film of 1, 2, 7, 13 or 40 chapters, series of 2, 14 or 60 episodes, each with 1..9 audio and 0..9 subtitle tracks.</summary>
    static IEnumerable<(string, MenuContent, bool)> Shapes(string? picture)
    {
        foreach (var (a, s) in new[] { (1, 0), (1, 1), (2, 0), (3, 2), (9, 9) })
        {
            foreach (var n in new[] { 1, 2, 7, 13, 40 }) yield return ($"film{n} a{a}s{s}", Film(picture, n, a, s), a == 3);
            foreach (var n in new[] { 2, 14, 60 }) yield return ($"série{n} a{a}s{s}", Series(picture, n, a, s), a == 3);
        }
        // extras: a few, a single one, more than a page of them (3x2 grids hold 6, 1x1 grids one), with and without anything else to choose
        foreach (var (name, content) in new[]
        {
            ("film13 +3 bonus a3s2", Film(picture, 13, 3, 2, 3)), ("film1 +1 bonus a1s0", Film(picture, 1, 1, 0, 1)), ("film2 +13 bonus a2s0", Film(picture, 2, 2, 0, 13)),
            ("série14 +7 bonus a3s2", Series(picture, 14, 3, 2, 7)), ("série2 +1 bonus a1s0", Series(picture, 2, 1, 0, 1)), ("série60 +20 bonus a9s9", Series(picture, 60, 9, 9, 20)),
        })
            yield return (name, content, name.StartsWith("film13") || name.StartsWith("série14"));
    }

    /// <summary>The extras' playlists come after the feature's (a film is playlist 1, the episodes 1..n).</summary>
    static List<TitleEntry>? Extras(string? picture, int first, int count) =>
        count == 0 ? null : Enumerable.Range(0, count).Select(i => new TitleEntry($"Bonus {i + 1}", first + i, picture)).ToList();

    static MenuContent Film(string? picture, int chapters, int audio, int subs, int bonus = 0) =>
        new(1, picture, Enumerable.Repeat(picture, chapters).ToList(), null, Langs.Take(audio).Select(l => l + " AC-3 5.1").ToList(), Langs.Take(subs).ToList(), Extras(picture, 2, bonus));

    static MenuContent Series(string? picture, int episodes, int audio, int subs, int bonus = 0) =>
        new(1, picture, Enumerable.Repeat(picture, 3).ToList(), Enumerable.Range(1, episodes).Select(i => new TitleEntry($"Episode {i}", i, picture)).ToList(),
            Langs.Take(audio).Select(l => l + " AC-3 5.1").ToList(), Langs.Take(subs).ToList(), Extras(picture, episodes + 1, bonus));

    /// <summary>What the menu editor can make without a template: 5 button styles x 5 grids x thumbnails x square corners x preview.</summary>
    static IEnumerable<MenuDesign> ComposedDesigns()
    {
        foreach (var style in new[] { "Pill", "Text", "Card", "Frame", "Glass" })
            foreach (var (cols, rows) in new[] { (1, 1), (2, 1), (3, 2), (4, 3), (6, 4) })
                foreach (var thumbs in new[] { true, false })
                    foreach (var square in new[] { false, true })
                        foreach (var preview in new[] { true, false })
                            yield return new MenuDesign
                            {
                                Name = $"{style} {cols}x{rows}{(thumbs ? "" : " sans image")}{(square ? " carré" : "")}{(preview ? "" : " sans aperçu")}",
                                Category = TemplateLabel.Open, ButtonStyle = style, Cols = cols, Rows = rows, Thumbnails = thumbs, Square = square, ShowPreview = preview,
                            };
    }

    static IEnumerable<string> Files(Op op) => op switch
    {
        Image i => [i.Path],
        Picture p => [p.Path],
        Framed f => new[] { f.Picture, f.Frame, f.Mask }.Where(s => s != null).Select(s => s!),
        _ => [],
    };

    static void Check(MenuDesign design, MenuContent c, string label, List<string> issues, bool render)
    {
        var pages = MenuBuilder.Build(design, c);
        if (pages.Count == 0 || pages[0].Id != "root") { issues.Add($"{label}: pas de page racine"); return; }
        var ids = pages.Select(p => p.Id).ToHashSet();
        var cells = c.Series ? c.Titles!.Count : c.ChapterPictures.Count;
        var cellButtons = 0;
        var bonusButtons = new List<int>();
        if (pages.Select(p => p.Id).Distinct().Count() != pages.Count) issues.Add($"{label}: identifiants de pages en double");
        if (!pages[0].Buttons.Any(b => b.Action.Kind is "play" or "playall")) issues.Add($"{label}: pas de bouton lecture sur la page racine");
        // the extras: a button to their page, and a page of buttons for them (never part of "play all", which covers the feature's playlists only)
        if (c.HasBonus && !pages.Any(p => p.Id != "bonus1" && p.Buttons.Any(b => b.Id == "bonus" && b.Action is { Kind: "page", Page: "bonus1" }))) issues.Add($"{label}: pas de bouton Bonus");
        if (!c.HasBonus && (ids.Any(i => i.StartsWith("bonus")) || pages.Any(p => p.Buttons.Any(b => b.Id == "bonus")))) issues.Add($"{label}: une page ou un bouton Bonus sans bonus");
        foreach (var all in pages.SelectMany(p => p.Buttons).Where(b => b.Action.Kind == "playall"))
            if (c.Series && (all.Action.Playlist != c.Titles![0].Playlist || all.Action.Mark != c.Titles.Count)) issues.Add($"{label}: « tout lire » ne couvre pas exactement les épisodes");
        if (c.HasSettings && (MenuBuilder.SettingsIds(design, c) is not { Count: > 0 } wanted || !wanted.All(ids.Contains))) issues.Add($"{label}: pas de page de réglages");
        if (design.CreditLines().Count > 0 && (!ids.Contains("credits") || !pages[0].Buttons.Any(b => b.Id == "credits"))) issues.Add($"{label}: pas de page ou de bouton crédits");
        // a layout with no menu of its own has its first page of thumbnails as the root page
        if (cells > 1 && !ids.Contains("scenes1") && !pages[0].Buttons.Any(b => b.Id != "play" && b.Action.Kind is "play" or "chapter")) issues.Add($"{label}: pas de page de scènes");

        foreach (var p in pages)
        {
            if (p.Buttons.Count == 0) { issues.Add($"{label}/{p.Id}: aucun bouton"); continue; }
            var buttonIds = p.Buttons.Select(b => b.Id).ToHashSet();
            if (buttonIds.Count != p.Buttons.Count) issues.Add($"{label}/{p.Id}: identifiants de boutons en double");
            for (var i = 0; i < p.Buttons.Count; i++)
                for (var j = i + 1; j < p.Buttons.Count; j++)
                {
                    Box a = p.Buttons[i].Rect, o = p.Buttons[j].Rect;
                    long w = Math.Min(a.X + a.W, o.X + o.W) - Math.Max(a.X, o.X), h = Math.Min(a.Y + a.H, o.Y + o.H) - Math.Max(a.Y, o.Y);
                    if (w > 0 && h > 0 && w * h > 0.15 * Math.Min((long)a.W * a.H, (long)o.W * o.H)) issues.Add($"{label}/{p.Id}: les boutons {p.Buttons[i].Id} et {p.Buttons[j].Id} se chevauchent ({a.X},{a.Y} {a.W}x{a.H} / {o.X},{o.Y} {o.W}x{o.H})");
                }
            foreach (var b in p.Buttons)
            {
                var r = b.Rect;
                if (r.W < 8 || r.H < 8 || r.X < -10 || r.Y < -10 || r.X + r.W > MenuBuilder.W + 10 || r.Y + r.H > MenuBuilder.H + 10) issues.Add($"{label}/{p.Id}/{b.Id}: bouton hors de l'écran ou trop petit ({r.X},{r.Y} {r.W}x{r.H})");
                foreach (var n in new[] { b.Left, b.Right, b.Up, b.Down })
                    if (n != null && !buttonIds.Contains(n)) issues.Add($"{label}/{p.Id}/{b.Id}: déplacement vers un bouton inconnu ({n})");
                if (b.Action.Kind == "page" && (b.Action.Page == null || !ids.Contains(b.Action.Page))) issues.Add($"{label}/{p.Id}/{b.Id}: mène à une page inconnue ({b.Action.Page})");
                if ((p.Id.StartsWith("scenes") || (p.Id == "root" && b.Id != "play")) && b.Action.Kind is "play" or "chapter") cellButtons++;
                if (p.Id.StartsWith("bonus") && b.Action.Kind == "play") bonusButtons.Add(b.Action.Playlist);
                if (b.FocusImage != null && !File.Exists(b.FocusImage)) issues.Add($"{label}/{p.Id}/{b.Id}: image de sélection introuvable");
            }
            // remote control: every button reachable from the first one
            var seen = new HashSet<string> { p.Buttons[0].Id };
            var todo = new Queue<MenuButton>([p.Buttons[0]]);
            while (todo.Count > 0)
                foreach (var next in new[] { todo.Dequeue() }.SelectMany(b => new[] { b.Left, b.Right, b.Up, b.Down }).Where(n => n != null && seen.Add(n)))
                    todo.Enqueue(p.Buttons.First(b => b.Id == next));
            if (seen.Count != p.Buttons.Count) issues.Add($"{label}/{p.Id}: {p.Buttons.Count - seen.Count} bouton(s) hors d'atteinte de la télécommande ({string.Join(',', p.Buttons.Select(b => b.Id).Where(id => !seen.Contains(id)))})");
            foreach (var file in p.Ops.SelectMany(Files).Where(f => !File.Exists(f)).Distinct()) issues.Add($"{label}/{p.Id}: image introuvable {Path.GetFileName(file)}");
        }
        if (cells > 1 && cellButtons != cells) issues.Add($"{label}: {cellButtons} boutons de scène pour {cells} chapitres / épisodes");
        if (!bonusButtons.Order().SequenceEqual((c.Bonus ?? []).Select(t => t.Playlist).Order())) issues.Add($"{label}: les boutons de la page Bonus ne jouent pas exactement les {c.Bonus?.Count ?? 0} bonus");

        // page graph: all pages reachable from the root, the root reachable from all pages
        var links = pages.ToDictionary(p => p.Id, p => p.Buttons.Where(b => b.Action.Kind == "page" && b.Action.Page != null).Select(b => b.Action.Page!).ToHashSet());
        HashSet<string> Reach(string from)
        {
            var seen = new HashSet<string> { from };
            var q = new Queue<string>([from]);
            while (q.Count > 0) foreach (var n in links.GetValueOrDefault(q.Dequeue()) ?? []) if (seen.Add(n)) q.Enqueue(n);
            return seen;
        }
        if (ids.Except(Reach("root")).ToList() is { Count: > 0 } lost) issues.Add($"{label}: page(s) inaccessible(s) depuis la racine : {string.Join(",", lost)}");
        foreach (var p in pages.Skip(1)) if (!Reach(p.Id).Contains("root")) issues.Add($"{label}/{p.Id}: pas de retour vers la racine");

        // the rendered pages: not black, not one flat colour
        if (!render) return;
        foreach (var (id, jpeg) in MenuRenderer.Sta(() => pages.Select(p => (p.Id, Jpeg: MenuRenderer.Background(design, p))).ToList()))
        {
            var frame = new FormatConvertedBitmap(new JpegBitmapDecoder(new MemoryStream(jpeg), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Gray8, null, 0);
            var px = new byte[frame.PixelWidth * frame.PixelHeight];
            frame.CopyPixels(px, frame.PixelWidth, 0);
            var mean = px.Average(v => (double)v);
            var deviation = Math.Sqrt(px.Average(v => (v - mean) * (v - mean)));
            if (mean < 6) issues.Add($"{label}/{id}: page noire");
            else if (deviation < 4) issues.Add($"{label}/{id}: page unie (écart {deviation:0.0})");
        }
        if (!design.Loop) return;
        // moving menu: the overlay must hold something (text, buttons) and must not be opaque everywhere (the video has to show through)
        foreach (var (id, png) in MenuRenderer.Sta(() => pages.Select(p => (p.Id, Png: MenuRenderer.Overlay(design, p))).ToList()))
        {
            var frame = new FormatConvertedBitmap(new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
            var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            frame.CopyPixels(px, frame.PixelWidth * 4, 0);
            var opaque = Enumerable.Range(0, px.Length / 4).Count(i => px[i * 4 + 3] > 240) / (double)(px.Length / 4);
            var any = Enumerable.Range(0, px.Length / 4).Any(i => px[i * 4 + 3] > 16);
            if (!any) issues.Add($"{label}/{id}: calque du menu animé vide");
            else if (opaque > 0.85) issues.Add($"{label}/{id}: calque du menu animé opaque à {opaque:P0} (la vidéo ne se verrait pas)");
        }
    }
}
