// Ported to C# from bdmvkit (module igs.py): https://github.com/kajisho5/bdmvkit, commit 5789076 (v0.7.0).
// Copyright (c) kajisho5 (Shogo Kajiyama). GNU Lesser General Public License v3.0 or later (COPYING.LESSER upstream);
// redistributed here under the GNU GPL v3 or later, with the LGPL's additional permissions removed, as section 7 of the GPL v3 allows. See THIRD-PARTY-NOTICES.md.
// Additions that bdmvkit does not have (marked "beyond bdmvkit"): the page UO mask, the optional PTS/DTS timeline of the segments.
using System.Buffers.Binary;

namespace BurnSmith.Core.Hdmv;

/// <summary>One button of a menu page. Pictures are straight-alpha RGBA, <see cref="W"/> x <see cref="H"/> x 4 bytes.</summary>
public sealed class IgButton
{
    public required string Id { get; init; }
    /// <summary>Top-left corner on the video frame (1920 x 1080, or 1280 x 720).</summary>
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int W { get; init; }
    public required int H { get; init; }
    /// <summary>Picture while the button has the focus.</summary>
    public required byte[] Selected { get; init; }
    /// <summary>Picture while it runs its commands (default: the selected one).</summary>
    public byte[]? Activated { get; init; }
    /// <summary>Picture while it has no focus (default: nothing drawn, the menu's own picture shows).</summary>
    public byte[]? Normal { get; init; }
    /// <summary>12-byte navigation commands run on activation, see <see cref="HdmvCommand"/>.</summary>
    public IReadOnlyList<byte[]> Commands { get; init; } = [];
    /// <summary>Neighbour for the arrow keys: "up" / "down" / "left" / "right" to a button id; missing ones are computed (<see cref="IgStream.AutoNav"/>).</summary>
    public IReadOnlyDictionary<string, string> Nav { get; init; } = new Dictionary<string, string>();
    /// <summary>Runs its commands as soon as it is selected.</summary>
    public bool Auto { get; init; }
}

/// <summary>How the segments of the .sup are dated (the .sup header carries a PTS and a DTS per segment).</summary>
public enum IgTimeline
{
    /// <summary>Every segment dated 0 / 0, as bdmvkit does; ffmpeg then adds its own delay and spaces the segments one tick apart. The PES packets carry a PTS only.</summary>
    Zero,
    /// <summary>
    /// Beyond bdmvkit: the timeline of the decoded reference disc (an authored menu): the ICS has a DTS earlier than its PTS by the time the
    /// graphics decoder needs to clear and write the plane, the palette is dated at that DTS, and each object follows the previous one by its
    /// own decode time. See <see cref="IgStream.Timeline"/>.
    /// </summary>
    Reference,
}

/// <summary>How the pictures of a page are reduced to the 255 colours of its palette.</summary>
public enum IgPalette
{
    /// <summary>bdmvkit's: fewer bits per channel until the colours fit in 255 (see <see cref="IgStream.Quantize"/>). Exact when the pictures have at most 255 colours.</summary>
    Bdmvkit,
    /// <summary>Beyond bdmvkit: median cut over (r, g, b, a) weighted by pixel count, which spends all 255 entries where the colours are (see <see cref="IgStream.QuantizeMedianCut"/>).</summary>
    MedianCut,
}

/// <summary>
/// Options of the IG writer. The defaults imitate the decoded reference disc (an authored menu: PTS + DTS on the segments, a page UO mask);
/// <see cref="BdmvkitCompatible"/> is what bdmvkit writes. libbluray plays both in full; neither is proven on a standalone player.
/// </summary>
public sealed record IgOptions
{
    /// <summary>Id of the button selected when the menu starts (default: the first).</summary>
    public string? Default { get; init; }
    /// <summary>A pop-up composition (ui_model 1): shown by the remote's Pop-up key over a title instead of a menu clip.</summary>
    public bool Popup { get; init; }
    /// <summary>The number keys select button n with n + this.</summary>
    public int ButtonOffset { get; init; }
    /// <summary>Keep the player's selection instead of selecting <see cref="Default"/> (a menu entered with a chosen button).</summary>
    public bool FollowSelection { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    /// <summary>
    /// Beyond bdmvkit. 64-bit UO_mask_table of the page: the remote keys the menu forbids (bit 0 = most significant; see <see cref="UoMask"/>).
    /// bdmvkit writes 0 (everything allowed); the reference disc has <see cref="UoMask.MenuReference"/>, the default.
    /// </summary>
    public ulong PageUoMask { get; init; } = UoMask.MenuReference;
    /// <summary>Beyond bdmvkit. Dating of the segments, see <see cref="IgTimeline"/>. Default: <see cref="IgTimeline.Reference"/>.</summary>
    public IgTimeline Timeline { get; init; } = IgTimeline.Reference;
    /// <summary>Beyond bdmvkit. Presentation time (90 kHz) of the ICS in the .sup when <see cref="Timeline"/> is <see cref="IgTimeline.Reference"/>.</summary>
    public uint PresentationTicks { get; init; } = 90000;

    /// <summary>
    /// Beyond bdmvkit. Palette reduction, see <see cref="IgPalette"/>. Default: median cut, which on BurnSmith's own rendered focus pictures
    /// keeps 255 entries at a mean error of 1.3 per channel where bdmvkit's quantizer keeps 110 at 11.9 (<c>HdmvCheck palette</c>).
    /// </summary>
    public IgPalette Palette { get; init; } = IgPalette.MedianCut;

    /// <summary>Exactly what bdmvkit writes: every segment dated 0 / 0 (ffmpeg spaces them), page UO mask 0, its quantizer.</summary>
    public static IgOptions BdmvkitCompatible => new() { Timeline = IgTimeline.Zero, PageUoMask = UoMask.None, Palette = IgPalette.Bdmvkit };
}

/// <summary>Dates (90 kHz) of the segments of one display set, see <see cref="IgStream.Timeline"/>.</summary>
public sealed record IgTimes((uint Pts, uint Dts) Ics, uint Pds, List<(uint Pts, uint Dts)> Ods, uint End);

/// <summary>Interactive Graphics (menu) streams: the buttons of a page as ICS / PDS / ODS / END segments in the .sup layout.</summary>
public static class IgStream
{
    public const byte Ics = 0x18, Pds = 0x14, Ods = 0x15, End = 0x80;
    public const int NoObject = 0xFFFF;
    public static readonly string[] Directions = ["up", "down", "left", "right"];

    /// <summary>
    /// RGBA pictures to (palette of at most 255 (r, g, b, a), one index picture per input; index 0 is transparent, palette entry k is index k + 1).
    /// Fewer bits per channel are kept until the colours fit; past 3 bits the rarest go to their nearest kept colour. Literal port of bdmvkit's
    /// quantize (same sort stability, same tie-breaks), so the output is byte-identical.
    /// </summary>
    public static (List<(byte R, byte G, byte B, byte A)> Palette, List<byte[]> Indexed) Quantize(IReadOnlyList<byte[]> images)
    {
        byte[][] q = [];
        var counts = new Dictionary<uint, int>();
        var order = new List<uint>();                        // first-seen order: the sort below must be stable on it
        byte mask = 0xFF;
        foreach (var bits in new[] { 8, 7, 6, 5, 4, 3 })
        {
            mask = (byte)((0xFF << (8 - bits)) & 0xFF);
            q = [.. images.Select(im => { var c = new byte[im.Length]; for (var i = 0; i < c.Length; i++) c[i] = (byte)(im[i] & mask); return c; })];
            counts = new Dictionary<uint, int>();
            order = [];
            foreach (var qi in q)
                for (var i = 0; i < qi.Length; i += 4)
                    if (qi[i + 3] != 0)
                    {
                        var key = Key(qi, i);
                        if (counts.TryGetValue(key, out var n)) counts[key] = n + 1;
                        else { counts[key] = 1; order.Add(key); }
                    }
            if (counts.Count <= 255) break;
        }
        var kept = order.OrderByDescending(k => counts[k]).Take(255).ToList(); // OrderBy is stable, like Python's sorted(reverse=True)
        var index = new Dictionary<uint, byte>();
        var items = new List<uint>();                         // `index` in insertion order, nearest search walks it
        for (var k = 0; k < kept.Count; k++) { index[kept[k]] = (byte)(k + 1); items.Add(kept[k]); }
        foreach (var c in order)
        {
            if (index.ContainsKey(c)) continue;
            var best = items[0];
            var bestD = long.MaxValue;
            foreach (var other in items)                       // includes colours already remapped, as bdmvkit's dict does
            {
                var d = 0L;
                for (var s = 0; s < 32; s += 8) { var diff = (int)(other >> s & 0xFF) - (int)(c >> s & 0xFF); d += diff * diff; }
                if (d < bestD) { bestD = d; best = other; }
            }
            index[c] = index[best];
            items.Add(c);
        }
        var half = (byte)((~mask & 0xFF) >> 1);
        var palette = kept.Select(c => (R: Spread(c >> 24, half), G: Spread(c >> 16, half), B: Spread(c >> 8, half), A: Spread(c, half))).ToList();
        var indexed = new List<byte[]>();
        foreach (var qi in q)
        {
            var o = new byte[qi.Length / 4];
            for (var i = 0; i < qi.Length; i += 4) o[i / 4] = qi[i + 3] != 0 ? index[Key(qi, i)] : (byte)0;
            indexed.Add(o);
        }
        return (palette, indexed);

        static uint Key(byte[] b, int i) => (uint)b[i] << 24 | (uint)b[i + 1] << 16 | (uint)b[i + 2] << 8 | b[i + 3];
        static byte Spread(uint v, byte half) { var c = (byte)v; return c != 0 ? (byte)(c | half) : c; }
    }

    /// <summary>
    /// Beyond bdmvkit: same contract as <see cref="Quantize"/> (at most 255 colours, index 0 transparent) but by median cut: the boxes of distinct
    /// (r, g, b, a) colours are split along their widest channel at the median pixel until there are 255, each box becoming its mean colour;
    /// pictures that already have at most 255 colours are kept exactly. Deterministic (stable sorts, first-seen order).
    /// </summary>
    public static (List<(byte R, byte G, byte B, byte A)> Palette, List<byte[]> Indexed) QuantizeMedianCut(IReadOnlyList<byte[]> images)
    {
        var counts = new Dictionary<uint, int>();
        var order = new List<uint>();
        foreach (var im in images)
            for (var i = 0; i < im.Length; i += 4)
                if (im[i + 3] != 0)
                {
                    var key = (uint)im[i] << 24 | (uint)im[i + 1] << 16 | (uint)im[i + 2] << 8 | im[i + 3];
                    if (counts.TryGetValue(key, out var n)) counts[key] = n + 1; else { counts[key] = 1; order.Add(key); }
                }
        static int Ch(uint c, int channel) => (int)(c >> (24 - 8 * channel) & 0xFF);
        List<uint> centres;
        if (order.Count <= 255) centres = [.. order.OrderByDescending(k => counts[k])];
        else
        {
            var boxes = new List<List<uint>> { order };
            while (boxes.Count < 255)
            {
                // the box with the widest channel range (several colours) is split next
                var best = -1; var bestRange = 0; var bestChannel = 0;
                for (var b = 0; b < boxes.Count; b++)
                {
                    if (boxes[b].Count < 2) continue;
                    for (var ch = 0; ch < 4; ch++)
                    {
                        int lo = 255, hi = 0;
                        foreach (var c in boxes[b]) { var v = Ch(c, ch); if (v < lo) lo = v; if (v > hi) hi = v; }
                        if (hi - lo > bestRange) { bestRange = hi - lo; best = b; bestChannel = ch; }
                    }
                }
                if (best < 0) break;
                var sorted = boxes[best].OrderBy(c => Ch(c, bestChannel)).ToList();
                var half = sorted.Sum(c => (long)counts[c]) / 2;
                long acc = 0; var cut = 1;
                for (var i = 0; i < sorted.Count - 1; i++) { acc += counts[sorted[i]]; cut = i + 1; if (acc >= half) break; }
                boxes[best] = sorted[..cut];
                boxes.Add(sorted[cut..]);
            }
            centres = boxes.Select(box =>
            {
                long w = box.Sum(c => (long)counts[c]); uint mean = 0;
                for (var ch = 0; ch < 4; ch++) mean |= (uint)((box.Sum(c => (long)Ch(c, ch) * counts[c]) + w / 2) / w) << (24 - 8 * ch);
                return mean;
            }).Distinct().ToList();
        }
        var palette = centres.Select(c => ((byte)Ch(c, 0), (byte)Ch(c, 1), (byte)Ch(c, 2), (byte)Ch(c, 3))).ToList();
        var nearest = new Dictionary<uint, byte>();
        foreach (var key in order)
        {
            var best = 0; var bestD = long.MaxValue;
            for (var k = 0; k < centres.Count; k++)
            {
                long d = 0;
                for (var ch = 0; ch < 4; ch++) { var diff = Ch(key, ch) - Ch(centres[k], ch); d += diff * diff; }
                if (d < bestD) { bestD = d; best = k; }
            }
            nearest[key] = (byte)(best + 1);
        }
        var indexed = images.Select(im =>
        {
            var o = new byte[im.Length / 4];
            for (var i = 0; i < im.Length; i += 4)
                if (im[i + 3] != 0) o[i / 4] = nearest[(uint)im[i] << 24 | (uint)im[i + 1] << 16 | (uint)im[i + 2] << 8 | im[i + 3]];
            return o;
        }).ToList();
        return (palette, indexed);
    }

    /// <summary>
    /// The arrow keys between buttons: the declared neighbour, else the nearest button whose centre lies that way (closer across counts twice
    /// as much as along), else the button itself. Returns {id: {direction: id}}.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> AutoNav(IReadOnlyList<IgButton> buttons)
    {
        var centre = buttons.ToDictionary(b => b.Id, b => (X: b.X + b.W / 2.0, Y: b.Y + b.H / 2.0));
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var b in buttons)
        {
            var (cx, cy) = centre[b.Id];
            var nav = new Dictionary<string, string>();
            foreach (var d in Directions)
            {
                if (b.Nav.TryGetValue(d, out var declared)) { nav[d] = declared; continue; }
                var best = b.Id;
                double? score = null;
                foreach (var o in buttons)
                {
                    if (o == b) continue;
                    var (ox, oy) = centre[o.Id];
                    var along = d switch { "up" => cy - oy, "down" => oy - cy, "left" => cx - ox, _ => ox - cx };
                    var across = d is "up" or "down" ? Math.Abs(ox - cx) : Math.Abs(oy - cy);
                    if (along <= 0) continue;
                    var s = along + 2 * across;
                    if (score == null || s < score) { best = o.Id; score = s; }
                }
                nav[d] = best;
            }
            result[b.Id] = nav;
        }
        return result;
    }

    /// <summary>
    /// The buttons as an IG stream in the .sup layout. <paramref name="frameRate"/> is the menu clip's frame-rate code (<see cref="HdmvFormats"/>).
    /// A faithful port of bdmvkit's ig_stream plus the options flagged "beyond bdmvkit" in <see cref="IgOptions"/>.
    /// </summary>
    public static byte[] Build(IReadOnlyList<IgButton> buttons, int frameRate, IgOptions? options = null)
    {
        var opt = options ?? new IgOptions();
        if (buttons.Count == 0) throw new ArgumentException("a menu needs at least one button");
        var ids = buttons.Select(b => b.Id).ToList();
        if (ids.Distinct().Count() != ids.Count) throw new ArgumentException("button ids must be unique");
        foreach (var b in buttons)
        {
            if (b.X < 0 || b.Y < 0 || b.X + b.W > opt.Width || b.Y + b.H > opt.Height)
                throw new ArgumentException($"button '{b.Id}' is not inside the {opt.Width} x {opt.Height} frame");
            foreach (var (name, pic) in new[] { ("selected", b.Selected), ("activated", b.Activated), ("normal", b.Normal) })
                if (pic != null && pic.Length != b.W * b.H * 4) throw new ArgumentException($"button '{b.Id}': its {name} picture is not {b.W} x {b.H} RGBA");
            foreach (var (d, to) in b.Nav)
                if (!Directions.Contains(d) || !ids.Contains(to)) throw new ArgumentException($"button '{b.Id}': nav {d} -> '{to}'");
            foreach (var c in b.Commands)
                if (c.Length != 12) throw new ArgumentException($"button '{b.Id}': a navigation command is 12 bytes");
        }
        if (opt.Default != null && !ids.Contains(opt.Default)) throw new ArgumentException($"no button '{opt.Default}'");
        if (opt.ButtonOffset + buttons.Count > 9999) throw new ArgumentException("button numbers go up to 9999");

        var nav = AutoNav(buttons);
        var normal = buttons.Any(b => b.Normal != null);
        var pictures = new List<byte[]>();
        foreach (var b in buttons)
        {
            pictures.Add(b.Normal ?? new byte[b.W * b.H * 4]);
            pictures.Add(b.Selected);
            pictures.Add(b.Activated ?? b.Selected);
        }
        var (colours, indexed) = opt.Palette == IgPalette.MedianCut ? QuantizeMedianCut(pictures) : Quantize(pictures);
        var pal = new BinaryWriterBE();
        pal.U8(0); pal.U8(0);                                    // palette id 0, version 0
        pal.U8(0); pal.U8(16); pal.U8(128); pal.U8(128); pal.U8(0);
        for (var k = 0; k < colours.Count; k++)
        {
            var (y, cb, cr) = Pgs.YCbCr(colours[k].R, colours[k].G, colours[k].B);
            pal.U8(k + 1); pal.U8(y); pal.U8(cr); pal.U8(cb); pal.U8(colours[k].A);
        }

        var objects = new List<(int Id, byte[] Coded, int W, int H)>();
        var bogs = new BinaryWriterBE();
        for (var k = 0; k < buttons.Count; k++)
        {
            var b = buttons[k];
            var oids = new int[3];
            for (var slot = 0; slot < 3; slot++)               // normal, selected, activated
            {
                oids[slot] = NoObject;
                if (slot == 0 && !normal) continue;
                var oid = 3 * k + slot;
                objects.Add((oid, Pgs.Rle(indexed[3 * k + slot], b.W, b.H), b.W, b.H));
                oids[slot] = oid;
            }
            bogs.U16(k); bogs.U8(1);                           // one button per overlap group: the buttons do not overlap
            bogs.U16(k); bogs.U16(opt.ButtonOffset + k + 1); bogs.U8(b.Auto ? 0x80 : 0); bogs.U16(b.X); bogs.U16(b.Y);
            foreach (var d in Directions) bogs.U16(ids.IndexOf(nav[b.Id][d]));
            bogs.U16(oids[0]); bogs.U16(oids[0]); bogs.U8(0);                // normal: start, end, repeat flag
            bogs.U8(0xFF); bogs.U16(oids[1]); bogs.U16(oids[1]); bogs.U8(0); // selected: no sound, start, end, repeat flag
            bogs.U8(0xFF); bogs.U16(oids[2]); bogs.U16(oids[2]);             // activated: no sound, start, end
            bogs.U16(b.Commands.Count);
            foreach (var c in b.Commands) bogs.Bytes(c);
        }

        var first = opt.FollowSelection ? 0xFFFF : opt.Default == null ? 0 : ids.IndexOf(opt.Default);
        var page = new BinaryWriterBE();
        page.U8(0); page.U8(0);                                    // page 0, version 0
        for (var s = 56; s >= 0; s -= 8) page.U8((int)(opt.PageUoMask >> s & 0xFF)); // UO_mask_table, 8 bytes
        page.U16(0); page.U16(0);                                  // no in / out effects
        page.U8(0); page.U16(first); page.U16(NoObject); page.U8(0); page.U8(buttons.Count);
        page.Bytes(bogs.ToArray());

        var body = new BinaryWriterBE();
        body.U8(opt.Popup ? 0x40 : 0x00);                          // multiplexed; always on, or a pop-up (the Pop-up key shows it)
        body.Zero(10);                                             // no time-outs
        body.Zero(3); body.U8(1);                                  // no user time-out; one page
        body.Bytes(page.ToArray());
        var ics = new BinaryWriterBE();
        ics.U16(opt.Width); ics.U16(opt.Height); ics.U8(frameRate << 4);
        ics.U16(0); ics.U8(0x80);                                  // composition number 0, epoch start
        ics.U8(0xC0); ics.U8(body.Length >> 16); ics.U16(body.Length & 0xFFFF);
        ics.Bytes(body.ToArray());
        if (ics.Length > Pgs.MaxSegment) throw new ArgumentException("too many buttons or button commands for one composition");

        return Assemble(ics.ToArray(), pal.ToArray(), objects, opt);
    }

    /// <summary>ICS, PDS, the ODS segments (split in 65531-byte pieces), END; dated according to <see cref="IgOptions.Timeline"/>.</summary>
    static byte[] Assemble(byte[] ics, byte[] pds, List<(int Id, byte[] Coded, int W, int H)> objects, IgOptions opt)
    {
        var o = new List<byte>();
        long width = opt.Width, height = opt.Height;
        var times = opt.Timeline == IgTimeline.Reference ? Timeline(width, height, objects.Select(x => (x.W, x.H)).ToList(), opt.PresentationTicks) : null;
        o.AddRange(Pgs.Segment(Ics, times?.Ics.Pts ?? 0, times?.Ics.Dts ?? 0, ics));
        o.AddRange(Pgs.Segment(Pds, times?.Pds ?? 0, 0, pds));
        var n = 0;
        foreach (var (id, coded, w, h) in objects)
        {
            var data = new byte[3 + 4 + coded.Length];
            data[0] = (byte)((coded.Length + 4) >> 16); data[1] = (byte)((coded.Length + 4) >> 8); data[2] = (byte)(coded.Length + 4);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3), (ushort)w);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(5), (ushort)h);
            coded.CopyTo(data, 7);
            const int size = Pgs.MaxSegment - 4;
            for (var pos = 0; pos < data.Length; pos += size)
            {
                var chunk = data.AsSpan(pos, Math.Min(size, data.Length - pos));
                var flags = (pos == 0 ? 0x80 : 0) | (pos + size >= data.Length ? 0x40 : 0);
                var seg = new byte[4 + chunk.Length];
                BinaryPrimitives.WriteUInt16BigEndian(seg, (ushort)id);
                seg[3] = (byte)flags;                             // (seg[2] = version 0)
                chunk.CopyTo(seg.AsSpan(4));
                o.AddRange(Pgs.Segment(Ods, times?.Ods[n].Pts ?? 0, times?.Ods[n].Dts ?? 0, seg));
            }
            n++;
        }
        o.AddRange(Pgs.Segment(End, times?.End ?? 0, 0, []));
        return [.. o];
    }

    /// <summary>
    /// Beyond bdmvkit. Segment dates (90 kHz) in the manner of the decoded reference disc: with the ICS shown at <paramref name="presentation"/>,
    /// DTS(ICS) is earlier by D(plane) = ceil(2 x 90000 x W x H / 16e6) (clearing and writing the plane: 23328 ticks for 1920 x 1080, as measured on the
    /// reference); the palette is dated DTS(ICS); each object has DTS = the PTS of the one before (the first: DTS(ICS)) and PTS = DTS + D(object)
    /// with D(object) computed like D(plane) from its size (933 ticks for the reference's 901 x 92 object); END is dated like the last object.
    /// When the objects take longer than the plane, the ICS is presented after the last object.
    /// </summary>
    public static IgTimes Timeline(long width, long height, IReadOnlyList<(int W, int H)> objects, uint presentation)
    {
        static uint Decode(long w, long h) => (uint)((2L * 90000 * w * h + 16_000_000 - 1) / 16_000_000);
        var plane = Decode(width, height);
        var ods = new List<(uint, uint)>();
        long t = 0;
        foreach (var (w, h) in objects) { var dts = t; t += Decode(w, h); ods.Add(((uint)t, (uint)dts)); }
        var span = Math.Max(plane, t);
        var start = presentation > span ? presentation - span : 0;     // DTS(ICS): decoding cannot begin before 0
        var pts = start + span;
        var shifted = ods.Select(x => (x.Item1 + (uint)start, x.Item2 + (uint)start)).ToList();
        return new IgTimes(((uint)pts, (uint)start), (uint)start, shifted, (uint)(start + t));
    }
}
