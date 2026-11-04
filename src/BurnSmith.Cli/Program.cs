using BurnSmith.Core;
using BurnSmith.Core.Menu;

// burnsmith bd|dvd --video <file> [--video <file2> ...] --out <folder> [--design Midnight] [--title "My Disc"] [--no-menu]
//                  [--chapters 5] [--audio 0,1] [--sub 0] [bd only: --uhd --label X]
// --audio / --sub: 0-based track numbers of each source, kept in that order (default: first audio, no subtitles). Several --video need a menu.
var a = args.ToList();
string? Opt(string name) => a.IndexOf(name) is var i and >= 0 && i + 1 < a.Count ? a[i + 1] : null;
List<int>? Numbers(string name) => Opt(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
var videos = a.Select((v, i) => (v, i)).Where(x => x.v == "--video" && x.i + 1 < a.Count).Select(x => Path.GetFullPath(a[x.i + 1])).ToList();

if (a.Count == 0 || a[0] is not ("bd" or "dvd") || videos.Count == 0 || Opt("--out") is not { } outDir)
{
    Console.Error.WriteLine("usage: burnsmith bd|dvd --video <file> [--video <file> ...] --out <folder> [--design NAME] [--title TEXT] [--no-menu] [--chapters MINUTES] [--audio N,N] [--sub N,N] [bd: --uhd --label NAME]");
    return 2;
}

var design = a.Contains("--no-menu") ? null
    : (MenuDesign.BuiltIn.FirstOrDefault(d => d.Name.Equals(Opt("--design"), StringComparison.OrdinalIgnoreCase)) ?? MenuDesign.BuiltIn[0]).Clone();
if (design != null && Opt("--title") is { } title) design.Title = title;
var chapters = int.TryParse(Opt("--chapters"), out var m) ? m : 5;
var titles = videos.Select(v => new DiscTitle(v, null, Numbers("--audio"), Numbers("--sub"))).ToList();

if (a[0] == "bd")
    BluRay.Build(new BluRayJob(titles, Path.GetFullPath(outDir), design, a.Contains("--uhd"), Opt("--label") ?? "BURNSMITH", chapters), Console.WriteLine);
else
    Dvd.Build(new DvdJob(titles, Path.GetFullPath(outDir), design, chapters), Console.WriteLine);
return 0;
