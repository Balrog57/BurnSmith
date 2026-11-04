using System.Buffers.Binary;
using BurnSmith.Core.Hdmv;

namespace BurnSmith.Core;

/// <summary>
/// Image subtitles in the PGS layout a Blu-ray carries (a .sup file: "PG" + PTS + DTS + type + length + body for each segment). A picture is a
/// display set, from its composition segment (PCS) to the END segment. Read to cut a .sup like the video it goes with, and to redraw the
/// pictures for a DVD (<see cref="DvdSubtitles"/>).
/// </summary>
public static class PgsSubtitles
{
    const byte PaletteKind = 0x14, ObjectKind = 0x15, CompositionKind = 0x16, EndKind = 0x80;
    const double Clock = 90000; // PTS and DTS tick at 90 kHz
    const int HeaderSize = 13;

    /// <summary>The segments of a display set (offset of the "PG" header, kind, body length) and what its composition segment says.</summary>
    sealed class Set
    {
        public readonly List<(int At, byte Kind, int Size)> Segments = [];
        public uint Pts;
        public int Width, Height, Objects;
        /// <summary>0x80 epoch start and 0x40 acquisition point carry the whole picture; 0x00 (normal) leans on what an earlier set defined.</summary>
        public byte State;
        public bool Whole => (State & 0xC0) != 0;
    }

    /// <summary>One picture to show from <see cref="Start"/> to <see cref="End"/> (seconds): a bitmap at (<see cref="X"/>, <see cref="Y"/>) of a screen plane, brightness and opacity 0..255 per pixel.</summary>
    public sealed record Cue(double Start, double End, int X, int Y, int Width, int Height, int PlaneWidth, int PlaneHeight, byte[] Luma, byte[] Alpha);

    static int Be16(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt16BigEndian(b);

    static List<Set> Read(byte[] sup)
    {
        var sets = new List<Set>();
        Set? open = null;
        for (var p = 0; p + HeaderSize <= sup.Length;)
        {
            if (sup[p] != 'P' || sup[p + 1] != 'G') throw new InvalidDataException($"not a PGS (.sup) file: no segment header at byte {p}");
            var size = Be16(sup.AsSpan(p + 11));
            if (p + HeaderSize + size > sup.Length) break; // cut short at the end
            var kind = sup[p + 10];
            if (kind == CompositionKind && size >= 11)
            {
                var body = sup.AsSpan(p + HeaderSize, size);
                sets.Add(open = new Set { Pts = BinaryPrimitives.ReadUInt32BigEndian(sup.AsSpan(p + 2)), Width = Be16(body), Height = Be16(body[2..]), State = body[7], Objects = body[10] });
            }
            open?.Segments.Add((p, kind, size));
            if (kind == EndKind) open = null;
            p += HeaderSize + size;
        }
        return sets;
    }

    /// <summary>Width and height of the screen the pictures are placed on (1920x1080 on a Blu-ray); null when the file shows nothing.</summary>
    public static (int Width, int Height)? PlaneSize(byte[] sup) => Read(sup) is [{ } first, ..] ? (first.Width, first.Height) : null;

    /// <summary>
    /// The part of the stream that falls in <paramref name="length"/> seconds from <paramref name="start"/>, moved back by <paramref name="start"/>
    /// (empty when nothing shows). A picture on screen at the start stays when it is a whole one; the sets that lean on an earlier picture are
    /// dropped until a whole one comes.
    /// </summary>
    public static byte[] Cut(byte[] sup, double start, double length)
    {
        long from = (long)Math.Round(start * Clock), to = from + (long)Math.Round(length * Clock);
        var sets = Read(sup);
        var keep = new List<Set>();
        var whole = false; // a whole picture has been kept, so the sets that follow have something to lean on
        if (from > 0 && sets.LastOrDefault(s => s.Pts < from) is { Objects: > 0, Whole: true } onScreen) { keep.Add(onScreen); whole = true; }
        foreach (var set in sets.Where(s => s.Pts >= from && s.Pts < to))
        {
            whole |= set.Whole;
            if (whole) keep.Add(set);
        }
        var output = new MemoryStream();
        foreach (var (at, _, size) in keep.SelectMany(s => s.Segments))
        {
            var segment = sup.AsSpan(at, HeaderSize + size).ToArray();
            foreach (var field in new[] { 2, 6 }) // PTS and DTS
                BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(field), (uint)Math.Max(0, BinaryPrimitives.ReadUInt32BigEndian(segment.AsSpan(field)) - from));
            output.Write(segment);
        }
        return output.ToArray();
    }

    /// <summary>A picture still being received: its objects arrive in pieces.</summary>
    sealed class Piece(int width, int height)
    {
        public readonly int Width = width, Height = height;
        public readonly MemoryStream Data = new();
    }

    /// <summary>
    /// The pictures in order, each lasting until the next set clears or replaces it (a set that only changes a palette or a window keeps the picture).
    /// One that is never cleared lasts three seconds. A picture that cannot be decoded is left out and counted in <paramref name="skipped"/>.
    /// </summary>
    public static List<Cue> Cues(byte[] sup, out int skipped)
    {
        var palettes = new Dictionary<int, (byte Luma, byte Alpha)[]>();
        var pieces = new Dictionary<int, Piece>();
        var objects = new Dictionary<int, (int Width, int Height, byte[] Index)>();
        var cues = new List<Cue>();
        var open = false;
        var bad = 0;
        void Close(double at)
        {
            if (open) cues[^1] = cues[^1] with { End = Math.Max(at, cues[^1].Start + 0.1) };
            open = false;
        }
        foreach (var set in Read(sup))
        {
            var fresh = false;
            foreach (var (at, kind, size) in set.Segments)
            {
                var body = sup.AsSpan(at + HeaderSize, size);
                if (kind == PaletteKind && size >= 2)
                {
                    if (!palettes.TryGetValue(body[0], out var table)) palettes[body[0]] = table = new (byte, byte)[256];
                    for (var o = 2; o + 5 <= size; o += 5) table[body[o]] = (body[o + 1], body[o + 4]); // index, Y, Cr, Cb, opacity
                }
                else if (kind == ObjectKind && size >= 4)
                {
                    int id = Be16(body), sequence = body[3];
                    if ((sequence & 0x80) != 0 && size >= 11)
                    {
                        pieces[id] = new Piece(Be16(body[7..]), Be16(body[9..]));
                        pieces[id].Data.Write(body[11..]);
                    }
                    else if (pieces.TryGetValue(id, out var piece)) piece.Data.Write(body[4..]);
                    if ((sequence & 0x40) != 0 && pieces.Remove(id, out var done))
                    {
                        try { objects[id] = (done.Width, done.Height, Pgs.Unrle(done.Data.ToArray(), done.Width, done.Height)); fresh = true; }
                        catch (InvalidDataException) { objects.Remove(id); bad++; }
                    }
                }
            }
            var time = set.Pts / Clock;
            if (set.Objects == 0) { Close(time); continue; }
            if (!fresh) continue;
            Close(time);
            if (Render(sup, set, palettes, objects, time) is { } cue) { cues.Add(cue); open = true; }
            else bad++;
        }
        if (open) cues[^1] = cues[^1] with { End = cues[^1].Start + 3 };
        skipped = bad;
        return cues;
    }

    static Cue? Render(byte[] sup, Set set, Dictionary<int, (byte Luma, byte Alpha)[]> palettes, Dictionary<int, (int Width, int Height, byte[] Index)> objects, double time)
    {
        var (at, _, size) = set.Segments[0];
        var body = sup.AsSpan(at + HeaderSize, size);
        if (!palettes.TryGetValue(body[9], out var palette)) return null;
        var placed = new List<(int X, int Y, int Width, int Height, byte[] Index)>();
        for (int o = 11, left = body[10]; left > 0 && o + 8 <= size; left--)
        {
            if (objects.TryGetValue(Be16(body[o..]), out var obj)) placed.Add((Be16(body[(o + 4)..]), Be16(body[(o + 6)..]), obj.Width, obj.Height, obj.Index));
            o += (body[o + 3] & 0x40) != 0 ? 16 : 8; // a cropped object (0x40; 0x80 is "forced") has four more fields
        }
        if (placed.Count == 0) return null;
        int x0 = placed.Min(p => p.X), y0 = placed.Min(p => p.Y), x1 = placed.Max(p => p.X + p.Width), y1 = placed.Max(p => p.Y + p.Height);
        int w = x1 - x0, h = y1 - y0;
        var luma = new byte[w * h];
        var alpha = new byte[w * h];
        foreach (var (px, py, pw, ph, index) in placed)
            for (var y = 0; y < ph; y++)
                for (var x = 0; x < pw; x++)
                {
                    var (l, a) = palette[index[y * pw + x]];
                    if (a == 0) continue;
                    var at2 = (py - y0 + y) * w + px - x0 + x;
                    luma[at2] = (byte)Math.Clamp((l - 16) * 255 / 219, 0, 255); // studio range to full
                    alpha[at2] = a;
                }
        return new Cue(time, time, x0, y0, w, h, set.Width, set.Height, luma, alpha);
    }
}
