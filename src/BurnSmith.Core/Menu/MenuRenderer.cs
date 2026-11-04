using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BurnSmith.Core.Menu;

/// <summary>
/// Draws menu pages with WPF. The editor preview and the disc builders call this same code, so what the user
/// edits is what ends up on the disc.
/// </summary>
public static class MenuRenderer
{
    /// <summary>Runs on an STA thread: WPF drawing needs one, and callers (CLI, background workers) may not have it.</summary>
    public static T Sta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = work(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return error == null ? result : throw new InvalidOperationException(error.Message, error);
    }

    static Color Col(string hex, double opacity = 1)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = (byte)Math.Round(255 * opacity);
        return c;
    }

    static Rect R(Box b) => new(b.X, b.Y, b.W, b.H);

    static byte[] Encode(BitmapEncoder encoder, int w, int h, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage> Images = new(); // frozen, so shared by every render thread

    static BitmapImage Load(string path) => Images.GetOrAdd(path, p =>
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(p);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException)
        {
            throw new InvalidDataException($"Image illisible : {p}", e);
        }
    });

    /// <summary>Opaque page background as JPEG (smaller and faster to decode than PNG on a player).</summary>
    public static byte[] Background(MenuDesign d, MenuPage page) =>
        Encode(new JpegBitmapEncoder { QualityLevel = 92 }, MenuBuilder.W, MenuBuilder.H, dc => DrawPage(dc, d, page));

    /// <summary>Transparent PNG with the focus frame, sized like the button plus the focus margin.</summary>
    public static byte[] Focus(MenuDesign d, MenuButton b)
    {
        var m = MenuBuilder.FocusMargin;
        var box = b.Rect.Inflate(m);
        return Encode(new PngBitmapEncoder(), box.W, box.H, dc =>
        {
            if (b.FocusImage != null)
            {
                var at = b.FocusSlot is { } s ? new Rect(s.X - box.X, s.Y - box.Y, s.W, s.H) : new Rect(m, m, b.Rect.W, b.Rect.H);
                dc.DrawImage(Load(b.FocusImage), at);
                return;
            }
            var pen = new Pen(new SolidColorBrush(Col(d.Accent)), 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Col(d.Accent, 0.18)), pen, new Rect(3, 3, box.W - 6, box.H - 6), 22, 22);
        });
    }

    /// <summary>Transparent PNG: the dot beside the audio / subtitle that is currently chosen.</summary>
    public static byte[] Marker(MenuDesign d, int size) =>
        Encode(new PngBitmapEncoder(), size, size, dc => dc.DrawEllipse(new SolidColorBrush(Col(d.Accent)), null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0));

    static void DrawPage(DrawingContext dc, MenuDesign d, MenuPage page)
    {
        var full = new Rect(0, 0, MenuBuilder.W, MenuBuilder.H);
        if (d.TemplateDir != null) { /* the template's own artwork is the first op of every page */ }
        else if (ElementLibrary.Resolve(d.BackgroundImage) is { } picture && File.Exists(picture))
        {
            dc.DrawRectangle(new ImageBrush(Load(picture)) { Stretch = Stretch.UniformToFill }, null, full);
            if (d.Dim > 0) dc.DrawRectangle(new SolidColorBrush(Col("#000000", d.Dim / 100.0)), null, full); // keeps text readable on any picture
        }
        else
        {
            dc.DrawRectangle(new LinearGradientBrush(Col(d.Top), Col(d.Bottom), 90), null, full);
            var rnd = new Random(d.Name.Sum(ch => ch)); // stable per design: same menu on every render
            for (var i = 0; i < 9; i++)
            {
                var r = 120 + rnd.Next(260);
                var glow = new RadialGradientBrush(Col("#FFFFFF", 0.10), Col("#FFFFFF", 0));
                dc.DrawEllipse(glow, null, new Point(rnd.Next(MenuBuilder.W), rnd.Next(MenuBuilder.H)), r, r);
            }
        }

        foreach (var op in page.Ops)
            switch (op)
            {
                case Image im:
                    dc.DrawImage(Load(im.Path), R(im.Rect));
                    break;
                case Text t:
                    DrawText(dc, t);
                    break;
                case Framed f:
                    DrawFramed(dc, f);
                    break;
                case Fill f:
                    dc.DrawRoundedRectangle(new SolidColorBrush(Col(f.Color, f.Opacity)), null, R(f.Rect), f.Radius, f.Radius);
                    break;
                case Picture p:
                    var rect = R(p.Rect);
                    var pen = p.BorderWidth > 0 ? new Pen(new SolidColorBrush(Col(d.Accent)), p.BorderWidth) : null;
                    dc.DrawRoundedRectangle(new ImageBrush(Load(p.Path)) { Stretch = Stretch.UniformToFill }, pen, rect, p.Radius, p.Radius);
                    break;
                case Label l:
                    DrawLabel(dc, d, l);
                    break;
            }
    }

    static void DrawFramed(DrawingContext dc, Framed f)
    {
        var rect = R(f.Rect);
        BitmapImage? mask = null;
        try { mask = f.Mask == null ? null : Load(f.Mask); }
        catch (InvalidDataException) { /* a mask WPF cannot decode (Holiday's): the picture is shown unmasked */ }
        if (mask != null) dc.PushOpacityMask(new ImageBrush(mask) { Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute, Viewport = rect });
        dc.DrawRectangle(f.Picture != null ? new ImageBrush(Load(f.Picture)) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Col("#000000", 0.45)), null, rect);
        if (mask != null) dc.Pop();
        if (f.Frame != null) dc.DrawImage(Load(f.Frame), rect);
    }

    static void DrawText(DrawingContext dc, Text t)
    {
        // a template's own .ttf is read from its folder (never installed); the system font of the same name is the fallback
        var family = t.FontDir == null ? new FontFamily(t.Font)
            : new FontFamily(new Uri(Path.GetFullPath(t.FontDir).TrimEnd('\\') + "\\"), $"./#{t.Font}, {t.Font}, Segoe UI");
        var face = new Typeface(family, t.Italic ? FontStyles.Italic : FontStyles.Normal, t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(t.Value, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                     face, t.Size, new SolidColorBrush(Col(t.Color)), 1.0)
        { MaxTextWidth = t.Rect.W, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var x = t.Align == "left" ? t.Rect.X : t.Rect.X + (t.Rect.W - text.Width) / (t.Align == "right" ? 1 : 2);
        var at = new Point(x, t.Rect.Y + (t.Rect.H - text.Height) / 2);
        if (t.Stroke != null && t.StrokeWidth > 0) // outline first, so the fill stays crisp on top
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Col(t.Stroke)), t.StrokeWidth * 2) { LineJoin = PenLineJoin.Round }, text.BuildGeometry(at));
        dc.DrawText(text, at);
    }

    static void DrawLabel(DrawingContext dc, MenuDesign d, Label l)
    {
        var face = new Typeface(new FontFamily(d.Font), FontStyles.Normal, l.Bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(l.Value, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                     face, l.Size, new SolidColorBrush(Col(d.TextColor)), 1.0)
        { MaxTextWidth = l.Rect.W, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var x = l.Align == "left" ? l.Rect.X : l.Rect.X + (l.Rect.W - text.Width) / 2;
        dc.DrawText(text, new Point(x, l.Rect.Y + (l.Rect.H - text.Height) / 2));
    }
}
