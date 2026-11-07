using System.Text;

namespace BurnSmith.Core.Menu;

/// <summary>
/// Menu themes stored as one <c>config.xml</c> with the layout of every screen size, and the pictures of each size in its own folder.
/// A theme installed on the machine keeps that layout in an encrypted <c>config.dat</c>; BurnSmith reads the plain carried <c>config.xml</c>
/// (made by <see cref="TemplateCatalog.Pack"/>) next to the pictures. Only the 1920x1080 branch is used, rewritten as the tree
/// <see cref="InstalledTemplate"/> draws.
/// </summary>
static class ThemeSkin
{
    const string Screen = "1920x1080";

    public static bool IsTheme(string dir) => Directory.Exists(Path.Combine(dir, Screen)) && (File.Exists(Path.Combine(dir, "config.xml")) || File.Exists(Path.Combine(dir, "config.dat")));

    /// <summary>The layout file as text: the carried <c>config.xml</c>. Null when it is absent or unreadable.</summary>
    public static string? ReadConfig(string dir)
    {
        var plain = Path.Combine(dir, "config.xml");
        return File.Exists(plain) ? File.ReadAllText(plain) : null;
    }

    static XNode El(string name, params (string Key, string Value)[] attrs) => new(name, attrs.ToDictionary(a => a.Key, a => a.Value));
    static XNode Leaf(string name, string text) { var n = El(name); n.Content.Append(text); return n; }
    static int I(XNode? n, string name) => int.TryParse(n?.Attr(name), out var v) ? v : 0;
    static string Rect(int x, int y, int w, int h) => $"{x},{y},{w},{h}";
    static string Colour(string argb) => argb.Length == 9 ? "#" + argb[3..] : argb; // "#ffff0080" is alpha, red, green, blue

    /// <summary>The tree of the 1920x1080 branch; null when the layout cannot be read.</summary>
    public static XNode? Build(string dir)
    {
        if (ReadConfig(dir) is not { } xml || LooseXml.Parse(xml).Child("MenuTemplate") is not { } theme
            || theme.Children("TemplateBranch").FirstOrDefault(b => b.Attr("Resolution") == Screen) is not { } branch) return null;
        var menu = branch.Child("Menu");
        var page = branch.Child("Scene");
        string Picture(XNode? n) => n?.Attr("FileName") is { Length: > 0 } f ? $"{Screen}/{f}" : "";

        // Left / Top are the text's top-left corner; the box is only as wide as the text can be (a left-aligned title must not run under the preview frame)
        XNode Text(string name, XNode? t, int width, string align, params (string, string)[] more)
        {
            var size = I(t, "Size") > 0 ? I(t, "Size") : 40;
            return El(name, [("rect", Rect(I(t, "Left"), I(t, "Top"), width, size * 3 / 2)), ("font", t?.Attr("Type", "Tahoma") ?? "Tahoma"), ("font_size", size.ToString()),
                ("color", t?.Attr("Color", "#FFFFFF") ?? "#FFFFFF"), ("bold", t?.Attr("Bold") == "true" ? "1" : "0"), ("italic", t?.Attr("Italic") == "true" ? "1" : "0"),
                ("align", align), .. more]);
        }
        int Room(XNode? t, int limit = 1000) => Math.Max(200, Math.Min(limit, MenuBuilder.W - I(t, "Left") - 40));

        var common = El("common");
        common.Kids.AddRange([Leaf("accent", Colour(branch.Child("Color")?.Child("Frame")?.Attr("Hover") ?? "#FF0080")), Leaf("templatename", $"{theme.Attr("Name", "Thème")} {Path.GetFileName(dir).Replace("THEME", "", StringComparison.OrdinalIgnoreCase)}"), // the names are only two kinds (Birthday, Natural): the number tells them apart
            Leaf("leftbuttontimage", Picture(page?.Child("ButtonPre"))), Leaf("rightbuttontimage", Picture(page?.Child("ButtonNext"))), Leaf("homebuttontimage", Picture(page?.Child("Return")))]);

        // ---- main menu: preview inside its frame, title, then Play / Scenes / Setup as text buttons
        var frame = menu?.Child("ThumbFrame");
        var preview = menu?.Child("Thumb");
        var bg = El("bg");
        bg.Kids.Add(Leaf("image", Picture(menu?.Child("Background"))));
        var root = El("rootmenu");
        root.Kids.AddRange([bg,
            El("video", ("rect", Rect(I(frame, "Left") + I(preview, "Left"), I(frame, "Top") + I(preview, "Top"), I(preview, "Width"), I(preview, "Height"))),
                ("frame_under", Picture(frame)), ("under_rect", Rect(I(frame, "Left"), I(frame, "Top"), I(frame, "Width"), I(frame, "Height"))))]);
        var title = menu?.Child("TitleText");
        var overFrame = I(title, "Left") < I(frame, "Left") && I(title, "Top") < I(frame, "Top") + I(frame, "Height") && I(title, "Top") + I(title, "Size") * 3 / 2 > I(frame, "Top");
        root.Kids.Add(Text("label", title, overFrame ? Math.Max(200, I(frame, "Left") - I(title, "Left") - 20) : Room(title), "left"));
        foreach (var (role, key) in new[] { ("play", "PlayText"), ("scenes", "ScenesText"), ("setup", "SetupText"), ("bonus", "BonusText") })
            root.Kids.Add(Text(role, menu?.Child(key), I(menu?.Child(key), "Size") * 7, "left", ("textbutton", "1")));

        // ---- scene page: a frame per thumbnail (the thumbnail's position is an offset inside it), its name under it, previous / back / next
        var scene = El("scene");
        var sceneBg = El("bg");
        sceneBg.Kids.Add(Leaf("image", Picture(page?.Child("Background"))));
        scene.Kids.Add(sceneBg);
        foreach (var (role, key) in new[] { ("left", "ButtonPre"), ("home", "Return"), ("right", "ButtonNext") })
            if (page?.Child(key) is { } button) scene.Kids.Add(El(role, ("rect", Rect(I(button, "Left"), I(button, "Top"), I(button, "Width"), I(button, "Height")))));
        scene.Kids.Add(Text("label", page?.Child("TitleText"), Room(page?.Child("TitleText")), "left"));
        var videos = El("videoes");
        for (var i = 0; page?.Child("ThumbFrame")?.Child($"Frame{i}") is { } box; i++)
        {
            var offset = page.Child("Thumb")?.Child($"Thumb{i}");
            var video = El("video", ("rect", Rect(I(box, "Left") + I(offset, "Left"), I(box, "Top") + I(offset, "Top"), I(offset, "Width"), I(offset, "Height"))),
                ("frame_under", Picture(box)), ("under_rect", Rect(I(box, "Left"), I(box, "Top"), I(box, "Width"), I(box, "Height"))));
            var label = page.Child("ThumbText")?.Child($"ThumbText{i}");
            var size = I(label, "Size") > 0 ? I(label, "Size") : 30;
            video.Kids.Add(El("name", ("rect", Rect(I(label, "Left"), I(label, "Top"), I(box, "Width"), size * 3 / 2)), ("font", label?.Attr("Type", "Tahoma") ?? "Tahoma"),
                ("font_size", size.ToString()), ("color", label?.Attr("Color", "#FFFFFF") ?? "#FFFFFF"), ("bold", label?.Attr("Bold") == "true" ? "1" : "0")));
            videos.Kids.Add(video);
        }
        scene.Kids.Add(videos);
        // the same chapters as a list of lines: the theme gives ten ItemText rectangles for it
        if (page?.Child("ListTypeText") is { } list)
        {
            var items = El("list");
            for (var i = 0; list.Child($"ItemText{i}") is { } item; i++)
            {
                var size = I(item, "Size") > 0 ? I(item, "Size") : 30;
                var width = item.Attr("Alignment", "Left") == "Center" ? 900 : 820;
                items.Kids.Add(El("item", ("rect", Rect(I(item, "Left"), I(item, "Top"), width, size * 3 / 2)), ("font", item.Attr("Type", "Tahoma") ?? "Tahoma"),
                    ("font_size", size.ToString()), ("color", item.Attr("Color", "#FFFFFF") ?? "#FFFFFF"), ("bold", item.Attr("Bold") == "true" ? "1" : "0"),
                    ("align", item.Attr("Alignment", "Left").ToLowerInvariant())));
            }
            if (items.Kids.Count > 0) scene.Kids.Add(items);
        }
        var tscenes = El("tscenes");
        tscenes.Kids.Add(scene);
        var scenes = El("scenes");
        scenes.Kids.Add(tscenes);

        // the audio / subtitle page: a theme without a language list gets BurnSmith's own rows over this background
        var setup = El("setup");
        var setupBg = El("bg");
        setupBg.Kids.Add(Leaf("image", Picture(branch.Child("Setup")?.Child("Background"))));
        setup.Kids.Add(setupBg);
        // une seule liste de huit lignes : le thème décrit une page par catégorie, comme les clips setup_audio / setup_subtitle
        if (branch.Child("Setup") is { } lang && lang.Child("LangItemText")?.Child("ItemText0") != null)
        {
            setup.Kids.Add(Text("setuptitle", lang.Child("ASLangTitleText"), Room(lang.Child("ASLangTitleText")), "left"));
            var rows = El("langitems");
            for (var i = 0; lang.Child("LangItemText")!.Child($"ItemText{i}") is { } item; i++) rows.Kids.Add(Text("item", item, 600, "left"));
            setup.Kids.Add(rows);
            foreach (var (role, key) in new[] { ("prev", "ButtonPre"), ("Return", "Return"), ("next", "ButtonNext") })
                if (lang.Child(key) is { } button) setup.Kids.Add(El(role, ("rect", Rect(I(button, "Left"), I(button, "Top"), I(button, "Width"), I(button, "Height"))), ("image", Picture(button))));
        }

        var template = El("template");
        template.Kids.AddRange([common, root, scenes, setup]);
        return template;
    }
}
