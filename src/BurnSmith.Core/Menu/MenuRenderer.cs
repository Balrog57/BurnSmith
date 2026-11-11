using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace BurnSmith.Core.Menu;

/// <summary>
/// Draws menu pages with WPF. The editor preview and the disc builders call this same code, so what the user
/// edits is what ends up on the disc.
/// </summary>
public static partial class MenuRenderer
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

    public static bool IsSvg(string path) => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || IsSvgText(path);

    /// <summary>A picture given as the SVG itself instead of a file: a template that keeps the drawing of each of its buttons in its own file (.dvdt).</summary>
    public static bool IsSvgText(string picture) => picture.StartsWith("<svg", StringComparison.Ordinal);

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
            var doc = IsSvgText(path) ? System.Xml.Linq.XDocument.Parse(path) : System.Xml.Linq.XDocument.Load(path);
            var root = doc.Root!;
            // an imported SVG draws itself only: a renderer that followed <image href="file://server/share/x.png"> would open it (Windows then sends the login hash), so links leave the document
            doc.DocumentType?.Remove();
            foreach (var link in root.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName == "href" && !a.Value.StartsWith('#') && !a.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)).ToList())
                link.Remove();
            if (size is { } s) { root.SetAttributeValue("width", Math.Round(s.Width)); root.SetAttributeValue("height", Math.Round(s.Height)); }
            double Dim(string name) => double.TryParse(((string?)root.Attribute(name))?.TrimEnd('p', 'x'), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 100;
            var (w, h) = (Dim("width"), Dim("height"));
            Simplify(root, w, h);
            using var ms = new MemoryStream();
            doc.Save(ms);
            ms.Position = 0;
            var drawing = Fitted(RoundDashes(new SVGImage.SVG.SVGRender(new NoFiles()).LoadDrawing(ms)), root, w, h);
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
            throw new InvalidDataException($"Image illisible : {(IsSvgText(path) ? "dessin d'un bouton du modèle" : path)}", e);
        }
    }

    /// <summary>
    /// The drawing, in the SVG's own units (its viewBox), placed in its width x height as the SVG standard says: scaled to fit and centred unless
    /// preserveAspectRatio says otherwise ("none" stretches, "slice" fills, xMin / xMax / YMin / YMax align). Without a viewBox the units are the pixels.
    /// </summary>
    static Drawing Fitted(Drawing drawing, System.Xml.Linq.XElement root, double w, double h)
    {
        if (Placement(root, w, h) is not { } m) return drawing;
        var placed = new DrawingGroup { Transform = new MatrixTransform(m) };
        placed.Children.Add(drawing);
        return placed;
    }

    /// <summary>Where an element's viewBox lands in a w x h box (see <see cref="Fitted"/>), or null without a viewBox.</summary>
    static Matrix? Placement(System.Xml.Linq.XElement e, double w, double h)
    {
        if (ViewBox(e) is not [var x, var y, var bw, var bh] || !(bw > 0 && bh > 0)) return null;
        var aspect = ((string?)e.Attribute("preserveAspectRatio") ?? "xMidYMid meet").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var align = aspect.FirstOrDefault() ?? "xMidYMid";
        double sx = w / bw, sy = h / bh;
        if (align != "none") sx = sy = aspect.Contains("slice") ? Math.Max(sx, sy) : Math.Min(sx, sy);
        double fx = align.Contains("xMid") ? 0.5 : align.Contains("xMax") ? 1 : 0, fy = align.Contains("YMid") ? 0.5 : align.Contains("YMax") ? 1 : 0;
        return new Matrix(sx, 0, 0, sy, (w - bw * sx) * fx - x * sx, (h - bh * sy) * fy - y * sy);
    }

    static double[]? ViewBox(System.Xml.Linq.XElement e) => ((string?)e.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
        .Select(v => Number(v) ?? double.NaN).ToArray();

    static double? Number(string s) => double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>A length in units: "50%" of <paramref name="of"/>, "12px" or "12"; null when absent or in another unit.</summary>
    static double? Length(string? v, double of) => v is null ? null : v.EndsWith('%') ? Number(v[..^1]) * of / 100 : Number(v.EndsWith("px") ? v[..^2] : v);

    static readonly string[] StrokeProperties = ["stroke", "stroke-width", "stroke-opacity", "stroke-dasharray", "stroke-dashoffset", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit"];
    static readonly string[] FillProperties = ["fill", "fill-opacity", "fill-rule"];

    /// <summary>
    /// The SVG rewritten in the plainer form SVGImage draws as a browser does. A &lt;use&gt; becomes the copy it stands for and a nested &lt;svg&gt;
    /// a group placed by its viewBox; #RRGGBBAA becomes a colour and an opacity (SVGImage drops the alpha); hidden parts and &lt;symbol&gt;s (SVGImage
    /// draws them) are left out, blurred ones (it cannot blur) faded; stroke widths in % become units; an element
    /// that changes one stroke or fill property gets the others it inherits written on it (SVGImage starts them over, in black); and the
    /// opacities SVGImage leaves out, a group's and any on a gradient, go down to the children and into faded copies of the gradients.
    /// </summary>
    static void Simplify(XElement root, double w, double h)
    {
        var (vw, vh) = ViewBox(root) is [_, _, var bw, var bh] && bw > 0 && bh > 0 ? (bw, bh) : (w, h);
        var ns = root.Name.Namespace;
        Dictionary<string, XElement> Ids() => root.Descendants().Where(e => e.Attribute("id") is not null).GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        var ids = Ids();

        var budget = 50_000; // elements copied in all: ten <use> of ten <use> of ... would otherwise fill the memory
        // one in <defs> or a <symbol> waits until a copy brings it out where it is drawn
        static bool Drawn(XElement use) => use.Name.LocalName == "use" && !use.Ancestors().Any(a => a.Name.LocalName is "defs" or "symbol" or "clipPath" or "mask" or "pattern" or "marker");
        for (var depth = 0; root.Descendants().Where(Drawn).ToList() is { Count: > 0 } uses; depth++)
            foreach (var use in uses)
            {
                if (depth > 8 || use.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value is not ['#', .. var id] || !ids.TryGetValue(id, out var target)
                    || (budget -= target.DescendantsAndSelf().Count()) < 0)
                {
                    use.Remove(); // a loop, a missing shape or a bomb of copies draws nothing
                    continue;
                }
                var copy = new XElement(target);
                copy.Attribute("id")?.Remove();
                if (copy.Name.LocalName is "symbol" or "svg")
                {
                    foreach (var name in (string[])["width", "height"])
                        copy.SetAttributeValue(name, (string?)use.Attribute(name) ?? (copy.Name.LocalName == "svg" ? (string?)copy.Attribute(name) : null) ?? "100%");
                    copy.SetAttributeValue("x", null);
                    copy.SetAttributeValue("y", null);
                    copy.Name = ns + "svg";
                }
                double x = Length((string?)use.Attribute("x"), vw) ?? 0, y = Length((string?)use.Attribute("y"), vh) ?? 0;
                var group = new XElement(ns + "g", use.Attributes().Where(a => a.Name.LocalName is not ("href" or "x" or "y" or "width" or "height")), copy);
                group.SetAttributeValue("transform", ((string?)use.Attribute("transform") + $" translate({Css(x)} {Css(y)})").Trim());
                use.ReplaceWith(group);
            }

        var viewports = new Dictionary<XElement, (double W, double H)> { [root] = (vw, vh) };
        foreach (var svg in root.Descendants().Where(e => e.Name.LocalName == "svg").ToList())
        {
            var (pw, ph) = viewports[svg.Ancestors().First(viewports.ContainsKey)];
            double x = Length((string?)svg.Attribute("x"), pw) ?? 0, y = Length((string?)svg.Attribute("y"), ph) ?? 0;
            double sw = Length((string?)svg.Attribute("width"), pw) ?? pw, sh = Length((string?)svg.Attribute("height"), ph) ?? ph;
            var m = Placement(svg, sw, sh) ?? Matrix.Identity;
            m.Translate(x, y);
            viewports[svg] = ViewBox(svg) is [_, _, var bw2, var bh2] && bw2 > 0 && bh2 > 0 ? (bw2, bh2) : (sw, sh);
            foreach (var name in (string[])["x", "y", "width", "height", "viewBox", "preserveAspectRatio"]) svg.Attribute(name)?.Remove();
            // shortcut: the nested box does not clip what overflows it; DVDStyler's buttons stay inside theirs
            svg.SetAttributeValue("transform", ((string?)svg.Attribute("transform") + $" matrix({Css(m.M11)} {Css(m.M12)} {Css(m.M21)} {Css(m.M22)} {Css(m.OffsetX)} {Css(m.OffsetY)})").Trim());
            svg.Name = ns + "g";
        }

        var own = root.DescendantsAndSelf().ToDictionary(e => e, Declared);
        foreach (var e in root.Descendants().ToList())
        {
            if (!e.Ancestors().Contains(root)) continue; // inside a part already left out
            var p = own[e];
            var blur = p.GetValueOrDefault("filter") is { } f && UrlRef().Match(f) is { Success: true } u && ids.TryGetValue(u.Groups[1].Value, out var filter)
                ? filter.Descendants().Where(d => d.Name.LocalName == "feGaussianBlur").SelectMany(d => ((string?)d.Attribute("stdDeviation") ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
                    .Select(v => Number(v) ?? 0).DefaultIfEmpty(0).Max()
                : 0;
            if (e.Name.LocalName == "symbol"
                || (p.GetValueOrDefault("visibility") == "hidden" || p.GetValueOrDefault("display") == "none") && !e.Descendants().Any(d => own[d].GetValueOrDefault("visibility") == "visible"))
            {
                e.Remove();
                continue;
            }
            // shortcut: SVGImage cannot blur, so a blur of a unit or more (a shadow, a glow) is drawn sharp at half its opacity, its ink being spread thinner
            if (blur >= 1) Declare(e, p, "opacity", Css((Number(p.GetValueOrDefault("opacity") ?? "1") ?? 1) / 2));
            foreach (var (paint, opacity) in (ReadOnlySpan<(string, string)>)[("fill", "fill-opacity"), ("stroke", "stroke-opacity"), ("stop-color", "stop-opacity")])
                if (p.GetValueOrDefault(paint) is { } colour && Rgba().Match(colour) is { Success: true } rgba)
                {
                    Declare(e, p, paint, "#" + rgba.Groups[1].Value);
                    Declare(e, p, opacity, Css((Number(p.GetValueOrDefault(opacity) ?? "1") ?? 1) * Convert.ToInt32(rgba.Groups[2].Value, 16) / 255));
                }
            if (p.GetValueOrDefault("stroke-width") is { } width && width.EndsWith('%') && Length(width, Math.Sqrt((vw * vw + vh * vh) / 2)) is { } units)
                Declare(e, p, "stroke-width", Css(units));
            if (e.Name.LocalName is "rect" or "circle" or "ellipse" or "line" or "image" or "text") // SVGImage takes an rx="45%" of the height, not the width
            {
                var (pw, ph) = viewports[e.Ancestors().First(viewports.ContainsKey)];
                foreach (var a in e.Attributes().Where(a => a.Value.EndsWith('%')).ToList())
                    if (Length(a.Value, a.Name.LocalName switch
                        {
                            "x" or "cx" or "rx" or "x1" or "x2" or "width" => pw,
                            "y" or "cy" or "ry" or "y1" or "y2" or "height" => ph,
                            "r" => Math.Sqrt((pw * pw + ph * ph) / 2),
                            _ => double.NaN,
                        }) is { } at && !double.IsNaN(at))
                        a.Value = Css(at);
            }
            if ((string?)e.Attribute("gradientUnits") == "userSpaceOnUse") // its % are of the picture, which SVGImage does not know
                foreach (var a in e.Attributes().Where(a => a.Value.EndsWith('%')).ToList())
                    if (Length(a.Value, a.Name.LocalName is "x1" or "x2" or "cx" or "fx" ? vw : a.Name.LocalName is "y1" or "y2" or "cy" or "fy" ? vh : Math.Sqrt((vw * vw + vh * vh) / 2)) is { } at)
                        a.Value = Css(at);
        }

        // shortcut: a group's opacity goes to its children and a shape's to its fill and stroke, so parts that overlap show through each other where the standard fades them as one
        foreach (var e in root.Descendants().ToList())
        {
            var p = own[e];
            if ((Number(p.GetValueOrDefault("opacity") ?? "1") ?? 1) is var opacity && opacity >= 1) continue;
            if (e.Name.LocalName == "g")
                foreach (var child in e.Elements())
                    Declare(child, own[child], "opacity", Css(opacity * (Number(own[child].GetValueOrDefault("opacity") ?? "1") ?? 1)));
            else if (e.Name.LocalName is "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" or "text")
                foreach (var fade in (string[])["fill-opacity", "stroke-opacity"])
                    Declare(e, p, fade, Css(opacity * (Number(e.AncestorsAndSelf().Select(a => own[a].GetValueOrDefault(fade)).FirstOrDefault(v => v is not null) ?? "1") ?? 1)));
            else continue;
            Declare(e, p, "opacity", "1");
        }

        foreach (var e in root.Descendants())
        {
            foreach (var group in (string[][])[StrokeProperties, FillProperties])
                if (group.Any(own[e].ContainsKey))
                    foreach (var name in group.Where(n => !own[e].ContainsKey(n)).ToList())
                        if (e.Ancestors().Select(a => own[a].GetValueOrDefault(name)).FirstOrDefault(v => v is not null) is { } inherited)
                            Declare(e, own[e], name, inherited);
            if (StrokeProperties.Any(own[e].ContainsKey) && !own[e].ContainsKey("stroke"))
                Declare(e, own[e], "stroke", "none"); // the standard's default, where SVGImage strokes in black
        }

        ids = Ids();
        foreach (var e in root.Descendants().ToList())
            foreach (var (paint, fade) in (ReadOnlySpan<(string, string)>)[("fill", "fill-opacity"), ("stroke", "stroke-opacity")])
                if (own[e].GetValueOrDefault(paint) is { } v && UrlRef().Match(v) is { Success: true } u && ids.TryGetValue(u.Groups[1].Value, out var gradient)
                    && gradient.Name.LocalName.EndsWith("Gradient") && (Number(own[e].GetValueOrDefault(fade) ?? "1") ?? 1) is var alpha && alpha < 1)
                {
                    Declare(e, own[e], paint, $"url(#{Faded(gradient, alpha)})");
                    Declare(e, own[e], fade, "1");
                }

        string Faded(XElement gradient, double alpha)
        {
            var id = $"{(string?)gradient.Attribute("id")}-faded-{Css(Math.Round(alpha, 3))}";
            if (ids.ContainsKey(id)) return id;
            var copy = new XElement(gradient);
            copy.SetAttributeValue("id", id);
            if (!copy.Elements().Any(s => s.Name.LocalName == "stop") && gradient.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value is ['#', .. var from]
                && ids.TryGetValue(from, out var source))
                copy.Add(source.Elements().Where(s => s.Name.LocalName == "stop").Select(s => new XElement(s)));
            foreach (var stop in copy.Elements().Where(s => s.Name.LocalName == "stop"))
            {
                var sp = Declared(stop);
                Declare(stop, sp, "stop-opacity", Css((Number(sp.GetValueOrDefault("stop-opacity") ?? "1") ?? 1) * alpha));
            }
            gradient.AddAfterSelf(copy);
            ids[id] = copy;
            return id;
        }
    }

    static string Css(double v) => v.ToString(CultureInfo.InvariantCulture);

    [System.Text.RegularExpressions.GeneratedRegex(@"url\(\s*#([^)\s]+)\s*\)")]
    private static partial System.Text.RegularExpressions.Regex UrlRef();

    /// <summary>An element's own presentation properties, its style winning over its attributes as in CSS.</summary>
    static Dictionary<string, string> Declared(System.Xml.Linq.XElement e)
    {
        var p = e.Attributes().Where(a => a.Name.Namespace == System.Xml.Linq.XNamespace.None).ToDictionary(a => a.Name.LocalName, a => a.Value.Trim());
        foreach (var d in ((string?)e.Attribute("style") ?? "").Split(';'))
            if (d.Split(':', 2) is [var k, var v]) p[k.Trim()] = v.Trim();
        return p;
    }

    static void Declare(System.Xml.Linq.XElement e, Dictionary<string, string> own, string name, string value)
    {
        own[name] = value;
        e.Attribute(name)?.Remove(); // one value, whichever of style and attribute a renderer reads last
        e.SetAttributeValue("style", ((string?)e.Attribute("style") is { Length: > 0 } s ? s.TrimEnd(' ', ';') + ";" : "") + $"{name}:{value}");
    }

    [System.Text.RegularExpressions.GeneratedRegex("^#([0-9a-fA-F]{6})([0-9a-fA-F]{2})$")]
    private static partial System.Text.RegularExpressions.Regex Rgba();

    /// <summary>Dashes end as the stroke does (WPF has a separate setting for them): "0 7" with round caps is a row of dots, as in a browser.</summary>
    static Drawing RoundDashes(Drawing drawing)
    {
        if (drawing is DrawingGroup g)
            foreach (var child in g.Children) RoundDashes(child);
        else if (drawing is GeometryDrawing { IsFrozen: false, Pen: { DashStyle.Dashes.Count: > 0 } pen } shape && pen.DashCap != pen.StartLineCap)
        {
            var rounded = pen.Clone();
            rounded.DashCap = pen.StartLineCap;
            shape.Pen = rounded;
        }
        return drawing;
    }

    /// <summary>What an SVG may open besides itself: nothing (its links are taken out before, this is in case one is missed).</summary>
    sealed class NoFiles : SVGImage.SVG.FileLoaders.IExternalFileLoader
    {
        public Stream LoadFile(string hRef, string svgFilename) => throw new InvalidDataException($"lien externe refusé : {hRef}");
    }

    // the file's date is part of the key: a build rewrites its thumbnails under the same names, and the app would draw the previous disc's
    static ImageSource Bitmap(string path) => Images.GetOrAdd($"{path}|{File.GetLastWriteTimeUtc(path).Ticks}", _ =>
    {
        var p = path;
        try
        {
            CheckSize(p);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // WPF keeps its own cache by file name, which would give the old picture back
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
                default: // "box": the accent frame BurnSmith has always drawn; around a picture (no label) a bare frame, as DVDFab and Yuhan do, or the fill hides the thumbnail
                    var pen = new Pen(new SolidColorBrush(Col(d.SelectionInk)), 6);
                    var fill = label == null ? null : new SolidColorBrush(Col(d.SelectionInk, Math.Clamp(d.SelectionOpacity, 0, 100) / 100.0));
                    dc.DrawRoundedRectangle(fill, pen, new Rect(3, 3, box.W - 6, box.H - 6), 22, 22);
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

    static Typeface Face(Text t)
    {
        // a template's own .ttf is read from its folder (never installed); the system font of the same name is the fallback
        // when neither the template nor Windows has the font (Brush Script MT, Showcard Gothic... are Office fonts), one of the same kind from Windows stands in
        var stand = t.Font.ToLowerInvariant() switch
        {
            var f when f.Contains("script") || f.Contains("brush") || f.Contains("corsiva") || f.Contains("calligraph") || f.Contains("handwrit") => "Segoe Script",
            var f when f.Contains("showcard") || f.Contains("impact") || f.Contains("cooper") => "Segoe UI Black",
            var f when f.Contains("alien") || f.Contains("space") || f.Contains("tech") => "Bahnschrift SemiBold", // science-fiction lettering (SF Alien Encounters): Windows' engineering face
            _ => "Segoe UI",
        };
        var family = t.FontDir == null ? new FontFamily($"{t.Font}, {stand}")
            : new FontFamily(new Uri(Path.GetFullPath(t.FontDir).TrimEnd('\\') + "\\"), $"./#{t.Font}, {t.Font}, {stand}");
        return new Typeface(family, t.Italic ? FontStyles.Italic : FontStyles.Normal, t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
    }

    /// <summary>Width in pixels of a text on one line at its own size, as <see cref="DrawText"/> writes it when its zone is wide enough.</summary>
    internal static double TextWidth(Text t) =>
        new FormattedText(t.Value, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face(t), t.Size, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;

    static void DrawText(DrawingContext dc, Text t)
    {
        var face = Face(t);
        FormattedText Make(double size) => new(t.FontDir == null ? t.Value : Drawable(face, t.Value), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                               face, size, new SolidColorBrush(Col(t.Color)), 1.0);
        // one line longer than its zone is drawn smaller (down to 60 %), then on two lines when the zone is high enough, before it is cut with "…":
        // "Cobra Kai Saison 1" (01movie: 240 x 70 at 48) came out as "Cobra..."
        var natural = t.Wrap ? 0 : Make(t.Size).WidthIncludingTrailingWhitespace;
        // 0.98: a line shrunk to exactly its zone's width was still cut ("My Blu-r…" on 01movie), the last pixel being a matter of rounding
        var size = natural > t.Rect.W ? Math.Max(t.Size * 0.6, t.Size * t.Rect.W / natural * 0.98) : t.Size;
        var lines = t.Wrap ? Math.Max(1, (int)(t.Rect.H / (t.Size * 1.25))) : 1;
        var room = (int)(t.Rect.H / (t.Size * 1.25)); // a zone high enough for several lines at full size takes them first (a synopsis in a tall text box)
        if (!t.Wrap && natural > t.Rect.W && room >= 2) (lines, size) = (room, t.Size);
        else if (!t.Wrap && natural * 0.6 > t.Rect.W && t.Rect.H / 2.5 >= t.Size * 0.5) (lines, size) = (2, Math.Min(size, t.Rect.H / 2.5));
        var text = Make(size);
        text.MaxTextWidth = t.Rect.W;
        text.MaxLineCount = lines;
        text.Trimming = TextTrimming.CharacterEllipsis;
        var x = t.Align == "left" ? t.Rect.X : t.Rect.X + (t.Rect.W - text.Width) / (t.Align == "right" ? 1 : 2);
        var at = new Point(x, t.Wrap ? t.Rect.Y : t.Rect.Y + (t.Rect.H - text.Height) / 2); // several lines start at the top of their zone
        if (t.Shadow != null) // under everything; a blurred shadow is drawn sharp at half its strength, as for the pictures (Simplify)
        {
            var shade = Make(size);
            (shade.MaxTextWidth, shade.MaxLineCount, shade.Trimming) = (t.Rect.W, lines, TextTrimming.CharacterEllipsis);
            shade.SetForegroundBrush(new SolidColorBrush(Col(t.Shadow, 0.5)));
            dc.DrawText(shade, new Point(at.X + t.ShadowOffset, at.Y + t.ShadowOffset));
        }
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

    /// <summary>Width in pixels of a label as <see cref="DrawLabel"/> draws it.</summary>
    internal static double TextWidth(MenuDesign d, string value, int size, bool bold) =>
        new FormattedText(Shown(d, value), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(d.Font), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal), size, Brushes.White, 1.0).Width;

    /// <summary>A label as written on the page: in capitals in the Metal style, like a title logo.</summary>
    static string Shown(MenuDesign d, string value) => d.ButtonStyle == "Metal" ? value.ToUpperInvariant() : value;

    static void DrawLabel(DrawingContext dc, MenuDesign d, Label l, Color? colour = null)
    {
        var face = new Typeface(new FontFamily(d.Font), FontStyles.Normal, l.Bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(Shown(d, l.Value), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                     face, l.Size, new SolidColorBrush(colour ?? Col(d.TextColor)), 1.0)
        { MaxTextWidth = l.Rect.W, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var x = l.Align == "left" ? l.Rect.X : l.Rect.X + (l.Rect.W - text.Width) / 2;
        var at = new Point(x, l.Rect.Y + (l.Rect.H - text.Height) / 2);
        if (d.ButtonStyle != "Metal")
        {
            dc.DrawText(text, at);
            return;
        }
        // the look of a film's title logo: a warm glow (widening strokes of the design's Glow colour), a drop shadow, brushed steel letters
        var shape = text.BuildGeometry(at);
        var glow = Col(d.Glow);
        foreach (var (width, alpha) in new[] { (0.50, 0.07), (0.34, 0.11), (0.20, 0.17), (0.10, 0.28) })
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), glow.R, glow.G, glow.B)), l.Size * width) { LineJoin = PenLineJoin.Round }, shape);
        dc.PushTransform(new TranslateTransform(l.Size * 0.04, l.Size * 0.06));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), null, shape);
        dc.Pop();
        Brush steel = colour is { } c ? new SolidColorBrush(c) : new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(246, 246, 250), 0), new(Color.FromRgb(222, 222, 228), 0.44), new(Color.FromRgb(150, 150, 156), 0.58),
            new(Color.FromRgb(214, 214, 220), 0.72), new(Color.FromRgb(196, 196, 202), 1),
        }, 90);
        dc.DrawGeometry(steel, null, shape);
    }
}
