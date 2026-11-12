using System.Buffers.Binary;

namespace BurnSmith.Core.Disc;

/// <summary>
/// The 12-byte navigation commands of an HDMV disc: the little instruction set a movie object or an
/// Interactive Graphics button runs. Laid out as the BD-ROM format describes them — operation code,
/// mode byte, compare and set options, then two 32-bit operands, everything big-endian.
/// </summary>
public static class Nav
{
    /// <summary>Every command is 12 bytes, which is also what the movie object tables count in.</summary>
    public const int Size = 12;

    /// <summary>One command.</summary>
    /// <param name="opcode">The operation.</param>
    /// <param name="mode">Operand count, operand group and sub-group, immediate flags.</param>
    /// <param name="first">First operand (destination).</param>
    /// <param name="second">Second operand (source).</param>
    /// <param name="compare">Compare option, for the conditional jumps.</param>
    /// <param name="set">Set option, for the register operations.</param>
    public static byte[] Command(byte opcode, byte mode, long first = 0, long second = 0, byte compare = 0, byte set = 0)
    {
        var command = new byte[Size];
        command[0] = opcode;
        command[1] = mode;
        command[2] = compare;
        command[3] = set;
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(4), (uint)first);
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(8), (uint)second);
        return command;
    }

    /// <summary>Play the playlist <paramref name="playlist"/> (00001.mpls is 1).</summary>
    public static byte[] PlayPlaylist(int playlist) => Command(0x22, 0x80, playlist);

    /// <summary>Go to title <paramref name="title"/> (0 is the top menu, as on the disc's index).</summary>
    public static byte[] JumpTitle(int title) => Command(0x21, 0x81, title);

    /// <summary>
    /// Choose the streams to play: audio track <paramref name="audio"/> (1-based, 0 = leave it) and subtitle
    /// track <paramref name="subtitle"/> (1-based, 0 = no subtitle); a null subtitle leaves the choice alone.
    /// </summary>
    public static byte[] SelectStreams(int audio = 0, int? subtitle = null)
    {
        long operand = 0;
        if (audio != 0) operand |= 0x8000_0000L | (long)audio << 16;
        if (subtitle is { } track && track != 0) operand |= 0xC000L | (uint)track;
        return Command(0x51, 0xC0, operand, set: 0x01);
    }

    // What a native (HDMV) menu needs on top of the above. An operand is a number, or the register that holds it: the mode byte says which
    // (0x80 = the first is a number, 0x40 = the second). A comparison that fails skips the command after it.

    /// <summary>Go on at command <paramref name="command"/> of the same object (0 = its first).</summary>
    public static byte[] GoTo(int command) => Command(0x20, 0x81, command);

    /// <summary>Run movie object <paramref name="movieObject"/> instead of this one.</summary>
    public static byte[] JumpObject(int movieObject) => Command(0x21, 0x80, movieObject);

    /// <summary>Play the playlist whose number register <paramref name="register"/> holds.</summary>
    public static byte[] PlayPlaylistIn(int register) => Command(0x22, 0x00, register);

    /// <summary>Play playlist <paramref name="playlist"/> from the chapter mark whose number (0 = the first) register <paramref name="markRegister"/> holds.</summary>
    public static byte[] PlayPlaylistAtMarkIn(int playlist, int markRegister) => Command(0x42, 0x82, playlist, markRegister);

    /// <summary>The next command runs only when register <paramref name="register"/> holds <paramref name="value"/>.</summary>
    public static byte[] IfEqual(int register, long value) => Command(0x48, 0x40, register, value, compare: 0x02);

    /// <summary>Register <paramref name="register"/> = <paramref name="value"/>.</summary>
    public static byte[] Set(int register, long value) => Command(0x50, 0x40, register, value, set: 0x01);

    /// <summary>Register <paramref name="register"/> = register <paramref name="from"/>.</summary>
    public static byte[] Copy(int register, int from) => Command(0x50, 0x00, register, from, set: 0x01);

    /// <summary>Register <paramref name="register"/> = the player's own register <paramref name="playerRegister"/> (10 = the button selected in the menu).</summary>
    public static byte[] CopyPlayer(int register, int playerRegister) => Command(0x50, 0x00, register, 0x8000_0000L | (uint)playerRegister, set: 0x01);

    /// <summary>
    /// <see cref="SelectStreams"/> with the numbers taken from registers: audio from <paramref name="audioRegister"/>, and when
    /// <paramref name="subtitleRegister"/> is given the subtitle from it, shown; without it the subtitles are switched off.
    /// </summary>
    public static byte[] SelectStreamsIn(int audioRegister, int? subtitleRegister = null) =>
        Command(0x51, 0x40, 0x8000_0000L | (long)audioRegister << 16 | (subtitleRegister is { } s ? 0xC000L | (uint)s : 0), set: 0x01);

    /// <summary>Menu button commands: select button <paramref name="button"/> of the page shown.</summary>
    public static byte[] SelectButton(int button) => Command(0x51, 0xC0, 0x8000_0000L | (uint)button, set: 0x03);

    /// <summary>Select the button whose number register <paramref name="register"/> holds.</summary>
    public static byte[] SelectButtonIn(int register) => Command(0x51, 0x40, 0x8000_0000L | (uint)register, set: 0x03);

    /// <summary>Show (and let the remote reach) button <paramref name="button"/>, or hide it.</summary>
    public static byte[] EnableButton(int button, bool enabled = true) => Command(0x31, 0x80, button, set: (byte)(enabled ? 0x04 : 0x05));
}
