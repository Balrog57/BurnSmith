namespace BurnSmith.Core.Menu;

/// <summary>
/// Finds the menu templates installed on this PC (DVDFab, also the ones DVDFab downloaded). They stay where they are:
/// BurnSmith reads them at run time and never copies or ships them.
/// </summary>
public static class TemplateCatalog
{
    public sealed record Found(MenuDesign Design, string? Thumbnail);
    public sealed record Skipped(string Name, string Why);

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
        yield return Path.Combine(AppData, "DVDFab", "DVDFabCommon", "Creator Menu", "MenuTemplate"); // templates downloaded from inside DVDFab
        foreach (var parent in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            foreach (var dir in Children(Environment.GetFolderPath(parent), "DVDFab")) yield return dir;
        foreach (var dir in Children(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "DVDFab")) yield return dir;
        foreach (var parent in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }) // Leawo: <program>\MenuTemplate
            foreach (var vendor in Children(Environment.GetFolderPath(parent), "Leawo"))
                foreach (var app in Directory.EnumerateDirectories(vendor)) yield return Path.Combine(app, "MenuTemplate");
        yield return Path.Combine(AppData, "Leawo");
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
            || (Directory.Exists(Path.Combine(root, "Menu")) && Directory.Exists(Path.Combine(root, "Scene"))); // Leawo, with or without a layout file
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
        var design = new MenuDesign { Name = name, Category = pack.Category, TemplateDir = dir };
        if (pack.Accent != null) design.Accent = pack.Accent;
        return design;
    }

    public static (List<Found> Usable, List<Skipped> Skipped) Scan()
    {
        var usable = new List<Found>();
        var skipped = new List<Skipped>();
        foreach (var dir in Roots().SelectMany(r => TemplateFolders(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pack = InstalledTemplate.Load(dir);
            if (pack == null) continue;
            if (pack.Unsupported is { } why) skipped.Add(new Skipped(Design(dir, pack).Name, why));
            else usable.Add(new Found(Design(dir, pack), pack.Thumbnail));
        }
        return (usable.OrderBy(f => f.Design.Category).ThenBy(f => f.Design.Name, StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }
}
