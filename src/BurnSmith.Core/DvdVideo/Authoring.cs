using System.Buffers.Binary;
using System.Text;

namespace BurnSmith.Core.DvdVideo;

/// <summary>A button of a menu page: its rectangle on the frame, its neighbours (1-based, 0 = itself) and the commands it runs.</summary>
public sealed record Button(int X0, int Y0, int X1, int Y1, int Up, int Down, int Left, int Right, IReadOnlyList<ulong> Commands);

/// <summary>
/// One program chain: a program stream from ffmpeg (null for a chain that only runs commands), its chapter times in seconds (one cell each; the first
/// is 0), the commands before and after it, a still that waits forever at its end, its buttons, its 16 colours (RGB).
/// </summary>
public sealed record Chain(string? Vob, IReadOnlyList<double> Chapters, IReadOnlyList<ulong> Pre, IReadOnlyList<ulong> Post, bool Still = false,
                           IReadOnlyList<Button>? Buttons = null, int[]? Colors = null, byte[]? Highlight = null);

/// <summary>An audio stream of the titles: AC-3, its channels, its two-letter language ("" unknown).</summary>
public sealed record AudioStream(int Channels, string Lang);

/// <summary>
/// A DVD-Video disc with one title set: <see cref="Menus"/> (the first is the root menu), <see cref="Titles"/>, the first-play commands.
/// <see cref="Highlight"/>: for codes 0..3 of the menu subpictures, the CLUT entry and opacity when a button is selected, then when it is activated.
/// </summary>
public sealed record DvdDisc(bool Pal, IReadOnlyList<Chain> Menus, IReadOnlyList<Chain> Titles, IReadOnlyList<ulong> FirstPlay,
                          IReadOnlyList<AudioStream> Audio, IReadOnlyList<string> Subtitles, bool Wide = true);

/// <summary>Writes the VIDEO_TS folder of a <see cref="DvdDisc"/>: the program streams copied into VOB files with their navigation packs filled, and the IFO files.</summary>
public static class Authoring
{
    const int Sector = 2048;
    const long MaxVobSectors = 0x3FFFF800 / Sector; // a VOB file stays under 1 GiB

    sealed class Cell
    {
        public int Vob, Id;
        public long First, Last; // sectors in the VOBS: the first unit, the first sector of the last unit
        public long LastEnd;
        public long StartPts, EndPts;
        public List<VobScan.Vobu> Units = [];
    }

    sealed class Placed
    {
        public required Chain Chain;
        public required List<Cell> Cells;
    }

    public static void Write(DvdDisc disc, string outDir, Action<string>? log = null)
    {
        log ??= _ => { };
        if (disc.Titles.Count is 0 or > 99) throw new ArgumentException("a DVD has 1 to 99 titles");
        if (disc.Menus.Count > 0 && disc.Menus[0].Vob == null) throw new ArgumentException("the root menu needs a picture");
        var frame = disc.Pal ? 3600 : 3003;
        var dir = Path.Combine(outDir, "VIDEO_TS");
        Directory.CreateDirectory(dir);

        // the VOBs in order: menus then titles, each program stream one VOB id of its VOBS
        log("author: menus");
        var menus = Place(disc.Menus, frame);
        log("author: titles");
        var titles = Place(disc.Titles, frame);
        var menuSectors = menus.SelectMany(m => m.Cells).Select(c => c.LastEnd + 1).DefaultIfEmpty(0).Max();
        var titleSectors = titles.SelectMany(m => m.Cells).Select(c => c.LastEnd + 1).Max();
        if (menuSectors > MaxVobSectors) throw new InvalidOperationException("the menus take more than 1 GB, the size of one VOB file");
        if (titleSectors > MaxVobSectors * 9) throw new InvalidOperationException("the titles take more than 9 VOB files of 1 GB");

        var vts = VtsIfo(disc, menus, titles, frame, menuSectors, titleSectors);
        var vtsSectors = vts.Length / Sector;
        var vmg = VmgIfo(disc, titles, vts);
        var vmgSectors = vmg.Length / Sector;
        BinaryPrimitives.WriteUInt32BigEndian(vmg.AsSpan(0x0C), (uint)(2 * vmgSectors - 1));

        File.WriteAllBytes(Path.Combine(dir, "VIDEO_TS.IFO"), vmg);
        File.WriteAllBytes(Path.Combine(dir, "VIDEO_TS.BUP"), vmg);
        File.WriteAllBytes(Path.Combine(dir, "VTS_01_0.IFO"), vts);
        File.WriteAllBytes(Path.Combine(dir, "VTS_01_0.BUP"), vts);
        log("author: menu VOB");
        if (menus.Count > 0) CopyVobs(menus, 1, disc.Pal, [Path.Combine(dir, "VTS_01_0.VOB")], long.MaxValue, true);
        log("author: title VOBs");
        CopyVobs(titles, disc.Subtitles.Count, disc.Pal, Enumerable.Range(1, 9).Select(i => Path.Combine(dir, $"VTS_01_{i}.VOB")).ToList(), MaxVobSectors, false);
    }

    /// <summary>Reads the units of each chain's stream and cuts it into cells at its chapters: a cell starts on the first unit at or after its time.</summary>
    static List<Placed> Place(IReadOnlyList<Chain> chains, int frame)
    {
        var placed = new List<Placed>();
        long at = 0;
        var vob = 0;
        foreach (var chain in chains)
        {
            var cells = new List<Cell>();
            if (chain.Vob != null)
            {
                vob++;
                var units = VobScan.Scan(chain.Vob, frame);
                var start = units[0].StartPts;
                var marks = chain.Chapters.Count == 0 ? [0.0] : chain.Chapters;
                var firsts = marks.Select(t => units.FindIndex(u => u.StartPts - start >= t * 90000 - frame / 2)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
                if (firsts.Count == 0 || firsts[0] != 0) firsts.Insert(0, 0);
                for (var k = 0; k < firsts.Count; k++)
                {
                    var mine = units.GetRange(firsts[k], (k + 1 < firsts.Count ? firsts[k + 1] : units.Count) - firsts[k]);
                    cells.Add(new Cell
                    {
                        Vob = vob, Id = k + 1, Units = mine,
                        First = at + mine[0].Sector, Last = at + mine[^1].Sector, LastEnd = at + mine[^1].Sector + mine[^1].Sectors - 1,
                        StartPts = mine[0].StartPts, EndPts = mine[^1].EndPts,
                    });
                }
                at += units[^1].Sector + units[^1].Sectors;
            }
            placed.Add(new Placed { Chain = chain, Cells = cells });
        }
        return placed;
    }

    // ---- times

    static uint Bcd(long ticks, bool pal)
    {
        var fps = pal ? 25 : 30;
        var frames = (long)Math.Round(ticks / (pal ? 3600.0 : 3003.0));
        var secs = frames / fps;
        int B(long v) => (int)(v / 10 << 4 | v % 10);
        return (uint)(B(secs / 3600 % 100) << 24 | B(secs / 60 % 60) << 16 | B(secs % 60) << 8 | B(frames % fps) | (pal ? 0x40 : 0xC0));
    }

    // ---- the navigation packs

    static void CopyVobs(List<Placed> chains, int subtitles, bool pal, List<string> files, long perFile, bool menus)
    {
        var cells = chains.SelectMany(c => c.Cells.Select(cell => (Chain: c.Chain, Cell: cell))).ToList();
        var buffer = new byte[Sector];
        FileStream? output = null;
        var fileIndex = -1;
        long written = 0;
        try
        {
            foreach (var group in cells.GroupBy(c => c.Cell.Vob))
            {
                var source = group.First().Chain.Vob!;
                var vobStart = group.First().Cell.StartPts;
                var vobEnd = group.Last().Cell.EndPts;
                var vobFirst = group.First().Cell.First;
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                foreach (var (chain, cell) in group)
                {
                    for (var i = 0; i < cell.Units.Count; i++)
                    {
                        var u = cell.Units[i];
                        var lbn = vobFirst + u.Sector;
                        for (long k = 0; k < u.Sectors; k++)
                        {
                            input.ReadExactly(buffer);
                            if (k == 0) Nav(buffer, u, subtitles, lbn, cell, vobStart, vobEnd, pal, chain, menus);
                            if (output == null || written >= perFile * (fileIndex + 1))
                            {
                                output?.Dispose();
                                fileIndex++;
                                output = new FileStream(files[fileIndex], FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                            }
                            output.Write(buffer);
                            written++;
                        }
                    }
                }
            }
        }
        finally { output?.Dispose(); }
    }

    static readonly int[] Steps = [240, 120, 60, 20, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1]; // half seconds, farthest first

    static void Nav(byte[] s, VobScan.Vobu u, int subtitles, long lbn, Cell cell, long vobStart, long vobEnd, bool pal, Chain chain, bool menu)
    {
        var pci = s.AsSpan(0x2D, 0x3D3);
        var dsi = s.AsSpan(0x407, 0x3F9);
        pci.Clear(); dsi.Clear();
        var eltm = Bcd(u.StartPts - cell.StartPts, pal);
        BinaryPrimitives.WriteUInt32BigEndian(pci[0x00..], (uint)lbn);
        BinaryPrimitives.WriteUInt32BigEndian(pci[0x0C..], (uint)u.StartPts);
        BinaryPrimitives.WriteUInt32BigEndian(pci[0x10..], (uint)u.EndPts);
        BinaryPrimitives.WriteUInt32BigEndian(pci[0x18..], eltm);
        if (menu && chain.Buttons is { Count: > 0 } buttons) Highlight(pci, buttons, vobStart, chain.Highlight!);

        BinaryPrimitives.WriteUInt32BigEndian(dsi[0x00..], (uint)u.Scr);
        BinaryPrimitives.WriteUInt32BigEndian(dsi[0x04..], (uint)lbn);
        BinaryPrimitives.WriteUInt32BigEndian(dsi[0x08..], (uint)(u.Sectors - 1));
        for (var i = 0; i < 3 && i < u.RefEnds.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(dsi[(0x0C + 4 * i)..], (uint)u.RefEnds[i]);
        BinaryPrimitives.WriteUInt16BigEndian(dsi[0x18..], (ushort)cell.Vob);
        dsi[0x1B] = (byte)cell.Id;
        BinaryPrimitives.WriteUInt32BigEndian(dsi[0x1C..], eltm);
        BinaryPrimitives.WriteUInt32BigEndian(dsi[0x2C..], (uint)vobStart);
        BinaryPrimitives.WriteUInt32BigEndian(dsi[0x30..], (uint)vobEnd);

        // search: the unit of this cell that shows the time n half-seconds away; bit 30 says other units lie between it and the nearer step's unit
        var units = cell.Units;
        var here = units.IndexOf(u);
        int At(long t) => t < units[0].StartPts || t >= units[^1].EndPts ? -1 : units.FindLastIndex(x => x.StartPts <= t);
        uint Distance(int other) => (uint)Math.Abs(units[other].Sector - u.Sector);
        var next = here + 1 < units.Count ? here + 1 : -1;
        var prev = here - 1;
        BinaryPrimitives.WriteUInt32BigEndian(dsi[0xEA..], next >= 0 ? 0x80000000u | Distance(next) : 0xBFFFFFFF);
        // nearest step first: a step past the cell's end says "none", with bit 30 when the nearer step found a unit and units lie between it and the cell's last (or first) one
        uint nearerFwd = 0, nearerBwd = 0;
        int lastFwd = here, lastBwd = here;
        for (var i = Steps.Length - 1; i >= 0; i--)
        {
            var f = At(u.StartPts + Steps[i] * 45000L);
            nearerFwd = f < 0 ? 0x3FFFFFFFu | ((nearerFwd & 0x80000000u) != 0 && units.Count - 1 - lastFwd > 1 ? 0x40000000u : 0) : 0x80000000u | (Math.Abs(f - lastFwd) > 1 ? 0x40000000u : 0) | Distance(f);
            if (f >= 0) lastFwd = f;
            BinaryPrimitives.WriteUInt32BigEndian(dsi[(0xEE + 4 * i)..], nearerFwd);
            var g = At(u.StartPts - Steps[i] * 45000L);
            nearerBwd = g < 0 ? 0x3FFFFFFFu | ((nearerBwd & 0x80000000u) != 0 && lastBwd > 0 ? 0x40000000u : 0) : 0x80000000u | (Math.Abs(g - lastBwd) > 1 ? 0x40000000u : 0) | Distance(g);
            if (g >= 0) lastBwd = g;
            BinaryPrimitives.WriteUInt32BigEndian(dsi[(0x18A - 4 * i)..], nearerBwd);
        }
        BinaryPrimitives.WriteUInt32BigEndian(dsi[0x13A..], next >= 0 ? 0x80000000u | Distance(next) : 0x3FFFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(dsi[0x13E..], prev >= 0 ? 0x80000000u | Distance(prev) : 0x3FFFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(dsi[0x18E..], prev >= 0 ? 0x80000000u | Distance(prev) : 0xBFFFFFFF);
        for (var a = 0; a < 8; a++)
            if (u.Audio[a] >= 0) BinaryPrimitives.WriteUInt16BigEndian(dsi[(0x192 + 2 * a)..], (ushort)u.Audio[a]);
        for (var p = 0; p < subtitles; p++) // the first packet of each stream in this unit, else "none here"
            BinaryPrimitives.WriteUInt32BigEndian(dsi[(0x1A2 + 4 * p)..], u.Sub[p] >= 0 ? (uint)u.Sub[p] : 0x3FFFFFFF);
    }

    /// <summary>
    /// The highlight of a menu page, in every unit: its buttons from the start of the VOB, until the page ends. Two button groups (16:9 and
    /// letterbox) with the same rectangles, as a widescreen menu without pan-scan needs; one group above 18 buttons.
    /// </summary>
    static void Highlight(Span<byte> pci, IReadOnlyList<Button> buttons, long start, byte[] colors)
    {
        if (buttons.Count > 36) throw new InvalidOperationException("a DVD menu page has at most 36 buttons");
        var groups = buttons.Count > 18 ? 1 : 2;
        BinaryPrimitives.WriteUInt16BigEndian(pci[0x60..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(pci[0x62..], (uint)start);
        BinaryPrimitives.WriteUInt32BigEndian(pci[0x66..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(pci[0x6A..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(pci[0x6E..], (ushort)(groups == 2 ? 0x2120 : 0x1100));
        pci[0x70] = 0;
        pci[0x71] = (byte)buttons.Count;
        pci[0x72] = (byte)buttons.Count;
        // colour scheme 1: [selected colours, selected contrast, activated colours, activated contrast], codes 3 and 2 in the first byte
        pci[0x76] = (byte)(colors[3] << 4 | colors[2]); pci[0x77] = (byte)(colors[1] << 4 | colors[0]);
        pci[0x78] = (byte)(colors[7] << 4 | colors[6]); pci[0x79] = (byte)(colors[5] << 4 | colors[4]);
        pci[0x7A] = (byte)(colors[11] << 4 | colors[10]); pci[0x7B] = (byte)(colors[9] << 4 | colors[8]);
        pci[0x7C] = (byte)(colors[15] << 4 | colors[14]); pci[0x7D] = (byte)(colors[13] << 4 | colors[12]);
        for (var g = 0; g < groups; g++)
            for (var i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                var e = pci.Slice(0x8E + 18 * (g * 18 + i), 18);
                e[0] = (byte)(1 << 6 | b.X0 >> 4); e[1] = (byte)((b.X0 & 15) << 4 | b.X1 >> 8); e[2] = (byte)b.X1;
                e[3] = (byte)(b.Y0 >> 4); e[4] = (byte)((b.Y0 & 15) << 4 | b.Y1 >> 8); e[5] = (byte)b.Y1;
                e[6] = (byte)(b.Up > 0 ? b.Up : i + 1); e[7] = (byte)(b.Down > 0 ? b.Down : i + 1);
                e[8] = (byte)(b.Left > 0 ? b.Left : i + 1); e[9] = (byte)(b.Right > 0 ? b.Right : i + 1);
                var cmd = b.Commands.Count == 1 ? b.Commands[0] : Vm.SetThenTail(15, i + 1);
                for (var k = 7; k >= 0; k--) { e[10 + k] = (byte)cmd; cmd >>= 8; }
            }
    }

    // ---- program chains

    /// <summary>
    /// The commands after a menu's picture: when a button needs more than one command it sets g15 to its number and comes here, where g15 picks its
    /// commands (g14 keeps it, g15 is cleared for the next time). The chain's own post commands run when it simply ends.
    /// </summary>
    static List<ulong> PostOf(Chain c)
    {
        var post = new List<ulong>();
        var many = (c.Buttons ?? []).Select((b, i) => (b, i)).Where(x => x.b.Commands.Count > 1).ToList();
        if (many.Count == 0) { post.AddRange(c.Post); return post; }
        post.Add(Vm.SetReg(14, 15));
        post.Add(Vm.Set(15, 0));
        var line = post.Count + many.Count + Math.Max(1, c.Post.Count) + 1; // where the first button's commands start
        foreach (var (b, i) in many) { post.Add(Vm.Goto(line, 14, Vm.Eq, i + 1)); line += b.Commands.Count; }
        if (c.Post.Count == 0) post.Add(Vm.Exit()); else post.AddRange(c.Post);
        foreach (var (b, _) in many) post.AddRange(b.Commands);
        if (post.Count > 128) throw new InvalidOperationException("a menu page needs more than 128 commands");
        return post;
    }

    static byte[] Pgc(Chain c, List<Cell> cells, bool pal, int audioCount, int subCount, bool menu, bool wide = true)
    {
        var post = PostOf(c);
        var cmds = c.Pre.Count + post.Count;
        var cmdAt = 0xEC;
        var cmdSize = cmds > 0 ? 8 + 8 * cmds : 0;
        var mapAt = cmdAt + cmdSize;
        var cellsAt = mapAt + (cells.Count + 1) / 2 * 2;
        var posAt = cellsAt + 24 * cells.Count;
        var b = new byte[posAt + 4 * cells.Count];
        var span = b.AsSpan();
        b[2] = (byte)cells.Count;
        b[3] = (byte)cells.Count;
        var total = cells.Sum(x => x.EndPts - x.StartPts);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], cells.Count > 0 ? Bcd(total, pal) : 0xC0);
        for (var a = 0; a < audioCount; a++) { b[0x0C + 2 * a] = (byte)(0x80 | a); }
        for (var s = 0; s < subCount; s++) // 4:3 column, then wide and letterbox for a 16:9 picture
            if (wide) { b[0x1C + 4 * s] = 0x80; b[0x1D + 4 * s] = (byte)s; b[0x1E + 4 * s] = (byte)s; }
            else b[0x1C + 4 * s] = (byte)(0x80 | s);
        var clut = c.Colors ?? new int[16];
        for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32BigEndian(span[(0xA4 + 4 * i)..], YCrCb(clut[i]));
        if (cmds > 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[0xE4..], (ushort)cmdAt);
            BinaryPrimitives.WriteUInt16BigEndian(span[cmdAt..], (ushort)c.Pre.Count);
            BinaryPrimitives.WriteUInt16BigEndian(span[(cmdAt + 2)..], (ushort)post.Count);
            BinaryPrimitives.WriteUInt16BigEndian(span[(cmdAt + 6)..], (ushort)(cmdSize - 1));
            var k = cmdAt + 8;
            foreach (var x in c.Pre.Concat(post)) { Vm.Write(b, k, x); k += 8; }
        }
        if (cells.Count > 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[0xE6..], (ushort)mapAt);
            BinaryPrimitives.WriteUInt16BigEndian(span[0xE8..], (ushort)cellsAt);
            BinaryPrimitives.WriteUInt16BigEndian(span[0xEA..], (ushort)posAt);
        }
        for (var i = 0; i < cells.Count; i++)
        {
            var x = cells[i];
            b[mapAt + i] = (byte)(i + 1);
            var e = span.Slice(cellsAt + 24 * i, 24);
            e[0] = (byte)(x.Id == 1 ? 0x02 : 0x08); // a new stream starts its clock again; a chapter inside it follows on seamlessly
            e[2] = (byte)(c.Still && i == cells.Count - 1 ? 0xFF : 0);
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], Bcd(x.EndPts - x.StartPts, pal));
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)x.First);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], (uint)x.Last);
            BinaryPrimitives.WriteUInt32BigEndian(e[20..], (uint)x.LastEnd);
            BinaryPrimitives.WriteUInt16BigEndian(span[(posAt + 4 * i)..], (ushort)x.Vob);
            b[posAt + 4 * i + 3] = (byte)x.Id;
        }
        _ = menu;
        return b;
    }

    /// <summary>RGB to the (0, Y, Cr, Cb) of a CLUT entry, studio range.</summary>
    static uint YCrCb(int rgb)
    {
        double r = (rgb >> 16 & 255) / 255.0, g = (rgb >> 8 & 255) / 255.0, bl = (rgb & 255) / 255.0;
        int Clip(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
        var y = Clip(16 + 65.481 * r + 128.553 * g + 24.966 * bl);
        var cb = Clip(128 - 37.797 * r - 74.203 * g + 112 * bl);
        var cr = Clip(128 + 112 * r - 93.786 * g - 18.214 * bl);
        return (uint)(y << 16 | cr << 8 | cb);
    }

    /// <summary>A list of chains: count, end, then (category, offset) per chain, then the chains.</summary>
    static byte[] PgcTable(List<byte[]> pgcs, Func<int, uint> category)
    {
        var head = 8 + 8 * pgcs.Count;
        var size = head + pgcs.Sum(p => p.Length);
        var b = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)pgcs.Count);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)(size - 1));
        var at = head;
        for (var i = 0; i < pgcs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + 8 * i), category(i));
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12 + 8 * i), (uint)at);
            pgcs[i].CopyTo(b, at);
            at += pgcs[i].Length;
        }
        return b;
    }

    // ---- the title set's IFO

    static byte[] VideoAttributes(bool pal, bool wide) => [(byte)(0x40 | (pal ? 0x10 : 0) | (wide ? 0x0C | 0x02 : 0)), 0x00]; // MPEG-2, 16:9 with no automatic pan-scan, or 4:3

    static byte[] AudioAttributes(AudioStream a)
    {
        var known = a.Lang.Length == 2 && a.Lang != "xx";
        return [(byte)(known ? 0x04 : 0), (byte)(0xC0 | (Math.Clamp(a.Channels, 1, 6) - 1)), (byte)(known ? a.Lang[0] : 0), (byte)(known ? a.Lang[1] : 0), 0, 0, 0, 0];
    }

    static byte[] SubAttributes(string lang)
    {
        var known = lang.Length == 2 && lang != "xx";
        return [(byte)(known ? 0x01 : 0), 0, (byte)(known ? lang[0] : 0), (byte)(known ? lang[1] : 0), 0, 0];
    }

    static byte[] VtsIfo(DvdDisc disc, List<Placed> menus, List<Placed> titles, int frame, long menuSectors, long titleSectors)
    {
        var pal = disc.Pal;
        var tables = new List<(int Pointer, byte[] Data)>();

        // chapters: each title's programs
        {
            var head = 8 + 4 * titles.Count;
            var size = head + titles.Sum(t => 4 * t.Cells.Count);
            var b = new byte[size];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)titles.Count);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)(size - 1));
            var at = head;
            for (var t = 0; t < titles.Count; t++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + 4 * t), (uint)at);
                for (var c = 0; c < titles[t].Cells.Count; c++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), (ushort)(t + 1));
                    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at + 2), (ushort)(c + 1));
                    at += 4;
                }
            }
            tables.Add((0xC8, b));
        }
        tables.Add((0xCC, PgcTable(titles.Select(t => Pgc(t.Chain, t.Cells, pal, disc.Audio.Count, disc.Subtitles.Count, false, disc.Wide)).ToList(), i => 0x80000000u | (uint)(i + 1) << 24)));
        if (menus.Count > 0)
        {
            var lu = PgcTable(menus.Select(m => Pgc(m.Chain, m.Cells, pal, 1, m.Chain.Buttons is { Count: > 0 } ? 1 : 0, true)).ToList(), i => i == 0 ? 0x83000000u : 0);
            var ut = new byte[16 + lu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(ut, 1);
            BinaryPrimitives.WriteUInt32BigEndian(ut.AsSpan(4), (uint)(ut.Length - 1));
            ut[8] = (byte)'e'; ut[9] = (byte)'n'; ut[11] = 0x80; // the menus of every language: the root one exists
            BinaryPrimitives.WriteUInt32BigEndian(ut.AsSpan(12), 16);
            lu.CopyTo(ut, 16);
            tables.Add((0xD0, ut));
        }
        // time map: per title, the unit that shows each second (or each few seconds for a long one, 2048 entries at most)
        {
            var maps = titles.Select(t =>
            {
                var units = t.Cells.SelectMany(c => c.Units.Select(u => (u, c))).ToList();
                var start = units[0].u.StartPts;
                var length = units[^1].u.EndPts - start;
                var second = (long)frame * (disc.Pal ? 25 : 30); // a DVD second is 25 or 30 frames, as in its BCD times: 1.001 s in NTSC
                var unit = 1;
                while (length / second / unit > 2047) unit++;
                var entries = new List<uint>();
                for (var k = 1; k * unit * second < length; k++)
                {
                    var time = start + k * unit * second;
                    var hit = units.FindLast(x => x.u.StartPts <= time);
                    entries.Add((uint)(hit.c.First + hit.u.Sector - hit.c.Units[0].Sector));
                }
                var m = new byte[4 + 4 * entries.Count];
                m[0] = (byte)unit;
                BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)entries.Count);
                for (var i = 0; i < entries.Count; i++) BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4 + 4 * i), entries[i]);
                return m;
            }).ToList();
            var head = 8 + 4 * maps.Count;
            var b = new byte[head + maps.Sum(m => m.Length)];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)maps.Count);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)(b.Length - 1));
            var at = head;
            for (var i = 0; i < maps.Count; i++) { BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + 4 * i), (uint)at); maps[i].CopyTo(b, at); at += maps[i].Length; }
            tables.Add((0xD4, b));
        }
        if (menus.Count > 0) { tables.Add((0xD8, CellTable(menus))); tables.Add((0xDC, UnitMap(menus))); }
        tables.Add((0xE0, CellTable(titles)));
        tables.Add((0xE4, UnitMap(titles)));

        var sectors = 1 + tables.Sum(t => (t.Data.Length + Sector - 1) / Sector);
        var ifo = new byte[sectors * Sector];
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(ifo, 0);
        var span = ifo.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[0x0C..], (uint)(2 * sectors + menuSectors + titleSectors - 1));
        BinaryPrimitives.WriteUInt32BigEndian(span[0x1C..], (uint)(sectors - 1));
        BinaryPrimitives.WriteUInt16BigEndian(span[0x20..], 0x0011);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x80..], 0x7FF);
        if (menus.Count > 0) BinaryPrimitives.WriteUInt32BigEndian(span[0xC0..], (uint)sectors);
        BinaryPrimitives.WriteUInt32BigEndian(span[0xC4..], (uint)(sectors + menuSectors));
        var next = 1;
        foreach (var (pointer, data) in tables)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[pointer..], (uint)next);
            data.CopyTo(ifo, next * Sector);
            next += (data.Length + Sector - 1) / Sector;
        }
        if (menus.Count > 0)
        {
            VideoAttributes(pal, true).CopyTo(ifo, 0x100);
            ifo[0x103] = 1;
            AudioAttributes(new AudioStream(2, "en")).CopyTo(ifo, 0x104);
            ifo[0x155] = 1;
            SubAttributes("en").CopyTo(ifo, 0x156);
        }
        VideoAttributes(pal, disc.Wide).CopyTo(ifo, 0x200);
        ifo[0x203] = (byte)disc.Audio.Count;
        for (var a = 0; a < disc.Audio.Count; a++) AudioAttributes(disc.Audio[a]).CopyTo(ifo, 0x204 + 8 * a);
        ifo[0x255] = (byte)disc.Subtitles.Count;
        for (var s = 0; s < disc.Subtitles.Count; s++) SubAttributes(disc.Subtitles[s]).CopyTo(ifo, 0x256 + 6 * s);
        return ifo;
    }

    static byte[] CellTable(List<Placed> chains)
    {
        var cells = chains.SelectMany(c => c.Cells).OrderBy(c => c.Vob).ThenBy(c => c.Id).ToList();
        var b = new byte[8 + 12 * cells.Count];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)cells.Select(c => c.Vob).Distinct().Count());
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)(b.Length - 1));
        for (var i = 0; i < cells.Count; i++)
        {
            var e = b.AsSpan(8 + 12 * i);
            BinaryPrimitives.WriteUInt16BigEndian(e, (ushort)cells[i].Vob);
            e[2] = (byte)cells[i].Id;
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], (uint)cells[i].First);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)cells[i].LastEnd);
        }
        return b;
    }

    static byte[] UnitMap(List<Placed> chains)
    {
        var starts = chains.SelectMany(c => c.Cells).SelectMany(c => c.Units.Select(u => c.First + u.Sector - c.Units[0].Sector)).Distinct().Order().ToList();
        var b = new byte[4 + 4 * starts.Count];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)(b.Length - 1));
        for (var i = 0; i < starts.Count; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4 + 4 * i), (uint)starts[i]);
        return b;
    }

    // ---- the video manager's IFO

    static byte[] VmgIfo(DvdDisc disc, List<Placed> titles, byte[] vts)
    {
        var fp = Pgc(new Chain(null, [], disc.FirstPlay, []), [], disc.Pal, 0, 0, false);
        if (0x400 + fp.Length > Sector) throw new InvalidOperationException("too many first-play commands");
        var srpt = new byte[8 + 12 * titles.Count];
        BinaryPrimitives.WriteUInt16BigEndian(srpt, (ushort)titles.Count);
        BinaryPrimitives.WriteUInt32BigEndian(srpt.AsSpan(4), (uint)(srpt.Length - 1));
        // the Title key (and Pop-up, which a DVD player sends as Title) calls the manager's title menu: without one it does nothing. One with no picture
        // that jumps to the root menu of the title set, as the Menu key shows it
        byte[]? titleMenu = null;
        if (disc.Menus.Count > 0)
        {
            var lu = PgcTable([Pgc(new Chain(null, [], [Vm.JumpRootMenu()], []), [], disc.Pal, 0, 0, true)], _ => 0x82000000u);
            titleMenu = new byte[16 + lu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(titleMenu, 1);
            BinaryPrimitives.WriteUInt32BigEndian(titleMenu.AsSpan(4), (uint)(titleMenu.Length - 1));
            titleMenu[8] = (byte)'e'; titleMenu[9] = (byte)'n'; titleMenu[11] = 0x80; // the title menu exists
            BinaryPrimitives.WriteUInt32BigEndian(titleMenu.AsSpan(12), 16);
            lu.CopyTo(titleMenu, 16);
        }
        var ifoSectors = titleMenu == null ? 3 : 4;
        for (var t = 0; t < titles.Count; t++)
        {
            var e = srpt.AsSpan(8 + 12 * t);
            e[0] = 0x3C; // one sequential chain; jumps and calls anywhere
            e[1] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(e[2..], (ushort)titles[t].Cells.Count);
            e[6] = 1;
            e[7] = (byte)(t + 1);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)(2 * ifoSectors)); // the title set starts after this IFO and its backup
        }
        var atrt = new byte[12 + 8 + 0x300];
        BinaryPrimitives.WriteUInt16BigEndian(atrt, 1);
        BinaryPrimitives.WriteUInt32BigEndian(atrt.AsSpan(4), (uint)(atrt.Length - 1));
        BinaryPrimitives.WriteUInt32BigEndian(atrt.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt32BigEndian(atrt.AsSpan(12), 8 + 0x300 - 1);
        Array.Copy(vts, 0x100, atrt, 20, 0x300);

        var ifo = new byte[ifoSectors * Sector];
        var span = ifo.AsSpan();
        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(ifo, 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x0C..], (uint)(2 * ifoSectors - 1)); // the manager: its IFO and the backup, no menu picture
        BinaryPrimitives.WriteUInt32BigEndian(span[0x1C..], (uint)(ifoSectors - 1));
        BinaryPrimitives.WriteUInt16BigEndian(span[0x20..], 0x0011);
        BinaryPrimitives.WriteUInt16BigEndian(span[0x26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[0x28..], 1);
        ifo[0x2A] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(span[0x3E..], 1);
        Encoding.ASCII.GetBytes("BurnSmith").CopyTo(ifo, 0x40);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x80..], (uint)(0x400 + fp.Length - 1));
        BinaryPrimitives.WriteUInt32BigEndian(span[0x84..], 0x400);
        BinaryPrimitives.WriteUInt32BigEndian(span[0xC4..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[0xD0..], 2);
        fp.CopyTo(ifo, 0x400);
        srpt.CopyTo(ifo, Sector);
        atrt.CopyTo(ifo, 2 * Sector);
        if (titleMenu != null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[0xC8..], 3);
            titleMenu.CopyTo(ifo, 3 * Sector);
            Array.Copy(vts, 0x100, ifo, 0x100, 0x5C); // its attributes: those of the title set's menus
        }
        return ifo;
    }
}
