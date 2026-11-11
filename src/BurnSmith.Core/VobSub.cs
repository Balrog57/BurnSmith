using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BurnSmith.Core;

/// <summary>
/// DVD subtitles kept as a pair of files, VobSub: the .idx is text (screen size, the 16 colours, then one line per picture with its time and where its
/// data starts in the .sub); the .sub is an MPEG program stream whose private-stream-1 packets carry the subpictures a DVD player draws (a size,
/// run-length coded pixels in four colours, and commands that give the colours, the place and when to show / hide it).
/// Read here into pictures, and written as the PGS a Blu-ray carries (<see cref="PgsSubtitles.Writer"/>), which a DVD then redraws like any other .sup.
/// </summary>
public static class VobSub
{
    /// <summary>One subtitle track of an .idx: its language ("und" when the file does not say), its substream number and its pictures (seconds, byte in the .sub).</summary>
    public sealed record Stream(int Number, string Lang, IReadOnlyList<(double Time, long Position)> Cues);

    public sealed record Index(int Width, int Height, int[] Palette, IReadOnlyList<Stream> Streams);

    /// <summary>
    /// A picture of <see cref="Width"/> x <see cref="Height"/> at (<see cref="X"/>, <see cref="Y"/>) of the screen, <see cref="Pixels"/> being numbers 0..3 into
    /// <see cref="Colors"/>, shown from <see cref="Start"/> to <see cref="End"/> (seconds).
    /// </summary>
    public sealed record Picture(double Start, double End, int X, int Y, int Width, int Height, byte[] Pixels, (byte R, byte G, byte B, byte A)[] Colors);

    static readonly Regex Cue = new(@"timestamp:\s*(-?)(\d+):(\d+):(\d+):(\d+)\s*,\s*filepos:\s*([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
    static readonly Regex Id = new(@"^id:\s*([^,\s]*)\s*,\s*index:\s*(\d+)", RegexOptions.IgnoreCase);
    static readonly Regex Size = new(@"^size:\s*(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase);

    /// <summary>The .sub that goes with an .idx.</summary>
    public static string SubFile(string idx) => Path.ChangeExtension(idx, ".sub");

    public static Index ReadIndex(string idxFile) => ParseIndex(File.ReadAllText(idxFile));

    /// <summary>The head of an .idx; <paramref name="width"/> x <paramref name="height"/> is the screen when the text names none.</summary>
    public static Index ParseIndex(string text, int width = 720, int height = 480)
    {
        var palette = Enumerable.Range(0, 16).Select(i => i * 0x111111).ToArray(); // greys when the file gives none
        var streams = new List<Stream>();
        List<(double, long)>? cues = null;
        string lang = "und";
        int number = 0;
        void Flush() { if (cues != null) streams.Add(new Stream(number, lang, cues)); cues = null; }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (Size.Match(line) is { Success: true } size) (width, height) = (int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value));
            else if (line.StartsWith("palette:", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = line[8..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                      .Select(p => int.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0).Take(16).ToArray();
                if (parsed.Length == 16) palette = parsed;
            }
            else if (Id.Match(line) is { Success: true } id)
            {
                Flush();
                (lang, number, cues) = (Languages.Code(id.Groups[1].Value) ?? "und", int.Parse(id.Groups[2].Value), new());
            }
            else if (cues != null && Cue.Match(line) is { Success: true } c)
            {
                var seconds = int.Parse(c.Groups[2].Value) * 3600 + int.Parse(c.Groups[3].Value) * 60 + int.Parse(c.Groups[4].Value) + int.Parse(c.Groups[5].Value) / 1000.0;
                cues.Add((c.Groups[1].Value == "-" ? -seconds : seconds, long.Parse(c.Groups[6].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }
        }
        Flush();
        return new Index(width, height, palette, streams);
    }

    /// <summary>
    /// The subpicture whose first packet starts at <paramref name="position"/> of the .sub (a 2048-byte pack, usually): the payloads of the packets of
    /// substream <paramref name="substream"/> put end to end until the size the subpicture announces is reached. Null when the data runs out first.
    /// </summary>
    public static byte[]? ReadSpu(byte[] sub, long position, int substream)
    {
        var spu = new MemoryStream();
        var want = -1;
        var p = (int)Math.Min(position, sub.Length);
        var stop = (int)Math.Min(sub.Length, position + (256 << 10)); // a subpicture is at most 64 KB: further on it is somebody else's
        while (p + 6 <= stop && (want < 0 || spu.Length < want))
        {
            if (sub[p] != 0 || sub[p + 1] != 0 || sub[p + 2] != 1) { p++; continue; } // padding, or the end of a pack header we skipped short
            var kind = sub[p + 3];
            if (kind == 0xBA) { if (p + 14 > sub.Length) break; p += 14 + (sub[p + 13] & 7); continue; } // pack header (MPEG-2) with its stuffing; a truncated .sub ends here
            var length = BinaryPrimitives.ReadUInt16BigEndian(sub.AsSpan(p + 4));
            if (kind == 0xBD && p + 9 <= sub.Length)
            {
                var payload = p + 9 + sub[p + 8];
                var end = Math.Min(sub.Length, p + 6 + length);
                if (payload < end && sub[payload] == substream)
                {
                    spu.Write(sub, payload + 1, end - payload - 1);
                    if (want < 0 && spu.Length >= 2) want = BinaryPrimitives.ReadUInt16BigEndian(spu.GetBuffer());
                    if (want == 0) return null;
                }
            }
            p += 6 + length;
        }
        return want > 0 && spu.Length >= want ? spu.ToArray()[..want] : null;
    }

    /// <summary>
    /// A subpicture as the DVD stores it. <see cref="Picture.Start"/> and <see cref="Picture.End"/> are the times it asks to show and hide at (End is NaN when it does not say),
    /// counted from its packet: the caller adds the time of that. Null when the data is not a subpicture this can read.
    /// </summary>
    public static Picture? Decode(byte[] spu, int[] palette)
    {
        if (spu.Length < 6) return null;
        int size = BinaryPrimitives.ReadUInt16BigEndian(spu), at = BinaryPrimitives.ReadUInt16BigEndian(spu.AsSpan(2));
        if (size > spu.Length || at < 4 || at + 4 > size) return null;
        int x = 0, y = 0, w = 0, h = 0, top = 0, bottom = 0, data = at; // the pixels lie between byte 4 and the first sequence
        int[] color = [0, 0, 0, 0], alpha = [-1, -1, -1, -1];
        double show = double.NaN, hide = double.NaN;
        var colored = false;
        for (var guard = 0; guard < 64; guard++)
        {
            var delay = BinaryPrimitives.ReadUInt16BigEndian(spu.AsSpan(at)) * 1024 / 90000.0;
            var next = BinaryPrimitives.ReadUInt16BigEndian(spu.AsSpan(at + 2));
            var p = at + 4;
            for (var done = false; !done && p < size;)
            {
                switch (spu[p++])
                {
                    case 0x00 or 0x01: if (double.IsNaN(show)) show = delay; break; // forced / normal start of display
                    case 0x02: hide = delay; break;
                    case 0x03 when p + 2 <= size:
                        if (!colored) { color = [spu[p + 1] & 15, spu[p + 1] >> 4, spu[p] & 15, spu[p] >> 4]; colored = true; } // background, pattern, emphasis 1, emphasis 2
                        p += 2;
                        break;
                    case 0x04 when p + 2 <= size:
                        if (alpha[0] < 0) alpha = [(spu[p + 1] & 15) * 17, (spu[p + 1] >> 4) * 17, (spu[p] & 15) * 17, (spu[p] >> 4) * 17];
                        p += 2;
                        break;
                    case 0x05 when p + 6 <= size:
                        (x, w) = (spu[p] << 4 | spu[p + 1] >> 4, (spu[p + 1] & 15) << 8 | spu[p + 2]);
                        (y, h) = (spu[p + 3] << 4 | spu[p + 4] >> 4, (spu[p + 4] & 15) << 8 | spu[p + 5]);
                        (w, h) = (w - x + 1, h - y + 1);
                        p += 6;
                        break;
                    case 0x06 when p + 4 <= size: (top, bottom) = (BinaryPrimitives.ReadUInt16BigEndian(spu.AsSpan(p)), BinaryPrimitives.ReadUInt16BigEndian(spu.AsSpan(p + 2))); p += 4; break;
                    case 0xFF: done = true; break;
                    default: return null; // a command this does not know: the length of its arguments is unknown
                }
            }
            if (next <= at || next + 4 > size) break; // the last sequence points to itself
            at = next;
        }
        if (w < 1 || h < 1 || w > 4096 || h > 4096 || top < 4 || bottom < top || bottom > size) return null;
        if (alpha[0] < 0) alpha = [0, 255, 255, 255];
        else if (alpha.All(a => a == 0)) alpha = [0, 255, 255, 255]; // every colour hidden: the file means them shown (players show them)
        var pixels = new byte[w * h];
        Field(spu, top, Math.Max(top, Math.Min(bottom, data)), pixels, w, h, 0);
        Field(spu, bottom, Math.Min(size, Math.Max(bottom, data)), pixels, w, h, 1);
        var colors = Enumerable.Range(0, 4).Select(i => (R: (byte)(palette[color[i]] >> 16), G: (byte)(palette[color[i]] >> 8), B: (byte)palette[color[i]], A: (byte)alpha[i])).ToArray();
        return new Picture(double.IsNaN(show) ? 0 : show, hide, x, y, w, h, pixels, colors);
    }

    /// <summary>The rows of one field (every other row from <paramref name="firstRow"/>): nibble codes of a run length and a colour, each row ending on a byte.</summary>
    static void Field(byte[] data, int from, int to, byte[] pixels, int w, int h, int firstRow)
    {
        int nibble = from * 2, last = to * 2;
        int Next() => nibble < last ? data[nibble >> 1] >> ((~nibble++ & 1) * 4) & 15 : 0;
        for (var row = firstRow; row < h; row += 2)
        {
            if (nibble >= last) return; // truncated: the rest stays transparent
            for (var col = 0; col < w;)
            {
                var v = Next();
                if (v < 4)
                {
                    v = v << 4 | Next();
                    if (v < 0x10)
                    {
                        v = v << 4 | Next();
                        if (v < 0x40)
                        {
                            v = v << 4 | Next();
                            if (v < 0x100) v |= (w - col) << 2; // a run of 0 goes to the end of the row
                        }
                    }
                }
                var run = Math.Min(v >> 2, w - col);
                if (run <= 0) run = 1;
                Array.Fill(pixels, (byte)(v & 3), row * w + col, run);
                col += run;
            }
            if ((nibble & 1) != 0) nibble++;
        }
    }

    /// <summary>The pictures of track <paramref name="stream"/> in order (null for one that could not be read), each hidden by its own command, else when the next shows.</summary>
    static IEnumerable<Picture?> Each(Index index, Stream stream, byte[] sub) =>
        Each(index, [.. stream.Cues.Select(c => c.Time)], i => ReadSpu(sub, stream.Cues[i].Position, 0x20 + stream.Number));

    /// <summary>The same for subpictures given one at a time: <paramref name="times"/> says when each one comes, <paramref name="spu"/> gives the data of the one at that number.</summary>
    static IEnumerable<Picture?> Each(Index index, IReadOnlyList<double> times, Func<int, byte[]?> spu)
    {
        for (var i = 0; i < times.Count; i++)
        {
            var picture = spu(i) is { } data ? Decode(data, index.Palette) : null;
            if (picture == null) { yield return null; continue; }
            var start = Math.Max(0, times[i]) + picture.Start;
            var end = double.IsNaN(picture.End) ? start + 3 : Math.Max(0, times[i]) + picture.End; // a picture that never says when to hide lasts three seconds
            if (i + 1 < times.Count) end = Math.Min(end, Math.Max(0, times[i + 1]));
            yield return picture with { Start = start, End = Math.Max(end, start + 0.1) };
        }
    }

    /// <summary>The pictures of one track of an .idx (tests and small files; <see cref="ToSup"/> goes one at a time).</summary>
    public static List<Picture> Read(string idxFile, int stream, out int skipped)
    {
        var index = ReadIndex(idxFile);
        var all = Each(index, index.Streams[stream], File.ReadAllBytes(SubFile(idxFile))).ToList();
        skipped = all.Count(p => p == null);
        return all.OfType<Picture>().ToList();
    }

    /// <summary>
    /// Track <paramref name="stream"/> of an .idx as a PGS (.sup) for a <paramref name="planeWidth"/> x <paramref name="planeHeight"/> screen: the whole DVD frame lands on the
    /// whole plane, and each picture is scaled smoothly, then brought down to at most 255 colours.
    /// </summary>
    public static byte[] ToSup(string idxFile, int stream, int planeWidth, int planeHeight, out int skipped)
    {
        var index = ReadIndex(idxFile);
        if (stream < 0 || stream >= index.Streams.Count) { skipped = 0; return []; }
        return ToSup(index, Each(index, index.Streams[stream], File.ReadAllBytes(SubFile(idxFile))), planeWidth, planeHeight, out skipped);
    }

    static byte[] ToSup(Index index, IEnumerable<Picture?> pictures, int planeWidth, int planeHeight, out int skipped)
    {
        double sx = (double)planeWidth / index.Width, sy = (double)planeHeight / index.Height;
        var writer = new PgsSubtitles.Writer(planeWidth, planeHeight);
        var bad = 0;
        foreach (var picture in pictures)
        {
            if (picture == null) { bad++; continue; }
            var (px, py, pw, ph, indexed, colors) = Scale(picture, sx, sy, planeWidth, planeHeight);
            writer.Add(picture.Start, picture.End, px, py, pw, ph, indexed, colors);
        }
        skipped = bad;
        return writer.Finish();
    }

    /// <summary>
    /// The text ffprobe's <c>-show_data</c> dumps the codec data of a stream as ("00000000: 7369 7a65 ...  size"): the bytes, as ASCII. For a VobSub in
    /// Matroska that text is the head of an .idx (size, palette).
    /// </summary>
    public static string ExtradataText(string dump)
    {
        var bytes = new List<byte>();
        foreach (var line in dump.Split('\n'))
            if (Regex.IsMatch(line, "^[0-9a-f]{8}: "))
                bytes.AddRange(Regex.Matches(line[10..Math.Min(line.Length, 49)], "[0-9a-f]{2}").Select(m => Convert.ToByte(m.Value, 16)));
        return System.Text.Encoding.ASCII.GetString([.. bytes]);
    }

    /// <summary>
    /// Subtitle track number <paramref name="track"/> (ffmpeg's 0:s:N) of a video that keeps a VobSub (Matroska: one subpicture per packet, the screen size and the
    /// colours in the codec data) as PGS, like <see cref="ToSup"/> does for an .idx. The packets are taken out with ffmpeg into a file in <paramref name="work"/>,
    /// their times and sizes read with ffprobe.
    /// </summary>
    public static byte[] FromVideo(string video, int track, int planeWidth, int planeHeight, string work, out int skipped)
    {
        var (ffprobe, select) = (Tools.Find("ffprobe.exe"), $"s:{track}");
        // a track cut out of a DVD often carries no codec data: its screen is then the video's, 576 lines on a PAL disc (read as 480, every line landed too low and was squashed at the bottom)
        var pal = Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=height", "-of", "csv=p=0", video]).Trim().TrimEnd(',') == "576"; // ffprobe ends the line with a comma
        var index = ParseIndex(ExtradataText(Tools.Run(ffprobe, ["-v", "error", "-select_streams", select, "-show_streams", "-show_data", video])), 720, pal ? 576 : 480);
        var packets = Tools.Run(ffprobe, ["-v", "error", "-select_streams", select, "-show_entries", "packet=pts_time,size", "-of", "csv=p=0", video])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split(','))
            .Select(f => (Time: double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0, Size: int.Parse(f[1], CultureInfo.InvariantCulture))).ToList();
        var raw = Path.Combine(work, $"vobsub_{track}.bin");
        try
        {
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-v", "error", "-y", "-i", video, "-map", $"0:s:{track}", "-c", "copy", "-f", "data", raw]);
            var data = File.ReadAllBytes(raw);
            var starts = new long[packets.Count];
            for (var i = 1; i < starts.Length; i++) starts[i] = starts[i - 1] + packets[i - 1].Size;
            if (packets.Sum(p => (long)p.Size) != data.Length) throw new InvalidDataException("the subtitle packets ffprobe lists are not the ones ffmpeg wrote");
            return ToSup(index, Each(index, [.. packets.Select(p => p.Time)], i => data[(int)starts[i]..(int)(starts[i] + packets[i].Size)]), planeWidth, planeHeight, out skipped);
        }
        finally { File.Delete(raw); }
    }

    /// <summary>
    /// A picture on the plane: each pixel blends the four around it (colours weighed by their opacity, so a clear colour does not bleed into the edge), then
    /// the colours are made coarser, a step at a time, until 255 are left.
    /// </summary>
    static (int X, int Y, int Width, int Height, byte[] Index, (byte R, byte G, byte B, byte A)[] Palette) Scale(Picture p, double sx, double sy, int planeWidth, int planeHeight)
    {
        int x = (int)Math.Round(p.X * sx), y = (int)Math.Round(p.Y * sy);
        x = Math.Clamp(x, 0, planeWidth - 1);
        y = Math.Clamp(y, 0, planeHeight - 1);
        int w = Math.Clamp((int)Math.Round(p.Width * sx), 1, planeWidth - x), h = Math.Clamp((int)Math.Round(p.Height * sy), 1, planeHeight - y);
        var pre = new float[p.Width * p.Height * 4]; // premultiplied R, G, B, A of each source pixel
        for (var i = 0; i < p.Pixels.Length; i++)
        {
            var (r, g, b, a) = p.Colors[p.Pixels[i]];
            (pre[i * 4], pre[i * 4 + 1], pre[i * 4 + 2], pre[i * 4 + 3]) = (r * a / 255f, g * a / 255f, b * a / 255f, a);
        }
        var rgba = new (int R, int G, int B, int A)[w * h];
        for (var v = 0; v < h; v++)
        {
            var fy = (v + 0.5) / h * p.Height - 0.5;
            int y0 = (int)Math.Floor(fy), y1 = Math.Clamp(y0 + 1, 0, p.Height - 1);
            var ty = (float)(fy - y0);
            y0 = Math.Clamp(y0, 0, p.Height - 1);
            for (var u = 0; u < w; u++)
            {
                var fx = (u + 0.5) / w * p.Width - 0.5;
                int x0 = (int)Math.Floor(fx), x1 = Math.Clamp(x0 + 1, 0, p.Width - 1);
                var tx = (float)(fx - x0);
                x0 = Math.Clamp(x0, 0, p.Width - 1);
                float Mix(int channel) =>
                    (pre[(y0 * p.Width + x0) * 4 + channel] * (1 - tx) + pre[(y0 * p.Width + x1) * 4 + channel] * tx) * (1 - ty)
                    + (pre[(y1 * p.Width + x0) * 4 + channel] * (1 - tx) + pre[(y1 * p.Width + x1) * 4 + channel] * tx) * ty;
                var a = Mix(3);
                rgba[v * w + u] = a < 8 ? (0, 0, 0, 0) : ((int)Math.Round(Mix(0) * 255 / a), (int)Math.Round(Mix(1) * 255 / a), (int)Math.Round(Mix(2) * 255 / a), (int)Math.Round(a));
            }
        }
        // coarser and coarser levels until the distinct colours fit a PGS palette (index 0 stays transparent)
        for (var step = 4; ; step *= 2)
        {
            int Q(int c) => Math.Min(255, (c + step / 2) / step * step);
            var palette = new Dictionary<(int, int, int, int), int>();
            var index = new byte[w * h];
            var fits = true;
            for (var i = 0; i < rgba.Length && fits; i++)
            {
                var (r, g, b, a) = rgba[i];
                if (a == 0) continue;
                var key = (Q(r), Q(g), Q(b), Q(a));
                if (!palette.TryGetValue(key, out var n))
                {
                    if (palette.Count == 255) { fits = false; break; }
                    palette[key] = n = palette.Count + 1;
                }
                index[i] = (byte)n;
            }
            if (!fits) continue;
            var colors = new (byte R, byte G, byte B, byte A)[palette.Count + 1];
            foreach (var ((r, g, b, a), n) in palette) colors[n] = ((byte)r, (byte)g, (byte)b, (byte)a);
            return (x, y, w, h, index, colors);
        }
    }
}
