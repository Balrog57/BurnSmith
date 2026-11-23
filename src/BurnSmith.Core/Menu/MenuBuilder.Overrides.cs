namespace BurnSmith.Core.Menu;

public static partial class MenuBuilder
{
    /// <summary>Returns the bounding rectangle of an <see cref="Op"/>, or null if it has none.</summary>
    public static Box? OpRect(Op op) => op switch
    {
        StyledOp s => OpRect(s.Inner),
        Fill f => f.Rect,
        Border b => b.Rect,
        Label l => l.Rect,
        Picture p => p.Rect,
        Image i => i.Rect,
        Text t => t.Rect,
        Framed fr => fr.Rect,
        _ => null,
    };

    /// <summary>Wraps <paramref name="op"/> in <see cref="StyledOp"/> only when visual effects differ from defaults.</summary>
    internal static Op WithEffects(Op op, Box bounds, int opacity, double rotation, string? shadowColor, int shadowBlur, int shadowDx, int shadowDy, int imageBlur, string? tintColor, int tintStrength)
    {
        var opNorm = Math.Clamp(opacity, 0, 100) / 100.0;
        var hasShadow = !string.IsNullOrWhiteSpace(shadowColor);
        var hasTint = !string.IsNullOrWhiteSpace(tintColor) && tintStrength > 0;
        if (opNorm >= 0.999 && Math.Abs(rotation) < 0.01 && !hasShadow && imageBlur <= 0 && !hasTint)
            return op;
        return new StyledOp(op, opNorm, rotation, bounds, hasShadow ? shadowColor : null, shadowBlur, shadowDx, shadowDy, imageBlur, hasTint ? tintColor : null, tintStrength);
    }

    static bool IsBackdropOrFull(Op op) => op switch
    {
        Image { Backdrop: true } => true,
        Fill f => f.Rect.W >= W - 20 && f.Rect.H >= H - 20,
        _ => false,
    };

    static bool ContainsBox(Box outer, Box inner, int tol = 8) =>
        inner.X >= outer.X - tol && inner.Y >= outer.Y - tol
        && inner.X + inner.W <= outer.X + outer.W + tol
        && inner.Y + inner.H <= outer.Y + outer.H + tol;

    static Box TransformBox(Box b, Box from, Box to)
    {
        if (from.W <= 0 || from.H <= 0) return to;
        var nx = to.X + (int)Math.Round((b.X - from.X) * (double)to.W / from.W);
        var ny = to.Y + (int)Math.Round((b.Y - from.Y) * (double)to.H / from.H);
        var nw = Math.Max(4, (int)Math.Round(b.W * (double)to.W / from.W));
        var nh = Math.Max(4, (int)Math.Round(b.H * (double)to.H / from.H));
        return new Box(nx, ny, nw, nh);
    }

    static Op MoveOrResizeOp(Op op, Box from, Box to, NativeElementOverride ov) => op switch
    {
        StyledOp s => s with
        {
            Inner = MoveOrResizeOp(s.Inner, from, to, ov),
            Bounds = to,
        },
        Fill f => f with
        {
            Rect = TransformBox(f.Rect, from, to),
            Color = ov.Fill ?? f.Color,
            Opacity = ov.Opacity < 100 ? Math.Clamp(ov.Opacity, 0, 100) / 100.0 * f.Opacity : f.Opacity,
            Radius = ov.Radius ?? f.Radius,
        },
        Border b => b with
        {
            Rect = TransformBox(b.Rect, from, to),
            Color = ov.Border ?? b.Color,
            Width = ov.BorderWidth ?? b.Width,
            Radius = ov.Radius ?? b.Radius,
        },
        Label l => l with
        {
            Rect = TransformBox(l.Rect, from, to),
            Value = ov.Text ?? l.Value,
            Size = ov.Size ?? l.Size,
            Bold = ov.Bold ?? l.Bold,
            Align = ov.Align ?? l.Align,
            Ink = ov.Color ?? l.Ink,
        },
        Picture p => p with
        {
            Rect = TransformBox(p.Rect, from, to),
            Path = ov.Image is { } img && (ElementLibrary.Resolve(img) ?? img) is { } res && File.Exists(res) ? res : p.Path,
            Radius = ov.Radius ?? p.Radius,
            BorderWidth = ov.BorderWidth ?? p.BorderWidth,
        },
        Image i => i with
        {
            Rect = TransformBox(i.Rect, from, to),
            Path = ov.Image is { } img && (ElementLibrary.Resolve(img) ?? img) is { } res && File.Exists(res) ? res : i.Path,
        },
        Text t => t with
        {
            Rect = TransformBox(t.Rect, from, to),
            Value = ov.Text ?? t.Value,
            Size = ov.Size ?? t.Size,
            Font = ov.Font ?? t.Font,
            Color = ov.Color ?? t.Color,
            Bold = ov.Bold ?? t.Bold,
            Italic = ov.Italic ?? t.Italic,
            Align = ov.Align ?? t.Align,
            Stroke = ov.OutlineWidth > 0 ? (ov.Outline ?? t.Stroke ?? "#000000") : ov.Outline ?? t.Stroke,
            StrokeWidth = ov.OutlineWidth > 0 ? ov.OutlineWidth : t.StrokeWidth,
        },
        Framed fr => fr with
        {
            Rect = TransformBox(fr.Rect, from, to),
            Picture = ov.Image is { } img && (ElementLibrary.Resolve(img) ?? img) is { } res && File.Exists(res) ? res : fr.Picture,
        },
        _ => op,
    };

    static List<int> MatchOpIndices(MenuPage page, string id, Box origRect, bool isButton)
    {
        var matches = new List<int>();
        for (var i = 0; i < page.Ops.Count; i++)
        {
            var op = page.Ops[i];
            if (IsBackdropOrFull(op) || OpRect(op) is not { } r) continue;
            if (r == origRect || ContainsBox(origRect, r))
            {
                matches.Add(i);
                continue;
            }
            // Thumbnail captions live just below cell thumbnails (Original 3x2 grid)
            if (isButton && op is Label && Math.Abs(r.X - origRect.X) <= 10 && Math.Abs(r.W - origRect.W) <= 20
                && r.Y >= origRect.Y + origRect.H - 12 && r.Y <= origRect.Y + origRect.H + 30)
                matches.Add(i);
            // Zone heading
            if (id == "heading" && op is Label && r.Y < 200 && r.W >= W / 2)
                matches.Add(i);
        }
        return matches;
    }

    public static Box? FindNativeRect(MenuPage page, string id)
    {
        if (page.Buttons.FirstOrDefault(b => b.Id == id) is { } b) return b.Rect;
        if (page.Zones.FirstOrDefault(z => z.Key == id) is { } z) return z.Rect;
        if (id == "heading" && page.Ops.OfType<Label>().FirstOrDefault(l => l.Rect.Y < 200 && l.Rect.W >= W / 2) is { } h)
            return h.Rect;
        return null;
    }

    static MenuPage ApplyNativeState(MenuDesign d, MenuPage page, MenuContent c)
    {
        if (d.FreePages.Any(fp => fp.Equals(page.Id, StringComparison.OrdinalIgnoreCase)
            || fp.Equals(MenuDesign.PageKind(page.Id), StringComparison.OrdinalIgnoreCase)))
        {
            return page with
            {
                Ops = page.Ops.Where(IsBackdropOrFull).ToList(),
                Buttons = [],
                Zones = [],
            };
        }

        var overrides = d.NativeOverrides.Where(o => o.AppliesTo(page.Id)).ToList();
        if (overrides.Count == 0) return page;

        var ops = page.Ops.ToList();
        var buttons = page.Buttons.ToList();
        var zones = page.Zones.ToList();
        var removeIndices = new HashSet<int>();
        var needsRelink = false;

        foreach (var ov in overrides)
        {
            var bIndex = buttons.FindIndex(b => b.Id == ov.Id);
            var zIndex = zones.FindIndex(z => z.Key == ov.Id);
            var origRect = bIndex >= 0 ? buttons[bIndex].Rect
                : zIndex >= 0 ? zones[zIndex].Rect
                : FindNativeRect(page, ov.Id);
            if (origRect is not { } from) continue;

            var matched = MatchOpIndices(page, ov.Id, from, bIndex >= 0);
            if (ov.Hidden)
            {
                foreach (var idx in matched) removeIndices.Add(idx);
                if (bIndex >= 0) { buttons.RemoveAt(bIndex); needsRelink = true; }
                if (zIndex >= 0) zones.RemoveAt(zIndex);
                continue;
            }

            var to = new Box(ov.X ?? from.X, ov.Y ?? from.Y, Math.Max(16, ov.W ?? from.W), Math.Max(16, ov.H ?? from.H));
            if (to != from && bIndex >= 0) needsRelink = true;

            var rebuildLook = bIndex >= 0 && (ov.Look != null || ov.Fill != null);
            if (rebuildLook)
            {
                foreach (var idx in matched) removeIndices.Add(idx);
                var existingLabel = matched.Select(i => page.Ops[i]).OfType<Label>().FirstOrDefault()?.Value
                    ?? matched.Select(i => page.Ops[i]).OfType<Text>().FirstOrDefault()?.Value
                    ?? buttons[bIndex].Id;
                var text = ov.Text ?? existingLabel;
                var size = ov.Size ?? matched.Select(i => page.Ops[i]).OfType<Label>().FirstOrDefault()?.Size ?? 48;
                var styled = d.Clone();
                if (!string.IsNullOrEmpty(ov.Look)) styled.ButtonStyle = ov.Look;
                if (!string.IsNullOrEmpty(ov.Fill)) styled.CardFill = ov.Fill;
                if (!string.IsNullOrEmpty(ov.Color)) styled.TextColor = ov.Color;
                if (!string.IsNullOrEmpty(ov.Font)) styled.Font = ov.Font;
                foreach (var newOp in Button(styled, to, text, size))
                    ops.Add(WithEffects(newOp, to, ov.Opacity, ov.Rotation, ov.ShadowColor, ov.ShadowBlur, ov.ShadowOffsetX, ov.ShadowOffsetY, ov.ImageBlur, null, 0));
            }
            else
            {
                foreach (var idx in matched)
                {
                    if (idx < ops.Count && !removeIndices.Contains(idx))
                    {
                        var updated = MoveOrResizeOp(ops[idx], from, to, ov);
                        ops[idx] = WithEffects(updated, to, ov.Opacity, ov.Rotation, ov.ShadowColor, ov.ShadowBlur, ov.ShadowOffsetX, ov.ShadowOffsetY, ov.ImageBlur, null, 0);
                    }
                }
            }

            if (bIndex >= 0)
            {
                var btn = buttons[bIndex] with { Rect = to };
                if (ov.Target != null) btn = btn with { Action = Action(d, ov.Target, c) };
                if (ov.Focus != null && (ElementLibrary.Resolve(ov.Focus) ?? ov.Focus) is { } f && File.Exists(f))
                    btn = btn with { FocusImage = f, DvdHighlightImage = f };
                if (ov.ActiveImage != null && (ElementLibrary.Resolve(ov.ActiveImage) ?? ov.ActiveImage) is { } act && File.Exists(act))
                    btn = btn with { DvdSelectImage = act };
                buttons[bIndex] = btn;
            }
            if (zIndex >= 0) zones[zIndex] = zones[zIndex] with { Rect = to };
        }

        if (removeIndices.Count > 0)
            ops = [.. ops.Where((_, i) => !removeIndices.Contains(i))];

        // Reorder native layers explicitly moved forward/backward via ZIndex (keeping backdrop first)
        var zMoved = overrides.Where(o => !o.Hidden && o.ZIndex != 0).OrderBy(o => o.ZIndex).ToList();
        if (zMoved.Count > 0)
        {
            var tempPage = page with { Ops = ops, Buttons = buttons, Zones = zones };
            var back = new List<Op>();
            var front = new List<Op>();
            var plucked = new HashSet<int>();
            foreach (var ov in zMoved)
            {
                var curRect = ov.X != null || ov.Y != null || ov.W != null || ov.H != null
                    ? new Box(ov.X ?? 0, ov.Y ?? 0, Math.Max(16, ov.W ?? 100), Math.Max(16, ov.H ?? 60))
                    : FindNativeRect(tempPage, ov.Id);
                if (curRect is not { } cr) continue;
                var idxList = MatchOpIndices(tempPage, ov.Id, cr, buttons.Any(b => b.Id == ov.Id));
                foreach (var i in idxList)
                {
                    if (!plucked.Add(i)) continue;
                    if (ov.ZIndex < 0) back.Add(ops[i]); else front.Add(ops[i]);
                }
            }
            if (plucked.Count > 0)
            {
                var remaining = ops.Where((_, i) => !plucked.Contains(i)).ToList();
                var insertAt = 0;
                while (insertAt < remaining.Count && IsBackdropOrFull(remaining[insertAt])) insertAt++;
                remaining.InsertRange(insertAt, back);
                remaining.AddRange(front);
                ops = remaining;
            }
        }

        var result = page with { Ops = ops, Buttons = buttons, Zones = zones };
        return needsRelink ? ApplyDirections(d, Relink(result)) : result;
    }

    static MenuPage ApplyDefaultButton(MenuDesign d, MenuPage page)
    {
        var defId = d.DefaultButtons.TryGetValue(page.Id, out var exact) ? exact
            : d.DefaultButtons.TryGetValue(MenuDesign.PageKind(page.Id), out var kind) ? kind : null;
        if (string.IsNullOrEmpty(defId)) return page;
        var at = page.Buttons.FindIndex(b => b.Id == defId);
        if (at <= 0) return page;
        var reordered = page.Buttons.ToList();
        var picked = reordered[at];
        reordered.RemoveAt(at);
        reordered.Insert(0, picked);
        return page with { Buttons = reordered };
    }

    /// <summary>Converts an action back to the canonical target string understood by <see cref="Action(MenuDesign, string, MenuContent)"/>.</summary>
    public static string ActionTarget(MenuAction a) => a.Kind switch
    {
        "playall" => "all",
        "play" => a.Playlist > 1 ? $"title:{a.Playlist}" : "movie",
        "chapter" => $"chapter:{a.Playlist}:{a.Mark + 1}",
        "audio" => $"audio:{a.Mark}",
        "sub" => $"sub:{a.Mark}",
        "resume" => "resume",
        "menu" => "menu",
        "page" => a.Page ?? "root",
        _ => "movie",
    };

    /// <summary>Converts a template page's native zones and buttons into editable free <see cref="MenuElement"/>s and marks the page as free.</summary>
    public static void ConvertPageToFree(MenuDesign d, MenuPage page)
    {
        if (d.FreePages.Any(fp => fp.Equals(page.Id, StringComparison.OrdinalIgnoreCase))) return;
        var handledRects = new List<Box>();

        // 1. Zones (title, poster, slot:...)
        foreach (var z in page.Zones)
        {
            if (z.Key.StartsWith("name:", StringComparison.Ordinal)) continue;
            handledRects.Add(z.Rect);
            if (z.Key == "poster")
            {
                var pic = page.Ops.OfType<Picture>().FirstOrDefault(p => p.Rect == z.Rect || ContainsBox(z.Rect, p.Rect))?.Path
                    ?? page.Ops.OfType<Image>().FirstOrDefault(im => !im.Backdrop && (im.Rect == z.Rect || ContainsBox(z.Rect, im.Rect)))?.Path;
                d.Elements.Add(new MenuElement { Kind = "image", Page = page.Id, X = z.Rect.X, Y = z.Rect.Y, W = z.Rect.W, H = z.Rect.H, Image = pic, Fit = true });
            }
            else
            {
                var textOp = page.Ops.OfType<Text>().FirstOrDefault(t => t.Rect == z.Rect || ContainsBox(z.Rect, t.Rect));
                var labelOp = page.Ops.OfType<Label>().FirstOrDefault(l => l.Rect == z.Rect || ContainsBox(z.Rect, l.Rect));
                var val = textOp?.Value ?? labelOp?.Value ?? (z.Key == "title" ? d.Title : "");
                var size = textOp != null ? (int)Math.Round(textOp.Size) : labelOp?.Size ?? 64;
                d.Elements.Add(new MenuElement
                {
                    Kind = "text", Page = page.Id, X = z.Rect.X, Y = z.Rect.Y, W = z.Rect.W, H = z.Rect.H,
                    Text = val, Size = size, Font = textOp?.Font ?? "", Color = textOp?.Color ?? labelOp?.Ink ?? d.TextColor,
                    Align = textOp?.Align ?? labelOp?.Align ?? "center", Bold = textOp?.Bold ?? labelOp?.Bold ?? true,
                    Italic = textOp?.Italic ?? false,
                });
            }
        }

        // Fallback heading if the page had no explicit title zone
        if (!page.Zones.Any(z => z.Key == "title")
            && page.Ops.OfType<Label>().FirstOrDefault(l => l.Rect.Y < 200 && l.Rect.W >= W / 2) is { } head)
        {
            d.Elements.Add(new MenuElement
            {
                Kind = "text", Page = page.Id, X = head.Rect.X, Y = head.Rect.Y, W = head.Rect.W, H = head.Rect.H,
                Text = head.Value, Size = head.Size, Color = head.Ink ?? d.TextColor, Align = head.Align, Bold = head.Bold,
            });
        }

        // 2. Native buttons
        foreach (var b in page.Buttons)
        {
            if (b.Id.StartsWith("u-", StringComparison.Ordinal) || (b.Id.StartsWith('u') && int.TryParse(b.Id[1..], out _))) continue;
            var insideOps = page.Ops.Where(o => !IsBackdropOrFull(o) && OpRect(o) is { } r && (r == b.Rect || ContainsBox(b.Rect, r))).ToList();
            var labelBelow = page.Ops.OfType<Label>().FirstOrDefault(r => Math.Abs(r.Rect.X - b.Rect.X) <= 10 && Math.Abs(r.Rect.W - b.Rect.W) <= 20
                && r.Rect.Y >= b.Rect.Y + b.Rect.H - 12 && r.Rect.Y <= b.Rect.Y + b.Rect.H + 30);
            var labelOp = insideOps.OfType<Label>().FirstOrDefault() ?? labelBelow;
            var textOp = insideOps.OfType<Text>().FirstOrDefault();
            var imgOp = insideOps.OfType<Image>().FirstOrDefault()?.Path
                ?? insideOps.OfType<Picture>().FirstOrDefault()?.Path
                ?? insideOps.OfType<Framed>().FirstOrDefault()?.Picture;

            d.Elements.Add(new MenuElement
            {
                Kind = "button",
                NavigationId = b.Id,
                Page = page.Id,
                X = b.Rect.X, Y = b.Rect.Y, W = b.Rect.W, H = b.Rect.H,
                Text = textOp?.Value ?? labelOp?.Value ?? "",
                Size = textOp != null ? (int)Math.Round(textOp.Size) : labelOp?.Size ?? 44,
                Color = textOp?.Color ?? labelOp?.Ink ?? d.TextColor,
                Image = imgOp != null ? ElementLibrary.Reference(imgOp) : null,
                Focus = b.FocusImage != null ? ElementLibrary.Reference(b.FocusImage) : null,
                ActiveImage = b.DvdSelectImage != null ? ElementLibrary.Reference(b.DvdSelectImage) : null,
                Target = ActionTarget(b.Action),
            });
        }

        d.NativeOverrides.RemoveAll(o => o.AppliesTo(page.Id));
        if (!d.FreePages.Contains(page.Id, StringComparer.OrdinalIgnoreCase))
            d.FreePages.Add(page.Id);
    }
}
