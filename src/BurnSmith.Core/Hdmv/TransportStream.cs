// Ported to C# from bdmvkit (modules formats.py, m2ts.py and the CRC of igs.py): https://github.com/kajisho5/bdmvkit, commit 5789076 (v0.7.0).
// Copyright (c) kajisho5 (Shogo Kajiyama). GNU Lesser General Public License v3.0 or later (COPYING.LESSER upstream);
// redistributed here under the GNU GPL v3 or later, with the LGPL's additional permissions removed, as section 7 of the GPL v3 allows. See THIRD-PARTY-NOTICES.md.
namespace BurnSmith.Core.Hdmv;

/// <summary>Numbers of the BD-ROM format the HDMV writer uses: stream PIDs, coding types, video format and frame-rate codes.</summary>
public static class HdmvFormats
{
    public const int Width = 1920, Height = 1080;
    public const int VideoPid = 0x1011, AudioPid = 0x1100, PgPid = 0x1200, IgPid = 0x1400, PmtPid = 0x0100;
    public const byte CodingH264 = 0x1B, CodingAc3 = 0x81, CodingPg = 0x90, CodingIg = 0x91;
    /// <summary>ts_recording_rate: the 48 Mbit/s maximum, in bytes per second.</summary>
    public const int TsRate = 48_000_000 / 8;
    public const int MaxVideoBps = 40_000_000, VbvBuffer = 30_000_000;

    /// <summary>(video_format, frame_rate) codes of a 1080 disc: 25 and 29.97 fps are coded as progressive frames in an interlaced stream, the format has no 1080p there.</summary>
    public static (int Format, int Rate)? VideoCodes(int num, int den, bool hd720 = false)
    {
        if (hd720)
        {
            // 25 and 29.97 fps pictures go on a 720p disc at twice their rate
            if ((num, den) is (25, 1) or (30000, 1001)) num *= 2;
            return (num, den) switch { (24000, 1001) => (5, 1), (50, 1) => (5, 6), (60000, 1001) => (5, 7), _ => null };
        }
        return (num, den) switch { (24000, 1001) => (6, 1), (25, 1) => (4, 3), (30000, 1001) => (4, 4), _ => null };
    }
}

/// <summary>User operation masks (UO_mask_table, 64 bits, bit 0 = most significant): the remote keys a menu or title forbids.</summary>
public static class UoMask
{
    /// <summary>bdmvkit's choice: nothing forbidden.</summary>
    public const ulong None = 0;

    /// <summary>
    /// The page mask found in the decoded reference menu (an amateur-authored disc, so "what one real menu does", not a certified value):
    /// forbids chapter / time search, next / previous, pause on, still off, forward / backward play, audio and angle change and the
    /// pop-up, subtitle and secondary-stream keys. The menu-call, title-search and button keys stay allowed.
    /// </summary>
    public const ulong MenuReference = 0x3cb805ff40000000UL;

    /// <summary>bit positions (from the most significant) each key turns off; same table as bdmvkit's uops.py</summary>
    static readonly Dictionary<string, int[]> Keys = new()
    {
        ["menu"] = [0, 24], ["title"] = [0], ["search"] = [1, 2, 3], ["next"] = [4], ["prev"] = [5], ["stop"] = [7], ["pause"] = [8],
        ["fast_forward"] = [11], ["rewind"] = [12], ["audio"] = [21], ["subtitle"] = [26, 27],
    };

    public static ulong Mask(IEnumerable<string> names)
    {
        ulong m = 0;
        foreach (var n in names) foreach (var bit in Keys[n]) m |= 1UL << (63 - bit);
        return m;
    }
}

/// <summary>The scan of a BDAV transport stream: its key frames and the span of its pictures (for the clip information).</summary>
/// <param name="Packets">192-byte source packets</param>
/// <param name="FirstPts">90 kHz, first picture shown</param>
/// <param name="EndPts">90 kHz, end of the last picture</param>
/// <param name="Entries">(pts, source packet number, size in bytes) of each key frame</param>
public sealed record ClipScan(int Packets, long FirstPts, long EndPts, int PcrPid, IReadOnlyList<(long Pts, int Spn, int Size)> Entries);

/// <summary>Reads and patches .m2ts files (BDAV, 192-byte source packets).</summary>
public static class TransportStream
{
    const int Packet = 192;

    static long Pts(byte[] b, int i) =>
        (long)((b[i] >> 1) & 7) << 30 | (long)b[i + 1] << 22 | (long)(b[i + 2] >> 1) << 15 | (long)b[i + 3] << 7 | (long)(b[i + 4] >> 1);

    /// <summary>
    /// Where the key frames are and the span of the pictures. Every picture is one PES packet (ffmpeg, tsMuxeR); key frames carry the
    /// random-access flag. Port of bdmvkit's scan_m2ts.
    /// </summary>
    public static ClipScan Scan(string path, int videoPid = HdmvFormats.VideoPid)
    {
        var entries = new List<(long Pts, int Spn, int Size)>();
        var open = -1;                                      // entry whose size is not known yet
        var ptsAll = new List<long>();
        var pcrPid = -1;
        var spn = 0;
        using var f = File.OpenRead(path);
        var buf = new byte[Packet * 8192];
        int got;
        while ((got = f.Read(buf, 0, buf.Length)) > 0)
        {
            while (got % Packet != 0)                        // a short read in the middle of a packet
            {
                var more = f.Read(buf, got, Packet - got % Packet);
                if (more <= 0) throw new InvalidDataException($"{path} is not a BDAV stream (length is not 192 x n)");
                got += more;
            }
            for (var off = 0; off < got; off += Packet)
            {
                var p = off + 4;
                var b1 = buf[p + 1];
                if ((b1 & 0x40) == 0) continue;
                if (buf[p] != 0x47) throw new InvalidDataException($"{path}: lost sync at packet {spn + off / Packet}");
                if (((b1 & 0x1F) << 8 | buf[p + 2]) != videoPid) continue;
                var afc = buf[p + 3] >> 4 & 3;
                var q = p + 4;
                var rai = false;
                if ((afc & 2) != 0)
                {
                    var alen = buf[q];
                    if (alen != 0)
                    {
                        rai = (buf[q + 1] & 0x40) != 0;
                        if ((buf[q + 1] & 0x10) != 0) pcrPid = videoPid;
                    }
                    q += 1 + alen;
                }
                // (a PES header must start in this packet with its PTS: 14 bytes)
                if ((afc & 1) == 0 || q + 14 > p + 188 || buf[q] != 0 || buf[q + 1] != 0 || buf[q + 2] != 1 || (buf[q + 7] & 0x80) == 0) continue;
                var pts = Pts(buf, q + 9);
                var n = spn + off / Packet;
                if (open >= 0) { entries[open] = entries[open] with { Size = (n - entries[open].Spn) * Packet }; open = -1; }
                ptsAll.Add(pts);
                if (rai) { entries.Add((pts, n, -1)); open = entries.Count - 1; }
            }
            spn += got / Packet;
        }
        if (ptsAll.Count == 0 || entries.Count == 0) throw new InvalidDataException($"{path}: no pictures with key frames found");
        if (open >= 0) entries[open] = entries[open] with { Size = (spn - entries[open].Spn) * Packet };
        ptsAll.Sort();
        var step = ptsAll.Count > 1 ? ptsAll[1] - ptsAll[0] : 3003;
        return new ClipScan(spn, ptsAll[0], ptsAll[^1] + step, pcrPid < 0 ? videoPid : pcrPid, entries);
    }

    /// <summary>One PES packet of an elementary stream, reassembled from its TS packets.</summary>
    /// <param name="Pts">90 kHz, null when the header has none</param>
    /// <param name="Dts">90 kHz, null when the header has none (decoding time = presentation time)</param>
    public sealed record PesPacket(long? Pts, long? Dts, byte[] Payload);

    /// <summary>The PES packets of one PID, in stream order (for tests and for checking what ffmpeg wrote).</summary>
    public static List<PesPacket> ReadPes(string path, int pid)
    {
        var result = new List<PesPacket>();
        var cur = new List<byte>();
        void Flush()
        {
            if (cur.Count < 9) { cur.Clear(); return; }
            var e = cur.ToArray();
            var flags = e[7] >> 6;
            long? pts = (flags & 2) != 0 ? Pts(e, 9) : null;
            long? dts = flags == 3 ? Pts(e, 14) : null;
            result.Add(new PesPacket(pts, dts, e[(9 + e[8])..]));
            cur.Clear();
        }
        using var f = File.OpenRead(path);
        var pkt = new byte[Packet];
        while (f.Read(pkt, 0, Packet) == Packet)
        {
            if (((pkt[5] & 0x1F) << 8 | pkt[6]) != pid) continue;
            var afc = pkt[7] >> 4 & 3;
            var off = 8;
            if ((afc & 2) != 0) off += 1 + pkt[8];
            if ((afc & 1) == 0) continue;
            if ((pkt[5] & 0x40) != 0) Flush();
            for (var i = off; i < Packet; i++) cur.Add(pkt[i]);
        }
        Flush();
        return result;
    }

    /// <summary>The (type, body) segments of an IG / PG PES payload.</summary>
    public static List<(byte Type, byte[] Body)> Segments(byte[] payload)
    {
        var list = new List<(byte, byte[])>();
        for (var i = 0; i + 3 <= payload.Length;)
        {
            var len = payload[i + 1] << 8 | payload[i + 2];
            list.Add((payload[i], payload[(i + 3)..(i + 3 + len)]));
            i += 3 + len;
        }
        return list;
    }

    /// <summary>{PID: stream type} of the elementary streams the first program map table of the clip names.</summary>
    public static Dictionary<int, byte> StreamPids(string path, int pmtPid = HdmvFormats.PmtPid)
    {
        using var f = File.OpenRead(path);
        var pkt = new byte[Packet];
        while (f.Read(pkt, 0, Packet) == Packet)
        {
            const int p = 4;
            if (((pkt[p + 1] & 0x1F) << 8 | pkt[p + 2]) != pmtPid || (pkt[p + 1] & 0x40) == 0) continue;
            var q = p + 4;
            if ((pkt[p + 3] & 0x20) != 0) q += 1 + pkt[q];
            q += 1 + pkt[q];                                 // pointer field
            var end = q + 3 + ((pkt[q + 1] & 0x0F) << 8 | pkt[q + 2]) - 4;
            var e = q + 12 + ((pkt[q + 10] & 0x0F) << 8 | pkt[q + 11]);
            var pids = new Dictionary<int, byte>();
            while (e < Math.Min(end, Packet))
            {
                pids[(pkt[e + 1] & 0x1F) << 8 | pkt[e + 2]] = pkt[e];
                e += 5 + ((pkt[e + 3] & 0x0F) << 8 | pkt[e + 4]);
            }
            return pids;
        }
        throw new InvalidDataException($"{path}: no program map table (PID {pmtPid:X4})");
    }

    /// <summary>
    /// Gives the elementary streams their stream types (<paramref name="types"/>: PID to type) in every program map table of the clip, in
    /// place, with the section CRC made again. Reads a piece at a time and only rewrites PMT packets, so a long clip costs no more memory
    /// than a short one. ffmpeg muxes an IG stream as a PGS stream (0x90): this marks it 0x91, Interactive Graphics.
    /// </summary>
    public static void SetStreamTypes(string path, IReadOnlyDictionary<int, byte> types, int pmtPid = HdmvFormats.PmtPid)
    {
        using var f = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        var buf = new byte[Packet * 4096];
        long pos = 0;
        int got;
        while ((got = f.Read(buf, 0, buf.Length)) > 0)
        {
            while (got % Packet != 0)
            {
                var more = f.Read(buf, got, Packet - got % Packet);
                if (more <= 0) throw new InvalidDataException($"{path} is not a BDAV stream (length is not 192 x n)");
                got += more;
            }
            for (var off = 0; off < got; off += Packet)
            {
                var b1 = buf[off + 5];
                if (buf[off + 4] != 0x47 || (b1 & 0x40) == 0 || ((b1 & 0x1F) << 8 | buf[off + 6]) != pmtPid) continue;
                var pkt = buf.AsSpan(off, Packet).ToArray();
                if (!Retype(pkt, types)) continue;
                f.Seek(pos + off, SeekOrigin.Begin);
                f.Write(pkt, 0, Packet);
                f.Seek(pos + got, SeekOrigin.Begin);
            }
            pos += got;
        }
    }

    /// <summary>Sets the stream types in one PMT packet; whether it changed.</summary>
    static bool Retype(byte[] pkt, IReadOnlyDictionary<int, byte> types)
    {
        const int p = 4;
        var q = p + 4;
        if ((pkt[p + 3] & 0x20) != 0) q += 1 + pkt[q];
        q += 1 + pkt[q];                                     // pointer field
        if (pkt[q] != 2) return false;                       // (not a PMT section)
        var end = q + 3 + ((pkt[q + 1] & 0x0F) << 8 | pkt[q + 2]) - 4;
        if (end + 4 > pkt.Length) throw new InvalidDataException("a program map table runs over its packet");
        var e = q + 12 + ((pkt[q + 10] & 0x0F) << 8 | pkt[q + 11]);
        var changed = false;
        while (e < end)
        {
            var pid = (pkt[e + 1] & 0x1F) << 8 | pkt[e + 2];
            if (types.TryGetValue(pid, out var t) && pkt[e] != t) { pkt[e] = t; changed = true; }
            e += 5 + ((pkt[e + 3] & 0x0F) << 8 | pkt[e + 4]);
        }
        if (changed)
        {
            var crc = Crc32(pkt.AsSpan(q, end - q));
            pkt[end] = (byte)(crc >> 24); pkt[end + 1] = (byte)(crc >> 16); pkt[end + 2] = (byte)(crc >> 8); pkt[end + 3] = (byte)crc;
        }
        return changed;
    }

    /// <summary>MPEG-2 section CRC (polynomial 0x04C11DB7, no reflection, initial value all ones).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++) crc = (crc & 0x80000000) != 0 ? crc << 1 ^ 0x04C11DB7u : crc << 1;
        }
        return crc;
    }
}
