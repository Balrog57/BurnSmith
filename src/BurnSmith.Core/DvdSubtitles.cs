using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BurnSmith.Core.DvdVideo;
using BurnSmith.Core.Menu;
using static BurnSmith.Core.Menu.IndexedPng;

namespace BurnSmith.Core;

/// <summary>
/// A DVD subpicture has four codes and no text: each SRT cue is drawn with WPF into a 4-colour picture of the frame
/// (transparent, outline, text, edge), as is each PGS picture, brought down to those four.
/// </summary>
public static class DvdSubtitles
{
    public sealed record Cue(double Start, double End, string Text);

    static readonly Regex Timing = new(@"(\d+):(\d+):(\d+)[,.](\d+)\s*-->\s*(\d+):(\d+):(\d+)[,.](\d+)");
    static readonly Regex Markup = new(@"<[^>]+>|\{[^}]*\}");

    public static List<Cue> ParseSrt(string file)
    {
        static double Seconds(Match m, int g) =>
            int.Parse(m.Groups[g].Value) * 3600 + int.Parse(m.Groups[g + 1].Value) * 60 + int.Parse(m.Groups[g + 2].Value)
            + double.Parse("0." + m.Groups[g + 3].Value, CultureInfo.InvariantCulture);

        var cues = new List<Cue>();
        var lines = File.ReadAllLines(file).Select(l => l.TrimStart('﻿')).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (Timing.Match(lines[i]) is not { Success: true } m) continue;
            var text = new List<string>();
            for (i++; i < lines.Count && lines[i].Trim().Length > 0; i++) text.Add(Markup.Replace(lines[i], "").Trim());
            var joined = string.Join("\n", text.Where(t => t.Length > 0));
            var start = Seconds(m, 1);
            if (joined.Length > 0) cues.Add(new Cue(start, Math.Max(Seconds(m, 5), start + 0.1), joined));
        }
        return cues;
    }

    /// <summary>A subtitle file the DVD can show: SRT text, or PGS pictures (.sup).</summary>
    public static bool HasCues(string file) =>
        Path.GetExtension(file).Equals(".sup", StringComparison.OrdinalIgnoreCase) ? PgsSubtitles.Cues(File.ReadAllBytes(file), out _).Count > 0 : File.ReadAllText(file).Contains("-->");

    /// <summary>The cues of <paramref name="srt"/> as subpictures of the frame.</summary>
    public static List<Spu.Picture> Pictures(string srt, int w, int h, double sar)
    {
        var cues = ParseSrt(srt);
        var images = MenuRenderer.Sta(() => cues.Select(c => Draw(c.Text, w, h, sar)).ToList());
        return [.. cues.Select((c, i) => new Spu.Picture(c.Start, c.End, images[i].Pixels, w, h))];
    }

    /// <summary>The pictures of a PGS file (.sup), each scaled to the DVD frame and brought down to the four colours of a subpicture.</summary>
    public static List<Spu.Picture> PgsPictures(string sup, int w, int h, Action<string> log)
    {
        var cues = PgsSubtitles.Cues(File.ReadAllBytes(sup), out var skipped);
        if (skipped > 0) log($"warning: {skipped} picture(s) of {Path.GetFileName(sup)} could not be read and are left out");
        return [.. cues.Select(c => new Spu.Picture(c.Start, c.End, Redraw(c, w, h).Pixels, w, h))];
    }

    /// <summary>Full-frame bitmap, text at the bottom. DVD pixels are wider than tall (<paramref name="sar"/>), so the text is squeezed to look right on a 16:9 screen.</summary>
    static Canvas Draw(string text, int w, int h, double sar)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var size = h * 0.058;
            var face = new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.White, 1.0)
            { MaxTextWidth = (w - 120) * sar, TextAlignment = TextAlignment.Center };
            var geometry = ft.BuildGeometry(new Point((w * sar - ft.MaxTextWidth) / 2, h - h * 0.07 - ft.Height));
            dc.PushTransform(new ScaleTransform(1 / sar, 1));
            // as a commercial DVD: white letters, a black outline about 2 pixels wide, grey where they meet. The outline first, the white over it:
            // a pen drawn on top covers half its width inside the letters, all of a stroke 5 pixels thick
            dc.DrawGeometry(null, new Pen(Brushes.Black, size * 0.14) { LineJoin = PenLineJoin.Round }, geometry);
            dc.DrawGeometry(Brushes.White, null, geometry);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);

        var canvas = new Canvas(w, h);
        for (var i = 0; i < w * h; i++)
        {
            int a = px[i * 4 + 3];
            if (a < 60) continue; // transparent
            var luminance = (px[i * 4] + px[i * 4 + 1] + px[i * 4 + 2]) / 3.0 * 255 / a; // premultiplied -> plain
            canvas.Pixels[i] = (byte)(luminance >= 170 ? 2 : luminance >= 85 ? 3 : 1); // white text, grey edge, black outline (as the PGS pictures below)
        }
        return canvas;
    }

    /// <summary>The colours of codes 0..3 (transparent, black outline, white text, grey edge), as the first entries of a title's 16 colours.</summary>
    public static readonly int[] Rgb = [0x000000, 0x101010, 0xFFFFFF, 0x808080, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// A PGS picture on the DVD frame (the whole 16:9 plane lands on the whole frame, whatever the plane's size): each DVD pixel averages the
    /// source pixels under it, kept when mostly opaque, then white, grey or black by brightness. Colours other than grey levels are lost.
    /// </summary>
    static Canvas Redraw(PgsSubtitles.Cue cue, int w, int h)
    {
        double sx = (double)w / cue.PlaneWidth, sy = (double)h / cue.PlaneHeight;
        var canvas = new Canvas(w, h);
        for (var dy = Math.Max(0, (int)Math.Floor(cue.Y * sy)); dy < Math.Min(h, (int)Math.Ceiling((cue.Y + cue.Height) * sy)); dy++)
            for (var dx = Math.Max(0, (int)Math.Floor(cue.X * sx)); dx < Math.Min(w, (int)Math.Ceiling((cue.X + cue.Width) * sx)); dx++)
            {
                // the source pixels under this one, in the bitmap's own coordinates
                int fromX = (int)Math.Floor(dx / sx) - cue.X, toX = Math.Max(fromX + 1, (int)Math.Ceiling((dx + 1) / sx) - cue.X);
                int fromY = (int)Math.Floor(dy / sy) - cue.Y, toY = Math.Max(fromY + 1, (int)Math.Ceiling((dy + 1) / sy) - cue.Y);
                long opacity = 0, bright = 0;
                for (var y = Math.Max(0, fromY); y < Math.Min(cue.Height, toY); y++)
                    for (var x = Math.Max(0, fromX); x < Math.Min(cue.Width, toX); x++)
                    {
                        var i = y * cue.Width + x;
                        opacity += cue.Alpha[i];
                        bright += cue.Alpha[i] * cue.Luma[i];
                    }
                if (opacity == 0 || opacity / ((toX - fromX) * (toY - fromY)) < 70) continue; // mostly transparent
                var luma = bright / opacity;
                canvas.Pixels[dy * w + dx] = (byte)(luma >= 170 ? 2 : luma >= 85 ? 3 : 1);
            }
        return canvas;
    }
}
