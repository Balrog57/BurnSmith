namespace BurnSmith.Core.Menu;

/// <summary>
/// Non-destructive override applied to a native element or zone of a template page
/// (move, resize, hide, restyle, retarget, lock, visual effects, or state artwork).
/// </summary>
public sealed class NativeElementOverride
{
    /// <summary>Exact page id ("root", "scenes1"...) or page kind ("scenes", "bonus", "settings", "credits").</summary>
    public string Page { get; set; } = "root";
    /// <summary>Native button id ("play", "c1", "back"...) or zone key ("title", "poster", "slot:info"...).</summary>
    public string Id { get; set; } = "";
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? W { get; set; }
    public int? H { get; set; }
    public bool Hidden { get; set; }
    public bool Locked { get; set; }
    public int ZIndex { get; set; }
    public string? Text { get; set; }
    public string? Font { get; set; }
    public int? Size { get; set; }
    public string? Color { get; set; }
    public string? Align { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public string? Look { get; set; }
    public string? Image { get; set; }
    public string? Focus { get; set; }
    public string? ActiveImage { get; set; }
    public string? ActiveColor { get; set; }
    public string? Target { get; set; }
    public string? Outline { get; set; }
    public int OutlineWidth { get; set; }
    public string? ShadowColor { get; set; }
    public int ShadowBlur { get; set; }
    public int ShadowOffsetX { get; set; }
    public int ShadowOffsetY { get; set; }
    public int Opacity { get; set; } = 100;
    public double Rotation { get; set; }
    public int ImageBlur { get; set; }
    public int? Radius { get; set; }
    public string? Fill { get; set; }
    public string? Border { get; set; }
    public int? BorderWidth { get; set; }

    public NativeElementOverride Copy() => (NativeElementOverride)MemberwiseClone();

    public bool AppliesTo(string pageId) =>
        Page.Equals(pageId, StringComparison.OrdinalIgnoreCase)
        || Page.Equals(MenuDesign.PageKind(pageId), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Per-page looping background video and/or audio override.</summary>
public sealed class PageMedia
{
    /// <summary>Exact page id ("root", "scenes1"...) or page kind ("scenes", "bonus", "settings", "credits").</summary>
    public string Page { get; set; } = "root";
    public bool? Loop { get; set; }
    public string? LoopFile { get; set; }
    public int? LoopStart { get; set; }
    public int? LoopSeconds { get; set; }
    public string? Music { get; set; }
    public int? MusicStart { get; set; }
    public int? MusicVolume { get; set; }
    public int? MusicFade { get; set; }

    public PageMedia Copy() => (PageMedia)MemberwiseClone();

    public bool AppliesTo(string pageId) =>
        Page.Equals(pageId, StringComparison.OrdinalIgnoreCase)
        || Page.Equals(MenuDesign.PageKind(pageId), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Visual layout and button visibility for Blu-ray Pop-up menus (BD-J and HDMV).</summary>
public sealed class PopupStyle
{
    /// <summary>"bottom", "top", "left", or "right".</summary>
    public string Dock { get; set; } = "bottom";
    public int BarHeight { get; set; } = 260;
    public int ButtonWidth { get; set; } = 380;
    public int ButtonHeight { get; set; } = 100;
    public string Fill { get; set; } = "#000000";
    public string? ButtonLook { get; set; }
    public bool ShowResume { get; set; } = true;
    public bool ShowChapters { get; set; } = true;
    public bool ShowSettings { get; set; } = true;
    public bool ShowMainMenu { get; set; } = true;

    public PopupStyle Copy() => (PopupStyle)MemberwiseClone();
}

/// <summary>Remote-control navigation/activation sound settings (Blu-ray sound.bdmv + BD-J HSound).</summary>
public sealed class MenuSoundSettings
{
    /// <summary>"none", "discret", "cinema", "retro", "classic", or "custom".</summary>
    public string Pack { get; set; } = "none";
    public string? MoveWav { get; set; }
    public string? SelectWav { get; set; }
    public int Volume { get; set; } = 80;

    public MenuSoundSettings Copy() => (MenuSoundSettings)MemberwiseClone();
}
