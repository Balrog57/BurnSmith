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

if (a.Count == 0 || a[0] is not ("bd" or "dvd") || videos.Count == 0 || Opt("--out") is not { } outDir)
{
    Console.Error.WriteLine("usage: burnsmith bd|dvd --video <file> [--video <file> ...] --out <folder> [--design NAME] [--title TEXT] [--no-menu] [--chapters MINUTES] [--audio N,N] [--sub N,N] [bd: --uhd --label NAME]");
    return 2;
}

var design = a.Contains("--no-menu") ? null
    : (TemplateCatalog.Find(Opt("--design")) ?? TemplateCatalog.Default()).Clone();
if (design != null && Opt("--title") is { } title) design.Title = title;
var chapters = int.TryParse(Opt("--chapters"), out var m) ? m : 5;
var titles = videos.Select(v => new DiscTitle(v, null, Numbers("--audio"), Numbers("--sub"))).ToList();

if (a[0] == "bd")
    BluRay.Build(new BluRayJob(titles, Path.GetFullPath(outDir), design, a.Contains("--uhd"), Opt("--label") ?? "BURNSMITH", chapters), Console.WriteLine);
else
    Dvd.Build(new DvdJob(titles, Path.GetFullPath(outDir), design, chapters), Console.WriteLine);
return 0;
