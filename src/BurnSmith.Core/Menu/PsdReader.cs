using System.Buffers.Binary;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BurnSmith.Core.Menu;

/// <summary>
/// Minimal zero-dependency Adobe Photoshop (.psd) reader (8BPS v1, 8-bit RGB/RGBA, Raw and PackBits RLE):
/// extracts visible named layers (e.g. "normal" and "selected" button states) and the merged composite image as PNGs.
/// </summary>
public static class PsdReader
{
    public sealed record Layer(string Name, int Width, int Height, byte[] Png);

    public static (byte[]? CompositePng, List<Layer> Layers) Read(string psdPath)
    {
        var data = File.ReadAllBytes(psdPath);
        return Parse(data);
    }

    public static (byte[]? CompositePng, List<Layer> Layers) Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 26 || data[0] != (byte)'8' || data[1] != (byte)'B' || data[2] != (byte)'P' || data[3] != (byte)'S')
            throw new InvalidDataException("Ce fichier n'est pas un PSD Photoshop valide (en-tête 8BPS absent).");
        var version = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        if (version != 1) throw new InvalidDataException($"Version PSD {version} non prise en charge (seul PSD standard v1 est supporté).");
        var channels = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(12, 2));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(14, 4));
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(18, 4));
        var depth = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(22, 2));
        var colorMode = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(24, 2));
        if (depth != 8 || colorMode != 3 || channels < 3)
            throw new InvalidDataException("Le PSD doit être en mode RVB 8 bits.");

        var pos = 26;
        pos += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4)); // Color Mode Data
        pos += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4)); // Image Resources
        var layerSectionLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
        var layerSectionStart = pos + 4;
        var layers = layerSectionLen > 0 ? ReadLayerSection(data.Slice(layerSectionStart, layerSectionLen)) : [];
        pos = layerSectionStart + layerSectionLen;

        byte[]? composite = null;
        if (pos + 2 <= data.Length)
            composite = ReadComposite(data[pos..], width, height, channels);
        return (composite, layers);
    }

    static List<Layer> ReadLayerSection(ReadOnlySpan<byte> section)
    {
        var result = new List<Layer>();
        if (section.Length < 6) return result;
        var layerInfoLen = (int)BinaryPrimitives.ReadUInt32BigEndian(section.Slice(0, 4));
        if (layerInfoLen <= 2 || 4 + layerInfoLen > section.Length) return result;
        var info = section.Slice(4, layerInfoLen);
        var rawCount = BinaryPrimitives.ReadInt16BigEndian(info.Slice(0, 2));
        var count = Math.Abs(rawCount);
        var pos = 2;

        var records = new List<(string Name, int W, int H, bool Visible, byte Opacity, List<(short Id, int Len)> Channels)>(count);
        for (var i = 0; i < count && pos + 18 <= info.Length; i++)
        {
            var top = BinaryPrimitives.ReadInt32BigEndian(info.Slice(pos, 4));
            var left = BinaryPrimitives.ReadInt32BigEndian(info.Slice(pos + 4, 4));
            var bottom = BinaryPrimitives.ReadInt32BigEndian(info.Slice(pos + 8, 4));
            var right = BinaryPrimitives.ReadInt32BigEndian(info.Slice(pos + 12, 4));
            var chCount = BinaryPrimitives.ReadUInt16BigEndian(info.Slice(pos + 16, 2));
            pos += 18;
            var chs = new List<(short Id, int Len)>(chCount);
            for (var c = 0; c < chCount && pos + 6 <= info.Length; c++)
            {
                chs.Add((BinaryPrimitives.ReadInt16BigEndian(info.Slice(pos, 2)), (int)BinaryPrimitives.ReadUInt32BigEndian(info.Slice(pos + 2, 4))));
                pos += 6;
            }
            if (pos + 16 > info.Length) break;
            // blend signature (4) + key (4) + opacity (1) + clipping (1) + flags (1) + filler (1) + extra len (4)
            var opacity = info[pos + 8];
            var flags = info[pos + 10];
            var visible = (flags & 0x02) == 0;
            var extraLen = (int)BinaryPrimitives.ReadUInt32BigEndian(info.Slice(pos + 12, 4));
            pos += 16;
            var extraEnd = Math.Min(info.Length, pos + extraLen);
            var name = $"Calque {i + 1}";
            var epos = pos;
            if (epos + 4 <= extraEnd) epos += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(info.Slice(epos, 4)); // mask
            if (epos + 4 <= extraEnd) epos += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(info.Slice(epos, 4)); // blending ranges
            if (epos < extraEnd)
            {
                var nameLen = info[epos];
                if (epos + 1 + nameLen <= extraEnd && nameLen > 0)
                    name = Encoding.Latin1.GetString(info.Slice(epos + 1, nameLen));
            }
            pos = extraEnd;
            records.Add((name, Math.Max(0, right - left), Math.Max(0, bottom - top), visible, opacity, chs));
        }

        foreach (var rec in records)
        {
            var planes = new Dictionary<short, byte[]>();
            foreach (var (id, len) in rec.Channels)
            {
                if (len < 2 || pos + len > info.Length) { pos += Math.Max(0, len); continue; }
                if (rec.W > 0 && rec.H > 0 && rec.Visible && id is -1 or 0 or 1 or 2)
                {
                    var comp = BinaryPrimitives.ReadUInt16BigEndian(info.Slice(pos, 2));
                    var plane = DecodePlane(info.Slice(pos + 2, len - 2), comp, rec.W, rec.H);
                    if (plane != null) planes[id] = plane;
                }
                pos += len;
            }
            if (!rec.Visible || rec.W == 0 || rec.H == 0 || !planes.ContainsKey(0) || !planes.ContainsKey(1) || !planes.ContainsKey(2))
                continue;
            var bgra = new byte[rec.W * rec.H * 4];
            var rP = planes[0]; var gP = planes[1]; var bP = planes[2];
            planes.TryGetValue(-1, out var aP);
            for (var p = 0; p < rec.W * rec.H; p++)
            {
                bgra[p * 4] = bP[p];
                bgra[p * 4 + 1] = gP[p];
                bgra[p * 4 + 2] = rP[p];
                bgra[p * 4 + 3] = (byte)((aP != null ? aP[p] : 255) * rec.Opacity / 255);
            }
            result.Add(new Layer(rec.Name, rec.W, rec.H, EncodePng(bgra, rec.W, rec.H)));
        }
        return result;
    }

    static byte[]? ReadComposite(ReadOnlySpan<byte> imageData, int width, int height, int channels)
    {
        if (width <= 0 || height <= 0 || imageData.Length < 2) return null;
        var comp = BinaryPrimitives.ReadUInt16BigEndian(imageData.Slice(0, 2));
        var payload = imageData[2..];
        var planes = new byte[channels][];
        var planeSize = width * height;
        if (comp == 0)
        {
            if (payload.Length < planeSize * channels) return null;
            for (var c = 0; c < channels; c++) planes[c] = payload.Slice(c * planeSize, planeSize).ToArray();
        }
        else if (comp == 1)
        {
            var tableBytes = channels * height * 2;
            if (payload.Length < tableBytes) return null;
            var src = tableBytes;
            for (var c = 0; c < channels; c++)
            {
                var dst = new byte[planeSize];
                for (var y = 0; y < height; y++)
                {
                    var rowLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice((c * height + y) * 2, 2));
                    if (src + rowLen > payload.Length) return null;
                    UnpackBits(payload.Slice(src, rowLen), dst.AsSpan(y * width, width));
                    src += rowLen;
                }
                planes[c] = dst;
            }
        }
        else return null;

        var bgra = new byte[planeSize * 4];
        for (var p = 0; p < planeSize; p++)
        {
            bgra[p * 4] = planes[2][p];
            bgra[p * 4 + 1] = planes[1][p];
            bgra[p * 4 + 2] = planes[0][p];
            bgra[p * 4 + 3] = channels >= 4 ? planes[3][p] : (byte)255;
        }
        return EncodePng(bgra, width, height);
    }

    static byte[]? DecodePlane(ReadOnlySpan<byte> payload, ushort compression, int width, int height)
    {
        var size = width * height;
        if (compression == 0) return payload.Length >= size ? payload[..size].ToArray() : null;
        if (compression != 1) return null;
        var tableBytes = height * 2;
        if (payload.Length < tableBytes) return null;
        var dst = new byte[size];
        var src = tableBytes;
        for (var y = 0; y < height; y++)
        {
            var rowLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(y * 2, 2));
            if (src + rowLen > payload.Length) return null;
            UnpackBits(payload.Slice(src, rowLen), dst.AsSpan(y * width, width));
            src += rowLen;
        }
        return dst;
    }

    static void UnpackBits(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        while (s < src.Length && d < dst.Length)
        {
            var n = (sbyte)src[s++];
            if (n >= 0)
            {
                var count = Math.Min(n + 1, Math.Min(src.Length - s, dst.Length - d));
                src.Slice(s, count).CopyTo(dst.Slice(d, count));
                s += count; d += count;
            }
            else if (n != -128 && s < src.Length)
            {
                var count = Math.Min(1 - n, dst.Length - d);
                dst.Slice(d, count).Fill(src[s++]);
                d += count;
            }
        }
    }

    static byte[] EncodePng(byte[] bgra, int width, int height) => MenuRenderer.Sta(() =>
    {
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    });
}
