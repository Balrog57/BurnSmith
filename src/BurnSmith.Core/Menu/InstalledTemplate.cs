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
/// Tolerant XML reader: some template files contain mismatched closing tags (<c>&lt;home&gt;Home&lt;/scenes&gt;</c>)
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
/// A Blu-ray menu template (<c>template.bd.xml</c> + its PNGs) turned into <see cref="MenuPage"/>s: same artwork, same positions.
/// The files are read from the user's own installation at run time; nothing of them is stored in BurnSmith.
/// What the template cannot hold is filled in from the disc: title, chapter pictures and names, audio and subtitle names.
/// </summary>
public sealed class InstalledTemplate
{
    const int RowMarkerGap = 24;
    static readonly Dictionary<string, InstalledTemplate?> Cache = new(StringComparer.OrdinalIgnoreCase);

    readonly string dir;
    readonly XNode template, common;

    /// <summary>The normalized template tree, as read from any vendor format. The unified pack format saves exactly this tree.</summary>
    internal XNode Tree => template;
    /// <summary>The folder the template's own files are read from (a vendor folder, or a converted pack folder).</summary>
    internal string SourceDir => dir;
    /// <summary>True when this template was read from BurnSmith's own pack (JSON + PNG assets).</summary>
    public bool IsPack { get; }

    /// <summary>Why BurnSmith cannot draw this template (null = it can).</summary>
    public string? Unsupported { get; }
    /// <summary>What the template is, which decides how it is read (see <see cref="TemplateKind"/>).</summary>
    public TemplateKind Kind { get; }
    public string Name { get; }
    /// <summary>The neutral group label of <see cref="Kind"/> (see <see cref="TemplateLabel"/>).</summary>
    public string Category { get; }
    /// <summary>Selection colour the template itself defines, if any.</summary>
    public string? Accent => common.Child("accent")?.Text is { Length: > 0 } a ? a : null;
    public string? Thumbnail => thumbnail ?? new[] { "thumb4x3.bd.png", "Menu/background01.jpg" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
    /// <summary>Colour and font of the template's own texts, used for the pages BurnSmith adds.</summary>
    public string? UiColor => common.Child("ui_color")?.Text is { Length: > 0 } c ? c : null;
    public string? UiFont => common.Child("font")?.Text is { Length: > 0 } f ? f : null;
    readonly string? thumbnail;
    /// <summary>The picture behind the main menu (null when the file is missing or unreadable).</summary>
    public string? BackgroundFile => Asset(template.Child("rootmenu")?.Child("bg")?.Child("image")?.Text);

    InstalledTemplate(string dir, XNode template, TemplateKind kind, string? noLayout = null, string? thumbnail = null, bool pack = false)
    {
        this.dir = dir;
        this.thumbnail = thumbnail;
        this.template = template;
        Kind = kind;
        IsPack = pack;
        Category = TemplateLabel.Of(kind);
        common = template.Child("common") ?? new XNode("common", []);
        Name = common.Child("templatename")?.Text is { Length: > 0 } n ? n : Path.GetFileName(dir);
        Unsupported = noLayout ?? Check();
    }

    /// <summary>The template of a converted pack: the same normalized tree, read from <c>template.json</c> with its PNG assets beside it.</summary>
    static InstalledTemplate FromPack(string dir, TemplatePack pack)
    {
        var tree = pack.ToTree()!;
        return new InstalledTemplate(dir, tree, pack.KindValue, pack.Unsupported, pack.ThumbnailFile(dir), pack: true);
    }

    /// <summary>
    /// Null when the folder is not a template: a Blu-ray template (<c>template.bd.xml</c>), a DVD one (<c>template.xml</c> with flat values),
    /// a picture theme (<c>template.xml</c> / <c>template2.xml</c> with <c>TemplateDef</c>, or just <c>Menu</c> and <c>Scene</c> pictures),
    /// a <c>.dvdt</c> file, or a theme described by its own <c>config.xml</c>.
    /// </summary>
    public static InstalledTemplate? Load(string dir)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(dir, out var known)) return known;
            // one bad file (a size of "24pt", a huge picture, two buttons with one id) must not stop the scan of every other template, nor the editor for good
            try { return Cache[dir] = Parse(dir); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return Cache[dir] = new InstalledTemplate(dir, new XNode("template", []), TemplateKind.None, "modèle illisible : " + e.Message);
            }
        }
    }

    static InstalledTemplate? Parse(string dir)
    {
        // a converted pack (template.json + PNGs) is read exactly like a vendor template: the normalized tree is the same
        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, TemplatePack.FileName)) && TemplatePack.TryRead(dir) is { HasLayout: true } pack)
            return FromPack(dir, pack);
        var file = new[] { "template.bd.xml", "template.xml", "template2.xml" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
        InstalledTemplate? parsed = null;
        if (dir.EndsWith(".dvdt", StringComparison.OrdinalIgnoreCase) && File.Exists(dir)) // one file per template, pictures in <library>\backgrounds
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(dir);
            var wide = stem.EndsWith("WS", StringComparison.Ordinal);
            var name = (wide ? stem[..^2] : stem) + (wide ? " 16-9" : " 4-3");
            var folder = System.IO.Path.GetDirectoryName(dir)!;
            var tree = DvdtTemplate.Convert(LooseXml.Parse(File.ReadAllText(dir)), name, out var problem);
            parsed = new InstalledTemplate(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(folder))!, "backgrounds"),
                tree ?? DvdtTemplate.Failed(name), TemplateKind.Library, tree == null ? problem : null,
                System.IO.Path.ChangeExtension(dir, ".png") is var png && File.Exists(png) ? png : null);
        }
        else if (file == null && ThemeSkin.IsTheme(dir))
            parsed = ThemeSkin.Build(dir) is { } theme
                ? new InstalledTemplate(dir, theme, TemplateKind.Theme, null, System.IO.Path.Combine(dir, "thumb_169.jpg"))
                : new InstalledTemplate(dir, new XNode("template", []), TemplateKind.Theme, "mise en page illisible (fichier de thème absent ?)");
        else if (file != null)
        {
            var text = File.ReadAllText(file);
            text = System.Text.RegularExpressions.Regex.Replace(text, "(\\w+=\"\\d+)(\\s+)(\\w+=\")", "$1\"$2$3"); // some files miss a closing quote: Top="136 Width="209
            var doc = LooseXml.Parse(text);
            if (doc.Child("template") is { } t) parsed = new InstalledTemplate(dir, AllInOne(t), TemplateKind.BluRay);
            else if (doc.Child("Template") is { } s) parsed = new InstalledTemplate(dir, Simple(s, dir), TemplateKind.Dvd);
            else if (doc.Children("TemplateDef").FirstOrDefault(d => d.Attr("Aspect").StartsWith("16-9")) is { } def)
            {
                var layout = PictureLayout(def, doc.Children("TemplateDef").FirstOrDefault(d => d.Attr("Aspect") == "4-3"));
                // the thumbnail positions cover one another in a few files: its pictures are then laid out by BurnSmith
                var skin = Directory.Exists(Path.Combine(dir, "Menu")) && Directory.Exists(Path.Combine(dir, "Scene")) && ThumbnailsOverlap(layout);
                parsed = new InstalledTemplate(dir, skin ? PictureThemeSkin.Build(dir) : layout, TemplateKind.Pictures);
            }
        }
        else if (Directory.Exists(Path.Combine(dir, "Menu")) && Directory.Exists(Path.Combine(dir, "Scene")))
            parsed = new InstalledTemplate(dir, PictureThemeSkin.Build(dir), TemplateKind.Pictures); // pictures only: BurnSmith lays them out
        return parsed;
    }

    static bool ThumbnailsOverlap(XNode layout)
    {
        var r = layout.Child("scenes")?.Child("tscenes")?.Child("scene")?.Child("videoes")?.Children("video").Select(v => v.Rect()).ToList() ?? [];
        return r.Where((a, i) => r.Skip(i + 1).Any(b => Meets(a, b))).Any();
    }

    static bool Meets(Box a, Box b) => Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X) > a.W / 4 && Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y) > a.H / 4;

    static (int W, int H)? PngSize(string path)
    {
        if (!File.Exists(path)) return null;
        Span<byte> head = stackalloc byte[24];
        using var f = File.OpenRead(path);
        return f.Read(head) < 24 || head[0] != 0x89 ? null
            : (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[16..]), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[20..]));
    }

    /// <summary>
    /// A template laid out in one piece has no main menu: its first scene page is the menu. It becomes the root page
    /// (background, poster frame, volume name, Play and Scenes buttons in the template's font); the scene page keeps its thumbnails, gets the volume
    /// name as heading and a Home button at the top right (BurnSmith's own).
    /// </summary>
    static XNode AllInOne(XNode t)
    {
        if (t.Child("rootmenu") != null || t.Child("scenes")?.Child("tscenes")?.Child("scene") is not { } page) return t;
        var common = t.Child("common");
        string C(string key, string fallback) => common?.Child(key)?.Text is { Length: > 0 } v ? v : fallback;
        XNode El(string name, params (string Key, string Value)[] attrs) => new(name, attrs.ToDictionary(a => a.Key, a => a.Value));
        var volume = page.Child("texts")?.Children("text").FirstOrDefault(x => x.Attr("as") == "volume_name");
        XNode Heading() => volume != null ? new XNode("label", new Dictionary<string, string>(volume.Attrs)) : El("label", ("rect", "536,115,1330,60"));
        // the Play picture is drawn at its own size (266x63) in its rectangle: both buttons share that box, so Down from Play reaches Scenes
        XNode Button(string name, int y) => El(name, ("rect", $"600,{y},266,63"), ("font", C("font", "Arial")), ("font_size", C("font_size", "44")), ("color", C("font_color", "#ffffff")),
            ("bold", C("font_bold", "0")), ("italic", C("font_italic", "0")), ("align", "left"), ("textbutton", "1"));

        // allinone: with more than one scene the first scene page itself is the menu (see Pages); this root only serves a disc without scenes
        var root = El("rootmenu", ("allinone", "1"));
        root.Kids.Add(page.Child("bg") ?? El("bg"));
        if (page.Child("specialvideo")?.Child("video") is { } poster) root.Kids.Add(poster);
        root.Kids.AddRange([Heading(), Button("play", 330), Button("scenes", 430)]);
        if (page.Child("texts") is { } texts) root.Kids.Add(texts);
        t.Kids.Add(root);
        page.Kids.AddRange([Heading(), El("home", ("rect", "1740,120,110,70"), ("glyph", "⌂"))]);
        return t;
    }

    /// <summary>
    /// The DVD templates (<c>&lt;Template&gt;</c> with <c>label_left</c>, <c>video1_width</c>… values) rewritten as the Blu-ray tree,
    /// scaled from the size of their background (720x480 for a DVD set) to the 1920x1080 canvas.
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
        // some templates ship a 16:9 twin of the 4:3 background (bdbackground\bg0.png): the same layout (set on the 4:3 size) on artwork that is not stretched
        var wide = "bdbackground/" + System.IO.Path.GetFileName(background);
        if (!File.Exists(System.IO.Path.Combine(dir, wide)) || PngSize(System.IO.Path.Combine(dir, wide)) is not (1920, 1080)) wide = background;
        XNode Bg() { var bg = El("bg"); bg.Kids.Add(Leaf("image", wide)); return bg; }
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
            video.Kids.Add(El("name", ("rect", $"{r[0]},{r[1] + r[3] + 4},{r[2]},60"))); // the DVD templates have no name box: under the thumbnail
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
    /// A picture theme's <c>TemplateDef</c> (positions in 1920x1080 for the 16-9 set): text buttons on the background, a picture frame under the
    /// preview and under each thumbnail (whose position is an offset inside its frame), previous / next / back pictures.
    /// </summary>
    static XNode PictureLayout(XNode def, XNode? reference)
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
            // the 16-9 offsets of some files do not fit their own frame picture; the 4-3 block of the same file does: its proportions are used
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

    /// <summary>A PNG or JPEG file; some files of a portable copy hold memory garbage instead of a picture.</summary>
    static bool IsImage(string path)
    {
        if (!File.Exists(path)) return false;
        Span<byte> head = stackalloc byte[4];
        using var f = File.OpenRead(path);
        return f.Read(head) == 4 && (head is [0x89, 0x50, 0x4E, 0x47] or [0xFF, 0xD8, _, _]);
    }

    string? Asset(string? relative) => TemplatePack.Inside(dir, relative) is { } full && IsImage(full) ? full : null;

    /// <summary>
    /// The highlighted twin of a button picture: <c>play/0.normal.png</c> → <c>play/0.sub.png</c>. Null when the template has none
    /// (the UHD ones) or it is unreadable: the button then gets BurnSmith's accent frame.
    /// </summary>
    string? Selected(string? normal) => normal != null && IsImage(normal.Replace(".normal.", ".sub.")) ? normal.Replace(".normal.", ".sub.") : null;

    /// <summary>
    /// The DVD subpicture of a button: the template's highlighted artwork (<c>frame/0.dvd.sub.png</c> beside <c>frame/0.dvd.normal.png</c>) is
    /// exactly the DVD's <c>highlight</c> image, and its <c>.mask.</c> twin gives the shape. Only artwork that has such a twin qualifies — a star
    /// marker beside a text button is not a button picture, and there BurnSmith keeps its own four-colour frame.
    /// </summary>
    MenuButton Dvd(MenuButton b)
    {
        if (b.FocusImage is not { } art || !art.Contains(".sub.", StringComparison.Ordinal)) return b;
        var mask = art.Replace(".sub.", ".mask.");
        // the chosen state has its own picture in the template; Asset gives null when it is missing or not a picture
        return b with { DvdHighlightImage = art, DvdHighlightMask = IsImage(mask) && !IsBlank(mask) ? mask : null, DvdSelectImage = Asset(common.Child("selection")?.Text) };
    }

    /// <summary>The template draws a button picture at its own size, centred on the rectangle of the XML (which is often smaller).</summary>
    static Box Place(string picture, Box rect) =>
        PngSize(picture) is var (w, h) ? new Box(rect.X + (rect.W - w) / 2, rect.Y + (rect.H - h) / 2, w, h) : rect;

    /// <summary>A button picture shrunk (never enlarged) to fit inside the rectangle of the XML, centred in it.</summary>
    static Box Fit(string picture, Box rect)
    {
        if (PngSize(picture) is not var (w, h) || w <= 0 || h <= 0) return rect;
        var s = Math.Min(1.0, Math.Min((double)rect.W / w, (double)rect.H / h));
        int fw = (int)(w * s), fh = (int)(h * s);
        return new Box(rect.X + (rect.W - fw) / 2, rect.Y + (rect.H - fh) / 2, fw, fh);
    }

    /// <summary>The template has rows for the audio / subtitle choice (the UHD ones lay that page out differently).</summary>
    /// <summary>The page title the template places on its audio / subtitle page (<c>ASLangTitleText</c>), null when it has none.</summary>
    public Box? SetupTitle => template.Child("setup")?.Child("setuptitle") is { } n ? n.Rect() : null;

    /// <summary>The Back button the template places on that page (<c>Return</c>), null when it has none.</summary>
    public Box? SetupBack => template.Child("setup")?.Child("Return") is { } n ? n.Rect() : null;

    public bool HasSetupLists => (template.Child("setup")?.Child("audiolist")?.Children("title").Any() ?? false) || (template.Child("setup")?.Child("subtitlelist")?.Children("title").Any() ?? false);

    /// <summary>
    /// Le modèle décrit une seule liste de langues (<c>LangItemText</c>) : une page par catégorie au lieu d'une page à deux colonnes.
    /// C'est le seul interrupteur de ce mode : <see cref="SettingsIds"/>, donc les disques, l'aperçu et l'éditeur, en découlent.
    /// </summary>
    public bool HasSetupPages => template.Child("setup")?.Child("langitems")?.Children("item").Any() ?? false;

    /// <summary>Les pages de réglages de ce disque, celle où mène le bouton « Réglages » d'abord : une catégorie sans choix n'a pas de page.</summary>
    public List<string> SettingsIds(MenuContent c) => !HasSetupPages ? ["settings"]
        : [.. c.AudioNames.Count > 1 ? new[] { "settings_audio" } : [], .. c.SubtitleNames.Count > 0 ? new[] { "settings_sub" } : []];

    /// <summary>Artwork of the audio / subtitle page for BurnSmith's own rows: the template's background under a dark veil.</summary>
    public List<Op> SetupBackdrop(string? film = null)
    {
        var ops = new List<Op>();
        Background(ops, template.Child("setup") ?? template.Child("rootmenu")!, IsAllInOne ? film : null);
        ops.Add(new Fill(new Box(0, 0, MenuBuilder.W, MenuBuilder.H), "#000000", 0.45, 0));
        return ops;
    }

    string? Button(string key) => Asset(common.Child(key)?.Text);

    // ---- text -------------------------------------------------------------

    Text Txt(XNode n, string value, string align = "left")
    {
        var font = n.Attr("font", common.Child("font")?.Text ?? "Segoe UI");
        var hasFiles = Directory.EnumerateFiles(dir, "*.*").Any(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
        var fontSize = double.TryParse(n.Attr("font_size", common.Child("font_size")?.Text ?? "32"), out var size) ? size : 32;
        if (n.Attr("fit") == "1" && value.Length > 0) fontSize = Math.Min(fontSize, n.Rect().W / (value.Length * 0.6)); // a text that overflows its box shrinks
        return new Text(value, n.Rect(), fontSize,
            font, hasFiles ? dir : null, n.Attr("color", common.Child("font_color")?.Text ?? "#FFFFFF"),
            n.Attr("bold", common.Child("font_bold")?.Text ?? "0") == "1", n.Attr("italic", common.Child("font_italic")?.Text ?? "0") == "1", align,
            n.Attrs.ContainsKey("stroke_color") ? n.Attr("stroke_color") : null, double.TryParse(n.Attr("stroke_width"), out var sw) ? sw : 0);
    }

    // ---- shared page parts --------------------------------------------------

    /// <summary>
    /// The text zones this page of the template declares (the user writes in them, and an empty one draws nothing).
    /// <paramref name="autoInfo"/> stands in for an empty information line. A template that declares none has none.
    /// </summary>
    void TemplateTexts(List<Op> ops, List<MenuZone> zones, XNode page, MenuDesign d, string? autoInfo = null, bool noOverview = false)
    {
        var overview = 0;
        foreach (var t in page.Child("texts")?.Children("text") ?? [])
        {
            var kind = t.Attr("as");
            if (kind == "volume_name" && page.Child("label") == null)
            {
                ops.Add(Txt(t, d.Title));
                zones.Add(new("title", t.Rect()));
            }
            if (kind != "volume_category" && (kind != "title_overview" || noOverview)) continue;
            var key = kind == "volume_category" ? "info" : $"overview{++overview}";
            zones.Add(new("slot:" + key, t.Rect()));
            var value = d.TextZones.GetValueOrDefault(key) is { Length: > 0 } written ? written : key == "info" ? autoInfo : null;
            if (value != null) ops.Add(Txt(t, value) with { Wrap = t.Attr("wrap") == "1" });
        }
    }

    /// <param name="film">
    /// A picture of the disc's own film, for the templates whose background is a frame of some film (a template can ship one as a sample and plays
    /// the real film there): it replaces the template's picture, the template's veil stays over it.
    /// </param>
    void Background(List<Op> ops, XNode page, string? film = null)
    {
        if (film != null && File.Exists(film)) ops.Add(new Image(film, new Box(0, 0, MenuBuilder.W, MenuBuilder.H), Backdrop: true, Cover: true));
        else if (Asset(page.Child("bg")?.Child("image")?.Text) is { } bg)
        {
            var node = page.Child("bg")!;
            var box = node.Attrs.ContainsKey("rect") ? node.Rect() : new Box(0, 0, MenuBuilder.W, MenuBuilder.H);
            ops.Add(new Image(bg, node.Attr("fit") is { Length: > 0 } fit ? Fit(bg, box, fit) : box, Backdrop: true));
        }
        var cover = page.Child("bg")?.Child("covering_color")?.Text; // #AARRGGBB shade between the artwork and the thumbnails
        if (cover is { Length: 9 }) ops.Add(new Fill(new Box(0, 0, MenuBuilder.W, MenuBuilder.H), "#" + cover[^6..], Convert.ToInt32(cover.TrimStart('#')[..2], 16) / 255.0, 0));
    }

    /// <summary>The picture inside <paramref name="box"/> with its own proportions (SVG "meet"), aligned as the template says (xminymax, xmidymid...).</summary>
    static Box Fit(string file, Box box, string align)
    {
        try
        {
            if (MenuRenderer.Size(file) is not var (pw, ph)) return box;
            var k = Math.Min(box.W / (double)pw, box.H / (double)ph);
            double w = pw * k, h = ph * k;
            double ax = align.Contains("xmin") ? 0 : align.Contains("xmax") ? 1 : 0.5, ay = align.Contains("ymin") ? 0 : align.Contains("ymax") ? 1 : 0.5;
            return new Box((int)(box.X + (box.W - w) * ax), (int)(box.Y + (box.H - h) * ay), (int)w, (int)h);
        }
        catch (Exception e) when (e is NotSupportedException or System.IO.FileFormatException or IOException) { return box; }
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

    /// <summary>Some templates draw their picture frame first and the preview inside it (others put the frame on top of the picture).</summary>
    void Under(List<Op> ops, XNode video)
    {
        if (Asset(video.Attr("frame_under")) is { } file) ops.Add(new Image(file, video.Rect("under_rect")));
    }

    Framed Frame(XNode video, string? picture)
    {
        var frame = Asset(video.Attr("frame_image", common.Child("frame")?.Text ?? ""));
        var mask = frame == null ? null : Asset(System.IO.Path.GetRelativePath(dir, frame).Replace(".normal.", ".mask."));
        return new Framed(video.Rect(), picture, frame, mask == frame || IsBlank(mask) ? null : mask);
    }

    static readonly Dictionary<string, bool> Blanks = [];

    /// <summary>A mask without any opaque pixel would hide the whole picture: it is ignored.</summary>
    static bool IsBlank(string? png)
    {
        if (png == null) return false;
        lock (Blanks)
        {
            if (Blanks.TryGetValue(png, out var known)) return known;
            try
            {
                MenuRenderer.CheckSize(png);
                var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(png),
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0],
                    System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var px = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(px, bitmap.PixelWidth * 4, 0);
                return Blanks[png] = !Enumerable.Range(0, px.Length / 4).Any(i => px[i * 4 + 3] > 16);
            }
            catch (Exception e) when (e is NotSupportedException or System.IO.FileFormatException or IOException or InvalidDataException) { return Blanks[png] = false; }
        }
    }

    // ---- pages --------------------------------------------------------------

    public List<MenuPage> Pages(MenuDesign d, MenuContent c)
    {
        var cells = MenuBuilder.Cells(d, c);
        var scene = template.Child("scenes")!.Child("tscenes")!.Child("scene")!;
        // the same page as a list of lines when the design asks for it and the template describes one
        var listItems = d.SceneList ? scene.Child("list")?.Children("item").ToList() ?? [] : [];
        var perPage = listItems.Count > 0 ? listItems.Count : scene.Child("videoes")!.Children("video").Count();
        var total = cells.Count > 1 ? (cells.Count + perPage - 1) / perPage : 0;
        // a template with no menu of its own: its first page of thumbnails, with the poster and the title, is the menu
        var pages = new List<MenuPage> { (IsAllInOne || d.ScenesOnly) && total > 0 ? Scenes(d, cells, 1, total, perPage, d.Title, c.PreviewPicture, c) : Root(d, c, cells.Count > 1) };
        for (var i = (IsAllInOne || d.ScenesOnly) && total > 0 ? 2 : 1; i <= total; i++) pages.Add(Scenes(d, cells, i, total, perPage, IsAllInOne ? d.Title : c.Series ? d.EpisodesLabel : d.ScenesLabel, c.PreviewPicture)); // a one-piece template keeps the disc's title on every page
        if (c.HasBonus) // the template has no Bonus page: its scene page layout lists the extras
        {
            var bonus = MenuBuilder.BonusCells(c);
            // text Bonus page: the template's list of lines when it has one, else its thumbnail grid as before
            var bonusList = d.BonusText ? scene.Child("list")?.Children("item").Count() ?? 0 : 0;
            var look = d;
            if (bonusList > 0) { look = d.Clone(); look.SceneList = true; }
            var bonusPerPage = bonusList > 0 ? bonusList : perPage;
            var bonusPages = MenuBuilder.PageCount(bonus.Count, bonusPerPage);
            for (var i = 1; i <= bonusPages; i++) pages.Add(Scenes(look, bonus, i, bonusPages, bonusPerPage, d.BonusLabel, c.PreviewPicture, prefix: "bonus"));
        }
        if (c.HasSettings) pages.AddRange(SettingsIds(c).Select(id => MenuBuilder.Settings(d, c, null, null, id))); // BurnSmith's own rows when the template has none
        if (d.CreditLines().Count > 0) pages.Add(MenuBuilder.Credits(d, SetupBackdrop(c.PreviewPicture))); // the template has no such page: its background under a veil
        return pages;
    }

    bool IsAllInOne => template.Child("rootmenu")?.Attr("allinone") == "1";

    /// <summary>An episode's name with its number in front, as streaming services show it: "Épisode 3 · Le dojo" → "E03 - Le dojo"; a name already numbered (E03…) is kept.</summary>
    internal static string Numbered(MenuBuilder.Cell cell)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(cell.Label, @"^E\d+\b")) return cell.Label;
        var number = $"E{int.Parse(cell.Id[1..]):00}";
        var m = System.Text.RegularExpressions.Regex.Match(cell.Label, @"^Épisode \d+(?: · (.+))?$"); // DiscTitle.Readable's default name
        return m.Success ? (m.Groups[1].Success ? $"{number} - {m.Groups[1].Value}" : number) : $"{number} - {cell.Label}";
    }

    /// <summary>The page id of page <paramref name="number"/> of a list (scenes, bonus): the first scene page is the root when the template has no menu of its own.</summary>
    string PageId(MenuDesign d, string prefix, int number) => number == 1 && (IsAllInOne || d.ScenesOnly) && prefix == "scenes" ? "root" : $"{prefix}{number}";

    /// <summary>Capsules for the buttons the template has no picture for, in one row from <paramref name="x0"/>: the caller says where the row goes.</summary>
    static void Capsules(List<Op> ops, List<MenuButton> buttons, MenuDesign d, List<(string Id, string Label, MenuAction Action)> pills, int x0, int y, int w, int h, int gap, int size)
    {
        for (var k = 0; k < pills.Count; k++)
        {
            var box = new Box(x0 + k * (w + gap), y, w, h);
            ops.Add(MenuBuilder.IsLight(d.TextColor) ? new Fill(box, "#000000", 0.35, h / 2) : new Fill(box, "#FFFFFF", 0.45, h / 2)); // dark text gets a light capsule
            ops.Add(new Label(pills[k].Label, box, size, true));
            buttons.Add(new MenuButton(pills[k].Id, box, pills[k].Action));
        }
    }

    static readonly Dictionary<string, int> Floors = [];

    /// <summary>
    /// Row of the picture where its solid part ends (a banner has a glow below its body): 1 = the bottom of <paramref name="rect"/>.
    /// Capsules go under that, not over the artwork.
    /// </summary>
    static int SolidBottom(string? png, Box rect)
    {
        if (png == null) return rect.Y + rect.H;
        lock (Floors)
        {
            if (!Floors.TryGetValue(png, out var permille))
            {
                try
                {
                    MenuRenderer.CheckSize(png);
                    var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(png),
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0],
                        System.Windows.Media.PixelFormats.Bgra32, null, 0);
                    int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
                    var px = new byte[w * h * 4];
                    bitmap.CopyPixels(px, w * 4, 0);
                    var row = h - 1;
                    while (row > 0 && !Enumerable.Range(0, w).Any(x => px[(row * w + x) * 4 + 3] >= 200)) row--;
                    permille = (row + 1) * 1000 / h;
                }
                catch (Exception e) when (e is NotSupportedException or System.IO.FileFormatException or IOException or InvalidDataException) { permille = 1000; }
                Floors[png] = permille;
            }
            return rect.Y + rect.H * permille / 1000;
        }
    }

    MenuPage Root(MenuDesign d, MenuContent c, bool withScenes)
    {
        var n = template.Child("rootmenu")!;
        var ops = new List<Op>();
        Background(ops, n, IsAllInOne ? c.PreviewPicture : null);
        Pictures(ops, n);
        var zones = new List<MenuZone>();
        if (n.Child("video") is { } video && d.ShowPreview)
        {
            Under(ops, video);
            ops.Add(Frame(video, d.PosterOr(c.PreviewPicture)));
            zones.Add(new("poster", video.Rect()));
        }
        if (n.Child("label") is { } label)
        {
            ops.Add(Txt(label, d.Title, label.Attr("align", "left")));
            zones.Add(new("title", label.Rect()));
        }
        TemplateTexts(ops, zones, n, d);

        var buttons = new List<MenuButton>();
        void Add(string id, XNode? node, string? image, MenuAction action, string label)
        {
            if (node == null) return;
            if (image != null)
            {
                var box = Place(image, node.Rect());
                ops.Add(new Image(image, box));
                buttons.Add(Dvd(new MenuButton(id, box, action) { FocusImage = Selected(image) }));
            }
            else if (node.Attr("textbutton") == "1") // DVD templates: the label is text, a star marks the selected one
            {
                var r = node.Rect();
                var star = Asset(common.Child("selection")?.Text);
                var slot = new Box(r.X - r.H, r.Y, r.H, r.H);
                ops.Add(Txt(node, label, node.Attr("align", "center")));
                buttons.Add(Dvd(star == null ? new MenuButton(id, r, action) : new MenuButton(id, new Box(slot.X, r.Y, r.W + r.H, r.H), action) { FocusImage = star, FocusSlot = slot }));
            }
        }
        Add("play", n.Child("play"), Button("playbuttontimage"),
            c.Series ? new MenuAction("playall", Playlist: c.Titles![0].Playlist, Mark: c.Titles.Count) : new MenuAction("play", Playlist: c.Playlist),
            c.Series ? d.PlayAllLabel : d.PlayLabel);
        if (withScenes) Add("scenes", n.Child("scenes"), Button("scenebuttontimage"), new MenuAction("page", "scenes1"), c.Series ? d.EpisodesLabel : d.ScenesLabel);
        // "none_setup" templates have no picture for Settings, and none has one for Bonus or Credits: plain capsules in a row under the other buttons, BurnSmith's own addition
        var pills = new List<(string Id, string Label, MenuAction Action)>();
        // a template that draws a Bonus button (<c>BonusText</c>) puts it where it says; the others get BurnSmith's capsule
        if (c.HasBonus)
        {
            if (n.Child("bonus")?.Attr("textbutton") == "1") Add("bonus", n.Child("bonus"), null, new MenuAction("page", "bonus1"), d.BonusLabel);
            else pills.Add(("bonus", d.BonusLabel, new MenuAction("page", "bonus1")));
        }
        if (c.HasSettings)
        {
            var settings = new MenuAction("page", SettingsIds(c)[0]);
            if (Button("setupbuttontimage") != null || n.Child("setup")?.Attr("textbutton") == "1") Add("settings", n.Child("setup"), Button("setupbuttontimage"), settings, d.SettingsLabel);
            else pills.Add(("settings", d.SettingsLabel, settings));
        }
        if (d.CreditLines().Count > 0) pills.Add(("credits", d.CreditsLabel, new MenuAction("page", "credits")));
        if (pills.Count > 0)
        {
            const int pillW = 320, pillGap = 20;
            var low = buttons.Count == 0 ? MenuBuilder.H / 2 : buttons.Max(b => b.Rect.Y + b.Rect.H);
            foreach (var pic in n.Child("pics")?.Children("pic") ?? []) low = Math.Max(low, SolidBottom(Asset(pic.Attr("image")), pic.Rect())); // under a banner, not over it
            var middle = buttons.Count == 0 ? MenuBuilder.W / 2 : (int)buttons.Average(b => b.Rect.X + b.Rect.W / 2.0);
            var row = pills.Count * pillW + (pills.Count - 1) * pillGap;
            var x0 = Math.Clamp(middle - row / 2, 20, MenuBuilder.W - row - 20);
            Capsules(ops, buttons, d, pills, x0, Math.Min(low + 8, MenuBuilder.H - 70), pillW, 58, pillGap, 36);
        }
        return new MenuPage("root", ops, Link(buttons)) { Zones = zones };
    }

    /// <param name="root">Not null when this page is the menu itself (a template laid out in one piece): it gets the disc's own lines and the Play / Settings / Credits capsules.</param>
    MenuPage Scenes(MenuDesign d, List<MenuBuilder.Cell> cells, int number, int total, int perPage, string title, string? preview, MenuContent? root = null, string prefix = "scenes")
    {
        var s = template.Child("scenes")!.Child("tscenes")!.Child("scene")!;
        var ops = new List<Op>();
        Background(ops, s, IsAllInOne ? preview : null);
        Pictures(ops, s);
        var zones = new List<MenuZone>();
        if (s.Child("specialvideo")?.Child("video") is { } poster && d.ShowPreview) // a template laid out in one piece: the poster stays beside the thumbnails
        {
            ops.Add(Frame(poster, d.PosterOr(preview)));
            zones.Add(new("poster", poster.Rect()));
        }
        var buttons = new List<MenuButton>();
        if (s.Child("label") is { } heading)
        {
            var text = Txt(heading, title);
            ops.Add(root == null ? text : text with { Rect = text.Rect with { W = 700 } }); // the capsules take the right of the title line
            if (root != null) zones.Add(new("title", heading.Rect() with { W = 700 }));
        }
        if (root != null)
        {
            var capsules = new List<(string Id, string Label, MenuAction Action)>
            {
                ("play", root.Series ? d.PlayAllLabel : d.PlayLabel, root.Series ? new MenuAction("playall", Playlist: root.Titles![0].Playlist, Mark: root.Titles.Count) : new MenuAction("play", Playlist: root.Playlist))
            };
            if (root.HasBonus) capsules.Add(("bonus", d.BonusLabel, new MenuAction("page", "bonus1")));
            if (root.HasSettings) capsules.Add(("settings", d.SettingsLabel, new MenuAction("page", SettingsIds(root)[0])));
            if (d.CreditLines().Count > 0) capsules.Add(("credits", d.CreditsLabel, new MenuAction("page", "credits")));
            const int w = 190, gap = 12;
            Capsules(ops, buttons, d, capsules, MenuBuilder.W - 60 - capsules.Count * w - (capsules.Count - 1) * gap, 118, w, 56, gap, 28);
        }
        string? autoInfo = null;
        if (root != null) // until the user writes the information line, it says what the disc holds
        {
            var info = new List<string> { $"{cells.Count} {(root.Series ? d.EpisodesLabel : d.ScenesLabel)}" };
            if (root.HasBonus) info.Add($"{root.Bonus!.Count} {d.BonusLabel}");
            if (root.AudioNames.Count > 1) info.Add($"{root.AudioNames.Count} {d.AudioLabel}");
            if (root.SubtitleNames.Count > 0) info.Add($"{root.SubtitleNames.Count} {d.SubtitlesLabel}");
            autoInfo = string.Join("  |  ", info);
        }
        var slots = s.Child("videoes")!.Children("video").ToList();
        var onPage = cells.Skip((number - 1) * perPage).Take(perPage).ToList();
        // a one-piece template's episodes are numbered (E01 - Name), and their summaries take the text zones' place: one row per episode, name over summary
        var episodes = IsAllInOne && prefix == "scenes" && onPage.All(cell => cell.Id.StartsWith('e'));
        string Shown(MenuBuilder.Cell cell) => episodes ? Numbered(cell) : cell.Label;
        var summaries = episodes && onPage.Any(cell => cell.Overview is { Length: > 0 });
        TemplateTexts(ops, zones, s, d, autoInfo, summaries);
        if (summaries && s.Child("texts")?.Children("text").FirstOrDefault(t => t.Attr("as") == "title_overview") is { } area)
        {
            var top = area.Rect().Y;
            var row = ((slots.Count > 0 ? slots.Min(v => v.Rect().Y) - 12 : MenuBuilder.H - 120) - top) / perPage;
            var grey = slots.FirstOrDefault()?.Child("name") is { } n ? Txt(n, "").Color : "#C8C8C8";
            for (var i = 0; i < onPage.Count; i++)
            {
                var y = top + i * row;
                var name = Txt(area, Shown(onPage[i])) with { Rect = new Box(area.Rect().X, y, area.Rect().W, 32), Size = 26, Bold = true };
                ops.Add(name);
                if (onPage[i].Overview is { Length: > 0 } summary)
                    ops.Add(Txt(area, summary) with { Rect = new Box(area.Rect().X, y + 34, area.Rect().W, row - 40), Size = 21, Bold = false, Color = grey, Wrap = true });
            }
        }
        var lines = d.SceneList ? s.Child("list")?.Children("item").ToList() ?? [] : [];
        if (lines.Count > 0)
        {
            // the list style: one line of text per chapter, at the rectangles the template gives, without a frame
            for (var i = 0; i < lines.Count && (number - 1) * perPage + i < cells.Count; i++)
            {
                var cell = cells[(number - 1) * perPage + i];
                ops.Add(Txt(lines[i], Shown(cell), lines[i].Attr("align", "left")));
                if (cell.Action.Kind == "play") zones.Add(new($"name:{cell.Action.Playlist}", lines[i].Rect()));
                buttons.Add(new MenuButton(cell.Id, lines[i].Rect(), cell.Action));
            }
        }
        for (var i = 0; i < slots.Count && lines.Count == 0 && (number - 1) * perPage + i < cells.Count; i++)
        {
            var cell = cells[(number - 1) * perPage + i];
            Under(ops, slots[i]);
            var framed = Frame(slots[i], cell.Picture);
            ops.Add(framed);
            if (slots[i].Child("name") is { } name)
            {
                ops.Add(Txt(name, Shown(cell), "center"));
                if (cell.Action.Kind == "play") zones.Add(new($"name:{cell.Action.Playlist}", name.Rect()));
            }
            buttons.Add(Dvd(new MenuButton(cell.Id, slots[i].Rect(), cell.Action) { FocusImage = Selected(framed.Frame) })); // none for picture themes: the accent frame
        }
        void Glyph(string id, Box r, string glyph, MenuAction action)
        {
            ops.AddRange(MenuBuilder.Button(d, r, glyph, Math.Max(24, r.H * 2 / 3)));
            buttons.Add(new MenuButton(id, r, action));
        }
        void Nav(string id, string key, string image, string fallbackGlyph, MenuAction action)
        {
            if (s.Child(key) is not { } node) return;
            // some templates draw their arrows as vector art, and a template's picture can be missing or unreadable: BurnSmith draws a text button there
            var drawn = node.Attr("glyph");
            if (Button(image) is not { } file || drawn.Length > 0) { Glyph(id, node.Rect(), drawn.Length > 0 ? drawn : fallbackGlyph, action); return; }
            var box = Place(file, node.Rect());
            ops.Add(new Image(file, box));
            buttons.Add(Dvd(new MenuButton(id, box, action) { FocusImage = Selected(file) }));
        }
        if (number > 1) Nav("prev", "left", "leftbuttontimage", "<", new MenuAction("page", PageId(d, prefix, number - 1)));
        if (root == null) Nav("home", "home", "homebuttontimage", "⌂", new MenuAction("page", "root"));
        if (number < total) Nav("next", "right", "rightbuttontimage", ">", new MenuAction("page", PageId(d, prefix, number + 1)));
        // whatever the template drew, a scene page must lead back to the menu and on to its neighbours: BurnSmith adds the missing buttons at the bottom
        bool Has(string id) => buttons.Any(b => b.Id == id);
        if (root == null && !Has("home")) Glyph("home", new Box(MenuBuilder.W / 2 - 55, MenuBuilder.H - 110, 110, 70), "⌂", new MenuAction("page", "root"));
        if (number > 1 && !Has("prev")) Glyph("prev", new Box(MenuBuilder.W / 2 - 195, MenuBuilder.H - 110, 110, 70), "<", new MenuAction("page", PageId(d, prefix, number - 1)));
        if (number < total && !Has("next")) Glyph("next", new Box(MenuBuilder.W / 2 + 85, MenuBuilder.H - 110, 110, 70), ">", new MenuAction("page", PageId(d, prefix, number + 1)));
        return new MenuPage(PageId(d, prefix, number), ops, Link(buttons)) { Zones = zones };
    }

    /// <summary>Row width of the audio list: the BD-J menu draws its "active" dot a fixed distance from each row's left edge.</summary>
    public int MarkerOffset => (template.Child("setup")?.Child("audiolist")?.Child("title")?.Rect("frame_rect").W ?? 600) - MenuBuilder.MarkerSize - RowMarkerGap + MenuBuilder.FocusMargin;

    public MenuPage Settings(MenuDesign d, MenuContent c, int? activeAudio, int? activeSub, string id)
    {
        if (HasSetupPages) return SetupPage(d, c, activeAudio, activeSub, id);
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
                buttons.Add(Dvd(new MenuButton(prefix + number, box, new MenuAction(kind, Mark: number)) { FocusImage = selection }));
            }
        }
        Rows(audioRows, audio, 1, "a", "audio", activeAudio);
        Rows(subRows, subs, 0, "s", "sub", activeSub);

        var navs = new[]
        {
            (Id: "play", Key: "play", Image: "playbuttontimage", Action: c.Series ? new MenuAction("playall", Playlist: c.Titles![0].Playlist, Mark: c.Titles.Count) : new MenuAction("play", Playlist: c.Playlist)),
            (Id: "back", Key: "home", Image: "homebuttontimage", Action: new MenuAction("page", "root")),
        }.Where(x => n.Child(x.Key) != null && Button(x.Image) != null).Select(x => (x.Id, x.Action, File: Button(x.Image)!, Rect: n.Child(x.Key)!.Rect())).ToList();
        // these rectangles are sized for a small button (like "Set up"): at the pictures' own size Play and Home would cover one another, so they are shrunk to their rectangle
        var crowded = navs.Count == 2 && Meets(Place(navs[0].File, navs[0].Rect), Place(navs[1].File, navs[1].Rect));
        foreach (var (navId, action, file, rect) in navs)
        {
            var box = crowded ? Fit(file, rect) : Place(file, rect);
            ops.Add(new Image(file, box));
            buttons.Add(Dvd(new MenuButton(navId, box, action) { FocusImage = Selected(file) }));
        }
        return new MenuPage(id, ops, Link(buttons));
    }

    /// <summary>
    /// Une page de réglages à une liste : celle de l'audio (<c>settings_audio…</c>) ou celle des sous-titres (<c>settings_sub…</c>), aux coordonnées du thème.
    /// Le point de la piste active est à gauche de la ligne, comme dans les colonnes de BurnSmith : c'est là que <see cref="MenuBuilder.MarkerOffset"/> place celui du xlet.
    /// </summary>
    MenuPage SetupPage(MenuDesign d, MenuContent c, int? activeAudio, int? activeSub, string id)
    {
        var n = template.Child("setup")!;
        var isSub = id.StartsWith("settings_sub", StringComparison.Ordinal);
        var rows = n.Child("langitems")!.Children("item").ToList();
        var names = isSub ? new[] { d.OffLabel }.Concat(c.SubtitleNames).ToList() : c.AudioNames.ToList();
        var ops = new List<Op>();
        Background(ops, n);
        // le titre nomme la catégorie : le libellé du thème (SetupAudioText, SetupSubtitleText) recouvrirait les lignes dès la 3e en audio, la 4e en sous-titres
        if (n.Child("setuptitle") is { } title) ops.Add(Txt(title, isSub ? d.SubtitlesLabel : d.AudioLabel));

        var buttons = new List<MenuButton>();
        var first = isSub ? 0 : 1;
        var active = isSub ? activeSub : activeAudio;
        // les plafonds de MenuContent (6 pistes audio, 5 sous-titres) laissent moins de lignes que les huit du thème : pas de pagination à faire
        for (var i = 0; i < names.Count && i < rows.Count; i++)
        {
            var box = rows[i].Rect();
            ops.Add(Txt(rows[i], names[i]));
            if (active == first + i)
                ops.Add(new Fill(new Box(box.X - MenuBuilder.MarkerSize - MenuBuilder.MarkerGap, box.Y + (box.H - MenuBuilder.MarkerSize) / 2, MenuBuilder.MarkerSize, MenuBuilder.MarkerSize), d.Accent, 1, MenuBuilder.MarkerSize / 2));
            buttons.Add(new MenuButton((isSub ? "s" : "a") + (first + i), box, new MenuAction(isSub ? "sub" : "audio", Mark: first + i)));
        }
        void Nav(string key, string glyph, MenuAction action, string? caption)
        {
            if (n.Child(key) is not { } node) return;
            var r = node.Rect();
            if (Asset(node.Attr("image")) is { } file) ops.Add(new Image(file, r));
            else ops.AddRange(MenuBuilder.Button(d, r, glyph, Math.Max(24, r.H * 2 / 3)));
            // la flèche du thème ne dit pas où elle mène : son nom à côté d'elle, en dehors du bouton
            if (caption != null && rows.Count > 0)
            {
                const int gap = 16;
                var left = key == "prev";
                var room = left ? new Box(40, r.Y, r.X - gap - 40, r.H) : new Box(r.X + r.W + gap, r.Y, MenuBuilder.W - 40 - r.X - r.W - gap, r.H);
                ops.Add(Txt(rows[0], caption, left ? "right" : "left") with { Rect = room });
            }
            buttons.Add(new MenuButton(key == "Return" ? "back" : key, r, action));
        }
        var both = SettingsIds(c).Count > 1;
        if (both && isSub) Nav("prev", "<", new MenuAction("page", "settings_audio"), d.AudioLabel);
        Nav("Return", "⌂", new MenuAction("page", "root"), null);
        if (both && !isSub) Nav("next", ">", new MenuAction("page", "settings_sub"), d.SubtitlesLabel);
        return new MenuPage(id, ops, Link(buttons));
    }

    /// <summary>
    /// Remote-control navigation from the positions: left/right stay in the same row and up/down in the same column when the row or column has
    /// another button (a Settings button centred under Play and Scenes belongs to the next row); otherwise the nearest button within 45°
    /// of each direction (cross axis counting double).
    /// Up and down fall back to any button on that side, so a thumbnail row can always reach the Home button below it.
    /// </summary>
    static List<MenuButton> Link(List<MenuButton> buttons)
    {
        static (double X, double Y) Centre(MenuButton b) => (b.Rect.X + b.Rect.W / 2.0, b.Rect.Y + b.Rect.H / 2.0);
        // share of the smaller button's extent that the two have in common across the direction of travel
        static bool SameLine(MenuButton a, MenuButton o, int dx)
        {
            var (a0, a1, o0, o1) = dx != 0 ? (a.Rect.Y, a.Rect.Y + a.Rect.H, o.Rect.Y, o.Rect.Y + o.Rect.H) : (a.Rect.X, a.Rect.X + a.Rect.W, o.Rect.X, o.Rect.X + o.Rect.W);
            return Math.Min(a1, o1) - Math.Max(a0, o0) >= 0.5 * Math.Min(a1 - a0, o1 - o0);
        }
        string? Nearest(MenuButton from, int dx, int dy)
        {
            var (fx, fy) = Centre(from);
            var ahead = buttons.Where(o => o != from)
                .Select(o => (o, p: Centre(o)))
                .Select(t => (t.o, along: (t.p.X - fx) * dx + (t.p.Y - fy) * dy, across: Math.Abs((t.p.X - fx) * dy) + Math.Abs((t.p.Y - fy) * dx)))
                .Where(t => t.along > 1).ToList();
            var line = ahead.Where(t => SameLine(from, t.o, dx)).ToList();
            var cone = ahead.Where(t => t.across <= t.along);
            return (line.Count > 0 ? line : cone.Any() || dy == 0 ? cone : ahead).OrderBy(t => t.along + 2 * t.across).Select(t => t.o.Id).FirstOrDefault();
        }
        return Reachable(buttons.Select(b => b with { Left = Nearest(b, -1, 0), Right = Nearest(b, 1, 0), Up = Nearest(b, 0, -1), Down = Nearest(b, 0, 1) }).ToList());
    }

    /// <summary>
    /// A staggered layout can leave a button that no direction leads to (a thumbnail below and to the left of the first row, which the "same column"
    /// rule skips): the nearest reachable button that has a free direction towards it gets that edge. Pages where every button is reachable are untouched.
    /// </summary>
    static List<MenuButton> Reachable(List<MenuButton> list)
    {
        static (double X, double Y) Centre(MenuButton b) => (b.Rect.X + b.Rect.W / 2.0, b.Rect.Y + b.Rect.H / 2.0);
        static string? Slot(MenuButton b, string direction) => direction switch { "Left" => b.Left, "Right" => b.Right, "Up" => b.Up, _ => b.Down };
        for (var round = 0; round < list.Count; round++)
        {
            var seen = new HashSet<string> { list[0].Id };
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var b in list.Where(b => seen.Contains(b.Id)).ToList())
                    foreach (var n in new[] { b.Left, b.Right, b.Up, b.Down })
                        if (n != null && seen.Add(n)) grew = true;
            }
            if (list.FirstOrDefault(b => !seen.Contains(b.Id)) is not { } lost) break;
            var (lx, ly) = Centre(lost);
            var choices = new List<(double Distance, int At, string Direction)>();
            for (var i = 0; i < list.Count; i++)
            {
                if (!seen.Contains(list[i].Id)) continue;
                var (x, y) = Centre(list[i]);
                double dx = lx - x, dy = ly - y;
                var (horizontal, vertical) = (dx > 0 ? "Right" : "Left", dy > 0 ? "Down" : "Up");
                var free = (Math.Abs(dx) >= Math.Abs(dy) ? new[] { horizontal, vertical } : [vertical, horizontal]).FirstOrDefault(d => Slot(list[i], d) == null);
                if (free != null) choices.Add((dx * dx + dy * dy, i, free));
            }
            if (choices.Count == 0) break; // every direction of every reachable button is taken: left as it is
            var (_, at, direction) = choices.MinBy(c => c.Distance);
            list[at] = direction switch
            {
                "Left" => list[at] with { Left = lost.Id },
                "Right" => list[at] with { Right = lost.Id },
                "Up" => list[at] with { Up = lost.Id },
                _ => list[at] with { Down = lost.Id },
            };
        }
        return list;
    }
}
