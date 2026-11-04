using System.Text;
using BurnSmith.Core.Hdmv;

namespace BurnSmith.HdmvCheck;

/// <summary>A small decoder of IG segments, independent of the writer (it reads the layout from the spec side, as libbluray and the Python analyser do).</summary>
sealed class IgDecoded
{
    public int W, H, FrameRate, CompNumber, State, Sequence, UserTimeout, Pages, PageId, DefaultSelected, DefaultActivated, PaletteId;
    public bool StreamModel, UiModel;
    public string Uo = "";
    public List<string> Buttons = [];
    public SortedDictionary<int, (int Y, int Cr, int Cb, int A)> Palette = [];
    public List<(int Id, int W, int H, byte[] Pixels)> Objects = [];

    static int U16(byte[] b, int o) => b[o] << 8 | b[o + 1];
    static int U24(byte[] b, int o) => b[o] << 16 | b[o + 1] << 8 | b[o + 2];

    /// <summary>The segments of a .sup (the "PG" layout), timestamps dropped.</summary>
    public static List<(byte Type, byte[] Body)> ReadSup(byte[] sup)
    {
        var list = new List<(byte, byte[])>();
        for (var i = 0; i < sup.Length;)
        {
            if (sup[i] != 'P' || sup[i + 1] != 'G') throw new InvalidDataException("segment magic");
            var len = U16(sup, i + 11);
            list.Add((sup[i + 10], sup[(i + 13)..(i + 13 + len)]));
            i += 13 + len;
        }
        return list;
    }

    public static IgDecoded Decode(IEnumerable<(byte Type, byte[] Body)> segments)
    {
        var d = new IgDecoded();
        var buf = new List<byte>();
        foreach (var (type, seg) in segments)
        {
            switch (type)
            {
                case 0x18: d.ReadIcs(seg); break;
                case 0x14:
                    d.PaletteId = seg[0];
                    for (var i = 2; i + 5 <= seg.Length; i += 5) d.Palette[seg[i]] = (seg[i + 1], seg[i + 2], seg[i + 3], seg[i + 4]);
                    break;
                case 0x15:
                    if ((seg[3] & 0x80) != 0) buf.Clear();
                    buf.AddRange(seg.AsSpan(4).ToArray());
                    if ((seg[3] & 0x40) != 0)
                    {
                        var all = buf.ToArray();
                        if (U24(all, 0) != all.Length - 3) throw new InvalidDataException("ODS data length");
                        int w = U16(all, 3), h = U16(all, 5);
                        d.Objects.Add((U16(seg, 0), w, h, Pgs.Unrle(all.AsSpan(7), w, h)));
                    }
                    break;
            }
        }
        return d;
    }

    void ReadIcs(byte[] s)
    {
        W = U16(s, 0); H = U16(s, 2); FrameRate = s[4] >> 4; CompNumber = U16(s, 5); State = s[7] >> 6; Sequence = s[8];
        var ic = s[12..];
        if (U24(s, 9) != ic.Length) throw new InvalidDataException("ICS length");
        StreamModel = (ic[0] >> 7) != 0; UiModel = (ic[0] >> 6 & 1) != 0;
        var o = 1;
        if (!StreamModel) o += 10;
        UserTimeout = U24(ic, o); o += 3;
        Pages = ic[o++];
        for (var pg = 0; pg < Pages; pg++)
        {
            PageId = ic[o]; Uo = Convert.ToHexString(ic[(o + 2)..(o + 10)]).ToLowerInvariant(); o += 10;
            if (ic[o] != 0 || ic[o + 1] != 0 || ic[o + 2] != 0 || ic[o + 3] != 0) throw new NotSupportedException("effects");
            o += 4;
            o++;                                              // animation frame rate code
            DefaultSelected = U16(ic, o); DefaultActivated = U16(ic, o + 2); PaletteId = ic[o + 4]; var bogs = ic[o + 5]; o += 6;
            for (var b = 0; b < bogs; b++)
            {
                var defaultValid = U16(ic, o); var count = ic[o + 2]; o += 3;
                for (var k = 0; k < count; k++)
                {
                    var sb = new StringBuilder();
                    sb.Append($"bog{b}(valid {defaultValid}) btn {U16(ic, o)} num={U16(ic, o + 2)} auto={ic[o + 4] >> 7} pos=({U16(ic, o + 5)},{U16(ic, o + 7)}) ");
                    sb.Append($"nav(U,D,L,R)=[{U16(ic, o + 9)},{U16(ic, o + 11)},{U16(ic, o + 13)},{U16(ic, o + 15)}] ");
                    sb.Append($"normal={U16(ic, o + 17)}-{U16(ic, o + 19)}/{ic[o + 21] >> 7} sel=snd{ic[o + 22]}:{U16(ic, o + 23)}-{U16(ic, o + 25)}/{ic[o + 27] >> 7} ");
                    sb.Append($"act=snd{ic[o + 28]}:{U16(ic, o + 29)}-{U16(ic, o + 31)} ");
                    var n = U16(ic, o + 33); o += 35;
                    sb.Append($"cmds={n}");
                    for (var c = 0; c < n; c++) { sb.Append(' ').Append(Convert.ToHexString(ic[o..(o + 12)]).ToLowerInvariant()); o += 12; }
                    Buttons.Add(sb.ToString());
                }
            }
        }
        if (o != ic.Length) throw new InvalidDataException($"ICS: {ic.Length - o} bytes left over");
    }

    /// <summary>A canonical text of everything that matters, to compare two streams or to print.</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ICS {W}x{H} fr={FrameRate} comp={CompNumber} state={State} seq={Sequence:x2} stream_model={StreamModel} ui_model={UiModel} timeout={UserTimeout} pages={Pages}");
        sb.AppendLine($"page {PageId} UO={Uo} default_sel={DefaultSelected} default_act={DefaultActivated:x} palette_ref={PaletteId}");
        foreach (var b in Buttons) sb.AppendLine(b);
        sb.AppendLine($"PDS {Palette.Count} entries: " + string.Join(' ', Palette.Select(p => $"{p.Key}:{p.Value.Y},{p.Value.Cr},{p.Value.Cb},{p.Value.A}")));
        foreach (var o in Objects) sb.AppendLine($"ODS {o.Id} {o.W}x{o.H} px-hash={Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(o.Pixels))[..12]}");
        return sb.ToString();
    }
}
