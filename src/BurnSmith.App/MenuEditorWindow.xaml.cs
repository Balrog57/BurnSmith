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
/// builders use, so it shows the real page, with the pictures of the disc's own videos as they arrive (see <see cref="UseContent"/>).
/// </summary>
public partial class MenuEditorWindow : Window
{
    MenuDesign working; // replaced by undo / redo
    MenuContent content;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    int version;
    string? error;

    /// <summary>The pages built for one state of the design; the pictures of a page are drawn when it is first shown.</summary>
    sealed class Shot(MenuDesign design, List<MenuPage> pages)
    {
        public MenuDesign Design { get; } = design;
        public List<MenuPage> Pages { get; } = pages;
        public Dictionary<string, PageImages> Cache { get; } = [];
    }

    sealed record PageImages(BitmapImage Background, Dictionary<string, BitmapImage> Focus);
    public sealed record Slide(string Id, string Name, BitmapImage Thumb)
    {
        public override string ToString() => Name; // what a screen reader says for a list item
    }

    Shot? shot;
    MenuPage? current;
    PageImages? currentImages;
    string? focusId;
    int navigationRequest;
    int trackRequest;
    int? previewPlaying;
    bool previewChain;
    int audioSel = 1, subSel; // what the settings page marks while previewing (audio stream number 1-based, subtitle 0 = off)
    bool stripSync;
    List<Element> backgrounds = [];
    List<ButtonCard> buttons = [];
    object? pressed, dragging; // the library item under the mouse button, then the one being dragged to the page
    System.Windows.Point pressedAt;

    public MenuDesign Result => working;
    internal bool IsPreviewingPopup => current?.Id.StartsWith("pop", StringComparison.Ordinal) == true;
    internal string PreviewStatus => Status.Text;

    /// <summary>For the off-screen snapshot (the bottom of the properties panel on show).</summary>
    public void ScrollProps(double? to = null)
    {
        UpdateLayout();
        var panel = ElementInspectorPanel.Visibility == Visibility.Visible ? ElementInspectorPanel
            : MenuSettingsPanel.Visibility == Visibility.Visible ? MenuSettingsPanel
            : Props.SelectedContent as ScrollViewer;
        if (panel == null) return;
        if (to is { } y) panel.ScrollToVerticalOffset(y); else panel.ScrollToEnd();
    }

    /// <summary>For the off-screen snapshot: the menu's language chosen in its list, as a click does ("fr" or "en").</summary>
    public void PickLanguage(string language) => LanguageBox.SelectedValue = language;

    /// <summary>For the off-screen snapshot: which step is on show (0 Modèle, 1 Personnaliser, 2 Aperçu) and optional drawer (tab=1 buttons, tab=2 backgrounds).</summary>
    public void ShowTabs(int? step, int? tab, bool withButton = false)
    {
        if (step is { } st) ShowStep(st > 2 ? 2 : st);
        if (withButton && buttons.Count > 0) AddLibraryButton(buttons[0].Item, new System.Windows.Point(MenuBuilder.W / 2.0, MenuBuilder.H * 0.75));
        if (tab == 1) { drawerKind = "btn"; ShowStep(1); }
        else if (tab == 2) { drawerKind = "bg"; ShowStep(1); }
        ShowRightPanel();
        UpdateLayout();
    }

    public MenuEditorWindow(MenuDesign design, string? previewFrame, MenuContent content)
    {
        InitializeComponent();
        FontBox.ItemsSource = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList();
        StyleBox.ItemsSource = ElementLibrary.ButtonStyles.Select(b => new { b.Label, b.Value }).ToList();
        LanguageBox.ItemsSource = new[] { new { Label = "Français", Value = "fr" }, new { Label = "Anglais", Value = "en" } };
        HlBox.ItemsSource = new[] { new { Value = "box", Label = "Cadre (comme avant)" }, new { Value = "underline", Label = "Soulignement" }, new { Value = "textcolor", Label = "Texte en couleur" } };
        ColsBox.ItemsSource = new[] { 1, 2, 3, 4, 5 };
        RowsBox.ItemsSource = new[] { 1, 2, 3, 4 };
        working = design.Clone();
        this.content = baseContent = content;
        LoadCatalog();
        CustomList.ItemsSource = Templates.List();
        Props.DataContext = working;
        InitElements();

        timer.Tick += (_, _) => { timer.Stop(); _ = Render(); };
        Props.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Dirty()));
        // a list of the library or a tab picked changes nothing of the menu by itself: only what is then done with it
        Props.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) => { if (e.OriginalSource is not (ListBox or TabControl)) Dirty(); }));
        Props.AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => Dirty()));
        Props.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Dirty()));
        Props.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Dirty()));
        ShowStep(0);
        Loaded += (_, _) => { PlayModeBox.SelectedIndex = Playback.Mode; Rebind(); };
        Loaded += (_, _) =>
        {
            PreviewPopupButton.Visibility = CanPreviewPopup ? Visibility.Visible : Visibility.Collapsed;
            PreviewStartButton.Visibility = PreviewEndButton.Visibility = DvdPreview || Hdmv ? Visibility.Visible : Visibility.Collapsed;
            if (!CanPreviewPopup) PopupBox.IsEnabled = false;
            if (!Hdmv) return;
            foreach (var box in new[] { PopupBox, LoopBox }) // kept in the design (a BD-J disc of the same menu has them), only not offered
            {
                box.IsEnabled = false;
                box.ToolTip = "Pas avec un menu HDMV (Paramètres avancés, Type de menu)";
                ToolTipService.SetShowOnDisabled(box, true);
            }
        };
    }

    /// <summary>The disc gets a native (HDMV) menu: what it cannot do (see <see cref="BurnSmith.Core.HdmvMenu.LeftOut"/>) is greyed.</summary>
    public bool Hdmv { get; init; }
    public bool CanPreviewPopup { get; init; }
    public bool DvdPreview { get; init; }

    MenuPage SettingsPreview(MenuDesign design, MenuContent facts, string id, int audio, int subtitle) =>
        DvdPreview ? Dvd.PreviewSettings(design, facts, id, audio, subtitle) with { Id = id }
            : MenuBuilder.WithElements(design, MenuBuilder.Settings(design, facts, audio, subtitle, id), facts);

    void Dirty() { timer.Stop(); timer.Start(); }

    // ---------------------------------------------------------------- undo / redo
    // one step per state the preview shows: the preview waits for a 250 ms pause, so a drag or a word typed is one step, not one per pixel or letter.
    readonly Stack<(MenuDesign Design, Dictionary<int, string> Renamed, string Json)> undo = new(), redo = new();
    (MenuDesign Design, Dictionary<int, string> Renamed, string Json)? kept; // the state last shown
    MenuContent baseContent;

    /// <summary>The design and video renames as they stand become a step when they differ from the last one.</summary>
    void Remember()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { working, renamed });
        if (kept?.Json == json) return;
        if (kept is { } previous) { undo.Push(previous); redo.Clear(); }
        kept = (working.Clone(), new Dictionary<int, string>(renamed), json);
        UndoButton.IsEnabled = undo.Count > 0;
        RedoButton.IsEnabled = redo.Count > 0;
    }

    void Step(Stack<(MenuDesign Design, Dictionary<int, string> Renamed, string Json)> from, Stack<(MenuDesign Design, Dictionary<int, string> Renamed, string Json)> to)
    {
        if (editing != null) return; // a zone typed on the page: Escape or Enter first
        timer.Stop();
        Remember();
        if (from.Count == 0 || kept is not { } now) return;
        to.Push(now);
        kept = from.Pop();
        working = kept.Value.Design.Clone();
        renamed.Clear();
        foreach (var kv in kept.Value.Renamed) renamed[kv.Key] = kv.Value;
        content = Named(baseContent);
        sel = null; // the elements are copies now
        UndoButton.IsEnabled = undo.Count > 0;
        RedoButton.IsEnabled = redo.Count > 0;
        Rebind();
        FillZonePanel(force: true);
    }

    /// <summary>Undo the last change (Ctrl+Z).</summary>
    public void Undo() => Step(undo, redo);

    /// <summary>Redo what was undone (Ctrl+Y or Ctrl+Maj+Z).</summary>
    public void Redo() => Step(redo, undo);

    void OnUndo(object s, RoutedEventArgs e) => Undo();
    void OnRedo(object s, RoutedEventArgs e) => Redo();

    // the editor's own undo, also in a text box: the box's would undo letters the design no longer has after a step
    void OnWindowKey(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers is not (System.Windows.Input.ModifierKeys.Control or (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))) return;
        var shift = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
        if (e.Key == System.Windows.Input.Key.Z && !shift) Undo();
        else if (e.Key == System.Windows.Input.Key.Y && !shift || e.Key == System.Windows.Input.Key.Z && shift) Redo();
        else return;
        e.Handled = true;
    }

    bool sceneModeSync;

    void Rebind()
    {
        Props.DataContext = null;
        Props.DataContext = working;
        Look.IsEnabled = GridLook.IsEnabled = working.TemplateDir == null;
        GridNote.Visibility = working.TemplateDir == null ? Visibility.Collapsed : Visibility.Visible;
        LookNote.Visibility = Detach.Visibility = working.TemplateDir == null ? Visibility.Collapsed : Visibility.Visible;
        SelectBackground();
        HlBox.SelectedValue = working.HighlightStyle;
        LanguageBox.SelectedValue = working.ButtonLanguage;
        sceneModeSync = true;
        SceneModeBox.SelectedIndex = working.ScenesOnly ? 3 : working.SceneList ? 2 : working.Thumbnails ? 0 : 1;
        sceneModeSync = false;
        RefreshElemList();
        ShowElem();
        Dirty();
    }

    void OnPickSceneMode(object s, SelectionChangedEventArgs e)
    {
        if (sceneModeSync || SceneModeBox.SelectedIndex < 0) return;
        switch (SceneModeBox.SelectedIndex)
        {
            case 0: working.Thumbnails = true; working.SceneList = false; working.ScenesOnly = false; break;
            case 1: working.Thumbnails = false; working.SceneList = false; working.ScenesOnly = false; break;
            case 2: working.Thumbnails = false; working.SceneList = true; working.ScenesOnly = false; break;
            case 3: working.Thumbnails = true; working.SceneList = false; working.ScenesOnly = true; break;
        }
        Dirty();
    }

    static readonly string[] PaletteSwatches =
    [
        "#FFFFFF", "#F4EFE6", "#D6DCE5", "#8B95A5", "#2B303A", "#14161B", "#08090C", "#000000",
        "#E50914", "#C93B47", "#E67E22", "#F1C40F", "#2ECC71", "#1ABC9C", "#3498DB", "#9B59B6",
        "#7F1D1D", "#78350F", "#1E3A5F", "#0F2942", "#1B4332", "#312E81", "#4C1D95", "#27272A",
    ];

    void OnSwatchClick(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (s is not System.Windows.Controls.Border swatch || swatch.Parent is not DockPanel row) return;
        var box = row.Children.OfType<TextBox>().FirstOrDefault();
        if (box == null || !box.IsEnabled) return;
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = swatch,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 8 };
        foreach (var hex in PaletteSwatches)
        {
            var btn = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(2), Padding = new Thickness(0),
                Background = new System.Windows.Media.BrushConverter().ConvertFromString(hex) as System.Windows.Media.Brush,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                ToolTip = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btn.Click += (_, _) =>
            {
                box.Text = hex;
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                popup.IsOpen = false;
            };
            grid.Children.Add(btn);
        }
        var frame = new System.Windows.Controls.Border
        {
            Background = TryFindResource("SurfaceBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
            BorderBrush = TryFindResource("OutlineBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Child = grid,
        };
        popup.Child = frame;
        popup.IsOpen = true;
        e.Handled = true;
    }

    void LoadCatalog()
    {
        var (usable, skipped) = TemplateCatalog.Scan();
        FillFamilies(usable);
        LoadBackgrounds((usable, skipped));
        LoadButtons((usable, skipped));
        _ = FillThumbnails(usable);
    }

    /// <summary>A list the library shows under one heading per source (BurnSmith's looks, yours, each template's own).</summary>
    static System.ComponentModel.ICollectionView BySource<T>(IEnumerable<T> items, string by = "Source")
    {
        var view = new System.Windows.Data.ListCollectionView(items.ToList());
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(by));
        return view;
    }

    void LoadBackgrounds((List<TemplateCatalog.Found>, List<TemplateCatalog.Skipped>)? scan = null)
    {
        backgrounds = ElementLibrary.Backgrounds(scan);
        BgList.ItemsSource = BySource(backgrounds);
        SelectBackground();
    }

    /// <summary>One button of the library as the list shows it: the picture, or a sample of the look BurnSmith draws.</summary>
    public sealed class ButtonCard(ElementLibrary.ButtonItem item, System.Windows.Media.ImageSource? sample) : System.ComponentModel.INotifyPropertyChanged
    {
        System.Windows.Media.ImageSource? sample = sample;
        public ElementLibrary.ButtonItem Item { get; } = item;
        public string Name => Item.Name;
        public string Source => Item.Source;
        public System.Windows.Media.ImageSource? Sample
        {
            get => sample;
            set { sample = value; PropertyChanged?.Invoke(this, new(nameof(Sample))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Name; // what a screen reader says for a list item
    }

    void LoadButtons((List<TemplateCatalog.Found>, List<TemplateCatalog.Skipped>)? scan = null)
    {
        buttons = ElementLibrary.StyleButtons().Select(i => new ButtonCard(i, null)).ToList();
        BtnList.ItemsSource = BySource(buttons);
        _ = FillButtonSamples();
        _ = FillButtonPictures(scan);
    }

    int buttonLoad;

    /// <summary>
    /// The picture buttons of the library are hundreds of traced drawings to read (seconds of work): they join the list from behind the window,
    /// so the editor and its preview open at once.
    /// </summary>
    async Task FillButtonPictures((List<TemplateCatalog.Found>, List<TemplateCatalog.Skipped>)? scan)
    {
        var mine = ++buttonLoad;
        try
        {
            var cards = await Task.Run(() => MenuRenderer.Sta(() =>
            {
                var thumb = new ThumbConverter();
                return ElementLibrary.Buttons(scan).Where(i => i.Style == null)
                    .Select(i => new ButtonCard(i, thumb.Convert(i.Normal!, typeof(System.Windows.Media.ImageSource), "", System.Globalization.CultureInfo.InvariantCulture) as System.Windows.Media.ImageSource)).ToList();
            }));
            if (mine != buttonLoad) return; // the library was read again meanwhile
            buttons = [.. buttons.Where(c => c.Item.Style != null), .. cards];
            BtnList.ItemsSource = BySource(buttons);
        }
        catch (Exception ex) { AppFiles.LogError("Boutons de la bibliothèque", ex); } // the looks BurnSmith draws stay listed
    }

    /// <summary>The looks BurnSmith draws have no picture file: one sample of each is drawn (in this design's colours) behind the list.</summary>
    async Task FillButtonSamples()
    {
        var design = working.Clone();
        design.TemplateDir = null;
        design.BackgroundImage = null;
        var cards = buttons.Where(c => c.Item.Style != null).ToList();
        try
        {
            var samples = await Task.Run(() => MenuRenderer.Sta(() => cards.Select(c =>
            {
                var sample = new MenuElement { Kind = "button", Look = c.Item.Style!, Text = "Bouton", X = 200, Y = 280, W = 1520, H = 520, Size = 150 };
                return Decode(MenuRenderer.Background(design, new MenuPage("sample", sample.Ops(design).ToList(), [])), 192);
            }).ToList()));
            for (var i = 0; i < cards.Count; i++) cards[i].Sample = samples[i];
        }
        catch (Exception) { /* the list still names the looks: the samples are a convenience */ }
    }

    /// <summary>Shows which library picture the design uses (none selected = the gradient, or a picture that is not in the library).</summary>
    void SelectBackground()
    {
        var file = ElementLibrary.Resolve(working.BackgroundImage);
        BgList.SelectedItem = file == null ? null : backgrounds.FirstOrDefault(b => b.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
        BgRemove.IsEnabled = (BgList.SelectedItem as Element)?.Removable == true;
    }

    /// <summary>Templates BurnSmith draws itself have no picture file: draw them once in the background, then refresh the gallery.</summary>
    async Task FillThumbnails(List<TemplateCatalog.Found> usable)
    {
        if (!await Task.Run(() => TemplateCatalog.RenderThumbnails(usable))) return;
        FillFamilies(TemplateCatalog.Scan().Usable);
    }

    /// <summary>
    /// Adds a folder of templates and converts them at once to the single pack format (template.json + PNG pictures in
    /// Documents\BurnSmith\Templates\converted); the exe already carries its own templates converted. A pack that already
    /// exists is kept, so the user's own edits are never overwritten.
    /// </summary>
    async void OnAddFolder(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Dossier contenant des modèles de menu") };
        if (dlg.ShowDialog(this) != true) return;
        TemplateCatalog.AddFolder(dlg.FolderName);
        ((Button)s).IsEnabled = false;
        ConvertNote.Text = "Conversion des modèles…";
        var progress = new Progress<string>(line => ConvertNote.Text = line);
        try
        {
            var result = await Task.Run(() => TemplatePack.ConvertAll(line => ((IProgress<string>)progress).Report(line)));
            LoadCatalog();
            CustomList.ItemsSource = Templates.List();
            ConvertNote.Text = $"{result.Converted} converti(s), {result.Skipped} déjà présent(s), {result.Failed} échec(s) — dossier Documents\\BurnSmith\\Templates\\converted";
            if (result.Failed > 0)
                MessageBox.Show(this, string.Join(Environment.NewLine, result.Messages.Take(10)), "Modèles non convertis");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            ConvertNote.Text = "";
            MessageBox.Show(this, ex.Message, "BurnSmith");
        }
        finally { ((Button)s).IsEnabled = true; }
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
        if (p.Id.StartsWith("pop", StringComparison.Ordinal))
        {
            var picture = d.Poster;
            d = MenuBuilder.PopupDesign(d);
            d.Poster = picture;
        }
        var focus = new Dictionary<string, BitmapImage>();
        foreach (var b in p.Buttons) focus[b.Id] = Decode(MenuRenderer.Focus(d, b, written: p.Ops.OfType<BurnSmith.Core.Menu.Label>().FirstOrDefault(l => l.Rect == b.Rect)));
        return new PageImages(Decode(MenuRenderer.Background(d, p)), focus);
    }

    string PageName(string id) => PageName(working, id);

    static string PageName(MenuDesign design, string id) => design.CustomPages.FirstOrDefault(page => page.Id == id)?.Title ?? (id switch
    {
        "root" => "Menu principal",
        "settings" => "Réglages",
        "settings_audio" => "Réglages · Audio",
        "settings_sub" => "Réglages · Sous-titres",
        "credits" => "Crédits",
        _ when id.StartsWith("scenes") => "Chapitres " + id[6..],
        _ when id.StartsWith("bonus") => "Bonus " + id[5..],
        _ => id,
    });

    async Task Render()
    {
        Remember();
        var d = working.Clone();
        var mine = ++version;
        var wanted = current?.Id ?? "root";
        var (audio, sub, facts) = (audioSel, subSel, content with { PlainFocus = DvdPreview });
        try
        {
            var (pages, page, images) = await Task.Run(() => MenuRenderer.Sta(() =>
            {
                var built = MenuBuilder.Build(d, facts);
                foreach (var id in MenuBuilder.SettingsIds(d, facts)) // une page ou deux, selon le modèle : chacune marque le choix en cours
                    if (built.FindIndex(p => p.Id == id) is >= 0 and var at) built[at] = SettingsPreview(d, facts, id, audio, sub);
                if (CanPreviewPopup && d.Popup) built.AddRange(MenuBuilder.Popup(d, facts, facts.PopupTitles, audio, sub));
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
                s.Pages.Where(p => !p.Id.StartsWith("pop", StringComparison.Ordinal))
                    .Select(p => new Slide(p.Id, PageName(s.Design, p.Id), Decode(MenuRenderer.Background(s.Design, p), 224))).ToList()));
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
        RenameSubmenu.Visibility = RemoveSubmenu.Visibility = working.CustomPages.Any(custom => custom.Id == page.Id)
            ? Visibility.Visible : Visibility.Collapsed;
        currentImages = images;
        FillZonePanel();
        if (page.Buttons.All(b => b.Id != focusId)) focusId = page.Buttons.FirstOrDefault()?.Id;
        var mainPages = shot?.Pages.Where(p => !p.Id.StartsWith("pop", StringComparison.Ordinal)).ToList() ?? [];
        PageText.Text = IsPreviewingPopup ? $"Pop-up · titre {previewPlaying}" :
            $"{PageName(page.Id)} · page {mainPages.FindIndex(p => p.Id == page.Id) + 1} / {mainPages.Count}";
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
        if (editing != null) return; // a redraw would take the text box away from under the typing: the end of the edit redraws
        StageCanvas.Children.Clear();
        if (current == null || currentImages == null) return;
        StageCanvas.Children.Add(new System.Windows.Controls.Image { Source = currentImages.Background, Width = MenuBuilder.W, Height = MenuBuilder.H });
        if (current.Buttons.FirstOrDefault(b => b.Id == focusId) is { } selected && currentImages.Focus.TryGetValue(selected.Id, out var frame))
        {
            var box = MenuRenderer.FocusBox(selected);
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
        if (SafeToggle.IsChecked == true) DrawSafe();
        if (Previewing) return; // the page as the disc shows it: nothing to grab
        foreach (var z in current.Zones) DrawZone(z);
        DrawElements();
        DrawMarks();
    }

}
