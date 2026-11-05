namespace BurnSmith.Core.Menu;

/// <summary>
/// Finds the DVDFab and Leawo menu templates: the ones installed on this PC are read where they are, and the exe carries a copy of their
/// pictures and layout files (made by <see cref="Pack"/> when it was published) for PCs that have neither program. An installed template wins
/// over the carried one of the same name. The repository holds none of these files.
/// </summary>
public static class TemplateCatalog
{
    public sealed record Found(MenuDesign Design, string? Thumbnail);
    public sealed record Skipped(string Name, string Why, string Dir);

    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Extra folders the user pointed at, one per line.</summary>
    public static string FoldersFile => Path.Combine(AppData, "BurnSmith", "template-folders.txt");

    public static void AddFolder(string folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FoldersFile)!);
        File.AppendAllLines(FoldersFile, [folder]);
    }

    /// <summary>Where DVDFab keeps templates; portable copies are looked for in Downloads and next to the program folders.</summary>
    static IEnumerable<string> Roots()
    {
        // test aid: pretend this folder (an unpacked templates.zip) is all the PC has, to check what a machine without DVDFab / Leawo gets
        if (Environment.GetEnvironmentVariable("BURNSMITH_CARRIED_ONLY") is { Length: > 0 } only) { yield return only; yield break; }
        foreach (var root in InstalledRoots()) yield return root;
        if (Tools.TemplatesDir() is { } carried) yield return carried; // last: a template found above keeps its name
    }

    static IEnumerable<string> InstalledRoots()
    {
        yield return Path.Combine(AppData, "DVDFab", "DVDFabCommon", "Creator Menu", "MenuTemplate"); // templates downloaded from inside DVDFab
        foreach (var parent in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            foreach (var dir in Children(Environment.GetFolderPath(parent), "DVDFab")) yield return dir;
        foreach (var dir in Children(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "DVDFab")) yield return dir;
        foreach (var parent in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }) // Leawo: <program>\MenuTemplate
            foreach (var vendor in Children(Environment.GetFolderPath(parent), "Leawo"))
                foreach (var app in Directory.EnumerateDirectories(vendor)) yield return Path.Combine(app, "MenuTemplate");
        yield return Path.Combine(AppData, "Leawo");
        foreach (var parent in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }) // Yuhan: <program>\PresetMenu\THEMExx
            foreach (var dir in Children(Environment.GetFolderPath(parent), "Yuhan")) yield return Path.Combine(dir, "PresetMenu");
        if (File.Exists(FoldersFile))
            foreach (var line in File.ReadAllLines(FoldersFile).Select(l => l.Trim()).Where(l => l.Length > 0)) yield return line;
    }

    static IEnumerable<string> Children(string parent, string contains) =>
        Directory.Exists(parent) ? Directory.EnumerateDirectories(parent).Where(d => Path.GetFileName(d).Contains(contains, StringComparison.OrdinalIgnoreCase)) : [];

    /// <summary>Folders holding a <c>template.bd.xml</c>, searched a few levels down (a template's own sub-folders are not entered).</summary>
    static IEnumerable<string> TemplateFolders(string root, int depth = 6)
    {
        if (depth < 0 || !Directory.Exists(root)) yield break;
        var isTemplate = new[] { "template.bd.xml", "template.xml", "template2.xml" }.Any(f => File.Exists(Path.Combine(root, f)))
            || (Directory.Exists(Path.Combine(root, "Menu")) && Directory.Exists(Path.Combine(root, "Scene"))) // Leawo, with or without a layout file
            || YuhanSkin.IsTheme(root);
        if (isTemplate) { yield return root; yield break; }
        string[] kids;
        try { kids = Directory.GetDirectories(root); } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { yield break; }
        foreach (var kid in kids)
            foreach (var found in TemplateFolders(kid, depth - 1)) yield return found;
    }

    /// <summary>The design a new project starts with: DVDFab's own default ("Birthday1") when installed, else a plain gradient.</summary>
    public static MenuDesign Default() =>
        Scan().Usable.Select(f => f.Design).FirstOrDefault(d => d.Name.Equals("Birthday1", StringComparison.OrdinalIgnoreCase)) ?? new MenuDesign();

    /// <summary>A template named on the command line: a catalogue name or a template folder.</summary>
    public static MenuDesign? Find(string? nameOrFolder) =>
        string.IsNullOrEmpty(nameOrFolder) ? null
        : Directory.Exists(nameOrFolder) && InstalledTemplate.Load(Path.GetFullPath(nameOrFolder)) is { Unsupported: null } pack
            ? Design(Path.GetFullPath(nameOrFolder), pack)
            : Scan().Usable.Select(f => f.Design).FirstOrDefault(d => d.Name.Equals(nameOrFolder, StringComparison.OrdinalIgnoreCase));

    /// <summary>Leawo ships the same names for several screen shapes: the shape folder (16-9, 16-9-2) is part of the name.</summary>
    static MenuDesign Design(string dir, InstalledTemplate pack)
    {
        var name = Path.GetFileName(dir);
        if (pack.Category == "Leawo") name += $" ({Path.GetFileName(Path.GetDirectoryName(dir))})";
        if (pack.Category.StartsWith("DVDStyler") || pack.Category == "Yuhan") name = pack.Name;
        var design = new MenuDesign { Name = name, Category = pack.Category, TemplateDir = dir };
        if (pack.Accent != null) design.Accent = pack.Accent;
        if (pack.Category.StartsWith("DVDStyler")) { design.TextColor = pack.UiColor ?? design.TextColor; design.Font = pack.UiFont ?? design.Font; }
        return design;
    }

    // ---- templates BurnSmith ships (open source) ----

    static string ThumbDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BurnSmith", "thumbs");
    const int ThumbVersion = 1; // bump when the menu engine draws differently, so cached pictures are redone

    static string ThumbFile(MenuDesign d) =>
        Path.Combine(ThumbDir, string.Concat($"{d.Category}_{d.Name}_v{ThumbVersion}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".jpg");

    /// <summary>Designs BurnSmith draws itself (DVD Forge's parameters): they have no picture file, one is drawn on first use.</summary>
    static IEnumerable<Found> Shipped() =>
        DvdForgeTemplates.All().Select(d => new Found(d, File.Exists(ThumbFile(d)) ? ThumbFile(d) : null));

    /// <summary>Draws the gallery picture (the scene page with sample thumbnails) of every design that has none yet. True when it made some.</summary>
    public static bool RenderThumbnails(IEnumerable<Found> found)
    {
        var todo = found.Where(f => f.Thumbnail == null).Select(f => f.Design).ToList();
        if (todo.Count == 0) return false;
        Directory.CreateDirectory(ThumbDir);
        var content = new MenuContent(1, null, Enumerable.Repeat<string?>(null, 12).ToList(), null, ["Français 5.1", "English 5.1"], ["Français"]);
        MenuRenderer.Sta(() =>
        {
            foreach (var d in todo)
                try
                {
                    var pages = MenuBuilder.Build(d, content);
                    File.WriteAllBytes(ThumbFile(d), MenuRenderer.Background(d, pages[Math.Min(1, pages.Count - 1)]));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException) { /* no picture: the gallery shows the name */ }
            return 0;
        });
        return true;
    }

    public static (List<Found> Usable, List<Skipped> Skipped) Scan()
    {
        var usable = new List<Found>(Shipped());
        var skipped = new List<Skipped>();
        var dvdt = OpenLibrary.Dir() is { } lib && Directory.Exists(Path.Combine(lib, "dvdstyler", "templates"))
            ? Directory.EnumerateFiles(Path.Combine(lib, "dvdstyler", "templates"), "*.dvdt", SearchOption.AllDirectories) : [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Roots().SelectMany(r => TemplateFolders(r)).Concat(dvdt).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pack = InstalledTemplate.Load(dir);
            if (pack == null) continue;
            var design = Design(dir, pack);
            if (!seen.Add($"{design.Category}|{design.Name}")) continue; // installed and carried copies of one template: the first is kept
            if (pack.Unsupported is { } why) skipped.Add(new Skipped(design.Name, why, dir));
            else usable.Add(new Found(design, pack.Thumbnail));
        }
        return (usable.OrderBy(f => f.Design.Category).ThenBy(f => f.Design.Name, StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }

    /// <summary>
    /// Zips the DVDFab and Leawo template folders found on this PC into <paramref name="zip"/>: pictures and layout files only (no fonts, no programs),
    /// as "DVDFab Blu-ray|DVDFab DVD|Leawo/[shape/]name/...". publish.ps1 embeds it in the exe. Returns the number of templates written; none is an error, so that a release never ships without them unnoticed.
    /// <paramref name="roots"/> replaces this PC's installs (for a check).
    /// </summary>
    public static int Pack(string zip, Action<string>? log = null, IEnumerable<string>? roots = null)
    {
        string[] keep = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".xml"];
        if (File.Exists(zip)) File.Delete(zip);
        using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in (roots ?? InstalledRoots()).SelectMany(r => TemplateFolders(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (InstalledTemplate.Load(dir) is not { Unsupported: null } pack) continue;
            var name = Path.GetFileName(dir);
            var prefix = pack.Category == "Leawo" ? $"Leawo/{Path.GetFileName(Path.GetDirectoryName(dir))}/{name}" : $"{pack.Category}/{name}";
            if (!done.Add(prefix)) continue;
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => keep.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();
            if (pack.Category == "Yuhan") // the 1920x1080 pictures, the gallery thumbnail and the layout in clear text (config.dat is encrypted with the program's key)
            {
                files = files.Where(f => Path.GetRelativePath(dir, f).StartsWith("1920x1080") || Path.GetFileName(f) == "thumb_169.jpg").ToList();
                using var w = new StreamWriter(archive.CreateEntry($"{prefix}/config.xml").Open());
                w.Write(YuhanSkin.ReadConfig(dir));
            }
            foreach (var f in files)
                System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, f, $"{prefix}/{Path.GetRelativePath(dir, f).Replace('\\', '/')}", System.IO.Compression.CompressionLevel.Optimal);
            log?.Invoke($"{prefix}: {files.Count} fichiers");
        }
        if (done.Count == 0) throw new InvalidOperationException("no DVDFab, Leawo or Yuhan template found on this PC: a release must carry them");
        using (var w = new StreamWriter(archive.CreateEntry("NOTICE.txt").Open()))
            w.Write("Images and layout files of DVDFab, Leawo and Yuhan menu templates, kept as published by their owners.\nThey are not part of BurnSmith's GPL-licensed code and belong to their owners.\nNo program, font or interface of these products is included.\n");
        return done.Count;
    }
}
