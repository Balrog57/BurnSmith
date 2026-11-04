// Ported to C# from bdmvkit (modules clip.py and playlist.py): https://github.com/kajisho5/bdmvkit, commit 5789076 (v0.7.0).
// Copyright (c) kajisho5 (Shogo Kajiyama). GNU Lesser General Public License v3.0 or later (COPYING.LESSER upstream);
// redistributed here under the GNU GPL v3 or later, with the LGPL's additional permissions removed, as section 7 of the GPL v3 allows. See THIRD-PARTY-NOTICES.md.
using System.Text;

namespace BurnSmith.Core.Hdmv;

/// <summary>A sound track of a menu clip or playlist: coding (0x81 AC-3 ...), channel count, ISO 639-2 language, 48 kHz.</summary>
public readonly record struct HdmvAudio(byte Coding, int Channels, string Lang);

/// <summary>One play item of a playlist: a clip played from <see cref="In"/> to <see cref="Out"/> (45 kHz, on the clip's time line).</summary>
/// <param name="Still">0 = no, 2 = hold the last picture (a still menu), 1 = hold it for <paramref name="StillTime"/> seconds</param>
/// <param name="Uo">the item's UO mask</param>
public sealed record PlayItem(string Clip, long In, long Out, int VideoFormat, int FrameRate, IReadOnlyList<HdmvAudio> Audio,
                              IReadOnlyList<string> Pg, bool Ig, int Still = 0, int StillTime = 0, ulong Uo = 0);

/// <summary>Writers of the clip information (.clpi) and playlist (.mpls) of a menu clip.</summary>
public static class ClipFiles
{
    static byte[] Pad(byte[] data, int n) => data.Length % n == 0 ? data : [.. data, .. new byte[n - data.Length % n]];

    static byte[] Bytes(Action<BinaryWriterBE> f) { var w = new BinaryWriterBE(); f(w); return w.ToArray(); }

    static byte[] WithLength(byte[] body) => Bytes(w => { w.U32(body.Length); w.Bytes(body); });

    static byte[] PgAttr(string lang, byte coding = HdmvFormats.CodingPg) => [coding, .. Encoding.ASCII.GetBytes(lang), 0];

    static byte[] AudioAttr(HdmvAudio a) =>
        [a.Coding, (byte)((a.Channels > 2 ? 6 : 3) << 4 | 1), .. Encoding.ASCII.GetBytes(a.Lang)]; // sampling_frequency 1 = 48 kHz

    /// <summary>
    /// EP_map() of the video stream: a coarse entry whenever the upper PTS or packet-number bits change, a fine entry per key frame.
    /// </summary>
    static byte[] EpMap(IReadOnlyList<(long Pts, int Spn, int Size)> entries)
    {
        var coarse = new List<(int Ref, long Pts, int Spn)>();
        var fine = new List<uint>();
        (long, long)? last = null;
        for (var i = 0; i < entries.Count; i++)
        {
            var (pts, spn, size) = entries[i];
            var key = (pts >> 19, (long)spn & 0xFFFE0000);
            if (last != key) { coarse.Add((i, pts >> 19 & 0x3FFF, spn)); last = key; }
            // I_end_position_offset: how many bytes the key picture takes, in steps
            int[] limits = [131072, 262144, 393216, 589824, 917504, 1310720];
            var end = 7;
            for (var k = 0; k < limits.Length; k++) if (size < limits[k]) { end = k + 1; break; }
            fine.Add((uint)end << 28 | (uint)(pts >> 9 & 0x7FF) << 17 | (uint)(spn & 0x1FFFF));
        }
        var table = Bytes(w => { foreach (var (r, cpts, cspn) in coarse) { w.U32((long)r << 14 | cpts); w.U32(cspn); } });
        var one = Bytes(w => { w.U32(4 + table.Length); w.Bytes(table); foreach (var v in fine) w.U32(v); });
        // stream_PID, 10 reserved bits + EP_stream_type 1 (video), the coarse and fine counts (16 + 18 bits), then where the stream's map starts
        var counts = 1UL << 34 | (ulong)(uint)coarse.Count << 18 | (ulong)(uint)fine.Count;
        return Bytes(w =>
        {
            w.U8(0); w.U8(1); w.U16(HdmvFormats.VideoPid);
            for (var s = 40; s >= 0; s -= 8) w.U8((int)(counts >> s & 0xFF));
            w.U32(2 + 2 + 6 + 4);                              // (the address counts from the start of EP_map)
            w.Bytes(one);
        });
    }

    /// <summary>
    /// Clip information of one .m2ts: its streams, time span and EP map. <paramref name="ig"/>: the clip carries menu buttons (PID 0x1400).
    /// </summary>
    public static byte[] Clpi(ClipScan scan, int videoFormat, int frameRate, IReadOnlyList<HdmvAudio> audio, IReadOnlyList<string>? pg = null, bool ig = false)
    {
        pg ??= [];
        var tsType = new byte[30]; tsType[0] = 0x80; Encoding.ASCII.GetBytes("HDMV").CopyTo(tsType, 1);
        var info = WithLength(Bytes(w =>
        {
            w.U16(0); w.U8(1); w.U8(1); w.U32(0); w.U32(HdmvFormats.TsRate); w.U32(scan.Packets);
            w.Zero(128); w.U16(tsType.Length); w.Bytes(tsType);
        }));
        var start = scan.FirstPts / 2;
        var end = scan.EndPts / 2;
        var seq = WithLength(Bytes(w =>
        {
            w.U8(0); w.U8(1); w.U32(0); w.U8(1); w.U8(0); w.U16(scan.PcrPid); w.U32(0); w.U32(start); w.U32(end);
        }));
        var isrc = Encoding.ASCII.GetBytes("000000000000");
        var streams = new BinaryWriterBE();
        void Stream(int pid, byte[] attr)
        {
            var coding = new byte[attr.Length + isrc.Length + 4];
            attr.CopyTo(coding, 0); isrc.CopyTo(coding, attr.Length);
            streams.U16(pid); streams.U8(coding.Length); streams.Bytes(coding);
        }
        Stream(HdmvFormats.VideoPid, [HdmvFormats.CodingH264, (byte)(videoFormat << 4 | frameRate), 3 << 4, 0, 0]);
        for (var k = 0; k < audio.Count; k++) Stream(HdmvFormats.AudioPid + k, AudioAttr(audio[k]));
        for (var k = 0; k < pg.Count; k++) Stream(HdmvFormats.PgPid + k, PgAttr(pg[k]));
        if (ig) Stream(HdmvFormats.IgPid, PgAttr("und", HdmvFormats.CodingIg));
        var prog = WithLength(Bytes(w =>
        {
            w.U8(0); w.U8(1); w.U32(0); w.U16(HdmvFormats.PmtPid); w.U8(1 + audio.Count + pg.Count + (ig ? 1 : 0)); w.U8(0);
            w.Bytes(streams.ToArray());
        }));
        var cpi = WithLength(Bytes(w => { w.U16(1); w.Bytes(EpMap(scan.Entries)); })); // 12 reserved bits, CPI_type 1 (EP map)
        byte[][] parts = [info, seq, prog, cpi, [0, 0, 0, 0]];                        // (the last one: no clip marks)
        var o = new BinaryWriterBE();
        o.Str("HDMV0200", 8);
        var pos = 40;
        var padded = parts.Select(p => Pad(p, 4)).ToList();
        for (var i = 1; i < padded.Count; i++) { pos += padded[i - 1].Length; o.U32(pos); }
        o.U32(0); o.Zero(12);
        foreach (var p in padded) o.Bytes(p);
        return o.ToArray();
    }

    /// <summary>
    /// A playlist. <paramref name="marks"/>: chapter marks as (item index, time in 45 kHz). <paramref name="uo"/>: the whole playlist's UO mask.
    /// </summary>
    public static byte[] Mpls(IReadOnlyList<PlayItem> items, IReadOnlyList<(int Item, long Time)> marks, ulong uo = 0)
    {
        var app = Bytes(w => { w.U32(14); w.U8(0); w.U8(1); w.U16(0); U64(w, uo); w.U16(0); });
        var body = new BinaryWriterBE();
        foreach (var it in items)
        {
            var stn = new BinaryWriterBE();
            stn.U16(0);
            foreach (var b in new[] { 1, it.Audio.Count, it.Pg.Count, it.Ig ? 1 : 0, 0, 0, 0, 0 }) stn.U8(b);
            stn.Zero(4);
            void Entry(int pid, byte[] attr)
            {
                stn.U8(1 + 2 + 6); stn.U8(1); stn.U16(pid); stn.Zero(6);   // stream_entry: type 1, PID, 6 reserved
                stn.U8(attr.Length); stn.Bytes(attr);
            }
            Entry(HdmvFormats.VideoPid, [HdmvFormats.CodingH264, (byte)(it.VideoFormat << 4 | it.FrameRate), 0, 0, 0]);
            for (var k = 0; k < it.Audio.Count; k++) Entry(HdmvFormats.AudioPid + k, AudioAttr(it.Audio[k]));
            for (var k = 0; k < it.Pg.Count; k++) Entry(HdmvFormats.PgPid + k, PgAttr(it.Pg[k]));
            if (it.Ig) Entry(HdmvFormats.IgPid, PgAttr("und", HdmvFormats.CodingIg));
            var stnBytes = stn.ToArray();
            var item = new BinaryWriterBE();
            item.Str(it.Clip, 5); item.Str("M2TS", 4);
            item.U16(1); item.U8(0);                                    // connection_condition 1 (not seamless); STC id 0
            item.U32(it.In); item.U32(it.Out); U64(item, it.Uo);
            item.U8(0); item.U8(it.Still); item.U16(it.StillTime);
            item.U16(stnBytes.Length); item.Bytes(stnBytes);
            body.U16(item.Length); body.Bytes(item.ToArray());
        }
        var playlist = WithLength(Bytes(w => { w.U16(0); w.U16(items.Count); w.U16(0); w.Bytes(body.ToArray()); }));
        var mark = WithLength(Bytes(w =>
        {
            w.U16(marks.Count);
            foreach (var (r, t) in marks) { w.U8(0); w.U8(1); w.U16(r); w.U32(t); w.U16(0xFFFF); w.U32(0); }
        }));
        var listPos = 40 + app.Length;
        var markPos = listPos + playlist.Length;
        return Bytes(w =>
        {
            w.Str("MPLS0200", 8); w.U32(listPos); w.U32(markPos); w.U32(0); w.Zero(20);
            w.Bytes(app); w.Bytes(playlist); w.Bytes(mark);
        });
    }

    static void U64(BinaryWriterBE w, ulong v) { w.U32((long)(v >> 32)); w.U32((long)(v & 0xFFFFFFFF)); }
}
