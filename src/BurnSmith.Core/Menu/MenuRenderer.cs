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
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
            finally { System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); } // else each render leaks its dispatcher's window and handles
        });
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

    /// <summary>Pixels of a length on a canvas drawn at <paramref name="scale"/> (a 720p disc draws its menu at 2/3 of the 1920x1080 layout).</summary>
    public static int Px(int length, double scale) => (int)Math.Round(length * scale);

    static byte[] Encode(BitmapEncoder encoder, int w, int h, Action<DrawingContext> draw, double scale = 1)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (scale != 1) dc.PushTransform(new ScaleTransform(scale, scale));
            draw(dc);
        }
        var bmp = new RenderTargetBitmap(Px(w, scale), Px(h, scale), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> Images = new(); // frozen, so shared by every render thread

    public static bool IsSvg(string path) => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A picture file, ready to draw. An SVG is kept as vectors and laid out at the size it will be drawn at (<paramref name="size"/>), as DVDStyler
    /// does: a button in percentages stretches to its box, one with a viewBox keeps its proportions, and both stay sharp at any size.
    /// </summary>
    static ImageSource Load(string path, Rect? size = null) => !IsSvg(path) ? Bitmap(path)
        : Images.GetOrAdd($"{path}|{size?.Width:0}x{size?.Height:0}", _ => Svg(path, size));

    /// <summary>An SVG file as a frozen vector picture at its own size (for the editor's lists).</summary>
    public static ImageSource SvgPicture(string path) => Load(path);

    static ImageSource Svg(string path, Rect? size)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(path);
            var root = doc.Root!;
            // an imported SVG draws itself only: SharpVectors would follow <image href="file://server/share/x.png"> (Windows then sends the login hash), so links leave the document
            doc.DocumentType?.Remove();
            foreach (var link in root.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName == "href" && !a.Value.StartsWith('#') && !a.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)).ToList())
                link.Remove();
            if (size is { } s) { root.SetAttributeValue("width", Math.Round(s.Width)); root.SetAttributeValue("height", Math.Round(s.Height)); }
            double Dim(string name) => double.TryParse(((string?)root.Attribute(name))?.TrimEnd('p', 'x'), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 100;
            var (w, h) = (Dim("width"), Dim("height"));
            using var ms = new MemoryStream();
            doc.Save(ms);
            ms.Position = 0;
            var settings = new SharpVectors.Renderers.Wpf.WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
            var drawing = new SharpVectors.Converters.FileSvgReader(settings).Read(ms);
            // the drawing's own bounds are its ink: framed in its page (w x h) so it lands where the SVG puts it inside the box
            var page = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, w, h)) };
            page.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, w, h))));
            page.Children.Add(drawing);
            var image = new DrawingImage(page);
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"Image illisible : {path}", e);
        }
    }

    static ImageSource Bitmap(string path) => Images.GetOrAdd(path, p =>
    {
        try
        {
            CheckSize(p);
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
    public static byte[] Background(MenuDesign d, MenuPage page, double scale = 1) =>
        Encode(new JpegBitmapEncoder { QualityLevel = 92 }, MenuBuilder.W, MenuBuilder.H, dc => DrawPage(dc, d, page), scale);

    /// <summary>
    /// The page without its background (transparent PNG): what goes over a menu video. The design's dim stays as a translucent veil,
    /// so text is readable on any picture.
    /// </summary>
    public static byte[] Overlay(MenuDesign d, MenuPage page, double scale = 1) =>
        Encode(new PngBitmapEncoder(), MenuBuilder.W, MenuBuilder.H, dc => DrawPage(dc, d, page, true), scale);

    /// <summary>
    /// Transparent PNG of a button's selected state, sized like the button plus the focus margin. A template's own artwork wins; otherwise the
    /// design's highlight style draws it — three kinds: a box, an underline, or the label in the accent
    /// colour. <paramref name="label"/> is the button's text, needed by the last one; null = the frame.
    /// </summary>
    public static byte[] Focus(MenuDesign d, MenuButton b, double scale = 1, string? label = null)
    {
        var m = MenuBuilder.FocusMargin;
        var box = b.Rect.Inflate(m);
        var style = label == null ? "box" : d.HighlightStyle;
        return Encode(new PngBitmapEncoder(), box.W, box.H, dc =>
        {
            if (b.FocusImage != null)
            {
                var at = b.FocusSlot is { } s ? new Rect(s.X - box.X, s.Y - box.Y, s.W, s.H) : new Rect(m, m, b.Rect.W, b.Rect.H);
                dc.DrawImage(Load(b.FocusImage, at), at);
                return;
            }
            switch (style)
            {
                case "underline": // a bar under the button, as an underline
                    dc.DrawRectangle(new SolidColorBrush(Col(d.SelectionInk, Math.Max(0.15, d.SelectionOpacity / 100.0))), null, new Rect(m, box.H - 12, b.Rect.W, 8));
                    break;
                case "textcolor": // no frame: the label is redrawn in the accent colour (the text-colour style)
                    DrawLabel(dc, d, new Label(label!, new Box(m, m, b.Rect.W, b.Rect.H), Math.Clamp(b.Rect.H / 3, 20, 54), true), Col(d.SelectionInk));
                    break;
                default: // "box": the accent frame BurnSmith has always drawn
                    var pen = new Pen(new SolidColorBrush(Col(d.SelectionInk)), 6);
                    dc.DrawRoundedRectangle(new SolidColorBrush(Col(d.SelectionInk, Math.Clamp(d.SelectionOpacity, 0, 100) / 100.0)), pen, new Rect(3, 3, box.W - 6, box.H - 6), 22, 22);
                    break;
            }
        }, scale);
    }

    /// <summary>Transparent PNG: the dot beside the audio / subtitle that is currently chosen.</summary>
    public static byte[] Marker(MenuDesign d, int size, double scale = 1) =>
        Encode(new PngBitmapEncoder(), size, size, dc => dc.DrawEllipse(new SolidColorBrush(Col(d.Accent)), null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0), scale);

    /// <summary>
    /// Refuses, before it is decoded, a picture over 8192 pixels a side (the library's largest is 3840x2160): a 2 MB PNG can say 20000x20000, gigabytes in memory,
    /// and an out-of-memory error ends the app.
    /// </summary>
    internal static void CheckSize(string image)
    {
        if (Size(image) is var (w, h) && (w > 8192 || h > 8192)) throw new InvalidDataException($"Image trop grande ({w} x {h}) : {image}");
    }

    /// <summary>Pixel size of an image file, or null when it cannot be read (a template picture can be damaged).</summary>
    public static (int W, int H)? Size(string image)
    {
        try
        {
            if (IsSvg(image)) return Load(image) is { Width: > 0, Height: > 0 } svg ? ((int)Math.Round(svg.Width), (int)Math.Round(svg.Height)) : null;
            using var file = File.OpenRead(image); // a stream closed here: from a Uri, WPF holds the file open until a garbage collection ("Retirer" then fails)
            var frame = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (InvalidDataException) { return null; }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or ArgumentException) { return null; }
    }

    /// <summary>
    /// A template's button artwork as raw pixels at the size a DVD subpicture needs (BGRA, premultiplied — WPF's own order), cut by its mask
    /// when it has one. Runs on an STA thread: callers wrap it in <see cref="Sta{T}"/>.
    /// </summary>
    public static (byte[] Px, int W, int H) Raster(string image, string? mask, int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, w, h);
            if (mask != null) dc.PushOpacityMask(new ImageBrush(Load(mask)) { Stretch = Stretch.Fill });
            dc.DrawImage(Load(image, rect), rect);
            if (mask != null) dc.Pop();
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    static void DrawPage(DrawingContext dc, MenuDesign d, MenuPage page, bool overlay = false)
    {
        var full = new Rect(0, 0, MenuBuilder.W, MenuBuilder.H);
        var own = overlay ? null : d.PageBackground(page.Id); // this page's own picture replaces the design's background, a template's included
        var shown = own ?? (overlay || d.TemplateDir != null ? null : ElementLibrary.Resolve(d.BackgroundImage) is { } picture && File.Exists(picture) ? picture : null);
        if (overlay)
        {
            if (d.TemplateDir == null && d.Dim > 0) dc.DrawRectangle(new SolidColorBrush(Col("#000000", d.Dim / 100.0)), null, full);
        }
        else if (shown == null && d.TemplateDir != null) { /* the template's own artwork is the first op of every page */ }
        else if (shown != null)
        {
            dc.DrawRectangle(new ImageBrush(Load(shown)) { Stretch = Stretch.UniformToFill }, null, full);
            if (d.Dim > 0 && d.TemplateDir == null) dc.DrawRectangle(new SolidColorBrush(Col("#000000", d.Dim / 100.0)), null, full); // keeps text readable on any picture (a template has no dim setting: its pages are not darkened)
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
                    if ((overlay || own != null) && im.Backdrop) break; // a template's background picture: the page has its own, or the video is behind
                    if (im.Cover) dc.DrawRectangle(new ImageBrush(Load(im.Path)) { Stretch = Stretch.UniformToFill }, null, R(im.Rect));
                    else dc.DrawImage(Load(im.Path, R(im.Rect)), R(im.Rect));
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
                case Border b:
                    dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Col(b.Color)), b.Width) { LineJoin = PenLineJoin.Round }, R(b.Rect), Math.Max(0, b.Radius), Math.Max(0, b.Radius));
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
        ImageSource? mask = null;
        try { mask = f.Mask == null ? null : Load(f.Mask); }
        catch (InvalidDataException) { /* a mask WPF cannot decode (Holiday's): the picture is shown unmasked */ }
        if (mask != null) dc.PushOpacityMask(new ImageBrush(mask) { Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute, Viewport = rect });
        dc.DrawRectangle(f.Picture != null ? new ImageBrush(Load(f.Picture)) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Col("#000000", 0.45)), null, rect);
        if (mask != null) dc.Pop();
        if (f.Frame != null) dc.DrawImage(Load(f.Frame, rect), rect);
    }

    static void DrawText(DrawingContext dc, Text t)
    {
        // a template's own .ttf is read from its folder (never installed); the system font of the same name is the fallback
        // when neither the template nor Windows has the font (Brush Script MT, Showcard Gothic... are Office fonts), one of the same kind from Windows stands in
        var stand = t.Font.ToLowerInvariant() switch
        {
            var f when f.Contains("script") || f.Contains("brush") || f.Contains("corsiva") || f.Contains("calligraph") || f.Contains("handwrit") => "Segoe Script",
            var f when f.Contains("showcard") || f.Contains("impact") || f.Contains("cooper") => "Segoe UI Black",
            _ => "Segoe UI",
        };
        var family = t.FontDir == null ? new FontFamily($"{t.Font}, {stand}")
            : new FontFamily(new Uri(Path.GetFullPath(t.FontDir).TrimEnd('\\') + "\\"), $"./#{t.Font}, {t.Font}, {stand}");
        var face = new Typeface(family, t.Italic ? FontStyles.Italic : FontStyles.Normal, t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(t.FontDir == null ? t.Value : Drawable(face, t.Value), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                     face, t.Size, new SolidColorBrush(Col(t.Color)), 1.0)
        { MaxTextWidth = t.Rect.W, MaxLineCount = t.Wrap ? Math.Max(1, (int)(t.Rect.H / (t.Size * 1.25))) : 1, Trimming = TextTrimming.CharacterEllipsis };
        var x = t.Align == "left" ? t.Rect.X : t.Rect.X + (t.Rect.W - text.Width) / (t.Align == "right" ? 1 : 2);
        var at = new Point(x, t.Wrap ? t.Rect.Y : t.Rect.Y + (t.Rect.H - text.Height) / 2); // several lines start at the top of their zone
        if (t.Stroke != null && t.StrokeWidth > 0) // outline first, so the fill stays crisp on top
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Col(t.Stroke)), t.StrokeWidth * 2) { LineJoin = PenLineJoin.Round }, text.BuildGeometry(at));
        dc.DrawText(text, at);
    }

    /// <summary>
    /// Decorative fonts often map accented letters to an empty glyph ("SCÈNES" came out as "SC NES"): such a letter is replaced by its plain base letter.
    /// </summary>
    static string Drawable(Typeface face, string value)
    {
        if (!face.TryGetGlyphTypeface(out var glyphs)) return value;
        bool Draws(char c) => char.IsWhiteSpace(c) || (glyphs.CharacterToGlyphMap.TryGetValue(c, out var g) && !glyphs.GetGlyphOutline(g, 16, 16).Bounds.IsEmpty);
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            var plain = c.ToString().Normalize(System.Text.NormalizationForm.FormD)[0];
            sb.Append(!Draws(c) && plain != c && Draws(plain) ? plain : c);
        }
        return sb.ToString();
    }

    static void DrawLabel(DrawingContext dc, MenuDesign d, Label l, Color? colour = null)
    {
        var face = new Typeface(new FontFamily(d.Font), FontStyles.Normal, l.Bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(l.Value, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                     face, l.Size, new SolidColorBrush(colour ?? Col(d.TextColor)), 1.0)
        { MaxTextWidth = l.Rect.W, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var x = l.Align == "left" ? l.Rect.X : l.Rect.X + (l.Rect.W - text.Width) / 2;
        dc.DrawText(text, new Point(x, l.Rect.Y + (l.Rect.H - text.Height) / 2));
    }
}
