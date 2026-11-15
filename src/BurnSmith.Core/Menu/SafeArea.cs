using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BurnSmith.Core.Menu;

/// <summary>
/// What a TV may crop: the action-safe (93 %) and title-safe (90 %) frames of SMPTE ST 2046-1 / EBU R95, centred on the 1920x1080 page.
/// The same shares for DVD and Blu-ray (a DVD page is this page scaled). A view aid only: nothing here changes the disc.
/// </summary>
public static class SafeArea
{
    public const double ActionShare = 0.93, TitleShare = 0.90;

    public static Box Frame(double share)
    {
        int w = (int)Math.Round(MenuBuilder.W * share), h = (int)Math.Round(MenuBuilder.H * share);
        return new((MenuBuilder.W - w) / 2, (MenuBuilder.H - h) / 2, w, h);
    }

    public static Box Action => Frame(ActionShare);
    public static Box Title => Frame(TitleShare);

    /// <summary>
    /// The buttons and texts of <paramref name="page"/> (drawn with <paramref name="d"/>) not wholly inside the title-safe frame: what it is (button id or text) and its rectangle.
    /// A text on one line is judged by the width its words take in its zone (a page title centred across the whole page is not out), a text inside a button by its button.
    /// shortcut: the height is the zone's, not the letters'; measure the ink if a tall text zone misleads.
    /// </summary>
    public static List<(string What, Box Rect)> Outside(MenuPage page, MenuDesign d)
    {
        var t = Title;
        bool In(Box b, Box f) => b.X >= f.X && b.Y >= f.Y && b.X + b.W <= f.X + f.W && b.Y + b.H <= f.Y + f.H;
        Box Words(Box r, double width, string align)
        {
            var w = Math.Min(r.W, (int)Math.Ceiling(width));
            return r with { X = align == "left" ? r.X : align == "right" ? r.X + r.W - w : r.X + (r.W - w) / 2, W = w };
        }
        var texts = page.Ops.Select(o => o switch
            {
                Label l => (l.Value, l.Rect, Words(l.Rect, MenuRenderer.TextWidth(d, l.Value, l.Size, l.Bold), l.Align)),
                Text x => (x.Value, x.Rect, MenuRenderer.TextWidth(x) is var w && x.Wrap && w > x.Rect.W ? x.Rect : Words(x.Rect, w, x.Align)), // several lines fill their zone
                _ => ("", default(Box), default(Box)),
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Item1) && !page.Buttons.Any(b => In(x.Item2, b.Rect)))
            .Select(x => ($"text \"{x.Item1}\"", x.Item3));
        return page.Buttons.Select(b => ($"button {b.Id}", b.Rect)).Concat(texts).Where(x => x.Item2.W > 0 && x.Item2.H > 0 && !In(x.Item2, t)).DistinctBy(x => x.Item2).ToList();
    }

    /// <summary>The two frames and an orange outline around each thing <see cref="Outside"/> finds, drawn on <paramref name="dc"/> at page scale.</summary>
    public static void Draw(DrawingContext dc, MenuPage page, MenuDesign d)
    {
        static Rect R(Box b) => new(b.X, b.Y, b.W, b.H);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 3) { DashStyle = new DashStyle([4, 3], 0) }, R(Action));
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xD0, 0xFF)), 3), R(Title));
        var orange = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)), 5);
        foreach (var (_, rect) in Outside(page, d)) dc.DrawRectangle(null, orange, R(rect));
    }

    /// <summary>A page JPEG with <see cref="Draw"/> over it (the CLI's previews). Call it on an STA thread.</summary>
    public static byte[] Over(byte[] jpeg, MenuPage page, MenuDesign d)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(BitmapFrame.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), new Rect(0, 0, MenuBuilder.W, MenuBuilder.H));
            Draw(dc, page, d);
        }
        var bmp = new RenderTargetBitmap(MenuBuilder.W, MenuBuilder.H, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>One warning line per thing out of the title-safe frame, on every page (empty when all is inside).</summary>
    public static IEnumerable<string> Warnings(IEnumerable<MenuPage> pages, MenuDesign d) =>
        pages.SelectMany(p => Outside(p, d).Select(x => $"warning: page {p.Id}: {x.What} ({x.Rect.X},{x.Rect.Y},{x.Rect.W},{x.Rect.H}) is outside the title-safe area (90 %): a TV may crop it"));
}
