// Ported to C# from bdmvkit (module pgs.py): https://github.com/kajisho5/bdmvkit, commit 5789076 (v0.7.0).
// Copyright (c) kajisho5 (Shogo Kajiyama). GNU Lesser General Public License v3.0 or later (COPYING.LESSER upstream);
// redistributed here under the GNU GPL v3 or later, with the LGPL's additional permissions removed, as section 7 of the GPL v3 allows. See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;

namespace BurnSmith.Core.Hdmv;

/// <summary>Presentation Graphics building blocks that Interactive Graphics shares: colour conversion, run-length coding, segment header.</summary>
public static class Pgs
{
    /// <summary>A segment body is at most 65535 bytes (16-bit length).</summary>
    public const int MaxSegment = 65535;

    /// <summary>BT.709 studio-range YCbCr, what HD Blu-ray players expect. Same operations in the same order as bdmvkit (byte-identical palettes).</summary>
    public static (int Y, int Cb, int Cr) YCbCr(int r8, int g8, int b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        double cb = (b - y) / 1.8556, cr = (r - y) / 1.5748;
        // Math.Round rounds half to even, like Python's round()
        return ((int)Math.Round(16 + 219 * y), (int)Math.Round(128 + 224 * cb), (int)Math.Round(128 + 224 * cr));
    }

    /// <summary>PGS/IG run-length coding of an index picture, row by row (colour 0 = transparent).</summary>
    public static byte[] Rle(byte[] idx, int w, int h)
    {
        var o = new List<byte>(idx.Length / 4 + 16);
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var x = 0;
            while (x < w)
            {
                var c = idx[row + x];
                var n = 1;
                while (x + n < w && idx[row + x + n] == c) n++;
                x += n;
                while (n > 0)
                {
                    var k = Math.Min(n, 16383);
                    if (c == 0)
                    {
                        if (k < 64) { o.Add(0); o.Add((byte)k); }
                        else { o.Add(0); o.Add((byte)(0x40 | k >> 8)); o.Add((byte)(k & 0xFF)); }
                    }
                    else if (k < 3) for (var i = 0; i < k; i++) o.Add(c);
                    else if (k < 64) { o.Add(0); o.Add((byte)(0x80 | k)); o.Add(c); }
                    else { o.Add(0); o.Add((byte)(0xC0 | k >> 8)); o.Add((byte)(k & 0xFF)); o.Add(c); }
                    n -= k;
                }
            }
            o.Add(0); o.Add(0);
        }
        return [.. o];
    }

    /// <summary>Inverse of <see cref="Rle"/>; throws when the data does not decode to w x h pixels.</summary>
    public static byte[] Unrle(ReadOnlySpan<byte> data, int w, int h)
    {
        var pixels = (long)w * h; // both sizes come from the file: a damaged one must not overflow or reserve gigabytes
        var o = new List<byte>((int)Math.Min(pixels, data.Length));
        int i = 0, row = 0;
        while (i < data.Length && row < h)
        {
            var b = data[i++];
            if (b != 0) { o.Add(b); continue; }
            if (i >= data.Length) throw new InvalidDataException("RLE data cut short");
            var f = data[i++];
            if (f == 0) { row++; continue; }
            if (i + ((f & 0x40) != 0 ? 1 : 0) + ((f & 0x80) != 0 ? 1 : 0) > data.Length) throw new InvalidDataException("RLE data cut short");
            var n = f & 0x3F;
            if ((f & 0x40) != 0) n = n << 8 | data[i++];
            byte c = 0;
            if ((f & 0x80) != 0) c = data[i++];
            if (o.Count + n > pixels) throw new InvalidDataException($"RLE gives more than the {pixels} pixels expected");
            for (var k = 0; k < n; k++) o.Add(c);
        }
        if (o.Count != pixels) throw new InvalidDataException($"RLE gives {o.Count} pixels, expected {pixels}");
        return [.. o];
    }

    /// <summary>"PG" + PTS + DTS + type + length + body, the .sup segment layout (ffmpeg reads it as a subtitle stream).</summary>
    public static byte[] Segment(byte kind, uint pts, uint dts, ReadOnlySpan<byte> body)
    {
        var s = new byte[13 + body.Length];
        s[0] = (byte)'P'; s[1] = (byte)'G';
        BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(2), pts);
        BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(6), dts);
        s[10] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(11), (ushort)body.Length);
        body.CopyTo(s.AsSpan(13));
        return s;
    }
}
