using System.Globalization;
using System.Text.RegularExpressions;

namespace BurnSmith.Core.Menu;

/// <summary>
/// DVDStyler menu templates (<c>.dvdt</c>, GPL; shipped in the exe with their notices) translated into the template tree
/// <see cref="InstalledTemplate"/> draws: the background picture, the title and button text with the template's font, colour and position,
/// and the thumbnail positions of its scene page. DVDStyler draws its buttons as SVG snippets; BurnSmith draws plain text buttons and
/// thumbnails at the same places, so the result is close to the template, not identical.
/// </summary>
static partial class DvdStylerTemplate
{
    [GeneratedRegex(@"-?\d+(\.\d+)?")]
    private static partial Regex Number();

    static double Num(string? s) => s != null && Number().Match(s) is { Success: true } m ? double.Parse(m.Value, CultureInfo.InvariantCulture) : 0;

    static Dictionary<string, string> Css(string? style) =>
        (style ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(':', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim());

    static IEnumerable<(XNode Node, List<XNode> Parents)> Walk(XNode n, List<XNode>? parents = null)
    {
        parents ??= [];
        foreach (var kid in n.Kids)
        {
            yield return (kid, parents);
            foreach (var deeper in Walk(kid, [.. parents, kid])) yield return deeper;
        }
    }

    /// <summary>An empty tree that only carries the name, for a template that cannot be drawn (it is listed as unsupported).</summary>
    public static XNode Failed(string name)
    {
        var common = El("common");
        common.Kids.Add(Leaf("templatename", name));
        var t = El("template");
        t.Kids.Add(common);
        return t;
    }

    static XNode El(string name, params (string Key, string Value)[] attrs) => new(name, attrs.ToDictionary(a => a.Key, a => a.Value));
    static XNode Leaf(string name, string text) { var n = El(name); n.Content.Append(text); return n; }

    /// <summary>The template tree, or null when the file is not a DVDStyler template; <paramref name="problem"/> says why one cannot be drawn.</summary>
    public static XNode? Convert(XNode doc, string stem, out string? problem)
    {
        problem = null;
        if (doc.Child("dvdstyler") is not { } root) return null;
        var menus = Walk(root).Where(w => w.Node.Name == "menu").Select(w => w.Node).ToList();
        if (menus.Count < 1) { problem = "pas de menu dans le modèle"; return null; }
        var main = menus[0];
        var scenes = menus.Count > 1 ? menus[1] : null; // Wall, travel: one menu only, BurnSmith makes the scene page
        if (main.Child("svg") is not { } svg1) { problem = "modèle sans dessin de menu"; return null; }
        var svg2 = scenes?.Child("svg") ?? svg1;

        double w = Num(svg1.Attr("width")), h = Num(svg1.Attr("height"));
        if (w <= 0 || h <= 0) { problem = "taille de menu illisible"; return null; }
        var s = Math.Min(MenuBuilder.W / w, MenuBuilder.H / h); // 720x405 fills the canvas; 720x540 (4:3) is centred with bars
        double offX = (MenuBuilder.W - w * s) / 2, offY = (MenuBuilder.H - h * s) / 2;
        int X(double v) => (int)Math.Round(offX + v * s);
        int Y(double v) => (int)Math.Round(offY + v * s);
        int S(double v) => (int)Math.Round(v * s);
        string Rect(double x, double y, double rw, double rh) => $"{X(x)},{Y(y)},{S(rw)},{S(rh)}";

        XNode Background(XNode svg)
        {
            var bg = El("bg", ("rect", $"{X(0)},{Y(0)},{S(w)},{S(h)}"));
            var image = svg.Kids.FirstOrDefault(k => k.Name == "image" && k.Attr("id") == "background");
            if (image == null) return bg;
            bg.Kids.Add(Leaf("image", System.IO.Path.GetFileName(image.Attr("xlink:href"))));
            // SVG default: the picture is fitted inside the page keeping its proportions ("meet"), centred unless the template says otherwise
            var aspect = image.Attr("preserveAspectRatio").ToLowerInvariant();
            if (!aspect.Contains("none") && !aspect.Contains("slice")) bg.Attrs["fit"] = aspect.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "xmidymid";
            return bg;
        }

        // the "use" elements place the objects and buttons; their drawing is the matching svg of the defs
        List<(string Id, XNode Def, double X, double Y, double W, double H)> Placed(XNode menu, XNode svg) =>
            Walk(svg).Where(u => u.Node.Name == "use" && u.Node.Attr("id").StartsWith("button") || u.Node.Name == "use" && u.Node.Attr("id").StartsWith("obj"))
                .Select(u => u.Node)
                .Select(u => (Id: u.Attr("id"), Def: Walk(svg).Select(x => x.Node).FirstOrDefault(x => x.Name == "svg" && x.Attr("id") == u.Attr("xlink:href").TrimStart('#')),
                              X: Num(u.Attr("x")), Y: Num(u.Attr("y")), W: Num(u.Attr("width")), H: Num(u.Attr("height"))))
                .Where(t => t.Def != null && t.W > 0 && t.H > 0).Select(t => (t.Id, t.Def!, t.X, t.Y, t.W, t.H)).ToList();

        // font, colour, alignment of the first text of a drawing; the colour may sit on a parent group
        (string Font, double Size, string Color, bool Bold, bool Italic, bool Centre, double TextX)? TextStyle(XNode def)
        {
            foreach (var (node, parents) in Walk(def))
            {
                if (node.Name != "text") continue;
                var css = Css(node.Attr("style"));
                var fill = css.GetValueOrDefault("fill") ?? parents.AsEnumerable().Reverse().Select(p => Css(p.Attr("style")).GetValueOrDefault("fill")).FirstOrDefault(f => f != null) ?? "#FFFFFF";
                if (!fill.StartsWith('#') || fill.Length != 7) fill = "#FFFFFF";
                var font = css.GetValueOrDefault("font-family") ?? "Arial";
                if (font.Contains("Shell Dlg")) font = "Segoe UI"; // a Windows alias, not a font name WPF knows
                return (font.Trim('\'', '"'), Num(css.GetValueOrDefault("font-size")) is > 0 and var size ? size : 26, fill,
                        css.GetValueOrDefault("font-weight") == "bold", css.GetValueOrDefault("font-style") == "italic",
                        css.GetValueOrDefault("text-anchor") == "middle", node.Attr("x").EndsWith('%') ? 0 : Num(node.Attr("x")));
            }
            return null;
        }

        XNode Styled(string name, (string Font, double Size, string Color, bool Bold, bool Italic, bool Centre, double TextX) t, double x, double y, double bw, double bh, bool textButton)
        {
            var inset = t.Centre ? 0 : t.TextX;
            var node = El(name, ("rect", Rect(x + inset, y, bw - inset, bh)), ("font", t.Font), ("font_size", S(t.Size).ToString()), ("color", t.Color),
                ("bold", t.Bold ? "1" : "0"), ("italic", t.Italic ? "1" : "0"), ("align", t.Centre ? "center" : "left"), ("fit", "1"));
            if (textButton) node.Attrs["textbutton"] = "1";
            return node;
        }

        var placed1 = Placed(main, svg1);
        var common = El("common");
        var title = placed1.FirstOrDefault(p => p.Id.StartsWith("obj"));
        var rootNode = El("rootmenu");
        rootNode.Kids.Add(Background(svg1));
        var uiColor = "#FFFFFF";
        var uiFont = "Segoe UI";
        if (title.Def != null && TextStyle(title.Def) is { } ts)
        {
            uiColor = ts.Color;
            uiFont = ts.Font;
            rootNode.Kids.Add(Styled("label", ts, title.X, title.Y, title.W, title.H, false));
            common.Kids.Add(Leaf("font", ts.Font));
        }
        string[] roles = ["play", "scenes", "setup"];
        if (scenes == null)
        {
            // one menu only (Wall: "Play all" + title buttons; travel: "Play All" + three pictures): the play-all button is kept, the
            // "Scenes" button is BurnSmith's own, under it (above it when the page has no room), in the same style
            var playAll = Walk(main).Select(x => x.Node).Where(x => x.Name == "button" && x.Child("action")?.Attr("playAll") == "true").Select(x => x.Attr("id")).ToHashSet();
            var texts = placed1.Where(p => p.Id.StartsWith("button") && TextStyle(p.Def) != null).ToList();
            var play = texts.FirstOrDefault(p => playAll.Contains(p.Id));
            if (play.Def == null && texts.Count > 0) play = texts[0];
            if (play.Def != null && TextStyle(play.Def) is { } ps)
            {
                var below = play.Y + play.H + 12;
                rootNode.Kids.Add(Styled("play", ps, play.X, play.Y, play.W, play.H, true));
                rootNode.Kids.Add(Styled("scenes", ps, play.X, below + play.H <= h ? below : play.Y - play.H - 12, play.W, play.H, true));
                if (title.Def == null) { uiColor = ps.Color; uiFont = ps.Font; }
            }
        }
        else
        {
            var buttons1 = placed1.Where(p => p.Id.StartsWith("button")).Take(roles.Length).ToList();
            for (var i = 0; i < buttons1.Count; i++)
                if (TextStyle(buttons1[i].Def) is { } bs)
                {
                    rootNode.Kids.Add(Styled(roles[i], bs, buttons1[i].X, buttons1[i].Y, buttons1[i].W, buttons1[i].H, true));
                    if (i == 0 && title.Def == null) { uiColor = bs.Color; uiFont = bs.Font; }
                }
        }
        if (rootNode.Child("play") == null || rootNode.Child("scenes") == null) { problem = "boutons du menu principal illisibles"; return null; }

        // the focus colour DVDStyler gives the buttons when selected
        var accent = Walk(main).Select(x => x.Node).Where(x => x.Name == "parameter").Select(x => x.Attr("selected")).FirstOrDefault(c => c.StartsWith('#') && c.Length == 7);
        common.Kids.Add(Leaf("accent", accent ?? "#FFC83D"));
        common.Kids.Add(Leaf("ui_color", uiColor));
        common.Kids.Add(Leaf("templatename", stem));

        var scene = El("scene");
        scene.Kids.Add(Background(svg2));
        var videos = El("videoes");
        var filenames = scenes == null ? new Dictionary<string, string>() : Walk(scenes).Where(x => x.Node.Name == "button").ToDictionary(x => x.Node.Attr("id"), x => x.Node.Child("filename")?.Text ?? "");
        foreach (var p in scenes == null ? new() : Placed(scenes, svg2).Where(p => p.Id.StartsWith("button")).ToList())
        {
            var file = filenames.GetValueOrDefault(p.Id, "").ToLowerInvariant();
            var image = Walk(p.Def).Select(x => x.Node).FirstOrDefault(x => x.Name == "image");
            if (image != null) // a thumbnail: the picture sits inside the button's own box (5% in from each side in most templates)
            {
                double Part(string a, double total, double fallback) => image.Attr(a) is { Length: > 0 } v ? (v.EndsWith('%') ? Num(v) / 100 * total : Num(v)) : fallback;
                var (ix, iy, iw, ih) = (Part("x", p.W, 0), Part("y", p.H, 0), Part("width", p.W, p.W), Part("height", p.H, p.H));
                var video = El("video", ("rect", Rect(p.X + ix, p.Y + iy, iw, ih)));
                video.Kids.Add(Styled("name", ("Segoe UI", 15, uiColor, false, false, true, 0), p.X, p.Y + p.H + 1, p.W, 20, false));
                videos.Kids.Add(video);
            }
            else if (file.Contains("home")) scene.Kids.Add(El("home", ("rect", Rect(p.X, p.Y, p.W, p.H)), ("glyph", "⌂")));
            else if (file.Contains("arrow") || file.Contains("hand"))
            {
                var left = file.Contains("left") || (!file.Contains("right") && Walk(p.Def).Any(x => x.Node.Attr("transform").Contains("rotate(180")));
                scene.Kids.Add(El(left ? "left" : "right", ("rect", Rect(p.X, p.Y, p.W, p.H)), ("glyph", left ? "<" : ">")));
            }
        }
        if (videos.Kids.Count == 0)
        {
            // no thumbnails to copy (one-menu templates, text-only lists): BurnSmith's own 3 x 2 grid on the template's background, in its colour and font
            scene.Kids.RemoveAll(k => k.Name is "left" or "home" or "right");
            const int tw = 440, th = 248, pitchX = 560, pitchY = 380, x0 = 180, y0 = 220;
            for (var i = 0; i < 6; i++)
            {
                int x = x0 + i % 3 * pitchX, y = y0 + i / 3 * pitchY;
                var video = El("video", ("rect", $"{x},{y},{tw},{th}"));
                video.Kids.Add(El("name", ("rect", $"{x},{y + th + 8},{tw},44"), ("font", uiFont), ("font_size", "30"), ("color", uiColor), ("bold", "0"), ("italic", "0"), ("align", "center"), ("fit", "1")));
                videos.Kids.Add(video);
            }
            foreach (var (role, glyph, x) in new[] { ("left", "<", 700), ("home", "⌂", 905), ("right", ">", 1110) })
                scene.Kids.Add(El(role, ("rect", $"{x},950,110,80"), ("glyph", glyph)));
        }
        scene.Kids.Add(videos);

        var tscenes = El("tscenes");
        tscenes.Kids.Add(scene);
        var scenesNode = El("scenes");
        scenesNode.Kids.Add(tscenes);
        var template = El("template");
        template.Kids.AddRange([common, rootNode, scenesNode]);
        return template;
    }
}
