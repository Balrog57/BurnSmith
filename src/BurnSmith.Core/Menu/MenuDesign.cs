namespace BurnSmith.Core.Menu;

/// <summary>What the user edits: the look of the menu pages. Plain data so it saves as a JSON template.</summary>
public sealed class MenuDesign
{
    public string Name { get; set; } = "Midnight";
    public override string ToString() => Name; // what a screen reader says for a list item
    string category = "";
    /// <summary>Group shown in the template gallery and in the element library (see <see cref="TemplateLabel"/>); a saved design that still holds an older group name shows the neutral one.</summary>
    public string Category { get => TemplateLabel.Clean(category) is { Length: > 0 } neutral ? neutral : TemplateLabel.Of(Kind); set => category = value; }
    /// <summary>What the template is; it decides how the file is read and drawn (see <see cref="TemplateKind"/>).</summary>
    public TemplateKind Kind { get; set; }
    /// <summary>
    /// Folder of an installed template read at run time from the user's own copy; null = plain gradient design.
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
    /// <summary>The word before an episode's summary, where the template writes one (Streaming, Saison).</summary>
    public string OverviewLabel { get; set; } = "Overview";
    public string SettingsLabel { get; set; } = "Settings";
    public string AudioLabel { get; set; } = "Audio";
    public string SubtitlesLabel { get; set; } = "Subtitles";
    public string OffLabel { get; set; } = "Off";
    public string BackLabel { get; set; } = "Back";
    public string CreditsLabel { get; set; } = "Credits";
    /// <summary>Button and heading of the page that lists the extras (videos marked Bonus).</summary>
    public string BonusLabel { get; set; } = "Bonus";

    /// <summary>The menu's words in French, as the application writes them: a new project, and the gallery's pictures of the designs.</summary>
    public MenuDesign French()
    {
        PlayLabel = "Lecture"; ScenesLabel = "Chapitres"; HomeLabel = "Accueil"; ChapterLabel = "Chapitre";
        PlayAllLabel = "Tout lire"; EpisodesLabel = "Épisodes"; OverviewLabel = "Aperçu"; SettingsLabel = "Réglages"; AudioLabel = "Audio";
        SubtitlesLabel = "Sous-titres"; OffLabel = "Désactivés"; BackLabel = "Retour";
        return this;
    }
    /// <summary>
    /// Blu-ray pop-up menu: the remote's Pop-up key opens a menu over the title that plays, without stopping it (resume, chapters of that title,
    /// audio and subtitles, back to the main menu). A DVD has none.
    /// </summary>
    public bool Popup { get; set; }
    public string ResumeLabel { get; set; } = "Resume";
    /// <summary>The pop-up's button that stops the title and opens the disc's main menu.</summary>
    public string MainMenuLabel { get; set; } = "Main menu";
    /// <summary>Free text of the credits page (cast, thanks, production), one row per line; empty = no credits page and no button for it.</summary>
    public string Credits { get; set; } = "";
    /// <summary>Disc title per language for the player's disc information (BDMV\META\DL), one "xxx: title" per line, xxx = 3-letter language code (fra, eng, deu...).</summary>
    public string TitlesByLanguage { get; set; } = "";
    /// <summary>
    /// Elements placed by hand (text, picture, box), drawn over the pages their <see cref="MenuElement.Page"/> names. They are baked into the page
    /// picture like the rest of the layout, so a DVD and a Blu-ray menu show them without any player-side code.
    /// </summary>
    public List<MenuElement> Elements { get; set; } = [];
    /// <summary>What the user wrote in the text zones the template declares ("info", "overview1"...; see <see cref="MenuZone"/>). A template without such zones ignores them.</summary>
    public Dictionary<string, string> TextZones { get; set; } = [];
    /// <summary>A picture of the user's for the template's picture slot (the poster), instead of a frame of the film; null = the frame.</summary>
    public string? Poster { get; set; }

    /// <summary>The picture for the template's picture slot: the user's when its file is there, else <paramref name="film"/>.</summary>
    public string? PosterOr(string? film) => ElementLibrary.Resolve(Poster) is { } file && File.Exists(file) ? file : film;
    public string Font { get; set; } = "Segoe UI";
    /// <summary>Background gradient (#RRGGBB). Ignored when <see cref="BackgroundImage"/> is set.</summary>
    public string Top { get; set; } = "#0B1B3B";
    public string Bottom { get; set; } = "#2B5DA8";
    public string? BackgroundImage { get; set; }
    /// <summary>
    /// For a design laid out by a template whose background is a sample frame of some film: that sample is left out, and without a picture of the
    /// disc's own the pages get the design's neutral gradient (<see cref="Top"/>, <see cref="Bottom"/>).
    /// </summary>
    public bool OwnBackdrop { get; set; }
    /// <summary>
    /// A picture for one kind of page instead of the design's own background (the template's artwork or <see cref="BackgroundImage"/>): the key is the page's kind,
    /// see <see cref="PageKind"/> ("root", "scenes", "settings", "credits", "bonus"), the value a picture (<see cref="ElementLibrary.Reference"/> or a path).
    /// A moving menu has its clip behind every page, so it leaves these out.
    /// </summary>
    public Dictionary<string, string> PageBackgrounds { get; set; } = [];

    /// <summary>The kind of a page: its id without the number or the choice that follows ("scenes2" and "scenes1" are "scenes", "settings_1_2" is "settings").</summary>
    public static string PageKind(string pageId) => new(pageId.TakeWhile(char.IsAsciiLetter).ToArray());

    /// <summary>The picture of this page's own background, or null when it has none (or the file is gone).</summary>
    public string? PageBackground(string pageId) =>
        PageBackgrounds.TryGetValue(PageKind(pageId), out var picture) && ElementLibrary.Resolve(picture) is { } file && File.Exists(file) ? file : null;
    public string TextColor { get; set; } = "#FFFFFF";
    /// <summary>Colour of the focus frame around the selected button.</summary>
    public string Accent { get; set; } = "#FFC83D";
    /// <summary>
    /// How the selected button is marked when the template brings no picture for it:
    /// "box" (an accent frame, BurnSmith's own), "underline" (a bar under the button), "textcolor" (the label in the accent colour, no frame).
    /// </summary>
    public string HighlightStyle { get; set; } = "box";
    /// <summary>
    /// Colour of the selected button when it differs from <see cref="Accent"/>; empty = the accent.
    /// A template that brings its own artwork for a button keeps that artwork.
    /// </summary>
    public string SelectionColor { get; set; } = "";
    /// <summary>Opacity of the fill drawn inside the highlight, in percent (0 = a bare outline).</summary>
    public int SelectionOpacity { get; set; } = 18;
    /// <summary>
    /// Scene thumbnails as short moving excerpts of their chapter instead of still pictures. The DVD menu is then
    /// built as a short clip: each thumbnail plays inside its own frame while the page stays still. Ignored on a moving menu (the page is already a
    /// video overlay) and on Blu-ray, where the menu is drawn by the xlet and cannot play several videos at once.
    /// </summary>
    public bool AnimatedThumbs { get; set; }

    /// <summary>The colour the selected state is drawn in: <see cref="SelectionColor"/> when set, else <see cref="Accent"/>.</summary>
    public string SelectionInk => SelectionColor.Length > 0 ? SelectionColor : Accent;
    public bool ShowPreview { get; set; } = true;
    /// <summary>
    /// "Pill" = translucent rounded buttons, "Text" = bare labels (only the focus frame shows the selection),
    /// "Card" = solid card in <see cref="CardFill"/>, "Frame" = double frame in the accent colour, "Glass" = card with a <see cref="Glow"/> outline.
    /// </summary>
    public string ButtonStyle { get; set; } = "Pill";
    /// <summary>
    /// A template button whose word is part of its picture ("Play", "Scenes", "Set Up", "Main Menu"): "fr" = its French remake, kept beside it
    /// (<c>LANG_FRA</c>, made by tools\french-buttons.py), "en" = the template's own picture. A template without remakes shows its own either way.
    /// </summary>
    public string ButtonLanguage { get; set; } = "fr";
    /// <summary>Scene pages hold <see cref="Cols"/> x <see cref="Rows"/> buttons (3 x 2 = BurnSmith's original page).</summary>
    public int Cols { get; set; } = 3;
    public int Rows { get; set; } = 2;
    /// <summary>False = the scene buttons are text only, without a picture.</summary>
    public bool Thumbnails { get; set; } = true;
    /// <summary>
    /// Scene page as a <em>list</em> of lines instead of a grid of thumbnails (the template's own list items).
    /// A template that describes such a list places the lines itself; the others fall back to the grid.
    /// </summary>
    public bool SceneList { get; set; }
    /// <summary>No main page: the first scene (or episode) page is the menu, with play, bonus and settings under its grid. Nothing changes when there is no grid.</summary>
    public bool ScenesOnly { get; set; }
    /// <summary>A disc of several videos leaves its first one out of the episode pages; « Tout lire » still plays it first.</summary>
    public bool SkipFirstScene { get; set; }
    /// <summary>The Bonus page as text buttons, without thumbnails (a template's list when it describes one).</summary>
    public bool BonusText { get; set; }
    /// <summary>Square corners instead of rounded ones.</summary>
    public bool Square { get; set; }
    public string CardFill { get; set; } = "#111827";
    public string Glow { get; set; } = "#000000";
    /// <summary>Darkening laid over a background picture, in percent (keeps the text readable).</summary>
    public int Dim { get; set; } = 35;
    /// <summary>
    /// Blur of the background picture (the design's, a page's own, or the film frame a template shows behind its page), 0 to 40 in pixels of the
    /// 1920x1080 page: a busy picture stops fighting the text written over it. A template's own artwork is never blurred.
    /// </summary>
    public int BackgroundBlur { get; set; }
    /// <summary>Margins, gap, header and footer of the scene pages as shares of the canvas; 0 = BurnSmith's original fixed geometry.</summary>
    public double MarginX { get; set; }
    public double MarginY { get; set; }
    public double Gap { get; set; }
    public double Header { get; set; }
    public double Footer { get; set; }
    /// <summary>
    /// Moving menu: a clip of <see cref="LoopSeconds"/> seconds, taken from <see cref="LoopFile"/> (the disc's first video when null) from
    /// <see cref="LoopStart"/> on, plays behind the buttons and starts again when it ends. The page background picture is then left out.
/// LoopSeconds 0 = the music's length (BluRay.LoopLength); a clip shorter than the loop is played again from its start.
    /// </summary>
    public bool Loop { get; set; }
    public string? LoopFile { get; set; }
    public int LoopStart { get; set; }
    public int LoopSeconds { get; set; } = 30;
    /// <summary>
    /// Menu music: this audio file, from <see cref="MusicStart"/> on, repeated over <see cref="LoopSeconds"/> seconds with <see cref="MusicVolume"/> % and a fade
    /// of <see cref="MusicFade"/> seconds in and out. A moving menu plays it instead of the clip's own sound; without one the pages stay still and a
    /// black picture carries the sound. Null = no music. The loop restarts at its end (on a DVD also when another page is opened).
    /// </summary>
    public string? Music { get; set; }
    public int MusicStart { get; set; }
    public int MusicVolume { get; set; } = 100;
    public int MusicFade { get; set; } = 2;

    /// <summary>The music file when it is set and exists, else null.</summary>
    public string? ExistingMusic() => Music is { Length: > 0 } m && File.Exists(m) ? m : null;

    /// <summary>A copy safe to edit: the free elements are copied too (a shared list would be changed behind the caller's back).</summary>
    public MenuDesign Clone()
    {
        var copy = (MenuDesign)MemberwiseClone();
        copy.Elements = Elements.Select(e => e.Copy()).ToList();
        copy.PageBackgrounds = new(PageBackgrounds);
        copy.TextZones = new(TextZones);
        return copy;
    }

    /// <summary>Rows of the credits page (blank lines dropped; what does not fit on one page is cut).</summary>
    public List<string> CreditLines() =>
        Credits.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(MenuBuilder.MaxCreditLines).ToList();

    /// <summary>
    /// Disc titles per language as (code, title): the lines of <see cref="TitlesByLanguage"/> that read "xxx: title" with a 3-letter code.
    /// The first of a language wins; the plain <see cref="Title"/> is the English fallback every disc carries.
    /// </summary>
    public List<(string Lang, string Title)> LanguageTitles()
    {
        var list = new List<(string, string)>();
        foreach (var line in TitlesByLanguage.Split('\n'))
        {
            var at = line.IndexOf(':');
            if (at != 3) continue;
            var lang = line[..3].Trim().ToLowerInvariant();
            var title = line[(at + 1)..].Trim();
            if (title.Length > 0 && lang.All(char.IsAsciiLetterLower) && !list.Any(t => t.Item1 == lang)) list.Add((lang, title));
        }
        return list;
    }

    /// <summary>Takes the look of a template (picture, colours, font, button style, grid) and keeps this design's texts, title and music.</summary>
    public void TakeLook(MenuDesign t)
    {
        var itsClip = TemplateDir is { } left && LoopFile != null && InstalledTemplate.LoopClip(left) == LoopFile; // the moving background of the template being left
        Name = t.Name; Category = t.Category; Kind = t.Kind; TemplateDir = t.TemplateDir; BackgroundImage = t.BackgroundImage;
        Top = t.Top; Bottom = t.Bottom; TextColor = t.TextColor; Accent = t.Accent; HighlightStyle = t.HighlightStyle; SelectionColor = t.SelectionColor; SelectionOpacity = t.SelectionOpacity; AnimatedThumbs = t.AnimatedThumbs; Font = t.Font;
        ButtonStyle = t.ButtonStyle; Cols = t.Cols; Rows = t.Rows; Thumbnails = t.Thumbnails; Square = t.Square;
        CardFill = t.CardFill; Glow = t.Glow; Dim = t.Dim; BackgroundBlur = t.BackgroundBlur; OwnBackdrop = t.OwnBackdrop; ShowPreview = t.ShowPreview;
        MarginX = t.MarginX; MarginY = t.MarginY; Gap = t.Gap; Header = t.Header; Footer = t.Footer;
        // a template's own moving background comes with it, and leaves with it; a clip the user chose stays
        if (t.Loop && t.LoopFile != null && t.TemplateDir != null) (Loop, LoopFile, LoopStart, LoopSeconds) = (true, t.LoopFile, 0, t.LoopSeconds);
        else if (itsClip) (Loop, LoopFile, LoopSeconds) = (false, null, 30);
        // the veils and fades of the look change with it, under the elements the user placed
        Elements.RemoveAll(e => e.OfLook);
        Elements.InsertRange(0, t.Elements.Where(e => e.OfLook).Select(e => e.Copy()));
    }
}
