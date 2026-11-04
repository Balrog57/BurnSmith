// Ported to C# from bdmvkit (module hdmv.py): https://github.com/kajisho5/bdmvkit, commit 5789076 (v0.7.0).
// Copyright (c) kajisho5 (Shogo Kajiyama). GNU Lesser General Public License v3.0 or later (COPYING.LESSER upstream);
// redistributed here under the GNU GPL v3 or later, with the LGPL's additional permissions removed, as section 7 of the GPL v3 allows. See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;

namespace BurnSmith.Core.Hdmv;

/// <summary>The 12-byte HDMV navigation commands (button commands and movie object commands).</summary>
public static class HdmvCommand
{
    /// <summary>operand count / group / sub-group, immediate flags / branch option, compare option, set option; then the two operands.</summary>
    static byte[] Cmd(int b0, int b1, long dst = 0, long src = 0, int b3 = 0, int b2 = 0)
    {
        var c = new byte[12];
        c[0] = (byte)b0; c[1] = (byte)b1; c[2] = (byte)b2; c[3] = (byte)b3;
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(4), (uint)dst);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(8), (uint)src);
        return c;
    }

    /// <summary>Play PlayList n (a playlist number, 00001.mpls is 1).</summary>
    public static byte[] PlayPl(int playlist) => Cmd(0x22, 0x80, playlist);
    /// <summary>Play PlayList n from mark m (0-based).</summary>
    public static byte[] PlayPlMark(int playlist, int mark) => Cmd(0x42, 0xC2, playlist, mark);
    /// <summary>Jump Title n (1..N, 0 = top menu).</summary>
    public static byte[] JumpTitle(int title) => Cmd(0x21, 0x81, title);
    /// <summary>Jump Title to the number held in general register r.</summary>
    public static byte[] JumpTitleReg(int reg) => Cmd(0x21, 0x01, reg);
    /// <summary>Jump Object n (a movie object number).</summary>
    public static byte[] JumpObject(int obj) => Cmd(0x21, 0x80, obj);
    /// <summary>Goto command pc of the same object.</summary>
    public static byte[] Goto(int pc) => Cmd(0x20, 0x81, pc);
    /// <summary>Move a value into general register r.</summary>
    public static byte[] Move(int reg, long value) => Cmd(0x50, 0x40, reg, value, 0x01);
    /// <summary>Add a value to general register r.</summary>
    public static byte[] Add(int reg, long value) => Cmd(0x50, 0x40, reg, value, 0x03);
    /// <summary>Set general register r to a random number from 1 to top.</summary>
    public static byte[] Random(int reg, int top) => Cmd(0x50, 0x40, reg, top, 0x08);
    /// <summary>Select a button of the page (an id), or the one held in general register r; the page stays.</summary>
    public static byte[] SetButton(int button) => Cmd(0x51, 0xC0, 0x80000000L | (uint)button, 0, 0x03);
    public static byte[] SetButtonFromRegister(int reg) => Cmd(0x51, 0x40, 0x80000000L | (uint)reg, 0, 0x03);
    /// <summary>Compare: the next command runs only if general register r >= value / == value / != value.</summary>
    public static byte[] IfAtLeast(int reg, long value) => Cmd(0x48, 0x40, reg, value, b2: 0x04);
    public static byte[] IfEqual(int reg, long value) => Cmd(0x48, 0x40, reg, value, b2: 0x02);
    public static byte[] IfNotEqual(int reg, long value) => Cmd(0x48, 0x40, reg, value, b2: 0x03);
    /// <summary>Close the pop-up menu.</summary>
    public static byte[] PopupOff() => Cmd(0x11, 0x00, b3: 0x07);
    /// <summary>Resume the title the menu was called from.</summary>
    public static byte[] Resume() => Cmd(0x01, 0x04);
    /// <summary>Set Stream: subtitle (PG) track n shown (1-based), or 0 = none shown.</summary>
    public static byte[] SetSubtitles(int number) => Cmd(0x51, 0xC0, number != 0 ? 0xC000 | number : 0, 0, 0x01);

    /// <summary>Set Stream: audio track (1-based) and / or subtitle track (0 = subtitles off) selected.</summary>
    public static byte[] SetStreams(int audio = 0, int? subtitle = null)
    {
        long dst = 0;
        if (audio != 0) dst |= 0x80000000L | (long)audio << 16;
        if (subtitle is { } s && s != 0) dst |= 0xC000L | (uint)s;
        return Cmd(0x51, 0xC0, dst, 0, 0x01);
    }
}

/// <summary>MovieObject.bdmv and index.bdmv for HDMV titles.</summary>
public static class HdmvNav
{
    /// <summary>MovieObject.bdmv; each object is a list of 12-byte commands. resume_intention_flag is set so a menu called during playback can resume it.</summary>
    public static byte[] MovieObjects(IReadOnlyList<IReadOnlyList<byte[]>> objects)
    {
        var data = new BinaryWriterBE();
        foreach (var cmds in objects)
        {
            data.U16(1 << 15); data.U16(cmds.Count);
            foreach (var c in cmds) data.Bytes(c);
        }
        var body = new BinaryWriterBE();
        body.U32(0); body.U16(objects.Count); body.Bytes(data.ToArray());
        var o = new BinaryWriterBE();
        o.Str("MOBJ0200", 8); o.U32(0); o.Zero(28); o.U32(body.Length); o.Bytes(body.ToArray());
        return o.ToArray();
    }

    /// <summary>
    /// The index table: first playback and top menu are HDMV titles running movie objects <paramref name="firstPlay"/> and <paramref name="topMenu"/>
    /// (interactive), then one HDMV title per entry of <paramref name="titleObjects"/> (title 1 first), flagged interactive when its number
    /// (1-based) is in <paramref name="interactive"/>.
    /// </summary>
    public static byte[] IndexTable(IReadOnlyList<int> titleObjects, int firstPlay, int topMenu, ISet<int>? interactive = null)
    {
        var w = new BinaryWriterBE();
        void Hdmv(int obj, bool interactiveTitle) { w.U32(1u << 30); w.U16((interactiveTitle ? 1 : 0) << 14); w.U16(obj); w.U32(0); }
        // object_type 1 = HDMV, access permitted; playback_type 0 = movie, 1 = interactive
        Hdmv(firstPlay, true); Hdmv(topMenu, true);
        w.U16(titleObjects.Count);
        for (var n = 1; n <= titleObjects.Count; n++) Hdmv(titleObjects[n - 1], interactive?.Contains(n) == true);
        var o = new BinaryWriterBE();
        o.U32(w.Length); o.Bytes(w.ToArray());
        return o.ToArray();
    }

    /// <summary>A whole index.bdmv (bdmvkit's layout: no extension data).</summary>
    public static byte[] Index(IReadOnlyList<int> titleObjects, int firstPlay, int topMenu, int videoFormat, int frameRate, ISet<int>? interactive = null)
    {
        var app = new BinaryWriterBE();
        app.U32(34); app.U8(0); app.U8(videoFormat << 4 | frameRate); app.Zero(32);
        var o = new BinaryWriterBE();
        o.Str("INDX0200", 8); o.U32(40 + app.Length); o.U32(0); o.Zero(24);
        o.Bytes(app.ToArray()); o.Bytes(IndexTable(titleObjects, firstPlay, topMenu, interactive));
        return o.ToArray();
    }

    /// <summary>
    /// Replaces the index table of an existing index.bdmv (the one tsMuxeR wrote) by HDMV titles, keeping its header, application info and
    /// extension data (UHD flags). Unlike <c>Bdmv.PatchIndex</c> (one BD-J title) this lays out several HDMV titles.
    /// </summary>
    public static byte[] PatchIndex(byte[] original, IReadOnlyList<int> titleObjects, int firstPlay, int topMenu, ISet<int>? interactive = null)
    {
        var indexStart = (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(8));
        var extStart = (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(12));
        var o = new BinaryWriterBE();
        o.Bytes(original.AsSpan(0, indexStart));
        o.Bytes(IndexTable(titleObjects, firstPlay, topMenu, interactive));
        if (extStart == 0) return o.ToArray();
        while (o.Length % 4 != 0) o.U8(0);
        var newExt = o.Length;
        o.Bytes(original.AsSpan(extStart));                  // extension offsets are relative to its own start: the block moves as is
        var res = o.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(12), (uint)newExt);
        return res;
    }
}
