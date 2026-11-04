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
    public static int Run(string? picture)
    {
        var (usable, skipped) = TemplateCatalog.Scan();
        string[] audio = ["Français AC-3 5.1", "English AC-3 5.1", "Español AC-3 mono"], subs = ["Français", "English"];
        var film = new MenuContent(1, picture, Enumerable.Repeat(picture, 9).ToList(), null, audio, subs);
        var series = new MenuContent(1, picture, Enumerable.Repeat(picture, 3).ToList(), Enumerable.Range(1, 14).Select(i => new TitleEntry($"Episode {i}", i, picture)).ToList(), audio, subs);
        var failed = 0;
        foreach (var f in usable)
        {
            var issues = new List<string>();
            foreach (var (label, content) in new[] { ("film", film), ("série", series) })
                try { Check(f.Design, content, label, issues); }
                catch (Exception e) { issues.Add($"{label}: exception {e.GetType().Name}: {e.Message}"); }
            // overlapping buttons are only reported: skewed thumbnails, arrows over a carousel and DVD star slots overlap by design
            var notes = issues.Where(i => i.Contains("se chevauchent")).Distinct().ToList();
            var errors = issues.Except(notes).Distinct().ToList();
            Console.WriteLine($"{(errors.Count == 0 ? "OK  " : "FAIL")} {f.Design.Category} | {f.Design.Name}{(errors.Count == 0 ? "" : " -> " + string.Join(" ; ", errors.Take(6)))}{(notes.Count == 0 ? "" : $"   [à voir : {notes.Count} chevauchement(s), ex. {notes[0]}]")}");
            if (errors.Count > 0) failed++;
        }
        Console.WriteLine($"{usable.Count - failed} sans problème, {failed} avec problèmes, {skipped.Count} refusés");
        return failed == 0 ? 0 : 1;
    }

    static IEnumerable<string> Files(Op op) => op switch
    {
        Image i => [i.Path],
        Picture p => [p.Path],
        Framed f => new[] { f.Picture, f.Frame, f.Mask }.Where(s => s != null).Select(s => s!),
        _ => [],
    };

    static void Check(MenuDesign design, MenuContent c, string label, List<string> issues)
    {
        var pages = MenuBuilder.Build(design, c);
        if (pages.Count == 0 || pages[0].Id != "root") { issues.Add($"{label}: pas de page racine"); return; }
        var ids = pages.Select(p => p.Id).ToHashSet();
        var cells = c.Series ? c.Titles!.Count : c.ChapterPictures.Count;
        var cellButtons = 0;
        if (!pages[0].Buttons.Any(b => b.Action.Kind is "play" or "playall")) issues.Add($"{label}: pas de bouton lecture sur la page racine");
        if (c.HasSettings && !ids.Contains("settings")) issues.Add($"{label}: pas de page de réglages");
        if (cells > 1 && !ids.Contains("scenes1")) issues.Add($"{label}: pas de page de scènes");

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
                if (p.Id.StartsWith("scenes") && b.Action.Kind is "play" or "chapter") cellButtons++;
                if (b.FocusImage != null && !File.Exists(b.FocusImage)) issues.Add($"{label}/{p.Id}/{b.Id}: image de sélection introuvable");
            }
            // remote control: every button reachable from the first one
            var seen = new HashSet<string> { p.Buttons[0].Id };
            var todo = new Queue<MenuButton>([p.Buttons[0]]);
            while (todo.Count > 0)
                foreach (var next in new[] { todo.Dequeue() }.SelectMany(b => new[] { b.Left, b.Right, b.Up, b.Down }).Where(n => n != null && seen.Add(n)))
                    todo.Enqueue(p.Buttons.First(b => b.Id == next));
            if (seen.Count != p.Buttons.Count) issues.Add($"{label}/{p.Id}: {p.Buttons.Count - seen.Count} bouton(s) hors d'atteinte de la télécommande");
            foreach (var file in p.Ops.SelectMany(Files).Where(f => !File.Exists(f)).Distinct()) issues.Add($"{label}/{p.Id}: image introuvable {Path.GetFileName(file)}");
        }
        if (cells > 1 && cellButtons != cells) issues.Add($"{label}: {cellButtons} boutons de scène pour {cells} chapitres / épisodes");

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
    }
}
