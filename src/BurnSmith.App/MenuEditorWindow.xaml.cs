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

    public MenuDesign Result => working;

    public MenuEditorWindow(MenuDesign design, string? previewFrame, MenuContent content)
    {
        InitializeComponent();
        working = design.Clone();
        this.content = content;
        LoadCatalog();
        CustomList.ItemsSource = Templates.List();
        Props.DataContext = working;

        timer.Tick += (_, _) => { timer.Stop(); _ = Render(); };
        Props.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Dirty()));
        Props.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Dirty()));
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
        LookNote.Visibility = working.TemplateDir == null ? Visibility.Collapsed : Visibility.Visible;
        Dirty();
    }

    void LoadCatalog()
    {
        var (usable, skipped) = TemplateCatalog.Scan();
        BuiltInList.ItemsSource = usable;
        CatalogNote.Text = usable.Count == 0
            ? "Aucun modèle trouvé. DVDFab n'est pas détecté : indiquez son dossier (ex. DVDFab13\\author)."
            : $"{usable.Count} modèle(s) lus depuis votre installation DVDFab." + (skipped.Count > 0 ? $" {skipped.Count} non pris en charge : {string.Join(", ", skipped.Select(s => s.Name))}." : "");
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
        working.Name = t.Name; working.Top = t.Top; working.Bottom = t.Bottom; working.TextColor = t.TextColor;
        working.Accent = t.Accent; working.ButtonStyle = t.ButtonStyle; working.BackgroundImage = t.BackgroundImage; working.Font = t.Font;
        working.TemplateDir = t.TemplateDir; working.Category = t.Category;
        Rebind();
    }

    void OnPrev(object s, RoutedEventArgs e) { page = Math.Max(0, page - 1); Dirty(); }
    void OnNext(object s, RoutedEventArgs e) { page++; Dirty(); } // Render clamps to the last page

    void OnBg(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|Tous les fichiers|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        working.BackgroundImage = dlg.FileName;
        Rebind();
    }

    void OnBgClear(object s, RoutedEventArgs e)
    {
        working.BackgroundImage = null;
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
            Title = title, Owner = owner, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x1F)), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
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
