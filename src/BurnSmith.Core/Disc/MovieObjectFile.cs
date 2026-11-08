namespace BurnSmith.Core.Disc;

/// <summary>
/// The two tables a Blu-ray player reads first: MovieObject.bdmv, the commands each title starts with,
/// and the index table of index.bdmv, which says what First Playback, the top menu and each title are.
/// </summary>
public static class MovieObjectFile
{
    /// <summary>Header length: "MOBJ0200", the extension start, the reserved block, the body length and the body's own reserved word.</summary>
    const int BodyStart = 48;

    /// <summary>A MovieObject.bdmv holding <paramref name="objects"/>, each a run of 12-byte commands (see <see cref="Nav"/>); <paramref name="version"/> "0300" on a 4K disc.</summary>
    public static byte[] Write(IReadOnlyList<IReadOnlyList<byte[]>> objects, string version = "0200")
    {
        var body = new BinaryWriterBE();
        body.U32(0);                                  // reserved
        body.U16(objects.Count);
        foreach (var commands in objects)
        {
            body.U16(1 << 15);                        // resume intention: a menu opened during playback gives the film back
            body.U16(commands.Count);
            foreach (var command in commands) body.Bytes(command);
        }
        var written = body.ToArray();

        var file = new BinaryWriterBE();
        file.Str("MOBJ" + version, 8);
        file.U32(0);                                  // no extension data
        file.Zero(BodyStart - 8 - 4 - 4 - 4);         // reserved, up to the body's own length word
        file.U32(written.Length);
        file.Bytes(written);
        return file.ToArray();
    }

    /// <summary>
    /// The index table: First Playback and Top Menu run the movie objects <paramref name="firstPlay"/> and
    /// <paramref name="topMenu"/>, then one HDMV title per entry of <paramref name="titleObjects"/>, title 1 first.
    /// A title is interactive (a menu) when its 1-based number is in <paramref name="interactive"/>.
    /// </summary>
    public static byte[] IndexTable(IReadOnlyList<int> titleObjects, int firstPlay, int topMenu, ISet<int>? interactive = null)
    {
        var table = new BinaryWriterBE();
        void Entry(int movieObject, bool menu)
        {
            table.U32(1u << 30);                      // object_type = HDMV, access permitted
            table.U16((menu ? 1 : 0) << 14);          // playback_type: 0 movie, 1 interactive
            table.U16(movieObject);
            table.U32(0);                             // no still time
        }

        Entry(firstPlay, true);
        Entry(topMenu, true);
        table.U16(titleObjects.Count);
        for (var number = 1; number <= titleObjects.Count; number++)
            Entry(titleObjects[number - 1], interactive?.Contains(number) == true);

        var written = table.ToArray();
        var block = new BinaryWriterBE();
        block.U32(written.Length);
        block.Bytes(written);
        return block.ToArray();
    }

    /// <summary>
    /// A whole index.bdmv: header, application info (video format and frame rate of the disc) and the index table.
    /// </summary>
    public static byte[] Index(IReadOnlyList<int> titleObjects, int firstPlay, int topMenu, int videoFormat, int frameRate, ISet<int>? interactive = null)
    {
        var app = new BinaryWriterBE();
        app.U32(34);                                  // length of what follows: initial output mode + 32 reserved bytes
        app.U8(0);
        app.U8(videoFormat << 4 | frameRate);
        app.Zero(32);
        var appBytes = app.ToArray();

        var table = IndexTable(titleObjects, firstPlay, topMenu, interactive);

        var file = new BinaryWriterBE();
        file.Str("INDX0200", 8);
        file.U32(40 + appBytes.Length);               // where the index table starts
        file.U32(0);                                  // no extension data
        file.Zero(24);                                // reserved
        file.Bytes(appBytes);
        file.Bytes(table);
        return file.ToArray();
    }
}
