namespace BurnSmith.Core.Menu;

/// <summary>
/// An element the user places by hand on the menu pages: a text, a picture or a box, with its own place and size.
/// Geometry is in pixels of the 1920x1080 layout (the renderer scales it with the rest of the page), so what the editor shows is what the disc gets.
/// Elements are drawn over the page, after its own artwork and buttons; they are baked into the page picture, so they show on a DVD and on a
/// Blu-ray menu alike.
/// </summary>
public sealed class MenuElement
{
    /// <summary>"text", "image", "box" or "button".</summary>
    public string Kind { get; set; } = "text";
    /// <summary>"all" (every page), "root", "scenes", "bonus", "settings" or "credits".</summary>
    public string Page { get; set; } = "all";
    public int X { get; set; } = 140;
    public int Y { get; set; } = 140;
    public int W { get; set; } = 700;
    public int H { get; set; } = 90;
    public string Text { get; set; } = "";
    /// <summary>Picture file of an "image" element (a library path or an absolute one).</summary>
    public string? Image { get; set; }
    /// <summary>Empty = the design's own font.</summary>
    public string Font { get; set; } = "";
    public int Size { get; set; } = 48;
    public string Color { get; set; } = "#FFFFFF";
    public string Align { get; set; } = "left";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    /// <summary>Outline colour and width of a text, 0 = no outline.</summary>
    public string? Outline { get; set; }
    public int OutlineWidth { get; set; }
    /// <summary>Box fill colour and its opacity in percent.</summary>
    public string Fill { get; set; } = "#000000";
    public int Opacity { get; set; } = 55;
    public int Radius { get; set; } = 20;
    /// <summary>Border colour and width (a box, or the edge of a picture); 0 = no border.</summary>
    public string Border { get; set; } = "#FFFFFF";
    public int BorderWidth { get; set; }
    /// <summary>An image keeps its proportions inside the box instead of being stretched to it.</summary>
    public bool Fit { get; set; } = true;
    /// <summary>
    /// Where a "button" element leads: "movie" (the disc's title), "all" (every title in turn, a series), a page id
    /// ("root", "scenes1", "settings", "credits", "bonus1"), or "chapter3" (the third chapter of the film).
    /// </summary>
    public string Target { get; set; } = "movie";
    /// <summary>
    /// Drawn under the page's own buttons (over its background): the place for a panel or a frame that the buttons sit on.
    /// Otherwise the element goes over everything, like a caption or a badge.
    /// </summary>
    public bool Behind { get; set; }
    /// <summary>
    /// A "button" drawn from a picture (<see cref="Image"/>, a library button) is selected on the disc with this picture, the "sub" twin of the
    /// button's own (a DVD subpicture is made from it too); null = the design's accent frame around the button.
    /// </summary>
    public string? Focus { get; set; }
    /// <summary>A "button" without a picture takes this look (Pill, Text, Card, Frame, Glass: <see cref="ElementLibrary.ButtonStyles"/>) instead of the design's; empty = the design's.</summary>
    public string Look { get; set; } = "";

    public const string AllPages = "all";

    /// <summary>True for an element the player can press (it becomes a real menu button, not just a drawing).</summary>
    public bool IsButton => Kind.Trim().ToLowerInvariant() == "button";

    public MenuElement Copy() => (MenuElement)MemberwiseClone();

    /// <summary>True when this element goes on the page of that id ("scenes" covers scenes1, scenes2...).</summary>
    public bool AppliesTo(string pageId)
    {
        var page = Page.Trim().ToLowerInvariant();
        if (page.Length == 0 || page == AllPages) return true;
        if (page is "scenes" or "bonus" or "settings") return pageId.StartsWith(page, StringComparison.Ordinal); // "settings" : aussi settings_audio, et les pages par choix d'un DVD
        return pageId.Equals(page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rectangle on the 1920x1080 canvas, kept inside the page.</summary>
    public Box Box()
    {
        var w = Math.Clamp(W, 8, MenuBuilder.W);
        var h = Math.Clamp(H, 8, MenuBuilder.H);
        var x = Math.Clamp(X, 0, MenuBuilder.W - w);
        var y = Math.Clamp(Y, 0, MenuBuilder.H - h);
        return new Box(x, y, w, h);
    }

    /// <summary>The drawing instructions of this element on a page (empty when the picture is missing: the page stays drawable).</summary>
    public IEnumerable<Op> Ops(MenuDesign d)
    {
        var box = Box();
        switch (Kind.Trim().ToLowerInvariant())
        {
            case "image":
                var file = ElementLibrary.Resolve(Image) ?? Image;
                if (file is null || !File.Exists(file)) yield break;
                yield return new Image(file, box, Cover: !Fit);
                if (BorderWidth > 0) yield return new Border(box, Border, BorderWidth, Radius);
                break;
            case "box":
                yield return new Fill(box, Fill, Math.Clamp(Opacity, 0, 100) / 100.0, Math.Max(0, Radius));
                if (BorderWidth > 0) yield return new Border(box, Border, BorderWidth, Radius);
                break;
            case "button":
                if (ElementLibrary.Resolve(Image) is { } art && File.Exists(art))
                {
                    // a library button: its picture is the look, the label (if any) goes over it
                    yield return new Image(art, box);
                    if (Text.Length > 0) yield return new Label(Text, box, Math.Clamp(Size, 20, 80), true);
                    break;
                }
                // the button's own look, else the design's (card, label), so a hand-placed button sits beside the template's own
                var look = ElementLibrary.ButtonStyles.Any(s => s.Value == Look) ? Look : "";
                var styled = look.Length == 0 ? d : d.Clone();
                styled.ButtonStyle = look.Length == 0 ? d.ButtonStyle : look;
                foreach (var op in MenuBuilder.Button(styled, box, Text.Length > 0 ? Text : "Button", Math.Clamp(Size, 20, 80))) yield return op;
                break;
            default:
                if (Text.Length == 0) yield break;
                yield return new Text(Text, box, Math.Clamp(Size, 8, 300), Font.Length > 0 ? Font : d.Font, null, Color, Bold, Italic, Align,
                                      OutlineWidth > 0 ? Outline : null, OutlineWidth);
                break;
        }
    }

    /// <summary>
    /// Reads a command-line element: "kind=text;page=root;rect=100,120,900,90;text=Bienvenue;size=54;color=#FFE6AE;align=center".
    /// Keys: kind, page, rect (x,y,w,h in pixels of the 1920x1080 canvas), text, file, font, size, color, align, bold, italic, outline,
    /// outlinew, fill, opacity, radius, border, borderw, fit, behind, target, focus, look. Unknown keys are ignored, so a misspelt one is not silently a different element.
    /// </summary>
    public static MenuElement Parse(string spec)
    {
        var e = new MenuElement();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim().ToLowerInvariant();
            var value = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "kind": e.Kind = value.ToLowerInvariant(); break;
                case "page": e.Page = value.ToLowerInvariant(); break;
                case "rect":
                    var n = value.Split(',').Select(v => int.TryParse(v.Trim(), out var i) ? i : 0).ToArray();
                    if (n.Length == 4) { e.X = n[0]; e.Y = n[1]; e.W = n[2]; e.H = n[3]; }
                    break;
                case "text": e.Text = value.Replace("\\n", "\n"); break;
                case "file" or "image": e.Image = value; break;
                case "font": e.Font = value; break;
                case "size": if (int.TryParse(value, out var size)) e.Size = size; break;
                case "color": e.Color = value; break;
                case "align": e.Align = value.ToLowerInvariant(); break;
                case "bold": e.Bold = value is "1" or "true" or "oui"; break;
                case "italic": e.Italic = value is "1" or "true" or "oui"; break;
                case "outline": e.Outline = value; break;
                case "outlinew": if (int.TryParse(value, out var ow)) e.OutlineWidth = ow; break;
                case "fill": e.Fill = value; break;
                case "opacity": if (int.TryParse(value, out var op)) e.Opacity = op; break;
                case "radius": if (int.TryParse(value, out var r)) e.Radius = r; break;
                case "border": e.Border = value; break;
                case "borderw": if (int.TryParse(value, out var bw)) e.BorderWidth = bw; break;
                case "fit": e.Fit = value is not ("0" or "false" or "non"); break;
                case "behind": e.Behind = value is "1" or "true" or "oui"; break;
                case "target": e.Target = value.ToLowerInvariant(); break;
                case "focus": e.Focus = value; break;
                case "look": e.Look = value; break;
            }
        }
        if (e.Kind is not ("text" or "image" or "box" or "button")) throw new ArgumentException($"element kind unknown: {e.Kind} (text, image, box or button)");
        return e;
    }
}
