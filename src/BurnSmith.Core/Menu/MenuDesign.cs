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
    /// <summary>"Pill" = translucent rounded buttons, "Text" = bare labels (only the focus frame shows the selection).</summary>
    public string ButtonStyle { get; set; } = "Pill";
    /// <summary>Background music: stored with the template, muxed by the disc builder when supported.</summary>
    public string? Music { get; set; }

    public MenuDesign Clone() => (MenuDesign)MemberwiseClone();
}
