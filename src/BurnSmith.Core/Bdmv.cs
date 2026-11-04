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
    /// Replaces the index table of an index.bdmv by: First Playback = Top Menu = BD-J title <paramref name="bdjoName"/>,
    /// plus that same BD-J object as title 1. Header, app info and extension data (UHD flags) are kept.
    /// </summary>
    public static byte[] PatchIndex(byte[] original, string bdjoName = "00000")
    {
        var indexStart = (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(8));
        var extStart = (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(12));

        static void BdjObject(BinaryWriterBE w, string name, int playbackType)
        {
            w.U8(playbackType << 6); w.U8(0); w.Str(name, 5); w.U8(0);
        }

        var table = new BinaryWriterBE();
        table.U32(38); // length of what follows: 2 x 12 + 2 + 12
        foreach (var _ in new[] { 0, 1 }) // first playback, top menu
        {
            table.U32(2u << 30);            // object_type = BD-J
            BdjObject(table, bdjoName, 3);  // interactive
        }
        table.U16(1); // one title
        table.U32((2u << 30));              // object_type = BD-J, access_type 0
        BdjObject(table, bdjoName, 2);      // movie

        var o = new BinaryWriterBE();
        o.Bytes(original.AsSpan(0, indexStart));
        o.Bytes(table.ToArray());
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
    public static byte[] Bdjo(string initialClass, int haviConfig, string jar = "00000", uint orgId = 0x42535430, ushort appId = 0x4000)
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
