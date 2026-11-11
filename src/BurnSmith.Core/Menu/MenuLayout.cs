namespace BurnSmith.Core.Menu;

public readonly record struct Box(int X, int Y, int W, int H)
{
    public Box Inflate(int m) => new(X - m, Y - m, W + 2 * m, H + 2 * m);
}

/// <summary>Drawing instructions of a page. Plain data, so the layout is testable without WPF.</summary>
public abstract record Op;
public sealed record Fill(Box Rect, string Color, double Opacity, int Radius) : Op;
/// <summary>An outline around <paramref name="Rect"/> (the edge of a free element).</summary>
public sealed record Border(Box Rect, string Color, int Width, int Radius) : Op;
public sealed record Label(string Value, Box Rect, int Size, bool Bold, string Align = "center") : Op;
public sealed record Picture(string Path, Box Rect, int Radius, int BorderWidth) : Op;

/// <summary>Template artwork (transparent PNG / JPEG) stretched to <paramref name="Rect"/>. <paramref name="Backdrop"/> = the page's background picture, which a moving menu replaces by its video.</summary>
/// <param name="Cover">Fill the rectangle with the picture's proportions kept (cropped), instead of stretching it.</param>
public sealed record Image(string Path, Box Rect, bool Backdrop = false, bool Cover = false) : Op;

/// <summary>Template text: own font (optionally a .ttf next to the template), colour, outline.</summary>
public sealed record Text(string Value, Box Rect, double Size, string Font, string? FontDir, string Color, bool Bold, bool Italic, string Align = "left",
                          string? Stroke = null, double StrokeWidth = 0, bool Wrap = false, string? Shadow = null, double ShadowOffset = 0) : Op;

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
    /// <summary>
    /// The template's highlighted artwork for this button (<c>frame/0.dvd.sub.png</c> beside <c>frame/0.dvd.normal.png</c>): the picture a DVD
    /// subpicture is made from, composited at this button's place. Null = the design's own four-colour frame.
    /// </summary>
    public string? DvdHighlightImage { get; init; }
    /// <summary>The shape of that artwork (<c>frame/0.dvd.mask.png</c>, 1-bit); null = the whole rectangle.</summary>
    public string? DvdHighlightMask { get; init; }
    /// <summary>
    /// The template's own picture for the <em>chosen</em> state (a <c>selection/0.png</c> picture, the DVD's <c>select</c> image, a theme's
    /// <c>select</c> attribute). Null = the silhouette of <see cref="DvdHighlightImage"/> stands for both states.
    /// </summary>
    public string? DvdSelectImage { get; init; }
}

public sealed record MenuPage(string Id, List<Op> Ops, List<MenuButton> Buttons)
{
    /// <summary>What the editor lets the user change by clicking on this page. Nothing here goes on the disc.</summary>
    public List<MenuZone> Zones { get; init; } = [];
}

/// <summary>
/// A place of the page the user can edit: "title", "poster", "name:N" (the video of playlist N), or "slot:KEY" (a text zone the template declares, see <see cref="MenuDesign.TextZones"/>).
/// Each template offers only what it draws.
/// </summary>
public sealed record MenuZone(string Key, Box Rect);

/// <summary>One video of a multi-video disc (an episode). <paramref name="Overview"/>: its summary (see <see cref="DiscTitle.Overview"/>).</summary>
public sealed record TitleEntry(string Name, int Playlist, string? Picture, string? Overview = null);

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

    internal sealed record Cell(string Id, string Label, string? Picture, MenuAction Action, string? Overview = null);

    internal static List<Cell> Cells(MenuDesign d, MenuContent c) => c.Series
        ? c.Titles!.Select((t, i) => new Cell($"e{i + 1}", t.Name, t.Picture, new MenuAction("play", Playlist: t.Playlist), t.Overview)).Skip(d.SkipFirstScene ? 1 : 0).ToList()
        : c.ChapterPictures.Select((p, i) => new Cell($"c{i + 1}", $"{d.ChapterLabel} {i + 1}", p, new MenuAction("chapter", Playlist: c.Playlist, Mark: i))).ToList();

    /// <summary>The Bonus page's buttons: each plays its video on its own and the disc then comes back to the menu.</summary>
    internal static List<Cell> BonusCells(MenuContent c) =>
        (c.Bonus ?? []).Select((t, i) => new Cell($"b{i + 1}", t.Name, t.Picture, new MenuAction("play", Playlist: t.Playlist))).ToList();

    /// <summary>Pages needed for <paramref name="count"/> buttons, <paramref name="perPage"/> on each.</summary>
    internal static int PageCount(int count, int perPage) => (count + perPage - 1) / perPage;

    /// <summary>The installed template of a design (it can be gone: uninstalled or deleted).</summary>
    static InstalledTemplate? Pack(MenuDesign d) => d.TemplateDir == null ? null
        : InstalledTemplate.Load(d.TemplateDir) ?? throw new FileNotFoundException($"Modèle introuvable : {d.TemplateDir} (dossier déplacé ou désinstallé ?)");

    /// <summary>Distance of the "active track" dot from the left edge of its button, as written to the BD-J menu file.</summary>
    public static int MarkerOffset(MenuDesign d) => Pack(d) is { HasSetupLists: true } p ? p.MarkerOffset : FocusMargin - MarkerSize - MarkerGap;

    /// <summary>Les pages de réglages de ce disque, celle où mène le bouton « Réglages » d'abord : une seule, <c>settings</c>, sauf pour un modèle à liste unique.</summary>
    public static List<string> SettingsIds(MenuDesign d, MenuContent c) => !c.HasSettings ? [] : Pack(d)?.SettingsIds(c) ?? ["settings"];

    /// <summary>Le message à donner quand des pistes du disque n'ont pas de ligne au menu (elles restent sur le disque, on ne peut seulement pas les y choisir) ; null quand toutes en ont une.</summary>
    public static string? TracksLeftOut(IEnumerable<MenuPage> pages, MenuContent c)
    {
        var buttons = pages.SelectMany(p => p.Buttons).ToList();
        int Rows(string kind) => buttons.Where(b => b.Action.Kind == kind && b.Action.Mark > 0).Select(b => b.Action.Mark).Distinct().Count();
        var (audio, subs) = (c.Audio?.Count ?? 0, c.Subtitles?.Count ?? 0);
        var missing = new List<string>();
        if (audio > 1 && Rows("audio") < audio) missing.Add($"{Rows("audio")} pistes audio sur {audio}");
        if (Rows("sub") < subs) missing.Add($"{Rows("sub")} sous-titres sur {subs}");
        return missing.Count == 0 ? null : $"attention : le menu ne propose que {string.Join(" et ", missing)} ; les autres restent sur le disque, sans ligne pour les choisir";
    }

    public static List<MenuPage> Build(MenuDesign d, MenuContent c)
    {
        var pages = BuildCore(d, c);
        if (d.Elements.Count == 0) return pages;
        pages = pages.Select(p => WithElements(d, p, c)).ToList();
        // a hand-placed button to a page this disc does not have (Scènes on a one-page menu, Bonus without extras, a mistyped name) leads to the
        // root page: left as it is, the button does nothing, or the name breaks the menu file the player reads
        var ids = pages.Select(p => p.Id).ToHashSet();
        return pages.Select(p => p.Buttons.All(b => b.Action.Kind != "page" || ids.Contains(b.Action.Page ?? "")) ? p
            : p with { Buttons = [.. p.Buttons.Select(b => b.Action.Kind == "page" && !ids.Contains(b.Action.Page ?? "") ? b with { Action = b.Action with { Page = "root" } } : b)] }).ToList();
    }

    /// <summary>
    /// The page with the design's free elements drawn over it, and its hand-placed buttons registered as real buttons. Call it on every page built
    /// outside <see cref="Build"/> (the audio / subtitle pages a DVD makes per choice), or the elements would be missing there.
    /// </summary>
    public static MenuPage WithElements(MenuDesign d, MenuPage page, MenuContent c)
    {
        if (d.Elements.Count == 0) return page;
        var mine = d.Elements.Where(e => e.AppliesTo(page.Id)).ToList();
        if (mine.Count == 0) return page;
        var behind = mine.Where(e => e.Behind && !e.IsButton).SelectMany(e => e.Ops(d)).ToList();
        var front = mine.Where(e => !e.Behind || e.IsButton).SelectMany(e => e.Ops(d)).ToList();
        var ops = page.Ops.ToList();
        // a template's own artwork is the page's first op: a "behind" element goes over it, under the buttons drawn after it
        var at = 0;
        while (at < ops.Count && ops[at] is Image { Backdrop: true }) at++;
        ops.InsertRange(at, behind);
        ops.AddRange(front);

        var buttons = page.Buttons.ToList();
        foreach (var e in mine.Where(x => x.IsButton))
        {
            var id = "u" + d.Elements.IndexOf(e); // stable across pages: the same element is the same button wherever it is drawn
            if (buttons.All(b => b.Id != id))
            {
                // a picture button is selected with its own "sub" picture: on a Blu-ray menu it is the focus image, on a DVD the highlight a subpicture is made from
                var focus = ElementLibrary.Resolve(e.Focus) is { } f && File.Exists(f) && ElementLibrary.Resolve(e.Image) is { } art && File.Exists(art) ? f : null;
                buttons.Add(new MenuButton(id, e.Box(), Action(d, e, c)) { FocusImage = focus, DvdHighlightImage = focus });
            }
        }
        var result = page with { Ops = ops, Buttons = buttons };
        return mine.Any(x => x.IsButton) ? Relink(result) : result;
    }

    /// <summary>What a hand-placed button does: the film, every title in turn, a page, or a chapter of the film.</summary>
    static MenuAction Action(MenuDesign d, MenuElement e, MenuContent c)
    {
        var target = e.Target.Trim().ToLowerInvariant();
        MenuAction Film() => c.Series
            ? new MenuAction("playall", Playlist: c.Titles![0].Playlist, Mark: c.Titles.Count)
            : new MenuAction("play", Playlist: c.Playlist);
        if (target is "movie" or "play" or "all" or "playall" or "") return Film();
        if (target.StartsWith("chapter", StringComparison.Ordinal))
            return new MenuAction("chapter", Playlist: c.Playlist, Mark: Math.Max(0, (int.TryParse(target[7..].TrimStart(':'), out var n) ? n : 1) - 1));
        return new MenuAction("page", target == "settings" && SettingsIds(d, c) is [var entry, ..] ? entry : target);
    }

    /// <summary>
    /// Neighbours by geometry, for a page carrying hand-placed buttons: each button points at the nearest one in each direction, so the remote
    /// reaches everything whatever order they were added in. Only those pages are re-linked; a template keeps the navigation its own layout wrote.
    /// </summary>
    static MenuPage Relink(MenuPage page)
    {
        var list = page.Buttons;
        (int X, int Y) Centre(MenuButton b) => (b.Rect.X + b.Rect.W / 2, b.Rect.Y + b.Rect.H / 2);
        MenuButton? Near(MenuButton b, int dx, int dy)
        {
            var (x, y) = Centre(b);
            MenuButton? best = null;
            long score = long.MaxValue;
            foreach (var o in list)
            {
                if (ReferenceEquals(o, b)) continue;
                var (ox, oy) = Centre(o);
                int ax = ox - x, ay = oy - y;
                if (dx != 0 && Math.Sign(ax) != dx) continue;
                if (dy != 0 && Math.Sign(ay) != dy) continue;
                var along = dx != 0 ? Math.Abs(ax) : Math.Abs(ay);
                var across = dx != 0 ? Math.Abs(ay) : Math.Abs(ax);
                var s = along + 3L * across;
                if (s < score) { score = s; best = o; }
            }
            return best;
        }
        for (var i = 0; i < list.Count; i++)
        {
            var b = list[i];
            list[i] = b with { Left = Near(b, -1, 0)?.Id, Right = Near(b, 1, 0)?.Id, Up = Near(b, 0, -1)?.Id, Down = Near(b, 0, 1)?.Id };
        }
        return page with { Buttons = list };
    }

    static List<MenuPage> BuildCore(MenuDesign d, MenuContent c)
    {
        if (Pack(d) is { } pack) return pack.Pages(d, c);
        var cells = Cells(d, c);
        List<MenuPage> pages = d.ScenesOnly && cells.Count > 1 ? [] : [Root(d, c, cells.Count > 1)]; // scenes only: the first scene page is the menu
        var gridPages = cells.Count > 1 ? (cells.Count + PerPage(d) - 1) / PerPage(d) : 0;
        for (var i = 1; i <= gridPages; i++) pages.Add(Grid(d, c, cells, i, gridPages));
        if (c.HasBonus)
        {
            var bonus = BonusCells(c);
            var bonusPages = PageCount(bonus.Count, PerPage(d));
            var look = d.BonusText ? d.Clone() : d;
            look.Thumbnails = !d.BonusText && d.Thumbnails;
            for (var i = 1; i <= bonusPages; i++) pages.Add(Grid(look, c, bonus, i, bonusPages, "bonus", d.BonusLabel));
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
            case "Text" or "Metal": return []; // Metal: no card, the letters themselves are drawn as steel (MenuRenderer.DrawLabel)
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
        var zones = new List<MenuZone> { new("title", new Box(0, 70, W, 140)) };
        if (d.ShowPreview && d.PosterOr(c.PreviewPicture) is { } preview)
        {
            ops.Add(new Picture(preview, new Box(560, 250, 800, 450), 24, 4));
            zones.Add(new("poster", new Box(560, 250, 800, 450)));
        }

        var entries = RootEntries(d, c, withGrid);

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
        return new MenuPage("root", ops, buttons) { Zones = zones };
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

    /// <summary>The main menu's buttons: play (all), the scene grid, bonus, settings, credits; the grid one only when it is <paramref name="withGrid"/>.</summary>
    internal static List<(string Id, string Text, MenuAction Action)> RootEntries(MenuDesign d, MenuContent c, bool withGrid)
    {
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
        return entries;
    }

    /// <summary>The id of scene page <paramref name="number"/>: with <see cref="MenuDesign.ScenesOnly"/> the first one is the menu itself.</summary>
    static string GridId(MenuDesign d, string prefix, int number) => number == 1 && prefix == "scenes" && d.ScenesOnly ? "root" : $"{prefix}{number}";

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
        if (number > 1) nav.Add(("prev", new Box(x0, navY, navW, navH), new MenuAction("page", GridId(d, prefix, number - 1)), "<"));
        // the page that is the menu (scenes only) carries the menu's own buttons between the arrows, the others a way back to it
        var center = GridId(d, prefix, number) == "root" ? RootEntries(d, c, false) : [("home", d.HomeLabel, new MenuAction("page", "root"))];
        var between = W - 2 * (x0 + navW + 20);
        var centerW = Math.Min(300, (between - 20 * (center.Count - 1)) / center.Count);
        var centerX = (W - (centerW * center.Count + 20 * (center.Count - 1))) / 2;
        for (var k = 0; k < center.Count; k++) nav.Add((center[k].Id, new Box(centerX + k * (centerW + 20), navY, centerW, navH), center[k].Action, center[k].Text));
        if (number < total) nav.Add(("next", new Box(W - x0 - navW, navY, navW, navH), new MenuAction("page", $"{prefix}{number + 1}"), ">"));

        var zones = new List<MenuZone>();
        var rows = (count + cols - 1) / cols;
        for (var i = 0; i < count; i++)
        {
            var r = slots[i];
            var cell = cells[first + i];
            void Named(Box at) { if (cell.Action.Kind == "play") zones.Add(new($"name:{cell.Action.Playlist}", at)); }
            if (original && d.Thumbnails)
            {
                if (cell.Picture is { } pic) ops.Add(new Picture(pic, r, 12, 0));
                else ops.Add(new Fill(r, "#000000", 0.4, 12));
                ops.Add(new Label(cell.Label, new Box(r.X, r.Y + r.H + 6, r.W, 44), 34, false));
                Named(new Box(r.X, r.Y + r.H + 6, r.W, 44));
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
                Named(new Box(r.X, r.Y + r.H - labelH - pad / 2, r.W, labelH));
            }
            else
            {
                ops.AddRange(Button(d, r, cell.Label, Math.Clamp(r.H / 3, 26, 54)));
                Named(r);
            }
            var row = i / cols;
            string? Id(int n) => n >= 0 && n < count ? cells[first + n].Id : null;
            buttons.Add(new MenuButton(cell.Id, r, cell.Action)
            {
                Left = i % cols > 0 ? Id(i - 1) : null,
                Right = i % cols < cols - 1 ? Id(i + 1) : null,
                Up = row > 0 ? Id(i - cols) : null,
                Down = row < rows - 1 && i + cols < count ? Id(i + cols) : center[0].Id,
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
        return new MenuPage(GridId(d, prefix, number), ops, buttons) { Zones = zones };
    }

    /// <summary>
    /// The design the pop-up pages are drawn with: BurnSmith's own layout in the design's font, colours and button style, never a template's
    /// artwork nor the page veil (the pages are transparent overlays on the film).
    /// </summary>
    public static MenuDesign PopupDesign(MenuDesign d)
    {
        var g = d.Clone();
        g.TemplateDir = null;
        g.Dim = 0;
        g.Elements = [];
        g.PageBackgrounds = [];
        g.Cols = 4;
        g.Rows = 3;
        g.Thumbnails = false;
        g.MarginX = 0;
        g.HomeLabel = d.BackLabel; // the chapter pages' middle button goes back to the bar
        return g;
    }

    /// <summary>
    /// The Blu-ray pop-up menu: per title (playlist, chapter count) a bar "pop{playlist}" over the bottom of the film (resume, chapters, settings,
    /// main menu), its chapter pages "popc{playlist}_N", and one "popset" page for the audio / subtitle choice. The xlet opens "pop" + the playlist that
    /// plays; the action "popup" goes back to that bar.
    /// </summary>
    public static List<MenuPage> Popup(MenuDesign d, MenuContent c, IReadOnlyList<(int Playlist, int Chapters)> titles)
    {
        var g = PopupDesign(d);
        var veil = new Fill(new Box(0, 0, W, H), "#000000", 0.6, 0);
        // the pages the pop-up reuses lead back to the bar, not to the disc's root page
        MenuPage Popped(MenuPage p, string id) => p with
        {
            Id = id,
            Ops = [veil, .. p.Ops],
            Buttons = p.Buttons.Select(b => b.Action is { Kind: "page", Page: "root" } ? b with { Action = new MenuAction("popup") } : b).ToList(),
        };
        var pages = new List<MenuPage>();
        foreach (var (playlist, chapters) in titles)
        {
            var entries = new List<(string Id, string Text, MenuAction Action)> { ("resume", d.ResumeLabel, new MenuAction("resume")) };
            if (chapters > 1) entries.Add(("chapters", d.ScenesLabel, new MenuAction("page", $"popc{playlist}_1")));
            if (c.HasSettings) entries.Add(("settings", d.SettingsLabel, new MenuAction("page", "popset")));
            entries.Add(("main", d.MainMenuLabel, new MenuAction("menu")));
            const int bw = 380, gap = 50;
            var n = entries.Count;
            var x0 = (W - (n * bw + (n - 1) * gap)) / 2;
            var ops = new List<Op> { new Fill(new Box(0, 820, W, H - 820), "#000000", 0.65, 0) };
            var buttons = new List<MenuButton>();
            for (var k = 0; k < n; k++)
            {
                var box = new Box(x0 + k * (bw + gap), 890, bw, 100);
                ops.AddRange(Button(g, box, entries[k].Text, 46));
                buttons.Add(new MenuButton(entries[k].Id, box, entries[k].Action)
                {
                    Left = n > 1 ? entries[(k + n - 1) % n].Id : null,
                    Right = n > 1 ? entries[(k + 1) % n].Id : null,
                });
            }
            pages.Add(new MenuPage($"pop{playlist}", ops, buttons));
            if (chapters <= 1) continue;
            var cells = Enumerable.Range(0, chapters).Select(i => new Cell($"c{i + 1}", $"{d.ChapterLabel} {i + 1}", null, new MenuAction("chapter", Playlist: playlist, Mark: i))).ToList();
            var total = PageCount(cells.Count, PerPage(g));
            for (var i = 1; i <= total; i++) pages.Add(Popped(Grid(g, c, cells, i, total, $"popc{playlist}_", d.ScenesLabel), $"popc{playlist}_{i}"));
        }
        if (c.HasSettings) pages.Add(Popped(Settings(g, c, null, null), "popset"));
        return pages;
    }

    /// <summary>
    /// Audio and subtitle choice, one column each. With <paramref name="activeAudio"/> / <paramref name="activeSub"/> null no
    /// marker is drawn (the BD-J menu draws it live); a DVD has no live drawing, so it uses one page per choice.
    /// </summary>
    public static MenuPage Settings(MenuDesign d, MenuContent c, int? activeAudio, int? activeSub, string id = "settings")
    {
        var pack = Pack(d);
        if (pack is { HasSetupLists: true } or { HasSetupPages: true }) return pack.Settings(d, c, activeAudio, activeSub, id);
        const int colW = 680, rowH = 84, pitch = 100, top = 300;
        var audio = c.AudioNames.Count > 1 ? c.AudioNames : [];
        var subs = c.SubtitleNames;
        var ops = pack?.SetupBackdrop(c.PreviewPicture) ?? []; // a template without audio / subtitle rows: its background, BurnSmith's rows
        // a template that places the page title or its Back button (a theme's ASLangTitleText / Return) has them used where it says
        ops.Add(new Label(d.SettingsLabel, pack?.SetupTitle ?? new Box(0, 60, W, 120), 80, true));
        var buttons = new List<MenuButton>();
        var both = audio.Count > 0 && subs.Count > 0;
        int AudioX() => both ? 200 : (W - colW) / 2;
        int SubX() => both ? W - 200 - colW : (W - colW) / 2;

        List<string> Column(string header, int x, IReadOnlyList<string> names, int firstNumber, string idPrefix, string kind, int? active)
        {
            ops.Add(new Label(header, new Box(x, 200, colW, 70), 46, true)); // centred like the rows under it
            var ids = new List<string>();
            // rows as wide as the column's longest name: the active-track dot, left of the row, then sits by the words, not 200 px away
            var w = Math.Min(colW, (int)Math.Ceiling(names.Max(n => MenuRenderer.TextWidth(d, n, 44, true))) + 40);
            for (var i = 0; i < names.Count; i++)
            {
                var number = firstNumber + i;
                var box = new Box(x + (colW - w) / 2, top + i * pitch, w, rowH);
                ops.AddRange(Button(d, box, names[i], 44));
                if (active == number) ops.Add(new Fill(new Box(box.X - MarkerSize - MarkerGap, box.Y + (rowH - MarkerSize) / 2, MarkerSize, MarkerSize), d.Accent, 1, MarkerSize / 2));
                var bid = idPrefix + number;
                ids.Add(bid);
                buttons.Add(new MenuButton(bid, box, new MenuAction(kind, Mark: number)));
            }
            return ids;
        }

        var left = audio.Count > 0 ? Column(d.AudioLabel, AudioX(), audio, 1, "a", "audio", activeAudio) : [];
        var subNames = subs.Count > 0 ? new[] { d.OffLabel }.Concat(subs).ToList() : [];
        var right = subs.Count > 0 ? Column(d.SubtitlesLabel, SubX(), subNames, 0, "s", "sub", activeSub) : [];

        var back = pack?.SetupBack ?? new Box((W - 300) / 2, 950, 300, 90);
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
