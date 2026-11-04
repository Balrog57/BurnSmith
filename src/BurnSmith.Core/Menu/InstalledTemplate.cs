using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BurnSmith.Core.Menu;

/// <summary>One element of a template file read by <see cref="LooseXml"/>.</summary>
sealed class XNode(string name, Dictionary<string, string> attrs)
{
    public string Name { get; } = name;
    public Dictionary<string, string> Attrs { get; } = attrs;
    public List<XNode> Kids { get; } = [];
    public StringBuilder Content { get; } = new();
    public string Text => WebUtility.HtmlDecode(Content.ToString().Trim());

    public XNode? Child(string name) => Kids.FirstOrDefault(k => k.Name == name);
    public IEnumerable<XNode> Children(string name) => Kids.Where(k => k.Name == name);
    public string Attr(string name, string fallback = "") => Attrs.TryGetValue(name, out var v) ? WebUtility.HtmlDecode(v) : fallback;

    public Box Rect(string attr = "rect")
    {
        var p = Attr(attr).Split(',', StringSplitOptions.TrimEntries).Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();
        return p.Length == 4 ? new Box(p[0], p[1], p[2], p[3]) : default;
    }
}

/// <summary>
/// Tolerant XML reader: DVDFab's own template files contain mismatched closing tags (<c>&lt;home&gt;Home&lt;/scenes&gt;</c>)
/// that XmlDocument refuses. A closing tag that does not match closes the innermost leaf instead.
/// </summary>
static partial class LooseXml
{
    [GeneratedRegex("""<!--.*?-->|<\?.*?\?>|<(/?)([\w:.-]+)((?:\s+[\w:.-]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(/?)>|([^<]+)""", RegexOptions.Singleline)]
    private static partial Regex Token();
    [GeneratedRegex("""([\w:.-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')""")]
    private static partial Regex Attribute();

    public static XNode Parse(string xml)
    {
        var root = new XNode("#root", []);
        var stack = new List<XNode> { root };
        foreach (Match m in Token().Matches(xml))
        {
            if (m.Groups[5].Success) { stack[^1].Content.Append(m.Groups[5].Value); continue; }
            if (!m.Groups[2].Success) continue; // comment or <?xml?>
            var name = m.Groups[2].Value;
            if (m.Groups[1].Value == "/")
            {
                if (stack[^1].Name == name || (stack.Count > 1 && stack[^1].Kids.Count == 0)) stack.RemoveAt(stack.Count - 1);
                else if (stack.FindLastIndex(n => n.Name == name) is > 0 and var at) stack.RemoveRange(at, stack.Count - at);
                continue;
            }
            var attrs = Attribute().Matches(m.Groups[3].Value).ToDictionary(a => a.Groups[1].Value, a => a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value, StringComparer.OrdinalIgnoreCase);
            var node = new XNode(name, attrs);
            stack[^1].Kids.Add(node);
            if (m.Groups[4].Value != "/") stack.Add(node);
        }
        return root;
    }
}

/// <summary>
/// A DVDFab Blu-ray menu template (<c>template.bd.xml</c> + its PNGs) turned into <see cref="MenuPage"/>s: same artwork, same positions.
/// The files are read from the user's own installation at run time; nothing of them is stored in BurnSmith.
/// What the template cannot hold is filled in from the disc: title, chapter pictures and names, audio and subtitle names.
/// </summary>
public sealed class InstalledTemplate
{
    const int RowMarkerGap = 24;
    static readonly Dictionary<string, InstalledTemplate?> Cache = new(StringComparer.OrdinalIgnoreCase);

    readonly string dir;
    readonly XNode template, common;

    /// <summary>Why BurnSmith cannot draw this template (null = it can).</summary>
    public string? Unsupported { get; }
    public string Name { get; }
    /// <summary>"DVDFab Blu-ray", "DVDFab DVD" or "Leawo".</summary>
    public string Category { get; }
    /// <summary>Selection colour the template itself defines (Leawo's highlight), if any.</summary>
    public string? Accent => common.Child("accent")?.Text is { Length: > 0 } a ? a : null;
    public string? Thumbnail => new[] { "thumb4x3.bd.png", "Menu/background01.jpg" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);

    InstalledTemplate(string dir, XNode template, string category, string? noLayout = null)
    {
        this.dir = dir;
        this.template = template;
        Category = category;
        common = template.Child("common") ?? new XNode("common", []);
        Name = common.Child("templatename")?.Text is { Length: > 0 } n ? n : Path.GetFileName(dir);
        Unsupported = noLayout ?? Check();
    }

    /// <summary>
    /// Null when the folder is not a template: a DVDFab one (<c>template.bd.xml</c>, or <c>template.xml</c> for its DVD set) or a Leawo one
    /// (<c>template.xml</c> / <c>template2.xml</c> with <c>TemplateDef</c>, or just <c>Menu</c> and <c>Scene</c> pictures, which are reported unsupported).
    /// </summary>
    public static InstalledTemplate? Load(string dir)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(dir, out var known)) return known;
            var file = new[] { "template.bd.xml", "template.xml", "template2.xml" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
            InstalledTemplate? parsed = null;
            if (file != null)
            {
                var text = File.ReadAllText(file);
                text = System.Text.RegularExpressions.Regex.Replace(text, "(\\w+=\"\\d+)(\\s+)(\\w+=\")", "$1\"$2$3"); // Leawo's files miss a closing quote: Top="136 Width="209"
                var doc = LooseXml.Parse(text);
                if (doc.Child("template") is { } t) parsed = new InstalledTemplate(dir, t, "DVDFab Blu-ray");
                else if (doc.Child("Template") is { } s) parsed = new InstalledTemplate(dir, Simple(s, dir), "DVDFab DVD");
                else if (doc.Children("TemplateDef").FirstOrDefault(d => d.Attr("Aspect").StartsWith("16-9")) is { } def)
                    parsed = new InstalledTemplate(dir, Leawo(def, doc.Children("TemplateDef").FirstOrDefault(d => d.Attr("Aspect") == "4-3")), "Leawo");
            }
            else if (Directory.Exists(Path.Combine(dir, "Menu")) && Directory.Exists(Path.Combine(dir, "Scene")))
                parsed = new InstalledTemplate(dir, new XNode("template", []), "Leawo", "pas de fichier de mise en page (les positions sont internes à Leawo)");
            return Cache[dir] = parsed;
        }
    }

    static (int W, int H)? PngSize(string path)
    {
        if (!File.Exists(path)) return null;
        Span<byte> head = stackalloc byte[24];
        using var f = File.OpenRead(path);
        return f.Read(head) < 24 || head[0] != 0x89 ? null
            : (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[16..]), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[20..]));
    }

    /// <summary>
    /// DVDFab's DVD templates (<c>&lt;Template&gt;</c> with <c>label_left</c>, <c>video1_width</c>… values) rewritten as the Blu-ray tree,
    /// scaled from the size of their background (720x480 for the DVD set) to the 1920x1080 canvas.
    /// </summary>
    static XNode Simple(XNode s, string dir)
    {
        string V(XNode? n, string name, string fallback = "") => n?.Child(name)?.Text is { Length: > 0 } t ? t : fallback;
        int I(XNode? n, string name) => int.TryParse(V(n, name), out var v) ? v : 0;
        var background = V(s, "bg");
        var (bw, bh) = PngSize(System.IO.Path.Combine(dir, background)) ?? (MenuBuilder.W, MenuBuilder.H);
        double sx = MenuBuilder.W / (double)bw, sy = MenuBuilder.H / (double)bh;
        string Rect(XNode? n, string p) => $"{(int)(I(n, p + "_left") * sx)},{(int)(I(n, p + "_top") * sy)},{(int)(I(n, p + "_width") * sx)},{(int)(I(n, p + "_height") * sy)}";
        string Scaled(string size) => ((int)(double.Parse(size, System.Globalization.CultureInfo.InvariantCulture) * sy)).ToString();
        XNode El(string name, params (string Key, string Value)[] attrs) => new(name, attrs.ToDictionary(a => a.Key, a => a.Value));
        XNode Leaf(string name, string text) { var n = El(name); n.Content.Append(text); return n; }
        XNode Bg() { var bg = El("bg"); bg.Kids.Add(Leaf("image", background)); return bg; }
        XNode Pics(XNode? n)
        {
            var pics = El("pics");
            for (var i = 1; i <= I(n, "pic_num"); i++) pics.Kids.Add(El("pic", ("rect", Rect(n, $"pic{i}")), ("image", V(n, $"pic{i}"))));
            return pics;
        }

        var top = s.Child("Topmenu");
        var title = s.Child("Title");
        var common = El("common");
        foreach (var (key, value) in new[] { ("font", V(s, "font", "Verdana")), ("font_size", Scaled(V(s, "font_size", "24"))), ("font_color", V(s, "font_color", "#FFFFFF")),
            ("font_bold", V(s, "font_bold", "0")), ("font_italic", V(s, "font_italic", "0")), ("frame", V(s, "frame")), ("selection", V(s, "selection")),
            ("homebuttontimage", V(s, "home")), ("leftbuttontimage", V(s, "left")), ("rightbuttontimage", V(s, "right")) })
            common.Kids.Add(Leaf(key, value));

        var root = El("rootmenu");
        var label = El("label", ("rect", Rect(top, "label")), ("font_size", Scaled(V(top, "label_font_size", "48"))));
        label.Content.Append(V(top, "label", "My Disc"));
        root.Kids.AddRange([label, Bg(), El("video", ("rect", Rect(top, "mask")), ("frame_image", V(s, "frame")))]);
        foreach (var k in new[] { "play", "scenes" }) root.Kids.Add(El(k, ("rect", Rect(top, k)), ("font_size", Scaled(V(top, k + "_font_size", "32"))), ("textbutton", "1")));
        root.Kids.Add(Pics(top));

        var scene = El("scene");
        scene.Kids.Add(Bg());
        foreach (var k in new[] { "left", "home", "right" }) scene.Kids.Add(El(k, ("rect", Rect(title, k))));
        var videos = El("videoes");
        for (var i = 1; I(title, $"video{i}_width") > 0; i++)
        {
            var r = Rect(title, $"video{i}").Split(',').Select(int.Parse).ToArray();
            var video = El("video", ("rect", string.Join(',', r)), ("frame_image", V(s, "frame")));
            video.Kids.Add(El("name", ("rect", $"{r[0]},{r[1] + r[3] + 4},{r[2]},60"))); // DVDFab's DVD templates have no name box: under the thumbnail
            videos.Kids.Add(video);
        }
        scene.Kids.AddRange([videos, Pics(title)]);
        var tscenes = El("tscenes");
        tscenes.Kids.Add(scene);
        var scenes = El("scenes");
        scenes.Kids.Add(tscenes);

        var template = El("template");
        template.Kids.AddRange([common, root, scenes]);
        return template;
    }

    /// <summary>
    /// Leawo's <c>TemplateDef</c> (positions in 1920x1080 for the 16-9 set): text buttons on the background, a picture frame under the
    /// preview and under each thumbnail (whose position is an offset inside its frame), previous / next / back pictures.
    /// </summary>
    static XNode Leawo(XNode def, XNode? reference)
    {
        string A(XNode? n, string name, string fallback = "") => n?.Attr(name, fallback) is { Length: > 0 } v ? v : fallback;
        var k = def.Attr("Aspect") == "16-9-2" ? 0.5 : 1.0; // the "16-9-2" files are laid out on a 3840x2160 canvas
        int I(XNode? n, string name) => int.TryParse(A(n, name), out var v) ? (int)(v * k) : 0;
        XNode El(string name, params (string Key, string Value)[] attrs) => new(name, attrs.ToDictionary(a => a.Key, a => a.Value));
        XNode Leaf(string name, string text) { var n = El(name); n.Content.Append(text); return n; }
        XNode Styled(string name, XNode? style, string rect, params (string, string)[] more) =>
            El(name, [("rect", rect), ("font", A(style, "Type", "Tahoma")), ("font_size", (I(style, "Size") is > 0 and var px ? px : 38).ToString()), ("color", A(style, "Color", "#FFFFFF")),
                ("bold", A(style, "Bold") == "true" ? "1" : "0"), ("italic", A(style, "Italic") == "true" ? "1" : "0"), .. more]);
        string Rect(XNode? n) => $"{I(n, "Left")},{I(n, "Top")},{I(n, "Width")},{I(n, "Height")}";
        string Text(XNode? style, bool centred, int width = 480) // Left / Top place the text: its left edge, or its centre when centred
        {
            var size = I(style, "Size") is > 0 and var s ? s : 38;
            return $"{I(style, "Left") - (centred ? width / 2 : 0)},{I(style, "Top")},{width},{size * 3 / 2}";
        }

        var menu = def.Child("Menu");
        var page = def.Child("Scene")?.Child("Page");
        var frame = menu?.Child("Frame");
        var screen = menu?.Child("videoscr");
        var buttonColors = def.Child("Color")?.Child("Button");

        var common = El("common");
        common.Kids.AddRange([Leaf("accent", A(buttonColors, "High")), Leaf("leftbuttontimage", "Scene/" + A(page?.Child("ButtonPre"), "FileName")),
            Leaf("rightbuttontimage", "Scene/" + A(page?.Child("ButtonNext"), "FileName")), Leaf("homebuttontimage", "Scene/" + A(page?.Child("Return"), "FileName"))]);

        var bg = El("bg");
        bg.Kids.Add(Leaf("image", "Menu/" + A(menu?.Child("Background"), "FileName")));
        var root = El("rootmenu");
        root.Kids.AddRange([bg, Styled("label", menu?.Child("TitleText"), Text(menu?.Child("TitleText"), false, 520)),
            El("video", ("rect", $"{I(frame, "Left") + I(screen, "Left")},{I(frame, "Top") + I(screen, "Top")},{I(screen, "Width")},{I(screen, "Height")}"),
                ("frame_under", "Menu/" + A(frame, "FileName")), ("under_rect", Rect(frame))),
            Styled("play", menu?.Child("ButtonPlayText"), Text(menu?.Child("ButtonPlayText"), true), ("textbutton", "1")),
            Styled("scenes", menu?.Child("ButtonScenesText"), Text(menu?.Child("ButtonScenesText"), true), ("textbutton", "1"))]);

        var scene = El("scene");
        var sceneBg = El("bg");
        sceneBg.Kids.Add(Leaf("image", "Scene/" + A(page?.Child("Background"), "FileName")));
        scene.Kids.AddRange([sceneBg, El("left", ("rect", Rect(page?.Child("ButtonPre")))), El("right", ("rect", Rect(page?.Child("ButtonNext")))),
            El("home", ("rect", Rect(page?.Child("Return")))), Styled("label", page?.Child("TitleText"), Text(page?.Child("TitleText"), false, 700))]);
        var videos = El("videoes");
        for (var i = 0; page?.Child("Frame")?.Child($"Frame{i}") is { } box; i++)
        {
            var offset = page.Child("videoscr")?.Child($"videoscr{i}");
            // the 16-9 offsets of Leawo's files do not fit their own frame picture; the 4-3 block of the same file does: its proportions are used
            var refPage = reference?.Child("Scene")?.Child("Page");
            var refBox = refPage?.Child("Frame")?.Child($"Frame{i}");
            var refOffset = refPage?.Child("videoscr")?.Child($"videoscr{i}");
            var (ox, oy, ow, oh) = refBox != null && refOffset != null && I(refBox, "Width") > 0 && I(refBox, "Height") > 0
                ? (I(refOffset, "Left") * I(box, "Width") / I(refBox, "Width"), I(refOffset, "Top") * I(box, "Height") / I(refBox, "Height"),
                   I(refOffset, "Width") * I(box, "Width") / I(refBox, "Width"), I(refOffset, "Height") * I(box, "Height") / I(refBox, "Height"))
                : (I(offset, "Left"), I(offset, "Top"), I(offset, "Width"), I(offset, "Height"));
            var video = El("video", ("rect", $"{I(box, "Left") + ox},{I(box, "Top") + oy},{ow},{oh}"),
                ("frame_under", "Scene/" + A(box, "FileName")), ("under_rect", Rect(box)));
            video.Kids.Add(Styled("name", page.Child("ButtonText")?.Child($"ButtonText{i}"), $"{I(box, "Left")},{I(box, "Top") + I(box, "Height") + 4},{I(box, "Width")},50"));
            videos.Kids.Add(video);
        }
        scene.Kids.Add(videos);
        var tscenes = El("tscenes");
        tscenes.Kids.Add(scene);
        var scenes = El("scenes");
        scenes.Kids.Add(tscenes);

        var template = El("template");
        template.Kids.AddRange([common, root, scenes]);
        return template;
    }

    string? Check()
    {
        if (template.Child("rootmenu") is not { } root) return "pas de menu racine (modèle « tout en un »)";
        if (Asset(root.Child("bg")?.Child("image")?.Text) == null) return "image de fond introuvable";
        if (root.Child("play") is not { } play || (Asset(common.Child("playbuttontimage")?.Text) == null && play.Attr("textbutton") != "1")) return "bouton lecture introuvable";
        if (template.Child("scenes")?.Child("tscenes")?.Child("scene")?.Child("videoes")?.Children("video").Any() != true) return "pas de grille de scènes";
        return null;
    }

    /// <summary>A PNG or JPEG file; some files of the portable DVDFab copy hold memory garbage instead of a picture.</summary>
    static bool IsImage(string path)
    {
        if (!File.Exists(path)) return false;
        Span<byte> head = stackalloc byte[4];
        using var f = File.OpenRead(path);
        return f.Read(head) == 4 && (head is [0x89, 0x50, 0x4E, 0x47] or [0xFF, 0xD8, _, _]);
    }

    string? Asset(string? relative) =>
        string.IsNullOrWhiteSpace(relative) || System.IO.Path.Combine(dir, relative.Trim()) is var full && !IsImage(full) ? null : full;

    /// <summary>
    /// The highlighted twin of a button picture: <c>play/0.normal.png</c> → <c>play/0.sub.png</c>. Null when the template has none
    /// (the UHD ones) or it is unreadable: the button then gets BurnSmith's accent frame.
    /// </summary>
    string? Selected(string? normal) => normal != null && IsImage(normal.Replace(".normal.", ".sub.")) ? normal.Replace(".normal.", ".sub.") : null;

    /// <summary>DVDFab draws a button picture at its own size, centred on the rectangle of the XML (which is often smaller).</summary>
    static Box Place(string picture, Box rect) =>
        PngSize(picture) is var (w, h) ? new Box(rect.X + (rect.W - w) / 2, rect.Y + (rect.H - h) / 2, w, h) : rect;

    /// <summary>The template has rows for the audio / subtitle choice (the UHD ones lay that page out differently).</summary>
    public bool HasSetupLists => (template.Child("setup")?.Child("audiolist")?.Children("title").Any() ?? false) || (template.Child("setup")?.Child("subtitlelist")?.Children("title").Any() ?? false);

    /// <summary>Artwork of the audio / subtitle page for BurnSmith's own rows: the template's background under a dark veil.</summary>
    public List<Op> SetupBackdrop()
    {
        var ops = new List<Op>();
        Background(ops, template.Child("setup") ?? template.Child("rootmenu")!);
        ops.Add(new Fill(new Box(0, 0, MenuBuilder.W, MenuBuilder.H), "#000000", 0.45, 0));
        return ops;
    }

    string? Button(string key) => Asset(common.Child(key)?.Text);

    // ---- text -------------------------------------------------------------

    Text Txt(XNode n, string value, string align = "left")
    {
        var font = n.Attr("font", common.Child("font")?.Text ?? "Segoe UI");
        var hasFiles = Directory.EnumerateFiles(dir, "*.*").Any(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
        return new Text(value, n.Rect(), double.TryParse(n.Attr("font_size", common.Child("font_size")?.Text ?? "32"), out var size) ? size : 32,
            font, hasFiles ? dir : null, n.Attr("color", common.Child("font_color")?.Text ?? "#FFFFFF"),
            n.Attr("bold", common.Child("font_bold")?.Text ?? "0") == "1", n.Attr("italic", common.Child("font_italic")?.Text ?? "0") == "1", align,
            n.Attrs.ContainsKey("stroke_color") ? n.Attr("stroke_color") : null, double.TryParse(n.Attr("stroke_width"), out var sw) ? sw : 0);
    }

    // ---- shared page parts --------------------------------------------------

    void Background(List<Op> ops, XNode page)
    {
        if (Asset(page.Child("bg")?.Child("image")?.Text) is { } bg) ops.Add(new Image(bg, new Box(0, 0, MenuBuilder.W, MenuBuilder.H)));
        var cover = page.Child("bg")?.Child("covering_color")?.Text; // #AARRGGBB shade between the artwork and the thumbnails
        if (cover is { Length: 9 }) ops.Add(new Fill(new Box(0, 0, MenuBuilder.W, MenuBuilder.H), "#" + cover[^6..], Convert.ToInt32(cover.TrimStart('#')[..2], 16) / 255.0, 0));
    }

    /// <param name="panelIfMissing">Some templates name a panel picture they do not ship (Birthday1's setup page): a dark translucent panel keeps the text readable.</param>
    void Pictures(List<Op> ops, XNode page, Func<Box, bool>? skip = null, bool panelIfMissing = false)
    {
        foreach (var pic in page.Child("pics")?.Children("pic") ?? [])
        {
            if (skip?.Invoke(pic.Rect()) == true) continue;
            if (Asset(pic.Attr("image")) is { } file) ops.Add(new Image(file, pic.Rect()));
            else if (panelIfMissing) ops.Add(new Fill(pic.Rect(), "#000000", 0.4, 24));
        }
    }

    /// <summary>Leawo draws its picture frame first and the preview inside it (DVDFab's frames go on top of the picture).</summary>
    void Under(List<Op> ops, XNode video)
    {
        if (Asset(video.Attr("frame_under")) is { } file) ops.Add(new Image(file, video.Rect("under_rect")));
    }

    Framed Frame(XNode video, string? picture)
    {
        var frame = Asset(video.Attr("frame_image", common.Child("frame")?.Text ?? ""));
        var mask = frame == null ? null : Asset(System.IO.Path.GetRelativePath(dir, frame).Replace(".normal.", ".mask."));
        return new Framed(video.Rect(), picture, frame, mask == frame ? null : mask);
    }

    // ---- pages --------------------------------------------------------------

    public List<MenuPage> Pages(MenuDesign d, MenuContent c)
    {
        var cells = MenuBuilder.Cells(d, c);
        var pages = new List<MenuPage> { Root(d, c, cells.Count > 1) };
        var perPage = template.Child("scenes")!.Child("tscenes")!.Child("scene")!.Child("videoes")!.Children("video").Count();
        var total = cells.Count > 1 ? (cells.Count + perPage - 1) / perPage : 0;
        for (var i = 1; i <= total; i++) pages.Add(Scenes(d, cells, i, total, perPage, c.Series ? d.EpisodesLabel : d.ScenesLabel));
        if (c.HasSettings) pages.Add(MenuBuilder.Settings(d, c, null, null, "settings")); // BurnSmith's own rows when the template has none
        return pages;
    }

    MenuPage Root(MenuDesign d, MenuContent c, bool withScenes)
    {
        var n = template.Child("rootmenu")!;
        var ops = new List<Op>();
        Background(ops, n);
        Pictures(ops, n);
        if (n.Child("video") is { } video && d.ShowPreview) { Under(ops, video); ops.Add(Frame(video, c.PreviewPicture)); }
        if (n.Child("label") is { } label) ops.Add(Txt(label, d.Title));

        var buttons = new List<MenuButton>();
        void Add(string id, XNode? node, string? image, MenuAction action, string label)
        {
            if (node == null) return;
            if (image != null)
            {
                var box = Place(image, node.Rect());
                ops.Add(new Image(image, box));
                buttons.Add(new MenuButton(id, box, action) { FocusImage = Selected(image) });
            }
            else if (node.Attr("textbutton") == "1") // DVD templates: the label is text, a star marks the selected one
            {
                var r = node.Rect();
                var star = Asset(common.Child("selection")?.Text);
                var slot = new Box(r.X - r.H, r.Y, r.H, r.H);
                ops.Add(Txt(node, label, "center"));
                buttons.Add(star == null ? new MenuButton(id, r, action) : new MenuButton(id, new Box(slot.X, r.Y, r.W + r.H, r.H), action) { FocusImage = star, FocusSlot = slot });
            }
        }
        Add("play", n.Child("play"), Button("playbuttontimage"),
            c.Series ? new MenuAction("playall", Playlist: c.Titles![0].Playlist, Mark: c.Titles.Count) : new MenuAction("play", Playlist: c.Playlist),
            c.Series ? d.PlayAllLabel : d.PlayLabel);
        if (withScenes) Add("scenes", n.Child("scenes"), Button("scenebuttontimage"), new MenuAction("page", "scenes1"), c.Series ? d.EpisodesLabel : d.ScenesLabel);
        if (c.HasSettings)
        {
            if (Button("setupbuttontimage") != null) Add("settings", n.Child("setup"), Button("setupbuttontimage"), new MenuAction("page", "settings"), d.SettingsLabel);
            else // "none_setup" templates have no picture for it: a plain capsule under the other buttons, BurnSmith's own addition
            {
                var low = buttons.Count == 0 ? MenuBuilder.H / 2 : buttons.Max(b => b.Rect.Y + b.Rect.H);
                var middle = buttons.Count == 0 ? MenuBuilder.W / 2 : (int)buttons.Average(b => b.Rect.X + b.Rect.W / 2.0);
                var box = new Box(Math.Clamp(middle - 160, 20, MenuBuilder.W - 340), Math.Min(low + 8, MenuBuilder.H - 70), 320, 58);
                ops.Add(new Fill(box, "#000000", 0.35, 28));
                ops.Add(new Label(d.SettingsLabel, box, 36, true));
                buttons.Add(new MenuButton("settings", box, new MenuAction("page", "settings")));
            }
        }
        return new MenuPage("root", ops, Link(buttons));
    }

    MenuPage Scenes(MenuDesign d, List<MenuBuilder.Cell> cells, int number, int total, int perPage, string title)
    {
        var s = template.Child("scenes")!.Child("tscenes")!.Child("scene")!;
        var ops = new List<Op>();
        Background(ops, s);
        Pictures(ops, s);
        if (s.Child("label") is { } heading) ops.Add(Txt(heading, title));
        var buttons = new List<MenuButton>();
        var slots = s.Child("videoes")!.Children("video").ToList();
        for (var i = 0; i < slots.Count && (number - 1) * perPage + i < cells.Count; i++)
        {
            var cell = cells[(number - 1) * perPage + i];
            Under(ops, slots[i]);
            var framed = Frame(slots[i], cell.Picture);
            ops.Add(framed);
            if (slots[i].Child("name") is { } name) ops.Add(Txt(name, cell.Label, "center"));
            buttons.Add(new MenuButton(cell.Id, slots[i].Rect(), cell.Action) { FocusImage = Selected(framed.Frame) }); // none for Leawo: the accent frame
        }
        void Nav(string id, string key, string image, MenuAction action)
        {
            if (s.Child(key) is not { } node || Button(image) is not { } file) return;
            var box = Place(file, node.Rect());
            ops.Add(new Image(file, box));
            buttons.Add(new MenuButton(id, box, action) { FocusImage = Selected(file) });
        }
        if (number > 1) Nav("prev", "left", "leftbuttontimage", new MenuAction("page", $"scenes{number - 1}"));
        Nav("home", "home", "homebuttontimage", new MenuAction("page", "root"));
        if (number < total) Nav("next", "right", "rightbuttontimage", new MenuAction("page", $"scenes{number + 1}"));
        return new MenuPage($"scenes{number}", ops, Link(buttons));
    }

    /// <summary>Row width of the audio list: the BD-J menu draws its "active" dot a fixed distance from each row's left edge.</summary>
    public int MarkerOffset => (template.Child("setup")?.Child("audiolist")?.Child("title")?.Rect("frame_rect").W ?? 600) - MenuBuilder.MarkerSize - RowMarkerGap + MenuBuilder.FocusMargin;

    public MenuPage Settings(MenuDesign d, MenuContent c, int? activeAudio, int? activeSub, string id)
    {
        var n = template.Child("setup") ?? throw new InvalidOperationException("this template has no audio / subtitle page");
        var audioRows = n.Child("audiolist")?.Children("title").ToList() ?? [];
        var subRows = n.Child("subtitlelist")?.Children("title").ToList() ?? [];
        var audio = c.AudioNames.Count > 1 ? c.AudioNames.Take(audioRows.Count).ToList() : [];
        var subs = c.SubtitleNames.Count > 0 ? new[] { d.OffLabel }.Concat(c.SubtitleNames).Take(subRows.Count).ToList() : [];

        // a column that has nothing to choose is hidden together with its panel and header
        var unused = new List<Box>();
        if (audio.Count == 0 && n.Child("audiolist") is { } a) unused.Add(a.Rect());
        if (subs.Count == 0 && n.Child("subtitlelist") is { } sl) unused.Add(sl.Rect());
        bool Hidden(Box r) => unused.Any(u => r.X + r.W / 2 >= u.X && r.X + r.W / 2 <= u.X + u.W);

        var ops = new List<Op>();
        Background(ops, n);
        Pictures(ops, n, Hidden, panelIfMissing: true);
        if (n.Child("video") is { } video && d.ShowPreview) ops.Add(Frame(video, c.PreviewPicture));
        var audioBox = n.Child("audiolist")?.Rect() ?? default;
        foreach (var t in n.Child("texts")?.Children("text") ?? [])
        {
            var r = t.Rect();
            if (Hidden(r)) continue;
            var inAudio = r.X >= audioBox.X && r.X <= audioBox.X + audioBox.W;
            var value = t.Attr("tr_lang") == "1" ? (inAudio ? d.AudioLabel : d.SubtitlesLabel) : t.Text;
            if (value.Length > 0) ops.Add(Txt(t, value));
        }

        var buttons = new List<MenuButton>();
        var selection = Asset(common.Child("selection")?.Text);
        void Rows(List<XNode> rows, List<string> names, int first, string prefix, string kind, int? active)
        {
            for (var i = 0; i < names.Count; i++)
            {
                var number = first + i;
                var box = rows[i].Rect("frame_rect");
                ops.Add(Txt(rows[i], names[i]));
                if (active == number)
                    ops.Add(new Fill(new Box(box.X + box.W - MenuBuilder.MarkerSize - RowMarkerGap, box.Y + (box.H - MenuBuilder.MarkerSize) / 2, MenuBuilder.MarkerSize, MenuBuilder.MarkerSize), d.Accent, 1, MenuBuilder.MarkerSize / 2));
                buttons.Add(new MenuButton(prefix + number, box, new MenuAction(kind, Mark: number)) { FocusImage = selection });
            }
        }
        Rows(audioRows, audio, 1, "a", "audio", activeAudio);
        Rows(subRows, subs, 0, "s", "sub", activeSub);

        void Nav(string id, string key, string image, MenuAction action)
        {
            if (n.Child(key) is not { } node || Button(image) is not { } file) return;
            var box = Place(file, node.Rect());
            ops.Add(new Image(file, box));
            buttons.Add(new MenuButton(id, box, action) { FocusImage = Selected(file) });
        }
        Nav("play", "play", "playbuttontimage", c.Series ? new MenuAction("playall", Playlist: c.Titles![0].Playlist, Mark: c.Titles.Count) : new MenuAction("play", Playlist: c.Playlist));
        Nav("back", "home", "homebuttontimage", new MenuAction("page", "root"));
        return new MenuPage(id, ops, Link(buttons));
    }

    /// <summary>
    /// Remote-control navigation from the positions: the nearest button within 45° of each direction (cross axis counting double).
    /// Up and down fall back to any button on that side, so a thumbnail row can always reach the Home button below it.
    /// </summary>
    static List<MenuButton> Link(List<MenuButton> buttons)
    {
        static (double X, double Y) Centre(MenuButton b) => (b.Rect.X + b.Rect.W / 2.0, b.Rect.Y + b.Rect.H / 2.0);
        string? Nearest(MenuButton from, int dx, int dy)
        {
            var (fx, fy) = Centre(from);
            var ahead = buttons.Where(o => o != from)
                .Select(o => (o, p: Centre(o)))
                .Select(t => (t.o, along: (t.p.X - fx) * dx + (t.p.Y - fy) * dy, across: Math.Abs((t.p.X - fx) * dy) + Math.Abs((t.p.Y - fy) * dx)))
                .Where(t => t.along > 1).ToList();
            var cone = ahead.Where(t => t.across <= t.along);
            return (cone.Any() || dy == 0 ? cone : ahead).OrderBy(t => t.along + 2 * t.across).Select(t => t.o.Id).FirstOrDefault();
        }
        return buttons.Select(b => b with { Left = Nearest(b, -1, 0), Right = Nearest(b, 1, 0), Up = Nearest(b, 0, -1), Down = Nearest(b, 0, 1) }).ToList();
    }
}
