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
}
