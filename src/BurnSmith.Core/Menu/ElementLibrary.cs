namespace BurnSmith.Core.Menu;

/// <summary>One picture of the library: where it comes from, and the file to draw.</summary>
public sealed record Element(string Name, string Source, string Path, bool Removable = false)
{
    public override string ToString() => Name; // what a screen reader says for a list item
}

/// <summary>
/// The pictures a menu can be composed from. Three sources, each kept where it is:
/// the open-source ones shipped in the exe, the backgrounds of the templates installed on this PC
/// (listed by path, read at run time, never copied), and the user's own imports (copied to %APPDATA%\BurnSmith\library\backgrounds).
/// </summary>
public static class ElementLibrary
{
    public const string Open = TemplateLabel.Open, Mine = "Mes images";
    static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".svg"];

    /// <summary>The user's own pictures: Documents\BurnSmith\Templates\images (earlier versions: %APPDATA%\BurnSmith\library\backgrounds, copied over once).</summary>
    public static string UserDir { get; } = AppFiles.UserFolder("images", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BurnSmith", "library", "backgrounds"));

    static bool IsPicture(string file) => Extensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    /// <summary>The button looks the generic layout can draw (value = <see cref="MenuDesign.ButtonStyle"/>).</summary>
    public static readonly (string Label, string Value)[] ButtonStyles =
        [("Capsules", "Pill"), ("Texte seul", "Text"), ("Cartes", "Card"), ("Cadre classique", "Frame"), ("Verre néon", "Glass"), ("Métal (logo)", "Metal")];

    /// <summary>The pictures that travel in the exe, by the prefix a design stores them with: the open-source library and the carried template pictures.</summary>
    static IEnumerable<(string Prefix, string? Root)> Carried() => [("lib:", OpenLibrary.Dir()), ("carried:", Tools.TemplatesDir())];

    /// <summary>
    /// What a design stores for a library picture: a picture that travels in the exe is kept as "lib:&lt;path&gt;" (open library) or "carried:&lt;path&gt;"
    /// (template pictures), because the unpacked folder is not the same on every PC; any other picture (yours, or a template installed on this PC)
    /// is kept by its full path.
    /// </summary>
    public static string Reference(string file)
    {
        foreach (var (prefix, root) in Carried())
            if (root != null && file.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return prefix + file[(root.Length + 1)..].Replace('\\', '/');
        return file;
    }

    /// <summary>The file a stored picture reference points to (null when there is none).</summary>
    public static string? Resolve(string? reference)
    {
        if (reference == null) return null;
        // the roots are only looked up for a reference that names one: a plain path must not make the exe unpack its pictures
        string prefix, dir;
        if (reference.StartsWith("lib:", StringComparison.Ordinal)) { prefix = "lib:"; dir = OpenLibrary.Dir()!; }
        else if (reference.StartsWith("carried:", StringComparison.Ordinal)) { prefix = "carried:"; dir = Tools.TemplatesDir()!; }
        else return reference;
        return dir == null ? null : TemplatePack.Inside(dir, reference[prefix.Length..]); // "lib:..\..\x" stays in the library
    }

    /// <summary>Every background, grouped by source (the order of the sources is the order of the list).</summary>
    public static List<Element> Backgrounds((List<TemplateCatalog.Found> Usable, List<TemplateCatalog.Skipped> Skipped)? scan = null)
    {
        var (usable, skipped) = scan ?? TemplateCatalog.Scan();
        var list = new List<Element>();
        if (Directory.Exists(UserDir))
            list.AddRange(Directory.EnumerateFiles(UserDir).Where(IsPicture).Order().Select(f => new Element(System.IO.Path.GetFileNameWithoutExtension(f), Mine, f, true)));

        if (OpenLibrary.Dir() is { } lib && Directory.Exists(System.IO.Path.Combine(lib, "open", "backgrounds")))
            list.AddRange(Directory.EnumerateFiles(System.IO.Path.Combine(lib, "open", "backgrounds")).Where(IsPicture).Order()
                .Select(f => new Element(Pretty(System.IO.Path.GetFileNameWithoutExtension(f)), Open, f)));

        // the installed templates' own backgrounds, by path: a converted pack lists every picture it keeps as a background
        foreach (var found in usable.Where(f => f.Design.TemplateDir != null))
        {
            var dir = found.Design.TemplateDir!;
            if (TemplatePack.TryRead(dir) is { } pack)
            {
                var pictures = pack.Assets.Where(a => a.Role == "background").ToList();
                foreach (var asset in pictures)
                {
                    if (PackFile(dir, asset.Path) is not { } packPicture) continue;
                    var suffix = pictures.Count > 1 ? " · " + (PageLabel(asset.Page) is { Length: > 0 } p ? p : Pretty(System.IO.Path.GetFileNameWithoutExtension(asset.Path))) : "";
                    list.Add(new Element(found.Design.Name + suffix, found.Design.Category, packPicture));
                }
                continue;
            }
            if (InstalledTemplate.Load(dir)?.BackgroundFile is { } file)
                list.Add(new Element(found.Design.Name, found.Design.Category, file));
        }

        // a template folder BurnSmith cannot lay out still has its picture 
        foreach (var s in skipped)
            if (System.IO.Path.Combine(s.Dir, "Menu", "background01.jpg") is var picture && File.Exists(picture)) list.Add(new Element(s.Name, TemplateLabel.Themes, picture));

        return list.DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The file of a pack asset, or null when the picture was removed from the folder.</summary>
    static string? PackFile(string dir, string relative) => TemplatePack.Inside(dir, relative) is { } file && File.Exists(file) ? file : null;

    static string PageLabel(string? page) => page switch
    {
        "root" => "menu",
        "scenes" => "scènes",
        "settings" => "réglages",
        _ => "",
    };

    /// <summary>The role a pack picture plays, read from the folder names the templates use.</summary>
    static string ButtonLabel(string relative)
    {
        var p = relative.Replace('\\', '/').ToLowerInvariant();
        foreach (var (prefix, label) in new[]
                 {
                     ("play/", "Lecture"), ("scenes/", "Chapitres"), ("setup/", "Réglages"), ("home/", "Accueil"),
                     ("left/", "Précédent"), ("right/", "Suivant"),
                     ("scene/back", "Retour"), ("scene/previous", "Précédent"), ("scene/next", "Suivant"),
                     ("1920x1080/back", "Retour"), ("1920x1080/previous", "Précédent"), ("1920x1080/next", "Suivant"),
                 })
            if (p.StartsWith(prefix, StringComparison.Ordinal)) return label;
        return Pretty(System.IO.Path.GetFileNameWithoutExtension(p));
    }

    /// <summary>"05_easyblue" -> "easyblue", "frosty_forest" -> "frosty forest".</summary>
    static string Pretty(string stem)
    {
        var s = stem.Contains('_') && char.IsDigit(stem[0]) ? stem[(stem.IndexOf('_') + 1)..] : stem;
        return s.Replace('_', ' ');
    }

    // ---------------------------------------------------------------- buttons (kept apart from the backgrounds)

    /// <summary>
    /// One button of the library: a look BurnSmith draws itself (<see cref="Style"/>), or a picture (<see cref="Normal"/>) with the one shown
    /// when it is selected (<see cref="Focus"/>, null = the design's accent frame).
    /// </summary>
    public sealed record ButtonItem(string Name, string Source, string? Normal = null, string? Focus = null, string? Style = null, bool Removable = false);

    public const string Styles = "Styles BurnSmith", MyButtons = "Mes boutons", DvdStyler = "DVDStyler (vectoriels)";

    /// <summary>The user's own buttons: Documents\BurnSmith\Templates\buttons, "name.png" and, for its selected state, "name.sub.png".</summary>
    public static string UserButtonsDir { get; } = AppFiles.UserFolder("buttons", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BurnSmith", "library", "buttons"));

    static bool IsSub(string file) => System.IO.Path.GetFileNameWithoutExtension(file).EndsWith(".sub", StringComparison.OrdinalIgnoreCase);

    /// <summary>The "name.sub.*" picture beside "name.*", or null.</summary>
    static string? SubOf(string file) =>
        Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(file)!, System.IO.Path.GetFileNameWithoutExtension(file) + ".sub.*").FirstOrDefault(IsPicture);

    /// <summary>The button pictures a template folder carries: "play\0.normal.png" with its "0.sub.png", or a single arrow picture (no selected state).</summary>
    static IEnumerable<(string Role, string Normal, string? Focus)> TemplateButtons(string dir)
    {
        foreach (var (folder, role) in new[] { ("play", "Lecture"), ("scenes", "Chapitres"), ("home", "Accueil"), ("setup", "Réglages"), ("left", "Précédent"), ("right", "Suivant") })
        {
            var normal = System.IO.Path.Combine(dir, folder, "0.normal.png");
            var sub = System.IO.Path.Combine(dir, folder, "0.sub.png");
            if (File.Exists(normal)) yield return (role, normal, File.Exists(sub) ? sub : null);
        }
        foreach (var folder in new[] { "Scene", "1920x1080" })
            foreach (var (name, role) in new[] { ("back", "Retour"), ("previous", "Précédent"), ("next", "Suivant") })
                if (System.IO.Path.Combine(dir, folder, name + ".png") is var file && File.Exists(file)) yield return (role, file, null);
    }

    /// <summary>
    /// Every button, grouped by source: BurnSmith's own looks, the user's imports, then the button pictures of the templates
    /// (a picture several templates share is listed once). Unreadable pictures are left out.
    /// </summary>
    /// <summary>The looks BurnSmith draws itself: the head of <see cref="Buttons"/>, known without reading a file.</summary>
    public static List<ButtonItem> StyleButtons() => ButtonStyles.Select(s => new ButtonItem(s.Label, Styles, Style: s.Value)).ToList();

    public static List<ButtonItem> Buttons((List<TemplateCatalog.Found> Usable, List<TemplateCatalog.Skipped> Skipped)? scan = null)
    {
        var list = StyleButtons();
        if (Directory.Exists(UserButtonsDir))
            list.AddRange(Directory.EnumerateFiles(UserButtonsDir).Where(f => IsPicture(f) && !IsSub(f)).Order()
                .Select(f => new ButtonItem(System.IO.Path.GetFileNameWithoutExtension(f), MyButtons, f, SubOf(f), Removable: true)));

        // DVDStyler's buttons, as SVG: redrawn sharp at any size (tools/dvdstyler-buttons.py made them)
        if (OpenLibrary.Dir() is { } lib && System.IO.Path.Combine(lib, "open", "buttons") is var svgDir && Directory.Exists(svgDir))
            list.AddRange(Directory.EnumerateFiles(svgDir, "*.svg").Where(f => !IsSub(f)).Order()
                .Select(f => new ButtonItem(Pretty(System.IO.Path.GetFileNameWithoutExtension(f)).Replace('-', ' '), DvdStyler, f, SubOf(f))));

        var seen = new HashSet<string>();
        foreach (var found in (scan ?? TemplateCatalog.Scan()).Usable)
        {
            if (found.Design.TemplateDir is not { } dir || !Directory.Exists(dir)) continue;
            if (TemplatePack.TryRead(dir) is { } pack)
            {
                // a pack keeps the selected state beside its normal picture: no folder guesswork
                foreach (var asset in pack.Assets.Where(a => a.Role == "button"))
                    Add(found, ButtonLabel(asset.Path), PackFile(dir, asset.Path), asset.Focus is { } sub ? PackFile(dir, sub) : null);
                continue;
            }
            foreach (var (role, normal, focus) in TemplateButtons(dir)) Add(found, role, normal, focus);
        }
        return list;

        void Add(TemplateCatalog.Found found, string role, string? normal, string? focus, bool remake = false)
        {
            if (normal == null) return;
            // the same button in French, when the template has its remake: both are offered
            if (!remake && found.Design.TemplateDir is { } home && InstalledTemplate.French(home, System.IO.Path.GetRelativePath(home, normal)) is { } french)
                Add(found, role + " (français)", french, focus != null && InstalledTemplate.French(home, System.IO.Path.GetRelativePath(home, focus)) is { } lit ? lit : focus, true);
            if (MenuRenderer.Size(normal) == null || (focus != null && MenuRenderer.Size(focus) == null)) return; // a damaged picture
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(normal))) + (focus == null ? "" : "+" + new FileInfo(focus).Length);
            if (seen.Add(key)) list.Add(new ButtonItem($"{found.Design.Name} · {role}", found.Design.Category, normal, focus));
        }
    }

    /// <summary>Copies a button picture, and the one shown when it is selected, into the user's buttons.</summary>
    public static ButtonItem ImportButton(string normal, string? focus = null)
    {
        foreach (var file in new[] { normal, focus }.OfType<string>())
            if (!IsPicture(file) || !File.Exists(file)) throw new ArgumentException($"not a picture file (jpg, png, bmp, svg): {file}");
        var target = Free(UserButtonsDir, System.IO.Path.GetFileNameWithoutExtension(normal).Replace(".sub", "", StringComparison.OrdinalIgnoreCase), System.IO.Path.GetExtension(normal));
        File.Copy(normal, target);
        string? twin = null;
        if (focus != null)
        {
            twin = System.IO.Path.Combine(UserButtonsDir, System.IO.Path.GetFileNameWithoutExtension(target) + ".sub" + System.IO.Path.GetExtension(focus));
            File.Copy(focus, twin, true);
        }
        return new ButtonItem(System.IO.Path.GetFileNameWithoutExtension(target), MyButtons, target, twin, Removable: true);
    }

    /// <summary>Removes one of the user's own buttons (with its selected picture); the others are not the library's to delete.</summary>
    public static void RemoveButton(ButtonItem b)
    {
        if (!b.Removable || b.Normal == null || !b.Normal.StartsWith(UserButtonsDir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("only your own buttons can be removed");
        File.Delete(b.Normal);
        if (b.Focus != null && File.Exists(b.Focus)) File.Delete(b.Focus);
    }

    /// <summary>Copies a picture into the user's library (a name that exists is kept apart with a number).</summary>
    public static Element Import(string file)
    {
        if (!IsPicture(file) || !File.Exists(file)) throw new ArgumentException($"not a picture file (jpg, png, bmp, svg): {file}");
        var target = Free(UserDir, System.IO.Path.GetFileNameWithoutExtension(file), System.IO.Path.GetExtension(file));
        File.Copy(file, target);
        return new Element(System.IO.Path.GetFileNameWithoutExtension(target), Mine, target, true);
    }

    /// <summary>
    /// A file name nothing in the folder has yet, whatever the extension ("logo (2).png" when "logo.svg" is there): a picture with a drawing
    /// of the same name beside it is shown from that drawing (<see cref="MenuRenderer.Traced"/>), which must only happen to a template's traced buttons.
    /// </summary>
    static string Free(string dir, string stem, string extension)
    {
        Directory.CreateDirectory(dir);
        var name = stem;
        for (var n = 2; Directory.EnumerateFiles(dir, name + ".*").Any(); n++) name = $"{stem} ({n})";
        return System.IO.Path.Combine(dir, name + extension);
    }

    /// <summary>Removes one of the user's own pictures; the others are not the library's to delete.</summary>
    public static void Remove(Element e)
    {
        if (!e.Removable || !e.Path.StartsWith(UserDir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("only your own images can be removed");
        File.Delete(e.Path);
    }
}
