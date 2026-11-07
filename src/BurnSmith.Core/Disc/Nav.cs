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
}
