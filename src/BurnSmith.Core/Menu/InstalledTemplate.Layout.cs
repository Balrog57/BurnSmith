using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BurnSmith.Core.Menu;

public sealed partial class InstalledTemplate
{
    MenuPage Root(MenuDesign d, MenuContent c, bool withScenes)
    {
        var n = template.Child("rootmenu")!;
        var ops = new List<Op>();
        Background(ops, n, FilmBehind ? c.PreviewPicture : null, d);
        Pictures(ops, n);
        var zones = new List<MenuZone>();
        if (n.Child("video") is { } video && d.ShowPreview)
        {
            Under(ops, video);
            ops.Add(Frame(video, d.PosterOr(c.PreviewPicture)));
            Over(ops, video);
            zones.Add(new("poster", video.Rect()));
        }
        if (n.Child("label") is { } label)
        {
            ops.Add(Txt(label, d.Title, label.Attr("align", "left")));
            zones.Add(new("title", label.Rect()));
        }
        TemplateTexts(ops, zones, n, d);

        var buttons = new List<MenuButton>();
        // DVD templates: the label is a word, a star marks the selected one. DVDFab writes it from the left edge of its zone (its own pictures
        // of 03wedding and 04baby show it), the other formats in the middle of it
        Text Written(XNode node, string label) => Txt(node, label, node.Attr("align", Kind == TemplateKind.Dvd ? "left" : "center"));
        void Word(string id, XNode node, Box r, MenuAction action, string label)
        {
            var text = Written(node, label) with { Rect = r };
            // written from one edge, the button is the word and not its whole zone: DVDFab's zones side by side run over each other
            if (text.Align != "center" && Math.Min(r.W, (int)Math.Ceiling(MenuRenderer.TextWidth(text))) is var wide)
                r = new Box(text.Align == "right" ? r.X + r.W - wide : r.X, r.Y, wide, r.H);
            ops.Add(text);
            if (Lit(node, r, id, action) is { } lit) { buttons.Add(lit); return; }
            var star = Asset(common.Child("selection")?.Text);
            var slot = new Box(r.X - r.H, r.Y, r.H, r.H);
            buttons.Add(Dvd(star == null ? new MenuButton(id, r, action) : new MenuButton(id, new Box(slot.X, r.Y, r.W + r.H, r.H), action) { FocusImage = star, FocusSlot = slot }));
        }
        // a row laid out for every button (row="center": Play, Episodes, Languages, Bonus) is centred again when the disc has nothing for one of
        // them: the buttons shown keep the row's spacing, around its middle, instead of leaving a hole at one end
        var moved = new Dictionary<string, int>();
        if (n.Attr("row") == "center")
        {
            double Mid(string k) { var r = n.Child(k)!.Rect(); return r.X + r.W / 2.0; }
            var row = new[] { "play", "scenes", "setup", "bonus" }.Where(k => n.Child(k) != null).OrderBy(Mid).ToList();
            var shown = row.Where(k => k switch { "scenes" => withScenes, "setup" => c.HasSettings, "bonus" => c.HasBonus, _ => true }).ToList();
            if (shown.Count < row.Count && row.Count > 1)
            {
                var pitch = (Mid(row[^1]) - Mid(row[0])) / (row.Count - 1);
                var first = (Mid(row[0]) + Mid(row[^1])) / 2 - pitch * (shown.Count - 1) / 2;
                for (var k = 0; k < shown.Count; k++) moved[shown[k]] = (int)Math.Round(first + k * pitch - Mid(shown[k]));
            }
        }
        void Add(string id, XNode? node, string? image, MenuAction action, string label)
        {
            if (node == null) return;
            var rect = node.Rect();
            if (moved.TryGetValue(node.Name, out var dx)) rect = rect with { X = rect.X + dx };
            if (image != null)
            {
                var box = Place(image, rect);
                ops.Add(new Image(image, box));
                buttons.Add(Dvd(new MenuButton(id, box, action) { FocusImage = Selected(image) }));
            }
            else if (node.Attr("textbutton") == "1") Word(id, node, rect, action, label);
        }
        var main = new List<(string Id, string Label, MenuAction Action)>
        {
            ("play", c.Series ? d.PlayAllLabel : d.PlayLabel,
                c.Series ? new MenuAction("playall", Playlist: c.Titles![0].Playlist, Mark: c.Titles.Count) : new MenuAction("play", Playlist: c.Playlist)),
        };
        if (withScenes) main.Add(("scenes", c.Series ? d.EpisodesLabel : d.ScenesLabel, new MenuAction("page", "scenes1")));
        // a DVDFab DVD menu is two words: the buttons BurnSmith adds are written with them further down, once they are all known
        var words = Kind == TemplateKind.Dvd && Button("playbuttontimage", d) == null && n.Child("play")?.Attr("textbutton") == "1" && n.Child("scenes") != null;
        if (!words)
            foreach (var b in main) Add(b.Id, n.Child(b.Id), Button(b.Id == "play" ? "playbuttontimage" : "scenebuttontimage", d), b.Action, b.Label);
        // "none_setup" templates have no picture for Settings, and none has one for Bonus or Credits: plain capsules in a row under the other buttons, BurnSmith's own addition
        var pills = new List<(string Id, string Label, MenuAction Action)>();
        // a template that draws a Bonus button (<c>BonusText</c>) puts it where it says; the others get BurnSmith's capsule
        if (c.HasBonus)
        {
            // its own picture ("bonusbuttontimage", like the three others) or its word
            if (Button("bonusbuttontimage", d) != null || n.Child("bonus")?.Attr("textbutton") == "1") Add("bonus", n.Child("bonus"), Button("bonusbuttontimage", d), new MenuAction("page", "bonus1"), d.BonusLabel);
            else pills.Add(("bonus", d.BonusLabel, new MenuAction("page", "bonus1")));
        }
        if (c.HasSettings)
        {
            var settings = new MenuAction("page", SettingsEntry(c));
            if (Button("setupbuttontimage", d) != null || n.Child("setup")?.Attr("textbutton") == "1") Add("settings", n.Child("setup"), Button("setupbuttontimage", d), settings, d.SettingsLabel);
            else pills.Add(("settings", d.SettingsLabel, settings));
        }
        if (d.CreditLines().Count > 0) pills.Add(("credits", d.CreditsLabel, new MenuAction("page", "credits")));
        if (words)
        {
            // the added buttons are words like the template's own when they fit beside them (not a dark capsule under a banner); else the template's two stay where it puts them
            XNode Node(int k) => n.Child(k == 0 ? "play" : "scenes")!;
            var all = main.Concat(pills).ToList();
            var slots = pills.Count == 0 ? null : WordSlots(n, all.Select((w, k) => Written(Node(k), w.Label)).ToList());
            if (slots != null) pills.Clear();
            else (all, slots) = (main, [n.Child("play")!.Rect(), n.Child("scenes")!.Rect()]);
            for (var k = 0; k < all.Count; k++) Word(all[k].Id, Node(k), slots[k], all[k].Action, all[k].Label);
        }
        if (pills.Count > 0)
        {
            const int pillW = 320, pillGap = 20;
            var low = buttons.Count == 0 ? MenuBuilder.H / 2 : buttons.Max(b => b.Rect.Y + b.Rect.H);
            foreach (var pic in n.Child("pics")?.Children("pic") ?? []) low = Math.Max(low, SolidBottom(Asset(pic.Attr("image")), pic.Rect())); // under a banner, not over it
            var middle = buttons.Count == 0 ? MenuBuilder.W / 2 : (int)buttons.Average(b => b.Rect.X + b.Rect.W / 2.0);
            var row = pills.Count * pillW + (pills.Count - 1) * pillGap;
            var x0 = Math.Clamp(middle - row / 2, 20, MenuBuilder.W - row - 20);
            Capsules(ops, buttons, d, pills, x0, Math.Min(low + 8, MenuBuilder.H - 70), pillW, 58, pillGap, 36);
        }
        return new MenuPage("root", ops, Link(buttons)) { Zones = zones };
    }

    /// <param name="root">Not null when this page is the menu itself (a template laid out in one piece): it gets the disc's own lines and the Play / Settings / Credits capsules.</param>
    MenuPage Scenes(MenuDesign d, List<MenuBuilder.Cell> cells, int number, int total, int perPage, string title, string? preview, MenuContent? root = null, string prefix = "scenes", bool plainFocus = false)
    {
        var s = template.Child("scenes")!.Child("tscenes")!.Child("scene")!;
        var ops = new List<Op>();
        Background(ops, s, FilmBehind ? preview : null, d);
        Pictures(ops, s);
        var zones = new List<MenuZone>();
        if (s.Child("specialvideo")?.Child("video") is { } poster && d.ShowPreview) // a template laid out in one piece: the poster stays beside the thumbnails
        {
            ops.Add(Frame(poster, d.PosterOr(preview)));
            zones.Add(new("poster", poster.Rect()));
        }
        var buttons = new List<MenuButton>();
        if (s.Child("label") is { } heading)
        {
            if (heading.Attr("counter") == "1" && total > 1) title = $"{title}  {number}/{total}"; // a Yuhan theme writes the page's number after its title
            var text = Txt(heading, title, s.Child("play") != null ? heading.Attr("align", "left") : "left"); // a one-page template's heading is its title, placed as the template places it
            var whole = root == null || s.Child("play") != null; // else the capsules take the right of the title line
            ops.Add(whole ? text : text with { Rect = text.Rect with { W = 700 } });
            if (root != null) zones.Add(new("title", whole ? heading.Rect() : heading.Rect() with { W = 700 }));
        }
        if (root != null)
        {
            var capsules = new List<(string Id, string Label, MenuAction Action)>
            {
                ("play", root.Series ? d.PlayAllLabel : d.PlayLabel, root.Series ? new MenuAction("playall", Playlist: root.Titles![0].Playlist, Mark: root.Titles.Count) : new MenuAction("play", Playlist: root.Playlist))
            };
            if (root.HasBonus) capsules.Add(("bonus", d.BonusLabel, new MenuAction("page", "bonus1")));
            if (root.HasSettings) capsules.Add(("settings", d.SettingsLabel, new MenuAction("page", SettingsEntry(root))));
            if (d.CreditLines().Count > 0) capsules.Add(("credits", d.CreditsLabel, new MenuAction("page", "credits")));
            const int w = 190, gap = 12;
            if (s.Child("play") is { } own) // the template writes its own "Play all" on its one page: BurnSmith's other buttons are written like it, to its left
            {
                var at = own.Rect();
                var x = at.X;
                foreach (var (id, label, action) in capsules)
                {
                    var word = Txt(own, label, own.Attr("align", "left"));
                    var wide = Math.Min(at.W, (int)Math.Ceiling(MenuRenderer.TextWidth(word)));
                    if (id != "play") x -= wide + 48;
                    var r = at with { X = x, W = wide };
                    ops.Add(word with { Rect = r });
                    var node = own;
                    if (own.Attrs.ContainsKey("focus_rect") && x != at.X) // its marker (a circle beside the words) moves with the words
                    {
                        node = new XNode(own.Name, new Dictionary<string, string>(own.Attrs));
                        var slot = own.Rect("focus_rect");
                        node.Attrs["focus_rect"] = $"{slot.X + x - at.X},{slot.Y},{slot.W},{slot.H}";
                    }
                    buttons.Add(Lit(node, r, id, action) ?? new MenuButton(id, r, action));
                }
            }
            else Capsules(ops, buttons, d, capsules, MenuBuilder.W - 60 - capsules.Count * w - (capsules.Count - 1) * gap, 118, w, 56, gap, 28);
        }
        string? autoInfo = null;
        if (root != null) // until the user writes the information line, it says what the disc holds
        {
            var info = new List<string> { $"{cells.Count} {(root.Series ? d.EpisodesLabel : d.ScenesLabel)}" };
            if (root.HasBonus) info.Add($"{root.Bonus!.Count} {d.BonusLabel}");
            if (root.AudioNames.Count > 1) info.Add($"{root.AudioNames.Count} {d.AudioLabel}");
            if (root.SubtitleNames.Count > 0) info.Add($"{root.SubtitleNames.Count} {d.SubtitlesLabel}");
            autoInfo = string.Join("  |  ", info);
        }
        var slots = s.Child("videoes")!.Children("video").ToList();
        var onPage = cells.Skip((number - 1) * perPage).Take(perPage).ToList();
        // a one-piece template's episodes are numbered (E01 - Name), and their summaries take the text zones' place: one row per episode, name over summary
        var episodes = FilmBehind && prefix == "scenes" && onPage.All(cell => cell.Id.StartsWith('e'));
        string Shown(MenuBuilder.Cell cell) => episodes ? Numbered(cell) : cell.Label;
        var summaries = episodes && onPage.Any(cell => cell.Overview is { Length: > 0 });
        // as the template's program shows them: the summary of the episode under the remote, alone, under its word ("Overview:"), in the last two
        // of the template's four zones; the first two stay the user's (a director, a cast). A DVD cannot write with the selected button: there
        // every episode of the page has its row, name over summary
        var overviews = s.Child("texts")?.Children("text").Where(t => t.Attr("as") == "title_overview").ToList() ?? [];
        var spoken = summaries && !plainFocus && overviews.Count >= 2;
        TemplateTexts(ops, zones, s, d, autoInfo, spoken ? overviews.Count - 2 : summaries ? 0 : int.MaxValue);
        if (spoken) ops.Add(Line(overviews[^2], d.OverviewLabel + ":") with { Color = Faint, Bold = true });
        if (summaries && !spoken && overviews.FirstOrDefault() is { } area)
        {
            var top = area.Rect().Y;
            var row = ((slots.Count > 0 ? slots.Min(v => v.Rect().Y) - 12 : MenuBuilder.H - 120) - top) / perPage;
            var grey = slots.FirstOrDefault()?.Child("name") is { } n ? Txt(n, "").Color : "#C8C8C8";
            for (var i = 0; i < onPage.Count; i++)
            {
                var y = top + i * row;
                var name = Txt(area, Shown(onPage[i])) with { Rect = new Box(area.Rect().X, y, area.Rect().W, 32), Size = 26, Bold = true };
                ops.Add(name);
                if (onPage[i].Overview is { Length: > 0 } summary)
                    ops.Add(Txt(area, summary) with { Rect = new Box(area.Rect().X, y + 34, area.Rect().W, row - 40), Size = 21, Bold = false, Color = grey, Wrap = true });
            }
        }
        var lines = ListItems(d, s);
        if (lines.Count > 0)
        {
            // the list style: one line of text per chapter, at the rectangles the template gives, without a frame
            for (var i = 0; i < lines.Count && (number - 1) * perPage + i < cells.Count; i++)
            {
                var cell = cells[(number - 1) * perPage + i];
                ops.Add(Txt(lines[i], Shown(cell), lines[i].Attr("align", "left")));
                if (cell.Action.Kind == "play") zones.Add(new($"name:{cell.Action.Playlist}", lines[i].Rect()));
                // a line that has its own selected drawing (a line under the words) is a button as wide as its words, not as its whole zone
                var line = lines[i].Rect();
                if (lines[i].Attrs.ContainsKey("svg_focus") && lines[i].Attr("align", "left") == "left")
                    line = line with { W = Math.Min(line.W, (int)Math.Ceiling(MenuRenderer.TextWidth(Txt(lines[i], Shown(cell))))) };
                buttons.Add(Lit(lines[i], line, cell.Id, cell.Action) ?? Iconed(ops, lines[i], line, (number - 1) * perPage + i + 1, cell.Id, cell.Action, prefix == "bonus") ?? new MenuButton(cell.Id, line, cell.Action));
            }
        }
        for (var i = 0; i < slots.Count && lines.Count == 0 && (number - 1) * perPage + i < cells.Count; i++)
        {
            var cell = cells[(number - 1) * perPage + i];
            Under(ops, slots[i]);
            var framed = Frame(slots[i], cell.Picture);
            ops.Add(framed);
            Over(ops, slots[i]);
            if (slots[i].Child("name") is { } name)
            {
                ops.Add(Txt(name, Shown(cell), name.Attr("align", "center")));
                if (cell.Action.Kind == "play") zones.Add(new($"name:{cell.Action.Playlist}", name.Rect()));
            }
            var button = Drawing(slots[i], "svg_focus") is { } lit ? new MenuButton(cell.Id, slots[i].Rect(), cell.Action) { FocusImage = lit, FocusSlot = slots[i].Rect("over_rect") }
                : Dvd(new MenuButton(cell.Id, slots[i].Rect(), cell.Action) { FocusImage = Selected(framed.Frame) }); // none for picture themes: the accent frame
            buttons.Add(spoken && cell.Overview is { Length: > 0 } told ? button with { FocusTexts = [Txt(overviews[^1], told) with { Wrap = true }] } : button);
        }
        void Glyph(string id, Box r, string glyph, MenuAction action)
        {
            ops.AddRange(MenuBuilder.Button(d, r, glyph, Math.Max(24, r.H * 2 / 3)));
            buttons.Add(new MenuButton(id, r, action));
        }
        void Nav(string id, string key, string image, string fallbackGlyph, MenuAction action)
        {
            if (s.Child(key) is not { } node) return;
            // a template that carries the drawing of its own arrow, or writes a word there ("Back"): that drawing, and BurnSmith's word in its place and style
            if (Drawing(node, "svg") is var art && (art != null || node.Child("text") != null))
            {
                if (art != null) ops.Add(new Image(art, node.Rect()));
                // the page before and the next one are named by the template's own word ("Previous", "Next"); the way home by the design's, which the user writes
                if (node.Child("text") is { } word) ops.Add(Txt(word, id == "home" ? d.HomeLabel : word.Attr("word") is { Length: > 0 } own ? own : id == "prev" ? "◄" : "►", word.Attr("align", "left")));
                buttons.Add(Lit(node, node.Rect(), id, action) ?? new MenuButton(id, node.Rect(), action));
                return;
            }
            // some templates draw their arrows as vector art, and a template's picture can be missing or unreadable: BurnSmith draws a text button there
            var drawn = node.Attr("glyph");
            if (Button(image, d) is not { } file || drawn.Length > 0) { Glyph(id, node.Rect(), drawn.Length > 0 ? drawn : fallbackGlyph, action); return; }
            // a picture theme draws its arrows at the size its layout gives them (the "16-9-2" pictures are twice as large as their place)
            var box = Kind == TemplateKind.Pictures ? node.Rect() : Place(file, node.Rect());
            ops.Add(new Image(file, box));
            buttons.Add(Dvd(new MenuButton(id, box, action) { FocusImage = Selected(file) }));
        }
        // a Yuhan theme shows both arrows on every page as soon as there are two: the first page goes back to the last, the last on to the first
        var around = s.Attr("wrap") == "1" && total > 1;
        if (number > 1 || around) Nav("prev", "left", "leftbuttontimage", "<", new MenuAction("page", PageId(d, prefix, number > 1 ? number - 1 : total)));
        if (root == null) Nav("home", "home", "homebuttontimage", "⌂", new MenuAction("page", "root"));
        if (number < total || around) Nav("next", "right", "rightbuttontimage", ">", new MenuAction("page", PageId(d, prefix, number < total ? number + 1 : 1)));
        // whatever the template drew, a scene page must lead back to the menu and on to its neighbours: BurnSmith adds the missing buttons at the bottom
        bool Has(string id) => buttons.Any(b => b.Id == id);
        // in the middle of the bottom; on a one-page template, whose own buttons are down there on the right, at the left under its heading
        var mid = s.Child("play") != null && s.Child("label") is { } under ? under.Rect().X + 195 : MenuBuilder.W / 2;
        if (root == null && !Has("home")) Glyph("home", new Box(mid - 55, MenuBuilder.H - 110, 110, 70), "⌂", new MenuAction("page", "root"));
        if (number > 1 && !Has("prev")) Glyph("prev", new Box(mid - 195, MenuBuilder.H - 110, 110, 70), "<", new MenuAction("page", PageId(d, prefix, number - 1)));
        if (number < total && !Has("next")) Glyph("next", new Box(mid + 85, MenuBuilder.H - 110, 110, 70), ">", new MenuAction("page", PageId(d, prefix, number + 1)));
        return new MenuPage(PageId(d, prefix, number), ops, Link(buttons)) { Zones = zones };
    }

    /// <summary>Row width of the audio list: the BD-J menu draws its "active" dot a fixed distance from each row's left edge.</summary>
    public int MarkerOffset => (template.Child("setup")?.Child("audiolist")?.Child("title")?.Rect("frame_rect").W ?? 600) - MenuBuilder.MarkerSize - RowMarkerGap + MenuBuilder.FocusMargin;

    // The marker rectangle supplies its size and its horizontal offset from the button's focus box.
    internal (string Path, Box Bounds)? TrackMarker =>
        common.Child("trackmarker") is { } node && Asset(node.Text) is { } image && node.Rect() is { W: > 0, H: > 0 } bounds
            ? (image, bounds) : null;

    public MenuPage Settings(MenuDesign d, MenuContent c, int? activeAudio, int? activeSub, string id)
    {
        if (HasSetupPages) return SetupPage(d, c, activeAudio, activeSub, id);
        var n = template.Child("setup") ?? throw new InvalidOperationException("this template has no audio / subtitle page");
        var audioRows = n.Child("audiolist")?.Children("title").ToList() ?? [];
        var subRows = n.Child("subtitlelist")?.Children("title").ToList() ?? [];
        var audio = c.AudioNames.Count > 1 ? c.AudioNames.Take(audioRows.Count).ToList() : [];
        var subs = c.SubtitleNames.Count > 0 ? new[] { d.OffLabel }.Concat(c.SubtitleNames).Take(subRows.Count).ToList() : [];

        // a column that has nothing to choose is hidden together with its panel and header
        var unused = new List<Box>();
        if (audio.Count == 0 && n.Child("audiolist") is { } a) unused.Add(a.Rect());
        if (subs.Count == 0 && n.Child("subtitlelist") is { } sl) unused.Add(sl.Rect());
        bool Hidden(Box r) => unused.Any(u => r.X + r.W / 2 >= u.X && r.X + r.W / 2 <= u.X + u.W);

        var ops = new List<Op>();
        Background(ops, n, null, d);
        Pictures(ops, n, Hidden, panelIfMissing: true);
        if (n.Child("video") is { } video && d.ShowPreview) ops.Add(Frame(video, c.PreviewPicture));
        var audioBox = n.Child("audiolist")?.Rect() ?? default;
        foreach (var t in n.Child("texts")?.Children("text") ?? [])
        {
            var r = t.Rect();
            if (Hidden(r)) continue;
            var inAudio = r.X >= audioBox.X && r.X <= audioBox.X + audioBox.W;
            var value = t.Attr("tr_lang") == "1" ? (inAudio ? d.AudioLabel : d.SubtitlesLabel) : t.Text;
            if (value.Length > 0) ops.Add(Txt(t, value));
        }

        var buttons = new List<MenuButton>();
        var selection = Asset(common.Child("selection")?.Text);
        void Rows(List<XNode> rows, List<string> names, int first, string prefix, string kind, int? active)
        {
            for (var i = 0; i < names.Count; i++)
            {
                var number = first + i;
                var box = rows[i].Rect("frame_rect");
                ops.Add(Txt(rows[i], names[i]));
                if (active == number)
                    ops.Add(new Fill(new Box(box.X + box.W - MenuBuilder.MarkerSize - RowMarkerGap, box.Y + (box.H - MenuBuilder.MarkerSize) / 2, MenuBuilder.MarkerSize, MenuBuilder.MarkerSize), d.Accent, 1, MenuBuilder.MarkerSize / 2));
                buttons.Add(Iconed(ops, rows[i], box, i + 1, prefix + number, new MenuAction(kind, Mark: number)) ?? Dvd(new MenuButton(prefix + number, box, new MenuAction(kind, Mark: number)) { FocusImage = selection }));
            }
        }
        Rows(audioRows, audio, 1, "a", "audio", activeAudio);
        Rows(subRows, subs, 0, "s", "sub", activeSub);

        // DVDFab's downloaded templates (The Fleeting Meteor…) lay this page out like the menu itself: Play, Scenes and Set up at the same places, no
        // Home. Scenes leads to the scenes; Set up, the page one is on, closes it — BurnSmith's reading, DVDFab's own disc was not seen: a page needs a way back
        var likeRoot = n.Child("home") == null;
        var navs = new[]
        {
            (Id: "play", Key: "play", Image: "playbuttontimage", Action: c.Series ? new MenuAction("playall", Playlist: c.Titles![0].Playlist, Mark: c.Titles.Count) : new MenuAction("play", Playlist: c.Playlist)),
            (Id: "scenes", Key: likeRoot && MenuBuilder.Cells(d, c).Count > 1 ? "scenes" : "", Image: "scenebuttontimage", Action: new MenuAction("page", PageId(d, "scenes", 1))),
            (Id: "back", Key: likeRoot ? "setup" : "home", Image: likeRoot ? "setupbuttontimage" : "homebuttontimage", Action: new MenuAction("page", "root")),
        }.Where(x => n.Child(x.Key) != null && Button(x.Image, d) != null).Select(x => (x.Id, x.Action, File: Button(x.Image, d)!, Rect: n.Child(x.Key)!.Rect())).ToList();
        // these rectangles are sized for a small button (like "Set up"): at the pictures' own size Play and Home would cover one another.
        // They keep the size they have on the other pages and are moved apart, side by side around the middle of the two rectangles
        var boxes = navs.Select(x => Place(x.File, x.Rect)).ToList();
        if (navs.Count == 2 && Meets(boxes[0], boxes[1]))
        {
            const int gap = 40;
            var (l, r) = boxes[0].X <= boxes[1].X ? (0, 1) : (1, 0);
            var mid = (Math.Min(navs[0].Rect.X, navs[1].Rect.X) + Math.Max(navs[0].Rect.X + navs[0].Rect.W, navs[1].Rect.X + navs[1].Rect.W)) / 2;
            var x = Math.Clamp(mid - (boxes[l].W + gap + boxes[r].W) / 2, 0, Math.Max(0, MenuBuilder.W - boxes[l].W - gap - boxes[r].W));
            boxes[l] = boxes[l] with { X = x };
            boxes[r] = boxes[r] with { X = x + boxes[l].W + gap };
        }
        for (var k = 0; k < navs.Count; k++)
        {
            var (navId, action, file, _) = navs[k];
            var box = boxes[k];
            ops.Add(new Image(file, box));
            buttons.Add(Dvd(new MenuButton(navId, box, action) { FocusImage = Selected(file) }));
        }
        return new MenuPage(id, ops, Link(buttons));
    }

    /// <summary>
    /// Une page de réglages à une liste : celle de l'audio (<c>settings_audio…</c>) ou celle des sous-titres (<c>settings_sub…</c>), aux coordonnées du thème.
    /// Le point de la piste active est à gauche de la ligne, comme dans les colonnes de BurnSmith : c'est là que <see cref="MenuBuilder.MarkerOffset"/> place celui du xlet.
    /// </summary>
    MenuPage SetupPage(MenuDesign d, MenuContent c, int? activeAudio, int? activeSub, string id)
    {
        var n = template.Child("setup")!;
        if (id == "settings") // the two words, Audio and Subtitles, where the theme writes them; each is a button as wide as its word
        {
            var first = new List<Op>();
            Background(first, n, null, d);
            var words = new List<MenuButton>();
            foreach (var (key, label, target) in new[] { ("setupaudio", d.AudioLabel, "settings_audio"), ("setupsub", d.SubtitlesLabel, "settings_sub") })
            {
                var node = n.Child(key)!;
                var id2 = key == "setupaudio" ? "audio" : "subs";
                // a UHD template's picture button: its word is part of the picture (« Subtitle »), its French remake when there is one
                if (Asset(node.Attr("image")) is { } english)
                {
                    var plate = d.ButtonLanguage == "fr" && French(dir, node.Attr("image")) is { } fr && IsImage(fr) ? fr : english;
                    first.Add(new Image(plate, node.Rect()));
                    words.Add(new MenuButton(id2, node.Rect(), new MenuAction("page", target)) { FocusImage = Selected(plate) });
                    continue;
                }
                var word = Txt(node, label);
                word = word with { Rect = word.Rect with { W = Math.Min(word.Rect.W, (int)Math.Ceiling(MenuRenderer.TextWidth(word))) } };
                first.Add(word);
                words.Add(new MenuButton(id2, word.Rect, new MenuAction("page", target)));
            }
            if (n.Child("Return") is { } way)
            {
                if (Asset(way.Attr("image")) is { } picture) first.Add(new Image(picture, way.Rect()));
                else first.AddRange(MenuBuilder.Button(d, way.Rect(), "⌂", Math.Max(24, way.Rect().H * 2 / 3)));
                words.Add(new MenuButton("back", way.Rect(), new MenuAction("page", "root")));
            }
            return new MenuPage(id, first, Link(words));
        }
        var isSub = id.StartsWith("settings_sub", StringComparison.Ordinal);
        var rows = n.Child("langitems")!.Children("item").ToList();
        var names = isSub ? new[] { d.OffLabel }.Concat(c.SubtitleNames).ToList() : c.AudioNames.ToList();
        var ops = new List<Op>();
        Background(ops, n, null, d);
        Pictures(ops, n); // a UHD template's panel under the list
        // le titre nomme la catégorie : le libellé du thème (SetupAudioText, SetupSubtitleText) recouvrirait les lignes dès la 3e en audio, la 4e en sous-titres
        if (n.Child("setuptitle") is { } title) ops.Add(Txt(title, isSub ? d.SubtitlesLabel : d.AudioLabel, title.Attr("align", "left")));

        var buttons = new List<MenuButton>();
        var from = isSub ? 0 : 1;
        var active = isSub ? activeSub : activeAudio;
        // les plafonds de MenuContent (6 pistes audio, 5 sous-titres) laissent moins de lignes que les huit du thème : pas de pagination à faire
        for (var i = 0; i < names.Count && i < rows.Count; i++)
        {
            var box = rows[i].Rect();
            ops.Add(Txt(rows[i], names[i]));
            if (active == from + i)
                ops.Add(new Fill(new Box(box.X - MenuBuilder.MarkerSize - MenuBuilder.MarkerGap, box.Y + (box.H - MenuBuilder.MarkerSize) / 2, MenuBuilder.MarkerSize, MenuBuilder.MarkerSize), d.Accent, 1, MenuBuilder.MarkerSize / 2));
            buttons.Add(new MenuButton((isSub ? "s" : "a") + (from + i), box, new MenuAction(isSub ? "sub" : "audio", Mark: from + i)));
        }
        void Nav(string key, string glyph, MenuAction action, string? caption)
        {
            if (n.Child(key) is not { } node) return;
            var r = node.Rect();
            if (Asset(node.Attr("image")) is { } file) ops.Add(new Image(file, r));
            else ops.AddRange(MenuBuilder.Button(d, r, glyph, Math.Max(24, r.H * 2 / 3)));
            // la flèche du thème ne dit pas où elle mène : son nom à côté d'elle, en dehors du bouton
            if (caption != null && rows.Count > 0)
            {
                const int gap = 16;
                var left = key == "prev";
                var room = left ? new Box(40, r.Y, r.X - gap - 40, r.H) : new Box(r.X + r.W + gap, r.Y, MenuBuilder.W - 40 - r.X - r.W - gap, r.H);
                ops.Add(Txt(rows[0], caption, left ? "right" : "left") with { Rect = room });
            }
            buttons.Add(new MenuButton(key == "Return" ? "back" : key, r, action));
        }
        // from the page of the two words, a list leads back to it; a theme without that page links its two lists by its arrows instead
        var menu = HasSetupMenu(c);
        var both = SettingsIds(c).Count > 1 && !menu;
        if (both && isSub) Nav("prev", "<", new MenuAction("page", "settings_audio"), d.AudioLabel);
        Nav("Return", "⌂", new MenuAction("page", menu ? "settings" : "root"), null);
        if (both && !isSub) Nav("next", ">", new MenuAction("page", "settings_sub"), d.SubtitlesLabel);
        return new MenuPage(id, ops, Link(buttons));
    }

    /// <summary>
    /// Remote-control navigation from the positions: left/right stay in the same row and up/down in the same column when the row or column has
    /// another button (a Settings button centred under Play and Scenes belongs to the next row); otherwise the nearest button within 45°
    /// of each direction (cross axis counting double).
    /// Up and down fall back to any button on that side, so a thumbnail row can always reach the Home button below it.
    /// </summary>
    static List<MenuButton> Link(List<MenuButton> buttons)
    {
        static (double X, double Y) Centre(MenuButton b) => (b.Rect.X + b.Rect.W / 2.0, b.Rect.Y + b.Rect.H / 2.0);
        // share of the smaller button's extent that the two have in common across the direction of travel
        static bool SameLine(MenuButton a, MenuButton o, int dx)
        {
            var (a0, a1, o0, o1) = dx != 0 ? (a.Rect.Y, a.Rect.Y + a.Rect.H, o.Rect.Y, o.Rect.Y + o.Rect.H) : (a.Rect.X, a.Rect.X + a.Rect.W, o.Rect.X, o.Rect.X + o.Rect.W);
            return Math.Min(a1, o1) - Math.Max(a0, o0) >= 0.5 * Math.Min(a1 - a0, o1 - o0);
        }
        string? Nearest(MenuButton from, int dx, int dy)
        {
            var (fx, fy) = Centre(from);
            var ahead = buttons.Where(o => o != from)
                .Select(o => (o, p: Centre(o)))
                .Select(t => (t.o, along: (t.p.X - fx) * dx + (t.p.Y - fy) * dy, across: Math.Abs((t.p.X - fx) * dy) + Math.Abs((t.p.Y - fy) * dx)))
                .Where(t => t.along > 1).ToList();
            var line = ahead.Where(t => SameLine(from, t.o, dx)).ToList();
            var cone = ahead.Where(t => t.across <= t.along);
            return (line.Count > 0 ? line : cone.Any() || dy == 0 ? cone : ahead).OrderBy(t => t.along + 2 * t.across).Select(t => t.o.Id).FirstOrDefault();
        }
        return Reachable(buttons.Select(b => b with { Left = Nearest(b, -1, 0), Right = Nearest(b, 1, 0), Up = Nearest(b, 0, -1), Down = Nearest(b, 0, 1) }).ToList());
    }

    /// <summary>
    /// A staggered layout can leave a button that no direction leads to (a thumbnail below and to the left of the first row, which the "same column"
    /// rule skips): the nearest reachable button that has a free direction towards it gets that edge. Pages where every button is reachable are untouched.
    /// </summary>
    static List<MenuButton> Reachable(List<MenuButton> list)
    {
        static (double X, double Y) Centre(MenuButton b) => (b.Rect.X + b.Rect.W / 2.0, b.Rect.Y + b.Rect.H / 2.0);
        static string? Slot(MenuButton b, string direction) => direction switch { "Left" => b.Left, "Right" => b.Right, "Up" => b.Up, _ => b.Down };
        for (var round = 0; round < list.Count; round++)
        {
            var seen = new HashSet<string> { list[0].Id };
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var b in list.Where(b => seen.Contains(b.Id)).ToList())
                    foreach (var n in new[] { b.Left, b.Right, b.Up, b.Down })
                        if (n != null && seen.Add(n)) grew = true;
            }
            if (list.FirstOrDefault(b => !seen.Contains(b.Id)) is not { } lost) break;
            var (lx, ly) = Centre(lost);
            var choices = new List<(double Distance, int At, string Direction)>();
            for (var i = 0; i < list.Count; i++)
            {
                if (!seen.Contains(list[i].Id)) continue;
                var (x, y) = Centre(list[i]);
                double dx = lx - x, dy = ly - y;
                var (horizontal, vertical) = (dx > 0 ? "Right" : "Left", dy > 0 ? "Down" : "Up");
                var free = (Math.Abs(dx) >= Math.Abs(dy) ? new[] { horizontal, vertical } : [vertical, horizontal]).FirstOrDefault(d => Slot(list[i], d) == null);
                if (free != null) choices.Add((dx * dx + dy * dy, i, free));
            }
            if (choices.Count == 0) break; // every direction of every reachable button is taken: left as it is
            var (_, at, direction) = choices.MinBy(c => c.Distance);
            list[at] = direction switch
            {
                "Left" => list[at] with { Left = lost.Id },
                "Right" => list[at] with { Right = lost.Id },
                "Up" => list[at] with { Up = lost.Id },
                _ => list[at] with { Down = lost.Id },
            };
        }
        return list;
    }
}
