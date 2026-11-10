namespace BurnSmith.Core.Menu;

/// <summary>
/// The menu designs BurnSmith draws from its own parameters: grid, colours, font and margins only.
/// The menu engine draws them, so the look is an interpretation, not a copy of any renderer.
/// </summary>
public static class BuiltInTemplates
{
    static MenuDesign T(string name, int cols, int rows, bool thumbs, string style, bool square, string bg, string accent, string font,
        string text, string fill, string glow, double mx, double my, double gap, double header, double footer, int dim) => new()
    {
        Name = name, Category = TemplateLabel.Own, Kind = TemplateKind.BuiltIn,
        Cols = cols, Rows = rows, Thumbnails = thumbs, ButtonStyle = style, Square = square,
        Top = bg, Bottom = bg, Accent = accent, Font = font, TextColor = text, CardFill = fill, Glow = glow,
        MarginX = mx, MarginY = my, Gap = gap, Header = header, Footer = footer, Dim = dim,
    };

    public static List<MenuDesign> All() =>
    [
        T("Nuit d'encre", 2, 3, true, "Card", false, "#0B1220", "#F59E0B", "Segoe UI", "#F8FAFC", "#131C2E", "#000000", 0.08, 0.09, 0.030, 0.13, 0.10, 10),
        T("Séries", 3, 2, true, "Card", true, "#0F172A", "#38BDF8", "Segoe UI", "#FFFFFF", "#1E293B", "#000000", 0.06, 0.07, 0.020, 0.09, 0.09, 6),
        T("Cinéma muet", 1, 4, false, "Frame", true, "#17120D", "#C9A227", "Georgia", "#F5EFE6", "#2A2118", "#000000", 0.11, 0.075, 0.022, 0.15, 0.11, 16),
        T("Tube cathodique", 2, 2, true, "Glass", false, "#0A0F14", "#FF2E88", "Courier New", "#D7FFE9", "#121A22", "#2BFF88", 0.065, 0.08, 0.023, 0.105, 0.105, 18),
        T("Papier", 4, 2, false, "Text", true, "#FAFAF7", "#4B5563", "Segoe UI", "#0F172A", "#F1F5F9", "#FFFFFF", 0.045, 0.065, 0.017, 0.10, 0.09, 0),
        Metal(),
    ];

    /// <summary>
    /// The menu made for a film (Bright), without what belonged to that film (its logo, its pictures): steel letters for buttons, an orange
    /// glow, the chosen button underlined, no preview picture. The scene and settings pages are darkened so that they stay readable over a
    /// background picture or a moving menu, which the root page shows untouched.
    /// </summary>
    static MenuDesign Metal()
    {
        var d = T("Métal", 3, 2, true, "Metal", false, "#05070B", "#FF5A1F", "Arial Black", "#F2F2F2", "#0B0F16", "#FF5A1F", 0.08, 0.09, 0.03, 0.13, 0.10, 0);
        (d.HighlightStyle, d.ShowPreview) = ("underline", false); // the selection takes the accent
        foreach (var page in new[] { "scenes", "settings" })
            d.Elements.Add(new MenuElement { Kind = "box", Page = page, X = 0, Y = 0, W = 1920, H = 1080, Fill = "#000000", Opacity = 62, Radius = 0, Behind = true });
        return d;
    }
}
