using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BurnSmith.Core.Menu;

namespace BurnSmith.App;

public partial class MenuEditorWindow
{
    // Uniform drag state: either El != null (free MenuElement) or NativeId != null (native template button/zone).
    // Part: 0 = move, 1..4 = corners NW/NE/SW/SE.
    sealed record SelItem(MenuElement? El, string? NativeId)
    {
        public bool IsFree => El != null;
    }

    readonly List<SelItem> multiSel = [];
    (MenuElement? El, string? NativeId, int Part, Point Start, int X, int Y, int W, int H, List<(SelItem Item, int X, int Y)> Group)? drag;
    Point? lassoStart;
    Point lassoEnd;
    bool dragMoved;
    List<SmartGuides.Mark> marks = [];

    static bool IsFreeButtonId(string id) =>
        id.StartsWith("u-", StringComparison.Ordinal) || (id.StartsWith('u') && int.TryParse(id[1..], out _));

    bool IsGridEnabled => GridToggle?.IsChecked == true;
    static int SnapGrid(int v, int step = 20) => (int)Math.Round(v / (double)step) * step;

    void OnGridToggle(object s, RoutedEventArgs e) => DrawStage();
    void OnArrowsToggle(object s, RoutedEventArgs e) => DrawStage();

    string CurrentFormat => DvdPreview ? "dvd" : Hdmv ? "hdmv" : "bdj";

    void UpdateValidationStatus()
    {
        if (error != null || current == null) return;
        var issues = MenuValidator.Validate(working, current, CurrentFormat);
        var top = issues.FirstOrDefault(i => i.Severity == "error") ?? issues.FirstOrDefault();
        if (top == null) { Status.Text = ""; return; }
        var msg = top.Kind switch
        {
            "overlap" => string.Format(Loc.T("Boutons superposés sur « {0} » : {1} et {2}."), top.PageId, top.ButtonId, top.TargetId),
            "limit" => string.Format(Loc.T("Norme DVD : maximum {0} boutons par page dépassé ({1})."), top.TargetId, current.Buttons.Count),
            "orphan" => string.Format(Loc.T("Bouton inatteignable à la télécommande sur « {0} » : {1}."), top.PageId, top.ButtonId),
            "safe" => string.Format(Loc.T("Hors zone sûre TV (90 %) sur « {0} » : {1}."), top.PageId, top.ButtonId),
            _ => Loc.T(top.Message),
        };
        Status.Text = $"{(top.Severity == "error" ? "⛔" : "⚠")} {msg}";
    }

    void DrawValidationOverlay()
    {
        if (current == null) return;
        var issues = MenuValidator.Validate(working, current, CurrentFormat);
        var danger = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        var warn = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        foreach (var issue in issues.Where(i => i.Kind != "safe" && i.Rect != null))
        {
            var r = issue.Rect!.Value;
            var box = new Rectangle
            {
                Width = r.W, Height = r.H,
                Stroke = issue.Severity == "error" ? danger : warn,
                StrokeThickness = 4,
                StrokeDashArray = [8, 4],
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, r.X);
            Canvas.SetTop(box, r.Y);
            StageCanvas.Children.Add(box);
        }
    }

    void DrawNavArrows()
    {
        if (ArrowsToggle?.IsChecked != true || current == null || current.Buttons.Count < 2) return;
        var byId = current.Buttons.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var penBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in current.Buttons)
        {
            var cx1 = b.Rect.X + b.Rect.W / 2.0;
            var cy1 = b.Rect.Y + b.Rect.H / 2.0;
            foreach (var (dir, targetId) in new[] { ("L", b.Left), ("R", b.Right), ("U", b.Up), ("D", b.Down) })
            {
                if (string.IsNullOrEmpty(targetId) || targetId == b.Id || !byId.TryGetValue(targetId, out var dest)) continue;
                if (!drawn.Add($"{b.Id}->{targetId}:{dir}")) continue;
                var offset = dir switch { "L" => (-12.0, -8.0), "R" => (12.0, 8.0), "U" => (-8.0, -12.0), _ => (8.0, 12.0) };
                var x1 = cx1 + offset.Item1;
                var y1 = cy1 + offset.Item2;
                var x2 = dest.Rect.X + dest.Rect.W / 2.0 + offset.Item1;
                var y2 = dest.Rect.Y + dest.Rect.H / 2.0 + offset.Item2;
                StageCanvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = penBrush, StrokeThickness = 3, Opacity = 0.85, IsHitTestVisible = false });
                var angle = Math.Atan2(y2 - y1, x2 - x1);
                var mx = (x1 + x2 * 2) / 3.0;
                var my = (y1 + y2 * 2) / 3.0;
                foreach (var da in new[] { 2.6, -2.6 })
                {
                    StageCanvas.Children.Add(new Line
                    {
                        X1 = mx, Y1 = my,
                        X2 = mx + 16 * Math.Cos(angle + da), Y2 = my + 16 * Math.Sin(angle + da),
                        Stroke = penBrush, StrokeThickness = 3, IsHitTestVisible = false,
                    });
                }
            }
        }
    }

    void DrawGridOverlay()
    {
        if (!IsGridEnabled) return;
        var minor = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));
        var major = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255));
        for (var x = 40; x < MenuBuilder.W; x += 40)
        {
            StageCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = MenuBuilder.H,
                Stroke = x % 120 == 0 ? major : minor,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }
        for (var y = 40; y < MenuBuilder.H; y += 40)
        {
            StageCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = MenuBuilder.W, Y2 = y,
                Stroke = y % 120 == 0 ? major : minor,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }
    }

    /// <summary>Draws selection outlines, handles, grid, lasso marquee, and zones for every editable target (native + free) in Step 2.</summary>
    void DrawEditableCanvas()
    {
        if (current == null) return;
        DrawGridOverlay();
        var accent = AccentBrush();
        var nativeStroke = new SolidColorBrush(Color.FromArgb(140, accent.Color.R, accent.Color.G, accent.Color.B));

        // 1. Native heading fallback (scenes/settings/credits) if no explicit "title" zone
        if (!current.Zones.Any(z => z.Key == "title") && MenuBuilder.FindNativeRect(current, "heading") is { } headRect)
            DrawNativeBox("heading", headRect, isButton: false, action: null, accent, nativeStroke, "Titre de la page (double-clic pour éditer)");

        // 2. Native template zones (title, poster, slot:..., name:...)
        foreach (var z in current.Zones)
        {
            if (z.Key.StartsWith("name:", StringComparison.Ordinal))
            {
                DrawZone(z);
                continue;
            }
            var tip = z.Key == "poster" ? "Vignette / image (clic : régler, double-clic : changer)" : "Zone du modèle (clic : déplacer/régler, double-clic : éditer)";
            DrawNativeBox(z.Key, z.Rect, isButton: false, action: null, accent, nativeStroke, tip);
        }

        // 3. Native buttons (non-free)
        foreach (var b in current.Buttons)
        {
            if (IsFreeButtonId(b.Id)) continue;
            DrawNativeBox(b.Id, b.Rect, isButton: true, b.Action, accent, nativeStroke, "Bouton du modèle (clic : déplacer/régler, double-clic : texte, Ctrl+clic : ouvrir la cible)");
        }

        // 4. Free elements on top + navigation arrows + normative warnings + marquee lasso + smart guides
        DrawElements();
        DrawValidationOverlay();
        DrawNavArrows();
        DrawLasso();
        DrawMarks();
    }

    void DrawLasso()
    {
        if (lassoStart is not { } p0) return;
        var x = Math.Min(p0.X, lassoEnd.X);
        var y = Math.Min(p0.Y, lassoEnd.Y);
        var w = Math.Abs(lassoEnd.X - p0.X);
        var h = Math.Abs(lassoEnd.Y - p0.Y);
        if (w < 2 && h < 2) return;
        var box = new Rectangle
        {
            Width = w, Height = h,
            Fill = new SolidColorBrush(Color.FromArgb(40, 0x3B, 0x82, 0xF6)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
            StrokeThickness = 2,
            StrokeDashArray = [6, 4],
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        StageCanvas.Children.Add(box);
    }

    bool IsNativeSelected(string id) => (sel == null && nativeSelId == id) || multiSel.Any(m => m.NativeId == id);

    void DrawNativeBox(string id, Box box, bool isButton, MenuAction? action, SolidColorBrush accent, SolidColorBrush subtle, string toolTip)
    {
        var selected = IsNativeSelected(id);
        var isPrimary = sel == null && nativeSelId == id;
        var ov = FindNativeOverride(id);
        var rect = new Rectangle
        {
            Width = box.W, Height = box.H, Fill = Brushes.Transparent,
            Stroke = selected ? accent : Brushes.Transparent,
            StrokeThickness = isPrimary ? 4 : selected ? 3 : 2,
            StrokeDashArray = [6, 4],
            Cursor = ov?.Locked == true ? Cursors.Arrow : Cursors.SizeAll,
            ToolTip = toolTip,
            Tag = "element",
        };
        Canvas.SetLeft(rect, box.X);
        Canvas.SetTop(rect, box.Y);
        if (!selected)
        {
            rect.MouseEnter += (_, _) => rect.Stroke = subtle;
            rect.MouseLeave += (_, _) => rect.Stroke = Brushes.Transparent;
        }
        rect.MouseLeftButtonDown += (_, me) =>
        {
            if (me.ClickCount == 2)
            {
                if (id == "poster") { PosterMenu(); me.Handled = true; return; }
                BeginInlineEditNative(id, box);
                me.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && isButton && action is { Kind: "page", Page: { } targetPage })
            {
                GoTo(targetPage);
                me.Handled = true;
                return;
            }
            Stage.Focus();
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                ToggleMultiNative(id);
                me.Handled = true;
                return;
            }
            if (!selected || multiSel.Count <= 1) SelectNative(id);
            if (ov?.Locked != true)
            {
                drag = (null, id, 0, me.GetPosition(StageCanvas), box.X, box.Y, box.W, box.H, SnapshotGroupStarts());
                dragMoved = false;
                StageCanvas.CaptureMouse();
            }
            me.Handled = true;
        };
        StageCanvas.Children.Add(rect);

        if (!isPrimary) return;
        var h = Math.Min(Handle, Math.Min(box.W, box.H) / 3);
        var right = box.X + box.W - h;
        var bottom = box.Y + box.H - h;
        NativePiece(id, 1, box.X, box.Y, h, h, accent, Cursors.SizeNWSE);
        NativePiece(id, 2, right, box.Y, h, h, accent, Cursors.SizeNESW);
        NativePiece(id, 3, box.X, bottom, h, h, accent, Cursors.SizeNESW);
        NativePiece(id, 4, right, bottom, h, h, accent, Cursors.SizeNWSE);
    }

    void NativePiece(string id, int part, int x, int y, int w, int h, Brush fill, Cursor cursor)
    {
        if (current == null || MenuBuilder.FindNativeRect(current, id) is not { } box) return;
        var shape = new Rectangle { Width = w, Height = h, Fill = fill, Cursor = cursor, Tag = "element" };
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        shape.MouseLeftButtonDown += (_, me) =>
        {
            Stage.Focus();
            var ov = FindNativeOverride(id);
            drag = ov?.Locked == true ? null : (null, id, part, me.GetPosition(StageCanvas), box.X, box.Y, box.W, box.H, []);
            dragMoved = false;
            if (drag != null) StageCanvas.CaptureMouse();
            me.Handled = true;
        };
        StageCanvas.Children.Add(shape);
    }

    /// <summary>The free element outlines on the page on show: click one to edit it, drag it to move it, drag a corner of the selected one to resize it.</summary>
    bool IsFreeSelected(MenuElement el) => el == sel || multiSel.Any(m => m.El == el);

    void DrawElements()
    {
        if (current == null) return;
        var accent = AccentBrush();
        var clear = Brushes.Transparent;
        var here = working.Elements.Where(x => !x.Hidden && x.AppliesTo(current.Id)).OrderBy(x => x.ZIndex).ToList();
        foreach (var el in here.Where(x => x != sel).Concat(here.Where(x => x == sel)))
        {
            var box = el.Box();
            var inSel = IsFreeSelected(el);
            Piece(el, 0, box.X, box.Y, box.W, box.H, clear, accent, el == sel ? 5 : inSel ? 4 : 2, el.Locked ? Cursors.Arrow : Cursors.SizeAll);
            if (el != sel) continue;
            var h = Math.Min(Handle, Math.Min(box.W, box.H) / 3);
            var right = box.X + box.W - h;
            var bottom = box.Y + box.H - h;
            Piece(el, 1, box.X, box.Y, h, h, accent, clear, 0, Cursors.SizeNWSE);
            Piece(el, 2, right, box.Y, h, h, accent, clear, 0, Cursors.SizeNESW);
            Piece(el, 3, box.X, bottom, h, h, accent, clear, 0, Cursors.SizeNESW);
            Piece(el, 4, right, bottom, h, h, accent, clear, 0, Cursors.SizeNWSE);
        }
    }

    void Piece(MenuElement el, int part, int x, int y, int w, int h, Brush fill, Brush stroke, double thickness, Cursor cursor)
    {
        var shape = new Rectangle { Width = w, Height = h, Fill = fill, Stroke = stroke, StrokeThickness = thickness, Cursor = cursor, Tag = "element" };
        if (part == 0) shape.StrokeDashArray = [6, 4];
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        shape.MouseLeftButtonDown += (_, me) =>
        {
            if (part == 0 && me.ClickCount == 2 && el.Kind is "text" or "button")
            {
                BeginInlineEditFree(el);
                me.Handled = true;
                return;
            }
            if (part == 0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && el.IsButton)
            {
                var action = MenuBuilder.Action(working, el.Target, content);
                if (action is { Kind: "page", Page: { } targetPage }) { GoTo(targetPage); me.Handled = true; return; }
            }
            Stage.Focus();
            if (part == 0 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                ToggleMultiFree(el);
                me.Handled = true;
                return;
            }
            if (!IsFreeSelected(el) || multiSel.Count <= 1) SelectElem(el);
            drag = el.Locked ? null : (el, null, part, me.GetPosition(StageCanvas), el.X, el.Y, el.W, el.H, part == 0 ? SnapshotGroupStarts() : []);
            dragMoved = false;
            if (drag != null) StageCanvas.CaptureMouse();
            me.Handled = true;
        };
        StageCanvas.Children.Add(shape);
    }

    void OnCanvasMove(object s, MouseEventArgs e)
    {
        if (lassoStart != null && StageCanvas.IsMouseCaptured)
        {
            lassoEnd = e.GetPosition(StageCanvas);
            DrawStage();
            return;
        }
        if (drag == null || !StageCanvas.IsMouseCaptured) return;
        DragTo(e.GetPosition(StageCanvas), (Keyboard.Modifiers & ModifierKeys.Alt) == 0);
    }

    /// <summary>The element being dragged follows the mouse to <paramref name="at"/>; with <paramref name="guided"/> (Alt not held) it snaps to smart guides / 20 px grid.</summary>
    void DragTo(Point at, bool guided)
    {
        if (drag is not { } d) return;
        int dx = (int)(at.X - d.Start.X), dy = (int)(at.Y - d.Start.Y);
        var others = guided && current != null
            ? working.Elements.Where(x => x != d.El && !multiSel.Any(m => m.El == x) && !x.Hidden && x.AppliesTo(current.Id)).Select(x => x.Box())
                .Concat(current.Buttons.Where(b => b.Id != d.NativeId && !multiSel.Any(m => m.NativeId == b.Id) && !IsFreeButtonId(b.Id)).Select(b => b.Rect))
                .Concat(current.Zones.Where(z => z.Key != d.NativeId && !multiSel.Any(m => m.NativeId == z.Key) && !z.Key.StartsWith("name:", StringComparison.Ordinal)).Select(z => z.Rect)).ToList()
            : null;
        marks = [];
        Box resultBox;
        if (d.Part == 0)
        {
            var rawX = Math.Clamp(d.X + dx, 0, MenuBuilder.W - 8);
            var rawY = Math.Clamp(d.Y + dy, 0, MenuBuilder.H - 8);
            if (guided && IsGridEnabled)
            {
                rawX = Math.Clamp(SnapGrid(rawX), 0, MenuBuilder.W - d.W);
                rawY = Math.Clamp(SnapGrid(rawY), 0, MenuBuilder.H - d.H);
            }
            var raw = new Box(rawX, rawY, d.W, d.H);
            if (others != null && (!IsGridEnabled || marks.Count == 0))
            {
                var snapped = SmartGuides.Move(raw, others);
                if (!IsGridEnabled || snapped.Marks.Count > 0) (resultBox, marks) = snapped;
                else resultBox = raw;
            }
            else resultBox = raw;

            if (d.Group.Count > 1)
            {
                int shiftX = resultBox.X - d.X, shiftY = resultBox.Y - d.Y;
                foreach (var (item, sx, sy) in d.Group)
                {
                    if (item.El is { Locked: false } gel)
                    {
                        var gb = gel.Box();
                        gel.X = Math.Clamp(sx + shiftX, 0, MenuBuilder.W - gb.W);
                        gel.Y = Math.Clamp(sy + shiftY, 0, MenuBuilder.H - gb.H);
                    }
                    else if (item.NativeId is { } gnid && current != null && MenuBuilder.FindNativeRect(current, gnid) is { } nb)
                    {
                        var ov = GetOrCreateNativeOverride(gnid);
                        if (ov.Locked) continue;
                        ov.X = Math.Clamp(sx + shiftX, 0, MenuBuilder.W - (ov.W ?? nb.W));
                        ov.Y = Math.Clamp(sy + shiftY, 0, MenuBuilder.H - (ov.H ?? nb.H));
                    }
                }
                dragMoved = true;
                ShowElem();
                DrawStage();
                return;
            }
        }
        else
        {
            int left = d.X, top = d.Y, right = d.X + d.W, bottom = d.Y + d.H;
            if (d.Part is 1 or 3) left = Math.Clamp(left + dx, 0, right - MinW); else right = Math.Clamp(right + dx, left + MinW, MenuBuilder.W);
            if (d.Part is 1 or 2) top = Math.Clamp(top + dy, 0, bottom - MinH); else bottom = Math.Clamp(bottom + dy, top + MinH, MenuBuilder.H);
            if (guided && IsGridEnabled)
            {
                if (d.Part is 1 or 3) left = Math.Clamp(SnapGrid(left), 0, right - MinW); else right = Math.Clamp(SnapGrid(right), left + MinW, MenuBuilder.W);
                if (d.Part is 1 or 2) top = Math.Clamp(SnapGrid(top), 0, bottom - MinH); else bottom = Math.Clamp(SnapGrid(bottom), top + MinH, MenuBuilder.H);
            }
            var raw = new Box(left, top, right - left, bottom - top);
            resultBox = others != null && !IsGridEnabled ? SmartGuides.Resize(raw, d.Part is 1 or 3, d.Part is 1 or 2, others, minW: MinW, minH: MinH).Box : raw;
        }

        if (d.El is { } el)
        {
            (el.X, el.Y, el.W, el.H) = (resultBox.X, resultBox.Y, resultBox.W, resultBox.H);
        }
        else if (d.NativeId is { } nid)
        {
            var ov = GetOrCreateNativeOverride(nid);
            (ov.X, ov.Y, ov.W, ov.H) = (resultBox.X, resultBox.Y, resultBox.W, resultBox.H);
        }
        dragMoved = true;
        ShowElem();
        DrawStage();
    }

    void DrawMarks()
    {
        var ink = new SolidColorBrush(Color.FromRgb(0xFF, 0x2B, 0xD6));
        foreach (var m in marks)
        {
            var (from, to) = m.Spacing ? (m.From, m.To) : (m.From - 24, m.To + 24);
            var line = new Line { Stroke = ink, StrokeThickness = 4, IsHitTestVisible = false, X1 = m.Vertical ? m.At : from, X2 = m.Vertical ? m.At : to, Y1 = m.Vertical ? from : m.At, Y2 = m.Vertical ? to : m.At };
            if (m.Spacing) line.StrokeDashArray = [3, 2];
            StageCanvas.Children.Add(line);
        }
    }

    void OnSafeToggle(object s, RoutedEventArgs e) => DrawStage();

    void DrawSafe()
    {
        var visual = new DrawingVisual();
        var page = new Rect(0, 0, MenuBuilder.W, MenuBuilder.H);
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(page));
            dc.DrawRectangle(Brushes.Transparent, null, page);
            SafeArea.Draw(dc, current!, shot?.Design ?? working);
        }
        StageCanvas.Children.Add(new System.Windows.Controls.Image { Source = new DrawingImage(visual.Drawing), Width = MenuBuilder.W, Height = MenuBuilder.H, IsHitTestVisible = false });
    }

    /// <summary>For the off-screen snapshot (<c>guides</c>).</summary>
    public async Task TryGuides()
    {
        MenuElement Button(string text, int x, int y) { var b = new MenuElement { Kind = "button", Page = "root", Text = text, X = x, Y = y, W = 420, H = 90 }; working.Elements.Add(b); return b; }
        Button("Un", 300, 300);
        Button("Deux", 300, 460);
        var third = Button("Trois", 180, 700);
        sel = third;
        drag = (third, null, 0, new Point(0, 0), third.X, third.Y, third.W, third.H, []);
        DragTo(new Point(124, -84), true);
        drag = null;
        var shown = marks;
        Dirty();
        await Task.Delay(2500);
        marks = shown;
        DrawStage();
        Status.Text = $"guides {(third.X == 300 && third.Y == 620 && marks.Count >= 3 ? "ok" : "FAIL")}: {third.X},{third.Y}, {marks.Count} repères";
        await Task.Delay(300);
    }

    void OnCanvasUp(object s, MouseButtonEventArgs e)
    {
        if (lassoStart != null)
        {
            FinishLasso();
            if (StageCanvas.IsMouseCaptured) StageCanvas.ReleaseMouseCapture();
            return;
        }
        if (drag == null) return;
        StageCanvas.ReleaseMouseCapture();
    }

    void OnCanvasLost(object s, MouseEventArgs e)
    {
        lassoStart = null;
        var moved = dragMoved;
        drag = null;
        dragMoved = false;
        marks = [];
        if (moved) { RefreshElemList(); Dirty(); }
        else DrawStage();
    }

    void FinishLasso()
    {
        if (lassoStart is not { } p0 || current == null) { lassoStart = null; return; }
        var p1 = lassoEnd;
        lassoStart = null;
        var rx = (int)Math.Min(p0.X, p1.X);
        var ry = (int)Math.Min(p0.Y, p1.Y);
        var rw = (int)Math.Abs(p1.X - p0.X);
        var rh = (int)Math.Abs(p1.Y - p0.Y);
        if (rw < 6 && rh < 6)
        {
            SelectElem(null);
            return;
        }
        var box = new Box(rx, ry, rw, rh);
        static bool Hits(Box a, Box b) => a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;

        multiSel.Clear();
        foreach (var el in working.Elements.Where(x => !x.Hidden && x.AppliesTo(current.Id)))
            if (Hits(box, el.Box())) multiSel.Add(new SelItem(el, null));
        foreach (var b in current.Buttons)
            if (!IsFreeButtonId(b.Id) && Hits(box, b.Rect)) multiSel.Add(new SelItem(null, b.Id));
        foreach (var z in current.Zones)
            if (!z.Key.StartsWith("name:", StringComparison.Ordinal) && Hits(box, z.Rect)) multiSel.Add(new SelItem(null, z.Key));

        if (multiSel.Count == 0) SelectElem(null);
        else
        {
            sel = multiSel[0].El;
            nativeSelId = multiSel[0].NativeId;
            ShowElem();
            DrawStage();
        }
    }

    void OnStageKey(object s, KeyEventArgs e)
    {
        if (editing != null)
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                EndZoneEdit(e.Key == Key.Enter);
                e.Handled = true;
            }
            return;
        }
        if (HasSelection)
        {
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1 : 10;
            switch (e.Key)
            {
                case Key.Delete: OnElemDelete(s, new RoutedEventArgs()); break;
                case Key.Escape: SelectElem(null); break;
                case Key.Left: NudgeSelection(-step, 0); break;
                case Key.Right: NudgeSelection(step, 0); break;
                case Key.Up: NudgeSelection(0, -step); break;
                case Key.Down: NudgeSelection(0, step); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (Remote(e.Key)) e.Handled = true;
    }

    void NudgeSelection(int dx, int dy)
    {
        var items = multiSel.Count > 1 ? multiSel : (sel != null ? [new SelItem(sel, null)] : nativeSelId != null ? new List<SelItem> { new SelItem(null, nativeSelId) } : []);
        foreach (var item in items)
        {
            if (item.El is { } el && !el.Locked)
            {
                var box = el.Box();
                el.X = Math.Clamp(el.X + dx, 0, MenuBuilder.W - box.W);
                el.Y = Math.Clamp(el.Y + dy, 0, MenuBuilder.H - box.H);
            }
            else if (item.NativeId is { } nid && current != null && MenuBuilder.FindNativeRect(current, nid) is { } nbox)
            {
                var ov = GetOrCreateNativeOverride(nid);
                if (ov.Locked) continue;
                ov.X = Math.Clamp((ov.X ?? nbox.X) + dx, 0, MenuBuilder.W - (ov.W ?? nbox.W));
                ov.Y = Math.Clamp((ov.Y ?? nbox.Y) + dy, 0, MenuBuilder.H - (ov.H ?? nbox.H));
            }
        }
        ShowElem();
        DrawStage();
        Dirty();
    }

    void BeginInlineEditFree(MenuElement el)
    {
        EndZoneEdit(true);
        var box = el.Box();
        var tb = new TextBox
        {
            Text = el.Text, Width = Math.Max(box.W, 300), MinHeight = box.H, FontSize = Math.Clamp(box.H * 0.55, 24, 34),
            TextWrapping = box.H >= 100 ? TextWrapping.Wrap : TextWrapping.NoWrap, Tag = el,
        };
        Canvas.SetLeft(tb, Math.Min(box.X, MenuBuilder.W - tb.Width));
        Canvas.SetTop(tb, box.Y);
        tb.LostKeyboardFocus += (_, _) => EndZoneEdit(true);
        tb.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        editing = tb;
        StageCanvas.Children.Add(tb);
    }

    void BeginInlineEditNative(string id, Box box)
    {
        EndZoneEdit(true);
        var initial = ReadNativeText(id);
        var tb = new TextBox
        {
            Text = initial, Width = Math.Max(box.W, 300), MinHeight = box.H, FontSize = Math.Clamp(box.H * 0.55, 24, 34),
            TextWrapping = box.H >= 100 ? TextWrapping.Wrap : TextWrapping.NoWrap, Tag = "native:" + id,
        };
        Canvas.SetLeft(tb, Math.Min(box.X, MenuBuilder.W - tb.Width));
        Canvas.SetTop(tb, box.Y);
        tb.LostKeyboardFocus += (_, _) => EndZoneEdit(true);
        tb.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        editing = tb;
        StageCanvas.Children.Add(tb);
    }

    void SyncMultiToPrimary()
    {
        multiSel.Clear();
        if (sel != null) multiSel.Add(new SelItem(sel, null));
        else if (nativeSelId != null) multiSel.Add(new SelItem(null, nativeSelId));
    }

    void ToggleMultiFree(MenuElement el)
    {
        if (multiSel.Count == 0 && HasSelection) SyncMultiToPrimary();
        var at = multiSel.FindIndex(m => m.El == el);
        if (at >= 0) multiSel.RemoveAt(at);
        else multiSel.Add(new SelItem(el, null));
        ApplyMultiPrimary();
    }

    void ToggleMultiNative(string id)
    {
        if (multiSel.Count == 0 && HasSelection) SyncMultiToPrimary();
        var at = multiSel.FindIndex(m => m.NativeId == id);
        if (at >= 0) multiSel.RemoveAt(at);
        else multiSel.Add(new SelItem(null, id));
        ApplyMultiPrimary();
    }

    void ApplyMultiPrimary()
    {
        if (multiSel.Count == 0) { SelectElem(null); return; }
        var first = multiSel[0];
        sel = first.El;
        nativeSelId = first.NativeId;
        ShowElem();
        DrawStage();
    }

    List<(SelItem Item, int X, int Y)> SnapshotGroupStarts()
    {
        if (multiSel.Count <= 1 || current == null) return [];
        var list = new List<(SelItem, int, int)>(multiSel.Count);
        foreach (var item in multiSel)
        {
            if (item.El is { } el) list.Add((item, el.X, el.Y));
            else if (item.NativeId is { } nid && MenuBuilder.FindNativeRect(current, nid) is { } nb)
            {
                var ov = FindNativeOverride(nid);
                list.Add((item, ov?.X ?? nb.X, ov?.Y ?? nb.Y));
            }
        }
        return list;
    }

    Box? BoxOfItem(SelItem item)
    {
        if (item.El is { } el) return el.Box();
        if (item.NativeId is { } nid && current != null && MenuBuilder.FindNativeRect(current, nid) is { } nb)
        {
            var ov = FindNativeOverride(nid);
            return new Box(ov?.X ?? nb.X, ov?.Y ?? nb.Y, ov?.W ?? nb.W, ov?.H ?? nb.H);
        }
        return null;
    }

    void SetItemPosition(SelItem item, int x, int y)
    {
        if (item.El is { } el) { el.X = x; el.Y = y; }
        else if (item.NativeId is { } nid)
        {
            var ov = GetOrCreateNativeOverride(nid);
            ov.X = x;
            ov.Y = y;
        }
    }

    void OnElemAlignCanvas(object s, RoutedEventArgs e)
    {
        if (!HasSelection || s is not Button btn || btn.Tag is not string dir) return;
        const int safeX = 96, safeY = 54;
        var items = multiSel.Count > 1 ? multiSel : (sel != null ? [new SelItem(sel, null)] : nativeSelId != null ? new List<SelItem> { new SelItem(null, nativeSelId) } : []);
        if (items.Count == 0) return;

        if (dir is "dist-h" or "dist-v")
        {
            var entries = items.Select(i => (Item: i, Box: BoxOfItem(i))).Where(x => x.Box != null).Select(x => (x.Item, Box: x.Box!.Value)).ToList();
            if (entries.Count < 3)
            {
                Status.Text = Loc.T("Sélectionnez au moins 3 éléments (Maj+clic ou lasso) pour répartir.");
                return;
            }
            if (dir == "dist-h")
            {
                entries.Sort((a, b) => a.Box.X.CompareTo(b.Box.X));
                var first = entries[0].Box;
                var last = entries[^1].Box;
                var sumW = entries.Sum(x => x.Box.W);
                var gap = (last.X + last.W - first.X - sumW) / (double)(entries.Count - 1);
                var cur = (double)(first.X + first.W);
                for (var i = 1; i < entries.Count - 1; i++)
                {
                    var nx = Math.Clamp((int)Math.Round(cur + gap), 0, MenuBuilder.W - entries[i].Box.W);
                    SetItemPosition(entries[i].Item, nx, entries[i].Box.Y);
                    cur = nx + entries[i].Box.W;
                }
            }
            else
            {
                entries.Sort((a, b) => a.Box.Y.CompareTo(b.Box.Y));
                var first = entries[0].Box;
                var last = entries[^1].Box;
                var sumH = entries.Sum(x => x.Box.H);
                var gap = (last.Y + last.H - first.Y - sumH) / (double)(entries.Count - 1);
                var cur = (double)(first.Y + first.H);
                for (var i = 1; i < entries.Count - 1; i++)
                {
                    var ny = Math.Clamp((int)Math.Round(cur + gap), 0, MenuBuilder.H - entries[i].Box.H);
                    SetItemPosition(entries[i].Item, entries[i].Box.X, ny);
                    cur = ny + entries[i].Box.H;
                }
            }
            ShowElem();
            Dirty();
            return;
        }

        if (items.Count > 1)
        {
            var boxes = items.Select(i => (Item: i, Box: BoxOfItem(i))).Where(x => x.Box != null).Select(x => (x.Item, Box: x.Box!.Value)).ToList();
            if (boxes.Count == 0) return;
            int minX = boxes.Min(x => x.Box.X), maxR = boxes.Max(x => x.Box.X + x.Box.W);
            int minY = boxes.Min(x => x.Box.Y), maxB = boxes.Max(x => x.Box.Y + x.Box.H);
            int midX = (minX + maxR) / 2, midY = (minY + maxB) / 2;
            foreach (var (item, b) in boxes)
            {
                switch (dir)
                {
                    case "left": SetItemPosition(item, minX, b.Y); break;
                    case "center-h": SetItemPosition(item, midX - b.W / 2, b.Y); break;
                    case "right": SetItemPosition(item, maxR - b.W, b.Y); break;
                    case "top": SetItemPosition(item, b.X, minY); break;
                    case "center-v": SetItemPosition(item, b.X, midY - b.H / 2); break;
                    case "bottom": SetItemPosition(item, b.X, maxB - b.H); break;
                }
            }
        }
        else if (sel is { } el)
        {
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
        }
        else if (nativeSelId is { } nid && current != null && MenuBuilder.FindNativeRect(current, nid) is { } box)
        {
            var ov = GetOrCreateNativeOverride(nid);
            int w = ov.W ?? box.W, h = ov.H ?? box.H;
            switch (dir)
            {
                case "left": ov.X = safeX; break;
                case "center-h": ov.X = Math.Max(0, (MenuBuilder.W - w) / 2); break;
                case "right": ov.X = Math.Max(0, MenuBuilder.W - safeX - w); break;
                case "top": ov.Y = safeY; break;
                case "center-v": ov.Y = Math.Max(0, (MenuBuilder.H - h) / 2); break;
                case "bottom": ov.Y = Math.Max(0, MenuBuilder.H - safeY - h); break;
                default: return;
            }
        }
        ShowElem();
        Dirty();
    }
}
