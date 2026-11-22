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
                    var selLayer = layers.FirstOrDefault(l => l.Name.Contains("select", StringComparison.OrdinalIgnoreCase)
                        || l.Name.Contains("focus", StringComparison.OrdinalIgnoreCase)
                        || l.Name.Contains("hover", StringComparison.OrdinalIgnoreCase));
                    var normLayer = layers.FirstOrDefault(l => l != selLayer && (l.Name.Contains("normal", StringComparison.OrdinalIgnoreCase)
                        || l.Name.Contains("base", StringComparison.OrdinalIgnoreCase))) ?? layers.FirstOrDefault(l => l != selLayer);
                    var normBytes = normLayer?.Png ?? comp ?? throw new InvalidDataException("Le fichier PSD ne contient aucun calque visible.");
                    var normPath = Path.Combine(tmp, stem + ".png");
                    File.WriteAllBytes(normPath, normBytes);
                    string? selPath = null;
                    if (selLayer != null)
                    {
                        selPath = Path.Combine(tmp, stem + "_sel.png");
                        File.WriteAllBytes(selPath, selLayer.Png);
                    }
                    ElementLibrary.ImportButton(normPath, selPath);
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

    // A drag on the page: the element, which part of it was grabbed (0 = the body moves it, 1..4 = a corner resizes it: NW, NE, SW, SE),
    // where the mouse was, and the element's rectangle at that moment. The mouse is captured by the canvas, because the page is redrawn
    // (and the shape under the mouse replaced) as soon as the element is selected.
    (MenuElement El, int Part, System.Windows.Point Start, int X, int Y, int W, int H)? drag;
    bool dragMoved;

    /// <summary>The element outlines on the page on show: click one to edit it, drag it to move it, drag a corner of the selected one to resize it.</summary>
    void DrawElements()
    {
        if (current == null) return;
        var accent = AccentBrush();
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
        if (drag == null || !StageCanvas.IsMouseCaptured) return;
        DragTo(e.GetPosition(StageCanvas), (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) == 0);
    }

    // the alignment marks of the drag in progress (SmartGuides): lines across what the element is aligned with, drawn over the page
    List<SmartGuides.Mark> marks = [];

    /// <summary>The element being dragged follows the mouse to <paramref name="at"/>; with <paramref name="guided"/> (Alt not held) it meets the edges and middles within reach.</summary>
    void DragTo(System.Windows.Point at, bool guided)
    {
        if (drag is not { } d) return;
        int dx = (int)(at.X - d.Start.X), dy = (int)(at.Y - d.Start.Y);
        var el = d.El;
        // what it can be aligned with: the other elements of this page and the template's own buttons (an element's own button is the element)
        var others = guided && current != null
            ? working.Elements.Where(x => x != el && x.AppliesTo(current.Id)).Select(x => x.Box()).Concat(current.Buttons.Where(b => !b.Id.StartsWith('u')).Select(b => b.Rect)).ToList()
            : null;
        marks = [];
        if (d.Part == 0)
        {
            el.X = Math.Clamp(d.X + dx, 0, MenuBuilder.W - 8);
            el.Y = Math.Clamp(d.Y + dy, 0, MenuBuilder.H - 8);
            if (others != null)
            {
                (var box, marks) = SmartGuides.Move(el.Box(), others);
                (el.X, el.Y) = (box.X, box.Y);
            }
        }
        else
        {
            int left = d.X, top = d.Y, right = d.X + d.W, bottom = d.Y + d.H;
            if (d.Part is 1 or 3) left = Math.Clamp(left + dx, 0, right - MinW); else right = Math.Clamp(right + dx, left + MinW, MenuBuilder.W);
            if (d.Part is 1 or 2) top = Math.Clamp(top + dy, 0, bottom - MinH); else bottom = Math.Clamp(bottom + dy, top + MinH, MenuBuilder.H);
            var box = new Box(left, top, right - left, bottom - top);
            if (others != null) (box, marks) = SmartGuides.Resize(box, d.Part is 1 or 3, d.Part is 1 or 2, others, minW: MinW, minH: MinH);
            (el.X, el.Y, el.W, el.H) = (box.X, box.Y, box.W, box.H);
        }
        dragMoved = true;
        ShowElem();
        DrawStage();
    }

    /// <summary>The marks of the drag in progress: a line across everything the element is aligned with, a dashed one in each of two equal gaps.</summary>
    void DrawMarks()
    {
        var ink = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x2B, 0xD6)); // magenta, as picture editors draw them: no menu is that colour
        foreach (var m in marks)
        {
            var (from, to) = m.Spacing ? (m.From, m.To) : (m.From - 24, m.To + 24);
            var line = new System.Windows.Shapes.Line { Stroke = ink, StrokeThickness = 4, IsHitTestVisible = false, X1 = m.Vertical ? m.At : from, X2 = m.Vertical ? m.At : to, Y1 = m.Vertical ? from : m.At, Y2 = m.Vertical ? to : m.At };
            if (m.Spacing) line.StrokeDashArray = [3, 2];
            StageCanvas.Children.Add(line);
        }
    }

    void OnSafeToggle(object s, RoutedEventArgs e) => DrawStage();

    /// <summary>
    /// « Zone sûre »: the action-safe and title-safe frames over the page (SafeArea), each button or text that leaves the title-safe one outlined in orange.
    /// shortcut: the outlines follow the last render, so an element being dragged shows where it left from until the mouse is let go.
    /// </summary>
    void DrawSafe()
    {
        var visual = new System.Windows.Media.DrawingVisual();
        var page = new System.Windows.Rect(0, 0, MenuBuilder.W, MenuBuilder.H);
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new System.Windows.Media.RectangleGeometry(page)); // the picture's bounds are the page's, so it lies on the canvas unscaled
            dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, page);
            SafeArea.Draw(dc, current!, shot?.Design ?? working);
        }
        StageCanvas.Children.Add(new System.Windows.Controls.Image { Source = new System.Windows.Media.DrawingImage(visual.Drawing), Width = MenuBuilder.W, Height = MenuBuilder.H, IsHitTestVisible = false });
    }

    /// <summary>
    /// For the off-screen snapshot (<c>guides</c>): two buttons one under the other, and a third dragged to just beside the place that continues
    /// the column. The status line says where it landed: on the column's edge and at the same gap (300, 620), not where the mouse left it.
    /// </summary>
    public async Task TryGuides()
    {
        MenuElement Button(string text, int x, int y) { var b = new MenuElement { Kind = "button", Page = "root", Text = text, X = x, Y = y, W = 420, H = 90 }; working.Elements.Add(b); return b; }
        Button("Un", 300, 300);
        Button("Deux", 300, 460);
        var third = Button("Trois", 180, 700);
        sel = third;
        drag = (third, 0, new System.Windows.Point(0, 0), third.X, third.Y, third.W, third.H);
        DragTo(new System.Windows.Point(124, -84), true); // to (304, 616): 4 px off each way
        drag = null;
        var shown = marks;
        Dirty();
        await Task.Delay(2500); // the page is drawn again with the three buttons: the marks of the drag are put back over it for the picture
        marks = shown;
        DrawStage();
        Status.Text = $"guides {(third.X == 300 && third.Y == 620 && marks.Count >= 3 ? "ok" : "FAIL")}: {third.X},{third.Y}, {marks.Count} repères";
        await Task.Delay(300); // the page is laid out again before the picture is taken
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
        marks = []; // the marks last as long as the drag
        if (moved) { RefreshElemList(); Dirty(); }
        else DrawStage();
    }

    /// <summary>Keys on the page: with an element selected, Delete removes it, Escape lets it go and the arrows nudge it (10 px, Shift 1 px); otherwise the arrows and Enter walk the buttons like a remote.</summary>
    void OnStageKey(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (editing != null) // the box takes every key but these two
        {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape)
            {
                EndZoneEdit(e.Key == System.Windows.Input.Key.Enter);
                e.Handled = true;
            }
            return;
        }
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
        if (Remote(e.Key)) e.Handled = true;
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

    string? drawerKind; // "bg" | "btn" | null

    void ShowRightPanel()
    {
        if (LibraryDrawer == null) return;
        var hasDrawer = drawerKind != null;
        LibraryDrawer.Visibility = hasDrawer ? Visibility.Visible : Visibility.Collapsed;
        BgDrawerPanel.Visibility = drawerKind == "bg" ? Visibility.Visible : Visibility.Collapsed;
        BtnDrawerPanel.Visibility = drawerKind == "btn" ? Visibility.Visible : Visibility.Collapsed;
        DrawerTitle.Text = drawerKind == "btn" ? "Bibliothèque de boutons" : "Bibliothèque de fonds";
        ElementInspectorPanel.Visibility = !hasDrawer && sel != null ? Visibility.Visible : Visibility.Collapsed;
        MenuSettingsPanel.Visibility = !hasDrawer && sel == null ? Visibility.Visible : Visibility.Collapsed;
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
        Hint.Text = Hints[step];
        BackStep.IsEnabled = step > 0;
        NextStep.IsEnabled = step < Hints.Length - 1;
        if (step != 1) drawerKind = null;
        ShowRightPanel();
        if (Previewing && sel != null) SelectElem(null); else DrawStage();
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
