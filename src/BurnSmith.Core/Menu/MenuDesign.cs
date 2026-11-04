namespace BurnSmith.Core.Menu;

/// <summary>What the user edits: the look of the menu pages. Plain data so it saves as a JSON template.</summary>
public sealed class MenuDesign
{
    public string Name { get; set; } = "Midnight";
    /// <summary>Group in the template gallery (the vendor / disc type the template came from).</summary>
    public string Category { get; set; } = "";
    /// <summary>
    /// Folder of an installed template (DVDFab / Leawo) read at run time from the user's own copy; null = plain gradient design.
    /// Its artwork decides the look and the layout; only the texts, tracks and pictures come from the disc.
    /// </summary>
    public string? TemplateDir { get; set; }
    public string Title { get; set; } = "My Disc";
    public string PlayLabel { get; set; } = "Play";
    public string ScenesLabel { get; set; } = "Scenes";
    public string HomeLabel { get; set; } = "Home";
    public string ChapterLabel { get; set; } = "Chapter";
    public string PlayAllLabel { get; set; } = "Play all";
    public string EpisodesLabel { get; set; } = "Episodes";
    public string SettingsLabel { get; set; } = "Settings";
    public string AudioLabel { get; set; } = "Audio";
    public string SubtitlesLabel { get; set; } = "Subtitles";
    public string OffLabel { get; set; } = "Off";
    public string BackLabel { get; set; } = "Back";
    public string Font { get; set; } = "Segoe UI";
    /// <summary>Background gradient (#RRGGBB). Ignored when <see cref="BackgroundImage"/> is set.</summary>
    public string Top { get; set; } = "#0B1B3B";
    public string Bottom { get; set; } = "#2B5DA8";
    public string? BackgroundImage { get; set; }
    public string TextColor { get; set; } = "#FFFFFF";
    /// <summary>Colour of the focus frame around the selected button.</summary>
    public string Accent { get; set; } = "#FFC83D";
    public bool ShowPreview { get; set; } = true;
    /// <summary>
    /// "Pill" = translucent rounded buttons, "Text" = bare labels (only the focus frame shows the selection),
    /// "Card" = solid card in <see cref="CardFill"/>, "Frame" = double frame in the accent colour, "Glass" = card with a <see cref="Glow"/> outline.
    /// </summary>
    public string ButtonStyle { get; set; } = "Pill";
    /// <summary>Scene pages hold <see cref="Cols"/> x <see cref="Rows"/> buttons (3 x 2 = BurnSmith's original page).</summary>
    public int Cols { get; set; } = 3;
    public int Rows { get; set; } = 2;
    /// <summary>False = the scene buttons are text only, without a picture.</summary>
    public bool Thumbnails { get; set; } = true;
    /// <summary>Square corners instead of rounded ones.</summary>
    public bool Square { get; set; }
    public string CardFill { get; set; } = "#111827";
    public string Glow { get; set; } = "#000000";
    /// <summary>Darkening laid over a background picture, in percent (keeps the text readable).</summary>
    public int Dim { get; set; } = 35;
    /// <summary>Margins, gap, header and footer of the scene pages as shares of the canvas; 0 = BurnSmith's original fixed geometry.</summary>
    public double MarginX { get; set; }
    public double MarginY { get; set; }
    public double Gap { get; set; }
    public double Header { get; set; }
    public double Footer { get; set; }
    /// <summary>Background music: stored with the template, muxed by the disc builder when supported.</summary>
    public string? Music { get; set; }

    public MenuDesign Clone() => (MenuDesign)MemberwiseClone();

    /// <summary>Takes the look of a template (picture, colours, font, button style, grid) and keeps this design's texts, title and music.</summary>
    public void TakeLook(MenuDesign t)
    {
        Name = t.Name; Category = t.Category; TemplateDir = t.TemplateDir; BackgroundImage = t.BackgroundImage;
        Top = t.Top; Bottom = t.Bottom; TextColor = t.TextColor; Accent = t.Accent; Font = t.Font;
        ButtonStyle = t.ButtonStyle; Cols = t.Cols; Rows = t.Rows; Thumbnails = t.Thumbnails; Square = t.Square;
        CardFill = t.CardFill; Glow = t.Glow; Dim = t.Dim;
        MarginX = t.MarginX; MarginY = t.MarginY; Gap = t.Gap; Header = t.Header; Footer = t.Footer;
    }
}
