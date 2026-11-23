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
public sealed partial class InstalledTemplate
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
    /// <summary>
    /// The moving background a template ships (a clip the program plays behind its menu: <c>bdmv\STREAM\00099.m2ts</c> in the template's folder,
    /// <c>loop.m2ts</c> in a converted pack); null for a still template.
    /// </summary>
    public static string? LoopClip(string dir) => File.Exists(dir) ? null
        : File.Exists(Path.Combine(dir, PackedLoop)) ? Path.Combine(dir, PackedLoop)
        : Directory.Exists(Path.Combine(dir, "bdmv", "STREAM")) ? Directory.EnumerateFiles(Path.Combine(dir, "bdmv", "STREAM"), "*.m2ts").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
    public const string PackedLoop = "loop.m2ts";

    /// <summary>The picture behind the main menu (null when the file is missing or unreadable).</summary>
    public string? BackgroundFile => Asset(template.Child("rootmenu")?.Child("bg")?.Child("image")?.Text);

    InstalledTemplate(string dir, XNode template, TemplateKind kind, string? noLayout = null, string? thumbnail = null, bool pack = false)
    {
        this.dir = dir;
        this.thumbnail = thumbnail;
        this.template = UhdSetup(template);
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
            // the layout of the folder's own shape (…\4-3\business) when the file has it, else the 16-9 one
            else if (doc.Children("TemplateDef").ToList() is var defs && (defs.FirstOrDefault(d => d.Attr("Aspect") == System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir)))
                         ?? defs.FirstOrDefault(d => d.Attr("Aspect").StartsWith("16-9"))) is { } def)
            {
                var layout = PictureLayout(def, def.Attr("Aspect") == "4-3" ? null : defs.FirstOrDefault(d => d.Attr("Aspect") == "4-3"));
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

    internal static (int W, int H)? PngSize(string path)
    {
        if (!File.Exists(path)) return null;
        if (MenuRenderer.IsSvg(path)) return MenuRenderer.Size(path); // a traced button says its size as its picture did
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
    /// The UHD templates lay their setup out as three pages: Audio, Subtitles and Back as picture buttons (<c>setup/buttons</c>), then one list
    /// on a panel per category (<c>audio_setup</c>, <c>subtitle_setup</c>). Rewritten as a theme's setup pages (<see cref="HasSetupPages"/>),
    /// so <see cref="SetupPage"/>, the discs and the editor draw them with the template's own pictures.
    /// </summary>
    static XNode UhdSetup(XNode t)
    {
        if (t.Child("setup") is not { } setup || setup.Child("langitems") != null || setup.Child("buttons") is not { } buttons
            || t.Child("audio_setup") is not { } list || list.Child("audiolist")?.Children("title").ToList() is not { Count: > 0 } titles) return t;
        XNode Copy(string name, XNode from, params (string Key, string Value)[] extra)
        {
            var n = new XNode(name, new Dictionary<string, string>(from.Attrs, StringComparer.OrdinalIgnoreCase));
            foreach (var (k, v) in extra) n.Attrs[k] = v;
            return n;
        }
        foreach (var b in buttons.Children("button"))
            if (b.Attr("type") switch { "audio" => "setupaudio", "subtitle" => "setupsub", "back" => "Return", _ => null } is { } role) setup.Kids.Add(Copy(role, b, ("align", "center")));
        setup.Kids.RemoveAll(k => k.Name is "pics" or "texts");
        if (list.Child("pics") is { } pics) setup.Kids.Add(pics); // the panel under the list
        if (list.Child("texts")?.Children("text").FirstOrDefault() is { } title) setup.Kids.Add(Copy("setuptitle", title, ("align", "center")));
        var rows = new XNode("langitems", []);
        // the row is the frame's height (the button), the text where the template writes it
        foreach (var row in titles)
        {
            Box r = row.Rect(), f = row.Rect("frame_rect");
            rows.Kids.Add(Copy("item", row, ("rect", $"{r.X},{f.Y},{r.W},{f.H}")));
        }
        setup.Kids.Add(rows);
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
        var (bw, bh) = (TemplatePack.Inside(dir, background) is { } inside ? PngSize(inside) : null) ?? (MenuBuilder.W, MenuBuilder.H);
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
        int Raw(XNode? n, string name) => int.TryParse(A(n, name), out var v) ? v : 0;
        var menu = def.Child("Menu");
        var page = def.Child("Scene")?.Child("Page");
        var frame = menu?.Child("Frame");
        var screen = menu?.Child("videoscr");
        var buttonColors = def.Child("Color")?.Child("Button");
        // the canvas the numbers are written on: 768x576 for the 4-3 set, drawn with its proportions between two bars; 1920x1080 for the others,
        // "16-9-2" included in the program's own layouts (its pictures are only twice as large) — the loose files beside two themes use 3840x2160
        var narrow = def.Attr("Aspect") == "4-3";
        var k = narrow ? MenuBuilder.H / 576.0 : Raw(frame, "Left") + Raw(frame, "Width") > MenuBuilder.W || Raw(page?.Child("ButtonNext"), "Left") > MenuBuilder.W ? 0.5 : 1.0;
        var dx = narrow ? (MenuBuilder.W - (int)(768 * k)) / 2 : 0;
        int I(XNode? n, string name) => (int)(Raw(n, name) * k);
        int X(XNode? n) => dx + I(n, "Left");
        XNode El(string name, params (string Key, string Value)[] attrs) => new(name, attrs.ToDictionary(a => a.Key, a => a.Value));
        XNode Leaf(string name, string text) { var n = El(name); n.Content.Append(text); return n; }
        XNode Styled(string name, XNode? style, string rect, params (string, string)[] more) =>
            El(name, [("rect", rect), ("font", A(style, "Type", "Tahoma")), ("font_size", (I(style, "Size") is > 0 and var px ? px : 38).ToString()), ("color", A(style, "Color", "#FFFFFF")),
                ("bold", A(style, "Bold") == "true" ? "1" : "0"), ("italic", A(style, "Italic") == "true" ? "1" : "0"), .. more]);
        string Rect(XNode? n) => $"{X(n)},{I(n, "Top")},{I(n, "Width")},{I(n, "Height")}";
        string Text(XNode? style, int width = 700) // the scene page's title: written from Left
        {
            var size = I(style, "Size") is > 0 and var s ? s : 38;
            return $"{X(style)},{I(style, "Top")},{width},{size * 3 / 2}";
        }
        // the menu's own texts end at a fixed distance from Left, whatever their length and their Alignment (which only matters for a text on
        // several lines): the program gives them a right edge, Left + 327 for the title and Left + 172 for a button (173 and 85 on the 4-3
        // canvas), and writes each one up to 4 px before it. Read in the program's code, not seen on one of its discs.
        string Ending(XNode? style, int after)
        {
            var size = I(style, "Size") is > 0 and var s ? s : 38;
            var right = dx + (int)((Raw(style, "Left") + after - 4) * k);
            var left = Math.Max(dx + 20, right - 1000);
            // a text placed to the right of the preview (education01) has only the room up to it: a longer title than the program's "My BD" is
            // drawn smaller rather than over the frame
            if (Raw(style, "Left") >= Raw(frame, "Left") + Raw(frame, "Width") / 2) left = Math.Min(right - 100, Math.Max(left, X(frame) + I(frame, "Width") + 8));
            return $"{left},{I(style, "Top")},{right - left},{size * 3 / 2}";
        }
        string Picture(string folder, XNode? n) => folder + "/" + A(n, "FileName");

        var common = El("common");
        common.Kids.AddRange([Leaf("accent", A(buttonColors, "High")), Leaf("leftbuttontimage", Picture("Scene", page?.Child("ButtonPre"))),
            Leaf("rightbuttontimage", Picture("Scene", page?.Child("ButtonNext"))), Leaf("homebuttontimage", Picture("Scene", page?.Child("Return")))]);

        XNode Bg(string file)
        {
            var bg = narrow ? El("bg", ("rect", $"{dx},0,{MenuBuilder.W - 2 * dx},{MenuBuilder.H}")) : El("bg");
            bg.Kids.Add(Leaf("image", file));
            return bg;
        }
        // the frame picture is drawn over the film's picture, as the program does: its leaves or its snow overlap the picture's edge
        var root = El("rootmenu");
        root.Kids.AddRange([Bg(Picture("Menu", menu?.Child("Background"))), Styled("label", menu?.Child("TitleText"), Ending(menu?.Child("TitleText"), narrow ? 173 : 327), ("align", "right")),
            El("video", ("rect", $"{X(frame) + I(screen, "Left")},{I(frame, "Top") + I(screen, "Top")},{I(screen, "Width")},{I(screen, "Height")}"),
                ("frame_over", Picture("Menu", frame)), ("over_rect", Rect(frame))),
            Styled("play", menu?.Child("ButtonPlayText"), Ending(menu?.Child("ButtonPlayText"), narrow ? 85 : 172), ("textbutton", "1"), ("align", "right")),
            Styled("scenes", menu?.Child("ButtonScenesText"), Ending(menu?.Child("ButtonScenesText"), narrow ? 85 : 172), ("textbutton", "1"), ("align", "right"))]);

        var scene = El("scene");
        scene.Kids.AddRange([Bg(Picture("Scene", page?.Child("Background"))), El("left", ("rect", Rect(page?.Child("ButtonPre")))), El("right", ("rect", Rect(page?.Child("ButtonNext")))),
            El("home", ("rect", Rect(page?.Child("Return")))), Styled("label", page?.Child("TitleText"), Text(page?.Child("TitleText")))]);
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
            var video = El("video", ("rect", $"{X(box) + ox},{I(box, "Top") + oy},{ow},{oh}"),
                ("frame_over", Picture("Scene", box)), ("over_rect", Rect(box)));
            video.Kids.Add(Styled("name", page.Child("ButtonText")?.Child($"ButtonText{i}"), $"{X(box)},{I(box, "Top") + I(box, "Height") + (int)(20 * k)},{I(box, "Width")},50")); // 20 under its frame, centred
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
        // thumbnails, or the lines of a list (a template whose scene page is only words)
        if (template.Child("scenes")?.Child("tscenes")?.Child("scene") is not { } scene || (scene.Child("videoes")?.Children("video").Any() != true && scene.Child("list")?.Children("item").Any() != true)) return "pas de grille de scènes";
        return null;
    }

    /// <summary>A PNG or JPEG file; some files of a portable copy hold memory garbage instead of a picture.</summary>
    static bool IsImage(string path)
    {
        if (!File.Exists(path)) return false;
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return true; // a drawing: read when it is drawn
        Span<byte> head = stackalloc byte[4];
        using var f = File.OpenRead(path);
        return f.Read(head) == 4 && (head is [0x89, 0x50, 0x4E, 0x47] or [0xFF, 0xD8, _, _]);
    }

    /// <summary>A picture of the template (a button traced into vectors has its .svg beside it: see <see cref="MenuRenderer.Traced"/>).</summary>
    string? Asset(string? relative) => TemplatePack.Inside(dir, relative) is { } full && IsImage(full) ? full : null;

    /// <summary>
    /// The highlighted twin of a button picture: <c>play/0.normal.png</c> → <c>play/0.sub.png</c>. Null when the template has none
    /// (the UHD ones) or it is unreadable: the button then gets BurnSmith's accent frame.
    /// </summary>
    string? Selected(string? normal)
    {
        if (normal == null) return null;
        var twin = normal.Replace(".normal.", ".sub.");
        if (IsImage(twin)) return twin;
        // beside a traced button, a chosen state that could not be traced is still its picture
        if (System.IO.Path.ChangeExtension(twin, ".png") is var png && png != twin && IsImage(png)) return png;
        // a French remake without a chosen state of its own: the template's (a glow, the same whatever the word)
        var own = normal.Replace(FrenchDir + System.IO.Path.DirectorySeparatorChar, "");
        return own != normal ? Selected(own) : null;
    }

    /// <summary>
    /// The DVD subpicture of a button: the template's highlighted artwork (<c>frame/0.dvd.sub.png</c> beside <c>frame/0.dvd.normal.png</c>) is
    /// exactly the DVD's <c>highlight</c> image, and its <c>.mask.</c> twin gives the shape. Only artwork that has such a twin qualifies — a star
    /// marker beside a text button is not a button picture, and there BurnSmith keeps its own four-colour frame.
    /// </summary>
    MenuButton Dvd(MenuButton b)
    {
        if (b.FocusImage is not { } art || !art.Contains(".sub.", StringComparison.Ordinal)) return b;
        var mask = System.IO.Path.ChangeExtension(art.Replace(".sub.", ".mask."), ".png"); // the shape stays a picture beside a traced button
        // the chosen state has its own picture in the template; Asset gives null when it is missing or not a picture
        return b with { DvdHighlightImage = art, DvdHighlightMask = IsImage(mask) && !IsBlank(mask) ? mask : null, DvdSelectImage = Asset(common.Child("selection")?.Text) };
    }

    /// <summary>The template draws a button picture at its own size, centred on the rectangle of the XML (which is often smaller).</summary>
    static Box Place(string picture, Box rect) =>
        PngSize(picture) is var (w, h) ? new Box(rect.X + (rect.W - w) / 2, rect.Y + (rect.H - h) / 2, w, h) : rect;

}
