using System.Buffers.Binary;

namespace BurnSmith.Core.Disc;

/// <summary>
/// Presentation Graphics building blocks: the colour conversion of HD Blu-ray, the run-length coding of a
/// picture's colour indexes, and the segment framing of a .sup stream.
/// </summary>
public static class PgsStream
{
    /// <summary>A segment body carries a 16-bit length, so it cannot be longer than this.</summary>
    public const int MaxBody = 65535;

    /// <summary>
    /// An 8-bit RGB colour as BT.709 studio-range YCbCr, the range HD players expect (Y 16-235, chroma 128 +/- 112).
    /// </summary>
    public static (int Y, int Cb, int Cr) ToYCbCr(int red, int green, int blue)
    {
        double r = red / 255.0, g = green / 255.0, b = blue / 255.0;
        var luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var cb = (b - luma) / 1.8556;
        var cr = (r - luma) / 1.5748;
        return (Round(16 + 219 * luma), Round(128 + 224 * cb), Round(128 + 224 * cr));
    }

    static int Round(double value) => (int)Math.Round(value, MidpointRounding.ToEven);

    /// <summary>
    /// Run-length coding of a picture of colour indexes, row by row: colour 0 is transparent and coded with
    /// the 0x00 / 0x40+ flags, a colour is coded alone when it appears once or twice and with 0x80 / 0xC0
    /// above that. Each row ends with the 0x00 0x00 end-of-line pair.
    /// </summary>
    public static byte[] Pack(ReadOnlySpan<byte> indexes, int width, int height)
    {
        var coded = new List<byte>(indexes.Length / 4 + height * 2);
        for (var row = 0; row < height; row++)
        {
            var start = row * width;
            var x = 0;
            while (x < width)
            {
                var colour = indexes[start + x];
                var run = 1;
                while (x + run < width && indexes[start + x + run] == colour) run++;
                x += run;
                while (run > 0)
                {
                    var piece = Math.Min(run, 16383);
                    if (colour == 0) AppendTransparent(coded, piece);
                    else AppendColour(coded, piece, colour);
                    run -= piece;
                }
            }
            coded.Add(0);
            coded.Add(0);
        }
        return [.. coded];
    }

    /// <summary>Skip <paramref name="length"/> transparent pixels.</summary>
    static void AppendTransparent(List<byte> coded, int length)
    {
        coded.Add(0);
        if (length < 64) coded.Add((byte)length);
        else { coded.Add((byte)(0x40 | length >> 8)); coded.Add((byte)(length & 0xFF)); }
    }

    /// <summary>Paint <paramref name="length"/> pixels of <paramref name="colour"/>.</summary>
    static void AppendColour(List<byte> coded, int length, byte colour)
    {
        if (length < 3) { for (var i = 0; i < length; i++) coded.Add(colour); return; }
        coded.Add(0);
        if (length < 64) { coded.Add((byte)(0x80 | length)); coded.Add(colour); }
        else { coded.Add((byte)(0xC0 | length >> 8)); coded.Add((byte)(length & 0xFF)); coded.Add(colour); }
    }

    /// <summary>
    /// The picture <see cref="Pack"/> coded as a flat list of <paramref name="width"/> x <paramref name="height"/> indexes.
    /// Throws when the data does not decode to exactly that many pixels: both sizes come from the file.
    /// </summary>
    public static byte[] Unpack(ReadOnlySpan<byte> coded, int width, int height)
    {
        var pixels = (long)width * height;
        var picture = new List<byte>((int)Math.Min(pixels, coded.Length));
        int at = 0, row = 0;
        while (at < coded.Length && row < height)
        {
            var first = coded[at++];
            if (first != 0) { picture.Add(first); continue; }
            if (at >= coded.Length) throw new InvalidDataException("RLE data cut short");
            var flags = coded[at++];
            if (flags == 0) { row++; continue; }

            var wide = (flags & 0x40) != 0;
            var painted = (flags & 0x80) != 0;
            if (at + (wide ? 1 : 0) + (painted ? 1 : 0) > coded.Length) throw new InvalidDataException("RLE data cut short");

            var length = flags & 0x3F;
            if (wide) length = length << 8 | coded[at++];
            byte colour = 0;
            if (painted) colour = coded[at++];

            if (picture.Count + length > pixels) throw new InvalidDataException($"RLE gives more than the {pixels} pixels expected");
            for (var i = 0; i < length; i++) picture.Add(colour);
        }
        if (picture.Count != pixels) throw new InvalidDataException($"RLE gives {picture.Count} pixels, expected {pixels}");
        return [.. picture];
    }

    /// <summary>A segment: "PG", presentation and decoding time stamps, kind, 16-bit body length, body.</summary>
    public static byte[] Segment(byte kind, uint pts, uint dts, ReadOnlySpan<byte> body)
    {
        var segment = new byte[13 + body.Length];
        segment[0] = (byte)'P';
        segment[1] = (byte)'G';
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(2), pts);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(6), dts);
        segment[10] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(11), (ushort)body.Length);
        body.CopyTo(segment.AsSpan(13));
        return segment;
    }

    /// <summary>
    /// Adjusts a PGS (.sup) picture subtitle stream: shifts vertical position upward (<paramref name="liftPercent"/> 0..100),
    /// scales bitmap size (<paramref name="scalePercent"/> 30..300), and optionally calibrates palette luma for UHD HDR (<paramref name="hdrNits"/>).
    /// </summary>
    public static byte[] Transform(ReadOnlySpan<byte> sup, int liftPercent, int scalePercent = 100, int hdrNits = 0)
    {
        liftPercent = Math.Clamp(liftPercent, 0, 100);
        scalePercent = Math.Clamp(scalePercent, 30, 300);
        var scaleHdr = hdrNits is > 0 and < 400;
        if (liftPercent == 0 && scalePercent == 100 && !scaleHdr) return sup.ToArray();

        var output = new MemoryStream(sup.Length);
        var factor = scalePercent / 100.0;
        var lumaScale = scaleHdr ? Math.Clamp(hdrNits, 80, 400) / 400.0 : 1.0;
        int videoW = 1920, videoH = 1080;
        var at = 0;
        while (at + 13 <= sup.Length)
        {
            if (sup[at] != (byte)'P' || sup[at + 1] != (byte)'G') throw new InvalidDataException("not a PGS stream");
            var pts = BinaryPrimitives.ReadUInt32BigEndian(sup.Slice(at + 2, 4));
            var dts = BinaryPrimitives.ReadUInt32BigEndian(sup.Slice(at + 6, 4));
            var kind = sup[at + 10];
            var len = BinaryPrimitives.ReadUInt16BigEndian(sup.Slice(at + 11, 2));
            if (at + 13 + len > sup.Length) throw new InvalidDataException("truncated PGS segment");
            var body = sup.Slice(at + 13, len).ToArray();
            at += 13 + len;

            var liftPx = liftPercent * videoH / 200; // 100 % lifts by half the frame height
            if (kind == 0x16 && body.Length >= 11) // PCS
            {
                videoW = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0, 2));
                videoH = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2, 2));
                liftPx = liftPercent * videoH / 200;
                var objCount = body[10];
                var off = 11;
                for (var i = 0; i < objCount && off + 8 <= body.Length; i++)
                {
                    var cropped = (body[off + 3] & 0x40) != 0;
                    int x = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(off + 4, 2));
                    int y = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(off + 6, 2));
                    if (scalePercent != 100)
                        x = Math.Clamp((int)Math.Round(videoW / 2.0 + (x - videoW / 2.0) * factor), 0, Math.Max(0, videoW - 2));
                    y = Math.Clamp(y - liftPx, 0, Math.Max(0, videoH - 2));
                    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(off + 4, 2), (ushort)x);
                    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(off + 6, 2), (ushort)y);
                    off += cropped ? 16 : 8;
                }
            }
            else if (kind == 0x17 && body.Length >= 1) // WDS
            {
                var count = body[0];
                var off = 1;
                for (var i = 0; i < count && off + 9 <= body.Length; i++)
                {
                    int x = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(off + 1, 2));
                    int y = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(off + 3, 2));
                    int w = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(off + 5, 2));
                    int h = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(off + 7, 2));
                    if (scalePercent != 100)
                    {
                        w = Math.Clamp((int)Math.Round(w * factor), 2, videoW);
                        h = Math.Clamp((int)Math.Round(h * factor), 2, videoH);
                        x = Math.Clamp((int)Math.Round(videoW / 2.0 + (x - videoW / 2.0) * factor), 0, Math.Max(0, videoW - w));
                    }
                    y = Math.Clamp(y - liftPx, 0, Math.Max(0, videoH - h));
                    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(off + 1, 2), (ushort)x);
                    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(off + 3, 2), (ushort)y);
                    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(off + 5, 2), (ushort)w);
                    BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(off + 7, 2), (ushort)h);
                    off += 9;
                }
            }
            else if (kind == 0x14 && scaleHdr && body.Length >= 2) // PDS
            {
                for (var off = 2; off + 5 <= body.Length; off += 5)
                {
                    var y = body[off + 1];
                    body[off + 1] = (byte)Math.Clamp(16 + (int)Math.Round((y - 16) * lumaScale), 16, 235);
                }
            }
            else if (kind == 0x15 && scalePercent != 100 && body.Length >= 11 && body[3] == 0xC0) // single-segment ODS
            {
                var w = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(7, 2));
                var h = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(9, 2));
                if (w > 0 && h > 0)
                {
                    var pixels = Unpack(body.AsSpan(11), w, h);
                    var nw = Math.Clamp((int)Math.Round(w * factor), 2, videoW);
                    var nh = Math.Clamp((int)Math.Round(h * factor), 2, videoH);
                    var scaled = new byte[nw * nh];
                    for (var ry = 0; ry < nh; ry++)
                    {
                        var sy = Math.Min(h - 1, ry * h / nh);
                        for (var rx = 0; rx < nw; rx++)
                            scaled[ry * nw + rx] = pixels[sy * w + Math.Min(w - 1, rx * w / nw)];
                    }
                    var packed = Pack(scaled, nw, nh);
                    if (11 + packed.Length <= MaxBody)
                    {
                        var rebuilt = new byte[11 + packed.Length];
                        Array.Copy(body, 0, rebuilt, 0, 4);
                        var dataLen = packed.Length + 4;
                        rebuilt[4] = (byte)(dataLen >> 16); rebuilt[5] = (byte)(dataLen >> 8); rebuilt[6] = (byte)dataLen;
                        BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(7, 2), (ushort)nw);
                        BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(9, 2), (ushort)nh);
                        packed.CopyTo(rebuilt, 11);
                        body = rebuilt;
                    }
                }
            }
            output.Write(Segment(kind, pts, dts, body));
        }
        return output.ToArray();
    }
}
