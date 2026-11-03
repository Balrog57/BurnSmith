namespace BurnSmith.Core.Menu;

/// <summary>What the user edits: the look of the menu pages. Plain data so it saves as a JSON template.</summary>
public sealed class MenuDesign
{
    public string Name { get; set; } = "Midnight";
    public string Title { get; set; } = "My Disc";
    public string PlayLabel { get; set; } = "Play";
    public string ScenesLabel { get; set; } = "Scenes";
    public string HomeLabel { get; set; } = "Home";
    public string ChapterLabel { get; set; } = "Chapter";
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

    /// <summary>Procedural built-in templates: no third-party artwork.</summary>
    public static IReadOnlyList<MenuDesign> BuiltIn { get; } =
    [
        new() { Name = "Midnight", Top = "#0B1B3B", Bottom = "#2B5DA8", Accent = "#FFC83D" },
        new() { Name = "Sunset", Top = "#3B0B4B", Bottom = "#E8632B", Accent = "#FFE066" },
        new() { Name = "Forest", Top = "#06251A", Bottom = "#2E7D4F", Accent = "#B9F27C" },
        new() { Name = "Ocean", Top = "#03243F", Bottom = "#0FA3B1", Accent = "#FFFFFF" },
        new() { Name = "Crimson", Top = "#2A0509", Bottom = "#A4161A", Accent = "#FFD166" },
        new() { Name = "Paper", Top = "#F4F1EA", Bottom = "#D8CFC0", TextColor = "#222222", Accent = "#C0392B" },
        new() { Name = "Violet", Top = "#1E1858", Bottom = "#6B5FB5", Accent = "#FFD23F", TextColor = "#FFD23F", ButtonStyle = "Text" },
    ];
}
