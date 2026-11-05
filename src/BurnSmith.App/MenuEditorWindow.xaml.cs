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
    List<ButtonCard> buttons = [];
    object? pressed, dragging; // the library item under the mouse button, then the one being dragged to the page
    System.Windows.Point pressedAt;

    public MenuDesign Result => working;

    /// <summary>For the off-screen snapshot (the bottom of the properties panel on show).</summary>
    public void ScrollProps(double? to = null)
    {
        UpdateLayout();
        if (Props.SelectedContent is not ScrollViewer panel) return;
        if (to is { } y) panel.ScrollToVerticalOffset(y); else panel.ScrollToEnd();
    }

    /// <summary>For the off-screen snapshot: which tab of the library (0 templates, 1 mine, 2 backgrounds, 3 buttons) and of the properties (0 element, 1 menu, 2 animation, 3 look) is on show.</summary>
    public void ShowTabs(int? library, int? props, bool withButton = false)
    {
        if (library is { } l) Library.SelectedIndex = Math.Clamp(l, 0, Library.Items.Count - 1);
        if (withButton && buttons.Count > 0) AddLibraryButton(buttons[0].Item, new System.Windows.Point(MenuBuilder.W / 2.0, MenuBuilder.H * 0.75)); // as a double click on the first button of the library does
        if (props is { } p) Props.SelectedIndex = Math.Clamp(p, 0, Props.Items.Count - 1);
        UpdateLayout();
    }

    public MenuEditorWindow(MenuDesign design, string? previewFrame, MenuContent content)
    {
        InitializeComponent();
        FontBox.ItemsSource = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList();
        StyleBox.ItemsSource = ElementLibrary.ButtonStyles.Select(b => new { b.Label, b.Value }).ToList();
        HlBox.ItemsSource = new[] { new { Value = "box", Label = "Cadre (comme avant)" }, new { Value = "underline", Label = "Soulignement" }, new { Value = "textcolor", Label = "Texte en couleur" } };
        ColsBox.ItemsSource = new[] { 1, 2, 3, 4, 5 };
        RowsBox.ItemsSource = new[] { 1, 2, 3, 4 };
        working = design.Clone();
        this.content = content;
        LoadCatalog();
        CustomList.ItemsSource = Templates.List();
        Props.DataContext = working;
        InitElements();

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
        HlBox.SelectedValue = working.HighlightStyle;
        RefreshElemList();
        ShowElem();
        Dirty();
    }

    void LoadCatalog()
    {
        var (usable, skipped) = TemplateCatalog.Scan();
        BuiltInList.ItemsSource = usable;
        LoadBackgrounds((usable, skipped));
        LoadButtons((usable, skipped));
        _ = FillThumbnails(usable);
    }

    /// <summary>A list the library shows under one heading per source (BurnSmith's looks, yours, each template's own).</summary>
    static System.ComponentModel.ICollectionView BySource<T>(IEnumerable<T> items)
    {
        var view = new System.Windows.Data.ListCollectionView(items.ToList());
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Source"));
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
    }

    void LoadButtons((List<TemplateCatalog.Found>, List<TemplateCatalog.Skipped>)? scan = null)
    {
        var thumb = new ThumbConverter();
        buttons = ElementLibrary.Buttons(scan).Select(i => new ButtonCard(i, i.Normal == null ? null : thumb.Convert(i.Normal, typeof(System.Windows.Media.ImageSource), "", System.Globalization.CultureInfo.InvariantCulture) as System.Windows.Media.ImageSource)).ToList();
        BtnList.ItemsSource = BySource(buttons);
        _ = FillButtonSamples();
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
        _ when id.StartsWith("bonus") => "Bonus " + id[5..],
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
                if (settings >= 0) built[settings] = MenuBuilder.WithElements(d, MenuBuilder.Settings(d, content, audio, sub), content);
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
        DrawElements();
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
                var page = await Task.Run(() => MenuRenderer.Sta(() => MenuBuilder.WithElements(design, MenuBuilder.Settings(design, content, audio, sub, current!.Id), content)));
                if (shot != s) return;
                s.Pages[at] = page;
                s.Cache.Remove(page.Id);
                GoTo(page.Id, false);
                break;
        }
    }

    /// <summary>A click on the page that misses the selected element's outline and handles lets go of it, as in DVDStyler.</summary>
    void OnStageMouse(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        Stage.Focus();
        if (sel != null && (e.OriginalSource as FrameworkElement)?.Tag as string != "element") SelectElem(null);
    }

    // ---------------------------------------------------------------- free elements (DVDStyler-style placement)
    MenuElement? sel;
    bool elemSync;
    const int Handle = 30, MinW = 40, MinH = 20; // size of a resize handle on the 1920x1080 page, smallest element

    // a record, not a tuple: the combo binds to properties, and a tuple's fields are not (the page would show empty and fall back to "all")
    sealed record PageChoice(string Value, string Label);

    static readonly PageChoice[] PageChoices =
    [
        new("all", "Toutes les pages"), new("root", "Menu principal"), new("scenes", "Scènes"), new("bonus", "Bonus"), new("settings", "Réglages"), new("credits", "Crédits"),
    ];

    void InitElements()
    {
        ElemPage.ItemsSource = PageChoices;
        ElemPage.SelectedValuePath = "Value";
        ElemPage.DisplayMemberPath = "Label";
        ElemAlign.ItemsSource = new[] { "left", "center", "right" };
        ElemTarget.ItemsSource = new[] { "movie", "all", "root", "scenes1", "settings", "credits", "bonus1" };
        ElemLook.ItemsSource = new[] { new { Label = "(celui du thème)", Value = "" } }.Concat(ElementLibrary.ButtonStyles.Select(b => new { b.Label, b.Value })).ToList();
        RefreshElemList();
        ShowElem();
    }

    static string Describe(MenuElement e) => e.Kind switch
    {
        "button" => $"Bouton · {e.Page} · {(e.Text.Length > 0 ? e.Text : e.Image != null ? Path.GetFileName(e.Image) : "sans texte")} → {e.Target}",
        "image" => $"Image · {e.Page} · {Path.GetFileName(e.Image ?? "")}",
        "box" => $"Cadre · {e.Page} · {e.W}×{e.H}",
        _ => $"Texte · {e.Page} · {(e.Text.Length > 24 ? e.Text[..24] + "…" : e.Text)}",
    };

    void RefreshElemList()
    {
        elemSync = true;
        ElemList.ItemsSource = working.Elements.Select(Describe).ToList();
        ElemList.SelectedIndex = sel == null ? -1 : working.Elements.IndexOf(sel);
        elemSync = false;
    }

    /// <summary>Puts the selected element's values into the panel; the handlers stay quiet while it runs.</summary>
    void ShowElem()
    {
        elemSync = true;
        var on = sel != null;
        foreach (var c in new FrameworkElement[] { ElemPage, ElemText, ElemImage, ElemFocus, ElemLook, ElemCopy, ElemX, ElemY, ElemW, ElemH, ElemSize, ElemColor, ElemFill, ElemBorder, ElemBorderW, ElemOpacity, ElemAlign, ElemTarget, ElemBold, ElemItalic, ElemBehind })
            c.IsEnabled = on;
        if (sel is { } e)
        {
            ElemPage.SelectedValue = e.Page;
            ElemText.Text = e.Text;
            ElemImage.Text = e.Image ?? "";
            ElemFocus.Text = e.Focus ?? "";
            ElemLook.SelectedValue = e.Look;
            ElemX.Value = e.X;
            ElemY.Value = e.Y;
            ElemW.Value = Math.Clamp(e.W, 40, 1920);
            ElemH.Value = Math.Clamp(e.H, 20, 1080);
            ElemSize.Value = Math.Clamp(e.Size, 12, 160);
            ElemColor.Text = e.Color;
            ElemFill.Text = e.Fill;
            ElemBorder.Text = e.Border;
            ElemBorderW.Value = Math.Clamp(e.BorderWidth, 0, 16);
            ElemOpacity.Value = Math.Clamp(e.Opacity, 0, 100);
            ElemAlign.SelectedItem = e.Align;
            ElemTarget.SelectedItem = e.Target;
            ElemBold.IsChecked = e.Bold;
            ElemItalic.IsChecked = e.Italic;
            ElemBehind.IsChecked = e.Behind;
            ElemText.IsEnabled = e.Kind != "image";
        }
        elemSync = false;
    }

    /// <summary>Takes the panel's values into the selected element and asks for a new preview.</summary>
    void SyncElem()
    {
        if (elemSync || sel is not { } e) return;
        e.Page = ElemPage.SelectedValue as string ?? MenuElement.AllPages;
        e.Text = ElemText.Text;
        e.Image = string.IsNullOrWhiteSpace(ElemImage.Text) ? null : ElemImage.Text.Trim();
        e.Focus = string.IsNullOrWhiteSpace(ElemFocus.Text) ? null : ElemFocus.Text.Trim();
        e.Look = ElemLook.SelectedValue as string ?? "";
        e.X = (int)ElemX.Value;
        e.Y = (int)ElemY.Value;
        e.W = (int)ElemW.Value;
        e.H = (int)ElemH.Value;
        e.Size = (int)ElemSize.Value;
        e.Color = ElemColor.Text;
        e.Fill = ElemFill.Text;
        e.Border = ElemBorder.Text;
        e.BorderWidth = (int)ElemBorderW.Value;
        e.Opacity = (int)ElemOpacity.Value;
        e.Align = ElemAlign.SelectedItem as string ?? "left";
        e.Target = ElemTarget.SelectedItem as string ?? "movie";
        e.Bold = ElemBold.IsChecked == true;
        e.Italic = ElemItalic.IsChecked == true;
        e.Behind = ElemBehind.IsChecked == true;
        var at = working.Elements.IndexOf(e);
        if (at >= 0)
        {
            elemSync = true;
            ElemList.Items[at] = Describe(e);
            elemSync = false;
        }
        Dirty();
    }

    void SelectElem(MenuElement? e)
    {
        sel = e;
        elemSync = true;
        ElemList.SelectedIndex = e == null ? -1 : working.Elements.IndexOf(e);
        elemSync = false;
        if (e != null) Props.SelectedIndex = 0; // the element's own settings, wherever the panel was
        ShowElem();
        DrawStage();
    }

    void AddElem(MenuElement e)
    {
        working.Elements.Add(e);
        sel = e;
        RefreshElemList();
        ShowElem();
        Dirty();
    }

    void OnAddText(object s, RoutedEventArgs e) => AddElem(new MenuElement { Kind = "text", Page = current?.Id == "root" ? "root" : MenuElement.AllPages, Text = "Votre texte", X = 140, Y = 140, W = 700, H = 90, Size = 48 });

    void OnAddBox(object s, RoutedEventArgs e) => AddElem(new MenuElement { Kind = "box", Page = current?.Id == "root" ? "root" : MenuElement.AllPages, X = 120, Y = 780, W = 1680, H = 200, Opacity = 45, Radius = 24, BorderWidth = 0, Behind = true });

    void OnAddButton(object s, RoutedEventArgs e) => AddElem(new MenuElement { Kind = "button", Page = current?.Id == "root" ? "root" : MenuElement.AllPages, Text = "Bouton", Target = "movie", X = 300, Y = 620, W = 400, H = 110, Size = 44 });

    void OnAddImage(object s, RoutedEventArgs e) => AddElem(new MenuElement { Kind = "image", Page = MenuElement.AllPages, X = 1500, Y = 60, W = 300, H = 300, Fit = true, BorderWidth = 0 });

    void OnElemPick(object s, SelectionChangedEventArgs e)
    {
        if (elemSync) return;
        var at = ElemList.SelectedIndex;
        SelectElem(at >= 0 && at < working.Elements.Count ? working.Elements[at] : null);
    }

    void OnElemText(object s, TextChangedEventArgs e) => SyncElem();
    void OnElemSlide(object s, RoutedPropertyChangedEventArgs<double> e) => SyncElem();
    void OnElemPickEvent(object s, SelectionChangedEventArgs e) => SyncElem();
    void OnElemToggle(object s, RoutedEventArgs e) => SyncElem();

    void OnElemImage(object s, RoutedEventArgs e)
    {
        if (sel is not { } el) return;
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|Tous les fichiers|*.*", Title = "Image de l'élément" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            el.Image = ElementLibrary.Import(dlg.FileName).Path;
            ShowElem();
            Dirty();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "BurnSmith");
        }
    }

    /// <summary>The picture a button image shows while it is selected (its "sub" twin). Stored where it is: a file that moves falls back to the theme's frame.</summary>
    void OnElemFocus(object s, RoutedEventArgs e)
    {
        if (sel is not { } el) return;
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|Tous les fichiers|*.*", Title = "Image du bouton sélectionné" };
        if (dlg.ShowDialog(this) != true) return;
        el.Focus = dlg.FileName;
        ShowElem();
        Dirty();
    }

    void OnElemDuplicate(object s, RoutedEventArgs e)
    {
        if (sel is not { } el) return;
        var copy = el.Copy();
        copy.X = Math.Min(el.X + 40, MenuBuilder.W - 8);
        copy.Y = Math.Min(el.Y + 40, MenuBuilder.H - 8);
        AddElem(copy);
    }

    void OnElemDelete(object s, RoutedEventArgs e)
    {
        if (sel is not { } el) return;
        working.Elements.Remove(el);
        SelectElem(null);
        RefreshElemList();
        Dirty();
    }

    // ---------------------------------------------------------------- the library onto the page

    /// <summary>The page on show as an element's page: its kind ("scenes2" is "scenes"), so what is dropped belongs to that kind of page.</summary>
    string PageOfCurrent()
    {
        var kind = new string((current?.Id ?? "root").TakeWhile(char.IsAsciiLetter).ToArray());
        return kind is "root" or "scenes" or "bonus" or "settings" or "credits" ? kind : MenuElement.AllPages;
    }

    /// <summary>Sets a library picture as the page background. An installed template draws its own: the picture then replaces it, after a question.</summary>
    void ApplyBackground(Element picked)
    {
        if (working.TemplateDir != null)
        {
            if (MessageBox.Show(this, "Ce fond remplace celui du modèle : ses boutons et sa mise en page deviennent les vôtres (police, boutons, grille, couleurs).\n\nContinuer ?",
                                "Fond", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            working.TemplateDir = null;
        }
        working.BackgroundImage = ElementLibrary.Reference(picked.Path);
        Rebind();
    }

    /// <summary>A library button becomes a button element centred on <paramref name="at"/> (page coordinates): a picture button keeps its picture's proportions.</summary>
    void AddLibraryButton(ElementLibrary.ButtonItem item, System.Windows.Point at)
    {
        int w = 400, h = 110;
        var el = new MenuElement { Kind = "button", Page = PageOfCurrent(), Target = "movie", Size = 44 };
        if (item.Normal != null)
        {
            el.Image = ElementLibrary.Reference(item.Normal);
            el.Focus = item.Focus == null ? null : ElementLibrary.Reference(item.Focus);
            if (MenuRenderer.Size(item.Normal) is { } size)
            {
                var k = Math.Min(1.0, 600.0 / size.W); // a picture made for a 1080p page stays as it is, a bigger one is brought down
                w = Math.Max(60, (int)(size.W * k));
                h = Math.Max(30, (int)(size.H * k));
            }
        }
        else
        {
            el.Look = item.Style ?? "";
            el.Text = "Bouton";
        }
        el.W = w;
        el.H = h;
        el.X = (int)Math.Clamp(at.X - w / 2.0, 0, MenuBuilder.W - w);
        el.Y = (int)Math.Clamp(at.Y - h / 2.0, 0, MenuBuilder.H - h);
        AddElem(el);
    }

    void OnLibraryDown(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        pressedAt = e.GetPosition(null);
        pressed = e.OriginalSource is DependencyObject d && System.Windows.Controls.ItemsControl.ContainerFromElement((ListBox)s, d) is ListBoxItem item ? item.DataContext : null;
    }

    /// <summary>Pulling a library item away from the list starts a drag; letting it go on the page applies it.</summary>
    void OnLibraryMove(object s, System.Windows.Input.MouseEventArgs e)
    {
        if (pressed == null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { pressed = null; return; }
        var d = pressedAt - e.GetPosition(null);
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragging = pressed;
        pressed = null;
        try { DragDrop.DoDragDrop((DependencyObject)s, new DataObject("burnsmith-library", "item"), DragDropEffects.Copy); }
        finally { dragging = null; }
    }

    void OnStageDragOver(object s, DragEventArgs e)
    {
        e.Effects = dragging is Element or ButtonCard ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnStageDrop(object s, DragEventArgs e)
    {
        var at = e.GetPosition(StageCanvas);
        switch (dragging)
        {
            case Element picture: ApplyBackground(picture); break;
            case ButtonCard card: AddLibraryButton(card.Item, at); break;
        }
        e.Handled = true;
    }

    void OnBgDouble(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && System.Windows.Controls.ItemsControl.ContainerFromElement(BgList, d) is ListBoxItem { DataContext: Element picked }) ApplyBackground(picked);
    }

    void OnBtnDouble(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && System.Windows.Controls.ItemsControl.ContainerFromElement(BtnList, d) is ListBoxItem { DataContext: ButtonCard card })
            AddLibraryButton(card.Item, new System.Windows.Point(MenuBuilder.W / 2.0, MenuBuilder.H * 0.75));
    }

    void OnBtnPick(object s, SelectionChangedEventArgs e) => BtnRemove.IsEnabled = (BtnList.SelectedItem as ButtonCard)?.Item.Removable == true;

    /// <summary>Adds a button picture to your library: the picture, then (it may be left out) the one it shows when selected.</summary>
    void OnBtnImport(object s, RoutedEventArgs e)
    {
        var filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|Tous les fichiers|*.*";
        var normal = new OpenFileDialog { Filter = filter, Title = "Image du bouton" };
        if (normal.ShowDialog(this) != true) return;
        var focus = new OpenFileDialog { Filter = filter, Title = "Image du bouton sélectionné (Annuler = aucune : le cadre du thème)" };
        try
        {
            ElementLibrary.ImportButton(normal.FileName, focus.ShowDialog(this) == true ? focus.FileName : null);
            LoadButtons();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "BurnSmith");
        }
    }

    void OnBtnRemove(object s, RoutedEventArgs e)
    {
        if (BtnList.SelectedItem is not ButtonCard { Item.Removable: true } card) return;
        try
        {
            ElementLibrary.RemoveButton(card.Item);
            LoadButtons();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "BurnSmith");
        }
    }

    // A drag on the page: the element, which part of it was grabbed (0 = the body moves it, 1..4 = a corner resizes it: NW, NE, SW, SE),
    // where the mouse was, and the element's rectangle at that moment. The mouse is captured by the canvas, because the page is redrawn
    // (and the shape under the mouse replaced) as soon as the element is selected.
    (MenuElement El, int Part, System.Windows.Point Start, int X, int Y, int W, int H)? drag;
    bool dragMoved;

    /// <summary>The element outlines on the page on show: click one to edit it, drag it to move it, drag a corner of the selected one to resize it.</summary>
    void DrawElements()
    {
        if (current == null) return;
        var accent = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(working.Accent));
        var clear = System.Windows.Media.Brushes.Transparent;
        var here = working.Elements.Where(x => x.AppliesTo(current.Id)).ToList();
        foreach (var el in here.Where(x => x != sel).Concat(here.Where(x => x == sel))) // the selected one last, so its corners are the ones that get the click
        {
            var box = el.Box();
            Piece(el, 0, box.X, box.Y, box.W, box.H, clear, accent, el == sel ? 5 : 2, System.Windows.Input.Cursors.SizeAll);
            if (el != sel) continue;
            var h = Math.Min(Handle, Math.Min(box.W, box.H) / 3);
            var right = box.X + box.W - h;
            var bottom = box.Y + box.H - h;
            Piece(el, 1, box.X, box.Y, h, h, accent, clear, 0, System.Windows.Input.Cursors.SizeNWSE);
            Piece(el, 2, right, box.Y, h, h, accent, clear, 0, System.Windows.Input.Cursors.SizeNESW);
            Piece(el, 3, box.X, bottom, h, h, accent, clear, 0, System.Windows.Input.Cursors.SizeNESW);
            Piece(el, 4, right, bottom, h, h, accent, clear, 0, System.Windows.Input.Cursors.SizeNWSE);
        }
    }

    void Piece(MenuElement el, int part, int x, int y, int w, int h, System.Windows.Media.Brush fill, System.Windows.Media.Brush stroke, double thickness, System.Windows.Input.Cursor cursor)
    {
        var shape = new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = fill, Stroke = stroke, StrokeThickness = thickness, Cursor = cursor, Tag = "element" };
        if (part == 0) shape.StrokeDashArray = [6, 4];
        System.Windows.Controls.Canvas.SetLeft(shape, x);
        System.Windows.Controls.Canvas.SetTop(shape, y);
        shape.MouseLeftButtonDown += (_, me) =>
        {
            Stage.Focus();
            drag = (el, part, me.GetPosition(StageCanvas), el.X, el.Y, el.W, el.H);
            dragMoved = false;
            if (el != sel) SelectElem(el); // redraws the page: this shape is gone, the canvas keeps the mouse
            StageCanvas.CaptureMouse();
            me.Handled = true;
        };
        StageCanvas.Children.Add(shape);
    }

    void OnCanvasMove(object s, System.Windows.Input.MouseEventArgs e)
    {
        if (drag is not { } d || !StageCanvas.IsMouseCaptured) return;
        var at = e.GetPosition(StageCanvas);
        int dx = (int)(at.X - d.Start.X), dy = (int)(at.Y - d.Start.Y);
        var el = d.El;
        if (d.Part == 0)
        {
            el.X = Math.Clamp(d.X + dx, 0, MenuBuilder.W - 8);
            el.Y = Math.Clamp(d.Y + dy, 0, MenuBuilder.H - 8);
        }
        else
        {
            int left = d.X, top = d.Y, right = d.X + d.W, bottom = d.Y + d.H;
            if (d.Part is 1 or 3) left = Math.Clamp(left + dx, 0, right - MinW); else right = Math.Clamp(right + dx, left + MinW, MenuBuilder.W);
            if (d.Part is 1 or 2) top = Math.Clamp(top + dy, 0, bottom - MinH); else bottom = Math.Clamp(bottom + dy, top + MinH, MenuBuilder.H);
            (el.X, el.Y, el.W, el.H) = (left, top, right - left, bottom - top);
        }
        dragMoved = true;
        ShowElem();
        DrawStage();
    }

    /// <summary>The preview is redrawn when the mouse is let go (a click that did not move anything costs nothing).</summary>
    void OnCanvasUp(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (drag == null) return;
        StageCanvas.ReleaseMouseCapture();
    }

    void OnCanvasLost(object s, System.Windows.Input.MouseEventArgs e)
    {
        var moved = dragMoved;
        drag = null;
        dragMoved = false;
        if (moved) { RefreshElemList(); Dirty(); }
    }

    /// <summary>Keys on the page: with an element selected, Delete removes it, Escape lets it go and the arrows nudge it (10 px, Shift 1 px); otherwise the arrows and Enter walk the buttons like a remote.</summary>
    void OnStageKey(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (sel is { } el)
        {
            var step = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 ? 1 : 10;
            switch (e.Key)
            {
                case System.Windows.Input.Key.Delete: OnElemDelete(s, new RoutedEventArgs()); break;
                case System.Windows.Input.Key.Escape: SelectElem(null); break;
                case System.Windows.Input.Key.Left: Nudge(el, -step, 0); break;
                case System.Windows.Input.Key.Right: Nudge(el, step, 0); break;
                case System.Windows.Input.Key.Up: Nudge(el, 0, -step); break;
                case System.Windows.Input.Key.Down: Nudge(el, 0, step); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
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

    void Nudge(MenuElement el, int dx, int dy)
    {
        var box = el.Box();
        el.X = Math.Clamp(el.X + dx, 0, MenuBuilder.W - box.W);
        el.Y = Math.Clamp(el.Y + dy, 0, MenuBuilder.H - box.H);
        ShowElem();
        DrawStage();
        Dirty();
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

    /// <summary>Which of DVDFab's three highlight styles marks the chosen button (a template with its own artwork keeps it).</summary>
    void OnPickHighlight(object s, SelectionChangedEventArgs e)
    {
        if (HlBox.SelectedValue is not string style) return;
        working.HighlightStyle = style;
        Dirty();
    }

    /// <summary>A click on a background only selects it (so it can be removed); a double click or a drop on the page applies it.</summary>
    void OnBgPick(object s, SelectionChangedEventArgs e) => BgRemove.IsEnabled = (BgList.SelectedItem as Element)?.Removable == true;

    /// <summary>Adds pictures to your library (a copy is kept in %APPDATA%\BurnSmith\library) and uses the last one.</summary>
    void OnBg(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|Tous les fichiers|*.*", Multiselect = true, Title = "Images à ajouter à la bibliothèque" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var added = dlg.FileNames.Select(ElementLibrary.Import).ToList();
            LoadBackgrounds();
            ApplyBackground(added[^1]);
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

    /// <summary>A picture for the kind of page on show ("scenes" for every scenes page); a template's own background gives way to it.</summary>
    void OnPageBg(object s, RoutedEventArgs e)
    {
        if (current == null) return;
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|Tous les fichiers|*.*", Title = $"Fond de la page « {PageName(current.Id)} »" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            working.PageBackgrounds[MenuDesign.PageKind(current.Id)] = ElementLibrary.Reference(ElementLibrary.Import(dlg.FileName).Path);
            Dirty();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "BurnSmith");
        }
    }

    void OnPageBgClear(object s, RoutedEventArgs e)
    {
        if (current == null || !working.PageBackgrounds.Remove(MenuDesign.PageKind(current.Id))) return;
        Dirty();
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
