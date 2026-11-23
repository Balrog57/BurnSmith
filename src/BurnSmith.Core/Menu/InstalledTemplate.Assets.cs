using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BurnSmith.Core.Menu;

public sealed partial class InstalledTemplate
{
    /// <summary>The template has rows for the audio / subtitle choice (the UHD ones lay that page out differently).</summary>
    /// <summary>The page title the template places on its audio / subtitle page (<c>ASLangTitleText</c>), null when it has none.</summary>
    public Box? SetupTitle => template.Child("setup")?.Child("setuptitle") is { } n ? n.Rect() : null;

    /// <summary>The Back button the template places on that page (<c>Return</c>), null when it has none.</summary>
    public Box? SetupBack => template.Child("setup")?.Child("Return") is { } n ? n.Rect() : null;

    public bool HasSetupLists => (template.Child("setup")?.Child("audiolist")?.Children("title").Any() ?? false) || (template.Child("setup")?.Child("subtitlelist")?.Children("title").Any() ?? false);

    /// <summary>
    /// Le modèle décrit une seule liste de langues (<c>LangItemText</c>) : une page par catégorie au lieu d'une page à deux colonnes.
    /// C'est le seul interrupteur de ce mode : <see cref="SettingsIds"/>, donc les disques, l'aperçu et l'éditeur, en découlent.
    /// </summary>
    public bool HasSetupPages => template.Child("setup")?.Child("langitems")?.Children("item").Any() ?? false;

    /// <summary>Les pages de réglages de ce disque, celle où mène le bouton « Réglages » d'abord : une catégorie sans choix n'a pas de page.</summary>
    /// <summary>
    /// A theme's Setup button opens a page of two words, Audio and Subtitles, each leading to its list; with one of the two only, the list opens
    /// at once. That page takes the id "settings": it marks no choice, so it is not one of <see cref="SettingsIds"/>.
    /// </summary>
    bool HasSetupMenu(MenuContent c) => HasSetupPages && SettingsIds(c).Count > 1 && template.Child("setup") is { } s && s.Child("setupaudio") != null && s.Child("setupsub") != null;
    /// <summary>The page a Settings button opens.</summary>
    public string SettingsEntry(MenuContent c) => HasSetupMenu(c) ? "settings" : SettingsIds(c)[0];

    public List<string> SettingsIds(MenuContent c) => !HasSetupPages ? ["settings"]
        : [.. c.AudioNames.Count > 1 ? new[] { "settings_audio" } : [], .. c.SubtitleNames.Count > 0 ? new[] { "settings_sub" } : []];

    /// <summary>Artwork of the audio / subtitle page for BurnSmith's own rows: the template's background under a dark veil.</summary>
    public List<Op> SetupBackdrop(MenuDesign d, string? film = null)
    {
        var ops = new List<Op>();
        Background(ops, template.Child("setup") ?? template.Child("rootmenu")!, FilmBehind ? film : null, d);
        ops.Add(new Fill(new Box(0, 0, MenuBuilder.W, MenuBuilder.H), "#000000", 0.45, 0));
        return ops;
    }

    /// <summary>The folder, beside a template's button pictures, of their French remakes: <c>LANG_FRA\play\0.normal.png</c> for <c>play\0.normal.png</c>.</summary>
    public const string FrenchDir = "LANG_FRA";

    /// <summary>A button picture of the template; its French remake when the design asks for French and the template has one (<see cref="MenuDesign.ButtonLanguage"/>).</summary>
    string? Button(string key, MenuDesign d) =>
        common.Child(key)?.Text is { Length: > 0 } relative && d.ButtonLanguage == "fr" && French(dir, relative) is { } french && IsImage(french) ? french : Asset(common.Child(key)?.Text);

    /// <summary>The French remake of a button picture of the template in <paramref name="dir"/>, as a picture or as its traced drawing (a pack keeps one of the two).</summary>
    public static string? French(string dir, string relative) =>
        new[] { relative, System.IO.Path.ChangeExtension(relative, ".svg"), System.IO.Path.ChangeExtension(relative, ".png") }
            .Select(r => TemplatePack.Inside(dir, FrenchDir + "/" + r)).FirstOrDefault(f => f != null && File.Exists(f));

    // ---- text -------------------------------------------------------------

    Text Txt(XNode n, string value, string align = "left")
    {
        var font = n.Attr("font", common.Child("font")?.Text ?? "Segoe UI");
        var hasFiles = Directory.EnumerateFiles(dir, "*.*").Any(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
        var inv = System.Globalization.CultureInfo.InvariantCulture; // "28.5" is the same size on a French PC
        var fontSize = double.TryParse(n.Attr("font_size", common.Child("font_size")?.Text ?? "32"), System.Globalization.NumberStyles.Float, inv, out var size) ? size : 32;
        if (n.Attr("fit") == "1" && value.Length > 0) fontSize = Math.Min(fontSize, n.Rect().W / (value.Length * 0.6)); // a text that overflows its box shrinks
        return new Text(value, n.Rect(), fontSize,
            font, hasFiles ? dir : null, n.Attr("color", common.Child("font_color")?.Text ?? "#FFFFFF"),
            n.Attr("bold", common.Child("font_bold")?.Text ?? "0") == "1", n.Attr("italic", common.Child("font_italic")?.Text ?? "0") == "1", align,
            n.Attrs.ContainsKey("stroke_color") ? n.Attr("stroke_color") : null, double.TryParse(n.Attr("stroke_width"), System.Globalization.NumberStyles.Float, inv, out var sw) ? sw : 0,
            Shadow: n.Attrs.ContainsKey("shadow") ? n.Attr("shadow") : null, ShadowOffset: double.TryParse(n.Attr("shadow_offset"), System.Globalization.NumberStyles.Float, inv, out var so) ? so : 0,
            Tight: n.Attr("tight") == "1");
    }

    // ---- shared page parts --------------------------------------------------

    /// <summary>
    /// The text zones this page of the template declares (the user writes in them, and an empty one draws nothing).
    /// <paramref name="autoInfo"/> stands in for an empty information line. A template that declares none has none.
    /// </summary>
    /// <param name="own">How many of the template's summary zones stay the user's to write in; the others are taken by the episodes' summaries.</param>
    void TemplateTexts(List<Op> ops, List<MenuZone> zones, XNode page, MenuDesign d, string? autoInfo = null, int own = int.MaxValue)
    {
        var overview = 0;
        foreach (var t in page.Child("texts")?.Children("text") ?? [])
        {
            var kind = t.Attr("as");
            if (kind == "volume_name" && page.Child("label") == null)
            {
                ops.Add(Txt(t, d.Title));
                zones.Add(new("title", t.Rect()));
            }
            if (kind != "volume_category" && (kind != "title_overview" || overview >= own)) continue;
            var key = kind == "volume_category" ? "info" : $"overview{++overview}";
            zones.Add(new("slot:" + key, t.Rect()));
            var value = d.TextZones.GetValueOrDefault(key) is { Length: > 0 } written ? written : key == "info" ? autoInfo : null;
            if (value == null) continue;
            // "Director: …" is written as the template's program writes it: the word in grey bold, what follows in the zone's own colour
            if (key != "info" && t.Attr("wrap") != "1" && value.IndexOf(": ", StringComparison.Ordinal) is > 0 and var colon)
            {
                var word = Line(t, value[..(colon + 1)]) with { Color = Faint, Bold = true };
                var wide = (int)Math.Ceiling(MenuRenderer.TextWidth(word)) + (int)(word.Size * 0.4);
                ops.Add(word);
                var said = Line(t, value[(colon + 2)..]);
                ops.Add(said with { Rect = said.Rect with { X = said.Rect.X + wide, W = Math.Max(1, said.Rect.W - wide) } });
            }
            else ops.Add(t.Attr("wrap") == "1" ? Txt(t, value) with { Wrap = true } : Line(t, value));
        }
    }

    /// <summary>One line of a zone the template writes from its top ("AlignTop"): the line sits at the top of the zone, not in its middle (6 px under it for a 34 px text, measured on the program's own page).</summary>
    Text Line(XNode zone, string value) => Txt(zone, value) is var text && zone.Attr("vertical_alignment") == "AlignTop" ? text with { Rect = text.Rect with { H = (int)(text.Size * 1.3) } } : Txt(zone, value);

    /// <summary>The grey of the words before a value ("Overview:") and of the names under the thumbnails in the Streaming template's own pages.</summary>
    const string Faint = "#868686";

    /// <param name="film">
    /// A picture of the disc's own film, for the templates whose background is a frame of some film (a template can ship one as a sample and plays
    /// the real film there): it replaces the template's picture, the template's veil stays over it.
    /// </param>
    void Background(List<Op> ops, XNode page, string? film = null, MenuDesign? d = null)
    {
        if (film != null && File.Exists(film)) ops.Add(new Image(film, new Box(0, 0, MenuBuilder.W, MenuBuilder.H), Backdrop: true, Cover: true));
        else if (d?.OwnBackdrop != true && Asset(page.Child("bg")?.Child("image")?.Text) is { } bg) // a design with its own backdrop leaves the template's sample picture out
        {
            var node = page.Child("bg")!;
            var box = node.Attrs.ContainsKey("rect") ? node.Rect() : new Box(0, 0, MenuBuilder.W, MenuBuilder.H);
            ops.Add(new Image(bg, node.Attr("fit") is { Length: > 0 } fit ? Fit(bg, box, fit) : box, Backdrop: true));
        }
        var cover = page.Child("bg")?.Child("covering_color")?.Text; // #AARRGGBB shade between the artwork and the thumbnails
        // a value that is not a colour draws no shade, rather than failing every page of the template
        if (cover is ['#', ..] and { Length: 9 } && uint.TryParse(cover.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var argb))
            ops.Add(new Fill(new Box(0, 0, MenuBuilder.W, MenuBuilder.H), "#" + cover[^6..], (argb >> 24) / 255.0, 0));
    }

    /// <summary>The picture inside <paramref name="box"/> with its own proportions (SVG "meet"), aligned as the template says (xminymax, xmidymid...).</summary>
    static Box Fit(string file, Box box, string align)
    {
        try
        {
            if (MenuRenderer.Size(file) is not var (pw, ph)) return box;
            var k = Math.Min(box.W / (double)pw, box.H / (double)ph);
            double w = pw * k, h = ph * k;
            double ax = align.Contains("xmin") ? 0 : align.Contains("xmax") ? 1 : 0.5, ay = align.Contains("ymin") ? 0 : align.Contains("ymax") ? 1 : 0.5;
            return new Box((int)(box.X + (box.W - w) * ax), (int)(box.Y + (box.H - h) * ay), (int)w, (int)h);
        }
        catch (Exception e) when (e is NotSupportedException or System.IO.FileFormatException or IOException) { return box; }
    }

    /// <param name="panelIfMissing">Some templates name a panel picture they do not ship (Birthday1's setup page): a dark translucent panel keeps the text readable.</param>
    void Pictures(List<Op> ops, XNode page, Func<Box, bool>? skip = null, bool panelIfMissing = false)
    {
        foreach (var pic in page.Child("pics")?.Children("pic") ?? [])
        {
            if (skip?.Invoke(pic.Rect()) == true) continue;
            if (Asset(pic.Attr("image")) is { } file) ops.Add(new Image(file, pic.Rect()));
            else if (panelIfMissing) ops.Add(new Fill(pic.Rect(), "#000000", 0.4, 24));
        }
    }

    /// <summary>Some templates draw their picture frame first and the preview inside it (others put the frame on top of the picture).</summary>
    void Under(List<Op> ops, XNode video)
    {
        if (Asset(video.Attr("frame_under")) is { } file) ops.Add(new Image(file, video.Rect("under_rect")));
        if (Drawing(video, "svg_under") is { } art) ops.Add(new Image(art, video.Rect("over_rect")));
    }

    /// <summary>The frame of a template that draws it after the picture, larger than the picture: its artwork overlaps the picture's edge.</summary>
    void Over(List<Op> ops, XNode video)
    {
        if (Asset(video.Attr("frame_over")) is { } file) ops.Add(new Image(file, video.Rect("over_rect")));
        if (Drawing(video, "svg_over") is { } art) ops.Add(new Image(art, video.Rect("over_rect")));
    }

    /// <summary>A drawing the template carries as SVG text (a .dvdt keeps each button's own): read as written, entities included, since it is XML.</summary>
    static string? Drawing(XNode node, string name) => node.Attrs.TryGetValue(name, out var svg) && MenuRenderer.IsSvgText(svg) ? svg : null;

    /// <summary>
    /// A row with its own picture beside its words ("icon", at "icon_rect"; "{n}" in the name is the row's number: a numbered disc before
    /// each episode, a dot before each language): the picture, and a button that lights the picture's chosen twin there. Null without one.
    /// A row of the Bonus pages takes "bonus_icon" when the template has one: an extra is not episode 3.
    /// </summary>
    MenuButton? Iconed(List<Op> ops, XNode node, Box r, int number, string id, MenuAction action, bool bonus = false)
    {
        if (Asset(node.Attr(bonus && node.Attr("bonus_icon") != "" ? "bonus_icon" : "icon").Replace("{n}", number.ToString(System.Globalization.CultureInfo.InvariantCulture))) is not { } file) return null;
        var slot = node.Rect("icon_rect");
        ops.Add(new Image(file, slot));
        int x0 = Math.Min(r.X, slot.X), y0 = Math.Min(r.Y, slot.Y);
        return Dvd(new MenuButton(id, new Box(x0, y0, Math.Max(r.X + r.W, slot.X + slot.W) - x0, Math.Max(r.Y + r.H, slot.Y + slot.H) - y0), action) { FocusImage = Selected(file), FocusSlot = slot });
    }

    /// <summary>
    /// A button whose template draws its own state under the remote ("svg_focus": a circle beside the words, a line under them, the frame in
    /// another colour): that drawing, at the place the template gives it ("focus_rect") or over the button. Null when the template has none.
    /// </summary>
    static MenuButton? Lit(XNode node, Box r, string id, MenuAction action)
    {
        if (Drawing(node, "svg_focus") is not { } lit) return null;
        if (!node.Attrs.ContainsKey("focus_rect")) return new MenuButton(id, r, action) { FocusImage = lit };
        var slot = node.Rect("focus_rect");
        int x0 = Math.Min(r.X, slot.X), y0 = Math.Min(r.Y, slot.Y);
        // the button holds its marker: a DVD player lights nothing outside a button's rectangle
        return new MenuButton(id, new Box(x0, y0, Math.Max(r.X + r.W, slot.X + slot.W) - x0, Math.Max(r.Y + r.H, slot.Y + slot.H) - y0), action) { FocusImage = lit, FocusSlot = slot };
    }

    /// <summary>The lines of a scene page shown as a list: when the user asks for the list, or when the template has no thumbnails at all.</summary>
    static List<XNode> ListItems(MenuDesign d, XNode scene) =>
        d.SceneList || scene.Child("videoes")?.Children("video").Any() != true ? scene.Child("list")?.Children("item").ToList() ?? [] : [];

    Framed Frame(XNode video, string? picture)
    {
        if (Drawing(video, "svg_mask") is { } shape) return new Framed(video.Rect(), picture, null, shape); // the frame is in the drawing put over it
        var frame = Asset(video.Attr("frame_image", common.Child("frame")?.Text ?? ""));
        var mask = frame == null ? null : Asset(System.IO.Path.GetRelativePath(dir, frame).Replace(".normal.", ".mask."));
        return new Framed(video.Rect(), picture, frame, mask == frame || IsBlank(mask) ? null : mask, video.Attr("fit") == "whole");
    }

    static readonly Dictionary<string, bool> Blanks = [];

    /// <summary>A mask without any opaque pixel would hide the whole picture: it is ignored.</summary>
    static bool IsBlank(string? png)
    {
        if (png == null) return false;
        lock (Blanks)
        {
            if (Blanks.TryGetValue(png, out var known)) return known;
            try
            {
                MenuRenderer.CheckSize(png);
                var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(png),
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0],
                    System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var px = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(px, bitmap.PixelWidth * 4, 0);
                return Blanks[png] = !Enumerable.Range(0, px.Length / 4).Any(i => px[i * 4 + 3] > 16);
            }
            catch (Exception e) when (e is NotSupportedException or System.IO.FileFormatException or IOException or InvalidDataException) { return Blanks[png] = false; }
        }
    }

    // ---- pages --------------------------------------------------------------

    public List<MenuPage> Pages(MenuDesign d, MenuContent c)
    {
        var cells = MenuBuilder.Cells(d, c);
        var scene = template.Child("scenes")!.Child("tscenes")!.Child("scene")!;
        // the same page as a list of lines when the design asks for it and the template describes one
        var listItems = ListItems(d, scene);
        var perPage = listItems.Count > 0 ? listItems.Count : scene.Child("videoes")!.Children("video").Count();
        var total = cells.Count > 1 ? (cells.Count + perPage - 1) / perPage : 0;
        // a template with no menu of its own: its first page of thumbnails, with the poster and the title, is the menu
        var pages = new List<MenuPage> { (IsAllInOne || d.ScenesOnly) && total > 0 ? Scenes(d, cells, 1, total, perPage, d.Title, c.PreviewPicture, c, plainFocus: c.PlainFocus) : Root(d, c, cells.Count > 1) };
        for (var i = (IsAllInOne || d.ScenesOnly) && total > 0 ? 2 : 1; i <= total; i++) pages.Add(Scenes(d, cells, i, total, perPage, IsAllInOne ? d.Title : c.Series ? d.EpisodesLabel : d.ScenesLabel, c.PreviewPicture, plainFocus: c.PlainFocus)); // a one-piece template keeps the disc's title on every page
        if (c.HasBonus) // the template has no Bonus page: its scene page layout lists the extras
        {
            var bonus = MenuBuilder.BonusCells(c);
            // text Bonus page: the template's list of lines when it has one, else its thumbnail grid as before
            var bonusList = d.BonusText ? scene.Child("list")?.Children("item").Count() ?? 0 : 0;
            var look = d;
            if (bonusList > 0) { look = d.Clone(); look.SceneList = true; }
            var bonusPerPage = bonusList > 0 ? bonusList : perPage;
            var bonusPages = MenuBuilder.PageCount(bonus.Count, bonusPerPage);
            for (var i = 1; i <= bonusPages; i++) pages.Add(Scenes(look, bonus, i, bonusPages, bonusPerPage, d.BonusLabel, c.PreviewPicture, prefix: "bonus"));
        }
        if (c.HasSettings && HasSetupMenu(c)) pages.Add(MenuBuilder.Settings(d, c, null, null, "settings"));
        if (c.HasSettings) pages.AddRange(SettingsIds(c).Select(id => MenuBuilder.Settings(d, c, null, null, id))); // BurnSmith's own rows when the template has none
        if (d.CreditLines().Count > 0) pages.Add(MenuBuilder.Credits(d, SetupBackdrop(d, c.PreviewPicture))); // the template has no such page: its background under a veil
        return pages;
    }

    bool IsAllInOne => template.Child("rootmenu")?.Attr("allinone") == "1";
    /// <summary>A one-page template made to show the disc's own film behind it (Streaming); one that keeps its own picture says film="0".</summary>
    bool FilmBehind => IsAllInOne && template.Child("rootmenu")?.Attr("film") != "0";

    /// <summary>An episode's name with its number in front, as streaming services show it: "Épisode 3 · Le dojo" → "E03 - Le dojo"; a name already numbered (E03…) is kept.</summary>
    internal static string Numbered(MenuBuilder.Cell cell)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(cell.Label, @"^E\d+\b")) return cell.Label;
        var number = $"E{int.Parse(cell.Id[1..]):00}";
        var m = System.Text.RegularExpressions.Regex.Match(cell.Label, @"^Épisode \d+(?: · (.+))?$"); // DiscTitle.Readable's default name
        return m.Success ? (m.Groups[1].Success ? $"{number} - {m.Groups[1].Value}" : number) : $"{number} - {cell.Label}";
    }

    /// <summary>The page id of page <paramref name="number"/> of a list (scenes, bonus): the first scene page is the root when the template has no menu of its own.</summary>
    string PageId(MenuDesign d, string prefix, int number) => number == 1 && (IsAllInOne || d.ScenesOnly) && prefix == "scenes" ? "root" : $"{prefix}{number}";

    /// <summary>Capsules for the buttons the template has no picture for, in one row from <paramref name="x0"/>: the caller says where the row goes.</summary>
    static void Capsules(List<Op> ops, List<MenuButton> buttons, MenuDesign d, List<(string Id, string Label, MenuAction Action)> pills, int x0, int y, int w, int h, int gap, int size)
    {
        for (var k = 0; k < pills.Count; k++)
        {
            var box = new Box(x0 + k * (w + gap), y, w, h);
            ops.Add(MenuBuilder.IsLight(d.TextColor) ? new Fill(box, "#000000", 0.35, h / 2) : new Fill(box, "#FFFFFF", 0.45, h / 2)); // dark text gets a light capsule
            ops.Add(new Label(pills[k].Label, box, size, true));
            buttons.Add(new MenuButton(pills[k].Id, box, pills[k].Action));
        }
    }

    static readonly Dictionary<string, int> Floors = [];

    /// <summary>
    /// Row of the picture where its solid part ends (a banner has a glow below its body): 1 = the bottom of <paramref name="rect"/>.
    /// Capsules go under that, not over the artwork.
    /// </summary>
    static int SolidBottom(string? png, Box rect)
    {
        if (png == null) return rect.Y + rect.H;
        lock (Floors)
        {
            if (!Floors.TryGetValue(png, out var permille))
            {
                permille = 1000;
                if (Pixels(png) is var (w, h, px))
                {
                    var row = h - 1;
                    while (row > 0 && !Enumerable.Range(0, w).Any(x => px[(row * w + x) * 4 + 3] >= 200)) row--;
                    permille = (row + 1) * 1000 / h;
                }
                Floors[png] = permille;
            }
            return rect.Y + rect.H * permille / 1000;
        }
    }

    /// <summary>A picture's pixels (BGRA, 4 bytes each); null when it cannot be read.</summary>
    static (int W, int H, byte[] Px)? Pixels(string png)
    {
        try
        {
            MenuRenderer.CheckSize(png);
            var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(png),
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0],
                System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            var px = new byte[w * h * 4];
            bitmap.CopyPixels(px, w * 4, 0);
            return (w, h, px);
        }
        catch (Exception e) when (e is NotSupportedException or System.IO.FileFormatException or IOException or InvalidDataException) { return null; }
    }

    static readonly Dictionary<string, (int, int)?> Spans = [];

    /// <summary>
    /// The longest solid stretch (alpha of 200 or more) of a picture along screen row <paramref name="y"/>, as screen columns: the body of a
    /// banner without its glow or its scattered dots. Null when the picture does not reach that row or is clear there.
    /// </summary>
    static (int Left, int Right)? SolidSpan(string? png, Box rect, int y)
    {
        if (png == null || y < rect.Y || y >= rect.Y + rect.H) return null;
        lock (Spans)
        {
            var key = $"{png}|{rect}|{y}";
            if (Spans.TryGetValue(key, out var known)) return known;
            (int, int)? span = null;
            if (Pixels(png) is var (w, h, px))
            {
                var row = (y - rect.Y) * h / rect.H;
                int best = 0, start = 0;
                for (var x = 0; x <= w; x++)
                {
                    if (x < w && px[(row * w + x) * 4 + 3] >= 200) continue;
                    if (x - start > best) (best, span) = (x - start, (rect.X + start * rect.W / w, rect.X + x * rect.W / w));
                    start = x + 1;
                }
            }
            return Spans[key] = span;
        }
    }

    /// <summary>
    /// Where the words of a DVD template's menu go once BurnSmith adds some (Settings, Bonus, Credits) to its Play and Scenes: down the column at
    /// the template's own step or, for words side by side, spread again over the solid part of the banner they are written on, each with the
    /// place of its star on its left. Null when they do not fit: the added ones are capsules then.
    /// </summary>
    List<Box>? WordSlots(XNode n, List<Text> words)
    {
        Box p = n.Child("play")!.Rect(), s = n.Child("scenes")!.Rect();
        if (p.Y != s.Y)
        {
            var column = words.Select((_, k) => new Box(p.X + k * (s.X - p.X), p.Y + k * (s.Y - p.Y), p.W, p.H)).ToList();
            return column[^1].Y >= 0 && column[^1].Y + p.H <= MenuBuilder.H - 20 ? column : null;
        }
        var banner = (n.Child("pics")?.Children("pic") ?? []).Select(pic => SolidSpan(Asset(pic.Attr("image")), pic.Rect(), p.Y + p.H / 2))
            .FirstOrDefault(span => span is var (x0, x1) && x0 <= p.X && p.X < x1);
        if (banner is not var (left, right)) return null;
        var widths = words.Select(w => p.H + (int)Math.Ceiling(MenuRenderer.TextWidth(w))).ToList();
        var gap = (right - left - widths.Sum()) / (words.Count + 1);
        if (gap < p.H / 2) return null;
        var row = new List<Box>();
        for (int k = 0, x = left + gap; k < words.Count; x += widths[k++] + gap) row.Add(new Box(x + p.H, p.Y, widths[k] - p.H, p.H));
        return row;
    }

}
