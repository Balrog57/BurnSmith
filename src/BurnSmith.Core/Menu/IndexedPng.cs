using System.IO.Compression;

namespace BurnSmith.Core.Menu;

/// <summary>
/// DVD subpictures accept at most 4 colours, so highlight images are written as 2-bit indexed PNGs
/// with a per-colour alpha. WPF cannot produce those, hence this small writer.
/// </summary>
public static class IndexedPng
{
    public readonly record struct Rgba(byte R, byte G, byte B, byte A);

    /// <summary>A 2-bit image: <see cref="Frame"/> draws rectangle outlines with palette index 1..3 (0 = transparent).</summary>
    public sealed class Canvas(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Pixels { get; } = new byte[width * height];

        public void Frame(int x0, int y0, int x1, int y1, int thickness, byte index)
        {
            for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
                for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++)
                    if (x < x0 + thickness || x > x1 - thickness || y < y0 + thickness || y > y1 - thickness)
                        Pixels[y * Width + x] = index;
        }

        /// <summary>
        /// Pastes a raster (BGRA, premultiplied, as WPF hands it over) at <paramref name="x0"/>,<paramref name="y0"/>, keeping only its shape:
        /// a pixel that is at least half opaque takes <paramref name="index"/>, the others stay transparent. That is how a template's button
        /// artwork becomes a DVD subpicture, which has four colours at most.
        /// </summary>
        public void Stamp(byte[] bgra, int w, int h, int x0, int y0, byte index)
        {
            for (var y = 0; y < h; y++)
            {
                var ty = y0 + y;
                if (ty < 0 || ty >= Height) continue;
                for (var x = 0; x < w; x++)
                {
                    var tx = x0 + x;
                    if (tx < 0 || tx >= Width) continue;
                    if (bgra[(y * w + x) * 4 + 3] >= 128) Pixels[ty * Width + tx] = index;
                }
            }
        }
    }

    /// <summary>
    /// The one tint a subpicture can spend on a piece of artwork: the average of its visible pixels, un-premultiplied (WPF's Pbgra32 stores
    /// colour multiplied by alpha, so the raw bytes of a half-transparent pixel are not its colour). White when nothing is visible.
    /// </summary>
    public static Rgba Average(byte[] bgra, int width, int height)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = 0; i < width * height; i++)
        {
            int a = bgra[i * 4 + 3];
            if (a < 128) continue;
            r += bgra[i * 4] * 255L / a;
            g += bgra[i * 4 + 1] * 255L / a;
            b += bgra[i * 4 + 2] * 255L / a;
            n++;
        }
        if (n == 0) return new Rgba(255, 255, 255, 255);
        return new Rgba((byte)Math.Min(255, r / n), (byte)Math.Min(255, g / n), (byte)Math.Min(255, b / n), 255);
    }

    public static byte[] Encode(Canvas c, Rgba[] palette)
    {
        if (palette.Length != 4) throw new ArgumentException("exactly 4 palette entries");
        var stride = (c.Width + 3) / 4; // 4 pixels per byte
        var raw = new byte[(stride + 1) * c.Height]; // + 1 filter byte per row (0 = none)
        for (var y = 0; y < c.Height; y++)
            for (var x = 0; x < c.Width; x++)
                raw[y * (stride + 1) + 1 + x / 4] |= (byte)(c.Pixels[y * c.Width + x] << (6 - 2 * (x % 4)));

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new BinaryWriterBE();
        ihdr.U32(c.Width); ihdr.U32(c.Height); ihdr.U8(2); ihdr.U8(3); ihdr.U8(0); ihdr.U8(0); ihdr.U8(0); // depth 2, indexed
        Chunk(ms, "IHDR", ihdr.ToArray());
        Chunk(ms, "PLTE", palette.SelectMany(p => new[] { p.R, p.G, p.B }).ToArray());
        Chunk(ms, "tRNS", palette.Select(p => p.A).ToArray());
        using (var z = new MemoryStream())
        {
            using (var zl = new ZLibStream(z, CompressionLevel.Optimal, true)) zl.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var w = new BinaryWriterBE();
        w.U32(data.Length);
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(w.ToArray());
        s.Write(body);
        var crc = new BinaryWriterBE();
        crc.U32(Crc32(body));
        s.Write(crc.ToArray());
    }

    static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
