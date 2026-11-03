namespace BurnSmith.Core.Menu;

public readonly record struct Box(int X, int Y, int W, int H)
{
    public Box Inflate(int m) => new(X - m, Y - m, W + 2 * m, H + 2 * m);
}

/// <summary>Drawing instructions of a page. Plain data, so the layout is testable without WPF.</summary>
public abstract record Op;
public sealed record Fill(Box Rect, string Color, double Opacity, int Radius) : Op;
public sealed record Label(string Value, Box Rect, int Size, bool Bold, string Align = "center") : Op;
public sealed record Picture(string Path, Box Rect, int Radius, int BorderWidth) : Op;

/// <summary>What a button does. Kinds: play (Playlist), chapter (Playlist + Mark), page (Page).</summary>
public sealed record MenuAction(string Kind, string? Page = null, int Playlist = 0, int Mark = 0);

public sealed record MenuButton(string Id, Box Rect, MenuAction Action)
{
    public string? Left { get; init; }
    public string? Right { get; init; }
    public string? Up { get; init; }
    public string? Down { get; init; }
}

public sealed record MenuPage(string Id, List<Op> Ops, List<MenuButton> Buttons);

/// <summary>Movie facts the menu needs: playlist number, preview picture, one picture per chapter (null = none).</summary>
public sealed record MenuContent(int Playlist, string? PreviewPicture, IReadOnlyList<string?> ChapterPictures);

/// <summary>Turns a design + movie facts into pages on a 1920x1080 canvas. Root page first, then the scene pages.</summary>
public static class MenuBuilder
{
    public const int W = 1920, H = 1080, FocusMargin = 10, ThumbsPerPage = 6;

    public static List<MenuPage> Build(MenuDesign d, MenuContent c)
    {
        var chapters = c.ChapterPictures.Count;
        var pages = new List<MenuPage> { Root(d, c, chapters > 1) };
        var scenePages = chapters > 1 ? (chapters + ThumbsPerPage - 1) / ThumbsPerPage : 0;
        for (var i = 1; i <= scenePages; i++) pages.Add(Scenes(d, c, i, scenePages));
        return pages;
    }

    static List<Op> Button(MenuDesign d, Box r, string text, int size = 54) =>
        d.ButtonStyle == "Text" ? [new Label(text, r, size, true)] : [new Fill(r, "#FFFFFF", 0.14, 28), new Label(text, r, size, true)];

    static MenuPage Root(MenuDesign d, MenuContent c, bool withScenes)
    {
        var ops = new List<Op> { new Label(d.Title, new Box(0, 70, W, 140), 100, true) };
        if (d.ShowPreview && c.PreviewPicture != null) ops.Add(new Picture(c.PreviewPicture, new Box(560, 250, 800, 450), 24, 4));

        var buttons = new List<MenuButton>();
        var play = new Box(withScenes ? 520 : 760, 820, 400, 110);
        var scenes = new Box(1000, 820, 400, 110);
        ops.AddRange(Button(d, play, d.PlayLabel));
        buttons.Add(new MenuButton("play", play, new MenuAction("play", Playlist: c.Playlist))
        { Left = withScenes ? "scenes" : null, Right = withScenes ? "scenes" : null });
        if (withScenes)
        {
            ops.AddRange(Button(d, scenes, d.ScenesLabel));
            buttons.Add(new MenuButton("scenes", scenes, new MenuAction("page", "scenes1")) { Left = "play", Right = "play" });
        }
        return new MenuPage("root", ops, buttons);
    }

    static MenuPage Scenes(MenuDesign d, MenuContent c, int number, int total)
    {
        const int cols = 3, tw = 480, th = 270, gapX = 70, rowPitch = 380;
        var first = (number - 1) * ThumbsPerPage;
        var count = Math.Min(ThumbsPerPage, c.ChapterPictures.Count - first);
        var ops = new List<Op> { new Label(d.ScenesLabel, new Box(0, 60, W, 120), 80, true) };
        var buttons = new List<MenuButton>();
        var x0 = (W - (cols * tw + (cols - 1) * gapX)) / 2;
        var nav = new List<(string Id, Box Rect, MenuAction Action, string Text)>();
        if (number > 1) nav.Add(("prev", new Box(x0, 930, 240, 90), new MenuAction("page", $"scenes{number - 1}"), "<"));
        nav.Add(("home", new Box((W - 300) / 2, 930, 300, 90), new MenuAction("page", "root"), d.HomeLabel));
        if (number < total) nav.Add(("next", new Box(W - x0 - 240, 930, 240, 90), new MenuAction("page", $"scenes{number + 1}"), ">"));

        var rows = (count + cols - 1) / cols;
        for (var i = 0; i < count; i++)
        {
            var r = new Box(x0 + i % cols * (tw + gapX), 190 + i / cols * rowPitch, tw, th);
            var chapter = first + i + 1;
            if (c.ChapterPictures[first + i] is { } pic) ops.Add(new Picture(pic, r, 12, 0));
            else ops.Add(new Fill(r, "#000000", 0.4, 12));
            ops.Add(new Label($"{d.ChapterLabel} {chapter}", new Box(r.X, r.Y + th + 6, tw, 44), 34, false));
            var row = i / cols;
            string? Id(int n) => n >= 0 && n < count ? $"c{first + n + 1}" : null;
            buttons.Add(new MenuButton($"c{chapter}", r, new MenuAction("chapter", Playlist: c.Playlist, Mark: chapter - 1))
            {
                Left = i % cols > 0 ? Id(i - 1) : null,
                Right = i % cols < cols - 1 ? Id(i + 1) : null,
                Up = row > 0 ? Id(i - cols) : null,
                Down = row < rows - 1 && i + cols < count ? Id(i + cols) : "home",
            });
        }
        foreach (var (id, rect, action, text) in nav)
        {
            ops.AddRange(Button(d, rect, text, 46));
            var k = nav.FindIndex(n => n.Id == id);
            buttons.Add(new MenuButton(id, rect, action)
            {
                Left = k > 0 ? nav[k - 1].Id : null,
                Right = k < nav.Count - 1 ? nav[k + 1].Id : null,
                Up = $"c{first + count}", // back to the last thumbnail
            });
        }
        return new MenuPage($"scenes{number}", ops, buttons);
    }
}
