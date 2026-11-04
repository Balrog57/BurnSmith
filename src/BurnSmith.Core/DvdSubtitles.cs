using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using BurnSmith.Core.Menu;
using static BurnSmith.Core.Menu.IndexedPng;

namespace BurnSmith.Core;

/// <summary>
/// The spumux build available here has no freetype, so it cannot draw text subtitles itself. Each SRT cue is drawn
/// with WPF into a 4-colour bitmap instead, which spumux accepts as a subpicture (the same trick as the menu buttons).
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

    /// <summary>One spumux &lt;stream&gt; for the cues of <paramref name="srt"/>; the bitmaps are written into <paramref name="dir"/>.</summary>
    public static XElement Stream(string srt, string dir, string prefix, int w, int h, double sar)
    {
        var cues = ParseSrt(srt);
        var images = MenuRenderer.Sta(() => cues.Select(c => Draw(c.Text, w, h, sar)).ToList());
        static string T(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);
        var stream = new XElement("stream");
        for (var i = 0; i < cues.Count; i++)
        {
            var name = $"{prefix}{i + 1:0000}.png";
            File.WriteAllBytes(Path.Combine(dir, name), images[i]);
            stream.Add(new XElement("spu", new XAttribute("start", T(cues[i].Start)), new XAttribute("end", T(cues[i].End)), new XAttribute("image", name)));
        }
        return stream;
    }

    /// <summary>
    /// spumux here always writes subtitle stream 0x20. To get several streams, each later pass is made on a file whose
    /// subtitle packets were moved up one id first: this raises every subtitle substream id (0x20..0x3E) by one, in place.
    /// A DVD program stream is made of 2048-byte sectors: pack header, then PES packets.
    /// </summary>
    public static void ShiftStreams(string mpg)
    {
        using var f = new FileStream(mpg, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        var s = new byte[2048];
        for (var sectors = f.Length / 2048; sectors > 0; sectors--)
        {
            f.ReadExactly(s);
            if (s[0] != 0 || s[1] != 0 || s[2] != 1 || s[3] != 0xBA) continue;
            var changed = false;
            for (var p = 14 + (s[13] & 7); p + 9 < 2048 && s[p] == 0 && s[p + 1] == 0 && s[p + 2] == 1; p += 6 + (s[p + 4] << 8 | s[p + 5]))
            {
                if (s[p + 3] != 0xBD) continue; // private stream 1: AC-3, subtitles...
                var payload = p + 9 + s[p + 8];
                if (payload < 2048 && s[payload] is >= 0x20 and < 0x3F) { s[payload]++; changed = true; }
            }
            if (!changed) continue;
            f.Seek(-2048, SeekOrigin.Current);
            f.Write(s);
        }
    }

    /// <summary>Full-frame bitmap, text at the bottom. DVD pixels are wider than tall (<paramref name="sar"/>), so the text is squeezed to look right on a 16:9 screen.</summary>
    static byte[] Draw(string text, int w, int h, double sar)
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
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, size * 0.12) { LineJoin = PenLineJoin.Round }, geometry);
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
            canvas.Pixels[i] = (byte)(luminance > 140 ? 2 : 1); // white text, black outline
        }
        return Encode(canvas, [new(0, 0, 0, 0), new(16, 16, 16, 255), new(255, 255, 255, 255), new(128, 128, 128, 255)]);
    }
}
