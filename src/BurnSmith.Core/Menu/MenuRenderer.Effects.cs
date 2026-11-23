using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BurnSmith.Core.Menu;

public static partial class MenuRenderer
{
    static void DrawStyledOp(DrawingContext dc, MenuDesign d, StyledOp s, bool overlay, string? own)
    {
        var bounds = s.Bounds.W > 0 && s.Bounds.H > 0 ? s.Bounds : (MenuBuilder.OpRect(s.Inner) ?? new Box(0, 0, MenuBuilder.W, MenuBuilder.H));
        var cx = bounds.X + bounds.W / 2.0;
        var cy = bounds.Y + bounds.H / 2.0;
        var pushed = 0;
        if (s.Opacity < 0.999) { dc.PushOpacity(Math.Clamp(s.Opacity, 0, 1)); pushed++; }
        if (Math.Abs(s.Rotation) >= 0.01) { dc.PushTransform(new RotateTransform(s.Rotation, cx, cy)); pushed++; }

        if (!string.IsNullOrWhiteSpace(s.ShadowColor) && (s.ShadowDx != 0 || s.ShadowDy != 0 || s.ShadowBlur > 0))
        {
            var dx = s.ShadowDx == 0 && s.ShadowBlur > 0 ? 4 : s.ShadowDx;
            var dy = s.ShadowDy == 0 && s.ShadowBlur > 0 ? 4 : s.ShadowDy;
            dc.PushTransform(new TranslateTransform(dx, dy));
            dc.PushOpacity(0.45);
            DrawOp(dc, d, TintShadowOp(s.Inner, s.ShadowColor!), overlay, own);
            dc.Pop();
            dc.Pop();
        }

        if (s.ImageBlur > 0 && s.Inner is Image im && File.Exists(im.Path))
        {
            var blurred = BlurredBitmap(Load(im.Path, R(im.Rect)), s.ImageBlur);
            dc.DrawImage(blurred, R(im.Rect));
        }
        else if (s.ImageBlur > 0 && s.Inner is Picture pic && File.Exists(pic.Path))
        {
            var blurred = BlurredBitmap(Load(pic.Path), s.ImageBlur);
            var pen = pic.BorderWidth > 0 ? new Pen(new SolidColorBrush(Col(d.Accent)), pic.BorderWidth) : null;
            dc.DrawRoundedRectangle(new ImageBrush(blurred) { Stretch = Stretch.UniformToFill }, pen, R(pic.Rect), pic.Radius, pic.Radius);
        }
        else
        {
            DrawOp(dc, d, s.Inner, overlay, own);
        }

        if (!string.IsNullOrWhiteSpace(s.TintColor) && s.TintStrength > 0)
        {
            var radius = s.Inner is Fill fl ? fl.Radius : s.Inner is Picture p ? p.Radius : 0;
            dc.DrawRoundedRectangle(new SolidColorBrush(Col(s.TintColor!, Math.Clamp(s.TintStrength, 0, 100) / 100.0)), null, R(bounds), radius, radius);
        }

        while (pushed-- > 0) dc.Pop();
    }

    static Op TintShadowOp(Op op, string shadowColor) => op switch
    {
        Text t => t with { Color = shadowColor, Stroke = null, Shadow = null },
        Label l => l with { Ink = shadowColor },
        Fill f => f with { Color = shadowColor },
        Border b => b with { Color = shadowColor },
        _ => op,
    };

    static ImageSource BlurredBitmap(ImageSource src, int blur)
    {
        var visual = new DrawingVisual { Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Clamp(blur, 1, 60) } };
        var w = Math.Max(1, (int)Math.Ceiling(src.Width));
        var h = Math.Max(1, (int)Math.Ceiling(src.Height));
        using (var dc = visual.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w, h));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Scales an encoded PNG or JPEG image's RGB luminance for HDR graphics calibration (ITU-R BT.2408: nits / 400).</summary>
    public static byte[] ScaleImageForHdr(byte[] encoded, int nits, bool jpeg)
    {
        if (nits is <= 0 or >= 400) return encoded;
        var factor = Math.Clamp(nits, 80, 400) / 400.0;
        using var src = new MemoryStream(encoded);
        var decoder = BitmapDecoder.Create(src, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var formatted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = formatted.PixelWidth, h = formatted.PixelHeight;
        var px = new byte[w * h * 4];
        formatted.CopyPixels(px, w * 4, 0);
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)Math.Clamp((int)Math.Round(px[i] * factor), 0, 255);
            px[i + 1] = (byte)Math.Clamp((int)Math.Round(px[i + 1] * factor), 0, 255);
            px[i + 2] = (byte)Math.Clamp((int)Math.Round(px[i + 2] * factor), 0, 255);
        }
        var wb = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(wb));
        using var dst = new MemoryStream();
        encoder.Save(dst);
        return dst.ToArray();
    }

    /// <summary>Transparent PNG of a button's activated (pressed / OK) state.</summary>
    public static byte[] Activated(MenuDesign d, MenuPage p, MenuButton b, double scale = 1)
    {
        var written = p.Ops.Select(o => o is StyledOp so ? so.Inner : o).OfType<Label>().FirstOrDefault(l => l.Rect == b.Rect);
        return Activated(d, b, scale, written?.Value, written);
    }

    public static byte[] Activated(MenuDesign d, MenuButton b, double scale = 1, string? label = null, Label? written = null)
    {
        var m = MenuBuilder.FocusMargin;
        var box = b.Rect.Inflate(m);
        var whole = FocusBox(b);
        var actPath = b.DvdSelectImage == null ? null : (ElementLibrary.Resolve(b.DvdSelectImage) ?? b.DvdSelectImage);
        return Encode(new PngBitmapEncoder(), whole.W, whole.H, dc =>
        {
            dc.PushTransform(new TranslateTransform(box.X - whole.X, box.Y - whole.Y));
            if (actPath != null && (IsSvgText(actPath) || File.Exists(actPath)))
            {
                var at = new Rect(m, m, b.Rect.W, b.Rect.H);
                dc.DrawImage(Load(actPath, at), at);
                if (written != null && b.FocusSlot == null)
                    DrawLabel(dc, d, written with { Rect = new Box(m, m, b.Rect.W, b.Rect.H), Ink = written.Ink == null ? null : InkOn(actPath, d.TextColor) });
                return;
            }
            if (b.FocusImage != null)
            {
                var at = b.FocusSlot is { } s ? new Rect(s.X - box.X, s.Y - box.Y, s.W, s.H) : new Rect(m, m, b.Rect.W, b.Rect.H);
                dc.DrawImage(Load(b.FocusImage, at), at);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)), new Pen(Brushes.White, 5), at, 16, 16);
                if (written != null && b.FocusSlot == null)
                    DrawLabel(dc, d, written with { Rect = new Box(m, m, b.Rect.W, b.Rect.H), Ink = "#FFFFFF" });
                return;
            }
            var pen = new Pen(Brushes.White, 6);
            var fill = label == null ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
                : new SolidColorBrush(Col(d.SelectionInk, Math.Clamp(Math.Max(60, d.SelectionOpacity), 0, 100) / 100.0));
            dc.DrawRoundedRectangle(fill, pen, new Rect(3, 3, box.W - 6, box.H - 6), 22, 22);
            if (!string.IsNullOrEmpty(label))
                DrawLabel(dc, d, new Label(label, new Box(m, m, b.Rect.W, b.Rect.H), Math.Clamp(b.Rect.H / 3, 20, 54), true), Colors.White);
        }, scale);
    }

    /// <summary>Generates and caches 6 smooth transparent PNG frames (128x128) for a built-in BD-J animated cursor preset.</summary>
    public static string[]? BuiltInCursorFrames(string? preset, string accentHex)
    {
        if (string.IsNullOrWhiteSpace(preset) || preset is "none" or "custom") return null;
        var accent = string.IsNullOrWhiteSpace(accentHex) ? "#EAB308" : accentHex.Trim();
        var key = $"{preset.ToLowerInvariant()}_{accent.TrimStart('#')}";
        var dir = Path.Combine(Path.GetTempPath(), "BurnSmith", "cursors", key);
        Directory.CreateDirectory(dir);
        var files = Enumerable.Range(0, 6).Select(i => Path.Combine(dir, $"frame_{i}.png")).ToArray();
        if (files.All(File.Exists)) return files;

        Sta(() =>
        {
            var color = Col(accent);
            for (var i = 0; i < 6; i++)
            {
                var phase = i / 6.0 * Math.PI * 2;
                var png = Encode(new PngBitmapEncoder(), 128, 128, dc =>
                {
                    var cx = 64.0;
                    var cy = 64.0;
                    switch (preset.ToLowerInvariant())
                    {
                        case "ring":
                        {
                            var rot = i * 60.0;
                            dc.PushTransform(new RotateTransform(rot, cx, cy));
                            var outer = new Pen(new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B)), 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                            var inner = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 5);
                            var geo = new StreamGeometry();
                            using (var ctx = geo.Open())
                            {
                                ctx.BeginFigure(new Point(cx, cy - 42), false, false);
                                ctx.ArcTo(new Point(cx + 42, cy), new Size(42, 42), 0, true, SweepDirection.Clockwise, true, false);
                            }
                            dc.DrawGeometry(null, outer, geo);
                            dc.DrawEllipse(null, inner, new Point(cx, cy), 22, 22);
                            dc.Pop();
                            break;
                        }
                        case "arrow":
                        {
                            var shift = Math.Sin(phase) * 10;
                            var glowAlpha = (byte)(150 + 80 * Math.Cos(phase));
                            var tri = new StreamGeometry();
                            using (var ctx = tri.Open())
                            {
                                ctx.BeginFigure(new Point(28 + shift, 24), true, true);
                                ctx.LineTo(new Point(102 + shift, 64), true, false);
                                ctx.LineTo(new Point(28 + shift, 104), true, false);
                            }
                            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(glowAlpha, color.R, color.G, color.B)),
                                new Pen(Brushes.White, 4), tri);
                            break;
                        }
                        default: // "pulse"
                        {
                            var r = 30 + 16 * (0.5 + 0.5 * Math.Sin(phase));
                            var alpha = (byte)(130 + 100 * (0.5 + 0.5 * Math.Cos(phase)));
                            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(alpha / 2), color.R, color.G, color.B)),
                                new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), 6), new Point(cx, cy), r, r);
                            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(cx, cy), 14, 14);
                            break;
                        }
                    }
                });
                File.WriteAllBytes(files[i], png);
            }
            return 0;
        });
        return files;
    }

    /// <summary>Quantizes an anti-aliased highlight PNG into a flat 1-bit shape tinted with <paramref name="flatHex"/> for faithful DVD SPU preview.</summary>
    public static byte[] QuantizeDvdSpu(byte[] encodedPng, string flatHex)
    {
        using var src = new MemoryStream(encodedPng);
        var decoder = BitmapDecoder.Create(src, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var formatted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = formatted.PixelWidth, h = formatted.PixelHeight;
        var px = new byte[w * h * 4];
        formatted.CopyPixels(px, w * 4, 0);
        var c = Col(string.IsNullOrWhiteSpace(flatHex) ? "#EAB308" : flatHex);
        for (var i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] >= 80) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
            else px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0;
        }
        var wb = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(wb));
        using var dst = new MemoryStream();
        encoder.Save(dst);
        return dst.ToArray();
    }

    /// <summary>Cuts transparent rectangles (alpha = 0) into an opaque PNG/JPEG image so underlying animated thumbnail video clips show through in BD-J.</summary>
    public static byte[] PunchAlphaCutouts(byte[] imageBytes, IReadOnlyList<Box> rects, double scale)
    {
        using var src = new MemoryStream(imageBytes);
        var decoder = BitmapDecoder.Create(src, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var formatted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = formatted.PixelWidth, h = formatted.PixelHeight;
        var px = new byte[w * h * 4];
        formatted.CopyPixels(px, w * 4, 0);
        foreach (var r in rects)
        {
            int x0 = Math.Clamp(Px(r.X, scale), 0, w), y0 = Math.Clamp(Px(r.Y, scale), 0, h);
            int x1 = Math.Clamp(Px(r.X + r.W, scale), 0, w), y1 = Math.Clamp(Px(r.Y + r.H, scale), 0, h);
            for (var y = y0; y < y1; y++)
            {
                var row = y * w * 4;
                for (var x = x0; x < x1; x++)
                {
                    var idx = row + x * 4;
                    px[idx] = px[idx + 1] = px[idx + 2] = px[idx + 3] = 0;
                }
            }
        }
        var wb = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(wb));
        using var dst = new MemoryStream();
        encoder.Save(dst);
        return dst.ToArray();
    }
}
