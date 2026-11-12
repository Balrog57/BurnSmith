using System.Buffers.Binary;

namespace BurnSmith.Core.Disc;

/// <summary>
/// One button of a native (HDMV) menu page. Its pictures are straight ARGB pixels, <see cref="W"/> x <see cref="H"/>, placed at <see cref="X"/>, <see cref="Y"/>:
/// <see cref="Selected"/> shows under the remote (and while the button is pressed), <see cref="Normal"/> the rest of the time; null = nothing drawn,
/// the page's video shows through. The four neighbours are button numbers (the button's own = the key does nothing). <see cref="Auto"/>: its commands
/// run as soon as it is selected. <see cref="Enabled"/> false: hidden until a command enables it.
/// </summary>
public sealed record IgButton(int Id, int X, int Y, int W, int H, uint[]? Selected, uint[]? Normal, int Up, int Down, int Left, int Right,
                              IReadOnlyList<byte[]> Commands, bool Auto = false, bool Enabled = true);

/// <summary>
/// The Interactive Graphics stream of a native menu page, as the file tsMuxeR muxes ("S_HDMV/IGS"): one display set = the composition (buttons, their
/// neighbours and commands), one palette, the pictures, an end mark. Same segments as subtitles (<see cref="PgsStream"/>) under the letters "IG".
/// The page is always on screen and loaded whole before it shows (stream model 1), so it carries no time-out.
/// </summary>
public static class IgStream
{
    /// <summary>Colours of a page: entry 0 is transparent and entry 255 is not a colour a disc may define.</summary>
    public const int MaxColours = 254;

    const uint Pts = 90000; // one second into the clip, in 90 kHz ticks: the muxer moves it to the clip's own clock

    /// <summary>The frame-rate code of a composition: 1 = 23.976, 2 = 24, 3 = 25, 4 = 29.97, 6 = 50, 7 = 59.94.</summary>
    public static int RateCode(string tsMuxerFps) => tsMuxerFps switch { "24" => 2, "25" => 3, "29.97" => 4, "50" => 6, "59.94" => 7, _ => 1 };

    /// <summary>The whole stream of one page <paramref name="width"/> x <paramref name="height"/>; <paramref name="selected"/> is the button the page opens on.</summary>
    public static byte[] Write(int width, int height, int rateCode, IReadOnlyList<IgButton> buttons, int selected)
    {
        if (buttons.Count is 0 or > 255) throw new ArgumentException($"a menu page takes 1 to 255 buttons, not {buttons.Count}");
        var pictures = buttons.SelectMany(b => new[] { b.Normal, b.Selected }).Where(p => p != null).Select(p => p!).Distinct().ToList();
        var (palette, index) = Quantize(pictures, MaxColours);
        int Object(uint[]? picture) => picture == null ? 0xFFFF : pictures.IndexOf(picture);

        var page = new BinaryWriterBE();
        page.U8(0x80);                                // stream model 1 (loaded before it shows), always on screen
        page.U8(0); page.U16(0);                      // the user never times out
        page.U8(1);                                   // one page
        page.U8(0); page.U8(0);                       // page 0, version 0
        page.Zero(8);                                 // every remote key allowed
        page.Zero(4);                                 // no effect when the page comes or goes
        page.U8(0);                                   // no animation
        page.U16(selected);
        page.U16(0xFFFF);                             // no button pressed by itself
        page.U8(0);                                   // palette 0
        page.U8(buttons.Count);
        foreach (var b in buttons)                    // one group per button: none of them replaces another
        {
            page.U16(b.Enabled ? b.Id : 0xFFFF);
            page.U8(1);
            page.U16(b.Id);
            page.U16(0xFFFF);                         // no number key
            page.U8(b.Auto ? 0x80 : 0);
            page.U16(b.X); page.U16(b.Y);
            page.U16(b.Up); page.U16(b.Down); page.U16(b.Left); page.U16(b.Right);
            page.U16(Object(b.Normal)); page.U16(Object(b.Normal)); page.U8(0);
            page.U8(0xFF); page.U16(Object(b.Selected)); page.U16(Object(b.Selected)); page.U8(0); // 0xFF: no sound
            page.U8(0xFF); page.U16(Object(b.Selected)); page.U16(Object(b.Selected));
            page.U16(b.Commands.Count);
            foreach (var c in b.Commands) page.Bytes(c);
        }

        var composition = new BinaryWriterBE();
        composition.U16(width); composition.U16(height); composition.U8(rateCode << 4);
        composition.U16(0); composition.U8(0x80);     // composition 0, start of an epoch
        composition.U8(0xC0);                         // in one segment
        composition.U8(page.Length >> 16); composition.U16(page.Length);
        composition.Bytes(page.ToArray());
        if (composition.Length > PgsStream.MaxBody) throw new InvalidDataException("menu page: too many buttons and commands for one composition");

        var stream = new MemoryStream();
        void Segment(byte kind, ReadOnlySpan<byte> body)
        {
            var s = PgsStream.Segment(kind, Pts, Pts, body);
            s[0] = (byte)'I'; s[1] = (byte)'G';
            stream.Write(s);
        }
        Segment(0x18, composition.ToArray());

        var colours = new byte[7 + palette.Length * 5]; // palette 0, version 0, then entry number, Y, Cr, Cb, opacity
        (colours[3], colours[4], colours[5]) = (16, 128, 128); // entry 0, what every clear pixel uses: said, not left to the player
        for (var i = 0; i < palette.Length; i++)
        {
            var c = palette[i];
            var (y, cb, cr) = PgsStream.ToYCbCr((int)(c >> 16 & 255), (int)(c >> 8 & 255), (int)(c & 255));
            colours[7 + i * 5] = (byte)(i + 1);
            colours[8 + i * 5] = (byte)y; colours[9 + i * 5] = (byte)cr; colours[10 + i * 5] = (byte)cb; colours[11 + i * 5] = (byte)(c >> 24);
        }
        Segment(0x14, colours);

        for (var id = 0; id < pictures.Count; id++)
        {
            var b = buttons.First(x => x.Normal == pictures[id] || x.Selected == pictures[id]);
            var coded = PgsStream.Pack(pictures[id].Select(c => c >> 24 == 0 ? (byte)0 : index[c]).ToArray(), b.W, b.H);
            var data = new byte[7 + coded.Length];    // length of what follows it, width, height, the coded picture
            data[0] = (byte)(coded.Length + 4 >> 16); data[1] = (byte)(coded.Length + 4 >> 8); data[2] = (byte)(coded.Length + 4);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3), (ushort)b.W);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(5), (ushort)b.H);
            coded.CopyTo(data, 7);
            const int room = PgsStream.MaxBody - 4;   // a big picture goes in several segments, the first and the last flagged
            for (var at = 0; at < data.Length; at += room)
            {
                var part = data.AsSpan(at, Math.Min(room, data.Length - at));
                var body = new byte[4 + part.Length];
                BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)id);
                body[3] = (byte)((at == 0 ? 0x80 : 0) | (at + room >= data.Length ? 0x40 : 0));
                part.CopyTo(body.AsSpan(4));
                Segment(0x15, body);
            }
        }
        Segment(0x80, []);
        return stream.ToArray();
    }

    /// <summary>
    /// At most <paramref name="max"/> colours for these pictures (straight ARGB; fully transparent pixels are not counted, they are entry 0), and for each colour
    /// met its entry number, from 1. Median cut: the group of colours with the widest channel is split in two at its middle pixel until there are enough groups;
    /// each group becomes its average.
    /// </summary>
    public static (uint[] Palette, Dictionary<uint, byte> Index) Quantize(IEnumerable<uint[]> pictures, int max)
    {
        var counts = new Dictionary<uint, int>();
        foreach (var picture in pictures)
            foreach (var c in picture)
                if (c >> 24 != 0) counts[c] = counts.GetValueOrDefault(c) + 1;

        static (int Shift, int Range) Widest(List<uint> box)
        {
            var best = (Shift: 0, Range: -1);
            foreach (var shift in new[] { 24, 16, 8, 0 })
            {
                int low = 255, high = 0;
                foreach (var c in box) { var v = (int)(c >> shift & 255); if (v < low) low = v; if (v > high) high = v; }
                if (high - low > best.Range) best = (shift, high - low);
            }
            return best;
        }

        var boxes = new List<(List<uint> Colours, int Shift, int Range)>();
        void Add(List<uint> colours) { var (shift, range) = Widest(colours); boxes.Add((colours, shift, range)); }
        if (counts.Count > 0) Add([.. counts.Keys]);
        while (boxes.Count < max)
        {
            var widest = -1;
            for (var i = 0; i < boxes.Count; i++)
                if (boxes[i].Range > 0 && (widest < 0 || boxes[i].Range > boxes[widest].Range)) widest = i;
            if (widest < 0) break;                    // every group is a single colour
            var (colours, shift, _) = boxes[widest];
            colours.Sort((a, b) => (a >> shift & 255).CompareTo(b >> shift & 255));
            long half = colours.Sum(c => (long)counts[c]) / 2, seen = 0;
            var cut = 0;
            while (cut < colours.Count - 1 && (seen += counts[colours[cut]]) <= half) cut++;
            cut = Math.Clamp(cut, 1, colours.Count - 1);
            // never between two pixels of the same value on that channel: the two groups would overlap
            var value = colours[cut] >> shift & 255;
            if ((colours[cut - 1] >> shift & 255) == value)
            {
                var first = colours.FindIndex(c => (c >> shift & 255) == value);
                cut = first > 0 ? first : colours.FindIndex(c => (c >> shift & 255) > value);
            }
            boxes.RemoveAt(widest);
            Add(colours.GetRange(0, cut));
            Add(colours.GetRange(cut, colours.Count - cut));
        }

        var palette = new uint[boxes.Count];
        var index = new Dictionary<uint, byte>(counts.Count);
        for (var i = 0; i < boxes.Count; i++)
        {
            long a = 0, r = 0, g = 0, b = 0, n = 0;
            foreach (var c in boxes[i].Colours)
            {
                var k = counts[c];
                a += (c >> 24) * k; r += (c >> 16 & 255) * k; g += (c >> 8 & 255) * k; b += (c & 255) * k; n += k;
                index[c] = (byte)(i + 1);
            }
            palette[i] = (uint)((a + n / 2) / n) << 24 | (uint)((r + n / 2) / n) << 16 | (uint)((g + n / 2) / n) << 8 | (uint)((b + n / 2) / n);
        }
        return (palette, index);
    }
}
