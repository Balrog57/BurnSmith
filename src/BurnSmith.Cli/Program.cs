using BurnSmith.Core;
using BurnSmith.Core.Menu;

// burnsmith bd|dvd --video <file> [--video <file2> ...] --out <folder> [--design Midnight] [--title "My Disc"] [--no-menu]
//                  [--chapters 5] [--audio 0,1] [--sub 0] [bd only: --uhd --label X]
// --audio / --sub: 0-based track numbers of each source, kept in that order (default: first audio, no subtitles). Several --video need a menu.
var a = args.ToList();
string? Opt(string name) => a.IndexOf(name) is var i and >= 0 && i + 1 < a.Count ? a[i + 1] : null;
List<int>? Numbers(string name) => Opt(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
var videos = a.Select((v, i) => (v, i)).Where(x => x.v == "--video" && x.i + 1 < a.Count).Select(x => Path.GetFullPath(a[x.i + 1])).ToList();

// burnsmith preview <template name or folder> <out folder> [--picture frame.jpg] [--episodes N]
//   draws every page of a menu template with sample content (3 audio, 2 subtitles), to compare with the vendor's own thumbnail
if (a.Count >= 3 && a[0] == "preview")
{
    var chosen = TemplateCatalog.Find(a[1]) ?? throw new InvalidOperationException($"unknown template: {a[1]}");
    Directory.CreateDirectory(a[2]);
    var pic = Opt("--picture") is { } p ? Path.GetFullPath(p) : null;
    var episodes = int.TryParse(Opt("--episodes"), out var ep) ? ep : 0;
    var sample = new MenuContent(1, pic, Enumerable.Repeat(pic, 7).ToList(),
        episodes > 1 ? Enumerable.Range(1, episodes).Select(i => new TitleEntry($"Episode {i}", i, pic)).ToList() : null,
        ["Français AC-3 mono", "English AC-3 mono", "Español AC-3 mono"], ["Français", "English"]);
    chosen.Title = "My Blu-ray";
    var files = MenuRenderer.Sta(() => MenuBuilder.Build(chosen, sample).Select(page => (page.Id, Jpeg: MenuRenderer.Background(chosen, page))).ToList());
    foreach (var (id, jpeg) in files) File.WriteAllBytes(Path.Combine(a[2], $"{chosen.Name}_{id}.jpg"), jpeg);
    Console.WriteLine(string.Join(", ", files.Select(f => f.Id)));
    return 0;
}

// burnsmith check-templates [--matrix] [--picture frame.jpg] : builds every template as film and series and checks pages, buttons, remote moves, pictures;
// --matrix = every disc shape (1..40 chapters, 2..60 episodes, 1..9 audio, 0..9 subtitles) and 200 designs the menu editor can compose
if (a.Count >= 1 && a[0] == "check-templates") return TemplateCheck.Run(Opt("--picture") is { } cp ? Path.GetFullPath(cp) : null, a.Contains("--matrix"));

// burnsmith templates : every template found, and the ones that cannot be drawn with the reason
if (a.Count >= 1 && a[0] == "templates")
{
    var (usable, skipped) = TemplateCatalog.Scan();
    foreach (var f in usable) Console.WriteLine($"OK    {f.Design.Category} | {f.Design.Name}");
    foreach (var s in skipped) Console.WriteLine($"SKIP  {s.Name} | {s.Why} | {s.Dir}");
    Console.WriteLine($"{usable.Count} usable, {skipped.Count} skipped");
    return 0;
}

// burnsmith drives | iso <disc folder> <out.iso> [--label X] [--dvd] | burn <image.iso> <F:>   (Windows' own IMAPI2, nothing to install)
if (a.Count >= 1 && a[0] == "drives")
{
    var drives = Imapi.Drives();
    foreach (var d in drives)
    {
        var disc = d.Letter.Length > 0 ? Imapi.Inspect(d.Letter) : null;
        Console.WriteLine($"{(d.Letter.Length > 0 ? d.Letter : "?"),-3} {d.Name} - {(disc == null ? "no usable disc" : $"{disc.Kind}, {(disc.Blank ? "blank" : "not blank")}, {disc.FreeBytes / 1e9:0.0} GB free")}");
    }
    if (drives.Count == 0) Console.WriteLine("no disc burner visible to Windows");
    Imapi.SelfTest();
    Console.WriteLine("IMAPI2: typed interfaces and progress events OK");
    return 0;
}
if (a.Count >= 3 && a[0] == "verify") { Imapi.Verify(a[1], a[2], Console.WriteLine, new Progress<double>(p => Console.Write($"\r{p:P0}  "))); return 0; }
if (a.Count >= 3 && a[0] == "iso") { Iso.Build(a[1], a[2], Opt("--label") ?? "BURNSMITH", a.Contains("--dvd"), Console.WriteLine); return 0; }
if (a.Count >= 3 && a[0] == "burn") { Iso.Burn(a[1], a[2], Console.WriteLine, new Progress<double>(p => Console.Write($"\r{p:P0}  "))); return 0; }

if (a.Count == 0 || a[0] is not ("bd" or "dvd") || videos.Count == 0 || Opt("--out") is not { } outDir)
{
    Console.Error.WriteLine("usage: burnsmith bd|dvd --video <file> [--video <file> ...] --out <folder> [--design NAME] [--title TEXT] [--no-menu] [--chapters MINUTES] [--audio N,N] [--sub N,N] [bd: --uhd --label NAME]");
    return 2;
}

var design = a.Contains("--no-menu") ? null
    : (TemplateCatalog.Find(Opt("--design")) ?? (Opt("--design") is { } unknown ? throw new InvalidOperationException($"unknown template: {unknown}") : TemplateCatalog.Default())).Clone();
if (design != null && Opt("--title") is { } title) design.Title = title;
var chapters = int.TryParse(Opt("--chapters"), out var m) ? m : 5;
var titles = videos.Select(v => new DiscTitle(v, null, Numbers("--audio"), Numbers("--sub"))).ToList();

if (a[0] == "bd")
    BluRay.Build(new BluRayJob(titles, Path.GetFullPath(outDir), design, a.Contains("--uhd"), Opt("--label") ?? "BURNSMITH", chapters), Console.WriteLine);
else
    Dvd.Build(new DvdJob(titles, Path.GetFullPath(outDir), design, chapters), Console.WriteLine);
return 0;
