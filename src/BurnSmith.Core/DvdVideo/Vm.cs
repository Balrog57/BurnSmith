namespace BurnSmith.Core.DvdVideo;

/// <summary>
/// The DVD navigation commands BurnSmith uses, as their 8 bytes (the first byte in the high bits). Registers: 0..15 are g0..g15, 0x80 + n is the
/// system register s<i>n</i> (s8 = the highlighted button, 1024 times its number). Comparisons are "equal" (2) and "not equal" (3).
/// </summary>
public static class Vm
{
    public const int Eq = 2, Ne = 3;
    public const byte Sprm = 0x80;
    public const int RootMenu = 3;

    static ulong Bytes(params int[] b)
    {
        ulong v = 0;
        for (var i = 0; i < 8; i++) v = v << 8 | (byte)(i < b.Length ? b[i] : 0);
        return v;
    }

    public static ulong Nop() => 0;

    /// <summary>Goes to command <paramref name="line"/> (1-based, in the same list), when <c>g<paramref name="reg"/> cmp value</c> if a compare is given.</summary>
    public static ulong Goto(int line, int reg = 0, int cmp = 0, int value = 0) => Bytes(0x00, (cmp != 0 ? 0x80 : 0) | cmp << 4 | 1, 0, reg, value >> 8, value, 0, line);

    public static ulong Exit() => Bytes(0x30, 0x01);

    /// <summary>gN = value (or gN = register), optionally followed by a link to menu PGC <paramref name="linkPgc"/>.</summary>
    public static ulong Set(int reg, int value, int linkPgc = 0) => Bytes(0x71, linkPgc > 0 ? 4 : 0, 0, reg, value >> 8, value, linkPgc >> 8, linkPgc);
    public static ulong SetReg(int reg, int source) => Bytes(0x61, 0, 0, reg, 0, source);
    public static ulong Mul(int reg, int value) => Bytes(0x75, 0, 0, reg, value >> 8, value);
    public static ulong AddReg(int reg, int source) => Bytes(0x63, 0, 0, reg, 0, source);

    /// <summary>gN = value, then the commands after the post commands' start ("LinkTailPGC"): how a button runs more than one command.</summary>
    public static ulong SetThenTail(int reg, int value) => Bytes(0x71, 0x01, 0, reg, value >> 8, value, 0, 0x0D);

    /// <summary>Plays menu PGC <paramref name="pgc"/>, when <c>g<paramref name="reg"/> == value</c> if a register is given.</summary>
    public static ulong LinkPgc(int pgc, int reg = -1, int value = 0) =>
        reg < 0 ? Bytes(0x20, 0x04, 0, 0, 0, 0, pgc >> 8, pgc) : Bytes(0x20, 0x80 | Eq << 4 | 4, 0, reg, value >> 8, value, pgc >> 8, pgc);

    /// <summary>Audio stream (0-based), optionally then a link to menu PGC <paramref name="linkPgc"/>.</summary>
    public static ulong Audio(int stream, int linkPgc = 0) => Bytes(0x51, linkPgc > 0 ? 4 : 0, 0, 0x80 | stream, 0, 0, linkPgc >> 8, linkPgc);

    /// <summary>Subpicture: 62 = none, 64 + n = stream n shown.</summary>
    public static ulong Subtitle(int value, int linkPgc = 0) => Bytes(0x51, linkPgc > 0 ? 4 : 0, 0, 0, 0x80 | value, 0, linkPgc >> 8, linkPgc);

    public static ulong Button(int value) => Bytes(0x56, 0, 0, 0, value >> 8, value);
    public static ulong ButtonReg(int reg) => Bytes(0x46, 0, 0, 0, 0, reg);

    /// <summary>From the first-play PGC: title <paramref name="title"/> of the disc.</summary>
    public static ulong JumpTitle(int title) => Bytes(0x30, 0x02, 0, 0, 0, title);
    /// <summary>From a menu or a title of the title set: its title <paramref name="title"/>.</summary>
    public static ulong JumpVtsTitle(int title) => Bytes(0x30, 0x03, 0, 0, 0, title);
    public static ulong JumpVtsChapter(int title, int chapter) => Bytes(0x30, 0x05, chapter >> 8 & 3, chapter, 0, title);
    /// <summary>From the first-play PGC: the root menu of title set 1.</summary>
    public static ulong JumpRootMenu() => Bytes(0x30, 0x06, 0, 1, 1, 0x80 | RootMenu);
    /// <summary>From a title: its title set's root menu (resuming would go back to cell 1).</summary>
    public static ulong CallRootMenu() => Bytes(0x30, 0x08, 0, 0, 1, 0x80 | RootMenu);

    public static void Write(byte[] to, int at, ulong cmd)
    {
        for (var i = 7; i >= 0; i--) { to[at + i] = (byte)cmd; cmd >>= 8; }
    }
}
