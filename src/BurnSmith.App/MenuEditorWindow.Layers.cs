using System.Windows;
using System.Windows.Controls;
using BurnSmith.Core.Menu;

namespace BurnSmith.App;

public partial class MenuEditorWindow
{
    public sealed record LayerRow(
        string Key,
        MenuElement? Element,
        string? NativeId,
        string DisplayName,
        string KindTag,
        int ZIndex,
        bool IsHidden,
        bool IsLocked)
    {
        public double RowOpacity => IsHidden ? 0.45 : 1.0;
        public double EyeOpacity => IsHidden ? 0.35 : 1.0;
        public double LockOpacity => IsLocked ? 1.0 : 0.3;
    }

    List<LayerRow> BuildLayerRows()
    {
        var rows = new List<LayerRow>();
        if (current == null) return rows;

        // 1. Free elements (all elements targeting this page, even hidden ones so user can unhide)
        foreach (var el in working.Elements.Where(x => x.AppliesTo(current.Id)))
        {
            var idx = working.Elements.IndexOf(el);
            var tag = el.Kind switch
            {
                "button" => "Bouton",
                "image" => "Image",
                "box" => "Cadre",
                _ => "Texte",
            };
            var name = !string.IsNullOrWhiteSpace(el.Text) ? el.Text.Replace('\n', ' ')
                : el.Image != null ? System.IO.Path.GetFileNameWithoutExtension(el.Image)
                : $"{tag} #{idx + 1}";
            var effZ = el.Behind ? el.ZIndex - 1000 : el.ZIndex + 100;
            rows.Add(new LayerRow($"free:{idx}", el, null, name, tag, effZ, el.Hidden, el.Locked));
        }

        // 2. Native elements (buttons, zones, fallback heading, plus any currently hidden overrides on this page)
        var seenNative = new HashSet<string>(StringComparer.Ordinal);
        if (!current.Zones.Any(z => z.Key == "title") && MenuBuilder.FindNativeRect(current, "heading") != null)
        {
            seenNative.Add("heading");
            var ov = FindNativeOverride("heading");
            rows.Add(new LayerRow("native:heading", null, "heading", ReadNativeText("heading"), "Titre page", ov?.ZIndex ?? 0, ov?.Hidden ?? false, ov?.Locked ?? false));
        }
        foreach (var z in current.Zones)
        {
            if (z.Key.StartsWith("name:", StringComparison.Ordinal) || !seenNative.Add(z.Key)) continue;
            var ov = FindNativeOverride(z.Key);
            var label = z.Key == "poster" ? "Vignette / Poster" : z.Key == "title" ? "Titre" : $"Zone {z.Key}";
            rows.Add(new LayerRow($"native:{z.Key}", null, z.Key, label, "Modèle", ov?.ZIndex ?? 0, ov?.Hidden ?? false, ov?.Locked ?? false));
        }
        foreach (var b in current.Buttons)
        {
            if (IsFreeButtonId(b.Id) || !seenNative.Add(b.Id)) continue;
            var ov = FindNativeOverride(b.Id);
            rows.Add(new LayerRow($"native:{b.Id}", null, b.Id, ReadNativeText(b.Id), "Bouton modèle", ov?.ZIndex ?? 0, ov?.Hidden ?? false, ov?.Locked ?? false));
        }
        foreach (var ov in working.NativeOverrides.Where(o => o.Hidden && o.AppliesTo(current.Id) && seenNative.Add(o.Id)))
            rows.Add(new LayerRow($"native:{ov.Id}", null, ov.Id, ov.Text ?? ov.Id, "Masqué", ov.ZIndex, true, ov.Locked));

        // Topmost layer first in UI stack
        return [.. rows.OrderByDescending(r => r.ZIndex)];
    }

    void RefreshLayersDrawer()
    {
        if (LayersList == null) return;
        elemSync = true;
        var rows = BuildLayerRows();
        LayersList.ItemsSource = rows;
        var selectedKey = sel != null ? $"free:{working.Elements.IndexOf(sel)}" : nativeSelId != null ? $"native:{nativeSelId}" : null;
        LayersList.SelectedItem = selectedKey == null ? null : rows.FirstOrDefault(r => r.Key == selectedKey);
        elemSync = false;
    }

    void OnOpenLayersDrawer(object s, RoutedEventArgs e)
    {
        drawerKind = drawerKind == "layers" ? null : "layers";
        ShowStep(1);
        ShowRightPanel();
    }

    LayerRow? FindLayerByTag(object s) =>
        s is FrameworkElement fe && fe.Tag is string key
            ? (LayersList.ItemsSource as IEnumerable<LayerRow>)?.FirstOrDefault(r => r.Key == key)
            : null;

    void OnLayersListSelection(object s, SelectionChangedEventArgs e)
    {
        if (elemSync || LayersList.SelectedItem is not LayerRow row) return;
        if (row.Element != null)
        {
            sel = row.Element;
            nativeSelId = null;
            SyncMultiToPrimary();
        }
        else if (row.NativeId != null)
        {
            sel = null;
            nativeSelId = row.NativeId;
            SyncMultiToPrimary();
        }
        DrawStage();
    }

    void OnLayerToggleHide(object s, RoutedEventArgs e)
    {
        e.Handled = true;
        if (FindLayerByTag(s) is not { } row) return;
        if (row.Element != null) row.Element.Hidden = !row.Element.Hidden;
        else if (row.NativeId != null)
        {
            var ov = GetOrCreateNativeOverride(row.NativeId);
            ov.Hidden = !ov.Hidden;
        }
        Dirty();
        RefreshLayersDrawer();
    }

    void OnLayerToggleLock(object s, RoutedEventArgs e)
    {
        e.Handled = true;
        if (FindLayerByTag(s) is not { } row) return;
        if (row.Element != null) row.Element.Locked = !row.Element.Locked;
        else if (row.NativeId != null)
        {
            var ov = GetOrCreateNativeOverride(row.NativeId);
            ov.Locked = !ov.Locked;
        }
        Dirty();
        RefreshLayersDrawer();
    }

    LayerRow? SelectedLayerRow() =>
        LayersList.SelectedItem as LayerRow
        ?? (LayersList.ItemsSource as IEnumerable<LayerRow>)?.FirstOrDefault(r =>
            (sel != null && r.Element == sel) || (nativeSelId != null && r.NativeId == nativeSelId));

    void MoveSelectedLayer(int delta, bool extreme = false)
    {
        if (SelectedLayerRow() is not { } row) return;
        var rows = (LayersList.ItemsSource as IEnumerable<LayerRow>)?.ToList() ?? BuildLayerRows();
        if (row.Element is { } el)
        {
            if (extreme)
            {
                el.Behind = delta < 0;
                el.ZIndex = delta > 0 ? (rows.Max(r => r.ZIndex) - 99) : (rows.Min(r => r.ZIndex) + 999);
            }
            else
            {
                el.ZIndex += delta;
            }
        }
        else if (row.NativeId is { } nid)
        {
            var ov = GetOrCreateNativeOverride(nid);
            if (extreme)
                ov.ZIndex = delta > 0 ? Math.Max(1, rows.Count) : -Math.Max(1, rows.Count);
            else
                ov.ZIndex += delta;
        }
        Dirty();
        RefreshLayersDrawer();
    }

    void OnLayerUp(object s, RoutedEventArgs e) => MoveSelectedLayer(+1);
    void OnLayerDown(object s, RoutedEventArgs e) => MoveSelectedLayer(-1);
    void OnLayerTop(object s, RoutedEventArgs e) => MoveSelectedLayer(+1, extreme: true);
    void OnLayerBottom(object s, RoutedEventArgs e) => MoveSelectedLayer(-1, extreme: true);

    void OnLayerShowAll(object s, RoutedEventArgs e)
    {
        if (current == null) return;
        foreach (var el in working.Elements.Where(x => x.AppliesTo(current.Id)))
            el.Hidden = false;
        foreach (var ov in working.NativeOverrides.Where(o => o.AppliesTo(current.Id)))
            ov.Hidden = false;
        Dirty();
        RefreshLayersDrawer();
    }
}
