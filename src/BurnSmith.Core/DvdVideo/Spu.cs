using System.Globalization;
using System.Text;

namespace BurnSmith.Core.DvdVideo;

/// <summary>
/// DVD subpictures, written: a subpicture unit is a 4-byte header, the run-length coded rows of the top field then of the bottom field, then its
/// display control sequences (colours, contrast, area, field addresses, when to show and hide). Pixels are codes 0..3; the CLUT of the PGC gives their
/// colours. Written in pairs of files, VobSub (.idx text + .sub program stream), which ffmpeg muxes into a DVD program stream as a subpicture stream.
/// </summary>
public static class Spu
{
    /// <summary>One picture: <paramref name="pixels"/> are <paramref name="width"/> x <paramref name="height"/> codes 0..3 of the whole frame.</summary>
    public sealed record Picture(double Start, double? End, byte[] Pixels, int Width, int Height);

    /// <summary>
    /// The subpicture unit of a full-frame picture, cropped to what is not code 0 (a 2x2 empty corner when all is).
    /// <paramref name="colors"/> and <paramref name="contrast"/> give, for codes 0..3, the CLUT entry and the opacity 0..15.
    /// <paramref name="seconds"/> null: shown until the next unit. <paramref name="forced"/>: shown whatever the subtitle setting (menu highlights).
    /// </summary>
    public static byte[] Unit(byte[] px, int w, int h, byte[] colors, byte[] contrast, double? seconds, bool forced)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (px[y * w + x] != 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < 0) (x0, y0, x1, y1) = (0, 0, 1, 1);
        y0 &= ~1; // the area starts on a top-field line, so the fields keep the video's parity
        if ((y1 - y0) % 2 == 0) y1 = Math.Min(h - 1, y1 + 1);

        var data = new List<byte> { 0, 0, 0, 0 };
        int top = data.Count;
        for (var y = y0; y <= y1; y += 2) Row(px, w, y, x0, x1, data);
        int bottom = data.Count;
        for (var y = y0 + 1; y <= y1; y += 2) Row(px, w, y, x0, x1, data);
        if (data.Count % 2 == 1) data.Add(0);

        var first = data.Count;
        var second = first + 4 + 1 + 3 + 3 + 7 + 5 + 1; // header, start, colours, contrast, area, addresses, end
        var hides = seconds is { } s;
        Put16(data, 0); // at once
        Put16(data, hides ? second : first);
        data.Add((byte)(forced ? 0x00 : 0x01));
        data.Add(0x03); data.Add((byte)(colors[3] << 4 | colors[2])); data.Add((byte)(colors[1] << 4 | colors[0]));
        data.Add(0x04); data.Add((byte)(contrast[3] << 4 | contrast[2])); data.Add((byte)(contrast[1] << 4 | contrast[0]));
        data.Add(0x05);
        data.Add((byte)(x0 >> 4)); data.Add((byte)((x0 & 15) << 4 | x1 >> 8)); data.Add((byte)x1);
        data.Add((byte)(y0 >> 4)); data.Add((byte)((y0 & 15) << 4 | y1 >> 8)); data.Add((byte)y1);
        data.Add(0x06); Put16(data, top); Put16(data, bottom);
        data.Add(0xFF);
        if (hides)
        {
            Put16(data, (int)Math.Min(0xFFFF, Math.Floor(seconds!.Value * 90000 / 1024))); // the delay counts 1024 ticks of 90 kHz; rounding up would hide a frame late
            Put16(data, second);
            data.Add(0x02); data.Add(0xFF);
        }
        if (data.Count > 53220) throw new InvalidOperationException("subpicture too large for a DVD (more than 53220 bytes)");
        data[0] = (byte)(data.Count >> 8); data[1] = (byte)data.Count;
        data[2] = (byte)(first >> 8); data[3] = (byte)first;
        return [.. data];
    }

    /// <summary>One row as runs of 4, 8, 12 or 16 bits; the last run says "to the end of the line"; the row ends on a byte.</summary>
    static void Row(byte[] px, int w, int y, int x0, int x1, List<byte> data)
    {
        var nibbles = new List<int>();
        for (var x = x0; x <= x1;)
        {
            var c = px[y * w + x];
            var n = 1;
            while (x + n <= x1 && px[y * w + x + n] == c && n < 255) n++;
            if (x + n > x1) { nibbles.AddRange([0, 0, 0, c]); break; } // to the end of the line
            if (n < 4) nibbles.Add(n << 2 | c);
            else if (n < 16) nibbles.AddRange([n >> 2, (n & 3) << 2 | c]);
            else if (n < 64) nibbles.AddRange([0, n >> 2, (n & 3) << 2 | c]);
            else nibbles.AddRange([0, n >> 6, (n >> 2) & 15, (n & 3) << 2 | c]);
            x += n;
        }
        if (nibbles.Count % 2 == 1) nibbles.Add(0);
        for (var i = 0; i < nibbles.Count; i += 2) data.Add((byte)(nibbles[i] << 4 | nibbles[i + 1]));
    }

    static void Put16(List<byte> d, int v) { d.Add((byte)(v >> 8)); d.Add((byte)v); }

    /// <summary>
    /// Writes <paramref name="idx"/> and the .sub beside it: one subpicture stream, each picture at its start time plus <paramref name="offset"/> seconds
    /// (the first video timestamp of the program stream it will join). <paramref name="rgb"/>: the 16 colours, for the .idx only (the disc takes its CLUT from the IFO).
    /// </summary>
    public static void WriteVobSub(string idx, IReadOnlyList<Picture> pictures, int frameWidth, int frameHeight, string lang, int[] rgb,
                                   byte[] colors, byte[] contrast, bool forced, double offset)
    {
        using var sub = new FileStream(Path.ChangeExtension(idx, ".sub"), FileMode.Create, FileAccess.Write);
        var text = new StringBuilder("# VobSub index file, v7 (do not modify this line!)\n");
        text.Append(CultureInfo.InvariantCulture, $"size: {frameWidth}x{frameHeight}\n");
        text.Append("palette: ").Append(string.Join(", ", rgb.Select(c => c.ToString("x6", CultureInfo.InvariantCulture)))).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"id: {lang}, index: 0\n");
        foreach (var p in pictures)
        {
            var time = TimeSpan.FromSeconds(p.Start + offset);
            text.Append(CultureInfo.InvariantCulture, $"timestamp: {(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}:{time.Milliseconds:000}, filepos: {sub.Position:x9}\n");
            var unit = Unit(p.Pixels, p.Width, p.Height, colors, contrast, p.End - p.Start, forced);
            WritePacks(sub, unit, (long)Math.Round((p.Start + offset) * 90000));
        }
        File.WriteAllText(idx, text.ToString());
    }

    /// <summary>The unit in 2048-byte packs: pack header, private stream 1 (PTS on the first), substream 0x20, data; padding to the sector's end.</summary>
    static void WritePacks(Stream s, byte[] unit, long pts)
    {
        var at = 0;
        var pack = new byte[2048];
        while (at < unit.Length)
        {
            Array.Clear(pack);
            var scr = Math.Max(0, pts - 9000);
            pack[0] = 0; pack[1] = 0; pack[2] = 1; pack[3] = 0xBA;
            pack[4] = (byte)(0x44 | (scr >> 27 & 0x38) | (scr >> 28 & 3)); pack[5] = (byte)(scr >> 20);
            pack[6] = (byte)((scr >> 12 & 0xF8) | 4 | (scr >> 13 & 3)); pack[7] = (byte)(scr >> 5);
            pack[8] = (byte)((scr << 3 & 0xF8) | 4); pack[9] = 1;
            pack[10] = 0x01; pack[11] = 0x89; pack[12] = 0xC3; pack[13] = 0xF8; // 10.08 Mb/s, no stuffing
            var p = 14;
            var withPts = at == 0;
            var header = withPts ? 5 : 0;
            var room = 2048 - p - 9 - header - 1;
            var n = Math.Min(room, unit.Length - at);
            var gap = room - n; // what is left of the sector: stuffing in the header when small, else a padding packet after
            var stuffing = gap is > 0 and < 6 ? gap : 0;
            var length = 3 + header + stuffing + 1 + n;
            pack[p] = 0; pack[p + 1] = 0; pack[p + 2] = 1; pack[p + 3] = 0xBD;
            pack[p + 4] = (byte)(length >> 8); pack[p + 5] = (byte)length;
            pack[p + 6] = 0x81; pack[p + 7] = (byte)(withPts ? 0x80 : 0); pack[p + 8] = (byte)(header + stuffing);
            p += 9;
            if (withPts)
            {
                pack[p] = (byte)(0x21 | (pts >> 29 & 0x0E)); pack[p + 1] = (byte)(pts >> 22);
                pack[p + 2] = (byte)(pts >> 14 | 1); pack[p + 3] = (byte)(pts >> 7); pack[p + 4] = (byte)(pts << 1 | 1);
                p += 5;
            }
            for (var k = 0; k < stuffing; k++) pack[p++] = 0xFF;
            pack[p++] = 0x20;
            Array.Copy(unit, at, pack, p, n);
            p += n; at += n;
            if (p < 2048)
            {
                var pad = 2048 - p - 6;
                pack[p] = 0; pack[p + 1] = 0; pack[p + 2] = 1; pack[p + 3] = 0xBE; pack[p + 4] = (byte)(pad >> 8); pack[p + 5] = (byte)pad;
                for (var k = p + 6; k < 2048; k++) pack[k] = 0xFF;
            }
            s.Write(pack);
        }
    }
}
