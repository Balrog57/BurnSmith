// Disc-level layout (titles, movie objects, index) modelled on bdmvkit's program.py / disc.py: https://github.com/kajisho5/bdmvkit, commit 5789076 (v0.7.0).
// Copyright (c) kajisho5 (Shogo Kajiyama). GNU Lesser General Public License v3.0 or later (COPYING.LESSER upstream);
// redistributed here under the GNU GPL v3 or later, with the LGPL's additional permissions removed, as section 7 of the GPL v3 allows. See THIRD-PARTY-NOTICES.md.
// Reduced to what BurnSmith needs: movies written by tsMuxeR plus menu pages (no link transitions, shuffle, play-all or pop-up).
using System.Globalization;

namespace BurnSmith.Core.Hdmv;

/// <summary>What a button does. Movies are numbered from 1 in the order of <see cref="HdmvMenuSet.Movies"/>.</summary>
public abstract record HdmvAction
{
    /// <summary>Play movie n from its start, then come back to the first page.</summary>
    public static HdmvAction PlayTitle(int title) => new Movie(title, 1);
    /// <summary>Play movie n from chapter c (1-based), then come back to the first page.</summary>
    public static HdmvAction PlayChapter(int title, int chapter) => new Movie(title, chapter);
    /// <summary>Show another page of the set.</summary>
    public static HdmvAction OpenPage(string pageId) => new Page(pageId);
    /// <summary>Your own 12-byte commands (<see cref="HdmvCommand"/>), run as they are.</summary>
    public static HdmvAction Run(params byte[][] commands) => new Raw(commands);

    internal sealed record Movie(int Title, int Chapter) : HdmvAction;
    internal sealed record Page(string Id) : HdmvAction;
    internal sealed record Raw(IReadOnlyList<byte[]> Cmds) : HdmvAction;
}

/// <summary>A movie already on the disc (written by tsMuxeR): its playlist number (00001.mpls is 1) and its number of chapter marks.</summary>
public sealed record HdmvMovie(int Playlist, int Chapters);

/// <summary>A button of a page: pictures are files (PNG ...), read through ffmpeg; the selected picture gives the button its size.</summary>
public sealed class HdmvButtonSpec
{
    public required string Id { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required string Selected { get; init; }
    public string? Activated { get; init; }
    public string? Normal { get; init; }
    public required HdmvAction Action { get; init; }
    public IReadOnlyDictionary<string, string> Nav { get; init; } = new Dictionary<string, string>();
    public bool Auto { get; init; }
}

/// <summary>A menu page: a background picture held on screen with its buttons over it. The first page is the main menu.</summary>
public sealed class HdmvPage
{
    public required string Id { get; init; }
    public required string Background { get; init; }
    public required IReadOnlyList<HdmvButtonSpec> Buttons { get; init; }
    /// <summary>Id of the button selected when the page opens (default: the first).</summary>
    public string? Default { get; init; }
    /// <summary>Length of the page's clip; the last picture is then held. 1 s is enough for a still page.</summary>
    public double Seconds { get; init; } = 1;
}

/// <summary>Movies and menu pages of a disc, and the choices of the writer.</summary>
public sealed class HdmvMenuSet
{
    public required IReadOnlyList<HdmvMovie> Movies { get; init; }
    public required IReadOnlyList<HdmvPage> Pages { get; init; }
    /// <summary>Frame rate of the menu clips (and ICS frame-rate code): 24000/1001, 25/1 or 30000/1001 for 1080, 24000/1001, 50/1 or 60000/1001 for 720.</summary>
    public (int Num, int Den) Fps { get; init; } = (24000, 1001);
    public bool Hd720 { get; init; }
    /// <summary>ultrafast x264, for tests.</summary>
    public bool Fast { get; init; }
    /// <summary>Start with the main menu (else with movie 1, the menu then being the Top Menu key).</summary>
    public bool StartWithMenu { get; init; } = true;
    /// <summary>Template of the IG options: page UO mask, timeline of the segments, palette (defaults: the reference disc's dating and mask, median cut; <see cref="IgOptions.BdmvkitCompatible"/> for bdmvkit's output). Default button and frame size come from the page.</summary>
    public IgOptions Ig { get; init; } = new();
    /// <summary>UO mask of the menu playlists (list and item). Default: the mask the reference disc's menu playlists carry, the same as its IG page.</summary>
    public ulong PlaylistUoMask { get; init; } = UoMask.MenuReference;
}

/// <summary>What <see cref="HdmvAuthor.AddMenus"/> wrote.</summary>
public sealed record HdmvResult(IReadOnlyList<int> PageClips, IReadOnlyList<int> PageTitles, int FirstPlayObject, int TopMenuObject, int ObjectCount);

/// <summary>
/// Adds native HDMV menus to a BDMV folder that already holds the movies (written by tsMuxeR, <c>--no-menu</c> style): the menu clips
/// (.m2ts with a video of the page and its IG stream), their clip information and playlists, then a new MovieObject.bdmv and index.bdmv.
/// </summary>
public static class HdmvAuthor
{
    const int LastMenuRegister = 1; // GPR holding the title number of the page shown last (bdmvkit's LAST_MENU_REG)

    public static HdmvResult AddMenus(string bdmv, HdmvMenuSet set)
    {
        if (set.Movies.Count == 0) throw new ArgumentException("at least one movie");
        if (set.Pages.Count == 0) throw new ArgumentException("at least one page");
        var pageIds = set.Pages.Select(p => p.Id).ToList();
        if (pageIds.Distinct().Count() != pageIds.Count) throw new ArgumentException("page ids must be unique");
        var codes = HdmvFormats.VideoCodes(set.Fps.Num, set.Fps.Den, set.Hd720) ?? throw new ArgumentException($"{set.Fps.Num}/{set.Fps.Den} fps has no Blu-ray format");
        var movies = set.Movies.Count;
        int PageTitle(string id) => movies + 1 + (pageIds.IndexOf(id) is var i and >= 0 ? i : throw new ArgumentException($"no page '{id}'"));
        var mainMenu = PageTitle(pageIds[0]);

        // chapter objects follow the first play and top menu objects, in the order the buttons use them
        var chapterObjects = new List<(int Title, int Chapter)>();
        var firstChapterObject = movies + set.Pages.Count + 2;
        List<byte[]> Commands(HdmvAction action)
        {
            switch (action)
            {
                case HdmvAction.Movie m:
                    if (m.Title < 1 || m.Title > movies) throw new ArgumentException($"no movie {m.Title}");
                    if (m.Chapter < 1 || m.Chapter > Math.Max(1, set.Movies[m.Title - 1].Chapters)) throw new ArgumentException($"movie {m.Title} has no chapter {m.Chapter}");
                    if (m.Chapter == 1) return [HdmvCommand.JumpTitle(m.Title)];
                    var k = chapterObjects.IndexOf((m.Title, m.Chapter));
                    if (k < 0) { chapterObjects.Add((m.Title, m.Chapter)); k = chapterObjects.Count - 1; }
                    return [HdmvCommand.JumpObject(firstChapterObject + k)];
                case HdmvAction.Page p: return [HdmvCommand.JumpTitle(PageTitle(p.Id))];
                case HdmvAction.Raw r: return [.. r.Cmds];
                default: throw new ArgumentException("unknown action");
            }
        }

        foreach (var d in new[] { "PLAYLIST", "CLIPINF", "STREAM", Path.Combine("BACKUP", "PLAYLIST"), Path.Combine("BACKUP", "CLIPINF") })
            Directory.CreateDirectory(Path.Combine(bdmv, d));
        var next = Math.Max(Max("STREAM", "*.m2ts"), Math.Max(Max("PLAYLIST", "*.mpls"), Max("CLIPINF", "*.clpi"))) + 1;
        int Max(string dir, string pattern) =>
            Directory.EnumerateFiles(Path.Combine(bdmv, dir), pattern).Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).DefaultIfEmpty(0).Max();

        var work = Path.Combine(Path.GetTempPath(), "burnsmith-hdmv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var pageClips = new List<int>();
        var (width, height) = set.Hd720 ? (1280, 720) : (HdmvFormats.Width, HdmvFormats.Height);
        try
        {
            for (var j = 0; j < set.Pages.Count; j++)
            {
                var page = set.Pages[j];
                var number = next + j;
                var buttons = new List<IgButton>();
                foreach (var b in page.Buttons)
                {
                    var (w, h, selected) = MenuClip.ReadRgba(b.Selected);
                    buttons.Add(new IgButton
                    {
                        Id = b.Id, X = b.X, Y = b.Y, W = w, H = h, Selected = selected,
                        Activated = b.Activated == null ? null : MenuClip.ReadRgba(b.Activated, w, h).Rgba,
                        Normal = b.Normal == null ? null : MenuClip.ReadRgba(b.Normal, w, h).Rgba,
                        Commands = Commands(b.Action), Nav = b.Nav, Auto = b.Auto,
                    });
                }
                var sup = Path.Combine(work, $"{number:00000}.sup");
                File.WriteAllBytes(sup, IgStream.Build(buttons, codes.Rate, set.Ig with { Default = page.Default, Width = width, Height = height }));
                var m2ts = Path.Combine(bdmv, "STREAM", $"{number:00000}.m2ts");
                MenuClip.Encode(page.Background, sup, m2ts, set.Fps.Num, set.Fps.Den, page.Seconds, set.Fast, set.Hd720);
                var scan = TransportStream.Scan(m2ts);
                Write(bdmv, Path.Combine("CLIPINF", $"{number:00000}.clpi"), ClipFiles.Clpi(scan, codes.Format, codes.Rate, [], [], ig: true));
                var item = new PlayItem($"{number:00000}", scan.FirstPts / 2, scan.EndPts / 2, codes.Format, codes.Rate, [], [], Ig: true, Still: 2, Uo: set.PlaylistUoMask);
                Write(bdmv, Path.Combine("PLAYLIST", $"{number:00000}.mpls"), ClipFiles.Mpls([item], [(0, scan.FirstPts / 2)], set.PlaylistUoMask));
                pageClips.Add(number);
            }
        }
        finally { Tools.TryDelete(work); }

        // movie objects: one per title (movies first, then the pages), first play, top menu, then one per chapter a button starts from
        var objects = new List<IReadOnlyList<byte[]>>();
        foreach (var m in set.Movies) objects.Add([HdmvCommand.PlayPl(m.Playlist), HdmvCommand.JumpTitle(mainMenu)]);
        for (var j = 0; j < set.Pages.Count; j++) objects.Add([HdmvCommand.Move(LastMenuRegister, movies + 1 + j), HdmvCommand.PlayPl(pageClips[j])]);
        var firstPlay = objects.Count;
        objects.Add([HdmvCommand.JumpTitle(set.StartWithMenu ? mainMenu : 1)]);
        objects.Add([HdmvCommand.JumpTitle(mainMenu)]);
        foreach (var (title, chapter) in chapterObjects)
            objects.Add([HdmvCommand.PlayPlMark(set.Movies[title - 1].Playlist, chapter - 1), HdmvCommand.JumpTitle(mainMenu)]);
        Write(bdmv, "MovieObject.bdmv", HdmvNav.MovieObjects(objects));

        var titleObjects = Enumerable.Range(0, movies + set.Pages.Count).ToList();
        var interactive = Enumerable.Range(movies + 1, set.Pages.Count).ToHashSet();
        var indexFile = Path.Combine(bdmv, "index.bdmv");
        Write(bdmv, "index.bdmv", File.Exists(indexFile)
            ? HdmvNav.PatchIndex(File.ReadAllBytes(indexFile), titleObjects, firstPlay, firstPlay + 1, interactive)
            : HdmvNav.Index(titleObjects, firstPlay, firstPlay + 1, codes.Format, codes.Rate, interactive));
        return new HdmvResult(pageClips, Enumerable.Range(movies + 1, set.Pages.Count).ToList(), firstPlay, firstPlay + 1, objects.Count);
    }

    /// <summary>A navigation file goes to BDMV and to BDMV\BACKUP, as players expect.</summary>
    static void Write(string bdmv, string relative, byte[] data)
    {
        foreach (var root in new[] { bdmv, Path.Combine(bdmv, "BACKUP") })
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
    }
}
