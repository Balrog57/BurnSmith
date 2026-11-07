namespace BurnSmith.Core.DvdVideo;

/// <summary>
/// What a DVD program stream from ffmpeg holds, read sector by sector: each video object unit (VOBU) starts on a navigation pack, which ffmpeg leaves
/// empty for the author to fill. Read here: where each unit starts and ends, the time it shows, where its first three reference pictures end,
/// and the first packet of each audio and subpicture stream in it.
/// </summary>
public static class VobScan
{
    public sealed class Vobu
    {
        public long Sector;           // in the file
        public long Sectors;
        public long Scr;
        public long StartPts = -1, EndPts;
        public int[] RefEnds = [];    // relative to the unit's first sector
        public readonly int[] Audio = new int[8];    // the sync offset of each AC-3 stream (bit 15: backwards), -1 = no such stream
        public readonly int[] Sub = new int[32];     // relative, -1 = none in this unit
        public Vobu() { Array.Fill(Audio, -1); Array.Fill(Sub, -1); }
    }

    /// <summary>The units of <paramref name="file"/>; <paramref name="frame"/> is one frame in 90 kHz ticks (3600 PAL, 3003 NTSC).</summary>
    public static List<Vobu> Scan(string file, int frame)
    {
        var units = new List<Vobu>();
        var s = new byte[2048];
        using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        if (f.Length % 2048 != 0) throw new InvalidDataException($"{Path.GetFileName(file)}: not a DVD program stream (size not a multiple of 2048)");
        Vobu? u = null;
        // the video elementary stream, byte by byte across packets: start codes, and the sector of the last bytes seen
        uint window = 0xFFFFFFFF;
        var sectors = new long[5];
        var pictures = new List<(long Start, long End, int Type, int Tref, long Pts)>();
        var audio = Enumerable.Range(0, 8).Select(_ => new List<(long Sector, long Pts)>()).ToArray();
        int pendingType = -1; // bytes still to read after a picture start code (2), to get its number and type
        int headerByte = 0, tref = 0;
        long pesPts = -1;

        void EndPicture(long endSector)
        {
            if (pictures.Count > 0 && pictures[^1].End < 0) pictures[^1] = pictures[^1] with { End = endSector };
        }

        void CloseUnit()
        {
            if (u == null) return;
            var mine = pictures;
            var refs = mine.Where(p => p.Type is 1 or 2).Take(3).Select(p => (int)((p.End < 0 ? u.Sector + u.Sectors - 1 : p.End) - u.Sector)).ToList();
            while (refs.Count is > 0 and < 3) refs.Add(refs[^1]);
            u.RefEnds = [.. refs];
            if (mine.Count > 0 && mine[0].Pts >= 0) u.StartPts = mine[0].Pts - (long)mine[0].Tref * frame;
            else if (units.Count > 1) u.StartPts = units[^2].EndPts;
            else u.StartPts = 0;
            u.EndPts = u.StartPts + (long)mine.Count * frame;
        }

        for (long n = 0; n < f.Length / 2048; n++)
        {
            f.ReadExactly(s);
            if (s[0] != 0 || s[1] != 0 || s[2] != 1 || s[3] != 0xBA) throw new InvalidDataException($"{Path.GetFileName(file)}: sector {n} is no MPEG-2 pack");
            var p = 14 + (s[13] & 7);
            if (s[p + 3] == 0xBB) // a navigation pack: a new unit
            {
                if (s[0x26] != 0 || s[0x27] != 0 || s[0x28] != 1 || s[0x29] != 0xBF || s[0x2C] != 0 || s[0x406] != 1)
                    throw new InvalidDataException($"{Path.GetFileName(file)}: sector {n} has a system header but no navigation packets where a DVD has them");
                EndPicture(n - 1); // a unit never shares a sector with the one before
                if (u != null) { u.Sectors = n - u.Sector; CloseUnit(); pictures = []; }
                u = new Vobu { Sector = n, Scr = Scr(s) };
                units.Add(u);
                continue;
            }
            if (u == null) throw new InvalidDataException($"{Path.GetFileName(file)}: does not start with a navigation pack");
            var rel = (int)(n - u.Sector);
            for (; p + 6 <= 2048 && s[p] == 0 && s[p + 1] == 0 && s[p + 2] == 1; p += 6 + (s[p + 4] << 8 | s[p + 5]))
            {
                var id = s[p + 3];
                var end = Math.Min(2048, p + 6 + (s[p + 4] << 8 | s[p + 5]));
                if (id == 0xBD)
                {
                    var sub = s[p + 9 + s[p + 8]];
                    if (sub is >= 0x80 and <= 0x87 && (s[p + 7] & 0x80) != 0) audio[sub - 0x80].Add((n, Pts(s, p + 9)));
                    else if (sub is >= 0x20 and <= 0x3F && u.Sub[sub - 0x20] < 0) u.Sub[sub - 0x20] = rel;
                    continue;
                }
                if (id != 0xE0) continue;
                pesPts = (s[p + 7] & 0x80) != 0 ? Pts(s, p + 9) : -1;
                for (var i = p + 9 + s[p + 8]; i < end; i++)
                {
                    var b = s[i];
                    sectors[0] = sectors[1]; sectors[1] = sectors[2]; sectors[2] = sectors[3]; sectors[3] = sectors[4]; sectors[4] = n;
                    if (pendingType >= 0)
                    {
                        if (headerByte == 0) { tref = b << 2; headerByte = 1; }
                        else
                        {
                            tref |= b >> 6;
                            pictures[^1] = pictures[^1] with { Type = b >> 3 & 7, Tref = tref };
                            pendingType = -1;
                        }
                    }
                    window = window << 8 | b;
                    if ((window & 0xFFFFFF00) != 0x00000100) continue;
                    var code = b;
                    if (code is 0x00 or 0xB3 or 0xB7 or 0xB8) EndPicture(sectors[0]); // the byte before the start code
                    if (code != 0x00) continue;
                    pictures.Add((sectors[1], -1, 0, 0, pesPts));
                    pesPts = -1; // the PTS is the first picture's that starts in the packet
                    pendingType = 0; headerByte = 0;
                }
            }
        }
        if (u == null) throw new InvalidDataException($"{Path.GetFileName(file)}: no navigation pack");
        EndPicture(f.Length / 2048 - 1);
        u.Sectors = f.Length / 2048 - u.Sector;
        CloseUnit();
        // audio: the packet that holds the sound of the unit's first picture (the last one starting at or before it), often after the unit's own sectors
        foreach (var unit in units)
            for (var a = 0; a < 8; a++)
            {
                var k = audio[a].FindLastIndex(x => x.Pts <= unit.StartPts);
                if (k < 0) k = audio[a].Count > 0 ? 0 : -1;
                if (k < 0) continue;
                var offset = audio[a][k].Sector - unit.Sector;
                unit.Audio[a] = offset >= 0 ? (int)Math.Min(0x3FFE, offset) : 0x8000 | (int)Math.Min(0x3FFE, -offset);
            }
        return units;
    }

    static long Scr(byte[] s) =>
        (long)(s[4] >> 3 & 7) << 30 | (long)(s[4] & 3) << 28 | (long)s[5] << 20 | (long)(s[6] >> 3) << 15 | (long)(s[6] & 3) << 13 | (long)s[7] << 5 | (long)(s[8] >> 3);

    static long Pts(byte[] s, int i) =>
        (long)(s[i] >> 1 & 7) << 30 | (long)s[i + 1] << 22 | (long)(s[i + 2] >> 1) << 15 | (long)s[i + 3] << 7 | (long)(s[i + 4] >> 1);
}
