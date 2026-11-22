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
public partial class MenuEditorWindow
{
    // ---------------------------------------------------------------- the zones the template offers (see MenuZone)

    readonly Dictionary<int, string> renamed = [];
    List<string> panelZones = [];
    TextBox? editing;

    /// <summary>The videos the user renamed on the menu's pages: playlist number (in the disc's order, see MainWindow.DiscOrder) and name.</summary>
    public IReadOnlyDictionary<int, string> RenamedTitles => renamed;

    /// <summary>The disc's facts changed behind the open editor (its pictures have arrived): the names written here stay.</summary>
    public void UseContent(MenuContent c)
    {
        baseContent = c;
        content = Named(c);
        Dirty();
    }

    MenuContent Named(MenuContent c)
    {
        List<TitleEntry>? Rename(IReadOnlyList<TitleEntry>? list) => list?.Select(t => renamed.TryGetValue(t.Playlist, out var name) ? t with { Name = name } : t).ToList();
        return c with { Titles = Rename(c.Titles), Bonus = Rename(c.Bonus) };
    }

    string ZoneValue(string key)
    {
        if (key == "title") return working.Title;
        if (key.StartsWith("slot:")) return working.TextZones.GetValueOrDefault(key[5..], "");
        if (!key.StartsWith("name:")) return "";
        var playlist = int.Parse(key[5..]);
        return (content.Titles ?? []).Concat(content.Bonus ?? []).FirstOrDefault(t => t.Playlist == playlist)?.Name ?? "";
    }

    void SetZone(string key, string value)
    {
        if (key == "title") working.Title = value;
        else if (key.StartsWith("slot:"))
        {
            if (value.Trim().Length > 0) working.TextZones[key[5..]] = value;
            else working.TextZones.Remove(key[5..]);
        }
        else if (key.StartsWith("name:") && value.Trim().Length > 0) // a video keeps a name
        {
            renamed[int.Parse(key[5..])] = value.Trim();
            content = Named(content);
        }
    }

    static string ZoneName(string key) =>
        key == "slot:info" ? "Ligne d'information"
        : key.StartsWith("slot:overview") ? "Texte " + key[13..]
        : key.StartsWith("name:") ? "Nom de la vidéo " + key[5..]
        : key;

    /// <summary>The same zones as text boxes in the Menu tab, for the keyboard. Rebuilt only when the page offers others: a redraw must not take a box away from under the typing.</summary>
    void FillZonePanel(bool force = false)
    {
        var keys = current?.Zones.Select(z => z.Key).Where(k => k is not "title" and not "poster").ToList() ?? [];
        if (!force && keys.SequenceEqual(panelZones)) return;
        panelZones = keys;
        ZonePanel.Children.Clear();
        foreach (var key in keys)
        {
            ZonePanel.Children.Add(new TextBlock { Text = ZoneName(key), Style = (Style)FindResource("Caption") });
            var box = new TextBox { Text = ZoneValue(key), TextWrapping = TextWrapping.Wrap };
            System.Windows.Automation.AutomationProperties.SetName(box, ZoneName(key));
            box.TextChanged += (_, _) => SetZone(key, box.Text); // the panel's own handler asks for the new preview
            ZonePanel.Children.Add(box);
        }
    }

    void DrawZone(MenuZone z)
    {
        var accent = AccentBrush();
        var empty = z.Key.StartsWith("slot:") && z.Key != "slot:info" && ZoneValue(z.Key).Length == 0;
        if (empty) // on the editor's screen only: an empty zone draws nothing on the disc
        {
            var hint = new TextBlock { Text = "Zone de texte du modèle : cliquez pour écrire", FontSize = 26, Opacity = 0.55, Foreground = System.Windows.Media.Brushes.White, IsHitTestVisible = false };
            System.Windows.Controls.Canvas.SetLeft(hint, z.Rect.X + 8);
            System.Windows.Controls.Canvas.SetTop(hint, z.Rect.Y + 8);
            StageCanvas.Children.Add(hint);
        }
        var shape = new System.Windows.Shapes.Rectangle
        {
            Width = z.Rect.W, Height = z.Rect.H, Fill = System.Windows.Media.Brushes.Transparent, StrokeThickness = 3, StrokeDashArray = [6, 4],
            Stroke = empty ? accent : null, Cursor = z.Key == "poster" ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.IBeam,
            ToolTip = z.Key == "poster" ? "Changer l'image" : "Cliquez pour écrire",
        };
        System.Windows.Controls.Canvas.SetLeft(shape, z.Rect.X);
        System.Windows.Controls.Canvas.SetTop(shape, z.Rect.Y);
        shape.MouseEnter += (_, _) =>
        {
            shape.Stroke = accent;
            // a name drawn on its button: the button under it stays the one selected, as without the zone
            var (x, y) = (z.Rect.X + z.Rect.W / 2, z.Rect.Y + z.Rect.H / 2);
            Select(current?.Buttons.FirstOrDefault(b => x >= b.Rect.X && x < b.Rect.X + b.Rect.W && y >= b.Rect.Y && y < b.Rect.Y + b.Rect.H)?.Id);
        };
        shape.MouseLeave += (_, _) => shape.Stroke = empty ? accent : null;
        shape.MouseLeftButtonDown += (_, me) =>
        {
            me.Handled = true;
            if (sel != null) SelectElem(null);
            if (z.Key == "poster") PosterMenu(); else BeginZoneEdit(z);
        };
        StageCanvas.Children.Add(shape);
    }

    /// <summary>For the off-screen snapshot (<c>undo</c>): two titles typed in the box, undone twice, redone once; the status line says whether each step came back.</summary>
    public async Task TryUndo()
    {
        var start = TitleBox.Text;
        TitleBox.Text = start + " A"; await Task.Delay(700);
        TitleBox.Text = start + " B"; await Task.Delay(700);
        Undo(); var one = TitleBox.Text; await Task.Delay(700);
        Undo(); var two = TitleBox.Text; await Task.Delay(700);
        Redo(); var three = TitleBox.Text; await Task.Delay(700);
        var ok = one == start + " A" && two == start && three == start + " A" && working.Title == three && RedoButton.IsEnabled;
        Status.Text = $"undo {(ok ? "ok" : "FAIL")}: {one} | {two} | {three}";
    }

    /// <summary>For the off-screen snapshot (<c>zones</c>): writes in the page's zones as a click then Enter does, and types in one that Escape gives back.</summary>
    public void TryZones()
    {
        void Write(string key, string text, bool keep)
        {
            if (current?.Zones.FirstOrDefault(z => z.Key == key) is not { } zone) return;
            BeginZoneEdit(zone);
            editing!.Text = text;
            EndZoneEdit(keep);
        }
        Write("slot:info", "2018 | Action, Drame", true);
        Write("slot:overview1", "Ne doit pas rester", false);
        Write("slot:overview2", "Distribution : William Zabka, Ralph Macchio", true);
        Write("slot:overview4", "Trente ans après le tournoi de 1984, Johnny Lawrence rouvre le dojo Cobra Kai, ce qui ravive sa rivalité avec Daniel LaRusso. Une histoire de rédemption et de vieilles rancunes, assez longue pour passer à la ligne dans sa zone.", true);
        Write("name:2", "S01E02 - Frappe en premier", true);
    }

    void BeginZoneEdit(MenuZone z)
    {
        EndZoneEdit(true); // a click on another zone: what was typed in the first one stays
        var box = new TextBox
        {
            Text = ZoneValue(z.Key), Width = Math.Max(z.Rect.W, 300), MinHeight = z.Rect.H, FontSize = Math.Clamp(z.Rect.H * 0.6, 24, 34),
            TextWrapping = z.Rect.H >= 100 ? TextWrapping.Wrap : TextWrapping.NoWrap, Tag = z.Key,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, ZoneName(z.Key));
        System.Windows.Controls.Canvas.SetLeft(box, Math.Min(z.Rect.X, MenuBuilder.W - box.Width));
        System.Windows.Controls.Canvas.SetTop(box, z.Rect.Y);
        box.LostKeyboardFocus += (_, _) => EndZoneEdit(true);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        editing = box;
        StageCanvas.Children.Add(box);
    }

    void EndZoneEdit(bool keep)
    {
        if (editing is not { } box) return;
        editing = null; // first: taking the box off the page takes its focus, which comes back here
        if (box.IsKeyboardFocusWithin) Stage.Focus(); // given away now: left to Windows, it would come back after the next box took it, and close that box
        StageCanvas.Children.Remove(box);
        if (keep) SetZone((string)box.Tag, box.Text);
        FillZonePanel(true);
        DrawStage();
        Rebind();
    }

    void PosterMenu()
    {
        var menu = new ContextMenu();
        var pick = new MenuItem { Header = "Choisir une image…" };
        pick.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = Loc.Filter("Images|*.jpg;*.jpeg;*.png;*.bmp;*.svg|Tous les fichiers|*.*"), Title = Loc.T("Image du menu") };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                working.Poster = ElementLibrary.Reference(ElementLibrary.Import(dlg.FileName).Path);
                Dirty();
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, "BurnSmith");
            }
        };
        var film = new MenuItem { Header = "Reprendre l'image du film", IsEnabled = working.Poster != null };
        film.Click += (_, _) => { working.Poster = null; Dirty(); };
        menu.Items.Add(pick);
        menu.Items.Add(film);
        menu.IsOpen = true;
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
        var request = ++navigationRequest;
        var mine = version;
        try
        {
            if (!s.Cache.TryGetValue(id, out var images))
            {
                images = await Task.Run(() => MenuRenderer.Sta(() => DrawPage(s.Design, page)));
                s.Cache[id] = images;
            }
            if (mine != version || shot != s || request != navigationRequest) return;
            if (first) focusId = null;
            Show(page, images);
        }
        catch (Exception ex)
        {
            if (mine == version && shot == s && request == navigationRequest) Status.Text = "Aperçu impossible : " + ex.Message;
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
                Status.Text = "";
                break;
            case "play":
                previewPlaying = a.Playlist;
                previewChain = Playback.PlayAll && a.Playlist <= (content.Titles?.Count ?? 1);
                Status.Text = $"▶ Sur le disque : lecture du titre {a.Playlist}.";
                break;
            case "chapter":
                previewPlaying = a.Playlist;
                previewChain = Playback.PlayAll && a.Playlist <= (content.Titles?.Count ?? 1);
                if (IsPreviewingPopup && shot?.Pages.FirstOrDefault() is { } root) GoTo(root.Id);
                Status.Text = $"▶ Sur le disque : lecture du titre {a.Playlist} à partir du chapitre {a.Mark + 1}.";
                break;
            case "playall":
                previewPlaying = a.Playlist;
                previewChain = true;
                Status.Text = $"▶ Sur le disque : lecture de {a.Mark} titre(s) à la suite.";
                break;
            case "resume":
                if (shot?.Pages.FirstOrDefault() is { } origin) GoTo(origin.Id);
                Status.Text = $"▶ Reprise du titre {previewPlaying}.";
                break;
            case "popup":
                if (previewPlaying is int playing) GoTo($"pop{playing}");
                break;
            case "menu":
                previewPlaying = null;
                if (shot?.Pages.FirstOrDefault() is { } main) GoTo(main.Id);
                break;
            case "audio" or "sub":
                if (shot is not { } s || current is not { } settingsPage) return;
                if (a.Kind == "audio") audioSel = a.Mark; else subSel = a.Mark;
                var at = s.Pages.FindIndex(p => p.Id == settingsPage.Id);
                if (at < 0) return;
                var (audio, sub, design, facts) = (audioSel, subSel, s.Design, content with { PlainFocus = DvdPreview });
                var request = ++trackRequest;
                var navigation = navigationRequest;
                try
                {
                    var page = await Task.Run(() => MenuRenderer.Sta(() => settingsPage.Id.StartsWith("pop", StringComparison.Ordinal)
                        ? MenuBuilder.Popup(design, facts, facts.PopupTitles, audio, sub).First(p => p.Id == settingsPage.Id)
                        : SettingsPreview(design, facts, settingsPage.Id, audio, sub)));
                    if (shot != s || request != trackRequest) return;
                    s.Pages[at] = page;
                    s.Cache.Remove(page.Id);
                    if (navigation == navigationRequest && current?.Id == page.Id) GoTo(page.Id, false);
                }
                catch (Exception ex)
                {
                    if (shot == s && request == trackRequest) Status.Text = "Aperçu impossible : " + ex.Message;
                }
                break;
        }
    }

    /// <summary>A click on the page that misses the selected element's outline and handles lets go of it.</summary>
    void OnStageMouse(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        Stage.Focus();
        if (sel != null && (e.OriginalSource as FrameworkElement)?.Tag as string != "element") SelectElem(null);
    }

    // ---------------------------------------------------------------- free elements (free placement)
    MenuElement? sel;
    bool elemSync;
    const int Handle = 30, MinW = 40, MinH = 20; // size of a resize handle on the 1920x1080 page, smallest element

    // a record, not a tuple: the combo binds to properties, and a tuple's fields are not (the page would show empty and fall back to "all")
    sealed record PageChoice(string Value, string Label);

    static readonly PageChoice[] PageChoices =
    [
        new("all", "Toutes les pages"), new("root", "Menu principal"), new("scenes", "Chapitres"), new("bonus", "Bonus"), new("settings", "Réglages"), new("credits", "Crédits"),
    ];

    void InitElements()
    {
        ElemPage.SelectedValuePath = "Value";
        ElemPage.DisplayMemberPath = "Label";
        ElemAlign.ItemsSource = new[] { "left", "center", "right" };
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

    /// <summary>The accent as it is being typed: "#FFC83" is not a colour yet, and must not raise an error box at every move of the mouse.</summary>
    System.Windows.Media.SolidColorBrush AccentBrush()
    {
        try { return new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(working.Accent)); }
        catch (FormatException) { return System.Windows.Media.Brushes.White; }
    }

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
            ElemPage.ItemsSource = PageChoices.Concat(working.CustomPages.Select(page => new PageChoice(page.Id, page.Title)))
                .Concat(PageChoices.Any(page => page.Value == e.Page) || working.CustomPages.Any(page => page.Id == e.Page)
                    ? [] : [new PageChoice(e.Page, e.Page)]).ToList();
            ElemTarget.ItemsSource = new[] { "movie", "all", "root", "scenes1", "settings", "credits", "bonus1" }
                .Concat(working.CustomPages.Select(page => page.Id)).Append(e.Target).Distinct().ToArray();
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
        ShowRightPanel();
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
        RefreshElemList(); // not ElemList.Items[at] = …: a list filled through ItemsSource refuses it, with an error box at every keystroke
        Dirty();
    }

    void SelectElem(MenuElement? e)
    {
        sel = e;
        elemSync = true;
        ElemList.SelectedIndex = e == null ? -1 : working.Elements.IndexOf(e);
        elemSync = false;
        if (e != null) { drawerKind = null; ShowStep(1); } // contextual inspector opens in Personnaliser
        ShowElem();
        DrawStage();
    }

    /// <param name="settings">Its settings come up (a text to type, a picture to choose); a button taken from the library leaves the drawer open, for the next one.</param>
    void AddElem(MenuElement e, bool settings = true)
    {
        if (e.IsButton) e.NavigationId = "u-" + Guid.NewGuid().ToString("N")[..10];
        working.Elements.Add(e);
        sel = e;
        RefreshElemList();
        if (settings) { drawerKind = null; ShowStep(1); }
        ShowElem();
        Dirty();
    }

    string ElementPage => current?.Id is { } page && (page == "root" || page.StartsWith("custom-")) ? page : MenuElement.AllPages;

    void OnAddText(object s, RoutedEventArgs e) => AddElem(new MenuElement { Kind = "text", Page = ElementPage, Text = "Votre texte", X = 140, Y = 140, W = 700, H = 90, Size = 48 });

    void OnAddBox(object s, RoutedEventArgs e) => AddElem(new MenuElement { Kind = "box", Page = ElementPage, X = 120, Y = 780, W = 1680, H = 200, Opacity = 45, Radius = 24, BorderWidth = 0, Behind = true });

    void OnAddButton(object s, RoutedEventArgs e) => AddElem(new MenuElement { Kind = "button", Page = ElementPage, Text = "Bouton", Target = "movie", X = 300, Y = 620, W = 400, H = 110, Size = 44 });

    void OnAddImage(object s, RoutedEventArgs e) => AddElem(new MenuElement { Kind = "image", Page = ElementPage, X = 1500, Y = 60, W = 300, H = 300, Fit = true, BorderWidth = 0 });

    void OnAddSubmenu(object s, RoutedEventArgs e)
    {
        if (Prompt.Ask(this, "Nom du sous-menu", "Sous-menu")?.Trim() is not { Length: > 0 } name) return;
        var parent = current?.Id ?? "root";
        var id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        working.CustomPages.Add(new(id, name, parent));
        AddElem(new MenuElement { Kind = "button", Page = parent, Text = name, Target = id, X = 300, Y = 620, W = 400, H = 110, Size = 44 });
    }

    void OnRenameSubmenu(object s, RoutedEventArgs e)
    {
        var index = working.CustomPages.FindIndex(page => page.Id == current?.Id);
        if (index < 0) return;
        var page = working.CustomPages[index];
        if (Prompt.Ask(this, "Nom du sous-menu", page.Title)?.Trim() is not { Length: > 0 } name) return;
        working.CustomPages[index] = page with { Title = name };
        foreach (var button in working.Elements.Where(element => element.IsButton && element.Target == page.Id && element.Text == page.Title))
            button.Text = name;
        RefreshElemList();
        ShowElem();
        Dirty();
    }

    void OnDirectionLink(object s, RoutedEventArgs e)
    {
        if (current is not { } page || page.Buttons.Count == 0) return;
        var ids = string.Join(", ", page.Buttons.Select(button => button.Id));
        var entry = Prompt.Ask(this, $"Bouton, flèche et cible ({ids})", $"{focusId ?? page.Buttons[0].Id} droite auto");
        if (entry == null) return;
        var words = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words is not [var button, var direction, var target]
            || !page.Buttons.Any(item => item.Id == button)
            || target != "-" && target != "auto" && !page.Buttons.Any(item => item.Id == target))
        {
            Status.Text = "Entrez un bouton, une flèche et un bouton cible. Utilisez auto ou - pour la cible.";
            return;
        }
        var key = direction switch { "gauche" => "left", "droite" => "right", "haut" => "up", "bas" => "down", _ => null };
        if (key == null) { Status.Text = "Indiquez gauche, droite, haut ou bas."; return; }
        working.Directions.RemoveAll(link => link.Page == page.Id && link.Button == button && link.Direction == key);
        if (target != "auto") working.Directions.Add(new(page.Id, button, key, target));
        Dirty();
    }

    void OnRemoveSubmenu(object s, RoutedEventArgs e)
    {
        if (working.CustomPages.FirstOrDefault(page => page.Id == current?.Id) is not { } selected) return;
        if (working.CustomPages.Any(page => page.Parent == selected.Id))
        {
            Status.Text = "Retirez d’abord ses sous-menus enfants.";
            return;
        }
        working.CustomPages.Remove(selected);
        working.Elements.RemoveAll(element => element.Page == selected.Id || element.IsButton && element.Target == selected.Id);
        working.Directions.RemoveAll(link => link.Page == selected.Id);
        SelectElem(null);
        RefreshElemList();
        Dirty();
    }

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
        var dlg = new OpenFileDialog { Filter = Loc.Filter("Images|*.jpg;*.jpeg;*.png;*.bmp;*.svg|Tous les fichiers|*.*"), Title = Loc.T("Image de l'élément") };
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
        var dlg = new OpenFileDialog { Filter = Loc.Filter("Images|*.jpg;*.jpeg;*.png;*.bmp;*.svg|Tous les fichiers|*.*"), Title = Loc.T("Image du bouton sélectionné") };
        if (dlg.ShowDialog(this) != true) return;
        el.Focus = dlg.FileName;
        ShowElem();
        Dirty();
    }

    void OnElemAlignCanvas(object s, RoutedEventArgs e)
    {
        if (sel is not { } el || s is not Button btn || btn.Tag is not string dir) return;
        const int safeX = 96, safeY = 54; // 5 % title-safe TV margins on 1920x1080
        switch (dir)
        {
            case "left": el.X = safeX; break;
            case "center-h": el.X = Math.Max(0, (MenuBuilder.W - el.W) / 2); break;
            case "right": el.X = Math.Max(0, MenuBuilder.W - safeX - el.W); break;
            case "top": el.Y = safeY; break;
            case "center-v": el.Y = Math.Max(0, (MenuBuilder.H - el.H) / 2); break;
            case "bottom": el.Y = Math.Max(0, MenuBuilder.H - safeY - el.H); break;
            default: return;
        }
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

}
