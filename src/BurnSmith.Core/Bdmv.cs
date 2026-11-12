using System.Buffers.Binary;
using System.Text;

namespace BurnSmith.Core;

/// <summary>
/// Writers for the two Blu-ray files a BD-J menu needs: index.bdmv (patched from the one tsMuxeR wrote)
/// and the .bdjo object. Layouts follow libbluray's parsers (index_parse.c, bdjo_parse.c).
/// </summary>
public static class Bdmv
{
    /// <summary>HAVI graphics configuration ids from the BDJO terminal info (libbluray GraphicsResolution).</summary>
    public const int HaviHd1080 = 1, HaviHd720 = 2, HaviUhd1080 = 7;

    /// <summary>
    /// A MovieObject.bdmv (the one tsMuxeR wrote for a disc with no menu) whose object 0, the one title 1 runs to start the film, begins with
    /// <paramref name="commands"/>: a Set Stream there selects the subtitle before the film plays. The objects are written back as they were.
    /// </summary>
    public static byte[] PrependToFilm(byte[] movieObject, params byte[][] commands)
    {
        var objects = ReadObjects(movieObject);
        if (objects.Count == 0) throw new InvalidDataException("MovieObject.bdmv has no object");
        objects[0] = [.. commands, .. objects[0]];
        return Disc.MovieObjectFile.Write(objects, Version(movieObject));
    }

    /// <summary>"0200", or "0300" on a 4K disc (tsMuxeR --blu-ray-v3): the same layout, and the file must keep the disc's version.</summary>
    static string Version(byte[] movieObject) => movieObject.Length >= 8 && movieObject.AsSpan(0, 4).SequenceEqual("MOBJ"u8) ? Encoding.ASCII.GetString(movieObject, 4, 4) : "";

    static List<IReadOnlyList<byte[]>> ReadObjects(byte[] movieObject)
    {
        const int first = 8 + 4 + 28 + 4 + 4 + 2; // "MOBJ0200", extension start, reserved, body length, reserved, number of objects
        if (movieObject.Length < first || Version(movieObject) is not ("0200" or "0300")) throw new InvalidDataException("not a MovieObject.bdmv");
        var count = BinaryPrimitives.ReadUInt16BigEndian(movieObject.AsSpan(first - 2));
        var objects = new List<IReadOnlyList<byte[]>>();
        for (int at = first, i = 0; i < count; i++)
        {
            var n = BinaryPrimitives.ReadUInt16BigEndian(movieObject.AsSpan(at + 2));
            objects.Add([.. Enumerable.Range(0, n).Select(c => movieObject.AsSpan(at + 4 + c * 12, 12).ToArray())]);
            at += 4 + n * 12;
        }
        return objects;
    }

    /// <summary>A MovieObject.bdmv with one more object, <paramref name="commands"/>, after the others (its number is <paramref name="index"/>); the others are written back as they were.</summary>
    public static byte[] AppendObject(byte[] movieObject, out int index, params byte[][] commands)
    {
        var objects = ReadObjects(movieObject);
        index = objects.Count;
        objects.Add(commands);
        return Disc.MovieObjectFile.Write(objects, Version(movieObject));
    }

    /// <summary>
    /// Replaces the index table of an index.bdmv by: First Playback = the HDMV movie object <paramref name="firstPlay"/>, Top Menu = BD-J object 00000 (the menu),
    /// titles 1 to <paramref name="videos"/> = BD-J object 00001 (title N plays video N), and one more title, the menu again. Header, app info and extension data (UHD flags) are kept.
    /// </summary>
    public static byte[] PatchIndex(byte[] original, int firstPlay, int videos)
    {
        var table = new BinaryWriterBE();
        void Bdj(string name, int playbackType) { table.U32(2u << 30); table.U8(playbackType << 6); table.U8(0); table.Str(name, 5); table.U8(0); } // object_type = BD-J
        table.U32(12 + 12 + 2 + 12 * (videos + 1));
        table.U32(1u << 30); table.U16(1 << 14); table.U16(firstPlay); table.U32(0); // HDMV, interactive
        Bdj("00000", 3);                                                      // the Top Menu: interactive
        table.U16(videos + 1);
        for (var n = 1; n <= videos; n++) Bdj("00001", 2);                   // movie
        Bdj("00000", 2);
        return ReplaceTable(original, table.ToArray());
    }

    /// <summary>
    /// The index.bdmv of a disc with a native (HDMV) menu: First Playback runs movie object 0, the Top Menu object <paramref name="topMenu"/>, and title N (of <paramref name="titles"/>)
    /// object N + 1. Header, app info and extension data are kept.
    /// </summary>
    public static byte[] HdmvIndex(byte[] original, int titles, int topMenu = 1) =>
        ReplaceTable(original, Disc.MovieObjectFile.IndexTable([.. Enumerable.Range(2, titles)], firstPlay: 0, topMenu: topMenu));

    /// <summary>An index.bdmv with its index table (length included) replaced by <paramref name="table"/>.</summary>
    static byte[] ReplaceTable(byte[] original, byte[] table)
    {
        var indexStart = (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(8));
        var extStart = (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(12));
        var o = new BinaryWriterBE();
        o.Bytes(original.AsSpan(0, indexStart));
        o.Bytes(table);
        if (extStart != 0)
        {
            while (o.Length % 4 != 0) o.U8(0);
            var newExt = o.Length;
            o.Bytes(original.AsSpan(extStart)); // extension offsets are relative to its own start: the block moves as is
            var res = o.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(12), (uint)newExt);
            return res;
        }
        return o.ToArray();
    }

    /// <summary>
    /// One-application BDJO: the jar <paramref name="jar"/> (BDMV/JAR/&lt;jar&gt;.jar) is cached and started with
    /// <paramref name="initialClass"/>. Field values mirror a BD-J disc known to play on standalone players.
    /// </summary>
    public static byte[] Bdjo(string initialClass, int haviConfig, string jar = "00000", uint orgId = BdjSign.OrgId, ushort appId = 0x4000)
    {
        var terminal = new BinaryWriterBE();
        terminal.U32(10); terminal.Str("00000", 5);       // default font name (no font file needed: we draw images only)
        terminal.U8(haviConfig << 4); terminal.Zero(4);

        var cache = new BinaryWriterBE();
        cache.U32(14); cache.U8(1); cache.U8(0);          // one item
        cache.U8(1); cache.Str(jar, 5); cache.Str("*.*", 3); cache.Zero(3); // type 1 = jar, any language

        var playlists = new BinaryWriterBE();
        playlists.U32(4); playlists.U16(0x0010); playlists.U16(0); // access to all playlists

        var body = new BinaryWriterBE(); // application descriptor
        body.U16(0x1000);                                  // one profile
        body.U16(1); body.U8(1); body.U8(0); body.U8(0); body.U8(0); // profile 1, version 1.0.0
        body.U8(3);                                        // priority
        body.U8(0xd0);                                     // binding / visibility
        body.U16(0);                                       // no application names
        void Word(string s) { body.U8(s.Length); body.Str(s, s.Length); if (s.Length % 2 == 0) body.U8(0); }
        Word("");                                          // icon locator
        body.U16(0);                                       // icon flags
        Word(jar);                                         // base directory
        Word("");                                          // classpath extension
        Word(initialClass);
        body.U8(0); body.U8(0);                            // no parameters

        var app = new BinaryWriterBE();
        app.U8(1);                                         // autostart
        app.U8(0x10);                                      // type 1 = BD-J
        app.U32(orgId); app.U16(appId);
        app.Zero(4); app.U16(4 + body.Length); app.Zero(4);
        app.Bytes(body.ToArray());

        var table = new BinaryWriterBE();
        table.U32(2 + app.Length); table.U8(1); table.U8(0);
        table.Bytes(app.ToArray());

        var keys = new byte[4];                            // no reserved remote keys
        var files = new BinaryWriterBE();
        files.U16(1); files.U8('.'); files.U8(0);          // file access: "." word aligned

        var sections = new[] { terminal.ToArray(), cache.ToArray(), playlists.ToArray(), table.ToArray(), keys, files.ToArray() };
        var o = new BinaryWriterBE();
        o.Str("BDJO0200", 8);
        var addr = 48;
        foreach (var s in sections) { o.U32(addr); addr += s.Length; }
        o.Zero(48 - o.Length);
        foreach (var s in sections) o.Bytes(s);
        return o.ToArray();
    }
}

/// <summary>Tiny big-endian writer: Blu-ray structures are big-endian everywhere.</summary>
public sealed class BinaryWriterBE
{
    readonly MemoryStream ms = new();
    public int Length => (int)ms.Length;
    public void U8(int v) => ms.WriteByte((byte)v);
    public void U16(int v) { U8(v >> 8); U8(v); }
    public void U32(long v) { U16((int)(v >> 16)); U16((int)v); }
    public void Zero(int n) { for (var i = 0; i < n; i++) U8(0); }
    public void Str(string s, int len) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, Math.Min(b.Length, len)); Zero(len - Math.Min(b.Length, len)); }
    public void Bytes(ReadOnlySpan<byte> b) => ms.Write(b);
    public byte[] ToArray() => ms.ToArray();
}
