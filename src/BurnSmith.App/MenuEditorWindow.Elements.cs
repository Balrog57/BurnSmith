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
    /// <summary>A click on empty canvas starts a marquee lasso in Step 2 (or deselects on simple click).</summary>
    void OnStageMouse(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        Stage.Focus();
        if (!Previewing && e.ChangedButton == System.Windows.Input.MouseButton.Left && (e.OriginalSource as FrameworkElement)?.Tag as string != "element")
        {
            lassoStart = lassoEnd = e.GetPosition(StageCanvas);
            StageCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (HasSelection && (e.OriginalSource as FrameworkElement)?.Tag as string != "element") SelectElem(null);
    }

    // ---------------------------------------------------------------- free elements & native overrides
    MenuElement? sel;
    string? nativeSelId;
    bool elemSync;
    const int Handle = 30, MinW = 40, MinH = 20;
    bool HasSelection => sel != null || nativeSelId != null || multiSel.Count > 0;

    NativeElementOverride? FindNativeOverride(string id) =>
        current == null ? null : working.NativeOverrides.FirstOrDefault(o => o.Id == id && o.AppliesTo(current.Id));

    NativeElementOverride GetOrCreateNativeOverride(string id)
    {
        var pageId = current?.Id ?? "root";
        if (working.NativeOverrides.FirstOrDefault(o => o.Id == id && o.Page.Equals(pageId, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;
        if (working.NativeOverrides.FirstOrDefault(o => o.Id == id && o.AppliesTo(pageId)) is { } matched)
        {
            var copy = matched.Copy();
            copy.Page = pageId;
            working.NativeOverrides.Add(copy);
            return copy;
        }
        var ov = new NativeElementOverride { Page = pageId, Id = id };
        working.NativeOverrides.Add(ov);
        return ov;
    }

    string ReadNativeText(string id)
    {
        if (FindNativeOverride(id)?.Text is { } ovText) return ovText;
        if (id == "title") return working.Title;
        if (id.StartsWith("slot:", StringComparison.Ordinal)) return working.TextZones.GetValueOrDefault(id[5..], "");
        if (current != null && MenuBuilder.FindNativeRect(current, id) is { } rect)
        {
            var rawOps = current.Ops.Select(o => o is StyledOp so ? so.Inner : o).ToList();
            if (rawOps.OfType<BurnSmith.Core.Menu.Text>().FirstOrDefault(t => t.Rect == rect) is { } txt) return txt.Value;
            if (rawOps.OfType<BurnSmith.Core.Menu.Label>().FirstOrDefault(l => l.Rect == rect) is { } lbl) return lbl.Value;
            if (rawOps.OfType<BurnSmith.Core.Menu.Label>().FirstOrDefault(l => Math.Abs(l.Rect.X - rect.X) <= 10 && Math.Abs(l.Rect.W - rect.W) <= 20
                && l.Rect.Y >= rect.Y + rect.H - 12 && l.Rect.Y <= rect.Y + rect.H + 30) is { } below) return below.Value;
        }
        return id;
    }

    void SelectNative(string? id)
    {
        sel = null;
        nativeSelId = id;
        SyncMultiToPrimary();
        elemSync = true;
        ElemList.SelectedIndex = -1;
        elemSync = false;
        if (id != null && drawerKind != "layers") { drawerKind = null; ShowStep(1); }
        ShowElem();
        if (drawerKind == "layers") RefreshLayersDrawer();
        DrawStage();
    }

    void OnResetNative(object s, RoutedEventArgs e)
    {
        if (nativeSelId is not { } nid || current == null) return;
        working.NativeOverrides.RemoveAll(o => o.Id == nid && o.AppliesTo(current.Id));
        ShowElem();
        Dirty();
    }

    void OnConvertFree(object s, RoutedEventArgs e)
    {
        if (current == null) return;
        MenuBuilder.ConvertPageToFree(working, current);
        RefreshElemList();
        Rebind();
        ShowRightPanel();
    }

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

    sealed record ChoiceItem(string Value, string Label);

    string? CurrentButtonId()
    {
        if (sel is { IsButton: true } el) return el.NavigationId;
        if (nativeSelId is { } nid && current != null && current.Buttons.Any(b => b.Id == nid)) return nid;
        return null;
    }

    List<ChoiceItem> BuildTargetChoices(string? curTarget)
    {
        var list = new List<ChoiceItem>
        {
            new("movie", Loc.T("Lire le film principal")),
            new("all", Loc.T("Tout lire à la suite")),
        };
        var titleCount = Math.Max(1, content.Titles?.Count ?? 1);
        for (var t = 1; t <= Math.Min(titleCount, 20); t++)
            list.Add(new($"title:{t}", string.Format(Loc.T("Lire la vidéo {0}"), t)));
        var chapCount = Math.Min(content.ChapterPictures.Count, 24);
        for (var ch = 1; ch <= chapCount; ch++)
            list.Add(new($"chapter:1:{ch}", string.Format(Loc.T("Chapitre {0}"), ch)));
        var audios = content.Audio ?? [];
        for (var a = 0; a < audios.Count; a++)
            list.Add(new($"audio:{a + 1}", string.Format(Loc.T("Audio : {0}"), audios[a])));
        list.Add(new("sub:0", Loc.T("Sous-titres : Désactivés")));
        var subs = content.Subtitles ?? [];
        for (var sb = 0; sb < subs.Count; sb++)
            list.Add(new($"sub:{sb + 1}", string.Format(Loc.T("Sous-titre : {0}"), subs[sb])));
        list.Add(new("root", Loc.T("Page : Menu principal")));
        list.Add(new("scenes1", Loc.T("Page : Chapitres / Épisodes")));
        list.Add(new("settings", Loc.T("Page : Réglages")));
        list.Add(new("credits", Loc.T("Page : Crédits")));
        list.Add(new("bonus1", Loc.T("Page : Bonus")));
        foreach (var cp in working.CustomPages)
            list.Add(new(cp.Id, string.Format(Loc.T("Page : {0}"), cp.Title)));
        list.Add(new("resume", Loc.T("Reprendre le film (Pop-up)")));
        list.Add(new("menu", Loc.T("Retour au menu du disque (Pop-up)")));
        if (!string.IsNullOrEmpty(curTarget) && !list.Any(x => x.Value == curTarget))
            list.Add(new(curTarget, curTarget));
        return list;
    }

    List<ChoiceItem> BuildNavButtonChoices()
    {
        var list = new List<ChoiceItem>
        {
            new("__auto__", Loc.T("(Automatique)")),
            new("-", Loc.T("(Aucun / bloqué)")),
        };
        if (current != null)
        {
            foreach (var b in current.Buttons)
                list.Add(new(b.Id, $"{ReadNativeText(b.Id)} ({b.Id})"));
        }
        return list;
    }

    void PopulateNavSection(string? btnId)
    {
        var isBtn = btnId != null && current != null;
        foreach (var box in new FrameworkElement[] { ElemDefaultBtn, NavUpBox, NavDownBox, NavLeftBox, NavRightBox })
            box.IsEnabled = isBtn;
        if (!isBtn || current == null || btnId == null)
        {
            ElemDefaultBtn.IsChecked = false;
            return;
        }
        var curDef = working.DefaultButtons.TryGetValue(current.Id, out var d1) ? d1
            : working.DefaultButtons.TryGetValue(MenuDesign.PageKind(current.Id), out var d2) ? d2 : null;
        ElemDefaultBtn.IsChecked = curDef != null ? curDef == btnId : current.Buttons.FirstOrDefault()?.Id == btnId;

        var choices = BuildNavButtonChoices();
        string DirVal(string dir) => working.Directions.FirstOrDefault(l => l.Page == current.Id && l.Button == btnId && l.Direction == dir)?.Target ?? "__auto__";
        foreach (var (box, dir) in new[] { (NavUpBox, "up"), (NavDownBox, "down"), (NavLeftBox, "left"), (NavRightBox, "right") })
        {
            box.ItemsSource = choices;
            box.SelectedValue = DirVal(dir);
        }
    }

    /// <summary>Puts the selected element (free or native) values into the inspector panel.</summary>
    void ShowElem()
    {
        elemSync = true;
        var isFree = sel != null;
        var isNative = sel == null && nativeSelId != null && current != null;
        var on = isFree || isNative;
        foreach (var c in new FrameworkElement[] { ElemPage, ElemText, ElemImage, ElemFocus, ElemActive, ElemLook, ElemCopy, ElemDelBtn, ElemX, ElemY, ElemW, ElemH, ElemSize, ElemColor, ElemFill, ElemBorder, ElemBorderW, ElemOpacity, ElemLayerOpacity, ElemRotation, ElemOutline, ElemOutlineW, ElemShadow, ElemShadowBlur, ElemShadowOffset, ElemImageBlur, ElemAlign, ElemTarget, ElemBold, ElemItalic, ElemBehind })
            c.IsEnabled = on;
        DistHBtn.IsEnabled = multiSel.Count >= 3;
        DistVBtn.IsEnabled = multiSel.Count >= 3;
        ResetNativeBtn.Visibility = isNative && FindNativeOverride(nativeSelId!) != null ? Visibility.Visible : Visibility.Collapsed;
        ElemDelBtn.Content = Loc.T(isNative ? "Masquer" : "Supprimer");

        if (sel is { } e)
        {
            ElemCopy.IsEnabled = true;
            ElemPage.IsEnabled = true;
            ElemBehind.IsEnabled = true;
            ElemPage.ItemsSource = PageChoices.Concat(working.CustomPages.Select(page => new PageChoice(page.Id, page.Title)))
                .Concat(PageChoices.Any(page => page.Value == e.Page) || working.CustomPages.Any(page => page.Id == e.Page)
                    ? [] : [new PageChoice(e.Page, e.Page)]).ToList();
            ElemTarget.ItemsSource = BuildTargetChoices(e.Target);
            ElemTarget.IsEnabled = e.IsButton;
            ElemPage.SelectedValue = e.Page;
            ElemText.Text = e.Text;
            ElemImage.Text = e.Image ?? "";
            ElemFocus.Text = e.Focus ?? "";
            ElemActive.Text = e.ActiveImage ?? "";
            ElemActive.IsEnabled = e.IsButton;
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
            ElemLayerOpacity.Value = Math.Clamp(e.ElementOpacity, 0, 100);
            ElemRotation.Value = Math.Clamp(e.Rotation, -180, 180);
            ElemOutline.Text = e.Outline ?? "#000000";
            ElemOutlineW.Value = Math.Clamp(e.OutlineWidth, 0, 12);
            ElemShadow.Text = e.ShadowColor ?? "";
            ElemShadowBlur.Value = Math.Clamp(e.ShadowBlur, 0, 24);
            ElemShadowOffset.Value = Math.Clamp(e.ShadowOffsetX, -24, 24);
            ElemImageBlur.Value = Math.Clamp(e.ImageBlur, 0, 30);
            ElemImageBlur.IsEnabled = e.Kind == "image";
            ElemAlign.SelectedItem = e.Align;
            ElemTarget.SelectedValue = e.Target;
            ElemBold.IsChecked = e.Bold;
            ElemItalic.IsChecked = e.Italic;
            ElemBehind.IsChecked = e.Behind;
            ElemText.IsEnabled = e.Kind != "image";
            PopulateNavSection(e.IsButton ? e.NavigationId : null);
        }
        else if (isNative && current != null && nativeSelId is { } nid)
        {
            var ov = FindNativeOverride(nid);
            var nbtn = current.Buttons.FirstOrDefault(b => b.Id == nid);
            var nbox = MenuBuilder.FindNativeRect(current, nid) ?? new Box(0, 0, 200, 80);
            ElemCopy.IsEnabled = false;
            ElemPage.IsEnabled = false;
            ElemBehind.IsEnabled = false;
            ElemPage.ItemsSource = PageChoices;
            ElemPage.SelectedValue = MenuDesign.PageKind(current.Id) is "scenes" or "bonus" or "settings" or "credits" or "root" ? MenuDesign.PageKind(current.Id) : "all";
            var actTarget = ov?.Target ?? (nbtn != null ? MenuBuilder.ActionTarget(nbtn.Action) : "movie");
            ElemTarget.ItemsSource = BuildTargetChoices(actTarget);
            ElemTarget.IsEnabled = nbtn != null;
            ElemTarget.SelectedValue = actTarget;
            ElemText.Text = ReadNativeText(nid);
            ElemText.IsEnabled = nid != "poster";
            ElemImage.Text = ov?.Image ?? "";
            ElemFocus.Text = ov?.Focus ?? nbtn?.FocusImage ?? "";
            ElemActive.Text = ov?.ActiveImage ?? nbtn?.DvdSelectImage ?? "";
            ElemActive.IsEnabled = nbtn != null;
            ElemLook.SelectedValue = ov?.Look ?? "";
            ElemLook.IsEnabled = nbtn != null;
            ElemX.Value = ov?.X ?? nbox.X;
            ElemY.Value = ov?.Y ?? nbox.Y;
            ElemW.Value = Math.Clamp(ov?.W ?? nbox.W, 40, 1920);
            ElemH.Value = Math.Clamp(ov?.H ?? nbox.H, 20, 1080);
            ElemSize.Value = Math.Clamp(ov?.Size ?? 48, 12, 160);
            ElemColor.Text = ov?.Color ?? working.TextColor;
            ElemFill.Text = ov?.Fill ?? working.CardFill;
            ElemBorder.Text = ov?.Border ?? "#FFFFFF";
            ElemBorderW.Value = Math.Clamp(ov?.BorderWidth ?? 0, 0, 16);
            ElemOpacity.Value = Math.Clamp(ov?.Opacity ?? 100, 0, 100);
            ElemLayerOpacity.Value = Math.Clamp(ov?.Opacity ?? 100, 0, 100);
            ElemRotation.Value = Math.Clamp(ov?.Rotation ?? 0, -180, 180);
            ElemOutline.Text = ov?.Outline ?? "#000000";
            ElemOutlineW.Value = Math.Clamp(ov?.OutlineWidth ?? 0, 0, 12);
            ElemShadow.Text = ov?.ShadowColor ?? "";
            ElemShadowBlur.Value = Math.Clamp(ov?.ShadowBlur ?? 0, 0, 24);
            ElemShadowOffset.Value = Math.Clamp(ov?.ShadowOffsetX ?? 0, -24, 24);
            ElemImageBlur.Value = Math.Clamp(ov?.ImageBlur ?? 0, 0, 30);
            ElemImageBlur.IsEnabled = nid == "poster";
            ElemAlign.SelectedItem = ov?.Align ?? "center";
            ElemBold.IsChecked = ov?.Bold ?? true;
            ElemItalic.IsChecked = ov?.Italic ?? false;
            PopulateNavSection(nbtn != null ? nid : null);
        }
        else
        {
            PopulateNavSection(null);
        }
        elemSync = false;
        ShowRightPanel();
    }

    /// <summary>Takes the panel's values into the selected free element or native override.</summary>
    void SyncElem()
    {
        if (elemSync) return;
        if (sel is { } e)
        {
            e.Page = ElemPage.SelectedValue as string ?? MenuElement.AllPages;
            e.Text = ElemText.Text;
            e.Image = string.IsNullOrWhiteSpace(ElemImage.Text) ? null : ElemImage.Text.Trim();
            e.Focus = string.IsNullOrWhiteSpace(ElemFocus.Text) ? null : ElemFocus.Text.Trim();
            e.ActiveImage = string.IsNullOrWhiteSpace(ElemActive.Text) ? null : ElemActive.Text.Trim();
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
            e.ElementOpacity = (int)ElemLayerOpacity.Value;
            e.Rotation = ElemRotation.Value;
            e.OutlineWidth = (int)ElemOutlineW.Value;
            e.Outline = e.OutlineWidth > 0 && !string.IsNullOrWhiteSpace(ElemOutline.Text) ? ElemOutline.Text.Trim() : null;
            e.ShadowColor = string.IsNullOrWhiteSpace(ElemShadow.Text) ? null : ElemShadow.Text.Trim();
            e.ShadowBlur = (int)ElemShadowBlur.Value;
            e.ShadowOffsetX = (int)ElemShadowOffset.Value;
            e.ShadowOffsetY = (int)ElemShadowOffset.Value;
            e.ImageBlur = (int)ElemImageBlur.Value;
            e.Align = ElemAlign.SelectedItem as string ?? "left";
            e.Target = ElemTarget.SelectedValue as string ?? "movie";
            e.Bold = ElemBold.IsChecked == true;
            e.Italic = ElemItalic.IsChecked == true;
            e.Behind = ElemBehind.IsChecked == true;
            RefreshElemList();
            Dirty();
        }
        else if (nativeSelId is { } nid && current != null)
        {
            var ov = GetOrCreateNativeOverride(nid);
            if (ElemText.IsEnabled)
            {
                if (nid == "title") working.Title = ElemText.Text;
                else if (nid.StartsWith("slot:", StringComparison.Ordinal)) SetZone(nid, ElemText.Text);
                else ov.Text = ElemText.Text;
            }
            ov.Image = string.IsNullOrWhiteSpace(ElemImage.Text) ? null : ElemImage.Text.Trim();
            ov.Focus = string.IsNullOrWhiteSpace(ElemFocus.Text) ? null : ElemFocus.Text.Trim();
            ov.ActiveImage = string.IsNullOrWhiteSpace(ElemActive.Text) ? null : ElemActive.Text.Trim();
            ov.Look = ElemLook.SelectedValue is string lk && lk.Length > 0 ? lk : null;
            ov.X = (int)ElemX.Value;
            ov.Y = (int)ElemY.Value;
            ov.W = (int)ElemW.Value;
            ov.H = (int)ElemH.Value;
            ov.Size = (int)ElemSize.Value;
            ov.Color = ElemColor.Text != working.TextColor ? ElemColor.Text : ov.Color;
            ov.Fill = ElemFill.Text != working.CardFill ? ElemFill.Text : ov.Fill;
            ov.Opacity = (int)ElemLayerOpacity.Value;
            ov.Rotation = ElemRotation.Value;
            ov.OutlineWidth = (int)ElemOutlineW.Value;
            ov.Outline = ov.OutlineWidth > 0 && !string.IsNullOrWhiteSpace(ElemOutline.Text) ? ElemOutline.Text.Trim() : null;
            ov.ShadowColor = string.IsNullOrWhiteSpace(ElemShadow.Text) ? null : ElemShadow.Text.Trim();
            ov.ShadowBlur = (int)ElemShadowBlur.Value;
            ov.ShadowOffsetX = (int)ElemShadowOffset.Value;
            ov.ShadowOffsetY = (int)ElemShadowOffset.Value;
            ov.ImageBlur = (int)ElemImageBlur.Value;
            ov.Align = ElemAlign.SelectedItem as string;
            if (ElemTarget.IsEnabled && ElemTarget.SelectedValue is string tgt) ov.Target = tgt;
            ov.Bold = ElemBold.IsChecked;
            ov.Italic = ElemItalic.IsChecked;
            Dirty();
        }
    }

    void SelectElem(MenuElement? e)
    {
        sel = e;
        nativeSelId = null;
        SyncMultiToPrimary();
        elemSync = true;
        ElemList.SelectedIndex = e == null ? -1 : working.Elements.IndexOf(e);
        elemSync = false;
        if (e != null && drawerKind != "layers") { drawerKind = null; ShowStep(1); } // contextual inspector opens in Personnaliser
        ShowElem();
        if (drawerKind == "layers") RefreshLayersDrawer();
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

    void PickElemImage(string title, Action<MenuElement, string> onEl, Action<NativeElementOverride, string> onOv)
    {
        if (!HasSelection) return;
        var dlg = new OpenFileDialog { Filter = Loc.Filter("Images|*.jpg;*.jpeg;*.png;*.bmp;*.svg|Tous les fichiers|*.*"), Title = Loc.T(title) };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var refPath = ElementLibrary.Import(dlg.FileName).Path;
            if (sel is { } el) onEl(el, refPath);
            else if (nativeSelId is { } nid) onOv(GetOrCreateNativeOverride(nid), refPath);
            ShowElem();
            Dirty();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "BurnSmith");
        }
    }

    void OnElemImage(object s, RoutedEventArgs e) =>
        PickElemImage("Image de l'élément", (el, p) => el.Image = p, (ov, p) => ov.Image = p);

    /// <summary>The picture a button image shows while it is selected (its "sub" twin).</summary>
    void OnElemFocus(object s, RoutedEventArgs e) =>
        PickElemImage("Image du bouton sélectionné", (el, p) => el.Focus = p, (ov, p) => ov.Focus = p);

    void OnElemActive(object s, RoutedEventArgs e) =>
        PickElemImage("Image du bouton activé (OK)", (el, p) => el.ActiveImage = p, (ov, p) => ov.ActiveImage = p);

    void OnPreviewStateChanged(object s, RoutedEventArgs e)
    {
        if (s is not RadioButton rb || rb.Tag is not string st) return;
        previewState = st;
        if (StageCanvas != null) DrawStage();
    }

    void OnDefaultBtnToggle(object s, RoutedEventArgs e)
    {
        if (elemSync || current == null || CurrentButtonId() is not { } bid) return;
        if (ElemDefaultBtn.IsChecked == true) working.DefaultButtons[current.Id] = bid;
        else working.DefaultButtons.Remove(current.Id);
        Dirty();
    }

    void OnNavDirPick(object s, SelectionChangedEventArgs e)
    {
        if (elemSync || current == null || CurrentButtonId() is not { } bid || s is not ComboBox cb || cb.SelectedValue is not string val) return;
        var dir = cb == NavUpBox ? "up" : cb == NavDownBox ? "down" : cb == NavLeftBox ? "left" : "right";
        working.Directions.RemoveAll(l => l.Page == current.Id && l.Button == bid && l.Direction == dir);
        if (val != "__auto__") working.Directions.Add(new(current.Id, bid, dir, val));
        Dirty();
    }

    void OnAutoNavRelink(object s, RoutedEventArgs e)
    {
        if (current == null) return;
        working.Directions.RemoveAll(l => l.Page == current.Id);
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
        if (multiSel.Count > 1 && current != null)
        {
            foreach (var item in multiSel.ToList())
            {
                if (item.El is { } mel) working.Elements.Remove(mel);
                else if (item.NativeId is { } mnid) GetOrCreateNativeOverride(mnid).Hidden = true;
            }
            SelectElem(null);
            RefreshElemList();
            Dirty();
        }
        else if (sel is { } el)
        {
            working.Elements.Remove(el);
            SelectElem(null);
            RefreshElemList();
            Dirty();
        }
        else if (nativeSelId is { } nid && current != null)
        {
            GetOrCreateNativeOverride(nid).Hidden = true;
            SelectNative(null);
            Dirty();
        }
    }

}
