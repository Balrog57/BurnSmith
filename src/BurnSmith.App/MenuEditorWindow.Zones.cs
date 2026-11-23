using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using BurnSmith.Core;
using BurnSmith.Core.Menu;
using Microsoft.Win32;

namespace BurnSmith.App;

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
        if (box.IsKeyboardFocusWithin) Stage.Focus();
        StageCanvas.Children.Remove(box);
        if (keep)
        {
            if (box.Tag is MenuElement el)
            {
                el.Text = box.Text;
                Dirty();
            }
            else if (box.Tag is string tag && tag.StartsWith("native:", StringComparison.Ordinal))
            {
                var nid = tag[7..];
                if (nid == "title" || nid.StartsWith("slot:", StringComparison.Ordinal))
                    SetZone(nid, box.Text);
                else if (nid.StartsWith("name:", StringComparison.Ordinal))
                    SetZone(nid, box.Text);
                else
                {
                    var ov = GetOrCreateNativeOverride(nid);
                    ov.Text = box.Text;
                }
                Dirty();
            }
            else if (box.Tag is string zoneKey)
            {
                SetZone(zoneKey, box.Text);
            }
        }
        FillZonePanel(true);
        ShowElem();
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
                images = await Task.Run(() => MenuRenderer.Sta(() => DrawPage(s.Design, page, DvdPreview)));
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
}
