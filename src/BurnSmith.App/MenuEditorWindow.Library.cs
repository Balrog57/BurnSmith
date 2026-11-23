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
            el.ActiveImage = item.Active == null ? null : ElementLibrary.Reference(item.Active);
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
        AddElem(el, settings: false);
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
        var filter = "Images et PSD|*.jpg;*.jpeg;*.png;*.bmp;*.svg;*.psd|Tous les fichiers|*.*";
        var normal = new OpenFileDialog { Filter = filter, Title = Loc.T("Image du bouton (ou PSD multi-calques)") };
        if (normal.ShowDialog(this) != true) return;
        try
        {
            if (Path.GetExtension(normal.FileName).Equals(".psd", StringComparison.OrdinalIgnoreCase))
            {
                var (comp, layers) = PsdReader.Read(normal.FileName);
                var tmp = Path.Combine(Path.GetTempPath(), "bs_psd_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tmp);
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(normal.FileName);
                    var actLayer = layers.FirstOrDefault(l => l.Name.Contains("active", StringComparison.OrdinalIgnoreCase)
                        || l.Name.Contains("press", StringComparison.OrdinalIgnoreCase));
                    var selLayer = layers.FirstOrDefault(l => l != actLayer && (l.Name.Contains("select", StringComparison.OrdinalIgnoreCase)
                        || l.Name.Contains("focus", StringComparison.OrdinalIgnoreCase)
                        || l.Name.Contains("hover", StringComparison.OrdinalIgnoreCase)));
                    var normLayer = layers.FirstOrDefault(l => l != selLayer && l != actLayer && (l.Name.Contains("normal", StringComparison.OrdinalIgnoreCase)
                        || l.Name.Contains("base", StringComparison.OrdinalIgnoreCase))) ?? layers.FirstOrDefault(l => l != selLayer && l != actLayer);
                    var normBytes = normLayer?.Png ?? comp ?? throw new InvalidDataException("Le fichier PSD ne contient aucun calque visible.");
                    var normPath = Path.Combine(tmp, stem + ".png");
                    File.WriteAllBytes(normPath, normBytes);
                    string? selPath = null;
                    if (selLayer != null)
                    {
                        selPath = Path.Combine(tmp, stem + "_sel.png");
                        File.WriteAllBytes(selPath, selLayer.Png);
                    }
                    string? actPath = null;
                    if (actLayer != null)
                    {
                        actPath = Path.Combine(tmp, stem + "_act.png");
                        File.WriteAllBytes(actPath, actLayer.Png);
                    }
                    ElementLibrary.ImportButton(normPath, selPath, actPath);
                }
                finally { Tools.TryDelete(tmp); }
            }
            else
            {
                var focus = new OpenFileDialog { Filter = filter, Title = Loc.T("Image du bouton sélectionné (Annuler = aucune : le cadre du thème)") };
                ElementLibrary.ImportButton(normal.FileName, focus.ShowDialog(this) == true ? focus.FileName : null);
            }
            LoadButtons();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
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
        if (shot == null || IsPreviewingPopup) return;
        var pages = shot.Pages.Where(page => !page.Id.StartsWith("pop", StringComparison.Ordinal)).ToList();
        var i = pages.FindIndex(p => p.Id == current?.Id) + by;
        if (i >= 0 && i < pages.Count) GoTo(pages[i].Id);
    }

    void OnPrev(object s, RoutedEventArgs e) => Step(-1);
    void OnNext(object s, RoutedEventArgs e) => Step(1);

    // ---------------------------------------------------------------- the five steps
    // Yuhan's one window (the page always in place) laid out as TMPGEnc's stages: the bar picks the panel on the right

    static readonly string[] Hints =
    [
        "Cliquez un modèle : il habille le menu ; vos textes, vos vidéos et vos réglages restent.",
        "Modifiez le titre, le fond, la musique et la présentation, ou cliquez un élément sur la page pour le déplacer et le régler (Suppr le retire, flèches : déplacement, Maj : pas fin).",
        "Flèches et Entrée du clavier, ou la télécommande à droite : le menu se parcourt comme sur le disque.",
    ];

    /// <summary>The step on show is the last one: the page as the disc shows it, nothing to grab on it.</summary>
    bool Previewing => Props.SelectedIndex == Hints.Length - 1;

    string? drawerKind; // "bg" | "btn" | "layers" | null

    void ShowRightPanel()
    {
        if (LibraryDrawer == null) return;
        var hasDrawer = drawerKind != null;
        LibraryDrawer.Visibility = hasDrawer ? Visibility.Visible : Visibility.Collapsed;
        BgDrawerPanel.Visibility = drawerKind == "bg" ? Visibility.Visible : Visibility.Collapsed;
        BtnDrawerPanel.Visibility = drawerKind == "btn" ? Visibility.Visible : Visibility.Collapsed;
        LayersDrawerPanel.Visibility = drawerKind == "layers" ? Visibility.Visible : Visibility.Collapsed;
        DrawerTitle.Text = drawerKind switch
        {
            "btn" => "Bibliothèque de boutons",
            "layers" => "Calques de la page",
            _ => "Bibliothèque de fonds",
        };
        if (drawerKind == "layers") RefreshLayersDrawer();
        ElementInspectorPanel.Visibility = !hasDrawer && HasSelection ? Visibility.Visible : Visibility.Collapsed;
        MenuSettingsPanel.Visibility = !hasDrawer && !HasSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnBackToMenu(object s, RoutedEventArgs e) => SelectElem(null);
    void OnOpenBgDrawer(object s, RoutedEventArgs e) { drawerKind = "bg"; ShowStep(1); ShowRightPanel(); }
    void OnOpenBtnDrawer(object s, RoutedEventArgs e) { drawerKind = "btn"; ShowStep(1); ShowRightPanel(); }
    void OnCloseDrawer(object s, RoutedEventArgs e) { drawerKind = null; ShowRightPanel(); }

    void OnAddMenuClick(object s, RoutedEventArgs e)
    {
        if (AddMenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = AddMenuButton;
        menu.IsOpen = true;
    }

    void OnNavigationMap(object s, RoutedEventArgs e)
    {
        var mode = DvdPreview ? "DVD" : Hdmv ? "Blu-ray HDMV" : "Blu-ray BD-J";
        var report = MenuNavigationMap.Analyze(shot?.Pages ?? [], Playback, mode);
        var text = (report.Warnings.Count > 0
            ? "Alertes :\n" + string.Join("\n", report.Warnings.Select(w => "⚠ " + w)) + "\n\n"
            : "✓ Toutes les pages sont accessibles et reliées.\n\n")
            + string.Join("\n", report.Lines);
        var box = new TextBox { Text = text, IsReadOnly = true, FontFamily = new("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(16) };
        new Window { Title = "Carte de navigation", Owner = this, Width = 640, Height = 520, Content = box, Background = (System.Windows.Media.Brush)FindResource("BgBrush"), WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }

    void OnStep(object s, RoutedEventArgs e)
    {
        if (Props != null) ShowStep(int.Parse((string)((FrameworkElement)s).Tag)); // null while the window is being built: its first step is shown by the constructor
    }

    /// <summary>Shows a step (0 Modèle, 1 Personnaliser, 2 Aperçu): its panel, its line of help.</summary>
    public void ShowStep(int step)
    {
        step = Math.Clamp(step, 0, Hints.Length - 1);
        if (StepBar.Children[step] is RadioButton bar && bar.IsChecked != true) { bar.IsChecked = true; return; } // comes back here through OnStep
        Props.SelectedIndex = step;
        Hint.Text = Loc.T(Hints[step]);
        BackStep.IsEnabled = step > 0;
        NextStep.IsEnabled = step < Hints.Length - 1;
        if (step != 1) drawerKind = null;
        ShowRightPanel();
        if (Previewing && HasSelection) SelectElem(null); else DrawStage();
        if (Previewing) Stage.Focus(); // the keyboard's arrows walk the menu at once
    }

    void OnBackStep(object s, RoutedEventArgs e) => ShowStep(Props.SelectedIndex - 1);
    void OnNextStep(object s, RoutedEventArgs e) => ShowStep(Props.SelectedIndex + 1);

    /// <summary>The disc's behaviour (« Lecture du disque », also in Paramètres avancés): given by the main window, read back by it after OK.</summary>
    public PlaybackOptions Playback { get; set; } = new();

    // ---------------------------------------------------------------- the templates, one family at a time
    const string AllFamilies = "*", Mine = "+", Recent = "~";
    List<TemplateCatalog.Found> catalog = [];
    string? family;

    /// <summary>The screen shape the gallery shows the templates for: a template with a 16:9 and a 4:3 version shows the chosen one.</summary>
    bool narrow;

    /// <summary>The gallery as shown: the templates of the chosen shape only (a name without « 4-3 » is a 16:9 one).</summary>
    List<TemplateCatalog.Found> Shown => [.. catalog.Where(f => TemplateShape.IsNarrow(f.Design.Name) == narrow)];

    /// <summary>One chip per use of the templates (Film, Série…, Original), « Tous » before and « Mes modèles » after; the use of the menu's own template is the one open.</summary>
    void FillFamilies(List<TemplateCatalog.Found> usable)
    {
        catalog = usable;
        narrow = TemplateShape.IsNarrow(working.Name);
        (narrow ? Shape43 : Shape169).IsChecked = true;
        var shown = Shown;
        var names = TemplateUsage.All.Where(u => shown.Any(f => f.Usage == u)).ToList();
        var own = TemplateUsage.Of(working.Name);
        var recent = Recents().Count;
        if (family is null || (family != AllFamilies && family != Mine && !(family == Recent && recent > 0) && !names.Contains(family)))
            family = names.Contains(own) ? own : names.FirstOrDefault() ?? AllFamilies;
        Families.Children.Clear();
        foreach (var name in (recent > 0 ? names.Prepend(Recent) : names).Prepend(AllFamilies).Append(Mine))
        {
            var chip = new RadioButton
            {
                Content = name == AllFamilies ? $"Tous ({shown.Count})" : name == Mine ? "Mes modèles" : name == Recent ? $"Récemment utilisés ({recent})" : $"{name} ({shown.Count(f => f.Usage == name)})",
                GroupName = "family", Style = (Style)FindResource("Step"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), IsChecked = name == family,
            };
            chip.Checked += (_, _) => { family = name; ShowFamily(); };
            Families.Children.Add(chip);
        }
        ShowFamily();
    }

    void ShowFamily()
    {
        MinePanel.Visibility = family == Mine ? Visibility.Visible : Visibility.Collapsed;
        BuiltInList.Visibility = family == Mine ? Visibility.Collapsed : Visibility.Visible;
        var search = SearchBox.Text;
        // a search looks through every template, whatever the group open; favourites first everywhere, the recent ones in the order used
        BuiltInList.ItemsSource = family == Recent && search.Trim().Length == 0 ? Recents()
            : TemplatePicks.Sorted(Shown.Where(f => search.Trim().Length > 0 ? TemplatePicks.Matches(f, search) : family == AllFamilies || f.Usage == family)).ToList();
    }

    /// <summary>The last templates used (OK of this window) that the gallery shows, the latest first.</summary>
    List<TemplateCatalog.Found> Recents()
    {
        var shown = Shown;
        return [.. TemplatePicks.Recent().Select(n => shown.FirstOrDefault(f => f.Label.Equals(n, StringComparison.OrdinalIgnoreCase))).OfType<TemplateCatalog.Found>()];
    }

    void OnSearch(object s, TextChangedEventArgs e)
    {
        if (family == Mine && SearchBox.Text.Trim().Length > 0) foreach (var chip in Families.Children.OfType<RadioButton>()) chip.IsChecked = chip == Families.Children[0]; // « Mes modèles » has no thumbnails to search: « Tous »
        ShowFamily();
    }

    void OnStar(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is not TemplateCatalog.Found f) return;
        TemplatePicks.Star(f.Design.Name, !f.Favorite);
        ShowFamily(); // the item is redrawn with its star, and moves to the top or back
    }

    /// <summary>16:9 or 4:3: the gallery shows that version of each template, and the menu's own template takes it when it has one.</summary>
    void OnShape(object s, RoutedEventArgs e)
    {
        if (Shape43.IsChecked == narrow) return; // set by FillFamilies, not chosen
        narrow = Shape43.IsChecked == true;
        ShowFamily();
        var twin = TemplateShape.Pick(working, narrow, catalog.Select(f => f.Design));
        if (twin == working) return;
        working.TakeLook(twin);
        Rebind();
    }

    /// <summary>The remote's keys: the arrows go to the neighbour button, Enter does what the disc would.</summary>
    bool Remote(System.Windows.Input.Key key)
    {
        if (current?.Buttons.FirstOrDefault(b => b.Id == focusId) is not { } b) return false;
        switch (key)
        {
            case System.Windows.Input.Key.Left: Select(b.Left); break;
            case System.Windows.Input.Key.Right: Select(b.Right); break;
            case System.Windows.Input.Key.Up: Select(b.Up); break;
            case System.Windows.Input.Key.Down: Select(b.Down); break;
            case System.Windows.Input.Key.Enter: Activate(b); break;
            default: return false;
        }
        return true;
    }

    /// <summary>For the off-screen snapshot: the remote's keys pressed one after the other on the Aperçu step ("Right,Enter").</summary>
    public async Task TryRemote(string keys)
    {
        ShowStep(Hints.Length - 1);
        foreach (var key in keys.Split(',')) { OnRemote(new FrameworkElement { Tag = key.Trim() }, new RoutedEventArgs()); await Task.Delay(800); }
    }

    void OnRemote(object s, RoutedEventArgs e)
    {
        var key = (string)((FrameworkElement)s).Tag;
        if (key == "Start")
        {
            previewPlaying = content.HasIntro ? 0 : Playback.StartAtFirstTitle ? 1 : null;
            previewChain = Playback.StartAtFirstTitle;
            if (shot?.Pages.FirstOrDefault() is { } first) GoTo(first.Id);
            Status.Text = previewPlaying switch { 0 => "▶ Démarrage : introduction.", 1 => "▶ Démarrage : titre 1.", _ => "Démarrage : menu principal." };
        }
        else if (key == "End")
        {
            if (previewPlaying is not int title) { Status.Text = "Choisissez un titre à lire."; return; }
            if (title == 0)
            {
                previewPlaying = Playback.StartAtFirstTitle ? 1 : null;
                if (previewPlaying == null && shot?.Pages.FirstOrDefault() is { } first) GoTo(first.Id);
                Status.Text = previewPlaying != null ? "▶ Fin de l’introduction : lecture du titre 1." : "Fin de l’introduction : menu principal.";
                return;
            }
            var main = content.Titles?.Count ?? 1;
            if (previewChain && title < main)
            {
                previewPlaying = title + 1;
                Status.Text = $"▶ Fin du titre {title} : lecture du titre {previewPlaying}.";
            }
            else if (Playback.StartAtFirstTitle && Playback.Rewind && (Hdmv || previewChain && title == main))
            {
                previewPlaying = 1;
                previewChain = true;
                Status.Text = $"▶ Fin du titre {title} : reprise au premier titre.";
            }
            else
            {
                previewPlaying = null;
                var stop = Playback.PlayAll && !Playback.Rewind && (Hdmv || previewChain && title == main);
                if (!stop && shot?.Pages.FirstOrDefault() is { } root) GoTo(root.Id);
                Status.Text = stop ? $"Fin du titre {title} : arrêt." : $"Fin du titre {title} : retour au menu.";
            }
        }
        else if (key == "Menu")
        {
            previewPlaying = null;
            if (shot?.Pages.FirstOrDefault() is { } first) GoTo(first.Id);
        }
        else if (key == "Popup")
        {
            if (current?.Id.StartsWith("pop", StringComparison.Ordinal) == true && shot?.Pages.FirstOrDefault() is { } first) GoTo(first.Id);
            else if (previewPlaying is int title && shot?.Pages.Any(page => page.Id == $"pop{title}") == true) GoTo($"pop{title}");
            else Status.Text = "Choisissez un titre dans l’aperçu d’un menu Blu-ray BD-J avec pop-up activé.";
        }
        else Remote(Enum.Parse<System.Windows.Input.Key>(key));
        if (SimLogBox != null)
        {
            var mode = DvdPreview ? "DVD" : Hdmv ? "HDMV" : "BD-J";
            var detail = !string.IsNullOrWhiteSpace(Status.Text) ? Status.Text : $"page {current?.Id ?? "?"} · bouton {focusId ?? "—"}";
            SimLogBox.AppendText($"[{mode}] {key} → {detail}\n");
            SimLogBox.ScrollToEnd();
        }
    }

    /// <summary>Which of the three highlight styles marks the chosen button (a template with its own artwork keeps it).</summary>
    void OnPickHighlight(object s, SelectionChangedEventArgs e)
    {
        if (HlBox.SelectedValue is not string style) return;
        working.HighlightStyle = style;
        Dirty();
    }

    void OnPickCursorPreset(object s, SelectionChangedEventArgs e)
    {
        if (CursorPresetBox.SelectedValue is not string preset) return;
        working.CursorPreset = preset;
        CursorCustomBtn.Visibility = preset == "custom" ? Visibility.Visible : Visibility.Collapsed;
        if (preset == "none") working.FocusFrames = null;
        else if (preset is "pulse" or "ring" or "arrow") working.FocusFrames = MenuRenderer.BuiltInCursorFrames(preset, working.Accent);
        Dirty();
    }

    void OnPickCursorFrames(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.Filter("Images PNG|*.png|Images|*.png;*.jpg;*.bmp|Tous les fichiers|*.*"), Multiselect = true, Title = Loc.T("Images du curseur animé BD-J (2 ou plus)") };
        if (dlg.ShowDialog(this) != true || dlg.FileNames.Length < 2)
        {
            if (dlg.FileNames.Length == 1) Status.Text = Loc.T("Sélectionnez au moins 2 images PNG pour animer le curseur.");
            return;
        }
        working.CursorPreset = "custom";
        working.FocusFrames = dlg.FileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        Dirty();
    }

    bool pageMediaSync;

    void SyncPageMediaToUi()
    {
        if (LoopBox == null || PageMediaScopeBox == null) return;
        pageMediaSync = true;
        var pid = current?.Id ?? "root";
        var hasOverride = working.PageMedias.Any(m => m.Page.Equals(pid, StringComparison.OrdinalIgnoreCase));
        PageMediaScopeBox.IsChecked = hasOverride;
        var pm = working.MediaFor(pid);
        LoopBox.IsChecked = pm.Loop ?? working.Loop;
        LoopFileBox.Text = pm.LoopFile ?? "";
        MusicFileBox.Text = pm.Music ?? "";
        MusicVolumeSlider.Value = pm.MusicVolume ?? working.MusicVolume;
        pageMediaSync = false;
    }

    void OnPageMediaScopeChanged(object s, RoutedEventArgs e)
    {
        if (pageMediaSync || current == null) return;
        var pid = current.Id;
        if (PageMediaScopeBox.IsChecked == true)
        {
            if (!working.PageMedias.Any(m => m.Page.Equals(pid, StringComparison.OrdinalIgnoreCase)))
                working.PageMedias.Add(new PageMedia { Page = pid, Loop = working.Loop, LoopFile = working.LoopFile, Music = working.Music, MusicVolume = working.MusicVolume });
        }
        else
        {
            working.PageMedias.RemoveAll(m => m.Page.Equals(pid, StringComparison.OrdinalIgnoreCase));
        }
        SyncPageMediaToUi();
        Dirty();
    }

    void OnPageMediaChanged(object s, RoutedEventArgs e)
    {
        if (pageMediaSync) return;
        if (PageMediaScopeBox.IsChecked == true && current != null)
        {
            var pid = current.Id;
            var pm = working.PageMedias.FirstOrDefault(m => m.Page.Equals(pid, StringComparison.OrdinalIgnoreCase));
            if (pm == null) { pm = new PageMedia { Page = pid }; working.PageMedias.Add(pm); }
            pm.Loop = LoopBox.IsChecked == true;
            pm.LoopFile = string.IsNullOrWhiteSpace(LoopFileBox.Text) ? null : LoopFileBox.Text.Trim();
            pm.Music = string.IsNullOrWhiteSpace(MusicFileBox.Text) ? null : MusicFileBox.Text.Trim();
            pm.MusicVolume = (int)MusicVolumeSlider.Value;
        }
        else
        {
            working.Loop = LoopBox.IsChecked == true;
            working.LoopFile = string.IsNullOrWhiteSpace(LoopFileBox.Text) ? null : LoopFileBox.Text.Trim();
            working.Music = string.IsNullOrWhiteSpace(MusicFileBox.Text) ? null : MusicFileBox.Text.Trim();
            working.MusicVolume = (int)MusicVolumeSlider.Value;
        }
        Dirty();
    }

    void OnPickSoundPack(object s, SelectionChangedEventArgs e)
    {
        if (SoundPackBox.SelectedValue is not string pack) return;
        working.Sounds.Pack = pack;
        SoundCustomPanel.Visibility = pack == "custom" ? Visibility.Visible : Visibility.Collapsed;
        Dirty();
    }

    void OnPickMoveWav(object s, RoutedEventArgs e) =>
        Pick("Fichiers audio|*.wav;*.mp3;*.flac;*.ogg;*.aac", Loc.T("Son de déplacement (télécommande)"), f => working.Sounds.MoveWav = f);

    void OnPickSelectWav(object s, RoutedEventArgs e) =>
        Pick("Fichiers audio|*.wav;*.mp3;*.flac;*.ogg;*.aac", Loc.T("Son de validation OK (télécommande)"), f => working.Sounds.SelectWav = f);

    void OnPreviewSound(object s, RoutedEventArgs e)
    {
        try
        {
            var pack = working.Sounds.Pack;
            var snd = string.IsNullOrWhiteSpace(pack) || pack == "none"
                ? new MenuSoundSettings { Pack = "discret", Volume = working.Sounds.Volume }
                : working.Sounds;
            var wav = SoundBank.BuildWav(snd, true);
            using var ms = new MemoryStream(wav);
            new System.Media.SoundPlayer(ms).Play();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Status.Text = ex.Message;
        }
    }

    /// <summary>The whole menu in French or in English: the words of the Menu tab and those drawn in a template's buttons.</summary>
    void OnPickLanguage(object s, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedValue is not string language || language == working.ButtonLanguage) return; // shown by Rebind, not chosen
        working.Language(language);
        Rebind(); // the words are in the boxes
        Dirty();
    }

    /// <summary>A click on a background only selects it (so it can be removed); a double click or a drop on the page applies it.</summary>
    void OnBgPick(object s, SelectionChangedEventArgs e) => BgRemove.IsEnabled = (BgList.SelectedItem as Element)?.Removable == true;

    /// <summary>Adds pictures to your library (a copy is kept in %APPDATA%\BurnSmith\library) and uses the last one.</summary>
    void OnBg(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.Filter("Images et PSD|*.jpg;*.jpeg;*.png;*.bmp;*.svg;*.psd|Tous les fichiers|*.*"), Multiselect = true, Title = Loc.T("Images à ajouter à la bibliothèque") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var added = new List<Element>();
            foreach (var file in dlg.FileNames)
            {
                if (Path.GetExtension(file).Equals(".psd", StringComparison.OrdinalIgnoreCase))
                {
                    var (comp, layers) = PsdReader.Read(file);
                    var png = comp ?? layers.FirstOrDefault()?.Png ?? throw new InvalidDataException("Le fichier PSD ne contient aucune image composite ni calque visible.");
                    var tmp = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(file) + ".png");
                    try { File.WriteAllBytes(tmp, png); added.Add(ElementLibrary.Import(tmp)); }
                    finally { try { File.Delete(tmp); } catch (IOException) { } }
                }
                else added.Add(ElementLibrary.Import(file));
            }
            LoadBackgrounds();
            if (added.Count > 0) ApplyBackground(added[^1]);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
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
        Pick("Vidéos|*.mkv;*.mp4;*.avi;*.mov;*.m2ts;*.ts;*.wmv", "Vidéo du menu animé", f =>
        {
            if (PageMediaScopeBox.IsChecked == true && current != null)
            {
                var pm = working.PageMedias.FirstOrDefault(m => m.Page.Equals(current.Id, StringComparison.OrdinalIgnoreCase));
                if (pm == null) { pm = new PageMedia { Page = current.Id }; working.PageMedias.Add(pm); }
                pm.LoopFile = f;
            }
            else working.LoopFile = f;
        });

    void OnMusic(object s, RoutedEventArgs e) =>
        Pick("Sons|*.mp3;*.flac;*.wav;*.m4a;*.ogg;*.ac3;*.aac", "Musique du menu", f =>
        {
            if (PageMediaScopeBox.IsChecked == true && current != null)
            {
                var pm = working.PageMedias.FirstOrDefault(m => m.Page.Equals(current.Id, StringComparison.OrdinalIgnoreCase));
                if (pm == null) { pm = new PageMedia { Page = current.Id }; working.PageMedias.Add(pm); }
                pm.Music = f;
            }
            else working.Music = f;
        });

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
        var dlg = new OpenFileDialog { Filter = Loc.Filter("Images|*.jpg;*.jpeg;*.png;*.bmp;*.svg|Tous les fichiers|*.*"), Title = Loc.T($"Fond de la page « {PageName(current.Id)} »") };
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

    void RefreshMineList()
    {
        CustomList.ItemsSource = Templates.Entries();
        MineRenameBtn.IsEnabled = MineDupBtn.IsEnabled = MineDelBtn.IsEnabled = CustomList.SelectedItem is Templates.UserTemplate;
    }

    void OnPickMine(object s, SelectionChangedEventArgs e)
    {
        MineRenameBtn.IsEnabled = MineDupBtn.IsEnabled = MineDelBtn.IsEnabled = CustomList.SelectedItem is Templates.UserTemplate;
        if (CustomList.SelectedItem is not Templates.UserTemplate u) return;
        working.TakeLook(u.Design);
        Rebind();
    }

    void OnQuickBlank(object s, RoutedEventArgs e)
    {
        var blank = BuiltInTemplates.Blank(working.Title);
        working.TakeLook(blank);
        Rebind();
        ShowStep(1);
    }

    void OnQuickTemplate(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string name } || TemplateCatalog.Find(name) is not { } tpl) return;
        working.TakeLook(tpl);
        Rebind();
    }

    void OnMineRename(object s, RoutedEventArgs e)
    {
        if (CustomList.SelectedItem is not Templates.UserTemplate u) return;
        var next = Prompt.Ask(this, "Renommer le modèle", u.Name)?.Trim();
        if (string.IsNullOrEmpty(next) || next == u.Name) return;
        try { Templates.Rename(u.Name, next); RefreshMineList(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "BurnSmith"); }
    }

    void OnMineDuplicate(object s, RoutedEventArgs e)
    {
        if (CustomList.SelectedItem is not Templates.UserTemplate u) return;
        var copy = Prompt.Ask(this, "Nom de la copie", u.Name + " (copie)")?.Trim();
        if (string.IsNullOrEmpty(copy)) return;
        try { Templates.Duplicate(u.Name, copy); RefreshMineList(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "BurnSmith"); }
    }

    void OnMineDelete(object s, RoutedEventArgs e)
    {
        if (CustomList.SelectedItem is not Templates.UserTemplate u) return;
        if (MessageBox.Show(this, $"Supprimer définitivement le modèle « {u.Name} » ?", "BurnSmith", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { Templates.Delete(u.Name); RefreshMineList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "BurnSmith"); }
    }

    void OnSave(object s, RoutedEventArgs e)
    {
        var name = Prompt.Ask(this, "Enregistrer le modèle", working.Name)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        working.Name = name;
        try
        {
            Templates.Save(working);
            RefreshMineList();
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
        Playback = PlaybackOptions.FromMode(Math.Max(0, PlayModeBox.SelectedIndex), Playback.Rewind) with
            { AutoStartSeconds = Playback.AutoStartSeconds, RememberPosition = Playback.RememberPosition };
        if (catalog.Any(f => f.Design.Name == working.Name)) TemplatePicks.Used(working.Name); // « Récemment utilisés »: the template this menu went out with
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
            if (MenuRenderer.IsSvg(file)) return MenuRenderer.SvgPicture(file); // vectors: no decoding, sharp at any tile size
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(file);
            img.DecodePixelWidth = 320; // the tile grows with the library column
            img.CacheOption = BitmapCacheOption.OnLoad; // does not keep the file open: "Retirer" can delete it
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException) { return null; } // InvalidDataException is an IOException
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
