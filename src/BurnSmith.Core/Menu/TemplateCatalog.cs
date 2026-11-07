namespace BurnSmith.Core.Menu;

/// <summary>
/// Finds the menu templates available on this PC: the folders the user pointed at, and the copy the exe carries of them (made by
/// <see cref="Pack"/> when it was published), so a PC without any other program still gets them. A template found in a user folder wins
/// over the carried one of the same name. The repository holds none of these files.
/// </summary>
public static class TemplateCatalog
{
    public sealed record Found(MenuDesign Design, string? Thumbnail)
    {
        public override string ToString() => Design.Name; // what a screen reader says for a list item
    }
    public sealed record Skipped(string Name, string Why, string Dir);

    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Extra folders the user pointed at, one per line.</summary>
    public static string FoldersFile => Path.Combine(AppData, "BurnSmith", "template-folders.txt");

    public static void AddFolder(string folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FoldersFile)!);
        File.AppendAllLines(FoldersFile, [folder]);
    }

    /// <summary>The folders the user added, then the carried copy (last: a template found above keeps its name).</summary>
    static IEnumerable<string> Roots()
    {
        // test aid: pretend this folder (an unpacked templates.zip) is all the PC has, to check what a machine without any template gets
        if (Environment.GetEnvironmentVariable("BURNSMITH_CARRIED_ONLY") is { Length: > 0 } only) { yield return only; yield break; }
        foreach (var root in UserRoots()) yield return root;
        // the carried templates already unpacked in the visible library (the exe's payload is only there in a release build)
        if (Path.Combine(AppFiles.Library(), "carried") is var carried && Directory.Exists(carried)) yield return carried;
        if (Tools.TemplatesDir() is { } bundled) yield return bundled;
    }

    /// <summary>The folders named in <see cref="FoldersFile"/> (also what <see cref="Pack"/> reads on the publishing PC).</summary>
    static IEnumerable<string> UserRoots()
    {
        if (Environment.GetEnvironmentVariable("BURNSMITH_TEMPLATE_ROOTS") is { Length: > 0 } roots)
            foreach (var root in roots.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) yield return root;
        if (File.Exists(FoldersFile))
            foreach (var line in File.ReadAllLines(FoldersFile).Select(l => l.Trim()).Where(l => l.Length > 0)) yield return line;
    }

    /// <summary>Folders holding a <c>template.bd.xml</c>, searched a few levels down (a template's own sub-folders are not entered).</summary>
    static IEnumerable<string> TemplateFolders(string root, int depth = 6)
    {
        if (depth < 0 || !Directory.Exists(root)) yield break;
        var isTemplate = new[] { "template.bd.xml", "template.xml", "template2.xml" }.Any(f => File.Exists(Path.Combine(root, f)))
            || (Directory.Exists(Path.Combine(root, "Menu")) && Directory.Exists(Path.Combine(root, "Scene"))) // a picture theme, with or without a layout file
            || ThemeSkin.IsTheme(root);
        if (isTemplate) { yield return root; yield break; }
        string[] kids;
        try { kids = Directory.GetDirectories(root); } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { yield break; }
        foreach (var kid in kids)
            foreach (var found in TemplateFolders(kid, depth - 1)) yield return found;
    }

    /// <summary>The design a new project starts with: the first Blu-ray template named "Birthday1" when available, else a plain gradient.</summary>
    public static MenuDesign Default() =>
        Scan().Usable.Select(f => f.Design).FirstOrDefault(d => d.Name.Equals("Birthday1", StringComparison.OrdinalIgnoreCase)) ?? new MenuDesign();

    /// <summary>A template named on the command line: a catalogue name or a template folder.</summary>
    public static MenuDesign? Find(string? nameOrFolder) =>
        string.IsNullOrEmpty(nameOrFolder) ? null
        : Directory.Exists(nameOrFolder) && InstalledTemplate.Load(Path.GetFullPath(nameOrFolder)) is { Unsupported: null } pack
            ? Design(Path.GetFullPath(nameOrFolder), pack)
            : Scan().Usable.Select(f => f.Design).FirstOrDefault(d => d.Name.Equals(nameOrFolder, StringComparison.OrdinalIgnoreCase));

    /// <summary>Picture themes ship the same names for several screen shapes: the shape folder (16-9, 16-9-2) is part of the name.</summary>
    static MenuDesign Design(string dir, InstalledTemplate pack)
    {
        var name = Path.GetFileName(dir);
        if (pack.Kind == TemplateKind.Pictures) name += $" ({Path.GetFileName(Path.GetDirectoryName(dir))})";
        if (pack.Kind is TemplateKind.Library or TemplateKind.Theme) name = pack.Name;
        var design = new MenuDesign { Name = name, Category = pack.Category, Kind = pack.Kind, TemplateDir = dir };
        if (pack.Accent != null) design.Accent = pack.Accent;
        if (pack.Kind == TemplateKind.Library) { design.TextColor = pack.UiColor ?? design.TextColor; design.Font = pack.UiFont ?? design.Font; }
        return design;
    }

    // ---- designs BurnSmith ships (open source) ----

    static string ThumbDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BurnSmith", "thumbs");
    const int ThumbVersion = 1; // bump when the menu engine draws differently, so cached pictures are redone

    static string ThumbFile(MenuDesign d) =>
        Path.Combine(ThumbDir, string.Concat($"{d.Category}_{d.Name}_v{ThumbVersion}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".jpg");

    /// <summary>Designs BurnSmith draws itself from its own parameters: they have no picture file, one is drawn on first use.</summary>
    static IEnumerable<Found> Shipped() =>
        BuiltInTemplates.All().Select(d => new Found(d, File.Exists(ThumbFile(d)) ? ThumbFile(d) : null));

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

    /// <param name="includePacks">False while converting: the source templates are read, not the packs made from them.</param>
    public static (List<Found> Usable, List<Skipped> Skipped) Scan(bool includePacks = true)
    {
        var usable = new List<Found>();
        var skipped = new List<Skipped>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // converted packs first: one PC can then use the same format for every template, and a pack replaces the vendor copy of the same name
        if (includePacks)
            foreach (var pack in TemplatePack.Scan())
        {
            var design = pack.Design.Clone();
            design.Name = pack.Name;
            design.Category = pack.Category;
            design.Kind = pack.KindValue;
            design.TemplateDir = pack.HasLayout ? pack.Dir : null;
            if (!seen.Add($"{design.Category}|{design.Name}")) continue;
            if (pack.Unsupported is { } why) skipped.Add(new Skipped(design.Name, why, pack.Dir));
            else usable.Add(new Found(design, pack.ThumbnailFile(pack.Dir)));
        }

        usable.AddRange(Shipped().Where(f => seen.Add($"{f.Design.Category}|{f.Design.Name}")));
        var dvdt = OpenLibrary.Dir() is { } lib && Directory.Exists(Path.Combine(lib, "open", "templates"))
            ? Directory.EnumerateFiles(Path.Combine(lib, "open", "templates"), "*.dvdt", SearchOption.AllDirectories) : [];
        foreach (var dir in Roots().SelectMany(r => TemplateFolders(r)).Concat(dvdt).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // a converted pack is read above, in its own format: walking it as a raw template would list its pictures twice
            if (Directory.Exists(dir) && TemplatePack.TryRead(dir) is not null) continue;
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
    /// Zips the template folders named in <see cref="FoldersFile"/> into <paramref name="zip"/>: pictures and layout files only (no fonts, no programs),
    /// as "Blu-ray|DVD|Thèmes/[shape/]name/...". publish.ps1 embeds it in the exe. Returns the number of templates written; none is an error, so that a
    /// release never ships without them unnoticed.
    /// <paramref name="roots"/> replaces the configured folders (for a check).
    /// </summary>
    public static int Pack(string zip, Action<string>? log = null, IEnumerable<string>? roots = null)
    {
        string[] keep = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".xml"];
        if (File.Exists(zip)) File.Delete(zip);
        using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in (roots ?? UserRoots()).SelectMany(r => TemplateFolders(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // a pack BurnSmith made from a template is not a source: the library can be named as a whole, its converted folder included
            if (TemplatePack.TryRead(dir) is not null) continue;
            if (InstalledTemplate.Load(dir) is not { Unsupported: null } pack) continue;
            var name = Path.GetFileName(dir);
            var prefix = pack.Kind switch
            {
                TemplateKind.BluRay => $"Blu-ray/{name}",
                TemplateKind.Dvd => $"DVD/{name}",
                TemplateKind.Pictures => $"Thèmes/{Path.GetFileName(Path.GetDirectoryName(dir))}/{name}",
                TemplateKind.Theme => $"Thèmes/{name}",
                TemplateKind.Library => $"Libres/{name}",
                _ => name,
            };
            if (!done.Add(prefix)) continue;
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => keep.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();
            if (pack.Kind == TemplateKind.Theme) // the 1920x1080 pictures, the gallery thumbnail and the layout in clear text
            {
                files = files.Where(f => Path.GetRelativePath(dir, f).StartsWith("1920x1080") || Path.GetFileName(f) == "thumb_169.jpg").ToList();
                if (ThemeSkin.ReadConfig(dir) is { } layout)
                    using (var w = new StreamWriter(archive.CreateEntry($"{prefix}/config.xml").Open())) w.Write(layout);
            }
            foreach (var f in files)
                System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, f, $"{prefix}/{Path.GetRelativePath(dir, f).Replace('\\', '/')}", System.IO.Compression.CompressionLevel.Optimal);
            log?.Invoke($"{prefix}: {files.Count} fichiers");
        }
        if (done.Count == 0) throw new InvalidOperationException("aucun modèle de menu trouvé : ajoutez un dossier dans template-folders.txt, une version publiée doit les porter");
        using (var w = new StreamWriter(archive.CreateEntry("NOTICE.txt").Open()))
            w.Write("Images and layout files of menu templates, kept as published by their owners.\nThey belong to their authors and are not part of BurnSmith's own code, which is MIT-licensed (LICENSE).\nNo program, font or interface of these products is included.\n");
        return done.Count;
    }
}
