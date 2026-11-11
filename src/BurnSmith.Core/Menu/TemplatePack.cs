using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BurnSmith.Core.Menu;

/// <summary>
/// One node of the normalized template tree, kept as plain data so a pack is readable, comparable and editable as JSON.
/// It is the same information a vendor <c>template.bd.xml</c> / <c>config.xml</c> / <c>.dvdt</c> carries, once every reader has
/// turned it into BurnSmith's own tree.
/// </summary>
public sealed class PackNode
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Attrs { get; set; } = [];
    public string Text { get; set; } = "";
    public List<PackNode> Kids { get; set; } = [];
}

/// <summary>One picture of a pack, named by the role it plays so buttons and backgrounds stay apart.</summary>
public sealed class PackAsset
{
    /// <summary><c>background</c>, <c>button</c>, <c>image</c> or <c>thumbnail</c>.</summary>
    public string Role { get; set; } = "image";
    /// <summary>The picture, relative to the pack folder (always a PNG).</summary>
    public string Path { get; set; } = "";
    /// <summary>The selected-state picture of a button, relative to the pack folder, when the template has one.</summary>
    public string? Focus { get; set; }
    /// <summary>The page kind the picture belongs to, when it can be told from its path.</summary>
    public string? Page { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// The single format every menu template is converted to: a folder holding <c>template.json</c> (metadata + the normalized
/// layout tree) and its PNG pictures. The layout tree is BurnSmith's own, so a picture-only theme, a Blu-ray template,
/// a DVD template and a theme whose layout is read from its settings all become the same thing. Pictures are split by role: backgrounds, buttons,
/// other images and one thumbnail.
/// <para>
/// Nothing is baked into one picture: a background stays a background, a button keeps its normal and selected pictures, and
/// every text, rectangle, colour and font is a value in the JSON. The folder lives in
/// <c>Documents\BurnSmith\Templates\converted</c>, so the user can open, edit, copy or back it up like any other document.
/// </para>
/// </summary>
public sealed class TemplatePack
{
    public const string SchemaId = "burnsmith/template/1";
    public const string FileName = "template.json";

    public string Schema { get; set; } = SchemaId;
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Kind { get; set; } = nameof(TemplateKind.None);
    public string Source { get; set; } = "";
    public string? Unsupported { get; set; }
    public int Width { get; set; } = MenuBuilder.W;
    public int Height { get; set; } = MenuBuilder.H;
    public MenuDesign Design { get; set; } = new();
    public List<PackAsset> Assets { get; set; } = [];
    public PackNode? Tree { get; set; }
    public string ConvertedAt { get; set; } = DateTime.UtcNow.ToString("O");

    /// <summary>Where this pack was read from (not stored: it depends on the PC).</summary>
    [JsonIgnore] public string Dir { get; internal set; } = "";

    [JsonIgnore] public bool HasLayout => Tree is { } t && t.Name == "template";
    [JsonIgnore] public TemplateKind KindValue => Enum.TryParse<TemplateKind>(Kind, true, out var k) ? k : TemplateKind.None;
    [JsonIgnore] public int Backgrounds => Assets.Count(a => a.Role == "background");
    [JsonIgnore] public int Buttons => Assets.Count(a => a.Role == "button");

    /// <summary>Where converted templates live: a visible folder of the user's library.</summary>
    public static string Root => Path.Combine(AppFiles.Library(), "converted");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reading only: "design": null in a downloaded pack is a JsonException, caught by TryRead, not a crash further on (a save is not refused for it).</summary>
    static readonly JsonSerializerOptions Strict = new(Json) { RespectNullableAnnotations = true };

    /// <summary>Reads one pack; null when the file is absent, unreadable or of another schema.</summary>
    public static TemplatePack? TryRead(string dir)
    {
        try
        {
            var file = Path.Combine(dir, FileName);
            if (!File.Exists(file)) return null;
            var pack = JsonSerializer.Deserialize<TemplatePack>(File.ReadAllText(file), Strict);
            if (pack == null || pack.Schema != SchemaId) return null;
            // "elements": [null] passes the nullable check (it covers properties, not list items) and would stop the scan of every template
            if (pack.Assets.Contains(null!) || pack.Design.Elements.Contains(null!)) return null;
            pack.Dir = dir;
            var d = pack.Design;
            (d.Poster, d.BackgroundImage, d.LoopFile, d.Music) = (Library(d.Poster), Library(d.BackgroundImage), null, null);
            d.PageBackgrounds = d.PageBackgrounds.Where(p => Library(p.Value) != null).ToDictionary();
            foreach (var e in d.Elements) (e.Image, e.Focus) = (Library(e.Image), Library(e.Focus));
            return pack;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Every converted pack on this PC, in category then name order: the user's (Documents\BurnSmith\Templates\converted) first, so an
    /// edited copy wins, then the ones the exe carries already converted.
    /// </summary>
    public static List<TemplatePack> Scan()
    {
        var packs = new List<TemplatePack>();
        // test aid (as in TemplateCatalog): an unpacked templates.zip is all the PC has
        string?[] roots = Environment.GetEnvironmentVariable("BURNSMITH_CARRIED_ONLY") is { Length: > 0 } only ? [only] : [Root, Tools.TemplatesDir()];
        foreach (var root in roots)
        {
            if (root == null || !Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, FileName, SearchOption.AllDirectories))
                if (TryRead(Path.GetDirectoryName(file)!) is { } pack) packs.Add(pack);
        }
        return packs.OrderBy(p => p.Category).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Writes <see cref="FileName"/> into <paramref name="dir"/>.</summary>
    public void Save(string dir) => File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));

    /// <summary>The normalized tree as the renderer reads it, or null for a design-only pack (BurnSmith's own design).</summary>
    internal XNode? ToTree() => Tree is null ? null : Node(Tree);

    static XNode Node(PackNode n)
    {
        var node = new XNode(n.Name, new Dictionary<string, string>(n.Attrs, StringComparer.OrdinalIgnoreCase));
        node.Content.Append(n.Text);
        foreach (var kid in n.Kids) node.Kids.Add(Node(kid));
        return node;
    }

    internal static PackNode FromNode(XNode n) => new()
    {
        Name = n.Name,
        Attrs = new Dictionary<string, string>(n.Attrs, StringComparer.OrdinalIgnoreCase),
        Text = n.Content.ToString(),
        Kids = n.Kids.Select(FromNode).ToList(),
    };

    /// <summary>A picture the pack names, when it exists inside the pack's folder (a pack is downloaded data: never a path to follow elsewhere).</summary>
    public string? AssetFile(string? relative) => Inside(Dir, relative) is { } file && File.Exists(file) ? file : null;

    /// <summary>The pack's thumbnail, when it carries one.</summary>
    public string? ThumbnailFile(string dir) =>
        Assets.FirstOrDefault(a => a.Role == "thumbnail") is { } t && Inside(dir, t.Path) is { } file && File.Exists(file) ? file : null;

    // ---------------------------------------------------------------- conversion

    public sealed record ConvertResult(int Converted, int Skipped, int Failed, List<string> Messages);

    /// <summary>
    /// Converts every template found on this PC to a pack. <paramref name="force"/> replaces packs that already exist;
    /// otherwise an existing pack is kept (so a user's edits are never overwritten).
    /// </summary>
    public static ConvertResult ConvertAll(Action<string>? log = null, string? outputRoot = null, bool force = false)
    {
        outputRoot ??= Root;
        var (usable, skipped) = TemplateCatalog.Scan(includePacks: false);
        var sources = new List<(string Name, string Category, string Dir, MenuDesign? Design)>();
        sources.AddRange(usable.Select(f => (f.Design.Name, f.Design.Category, f.Design.TemplateDir ?? "", (MenuDesign?)f.Design)));
        // a template the renderer refused still has pictures worth converting (and its name explains why it was refused)
        foreach (var s in skipped) sources.Add((s.Name, TemplateLabel.Themes, s.Dir, null));

        var done = 0;
        var kept = 0;
        var failed = 0;
        var messages = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var dir = source.Dir;
            if (dir.Length == 0 && source.Design == null) continue;
            if (dir.Length > 0 && (IsUnder(dir, Root) || IsUnder(dir, outputRoot))) { kept++; continue; }
            var key = $"{source.Category}|{source.Name}";
            if (!seen.Add(key)) continue;

            var target = PackDir(outputRoot, source.Category, source.Name);
            try
            {
                if (Directory.Exists(target) && File.Exists(Path.Combine(target, FileName)) && !force) { kept++; log?.Invoke($"garde  {key}"); continue; }
                Directory.CreateDirectory(target);
                var pack = ConvertOne(source.Name, source.Category, dir, source.Design, target);
                pack.Save(target);
                done++;
                log?.Invoke($"convertis {key} -> {Path.GetRelativePath(outputRoot, target)} ({pack.Backgrounds} fonds, {pack.Buttons} boutons)");
            }
            // InvalidOperationException: a damaged picture, as MenuRenderer.Sta reports it; JsonException: a tree nested too deep to save
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or JsonException)
            {
                failed++;
                messages.Add($"{key}: {e.Message}");
                log?.Invoke($"ECHEC {key}: {e.Message}");
            }
        }
        return new ConvertResult(done, kept, failed, messages);
    }

    /// <summary>Converts one template folder (or one built-in design) into a pack, copying its pictures into <paramref name="targetDir"/>.</summary>
    public static TemplatePack ConvertOne(string name, string category, string dir, MenuDesign? design, string targetDir)
    {
        var pack = new TemplatePack
        {
            Name = name,
            Category = category.Length > 0 ? category : TemplateLabel.Themes,
            Source = dir,
            Design = (design ?? new MenuDesign()).Clone(),
        };
        pack.Design.Name = name;
        pack.Design.Category = pack.Category;
        pack.Design.TemplateDir = null;
        if (design != null) pack.Kind = design.Kind.ToString();

        var source = dir.Length > 0 && (Directory.Exists(dir) || File.Exists(dir)) ? InstalledTemplate.Load(dir) : null;
        if (source != null)
        {
            pack.Kind = source.Kind.ToString();
            pack.Unsupported = source.Unsupported;
            pack.Design.Kind = source.Kind;
            pack.Design.Accent = source.Accent ?? pack.Design.Accent;
            pack.Design.Font = source.UiFont ?? pack.Design.Font;
            pack.Design.TextColor = source.UiColor ?? pack.Design.TextColor;
            pack.Design.BackgroundImage = null;
            if (source.Tree is { } tree) pack.Tree = FromNode(tree);
        }
        else if (design == null) pack.Unsupported = "modèle illisible (aucun lecteur ne le reconnaît)";

        if (pack.Tree != null && source != null)
        {
            var mapping = CopyAssets(pack, source.SourceDir, pack.Tree, targetDir, source.Thumbnail);
            Rewrite(pack.Tree, mapping);
            // the template's moving background travels as it is: the menu's clip is encoded from it, so nothing is lost on the way
            if (InstalledTemplate.LoopClip(source.SourceDir) is { } clip) File.Copy(clip, Path.Combine(targetDir, InstalledTemplate.PackedLoop), true);
        }
        pack.Design.Name = name;
        pack.Design.Category = pack.Category;
        pack.Design.TemplateDir = null;
        pack.Design.LoopFile = null; // the template's own clip is found again beside the pack; this PC's path means nothing elsewhere
        return pack;
    }

    /// <summary>Copies every picture the normalized tree names (a JPEG as it is, any other to PNG), and fills <see cref="Assets"/>.</summary>
    static Dictionary<string, string> CopyAssets(TemplatePack pack, string sourceDir, PackNode tree, string targetDir, string? thumbnail)
    {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, (string Role, string? Page)>(StringComparer.OrdinalIgnoreCase);
        WalkAssets(tree, sourceDir, [], (file, role, page) =>
        {
            if (!found.TryGetValue(file, out var known) || Strength(role) > Strength(known.Role))
                found[file] = (role, known.Page ?? page);
        });

        // the gallery thumbnail is an extra copy: the template may also use the same picture as its background
        if (thumbnail != null && File.Exists(thumbnail))
        {
            // the gallery shows it 170 wide: a small JPEG, where a theme gives its whole background for it (4K for some)
            var small = Opaque(thumbnail) ? "thumbnail.jpg" : "thumbnail.png";
            CopyImage(thumbnail, Path.Combine(targetDir, small), ThumbnailWidth);
            var (tw, th) = Size(Path.Combine(targetDir, small));
            pack.Assets.Add(new PackAsset { Role = "thumbnail", Path = small, Width = tw, Height = th });
        }

        foreach (var (file, (role, page)) in found.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Relative(sourceDir, file);
            if (mapping.ContainsKey(relative)) continue;
            var target = Copy(file, Target(relative, role, mapping.Values, file));
            mapping[relative] = target;

            string? focusTarget = null;
            var focus = FocusRelative(relative, sourceDir);
            if (focus is { } f && File.Exists(Path.Combine(sourceDir, f.Replace('/', Path.DirectorySeparatorChar))))
            {
                focusTarget = Copy(Path.Combine(sourceDir, f.Replace('/', Path.DirectorySeparatorChar)), Target(f, "button", mapping.Values.Append(target)));
                mapping[f] = focusTarget;
            }
            // a button traced into a drawing (tools\vectorize-buttons.py) goes as that drawing alone: it is what the menu draws, at any size
            string Copy(string source, string name)
            {
                if (MenuRenderer.Traced(source) is { } svg)
                {
                    name = UniqueTarget(Path.ChangeExtension(name, ".svg").Replace('\\', '/'), mapping.Values);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(targetDir, name))!);
                    File.Copy(svg, Path.Combine(targetDir, name.Replace('/', Path.DirectorySeparatorChar)), true);
                }
                else CopyImage(source, Path.Combine(targetDir, name.Replace('/', Path.DirectorySeparatorChar)));
                return name;
            }
            // the French remake of a button whose word is part of its picture travels under the same name, in its own folder (InstalledTemplate.Button)
            foreach (var (from, to) in new[] { (relative, target), (focus, focusTarget) })
                if (from != null && to != null && Path.Combine(sourceDir, InstalledTemplate.FrenchDir, from.Replace('/', Path.DirectorySeparatorChar)) is var french && File.Exists(french))
                    Copy(french, InstalledTemplate.FrenchDir + "/" + to);
            // the shape a thumbnail or a DVD highlight is cut to: the template keeps it beside the "normal" picture and no value of the tree names it,
            // the "normal" / "sub" → "mask" name is how it is found again (a frame without a "sub" picture has one too)
            var mask = relative.Replace(".normal.", ".mask.", StringComparison.OrdinalIgnoreCase);
            var maskTarget = target.Replace(".normal.", ".mask.", StringComparison.OrdinalIgnoreCase);
            if (mask != relative && maskTarget != target && !mapping.ContainsKey(mask) && File.Exists(Path.Combine(sourceDir, mask.Replace('/', Path.DirectorySeparatorChar))))
            {
                mapping[mask] = maskTarget;
                CopyImage(Path.Combine(sourceDir, mask.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(targetDir, maskTarget.Replace('/', Path.DirectorySeparatorChar)));
            }
            pack.Assets.Add(new PackAsset
            {
                Role = role,
                Path = target,
                Focus = focusTarget,
                Page = page ?? PageOf(relative),
                Width = Size(file).W,
                Height = Size(file).H,
            });
        }
        return mapping;
    }

    static int Strength(string role) => role switch { "background" => 3, "button" => 2, _ => 1 };

    /// <summary>
    /// The pictures a tree names, with the role the tree gives them (a <c>bg/image</c> is a background, a
    /// <c>playbuttontimage</c> or a <c>play/0.normal.png</c> is a button, a <c>pic</c> or a frame is another image).
    /// </summary>
    static void WalkAssets(PackNode node, string sourceDir, List<string> path, Action<string, string, string?> visit)
    {
        path.Add(node.Name);
        var page = path.FirstOrDefault(p => p is "rootmenu" or "scenes" or "setup") switch
        {
            "rootmenu" => "root",
            "scenes" => "scenes",
            "setup" => "settings",
            _ => null,
        };
        var button = node.Name is "play" or "scenes" or "setup" or "bonus" or "left" or "right" or "home"
            || node.Attrs.TryGetValue("textbutton", out var text) && text == "1";
        foreach (var value in node.Attrs.Values)
            if (Resolve(sourceDir, value) is { } file)
                visit(file, RoleIn(path, node, value, button), page);
        if (Resolve(sourceDir, node.Text) is { } textFile)
            visit(textFile, RoleIn(path, node, node.Text, button), page);
        foreach (var kid in node.Kids) WalkAssets(kid, sourceDir, path, visit);
        path.RemoveAt(path.Count - 1);
    }

    static string RoleIn(List<string> path, PackNode node, string value, bool button)
    {
        if (path.Contains("bg")) return "background";
        if (button && (node.Name == "image" || path[^1] is "play" or "scenes" or "setup" or "bonus" or "left" or "right" or "home")) return "button";
        if (node.Name.EndsWith("buttontimage", StringComparison.OrdinalIgnoreCase)) return "button";
        return RoleOf(value);
    }

    static (int W, int H) Size(string file) => MenuRenderer.Size(file) ?? (0, 0);

    /// <summary>The "name.sub.png" twin of "name.normal.png", if the template ships one.</summary>
    static string? FocusRelative(string relative, string sourceDir)
    {
        if (!relative.Contains(".normal.", StringComparison.OrdinalIgnoreCase)) return null;
        var twin = relative.Replace(".normal.", ".sub.", StringComparison.OrdinalIgnoreCase);
        return File.Exists(Path.Combine(sourceDir, twin.Replace('/', Path.DirectorySeparatorChar))) ? twin : null;
    }

    /// <param name="file">The picture itself, when its content decides the format: a background without transparency becomes a JPEG.</param>
    static string Target(string relative, string role, IEnumerable<string> taken, string? file = null)
    {
        var ext = Path.GetExtension(relative);
        var stem = ext.Length > 0 ? relative[..^ext.Length] : relative;
        // a PNG under half a megabyte stays as it is: already light, and usually flat drawn art, whose sharp edges a JPEG smears for no gain
        var heavy = file != null && new FileInfo(file).Length >= 512 * 1024;
        var target = (role == "thumbnail" ? "thumbnail" : stem) + (IsJpeg(relative) || (role == "background" && heavy && Opaque(file!)) ? ".jpg" : ".png");
        return UniqueTarget(target, taken);
    }

    static string UniqueTarget(string target, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(target)) return target;
        var dir = Path.GetDirectoryName(target)?.Replace('\\', '/') ?? "";
        var stem = Path.GetFileNameWithoutExtension(target);
        for (var n = 2; ; n++)
        {
            var candidate = (dir.Length > 0 ? dir + "/" : "") + $"{stem}-{n}{Path.GetExtension(target)}";
            if (!set.Contains(candidate)) return candidate;
        }
    }

    /// <summary>A JPEG is kept as it is: it has no transparency to lose, and as PNG the theme pictures weigh three times more in the exe.</summary>
    static bool IsJpeg(string file) => Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg";

    /// <summary>
    /// The JPEG a background becomes. A menu background ends as a frame of the disc's video, compressed far harder than this: at 95 the picture
    /// cannot be told from its PNG (39 to 52 dB of PSNR on the 27 templates concerned, 46 in the middle), for a sixth of the size. Higher buys
    /// little: Windows' encoder halves the colour resolution whatever the level, as the disc's video does anyway, and that is what caps the
    /// figure on the most saturated pictures. WebP or AVIF would be smaller still, but Windows' own decoder, which draws every menu, reads
    /// neither without an add-on.
    /// </summary>
    const int JpegQuality = 95;
    const int ThumbnailWidth = 480;

    /// <summary>No transparency to keep: a JPEG, or a picture whose every pixel is solid.</summary>
    static bool Opaque(string file)
    {
        if (IsJpeg(file)) return true;
        try
        {
            MenuRenderer.CheckSize(file);
            return MenuRenderer.Sta(() =>
            {
                var frame = new System.Windows.Media.Imaging.FormatConvertedBitmap(System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(file),
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0],
                    System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
                frame.CopyPixels(px, frame.PixelWidth * 4, 0);
                for (var i = 3; i < px.Length; i += 4) if (px[i] < 250) return false;
                return true;
            });
        }
        catch (Exception e) when (e is NotSupportedException or System.IO.FileFormatException or IOException or InvalidDataException) { return false; }
    }

    /// <param name="maxWidth">A wider picture is made that wide (the gallery's thumbnail).</param>
    static void CopyImage(string source, string target, int maxWidth = int.MaxValue)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var fits = maxWidth == int.MaxValue || Size(source).W <= maxWidth;
        if (fits && (Path.GetExtension(source).Equals(Path.GetExtension(target), StringComparison.OrdinalIgnoreCase)
            || IsJpeg(source) && IsJpeg(target)))
        {
            File.Copy(source, target, true);
            return;
        }
        MenuRenderer.CheckSize(source);
        MenuRenderer.Sta(() =>
        {
            System.Windows.Media.Imaging.BitmapSource frame = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(source),
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            if (!fits) frame = new System.Windows.Media.Imaging.TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(maxWidth / (double)frame.PixelWidth, maxWidth / (double)frame.PixelWidth));
            System.Windows.Media.Imaging.BitmapEncoder encoder = IsJpeg(target)
                ? new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = JpegQuality } : new System.Windows.Media.Imaging.PngBitmapEncoder();
            // a JPEG has no transparency: the picture is flattened first (it was checked solid)
            if (IsJpeg(target)) frame = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr24, null, 0);
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
            using var stream = File.Create(target);
            encoder.Save(stream);
            return 0;
        });
    }

    static string RoleOf(string relative)
    {
        var p = relative.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("background") || p.StartsWith("bdbackground/") || p.StartsWith("background/") || p.Contains("/bg_")) return "background";
        if (p.StartsWith("play/") || p.StartsWith("scenes/") || p.StartsWith("setup/") || p.StartsWith("home/") || p.StartsWith("left/") || p.StartsWith("right/")
            || p.StartsWith("scene/back") || p.StartsWith("scene/previous") || p.StartsWith("scene/next")
            || p.StartsWith("1920x1080/back") || p.StartsWith("1920x1080/previous") || p.StartsWith("1920x1080/next")) return "button";
        return "image";
    }

    static string? PageOf(string relative)
    {
        var p = relative.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("background01") || p.Contains("bg_menu") || p.StartsWith("bdbackground/")) return "root";
        if (p.Contains("background02") || p.Contains("thumb_frame_scene") || p.StartsWith("scene/background")) return "scenes";
        return null;
    }

    /// <summary>Rewrites every asset reference in the tree to its new, converted path.</summary>
    static void Rewrite(PackNode node, Dictionary<string, string> mapping)
    {
        foreach (var key in node.Attrs.Keys.ToList())
            if (mapping.TryGetValue(node.Attrs[key], out var value)) node.Attrs[key] = value;
        if (mapping.TryGetValue(node.Text.Trim(), out var text)) node.Text = text;
        foreach (var kid in node.Kids) Rewrite(kid, mapping);
    }

    static void Walk(PackNode node, Action<string> visit)
    {
        foreach (var value in node.Attrs.Values) visit(value);
        visit(node.Text.Trim());
        foreach (var kid in node.Kids) Walk(kid, visit);
    }

    /// <summary>The existing picture a tree value names, or null (rectangles, words and colours are not pictures).</summary>
    static string? Resolve(string dir, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.Length == 0 || v.Contains(',') || v.Contains('#')) return null;
        if (Inside(dir, v) is not { } path || !File.Exists(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" ? path : null;
    }

    static string Relative(string sourceDir, string file)
    {
        var root = Path.GetFullPath(sourceDir);
        var full = Path.GetFullPath(file);
        return IsUnder(full, root) ? Path.GetRelativePath(root, full).Replace('\\', '/') : Path.GetFileName(full);
    }

    /// <summary>
    /// The file a template names, only when it is a relative path that stays inside <paramref name="dir"/>, else null. A downloaded template must not make the app
    /// open a network share (Windows would send it the user's login hash) or read a private picture into a pack: no C:\, no \\server, no ..\..
    /// </summary>
    internal static string? Inside(string dir, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0')) return null;
        var v = relative.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(v)) return null; // C:\x, C:x, \x and \\server\share
        var full = Path.GetFullPath(Path.Combine(dir, v));
        return IsUnder(full, dir) ? full : null;
    }

    /// <summary>A picture reference of a pack's design, kept only when it names the library: a pack is shared looks, never a path to follow.</summary>
    static string? Library(string? reference) =>
        reference != null && (reference.StartsWith("lib:", StringComparison.Ordinal) || reference.StartsWith("carried:", StringComparison.Ordinal)) ? reference : null;

    static bool IsUnder(string path, string root)
    {
        if (root.Length == 0) return false;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    internal static string PackDir(string root, string category, string name)
    {
        static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        var cat = Safe(category.Length == 0 ? "Autres" : category);
        var stem = Safe(name);
        if (stem.Length == 0) stem = "Modele";
        return Path.Combine(root, cat, stem);
    }

    /// <summary>What is wrong with a converted pack: unreadable JSON, missing pictures, no background or no pressable button.</summary>
    public List<string> Problems()
    {
        var issues = new List<string>();
        if (Tree != null && !HasLayout) issues.Add("arbre illisible");
        foreach (var asset in Assets)
            if (AssetFile(asset.Path) == null) issues.Add($"image absente : {asset.Path}");
        if (Tree != null)
        {
            if (Assets.All(a => a.Role != "background")) issues.Add("aucun fond");
            if (!HasButton(Tree)) issues.Add("aucun bouton");
        }
        return issues;
    }

    /// <summary>A button can be a picture or a text button: the tree must have at least one of them.</summary>
    static bool HasButton(PackNode node) =>
        node.Name is "play" or "scenes" or "setup" or "bonus" or "left" or "right" or "home"
        || node.Attrs.TryGetValue("textbutton", out var text) && text == "1"
        || node.Kids.Any(HasButton);

    /// <summary>Checks every pack on this PC (used by the command line and the editor).</summary>
    public static (int Packs, int Ok, List<string> Problems) Verify()
    {
        var packs = Scan();
        var problems = new List<string>();
        var ok = 0;
        foreach (var pack in packs)
        {
            var issues = pack.Problems();
            if (issues.Count == 0) ok++;
            else problems.Add($"{pack.Category} | {pack.Name}: {string.Join(" ; ", issues)}");
        }
        return (packs.Count, ok, problems);
    }
}
