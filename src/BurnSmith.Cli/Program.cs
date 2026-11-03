using BurnSmith.Core;
using BurnSmith.Core.Menu;

// burnsmith bd|dvd --video <file> --out <folder> [--design Midnight] [--title "My Disc"] [--no-menu] [--chapters 5] [bd only: --uhd --label X]
var a = args.ToList();
string? Opt(string name) => a.IndexOf(name) is var i and >= 0 && i + 1 < a.Count ? a[i + 1] : null;

if (a.Count == 0 || a[0] is not ("bd" or "dvd") || Opt("--video") is not { } video || Opt("--out") is not { } outDir)
{
    Console.Error.WriteLine("usage: burnsmith bd|dvd --video <file> --out <folder> [--design NAME] [--title TEXT] [--no-menu] [--chapters MINUTES] [bd: --uhd --label NAME]");
    return 2;
}

var design = a.Contains("--no-menu") ? null
    : (MenuDesign.BuiltIn.FirstOrDefault(d => d.Name.Equals(Opt("--design"), StringComparison.OrdinalIgnoreCase)) ?? MenuDesign.BuiltIn[0]).Clone();
if (design != null && Opt("--title") is { } title) design.Title = title;
var chapters = int.TryParse(Opt("--chapters"), out var m) ? m : 5;

if (a[0] == "bd")
    BluRay.Build(new BluRayJob(Path.GetFullPath(video), Path.GetFullPath(outDir), design, a.Contains("--uhd"), Opt("--label") ?? "BURNSMITH", chapters), Console.WriteLine);
else
    Dvd.Build(new DvdJob(Path.GetFullPath(video), Path.GetFullPath(outDir), design, chapters), Console.WriteLine);
return 0;
