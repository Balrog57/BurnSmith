using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BurnSmith.Core;

/// <summary>
/// The scan of a disc's printed face made into a label to print on a printable disc: a square PNG (118 mm at 300 dpi = 1394 px),
/// white outside the disc and in the centre hole. The artwork of a pressed disc is centred on its hub, not on its outer edge
/// (Judge Dredd: 28 px = 0.6 mm apart at 1200 dpi, enough to show the brown inner ring of the print on one side), so the
/// centre is the circle where the light, colourless hub ends; the outer edge is only the fallback.
/// </summary>
public static class DiscLabel
{
    public sealed record Result(double CenterX, double CenterY, double HubRadius, double OuterX, double OuterY, double OuterRadius,
        double ScanDpi, bool OnHub, double HoleRadius, List<string> Notes);

    sealed record Scan(byte[] Px, int W, int H, int Stride)
    {
        public (int B, int G, int R) At(double x, double y)
        {
            var i = Math.Clamp((int)y, 0, H - 1) * Stride + Math.Clamp((int)x, 0, W - 1) * 3;
            return (Px[i], Px[i + 1], Px[i + 2]);
        }
    }

    // the disc at a glance: dark or coloured (the lid around it is shaded down to ~215 near the disc); the hub (clear plastic over the lid, centre hole): light and nearly colourless
    static bool Ink((int B, int G, int R) p) => Lum(p) < 200 || Chroma(p) > 25;
    static int Lum((int B, int G, int R) p) => (p.B + p.G + p.R) / 3;
    static bool Hub((int B, int G, int R) p) => Lum(p) >= 185 && Chroma(p) <= 15;
    static int Chroma((int B, int G, int R) p) => Math.Max(p.B, Math.Max(p.G, p.R)) - Math.Min(p.B, Math.Min(p.G, p.R));

    /// <param name="holeMm">diameter of the white centre hole; null = just past the inner edge of the print (hub + 1.2 mm)</param>
    public static Result Make(string scanPath, string outPath, double diameterMm = 118, double dpi = 300, double? holeMm = null, string? debugPath = null)
    {
        BitmapSource src = BitmapFrame.Create(new Uri(Path.GetFullPath(scanPath)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var fileDpi = src.DpiX;
        if (src.Format != PixelFormats.Bgr24) src = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        if ((long)src.PixelWidth * 3 * src.PixelHeight > Array.MaxLength) throw new InvalidOperationException("scan trop grand (2 Go de pixels au plus)");
        var scan = new Scan(new byte[src.PixelWidth * 3 * src.PixelHeight], src.PixelWidth, src.PixelHeight, src.PixelWidth * 3);
        src.CopyPixels(scan.Px, scan.Stride, 0);
        var notes = new List<string>();

        var (rx, ry, rr) = RoughDisc(scan);
        var mmPx = rr * 2 / 120; // a first scale from the disc itself, for the tolerances
        var k = Math.Max(4, (int)(0.5 * mmPx)); // an edge must stay crossed for 0.5 mm: the thin rings of the clear plastic do not
        var tol = Math.Max(1.5, 0.064 * mmPx);  // 3 px at 1200 dpi
        var outer = Ransac(Edges(scan, rx, ry, 0.85 * rr, 1.15 * rr, (c, s) =>
        {
            // this ray's lid: its median grey just past the disc, which the shading of the lid changes from side to side
            var lid = Enumerable.Range(0, 40).Select(i => Lum(scan.At(rx + (1.1 + i * 0.0025) * rr * c, ry + (1.1 + i * 0.0025) * rr * s))).Order().ElementAt(20);
            return p => Math.Abs(Lum(p) - lid) > 10 || Chroma(p) > 15;
        }, k), tol) ?? (rx, ry, rr, 0, 0);
        var hub = Ransac(Edges(scan, outer.X, outer.Y, 0.1 * outer.R, 0.3 * outer.R, (_, _) => Hub, k), tol);

        // the scan's dpi from its file, unless it disagrees with a 120 mm disc (no pHYs: WPF says 96)
        var discDpi = outer.R * 2 / (120 / 25.4);
        var scanDpi = Math.Abs(fileDpi / discDpi - 1) <= 0.15 ? fileDpi : discDpi;
        if (scanDpi != fileDpi) notes.Add($"résolution du fichier ({fileDpi:0} ppp) ignorée : {scanDpi:0} ppp d'après le disque de 120 mm");
        var px1mm = scanDpi / 25.4;

        var onHub = hub is { } h && h.Inliers >= 0.4 * h.Rays && h.R is var hr && hr > 0.12 * outer.R && hr < 0.25 * outer.R
            && Math.Sqrt((h.X - outer.X) * (h.X - outer.X) + (h.Y - outer.Y) * (h.Y - outer.Y)) < 2 * px1mm;
        if (!onHub) notes.Add(hub == null ? "moyeu introuvable : centré sur le bord extérieur du disque"
            : $"moyeu peu sûr ({hub.Value.Inliers}/{hub.Value.Rays} rayons, rayon {hub.Value.R:0} px) : centré sur le bord extérieur du disque");
        var (cx, cy) = onHub ? (hub!.Value.X, hub.Value.Y) : (outer.X, outer.Y);
        var hole = holeMm is { } hm ? hm / 2 * px1mm : onHub ? hub!.Value.R + 1.2 * px1mm : 11.7 * px1mm; // 23.4 mm: where Judge Dredd's print starts

        var size = (int)Math.Round(diameterMm / 25.4 * dpi);
        var crop = diameterMm / 2 * px1mm;
        var label = Resample(scan, cx - crop, cy - crop, 2 * crop, size);
        // white outside the disc and in the hole, edges smoothed over one pixel
        var s = size / (2 * crop);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = Math.Sqrt((x + 0.5 - size / 2.0) * (x + 0.5 - size / 2.0) + (y + 0.5 - size / 2.0) * (y + 0.5 - size / 2.0));
                var a = Math.Clamp(size / 2.0 - d + 0.5, 0, 1) * Math.Clamp(d - hole * s + 0.5, 0, 1);
                for (var c = 0; c < 3; c++) { var i = (y * size + x) * 3 + c; label[i] = (byte)Math.Round(label[i] * a + 255 * (1 - a)); }
            }
        Save(outPath, label, size, size, dpi);

        if (debugPath != null)
        {
            // the scan around the disc at half size, with what was found: outer edge (blue), hub (green), hole (red), cut (yellow)
            var half = outer.R * 1.05;
            var dsize = (int)(half); // 2 * half scan px at 1/2
            var view = Resample(scan, outer.X - half, outer.Y - half, 2 * half, dsize);
            var f = dsize / (2 * half);
            (double, double) V(double x, double y) => ((x - (outer.X - half)) * f, (y - (outer.Y - half)) * f);
            void Ring(double x, double y, double r, byte b, byte g, byte rr2)
            {
                var (vx, vy) = V(x, y);
                for (var t = 0.0; t < 2 * Math.PI; t += 0.25 / Math.Max(1, r * f))
                    for (var w = -1.5; w <= 1.5; w += 0.5) Dot(view, dsize, vx + (r * f + w) * Math.Cos(t), vy + (r * f + w) * Math.Sin(t), b, g, rr2);
                for (var w = -12; w <= 12; w++) { Dot(view, dsize, vx + w, vy, b, g, rr2); Dot(view, dsize, vx, vy + w, b, g, rr2); }
            }
            Ring(outer.X, outer.Y, outer.R, 255, 80, 0);
            if (hub is { } hb) Ring(hb.X, hb.Y, hb.R, 0, 200, 0);
            Ring(cx, cy, hole, 0, 0, 255);
            Ring(cx, cy, crop, 0, 220, 255);
            Save(debugPath, view, dsize, dsize, scanDpi / 2);
        }
        return new Result(cx, cy, hub?.R ?? 0, outer.X, outer.Y, outer.R, scanDpi, onHub, hole, notes);
    }

    static void Dot(byte[] px, int size, double x, double y, byte b, byte g, byte r)
    {
        int xi = (int)x, yi = (int)y;
        if (xi < 0 || yi < 0 || xi >= size || yi >= size) return;
        var i = (yi * size + xi) * 3;
        px[i] = b; px[i + 1] = g; px[i + 2] = r;
    }

    /// <summary>The largest inked blob of the page, at 1/8: its box. A cell is kept when 70 % of the 17 x 17 cells around it are inked, which wears away the thin dark lines at the scan's border.</summary>
    static (double X, double Y, double R) RoughDisc(Scan scan)
    {
        const int f = 8, erode = 8;
        int w = scan.W / f, h = scan.H / f;
        var sum = new int[(w + 1) * (h + 1)]; // summed-area table of the mask
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                sum[(y + 1) * (w + 1) + x + 1] = (Ink(scan.At(x * f + f / 2, y * f + f / 2)) ? 1 : 0)
                    + sum[y * (w + 1) + x + 1] + sum[(y + 1) * (w + 1) + x] - sum[y * (w + 1) + x];
        var kept = new bool[w * h];
        for (var y = erode; y < h - erode; y++)
            for (var x = erode; x < w - erode; x++)
            {
                int x0 = x - erode, y0 = y - erode, x1 = x + erode + 1, y1 = y + erode + 1;
                kept[y * w + x] = sum[y1 * (w + 1) + x1] - sum[y0 * (w + 1) + x1] - sum[y1 * (w + 1) + x0] + sum[y0 * (w + 1) + x0] >= 0.7 * (2 * erode + 1) * (2 * erode + 1);
            }
        var seen = new bool[w * h];
        (int Count, int X0, int Y0, int X1, int Y1) best = default;
        var queue = new Queue<int>();
        for (var start = 0; start < w * h; start++)
        {
            if (!kept[start] || seen[start]) continue;
            (int Count, int X0, int Y0, int X1, int Y1) blob = (0, int.MaxValue, int.MaxValue, 0, 0);
            seen[start] = true; queue.Enqueue(start);
            while (queue.TryDequeue(out var i))
            {
                int x = i % w, y = i / w;
                blob = (blob.Count + 1, Math.Min(blob.X0, x), Math.Min(blob.Y0, y), Math.Max(blob.X1, x), Math.Max(blob.Y1, y));
                foreach (var j in new[] { i - 1, i + 1, i - w, i + w })
                    if (j >= 0 && j < w * h && kept[j] && !seen[j] && Math.Abs(j % w - x) <= 1) { seen[j] = true; queue.Enqueue(j); }
            }
            if (blob.Count > best.Count) best = blob;
        }
        if (best.Count == 0) throw new InvalidOperationException("aucun disque trouvé sur le scan");
        return ((best.X0 + best.X1 + 1) / 2.0 * f, (best.Y0 + best.Y1 + 1) / 2.0 * f, ((best.X1 - best.X0) + (best.Y1 - best.Y0) + 2) / 4.0 * f);
    }

    /// <summary>Along 720 rays from (cx, cy): the last radius where the ray's test holds before it stops holding for k pixels in a row.</summary>
    static List<(double X, double Y)> Edges(Scan scan, double cx, double cy, double r0, double r1, Func<double, double, Func<(int B, int G, int R), bool>> rayTest, int k)
    {
        var points = new List<(double, double)>();
        for (var ray = 0; ray < 720; ray++)
        {
            double c = Math.Cos(ray * Math.PI / 360), s = Math.Sin(ray * Math.PI / 360);
            var inside = rayTest(c, s);
            var run = 0; double? last = null;
            for (var r = r0; r < r1; r++)
            {
                if (inside(scan.At(cx + r * c, cy + r * s))) { last = r; run = 0; }
                else if (last != null && ++run >= k) break;
            }
            if (last is { } e && run >= k) points.Add((cx + e * c, cy + e * s));
        }
        return points;
    }

    /// <summary>RANSAC over 3-point circles, then the least-squares circle of the points within tol of the best one.</summary>
    static (double X, double Y, double R, int Inliers, int Rays)? Ransac(List<(double X, double Y)> pts, double tol)
    {
        if (pts.Count < 20) return null;
        var rnd = new Random(1);
        bool[]? best = null; var bestCount = 0;
        for (var it = 0; it < 1000; it++)
        {
            if (Fit([pts[rnd.Next(pts.Count)], pts[rnd.Next(pts.Count)], pts[rnd.Next(pts.Count)]]) is not { } c) continue;
            var inl = pts.Select(p => Math.Abs(Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y)) - c.R) < tol).ToArray();
            var count = inl.Count(b => b);
            if (count > bestCount) (best, bestCount) = (inl, count);
        }
        return best == null || Fit(pts.Where((_, i) => best[i]).ToList()) is not { } fit ? null : (fit.X, fit.Y, fit.R, bestCount, pts.Count);
    }

    /// <summary>Kåsa fit: x² + y² = a·x + b·y + c by least squares.</summary>
    static (double X, double Y, double R)? Fit(List<(double X, double Y)> pts)
    {
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, n = pts.Count, sxz = 0, syz = 0, sz = 0;
        foreach (var (x, y) in pts)
        {
            var z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; sxz += x * z; syz += y * z; sz += z;
        }
        // [sxx sxy sx; sxy syy sy; sx sy n] [a b c] = [sxz syz sz], by Cramer's rule
        double Det(double a1, double b1, double c1, double a2, double b2, double c2, double a3, double b3, double c3)
            => a1 * (b2 * c3 - b3 * c2) - b1 * (a2 * c3 - a3 * c2) + c1 * (a2 * b3 - a3 * b2);
        var d = Det(sxx, sxy, sx, sxy, syy, sy, sx, sy, n);
        if (Math.Abs(d) < 1e-9) return null;
        var a = Det(sxz, sxy, sx, syz, syy, sy, sz, sy, n) / d;
        var b = Det(sxx, sxz, sx, sxy, syz, sy, sx, sz, n) / d;
        var cc = Det(sxx, sxy, sxz, sxy, syy, syz, sx, sy, sz) / d;
        double X = a / 2, Y = b / 2, r2 = cc + X * X + Y * Y;
        return r2 > 0 ? (X, Y, Math.Sqrt(r2)) : null;
    }

    /// <summary>The square (x0, y0, side) of the scan as size x size pixels, Lanczos 3 (outside the scan: white).</summary>
    static byte[] Resample(Scan scan, double x0, double y0, double side, int size)
    {
        var scale = size / side;
        var support = 3 / Math.Min(1, scale);
        double L(double t) { t = Math.Abs(t); return t < 1e-9 ? 1 : t >= 3 ? 0 : 3 * Math.Sin(Math.PI * t) * Math.Sin(Math.PI * t / 3) / (Math.PI * Math.PI * t * t); }
        (int First, double[] W)[] Taps(double origin) => Enumerable.Range(0, size).Select(o =>
        {
            var centre = origin + (o + 0.5) / scale - 0.5;
            var first = (int)Math.Floor(centre - support) + 1;
            var w = Enumerable.Range(0, (int)Math.Ceiling(2 * support)).Select(j => L((first + j - centre) * Math.Min(1, scale))).ToArray();
            var total = w.Sum();
            return (first, w.Select(v => v / total).ToArray());
        }).ToArray();
        var cols = Taps(x0); var rows = Taps(y0);
        int rowFirst = rows[0].First, rowCount = rows[^1].First + rows[^1].W.Length - rowFirst;
        var tmp = new float[rowCount * size * 3];
        Parallel.For(0, rowCount, j =>
        {
            var y = rowFirst + j;
            for (var o = 0; o < size; o++)
            {
                double b = 0, g = 0, r = 0;
                var (first, w) = cols[o];
                for (var t = 0; t < w.Length; t++)
                {
                    var x = first + t;
                    var p = x < 0 || y < 0 || x >= scan.W || y >= scan.H ? (255, 255, 255) : scan.At(x, y);
                    b += w[t] * p.Item1; g += w[t] * p.Item2; r += w[t] * p.Item3;
                }
                var i = (j * size + o) * 3; tmp[i] = (float)b; tmp[i + 1] = (float)g; tmp[i + 2] = (float)r;
            }
        });
        var outPx = new byte[size * size * 3];
        Parallel.For(0, size, v =>
        {
            var (first, w) = rows[v];
            for (var o = 0; o < size; o++)
                for (var c = 0; c < 3; c++)
                {
                    double acc = 0;
                    for (var t = 0; t < w.Length; t++) acc += w[t] * tmp[((first + t - rowFirst) * size + o) * 3 + c];
                    outPx[(v * size + o) * 3 + c] = (byte)Math.Clamp(Math.Round(acc), 0, 255);
                }
        });
        return outPx;
    }

    static void Save(string path, byte[] px, int w, int h, double dpi)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Bgr24, null, px, w * 3)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        enc.Save(file);
    }
}
