namespace BurnSmith.Core.Menu;

/// <summary>One picture of the library: where it comes from, and the file to draw.</summary>
public sealed record Element(string Name, string Source, string Path, bool Removable = false);

/// <summary>
/// The pictures a menu can be composed from. Three sources, each kept where it is:
/// the open-source ones shipped in the exe (DVDStyler), the backgrounds of the templates installed on this PC (DVDFab, Leawo:
/// listed by path, read at run time, never copied), and the user's own imports (copied to %APPDATA%\BurnSmith\library\backgrounds).
/// </summary>
public static class ElementLibrary
{
    public const string Shipped = "DVDStyler", Mine = "Mes images";
    static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];

    /// <summary>The user's own pictures: Documents\BurnSmith\Templates\images (earlier versions: %APPDATA%\BurnSmith\library\backgrounds, copied over once).</summary>
    public static string UserDir { get; } = AppFiles.UserFolder("images", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BurnSmith", "library", "backgrounds"));

    static bool IsPicture(string file) => Extensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    /// <summary>The button looks the generic layout can draw (value = <see cref="MenuDesign.ButtonStyle"/>).</summary>
    public static readonly (string Label, string Value)[] ButtonStyles =
        [("Capsules", "Pill"), ("Texte seul", "Text"), ("Cartes", "Card"), ("Cadre classique", "Frame"), ("Verre néon", "Glass")];

    /// <summary>
    /// What a design stores for a library picture: a picture that travels in the exe is kept as "lib:&lt;path in the library&gt;", because its unpacked
    /// folder changes with every build; any other picture (yours, or a template installed on this PC) is kept by its full path.
    /// </summary>
    public static string Reference(string file)
    {
        var lib = OpenLibrary.Dir();
        return lib != null && file.StartsWith(lib + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "lib:" + file[(lib.Length + 1)..].Replace('\\', '/') : file;
    }

    /// <summary>The file a stored picture reference points to (null when there is none).</summary>
    public static string? Resolve(string? reference) =>
        reference != null && reference.StartsWith("lib:", StringComparison.Ordinal)
            ? OpenLibrary.Dir() is { } lib ? System.IO.Path.Combine(lib, reference[4..].Replace('/', System.IO.Path.DirectorySeparatorChar)) : null
            : reference;

    /// <summary>Every background, grouped by source (the order of the sources is the order of the list).</summary>
    public static List<Element> Backgrounds((List<TemplateCatalog.Found> Usable, List<TemplateCatalog.Skipped> Skipped)? scan = null)
    {
        var (usable, skipped) = scan ?? TemplateCatalog.Scan();
        var list = new List<Element>();
        if (Directory.Exists(UserDir))
            list.AddRange(Directory.EnumerateFiles(UserDir).Where(IsPicture).Order().Select(f => new Element(System.IO.Path.GetFileNameWithoutExtension(f), Mine, f, true)));

        if (OpenLibrary.Dir() is { } lib && Directory.Exists(System.IO.Path.Combine(lib, "dvdstyler", "backgrounds")))
            list.AddRange(Directory.EnumerateFiles(System.IO.Path.Combine(lib, "dvdstyler", "backgrounds")).Where(IsPicture).Order()
                .Select(f => new Element(Pretty(System.IO.Path.GetFileNameWithoutExtension(f)), Shipped, f)));

        // the installed templates' own backgrounds, by path
        foreach (var found in usable.Where(f => f.Design.TemplateDir != null))
            if (InstalledTemplate.Load(found.Design.TemplateDir!)?.BackgroundFile is { } file && !found.Design.Category.StartsWith("DVDStyler"))
                list.Add(new Element(found.Design.Name, found.Design.Category, file));

        // a template folder BurnSmith cannot lay out still has its picture (Leawo: Menuackground01.jpg)
        foreach (var s in skipped)
            if (System.IO.Path.Combine(s.Dir, "Menu", "background01.jpg") is var picture && File.Exists(picture)) list.Add(new Element(s.Name, "Leawo", picture));

        return list.DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"05_easyblue" -> "easyblue", "frosty_forest" -> "frosty forest".</summary>
    static string Pretty(string stem)
    {
        var s = stem.Contains('_') && char.IsDigit(stem[0]) ? stem[(stem.IndexOf('_') + 1)..] : stem;
        return s.Replace('_', ' ');
    }

    /// <summary>Copies a picture into the user's library (a name that exists is kept apart with a number).</summary>
    public static Element Import(string file)
    {
        if (!IsPicture(file) || !File.Exists(file)) throw new ArgumentException("not a picture file (jpg, png, bmp)");
        Directory.CreateDirectory(UserDir);
        var stem = System.IO.Path.GetFileNameWithoutExtension(file);
        var target = System.IO.Path.Combine(UserDir, stem + System.IO.Path.GetExtension(file));
        for (var n = 2; File.Exists(target); n++) target = System.IO.Path.Combine(UserDir, $"{stem} ({n}){System.IO.Path.GetExtension(file)}");
        File.Copy(file, target);
        return new Element(System.IO.Path.GetFileNameWithoutExtension(target), Mine, target, true);
    }

    /// <summary>Removes one of the user's own pictures; the others are not the library's to delete.</summary>
    public static void Remove(Element e)
    {
        if (!e.Removable || !e.Path.StartsWith(UserDir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("only your own images can be removed");
        File.Delete(e.Path);
    }
}
