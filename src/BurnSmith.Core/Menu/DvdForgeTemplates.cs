namespace BurnSmith.Core.Menu;

/// <summary>
/// The five menu templates of DVD Forge (GPL-3.0, https://github.com/Nicolasalucin/dvd-forge, crates/dvdforge-menu/src/templates.rs).
/// Only their parameters (grid, colours, font, margins) are taken; BurnSmith's own menu engine draws them, so the look is an
/// interpretation of DVD Forge's, not a copy of its renderer.
/// </summary>
public static class DvdForgeTemplates
{
    public const string Category = "DVD Forge";

    static MenuDesign T(string name, int cols, int rows, bool thumbs, string style, bool square, string bg, string accent, string font,
        string text, string fill, string glow, double mx, double my, double gap, double header, double footer, int dim) => new()
    {
        Name = name, Category = Category, Cols = cols, Rows = rows, Thumbnails = thumbs, ButtonStyle = style, Square = square,
        Top = bg, Bottom = bg, Accent = accent, Font = font, TextColor = text, CardFill = fill, Glow = glow,
        MarginX = mx, MarginY = my, Gap = gap, Header = header, Footer = footer, Dim = dim,
    };

    public static List<MenuDesign> All() =>
    [
        T("Cinema Dark", 2, 2, true, "Card", false, "#090E18", "#EAB308", "Inter, Segoe UI", "#FFFFFF", "#111827", "#000000", 0.07, 0.08, 0.025, 0.12, 0.12, 12),
        T("Multi-Page TV Series Grid", 3, 2, true, "Card", true, "#0C1827", "#22D3EE", "Inter, Segoe UI", "#FFFFFF", "#1E293B", "#000000", 0.055, 0.075, 0.018, 0.10, 0.10, 8),
        T("Classic Criterion", 1, 4, false, "Frame", true, "#14100C", "#D4AF37", "Georgia", "#FAF5EB", "#241E18", "#000000", 0.10, 0.08, 0.025, 0.14, 0.10, 18),
        T("Retro CRT", 2, 2, true, "Glass", false, "#080C12", "#FF00B4", "Courier New", "#D9FFE8", "#101820", "#39FF14", 0.06, 0.075, 0.02, 0.11, 0.11, 20),
        T("Minimalist", 4, 2, false, "Text", true, "#FFFFFF", "#6B7280", "Inter, Segoe UI", "#111827", "#F8FAFC", "#FFFFFF", 0.05, 0.07, 0.015, 0.11, 0.10, 0),
    ];
}
