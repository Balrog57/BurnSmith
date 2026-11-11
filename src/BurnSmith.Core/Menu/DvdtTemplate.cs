using System.Globalization;
using System.Text.RegularExpressions;

namespace BurnSmith.Core.Menu;

/// <summary>
/// Menu templates stored as <c>.dvdt</c> files, translated into the template tree
/// <see cref="InstalledTemplate"/> draws: the background picture, the title and button text with the template's font, colour and position,
/// and the thumbnail positions of its scene page. The format draws its buttons as SVG snippets; BurnSmith draws plain text buttons and
/// thumbnails at the same places, so the result is close to the template, not identical.
/// </summary>
static partial class DvdtTemplate
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

    // ---- a button's own drawing ---------------------------------------------------------------------------------------------------

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static XNode Copy(XNode n)
    {
        var copy = new XNode(n.Name, new Dictionary<string, string>(n.Attrs));
        copy.Content.Append(n.Content);
        copy.Kids.AddRange(n.Kids.Select(Copy));
        return copy;
    }

    /// <summary>Back to text; attribute values and text are kept as the file wrote them (already escaped).</summary>
    static void Write(XNode n, System.Text.StringBuilder sb)
    {
        sb.Append('<').Append(n.Name);
        foreach (var (key, value) in n.Attrs) sb.Append(' ').Append(key).Append("=\"").Append(value.Replace("\"", "&quot;")).Append('"');
        if (n.Kids.Count == 0 && n.Content.Length == 0) { sb.Append("/>"); return; }
        sb.Append('>').Append(n.Content);
        foreach (var kid in n.Kids) Write(kid, sb);
        sb.Append("</").Append(n.Name).Append('>');
    }

    /// <summary>
    /// What the template keeps for one of its buttons: the program's drawing of it, already in the template's colours (a nested svg). Cut here
    /// into what goes under the thumbnail and what goes over it, each a whole SVG the menu engine draws at the button's place; the place of the
    /// thumbnail and the shape that clips it (an ellipse, a rounded box); and its text, left out of the drawings because BurnSmith writes the
    /// words. Lengths are in the drawing's units: its viewBox, or the button's box on the menu.
    /// </summary>
    sealed record Art(string? Under, string? Over, string? Mask, (double X, double Y, double W, double H)? Slot, XNode? Words, double[] Units);

    static Art Draw(XNode def, double bw, double bh)
    {
        var box = def.Attr("viewBox").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(v => Num(v)).ToArray();
        if (box.Length != 4 || box[2] <= 0 || box[3] <= 0) box = [0, 0, bw, bh];
        var kids = def.Kids.Select(Copy).ToList();
        // "50%" is half the box: written out, since the drawing is then read apart from its button
        foreach (var node in kids.SelectMany(k => Walk(k).Select(w => w.Node).Prepend(k)))
            foreach (var key in node.Attrs.Keys.ToList())
                if (node.Attrs[key].EndsWith('%') && key is "x" or "cx" or "width" or "rx" or "y" or "cy" or "height" or "ry")
                    node.Attrs[key] = F(Num(node.Attrs[key]) / 100 * (key is "x" or "cx" or "width" or "rx" ? box[2] : box[3]));

        var words = kids.SelectMany(k => Walk(k).Select(w => w.Node).Prepend(k)).FirstOrDefault(n => n.Name == "text");
        bool Worded(XNode n) => n.Name == "text" || (n.Name == "use" && words != null && n.Attr("xlink:href") == "#" + words.Attr("id"));
        void Strip(List<XNode> list) { list.RemoveAll(Worded); foreach (var n in list) Strip(n.Kids); }
        Strip(kids);

        var picture = kids.FindIndex(k => k.Name == "image");
        string? Whole(IEnumerable<XNode> part)
        {
            var shown = part.Where(k => k.Name != "defs").ToList();
            // nothing to see: a text button's marker is a circle without fill until the remote is on it
            if (!shown.SelectMany(k => Walk(k).Select(w => w.Node).Prepend(k)).Any(n => n.Name is "path" or "rect" or "ellipse" or "polygon" or "line" or "polyline" or "use"
                    || (n.Name == "circle" && !n.Attr("style").Contains("fill:none")))) return null;
            var sb = new System.Text.StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{F(bw)}\" height=\"{F(bh)}\" viewBox=\"{string.Join(' ', box.Select(F))}\">");
            foreach (var k in kids.Where(k => k.Name == "defs").Concat(shown)) Write(k, sb);
            return sb.Append("</svg>").ToString();
        }
        if (picture < 0) return new Art(null, Whole(kids), null, null, words, box);

        var image = kids[picture];
        var slot = (X: Num(image.Attr("x")), Y: Num(image.Attr("y")), W: Num(image.Attr("width")), H: Num(image.Attr("height")));
        string? mask = null;
        var clip = Regex.Match(image.Attr("style"), @"clip-path:\s*url\(#([^)]+)\)");
        if (clip.Success && slot.W > 0 && slot.H > 0 && kids.SelectMany(k => Walk(k).Select(w => w.Node)).FirstOrDefault(n => n.Name == "clipPath" && n.Attr("id") == clip.Groups[1].Value) is { } shape)
        {
            // the same shape, alone and black, seen through the picture's rectangle: what is black keeps the picture
            var sb = new System.Text.StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(slot.W)}\" height=\"{F(slot.H)}\" viewBox=\"{F(slot.X)} {F(slot.Y)} {F(slot.W)} {F(slot.H)}\" preserveAspectRatio=\"none\">");
            sb.Append("<g fill=\"#000000\"").Append(shape.Attrs.TryGetValue("transform", out var turn) ? $" transform=\"{turn}\">" : ">");
            foreach (var k in shape.Kids) Write(k, sb);
            mask = sb.Append("</g></svg>").ToString();
        }
        return new Art(Whole(kids.Take(picture)), Whole(kids.Skip(picture + 1)), mask, slot, words, box);
    }

    /// <summary>The template tree, or null when the file is not a menu template; <paramref name="problem"/> says why one cannot be drawn.</summary>
    public static XNode? Convert(XNode doc, string stem, out string? problem)
    {
        problem = null;
        // the file's own root carries the menus, whatever it is called
        if (doc.Kids.FirstOrDefault(r => Walk(r).Any(w => w.Node.Name == "menu")) is not { } root) return null;
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
            // the program stretches the picture over the whole page unless the template asks to keep its proportions (Cake, Fireworks, CameraWS do):
            // its own preview pictures show it, although SVG's rule for a missing attribute is "meet" — a 16:9 menu then had its picture in a column
            var aspect = image.Attr("preserveAspectRatio").ToLowerInvariant();
            if (aspect.Length > 0 && !aspect.Contains("none") && !aspect.Contains("slice")) bg.Attrs["fit"] = aspect.Split(' ', StringSplitOptions.RemoveEmptyEntries).First();
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
                if (font.Contains("Shell Dlg")) font = "Tahoma"; // a Windows alias, not a font name WPF knows: Windows maps it to Tahoma (FontSubstitutes)
                return (font.Trim('\'', '"'), Num(css.GetValueOrDefault("font-size")) is > 0 and var size ? size : 26, fill,
                        css.GetValueOrDefault("font-weight") == "bold", css.GetValueOrDefault("font-style") == "italic",
                        css.GetValueOrDefault("text-anchor") == "middle", node.Attr("x").EndsWith('%') ? 0 : Num(node.Attr("x")));
            }
            return null;
        }

        // an object the template lets size itself (defSize) is as wide as its text and keeps its left edge (DVDStyler's MenuObject): the box in the
        // file is the one of the placeholder ("$DiscTitle", "Play All") and says nothing of the room a real title has, so the text is written at
        // its size from that edge instead of being shrunk into that box
        bool OwnSize(XNode menu, string id) => Walk(menu).Any(x => x.Node.Name is "object" or "button" && x.Node.Attr("id") == id && x.Node.Attr("defSize") == "true");
        XNode Styled(string name, (string Font, double Size, string Color, bool Bold, bool Italic, bool Centre, double TextX) t, double x, double y, double bw, double bh, bool textButton, bool ownSize = false)
        {
            var inset = t.Centre ? 0 : t.TextX;
            var node = El(name, ("rect", ownSize ? Rect(x + inset, y, w - x - inset - 20, bh) : Rect(x + inset, y, bw - inset, bh)), ("font", t.Font), ("font_size", S(t.Size).ToString()), ("color", t.Color),
                ("bold", t.Bold ? "1" : "0"), ("italic", t.Italic ? "1" : "0"), ("align", t.Centre && !ownSize ? "center" : "left"));
            if (!ownSize) node.Attrs["fit"] = "1";
            if (textButton) node.Attrs["textbutton"] = "1";
            return node;
        }
        // what the program adds to a text when the template shows it: a shadow (a copy of the text, 2 units down and to the right) and an outline
        XNode Decorated(XNode node, XNode def)
        {
            if (Walk(def).FirstOrDefault(x => x.Node.Name == "text") is not { Node: { } text } found) return node;
            if (Walk(def).Select(x => x.Node).FirstOrDefault(x => x.Name == "use" && x.Attr("xlink:href") == "#" + text.Attr("id")) is { } copy
                && Css(copy.Attr("style")) is var shade && shade.GetValueOrDefault("visibility") != "hidden" && shade.GetValueOrDefault("fill") is ['#', ..] and { Length: 7 } dark)
                (node.Attrs["shadow"], node.Attrs["shadow_offset"]) = (dark, F(Math.Max(Num(copy.Attr("x")), Num(copy.Attr("y"))) * s));
            string? Own(string key) => Css(text.Attr("style")).GetValueOrDefault(key) ?? found.Parents.AsEnumerable().Reverse().Select(p => Css(p.Attr("style")).GetValueOrDefault(key)).FirstOrDefault(v => v != null);
            if (Own("stroke") is ['#', ..] and { Length: 7 } line && Num(Own("stroke-width")) is > 0 and var width)
                (node.Attrs["stroke_color"], node.Attrs["stroke_width"]) = (line, F(width * s / 2)); // half of an SVG stroke is outside the letters
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
            rootNode.Kids.Add(Decorated(Styled("label", ts, title.X, title.Y, title.W, title.H, false, OwnSize(main, title.Id)), title.Def));
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
                rootNode.Kids.Add(Decorated(Styled("play", ps, play.X, play.Y, play.W, play.H, true, OwnSize(main, play.Id)), play.Def));
                rootNode.Kids.Add(Decorated(Styled("scenes", ps, play.X, below + play.H <= h ? below : play.Y - play.H - 12, play.W, play.H, true, OwnSize(main, play.Id)), play.Def));
                if (title.Def == null) { uiColor = ps.Color; uiFont = ps.Font; }
            }
        }
        else
        {
            var buttons1 = placed1.Where(p => p.Id.StartsWith("button")).Take(roles.Length).ToList();
            for (var i = 0; i < buttons1.Count; i++)
                if (TextStyle(buttons1[i].Def) is { } bs)
                {
                    rootNode.Kids.Add(Decorated(Styled(roles[i], bs, buttons1[i].X, buttons1[i].Y, buttons1[i].W, buttons1[i].H, true, OwnSize(main, buttons1[i].Id)), buttons1[i].Def));
                    if (i == 0 && title.Def == null) { uiColor = bs.Color; uiFont = bs.Font; }
                }
        }
        if (rootNode.Child("play") == null || rootNode.Child("scenes") == null) { problem = "boutons du menu principal illisibles"; return null; }

        // the colour the template gives a button the remote is on: "highlighted" ("selected" is the instant it is pressed, a DVD's select state)
        var accent = Walk(main).Select(x => x.Node).Where(x => x.Name == "parameter").Select(x => x.Attr("highlighted")).FirstOrDefault(c => c.StartsWith('#') && c.Length == 7);
        common.Kids.Add(Leaf("accent", accent ?? "#FFC83D"));
        common.Kids.Add(Leaf("ui_color", uiColor));
        common.Kids.Add(Leaf("templatename", stem));

        var scene = El("scene");
        scene.Kids.Add(Background(svg2));
        var videos = El("videoes");
        var filenames = scenes == null ? new Dictionary<string, string>() : Walk(scenes).Where(x => x.Node.Name == "button").ToDictionary(x => x.Node.Attr("id"), x => x.Node.Child("filename")?.Text ?? "");
        // each button names the title it plays (pgci 1, 3, 5… = titles 1, 2, 3…): thumbnails go in that order, not the order they were drawn in
        // (ChristmasL1WS draws its third title last, BlueFractal swaps its bottom two)
        var plays = scenes == null ? new Dictionary<string, double>() : Walk(scenes).Where(x => x.Node.Name == "button").ToDictionary(x => x.Node.Attr("id"), x => Num(x.Node.Child("action")?.Attr("pgci")));
        var placed2 = scenes == null ? new() : Placed(scenes, svg2).Where(p => p.Id.StartsWith("button")).OrderBy(p => plays.GetValueOrDefault(p.Id)).ToList();
        // the name BurnSmith writes under a thumbnail is its own addition (the program shows none): only when every one has room for it, between
        // the rows and above the arrows
        bool Thumbnail((string Id, XNode Def, double X, double Y, double W, double H) p) => Walk(p.Def).Any(x => x.Node.Name == "image");
        var named = placed2.Where(Thumbnail).All(p => p.Y + p.H + 21 <= h && !placed2.Any(o => o.Id != p.Id && o.X < p.X + p.W && p.X < o.X + o.W && o.Y < p.Y + p.H + 21 && p.Y + p.H + 1 < o.Y + o.H));
        foreach (var p in placed2)
        {
            var file = filenames.GetValueOrDefault(p.Id, "").ToLowerInvariant();
            // the button as the program draws it, in the template's colours. Its units on the menu: its viewBox fitted in the button's box, centred
            var art = Draw(p.Def, p.W, p.H);
            var u = Math.Min(p.W / art.Units[2], p.H / art.Units[3]);
            double ux = p.X + (p.W - art.Units[2] * u) / 2 - art.Units[0] * u, uy = p.Y + (p.H - art.Units[3] * u) / 2 - art.Units[1] * u;
            if (art.Slot is { } slot) // a thumbnail: the picture sits inside the button's own box (5% in from each side in most templates)
            {
                var video = El("video", ("rect", Rect(ux + slot.X * u, uy + slot.Y * u, slot.W * u, slot.H * u)), ("over_rect", Rect(p.X, p.Y, p.W, p.H)));
                foreach (var (key, svg) in new[] { ("svg_under", art.Under), ("svg_over", art.Over), ("svg_mask", art.Mask) })
                    if (svg != null) video.Attrs[key] = svg;
                // a frame with its caption: the name goes where the drawing has its text, under the frame. Its y is the top of the line: the button's
                // own file leaves the frame at 77 % of the height and puts the text at 83 %, with just the room of its 20 units below (132 high)
                if (art.Words != null && TextStyle(p.Def) is { } caption)
                    video.Kids.Add(Decorated(Styled("name", caption with { Size = caption.Size * u, Centre = true }, p.X, uy + Num(art.Words.Attr("y")) * u, p.W, caption.Size * u * 1.2, false), p.Def));
                else if (named) video.Kids.Add(Styled("name", ("Segoe UI", 15, uiColor, false, false, true, 0), p.X, p.Y + p.H + 1, p.W, 20, false));
                videos.Kids.Add(video);
                continue;
            }
            // where a button leads says what it is: the first menu (0), the page before (200), another page (an even number)
            var target = int.TryParse(Walk(scenes!).FirstOrDefault(x => x.Node.Name == "button" && x.Node.Attr("id") == p.Id).Node?.Child("action")?.Attr("pgci"), out var pgci) ? pgci : -1;
            var role = target == 0 ? "home" : target == 200 ? "left" : target > 0 && target % 2 == 0 ? "right"
                : file.Contains("home") ? "home" : !(file.Contains("arrow") || file.Contains("hand")) ? null
                : file.Contains("left") || (!file.Contains("right") && Walk(p.Def).Any(x => x.Node.Attr("transform").Contains("rotate(180"))) ? "left" : "right";
            if (role == null) continue;
            var nav = El(role, ("rect", Rect(p.X, p.Y, p.W, p.H)));
            if (art.Over != null) nav.Attrs["svg"] = art.Over;
            // a word for a button ("Back", "Next"): BurnSmith's own word or sign, where the template writes its own and in its style
            if (art.Words != null && TextStyle(p.Def) is { } word) nav.Kids.Add(Decorated(Styled("text", word, p.X, p.Y, p.W, p.H, false, true), p.Def));
            if (art.Over == null && art.Words == null) nav.Attrs["glyph"] = role == "home" ? "⌂" : role == "left" ? "<" : ">";
            scene.Kids.Add(nav);
        }
        if (videos.Kids.Count == 0)
        {
            // no thumbnails to copy (one-menu templates, text-only lists): BurnSmith's own 3 x 2 grid on the template's background, in its colour and font
            scene.Kids.RemoveAll(k => k.Name is "left" or "home" or "right");
            // inside the template's own picture: a 4:3 menu is 1440 wide between two bars, and the grid is as much narrower
            var g = S(w) / (double)MenuBuilder.W;
            int tw = (int)(440 * g), th = (int)(248 * g), pitchX = (int)(560 * g), x0 = X(0) + (int)(180 * g);
            const int pitchY = 380, y0 = 220;
            for (var i = 0; i < 6; i++)
            {
                int x = x0 + i % 3 * pitchX, y = y0 + i / 3 * pitchY;
                var video = El("video", ("rect", $"{x},{y},{tw},{th}"));
                video.Kids.Add(El("name", ("rect", $"{x},{y + th + 8},{tw},44"), ("font", uiFont), ("font_size", "30"), ("color", uiColor), ("bold", "0"), ("italic", "0"), ("align", "center"), ("fit", "1")));
                videos.Kids.Add(video);
            }
            foreach (var (role, glyph, x) in new[] { ("left", "<", 700), ("home", "⌂", 905), ("right", ">", 1110) })
                scene.Kids.Add(El(role, ("rect", $"{X(0) + (int)(x * g)},950,110,80"), ("glyph", glyph)));
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
