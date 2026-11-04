using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BurnSmith.Core;
using BurnSmith.Core.Menu;
using Microsoft.Win32;

namespace BurnSmith.App;

/// <summary>
/// Edits a <see cref="MenuDesign"/> with a live preview. The preview is drawn by the same <see cref="MenuRenderer"/> the disc
/// builders use, so it shows the real page (chapter pictures are grey boxes: only the preview frame is extracted).
/// </summary>
public partial class MenuEditorWindow : Window
{
    readonly MenuDesign working;
    readonly MenuContent content;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    int version;
    string? error;
    bool syncing;

    /// <summary>The pages built for one state of the design; the pictures of a page are drawn when it is first shown.</summary>
    sealed class Shot(MenuDesign design, List<MenuPage> pages)
    {
        public MenuDesign Design { get; } = design;
        public List<MenuPage> Pages { get; } = pages;
        public Dictionary<string, PageImages> Cache { get; } = [];
    }

    sealed record PageImages(BitmapImage Background, Dictionary<string, BitmapImage> Focus);
    public sealed record Slide(string Id, string Name, BitmapImage Thumb);

    Shot? shot;
    MenuPage? current;
    PageImages? currentImages;
    string? focusId;
    int audioSel = 1, subSel; // what the settings page marks while previewing (audio stream number 1-based, subtitle 0 = off)
    bool stripSync;
    List<Element> backgrounds = [];

    public MenuDesign Result => working;

    /// <summary>For the off-screen snapshot (the bottom of the properties panel).</summary>
    public void ScrollProps(double? to = null) { UpdateLayout(); if (to is { } y) PropsScroll.ScrollToVerticalOffset(y); else PropsScroll.ScrollToEnd(); }

    public MenuEditorWindow(MenuDesign design, string? previewFrame, MenuContent content)
    {
        InitializeComponent();
        FontBox.ItemsSource = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList();
        StyleBox.ItemsSource = ElementLibrary.ButtonStyles.Select(b => new { b.Label, b.Value }).ToList();
        ColsBox.ItemsSource = new[] { 1, 2, 3, 4, 5 };
        RowsBox.ItemsSource = new[] { 1, 2, 3, 4 };
        working = design.Clone();
        this.content = content;
        LoadCatalog();
        CustomList.ItemsSource = Templates.List();
        Props.DataContext = working;

        timer.Tick += (_, _) => { timer.Stop(); _ = Render(); };
        Props.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Dirty()));
        Props.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Dirty()));
        Props.AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => Dirty()));
        Props.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Dirty()));
        Props.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Dirty()));
        Loaded += (_, _) => Rebind();
    }

    void Dirty() { timer.Stop(); timer.Start(); }

    void Rebind()
    {
        Props.DataContext = null;
        Props.DataContext = working;
        Look.IsEnabled = working.TemplateDir == null;
        LookNote.Visibility = Detach.Visibility = working.TemplateDir == null ? Visibility.Collapsed : Visibility.Visible;
        SelectBackground();
        Dirty();
    }

    void LoadCatalog()
    {
        var (usable, skipped) = TemplateCatalog.Scan();
        BuiltInList.ItemsSource = usable;
        LoadBackgrounds((usable, skipped));
        _ = FillThumbnails(usable);
    }

    void LoadBackgrounds((List<TemplateCatalog.Found>, List<TemplateCatalog.Skipped>)? scan = null)
    {
        backgrounds = ElementLibrary.Backgrounds(scan);
        BgList.ItemsSource = backgrounds;
        SelectBackground();
    }

    /// <summary>Shows which library picture the design uses (none selected = the gradient, or a picture that is not in the library).</summary>
    void SelectBackground()
    {
        var file = ElementLibrary.Resolve(working.BackgroundImage);
        syncing = true;
        BgList.SelectedItem = file == null ? null : backgrounds.FirstOrDefault(b => b.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
        BgRemove.IsEnabled = (BgList.SelectedItem as Element)?.Removable == true;
        syncing = false;
    }

    /// <summary>Templates BurnSmith draws itself have no picture file: draw them once in the background, then refresh the gallery.</summary>
    async Task FillThumbnails(List<TemplateCatalog.Found> usable)
    {
        if (!await Task.Run(() => TemplateCatalog.RenderThumbnails(usable))) return;
        BuiltInList.ItemsSource = TemplateCatalog.Scan().Usable;
    }

    void OnAddFolder(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Dossier contenant des modèles (DVDFab…)" };
        if (dlg.ShowDialog(this) != true) return;
        TemplateCatalog.AddFolder(dlg.FolderName);
        LoadCatalog();
    }

    static BitmapImage Decode(byte[] bytes, int width = 0)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) img.DecodePixelWidth = width;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze(); // built on a worker thread, shown on the UI thread
        return img;
    }

    /// <summary>The page picture and one focus frame per button. Call it on an STA thread (see <see cref="MenuRenderer.Sta{T}"/>).</summary>
    static PageImages DrawPage(MenuDesign d, MenuPage p)
    {
        var focus = new Dictionary<string, BitmapImage>();
        foreach (var b in p.Buttons) focus[b.Id] = Decode(MenuRenderer.Focus(d, b));
        return new PageImages(Decode(MenuRenderer.Background(d, p)), focus);
    }

    static string PageName(string id) => id switch
    {
        "root" => "Menu principal",
        "settings" => "Réglages",
        "credits" => "Crédits",
        _ when id.StartsWith("scenes") => "Scènes " + id[6..],
        _ => id,
    };

    async Task Render()
    {
        var d = working.Clone();
        var mine = ++version;
        var wanted = current?.Id ?? "root";
        var (audio, sub) = (audioSel, subSel);
        try
        {
            var (pages, page, images) = await Task.Run(() => MenuRenderer.Sta(() =>
            {
                var built = MenuBuilder.Build(d, content);
                var settings = built.FindIndex(p => p.Id == "settings");
                if (settings >= 0) built[settings] = MenuBuilder.Settings(d, content, audio, sub);
                var shown = built.FirstOrDefault(p => p.Id == wanted) ?? built[0];
                return (built, shown, DrawPage(d, shown));
            }));
            if (mine != version) return; // a newer edit is already rendering
            shot = new Shot(d, pages);
            shot.Cache[page.Id] = images;
            Show(page, images);
            error = null;
            Status.Text = "";
            _ = FillStrip(shot, mine);
        }
        catch (Exception ex)
        {
            if (mine != version) return;
            error = ex.Message;
            Status.Text = "Aperçu impossible : " + ex.Message;
        }
    }

    /// <summary>The whole theme at a glance: one small picture per page, drawn after the page on show so editing stays quick.</summary>
    async Task FillStrip(Shot s, int mine)
    {
        try
        {
            var slides = await Task.Run(() => MenuRenderer.Sta(() =>
                s.Pages.Select(p => new Slide(p.Id, PageName(p.Id), Decode(MenuRenderer.Background(s.Design, p), 224))).ToList()));
            if (mine != version) return;
            stripSync = true;
            PageStrip.ItemsSource = slides;
            PageStrip.SelectedItem = slides.FirstOrDefault(x => x.Id == current?.Id);
            stripSync = false;
        }
        catch (Exception) { /* the strip is a convenience: the page on show is what matters */ }
    }

    void Show(MenuPage page, PageImages images)
    {
        current = page;
        currentImages = images;
        if (page.Buttons.All(b => b.Id != focusId)) focusId = page.Buttons.FirstOrDefault()?.Id;
        var index = shot?.Pages.FindIndex(p => p.Id == page.Id) ?? 0;
        PageText.Text = $"{PageName(page.Id)} · page {index + 1} / {shot?.Pages.Count ?? 1}";
        if (PageStrip.ItemsSource is List<Slide> slides)
        {
            stripSync = true;
            PageStrip.SelectedItem = slides.FirstOrDefault(x => x.Id == page.Id);
            stripSync = false;
        }
        DrawStage();
    }

    /// <summary>The page as the disc shows it: its picture, the focus frame of the selected button, and a hit area per button for the mouse.</summary>
    void DrawStage()
    {
        StageCanvas.Children.Clear();
        if (current == null || currentImages == null) return;
        StageCanvas.Children.Add(new System.Windows.Controls.Image { Source = currentImages.Background, Width = MenuBuilder.W, Height = MenuBuilder.H });
        if (current.Buttons.FirstOrDefault(b => b.Id == focusId) is { } selected && currentImages.Focus.TryGetValue(selected.Id, out var frame))
        {
            var box = selected.Rect.Inflate(MenuBuilder.FocusMargin);
            var image = new System.Windows.Controls.Image { Source = frame, Width = box.W, Height = box.H, IsHitTestVisible = false };
            System.Windows.Controls.Canvas.SetLeft(image, box.X);
            System.Windows.Controls.Canvas.SetTop(image, box.Y);
            StageCanvas.Children.Add(image);
        }
        foreach (var b in current.Buttons)
        {
            var hit = new System.Windows.Shapes.Rectangle { Width = b.Rect.W, Height = b.Rect.H, Fill = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
            System.Windows.Controls.Canvas.SetLeft(hit, b.Rect.X);
            System.Windows.Controls.Canvas.SetTop(hit, b.Rect.Y);
            var id = b.Id;
            hit.MouseEnter += (_, _) => Select(id);
            hit.MouseLeftButtonUp += (_, _) => { Select(id); Activate(b); };
            StageCanvas.Children.Add(hit);
        }
    }

    void Select(string? id)
    {
        if (id == null || id == focusId || current?.Buttons.Any(b => b.Id == id) != true) return;
        focusId = id;
        DrawStage();
    }

    async void GoTo(string id, bool first = true)
    {
        if (shot is not { } s || s.Pages.FirstOrDefault(p => p.Id == id) is not { } page) return;
        if (first) focusId = null; // a page opens on its first button, like the disc
        var mine = version;
        try
        {
            if (!s.Cache.TryGetValue(id, out var images))
            {
                images = await Task.Run(() => MenuRenderer.Sta(() => DrawPage(s.Design, page)));
                s.Cache[id] = images;
            }
            if (mine != version || shot != s) return;
            Show(page, images);
        }
        catch (Exception ex)
        {
            Status.Text = "Aperçu impossible : " + ex.Message;
        }
    }

    /// <summary>What Enter does on the disc: a page opens, a playing button says what would play, audio / subtitle move the marker.</summary>
    async void Activate(MenuButton b)
    {
        var a = b.Action;
        switch (a.Kind)
        {
            case "page":
                GoTo(a.Page ?? "root");
                Hint.Text = "";
                break;
            case "play":
                Hint.Text = $"▶ Sur le disque : lecture du titre {a.Playlist}.";
                break;
            case "chapter":
                Hint.Text = $"▶ Sur le disque : lecture du titre {a.Playlist} à partir du repère {a.Mark}.";
                break;
            case "playall":
                Hint.Text = $"▶ Sur le disque : lecture de {a.Mark} titre(s) à la suite.";
                break;
            case "audio" or "sub":
                if (shot is not { } s) return;
                if (a.Kind == "audio") audioSel = a.Mark; else subSel = a.Mark;
                var at = s.Pages.FindIndex(p => p.Id == current?.Id);
                if (at < 0) return;
                var (audio, sub, design) = (audioSel, subSel, s.Design);
                var page = await Task.Run(() => MenuRenderer.Sta(() => MenuBuilder.Settings(design, content, audio, sub, current!.Id)));
                if (shot != s) return;
                s.Pages[at] = page;
                s.Cache.Remove(page.Id);
                GoTo(page.Id, false);
                break;
        }
    }

    void OnStageMouse(object s, System.Windows.Input.MouseButtonEventArgs e) => Stage.Focus();

    void OnStageKey(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (current?.Buttons.FirstOrDefault(b => b.Id == focusId) is not { } b) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Left: Select(b.Left); break;
            case System.Windows.Input.Key.Right: Select(b.Right); break;
            case System.Windows.Input.Key.Up: Select(b.Up); break;
            case System.Windows.Input.Key.Down: Select(b.Down); break;
            case System.Windows.Input.Key.Enter: Activate(b); break;
            default: return;
        }
        e.Handled = true;
    }

    void OnStripPick(object s, SelectionChangedEventArgs e)
    {
        if (!stripSync && PageStrip.SelectedItem is Slide slide) GoTo(slide.Id);
    }

    /// <summary>Applying a template changes the look but keeps the user's texts.</summary>
    void OnPick(object s, SelectionChangedEventArgs e)
    {
        var t = ((ListBox)s).SelectedItem switch { TemplateCatalog.Found f => f.Design, MenuDesign m => m, _ => null };
        if (t == null) return;
        working.TakeLook(t);
        Rebind();
    }

    void Step(int by)
    {
        if (shot == null) return;
        var i = shot.Pages.FindIndex(p => p.Id == current?.Id) + by;
        if (i >= 0 && i < shot.Pages.Count) GoTo(shot.Pages[i].Id);
    }

    void OnPrev(object s, RoutedEventArgs e) => Step(-1);
    void OnNext(object s, RoutedEventArgs e) => Step(1);

    void OnBgPick(object s, SelectionChangedEventArgs e)
    {
        if (syncing || BgList.SelectedItem is not Element picked) return;
        working.BackgroundImage = ElementLibrary.Reference(picked.Path);
        BgRemove.IsEnabled = picked.Removable;
    }

    /// <summary>Adds pictures to your library (a copy is kept in %APPDATA%\BurnSmith\library) and uses the last one.</summary>
    void OnBg(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|Tous les fichiers|*.*", Multiselect = true, Title = "Images à ajouter à la bibliothèque" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var added = dlg.FileNames.Select(ElementLibrary.Import).ToList();
            working.BackgroundImage = added[^1].Path;
            LoadBackgrounds();
            Dirty();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "BurnSmith");
        }
    }

    /// <summary>Picks the video or the sound of the moving menu; the files stay where they are (the path is stored).</summary>
    void Pick(string filter, string title, Action<string> set)
    {
        var dlg = new OpenFileDialog { Filter = filter + "|Tous les fichiers|*.*", Title = title };
        if (dlg.ShowDialog(this) != true) return;
        set(dlg.FileName);
        Rebind();
    }

    void OnLoopFile(object s, RoutedEventArgs e) =>
        Pick("Vidéos|*.mkv;*.mp4;*.avi;*.mov;*.m2ts;*.ts;*.wmv", "Vidéo du menu animé", f => working.LoopFile = f);

    void OnMusic(object s, RoutedEventArgs e) =>
        Pick("Sons|*.mp3;*.flac;*.wav;*.m4a;*.ogg;*.ac3;*.aac", "Musique du menu", f => working.Music = f);

    void OnBgRemove(object s, RoutedEventArgs e)
    {
        if (BgList.SelectedItem is not Element { Removable: true } mine) return;
        try
        {
            ElementLibrary.Remove(mine);
            working.BackgroundImage = null;
            LoadBackgrounds();
            Dirty();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "BurnSmith");
        }
    }

    void OnBgClear(object s, RoutedEventArgs e)
    {
        working.BackgroundImage = null;
        Rebind();
    }

    /// <summary>Leaves an installed template but keeps its background (by path, not copied): font, buttons, grid and colours become yours.</summary>
    void OnDetach(object s, RoutedEventArgs e)
    {
        var file = working.TemplateDir == null ? null : InstalledTemplate.Load(working.TemplateDir)?.BackgroundFile;
        working.TemplateDir = null;
        working.BackgroundImage = file == null ? null : ElementLibrary.Reference(file);
        Rebind();
    }

    void OnSave(object s, RoutedEventArgs e)
    {
        var name = Prompt.Ask(this, "Enregistrer le modèle", working.Name)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        working.Name = name;
        try
        {
            Templates.Save(working);
            CustomList.ItemsSource = Templates.List();
            Rebind();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "BurnSmith");
        }
    }

    void OnOk(object s, RoutedEventArgs e)
    {
        if (error != null) { MessageBox.Show(this, "Corrigez d'abord l'erreur de l'aperçu :\n" + error, "BurnSmith"); return; }
        DialogResult = true;
    }
}

/// <summary>Minimal "type a name" dialog.</summary>
static class Prompt
{
    public static string? Ask(Window owner, string title, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 320, Margin = new Thickness(0, 0, 0, 16) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90 };
        var win = new Window
        {
            Title = title, Owner = owner, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x14, 0x16)), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children = { box, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } },
            },
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }
}

/// <summary>A small decoded copy of a picture file for the gallery (the big one would be decoded for every row otherwise).</summary>
sealed class ThumbConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string file || !File.Exists(file)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(file);
            img.DecodePixelWidth = 192;
            img.CacheOption = BitmapCacheOption.OnLoad; // does not keep the file open: "Retirer" can delete it
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException) { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
