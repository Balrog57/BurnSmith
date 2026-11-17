using System.Buffers.Binary;
using System.Text;

namespace BurnSmith.Core;

/// <summary>
/// A Blu-ray playlist (.mpls) as read: its play items with the streams they offer (STN table: video, audio, PG, IG, in the order the disc's
/// commands number them), its sub-paths and its marks. Times are the clip's 45 kHz clock. Read only; tsMuxeR writes the playlists BurnSmith makes.
/// </summary>
public sealed record Mpls(List<Mpls.Item> Items, List<int> SubPathTypes, List<Mpls.Mark> Marks)
{
    /// <summary>One stream of an STN table: PID, coding type (0x1B H.264, 0x86 DTS-HD MA, 0x90 PG...), language ("" for video), video rate code (0 otherwise).</summary>
    public sealed record Stream(int Pid, int Coding, string Lang, int Rate = 0);
    public sealed record Item(string Clip, uint In, uint Out, List<Stream> Video, List<Stream> Audio, List<Stream> Pg, List<Stream> Ig)
    {
        public double Seconds => (Out - In) / 45000.0;
    }
    /// <summary>Type 1 = entry mark (a chapter), 2 = link point.</summary>
    public sealed record Mark(int Type, int Item, uint Time);

    public double Seconds => Items.Sum(i => i.Seconds);
    public List<Mark> Chapters => [.. Marks.Where(m => m.Type == 1)];

    static int U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
    static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));

    internal static string? StructuralProblem(byte[] b)
    {
        if (b.Length < 20) return "en-tête MPLS tronqué";
        bool Section(int field, int minimum, out int start, out int end)
        {
            var address = U32(b, field);
            start = end = 0;
            if (address < 20 || address > b.Length - 4) return false;
            start = (int)address;
            var size = U32(b, start);
            if (size < minimum || size > b.Length - start - 4) return false;
            end = start + 4 + (int)size;
            return true;
        }
        if (!Section(8, 6, out var list, out var listEnd)) return "table de lecture MPLS hors du fichier ou tronquée";
        if (!Section(12, 2, out var marks, out var marksEnd) || marks < listEnd)
            return "table des repères MPLS hors du fichier ou tronquée";

        var count = U16(b, list + 6);
        if (count == 0) return "liste de lecture MPLS vide";
        var bounds = new (uint In, uint Out)[count];
        var item = list + 10;
        for (var i = 0; i < count; i++)
        {
            if (item > listEnd - 2) return $"élément de lecture {i + 1} manquant";
            var size = U16(b, item);
            if (size < 32 || size > listEnd - item - 2) return $"élément de lecture {i + 1} tronqué";
            bounds[i] = (U32(b, item + 14), U32(b, item + 18));
            item += 2 + size;
        }

        var markCount = U16(b, marks + 4);
        if (markCount > (marksEnd - marks - 6) / 14) return "table des repères MPLS tronquée";
        for (int i = 0, mark = marks + 6; i < markCount; i++, mark += 14)
        {
            var reference = U16(b, mark + 2);
            if (reference >= count) return $"repère {i + 1} : élément de lecture inexistant";
            if (b[mark + 1] != 1) continue;
            var (start, end) = bounds[reference];
            // Relative offsets preserve a clip's interval when the 45 kHz clock wraps.
            var time = U32(b, mark + 4);
            if (unchecked(time - start) > unchecked(end - start))
                return $"chapitre {i + 1} : repère hors des limites de l'élément de lecture {reference + 1}";
        }
        return null;
    }

    public static Mpls Read(string file) => Read(File.ReadAllBytes(file));

    public static Mpls Read(byte[] b)
    {
        if (b.Length < 20 || Encoding.ASCII.GetString(b, 0, 4) != "MPLS") throw new InvalidDataException("not a Blu-ray playlist (MPLS)");
        int list = (int)U32(b, 8), marks = (int)U32(b, 12);
        int items = U16(b, list + 6), subPaths = U16(b, list + 8);
        var o = list + 10;
        var read = new List<Item>();
        for (var i = 0; i < items; i++)
        {
            var length = U16(b, o);
            if ((b[o + 12] & 0x10) != 0) throw new NotSupportedException("playlist with several angles: not handled");
            read.Add(ReadItem(b, o));
            o += 2 + length;
        }
        var types = new List<int>();
        for (var i = 0; i < subPaths; i++) { types.Add(b[o + 5]); o += 4 + (int)U32(b, o); }
        var found = new List<Mark>();
        for (int i = 0, n = U16(b, marks + 4), m = marks + 6; i < n; i++, m += 14) found.Add(new Mark(b[m + 1], U16(b, m + 2), U32(b, m + 4)));
        return new Mpls(read, types, found);
    }

    static Item ReadItem(byte[] b, int o)
    {
        var clip = Encoding.ASCII.GetString(b, o + 2, 5);
        var stn = o + 34; // length, clip name, "M2TS", flags, STC id, in, out, UO mask, random access, still mode and time
        var counts = b.AsSpan(stn + 4, 4).ToArray(); // video, audio, PG, IG (secondary streams follow, not read)
        var r = stn + 16;
        var groups = new List<List<Stream>>();
        foreach (var n in counts)
        {
            var group = new List<Stream>();
            for (var i = 0; i < n; i++)
            {
                var type = b[r + 1];
                var pid = U16(b, r + type switch { 2 or 4 => 4, 3 => 3, _ => 2 }); // 1: the play item's clip; 2-4: a sub-path's
                r += 1 + b[r];
                var a = r + 1; // attributes: coding, then by kind
                var coding = b[a];
                var lang = coding switch
                {
                    0x80 or 0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x86 or 0xA1 or 0xA2 => Encoding.ASCII.GetString(b, a + 2, 3),
                    0x90 or 0x91 => Encoding.ASCII.GetString(b, a + 1, 3),
                    0x92 => Encoding.ASCII.GetString(b, a + 2, 3),
                    _ => "",
                };
                group.Add(new Stream(pid, coding, lang, coding is 0x1B or 0x02 or 0xEA or 0x24 ? b[a + 1] & 15 : 0));
                r += 1 + b[r];
            }
            groups.Add(group);
        }
        return new Item(clip, U32(b, o + 14), U32(b, o + 18), groups[0], groups[1], groups[2], groups[3]);
    }

    /// <summary>
    /// <paramref name="fresh"/> (a one-item playlist tsMuxeR wrote, no sub-path) with the sub-paths of <paramref name="original"/> grafted byte for byte and the IG entries
    /// of the original's STN added to the fresh play item (they point at those sub-paths: a pop-up menu clip). Also gives the clips the sub-paths play.
    /// </summary>
    public static byte[] Graft(byte[] fresh, byte[] original, out List<string> subClips)
    {
        static (int List, int Marks, int Item, int ItemEnd) Parts(byte[] b)
        {
            int list = (int)U32(b, 8), item = list + 10;
            return (list, (int)U32(b, 12), item, item + 2 + U16(b, item));
        }
        static (int Start, int End) IgEntries(byte[] b, int stn)
        {
            var r = stn + 16;
            for (var g = 0; g < 4; g++)
                for (var i = 0; i < b[stn + 4 + g]; i++)
                {
                    if (g == 3 && i == 0) return (r, End(b, r, b[stn + 7]));
                    r += 1 + b[r]; r += 1 + b[r];
                }
            return (r, r);
        }
        static int End(byte[] b, int r, int n) { for (var i = 0; i < n; i++) { r += 1 + b[r]; r += 1 + b[r]; } return r; }
        var (ol, om, oi, oe) = Parts(original);
        var (nl, nm, ni, ne) = Parts(fresh);
        if (U16(fresh, nl + 6) != 1 || U16(fresh, nl + 8) != 0 || U32(fresh, 16) != 0 || U32(original, 16) != 0)
            throw new NotSupportedException("playlist graft: one play item, no sub-path and no extension data expected");
        var (igStart, igEnd) = IgEntries(original, oi + 34);
        var stn = fresh.AsSpan(ni + 34, ne - ni - 34).ToArray();
        if (stn[7] != 0 || End(stn, 16, stn[4] + stn[5] + stn[6]) != stn.Length) throw new NotSupportedException("playlist graft: the fresh STN must end after its video, audio and PG entries");
        byte[] newStn = [.. stn, .. original.AsSpan(igStart, igEnd - igStart)];
        newStn[7] = original[oi + 34 + 7];
        BinaryPrimitives.WriteUInt16BigEndian(newStn, (ushort)(newStn.Length - 2));
        byte[] item = [.. fresh.AsSpan(ni, 34), .. newStn];
        BinaryPrimitives.WriteUInt16BigEndian(item, (ushort)(item.Length - 2));
        var subs = original.AsSpan(oe, om - oe).ToArray();
        byte[] list = [0, 0, 0, 0, 0, 0, 0, 1, .. original.AsSpan(ol + 8, 2), .. item, .. subs];
        BinaryPrimitives.WriteUInt32BigEndian(list, (uint)(list.Length - 4));
        byte[] output = [.. fresh.AsSpan(0, nl), .. list, .. fresh.AsSpan(nm)];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12), (uint)(nl + list.Length));
        // the clips: each sub-path's sub-play-items (length, reserved, type, flags, reserved, count, then items: length, clip name...)
        subClips = [];
        for (var o = 0; o < subs.Length; o += 4 + (int)U32(subs, o))
            for (int i = 0, p = o + 10; i < subs[o + 9]; i++, p += 2 + U16(subs, p)) subClips.Add(Encoding.ASCII.GetString(subs, p + 2, 5));
        return output;
    }

    /// <summary>Frames per second of a video rate code of the STN table (1 = 23.976 ... 7 = 59.94); 0 when unknown.</summary>
    public static double Fps(int rateCode) => rateCode switch { 1 => 24000 / 1001.0, 2 => 24, 3 => 25, 4 => 30000 / 1001.0, 6 => 50, 7 => 60000 / 1001.0, _ => 0 };

    public static string CodingName(int coding) => coding switch
    {
        0x1B => "H.264", 0x24 => "HEVC", 0xEA => "VC-1", 0x02 => "MPEG-2", 0x80 => "LPCM", 0x81 => "AC-3", 0x82 => "DTS", 0x83 => "TrueHD", 0x84 => "E-AC-3",
        0x85 => "DTS-HD HR", 0x86 => "DTS-HD MA", 0x90 => "PG", 0x91 => "IG", 0x92 => "texte", _ => $"0x{coding:X2}",
    };
}
