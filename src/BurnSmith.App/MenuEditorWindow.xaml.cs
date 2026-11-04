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
    int page, version;
    string? error;
    bool syncing;
    List<Element> backgrounds = [];

    public MenuDesign Result => working;

    /// <summary>For the off-screen snapshot (the bottom of the properties panel).</summary>
    public void ScrollPropsToEnd() { UpdateLayout(); PropsScroll.ScrollToEnd(); }

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
        var shipped = usable.Count(f => f.Design.Category == DvdForgeTemplates.Category || f.Design.Category.StartsWith("DVDStyler"));
        var installed = usable.Count - shipped;
        CatalogNote.Text = $"{usable.Count} modèles : {shipped} libres (DVD Forge, DVDStyler), {installed} DVDFab / Leawo."
            + (installed == 0 ? " Aucun modèle DVDFab / Leawo : cet exe a été construit sans eux et il n'y en a pas sur ce PC, indiquez le dossier de leurs modèles." : "")
            + (skipped.Count > 0 ? $" {skipped.Count} non pris en charge (détail au survol)." : "");
        CatalogNote.ToolTip = skipped.Count == 0 ? null : string.Join("\n", skipped.Select(s => $"{s.Name} : {s.Why}"));
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

    async Task Render()
    {
        var d = working.Clone();
        var mine = ++version;
        var wanted = page;
        try
        {
            var (jpeg, count, shown) = await Task.Run(() => MenuRenderer.Sta(() =>
            {
                var pages = MenuBuilder.Build(d, content);
                var settings = pages.FindIndex(p => p.Id == "settings");
                if (settings >= 0) pages[settings] = MenuBuilder.Settings(d, content, 1, 0); // the disc draws the marker live; the preview shows one
                var i = Math.Min(wanted, pages.Count - 1);
                return (MenuRenderer.Background(d, pages[i]), pages.Count, i);
            }));
            if (mine != version) return; // a newer edit is already rendering
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(jpeg);
            img.EndInit();
            img.Freeze();
            Preview.Source = img;
            page = shown;
            PageText.Text = $"Page {shown + 1} / {count}";
            error = null;
            Status.Text = "";
        }
        catch (Exception ex)
        {
            if (mine != version) return;
            error = ex.Message;
            Status.Text = "Aperçu impossible : " + ex.Message;
        }
    }

    /// <summary>Applying a template changes the look but keeps the user's texts.</summary>
    void OnPick(object s, SelectionChangedEventArgs e)
    {
        var t = ((ListBox)s).SelectedItem switch { TemplateCatalog.Found f => f.Design, MenuDesign m => m, _ => null };
        if (t == null) return;
        working.TakeLook(t);
        Rebind();
    }

    void OnPrev(object s, RoutedEventArgs e) { page = Math.Max(0, page - 1); Dirty(); }
    void OnNext(object s, RoutedEventArgs e) { page++; Dirty(); } // Render clamps to the last page

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
        Pick("Sons|*.mp3;*.flac;*.wav;*.m4a;*.ogg;*.ac3;*.aac", "Musique du menu animé", f => working.Music = f);

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
