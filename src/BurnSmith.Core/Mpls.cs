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

    /// <summary>Frames per second of a video rate code of the STN table (1 = 23.976 ... 7 = 59.94); 0 when unknown.</summary>
    public static double Fps(int rateCode) => rateCode switch { 1 => 24000 / 1001.0, 2 => 24, 3 => 25, 4 => 30000 / 1001.0, 6 => 50, 7 => 60000 / 1001.0, _ => 0 };

    public static string CodingName(int coding) => coding switch
    {
        0x1B => "H.264", 0x24 => "HEVC", 0xEA => "VC-1", 0x02 => "MPEG-2", 0x80 => "LPCM", 0x81 => "AC-3", 0x82 => "DTS", 0x83 => "TrueHD", 0x84 => "E-AC-3",
        0x85 => "DTS-HD HR", 0x86 => "DTS-HD MA", 0x90 => "PG", 0x91 => "IG", 0x92 => "texte", _ => $"0x{coding:X2}",
    };
}
