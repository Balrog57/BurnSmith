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

/// <summary>Template artwork (transparent PNG / JPEG) stretched to <paramref name="Rect"/>. <paramref name="Backdrop"/> = the page's background picture, which a moving menu replaces by its video.</summary>
/// <param name="Cover">Fill the rectangle with the picture's proportions kept (cropped), instead of stretching it.</param>
public sealed record Image(string Path, Box Rect, bool Backdrop = false, bool Cover = false) : Op;

/// <summary>Template text: own font (optionally a .ttf next to the template), colour, outline.</summary>
public sealed record Text(string Value, Box Rect, double Size, string Font, string? FontDir, string Color, bool Bold, bool Italic, string Align = "left",
                          string? Stroke = null, double StrokeWidth = 0) : Op;

/// <summary>A picture (null = dark placeholder) cut by the template's mask, with its frame drawn on top.</summary>
public sealed record Framed(Box Rect, string? Picture, string? Frame, string? Mask) : Op;

/// <summary>
/// What a button does. Kinds: play (Playlist), chapter (Playlist + Mark), page (Page),
/// playall (Playlist = first, Mark = how many, played one after the other),
/// audio (Mark = stream number, 1-based), sub (Mark = stream number, 0 = off).
/// </summary>
public sealed record MenuAction(string Kind, string? Page = null, int Playlist = 0, int Mark = 0);

public sealed record MenuButton(string Id, Box Rect, MenuAction Action)
{
    public string? Left { get; init; }
    public string? Right { get; init; }
    public string? Up { get; init; }
    public string? Down { get; init; }
    /// <summary>Template artwork shown while the button is selected; null = the design's accent frame.</summary>
    public string? FocusImage { get; init; }
    /// <summary>Where <see cref="FocusImage"/> goes when it is only a marker beside the button (a star next to text); null = it covers the button.</summary>
    public Box? FocusSlot { get; init; }
}

public sealed record MenuPage(string Id, List<Op> Ops, List<MenuButton> Buttons);

/// <summary>One video of a multi-video disc (an episode).</summary>
public sealed record TitleEntry(string Name, int Playlist, string? Picture);

/// <summary>
/// Disc facts the menu needs: playlist number, preview picture, one picture per chapter (null = none).
/// <see cref="Titles"/> with 2+ entries makes a series menu (episodes instead of chapters).
/// <see cref="Audio"/> / <see cref="Subtitles"/> are the display names of the tracks on the disc; they give the settings page.
/// <see cref="Bonus"/> are the extras (trailers, making-of): playlists after the main feature's, listed on their own Bonus page.
/// </summary>
public sealed record MenuContent(int Playlist, string? PreviewPicture, IReadOnlyList<string?> ChapterPictures,
    IReadOnlyList<TitleEntry>? Titles = null, IReadOnlyList<string>? Audio = null, IReadOnlyList<string>? Subtitles = null,
    IReadOnlyList<TitleEntry>? Bonus = null)
{
    public bool Series => Titles is { Count: > 1 };
    public bool HasBonus => Bonus is { Count: > 0 };
    public IReadOnlyList<string> AudioNames => (Audio ?? []).Take(MenuBuilder.MaxAudio).ToList();
    public IReadOnlyList<string> SubtitleNames => (Subtitles ?? []).Take(MenuBuilder.MaxSubtitles).ToList();
    /// <summary>The settings page exists when the viewer has something to choose.</summary>
    public bool HasSettings => AudioNames.Count > 1 || SubtitleNames.Count > 0;
}

/// <summary>Turns a design + disc facts into pages on a 1920x1080 canvas. Root page first, then the scene/episode pages, then settings.</summary>
public static class MenuBuilder
{
    public const int W = 1920, H = 1080, FocusMargin = 10, MaxAudio = 6, MaxSubtitles = 5, MaxCreditLines = 14, MarkerSize = 36, MarkerGap = 24;

    /// <summary>Scene buttons per page.</summary>
    public static int PerPage(MenuDesign d) => Math.Max(1, d.Cols) * Math.Max(1, d.Rows);

    internal sealed record Cell(string Id, string Label, string? Picture, MenuAction Action);

    internal static List<Cell> Cells(MenuDesign d, MenuContent c) => c.Series
        ? c.Titles!.Select((t, i) => new Cell($"e{i + 1}", t.Name, t.Picture, new MenuAction("play", Playlist: t.Playlist))).ToList()
        : c.ChapterPictures.Select((p, i) => new Cell($"c{i + 1}", $"{d.ChapterLabel} {i + 1}", p, new MenuAction("chapter", Playlist: c.Playlist, Mark: i))).ToList();

    /// <summary>The Bonus page's buttons: each plays its video on its own and the disc then comes back to the menu.</summary>
    internal static List<Cell> BonusCells(MenuContent c) =>
        (c.Bonus ?? []).Select((t, i) => new Cell($"b{i + 1}", t.Name, t.Picture, new MenuAction("play", Playlist: t.Playlist))).ToList();

    /// <summary>Pages needed for <paramref name="count"/> buttons, <paramref name="perPage"/> on each.</summary>
    internal static int PageCount(int count, int perPage) => (count + perPage - 1) / perPage;

    /// <summary>The installed template of a design (it can be gone: DVDFab moved or uninstalled).</summary>
    static InstalledTemplate? Pack(MenuDesign d) => d.TemplateDir == null ? null
        : InstalledTemplate.Load(d.TemplateDir) ?? throw new FileNotFoundException($"Modèle introuvable : {d.TemplateDir} (dossier déplacé ou désinstallé ?)");

    /// <summary>Distance of the "active track" dot from the left edge of its button, as written to the BD-J menu file.</summary>
    public static int MarkerOffset(MenuDesign d) => Pack(d) is { HasSetupLists: true } p ? p.MarkerOffset : FocusMargin - MarkerSize - MarkerGap;

    public static List<MenuPage> Build(MenuDesign d, MenuContent c)
    {
        if (Pack(d) is { } pack) return pack.Pages(d, c);
        var cells = Cells(d, c);
        var pages = new List<MenuPage> { Root(d, c, cells.Count > 1) };
        var gridPages = cells.Count > 1 ? (cells.Count + PerPage(d) - 1) / PerPage(d) : 0;
        for (var i = 1; i <= gridPages; i++) pages.Add(Grid(d, c, cells, i, gridPages));
        if (c.HasBonus)
        {
            var bonus = BonusCells(c);
            var bonusPages = PageCount(bonus.Count, PerPage(d));
            for (var i = 1; i <= bonusPages; i++) pages.Add(Grid(d, c, bonus, i, bonusPages, "bonus", d.BonusLabel));
        }
        if (c.HasSettings) pages.Add(Settings(d, c, null, null));
        if (d.CreditLines().Count > 0) pages.Add(Credits(d, null));
        return pages;
    }

    /// <summary>True for a light colour (#RRGGBB): text in it needs a dark backdrop, and the other way round.</summary>
    internal static bool IsLight(string hex)
    {
        var rgb = hex.TrimStart('#');
        return Convert.ToInt32(rgb[..2], 16) * 0.3 + Convert.ToInt32(rgb[2..4], 16) * 0.59 + Convert.ToInt32(rgb[4..6], 16) * 0.11 > 128;
    }

    /// <summary>The card behind a button, drawn in the design's button style (nothing for "Text").</summary>
    internal static List<Op> Card(MenuDesign d, Box r)
    {
        var radius = d.Square ? 0 : 28;
        switch (d.ButtonStyle)
        {
            case "Text": return [];
            case "Card": return [new Fill(r, d.CardFill, 0.92, radius)];
            case "Frame": return [new Fill(r, d.Accent, 1, 0), new Fill(r.Inflate(-3), d.CardFill, 1, 0), new Fill(r.Inflate(-9), d.Accent, 1, 0), new Fill(r.Inflate(-12), d.CardFill, 1, 0)];
            case "Glass": return [new Fill(r.Inflate(3), d.Glow, 0.9, radius + 3), new Fill(r, d.CardFill, 0.85, radius)];
            default: // translucent capsule: white on dark designs, black on light ones (dark text), so it shows on any background
                var lightText = IsLight(d.TextColor);
                return [new Fill(r, lightText ? "#FFFFFF" : "#000000", lightText ? 0.14 : 0.10, radius)];
        }
    }

    internal static List<Op> Button(MenuDesign d, Box r, string text, int size = 54)
    {
        var ops = Card(d, r);
        ops.Add(new Label(text, r, size, true));
        return ops;
    }

    static MenuPage Root(MenuDesign d, MenuContent c, bool withGrid)
    {
        var ops = new List<Op> { new Label(d.Title, new Box(0, 70, W, 140), 100, true) };
        if (d.ShowPreview && c.PreviewPicture != null) ops.Add(new Picture(c.PreviewPicture, new Box(560, 250, 800, 450), 24, 4));

        var entries = new List<(string Id, string Text, MenuAction Action)>
        {
            c.Series
                ? ("play", d.PlayAllLabel, new MenuAction("playall", Playlist: c.Titles![0].Playlist, Mark: c.Titles.Count))
                : ("play", d.PlayLabel, new MenuAction("play", Playlist: c.Playlist)),
        };
        if (withGrid) entries.Add(("scenes", c.Series ? d.EpisodesLabel : d.ScenesLabel, new MenuAction("page", "scenes1")));
        if (c.HasBonus) entries.Add(("bonus", d.BonusLabel, new MenuAction("page", "bonus1")));
        if (c.HasSettings) entries.Add(("settings", d.SettingsLabel, new MenuAction("page", "settings")));
        if (d.CreditLines().Count > 0) entries.Add(("credits", d.CreditsLabel, new MenuAction("page", "credits")));

        var n = entries.Count;
        // up to 4 buttons keep BurnSmith's 400 px; 5 (Play, Scenes, Bonus, Settings, Credits) would not fit the 1920 px screen, so they narrow
        var gap = n <= 4 ? 60 : 40;
        var bw = n <= 4 ? 400 : (W - 120 - (n - 1) * gap) / n;
        var x0 = (W - (n * bw + (n - 1) * gap)) / 2;
        var buttons = new List<MenuButton>();
        for (var k = 0; k < n; k++)
        {
            var box = new Box(x0 + k * (bw + gap), 820, bw, 110);
            ops.AddRange(Button(d, box, entries[k].Text));
            buttons.Add(new MenuButton(entries[k].Id, box, entries[k].Action)
            {
                Left = n > 1 ? entries[(k + n - 1) % n].Id : null,
                Right = n > 1 ? entries[(k + 1) % n].Id : null,
            });
        }
        return new MenuPage("root", ops, buttons);
    }

    /// <summary>
    /// The credits page: the design's free text, one centred row per line, and a Back button. <paramref name="backdrop"/> is the artwork
    /// an installed template gives it (null = the design's own background).
    /// </summary>
    public static MenuPage Credits(MenuDesign d, List<Op>? backdrop)
    {
        var lines = d.CreditLines();
        var ops = backdrop ?? [];
        ops.Add(new Label(d.CreditsLabel, new Box(0, 60, W, 120), 80, true));
        const int top = 210, bottom = 920;
        var pitch = Math.Min(72, (bottom - top) / Math.Max(1, lines.Count));
        for (var i = 0; i < lines.Count; i++)
            ops.Add(new Label(lines[i], new Box(160, top + i * pitch, W - 320, pitch), Math.Clamp(pitch * 3 / 5, 24, 44), false));
        var back = new Box((W - 300) / 2, 950, 300, 90);
        ops.AddRange(Button(d, back, d.BackLabel, 46));
        return new MenuPage("credits", ops, [new MenuButton("back", back, new MenuAction("page", "root"))]);
    }

    /// <summary>Cell rectangles of a scene page: BurnSmith's original 3x2 geometry, or the ratio geometry (margins, gap, header, footer) of the design.</summary>
    static (List<Box> Cells, int HeaderY, int HeaderH, int NavY, int NavH, bool Original) GridGeometry(MenuDesign d)
    {
        var cols = Math.Max(1, d.Cols);
        var rows = Math.Max(1, d.Rows);
        var cells = new List<Box>();
        if (d.MarginX <= 0 && cols == 3 && rows == 2)
        {
            const int tw = 480, th = 270, gapX = 70, rowPitch = 380;
            var x0 = (W - (cols * tw + (cols - 1) * gapX)) / 2;
            for (var i = 0; i < 6; i++) cells.Add(new Box(x0 + i % cols * (tw + gapX), 190 + i / cols * rowPitch, tw, th));
            return (cells, 60, 120, 930, 90, true);
        }
        // the design's own ratios, or sensible ones when only the grid size was chosen
        double mx = d.MarginX > 0 ? d.MarginX : 0.055, my = d.MarginY > 0 ? d.MarginY : 0.075, gp = d.Gap > 0 ? d.Gap : 0.018;
        double hd = d.Header > 0 ? d.Header : 0.12, ft = d.Footer > 0 ? d.Footer : 0.12;
        double marginX = W * mx, marginY = H * my, gap = Math.Max(8, Math.Min(W, H) * gp), header = H * hd, footer = H * ft;
        var cw = Math.Max(16, (W - marginX * 2 - gap * (cols - 1)) / cols);
        var ch = Math.Max(16, (H - marginY * 2 - header - footer - gap * (rows - 1)) / rows);
        for (var i = 0; i < cols * rows; i++)
            cells.Add(new Box((int)(marginX + i % cols * (cw + gap)), (int)(marginY + header + i / cols * (ch + gap)), (int)cw, (int)ch));
        var navH = (int)Math.Min(90, footer - 8);
        return (cells, (int)marginY, (int)header, (int)(H - marginY - footer + (footer - navH) / 2) + 8, navH, false); // +8: not touching the last row
    }

    /// <summary>
    /// Chapter thumbnails of a movie, or the episodes of a series, or the extras, as many per page as the design's grid holds.
    /// Pages are called <paramref name="prefix"/>1, <paramref name="prefix"/>2...; <paramref name="title"/> null = the chapters' or episodes' heading.
    /// </summary>
    static MenuPage Grid(MenuDesign d, MenuContent c, List<Cell> cells, int number, int total, string prefix = "scenes", string? title = null)
    {
        var cols = Math.Max(1, d.Cols);
        var perPage = PerPage(d);
        var (slots, headerY, headerH, navY, navH, original) = GridGeometry(d);
        var first = (number - 1) * perPage;
        var count = Math.Min(perPage, cells.Count - first);
        var ops = new List<Op> { new Label(title ?? (c.Series ? d.EpisodesLabel : d.ScenesLabel), new Box(0, headerY, W, headerH), original ? 80 : Math.Clamp(headerH * 2 / 3, 40, 80), true) };
        var buttons = new List<MenuButton>();
        var nav = new List<(string Id, Box Rect, MenuAction Action, string Text)>();
        var x0 = slots[0].X;
        var navW = Math.Min(240, slots[0].W);
        if (number > 1) nav.Add(("prev", new Box(x0, navY, navW, navH), new MenuAction("page", $"{prefix}{number - 1}"), "<"));
        nav.Add(("home", new Box((W - 300) / 2, navY, 300, navH), new MenuAction("page", "root"), d.HomeLabel));
        if (number < total) nav.Add(("next", new Box(W - x0 - navW, navY, navW, navH), new MenuAction("page", $"{prefix}{number + 1}"), ">"));

        var rows = (count + cols - 1) / cols;
        for (var i = 0; i < count; i++)
        {
            var r = slots[i];
            var cell = cells[first + i];
            if (original)
            {
                if (cell.Picture is { } pic) ops.Add(new Picture(pic, r, 12, 0));
                else ops.Add(new Fill(r, "#000000", 0.4, 12));
                ops.Add(new Label(cell.Label, new Box(r.X, r.Y + r.H + 6, r.W, 44), 34, false));
            }
            else if (d.Thumbnails)
            {
                var labelH = Math.Clamp(r.H / 5, 36, 64);
                var pad = Math.Max(6, r.H / 24);
                var pictureBox = new Box(r.X + pad, r.Y + pad, r.W - 2 * pad, r.H - labelH - 2 * pad);
                ops.AddRange(Card(d, r));
                if (cell.Picture is { } pic) ops.Add(new Picture(pic, pictureBox, d.Square ? 0 : 10, 0));
                else ops.Add(new Fill(pictureBox, "#000000", 0.4, d.Square ? 0 : 10));
                ops.Add(new Label(cell.Label, new Box(r.X, r.Y + r.H - labelH - pad / 2, r.W, labelH), Math.Clamp(labelH * 3 / 5, 22, 36), false));
            }
            else ops.AddRange(Button(d, r, cell.Label, Math.Clamp(r.H / 3, 26, 54)));
            var row = i / cols;
            string? Id(int n) => n >= 0 && n < count ? cells[first + n].Id : null;
            buttons.Add(new MenuButton(cell.Id, r, cell.Action)
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
                Up = cells[first + count - 1].Id, // back to the last thumbnail
            });
        }
        return new MenuPage($"{prefix}{number}", ops, buttons);
    }

    /// <summary>
    /// Audio and subtitle choice, one column each. With <paramref name="activeAudio"/> / <paramref name="activeSub"/> null no
    /// marker is drawn (the BD-J menu draws it live); a DVD has no live drawing, so it uses one page per choice.
    /// </summary>
    public static MenuPage Settings(MenuDesign d, MenuContent c, int? activeAudio, int? activeSub, string id = "settings")
    {
        var pack = Pack(d);
        if (pack is { HasSetupLists: true }) return pack.Settings(d, c, activeAudio, activeSub, id);
        const int colW = 680, rowH = 84, pitch = 100, top = 300;
        var audio = c.AudioNames.Count > 1 ? c.AudioNames : [];
        var subs = c.SubtitleNames;
        var ops = pack?.SetupBackdrop(c.PreviewPicture) ?? []; // a template without audio / subtitle rows: its background, BurnSmith's rows
        ops.Add(new Label(d.SettingsLabel, new Box(0, 60, W, 120), 80, true));
        var buttons = new List<MenuButton>();
        var both = audio.Count > 0 && subs.Count > 0;
        int AudioX() => both ? 200 : (W - colW) / 2;
        int SubX() => both ? W - 200 - colW : (W - colW) / 2;

        List<string> Column(string header, int x, IReadOnlyList<string> names, int firstNumber, string idPrefix, string kind, int? active)
        {
            ops.Add(new Label(header, new Box(x, 200, colW, 70), 46, true, "left"));
            var ids = new List<string>();
            for (var i = 0; i < names.Count; i++)
            {
                var number = firstNumber + i;
                var box = new Box(x, top + i * pitch, colW, rowH);
                ops.AddRange(Button(d, box, names[i], 44));
                if (active == number) ops.Add(new Fill(new Box(x - MarkerSize - MarkerGap, box.Y + (rowH - MarkerSize) / 2, MarkerSize, MarkerSize), d.Accent, 1, MarkerSize / 2));
                var bid = idPrefix + number;
                ids.Add(bid);
                buttons.Add(new MenuButton(bid, box, new MenuAction(kind, Mark: number)));
            }
            return ids;
        }

        var left = audio.Count > 0 ? Column(d.AudioLabel, AudioX(), audio, 1, "a", "audio", activeAudio) : [];
        var subNames = subs.Count > 0 ? new[] { d.OffLabel }.Concat(subs).ToList() : [];
        var right = subs.Count > 0 ? Column(d.SubtitlesLabel, SubX(), subNames, 0, "s", "sub", activeSub) : [];

        var back = new Box((W - 300) / 2, 950, 300, 90);
        ops.AddRange(Button(d, back, d.BackLabel, 46));
        var last = (left.Count > 0 ? left : right)[^1];
        buttons.Add(new MenuButton("back", back, new MenuAction("page", "root")) { Up = last });

        // neighbours: up/down inside a column, left/right to the same row of the other column, the last row leads to Back
        for (var k = 0; k < buttons.Count - 1; k++)
        {
            var b = buttons[k];
            var col = left.Contains(b.Id) ? left : right;
            var other = col == left ? right : left;
            var row = col.IndexOf(b.Id);
            string? Other() => other.Count == 0 ? null : other[Math.Min(row, other.Count - 1)];
            buttons[k] = b with
            {
                Up = row > 0 ? col[row - 1] : null,
                Down = row < col.Count - 1 ? col[row + 1] : "back",
                Left = col == right ? Other() : null,
                Right = col == left ? Other() : null,
            };
        }
        return new MenuPage(id, ops, buttons);
    }
}
