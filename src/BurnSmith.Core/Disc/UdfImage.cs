using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace BurnSmith.Core.Disc;

/// <summary>
/// A DVD-Video image, written as mkisofs -dvd-video does: UDF 1.02 with an ISO 9660 bridge (both name the same data), the files of VIDEO_TS one after the other
/// in the order of the standard: VIDEO_TS.IFO, .VOB, .BUP, then each title set's IFO, menu VOB, title VOBs and BUP. The IFOs give each part's place in sectors
/// from VIDEO_TS.IFO, and a living-room player goes there, not through the file names; IMAPI2 sorts files by size, which puts the IFO and BUP files first.
/// The folder holds folders of files (VIDEO_TS, AUDIO_TS) and files, with 8.3 names, as a DVD has. ECMA-167 / OSTA UDF 1.02 and ISO 9660 level 1.
/// </summary>
public static partial class UdfImage
{
    const int S = 2048;
    const int MainVds = 32, ReserveVds = 48, Integrity = 64, IsoArea = 66, Anchor = 256, Partition = 257;
    const int MaxExtent = (1 << 30) - S; // a short_ad's length field has 30 bits

    sealed class Node(string name, string? file, bool dir)
    {
        public string Name = name;
        public string? File = file;
        public bool Dir = dir;
        public long Size;
        public List<Node> Kids = [];
        public int Entry, Data, DataBlocks, IsoAt, IsoBytes; // UDF: file entry and data in partition blocks; ISO 9660: directory extent
        public long Unique;
    }

    [GeneratedRegex(@"^[A-Z0-9_]{1,8}(\.[A-Z0-9_]{1,3})?$")] private static partial Regex FileName();
    [GeneratedRegex(@"^[A-Z0-9_]{1,8}$")] private static partial Regex DirName();

    /// <summary>VIDEO_TS.IFO, .VOB, .BUP, then each title set: VTS_nn_0.IFO, VTS_nn_0.VOB (its menus), VTS_nn_1.VOB to _9, VTS_nn_0.BUP; anything else after, by name.</summary>
    public static IEnumerable<string> DvdOrder(IEnumerable<string> files) => files.OrderBy(f =>
    {
        var name = Path.GetFileName(f).ToUpperInvariant();
        var rank = Path.GetExtension(name) switch { ".IFO" => 0, ".BUP" => 20, _ => 10 };
        if (name is "VIDEO_TS.IFO" or "VIDEO_TS.VOB" or "VIDEO_TS.BUP") return rank;
        var m = Regex.Match(name, @"^VTS_(\d\d)_(\d)\.(IFO|VOB|BUP)$");
        return m.Success ? int.Parse(m.Groups[1].Value) * 100 + rank + int.Parse(m.Groups[2].Value) : 1_000_000;
    }).ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes the image of <paramref name="folder"/> to <paramref name="image"/>; returns what the IFOs say that the layout does not match (empty when all is in place).</summary>
    public static List<string> Write(string folder, string image, string label, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var root = Tree(folder);
        var dirs = new List<Node> { root };
        dirs.AddRange(root.Kids.Where(k => k.Dir).OrderBy(k => k.Name, StringComparer.Ordinal));
        // the data: VIDEO_TS first, in the standard's order, then the other folders' files, then the files beside them
        var video = root.Kids.FirstOrDefault(k => k.Dir && k.Name == "VIDEO_TS");
        var files = new List<Node>();
        if (video != null) files.AddRange(DvdOrder(video.Kids.Select(k => k.File!)).Select(f => video.Kids.First(k => k.File == f)));
        files.AddRange(dirs.Skip(1).Where(d => d != video).SelectMany(d => d.Kids.OrderBy(k => k.Name, StringComparer.Ordinal)));
        files.AddRange(root.Kids.Where(k => !k.Dir).OrderBy(k => k.Name, StringComparer.Ordinal));

        // ---- the partition: file set, its terminator, each folder's entry and identifiers, each file's entry, then the data
        var lb = 2;
        long unique = 16; // 0 to 15 are the root's and reserved
        foreach (var d in dirs)
        {
            d.Entry = lb++;
            d.Size = d.Kids.Sum(k => (long)FidLength(k.Name)) + FidLength("");
            d.Data = lb;
            d.DataBlocks = Blocks(d.Size);
            lb += d.DataBlocks;
            d.Unique = d == root ? 0 : unique++;
        }
        foreach (var f in files) { f.Entry = lb++; f.Unique = unique++; }
        foreach (var f in files) { f.Data = lb; f.DataBlocks = Blocks(f.Size); lb += f.DataBlocks; }
        var partitionLength = lb;
        var total = Partition + partitionLength + 1L; // the last sector: the second anchor
        if (total > uint.MaxValue) throw new IOException("image trop grande");

        // ---- ISO 9660: path tables, then the root and each folder, between the integrity sequence and the anchor
        var pathTable = PathTable(dirs, littleEndian: true, out var pathTableSize);
        var at = IsoArea + 2 * Blocks(pathTableSize);
        foreach (var d in dirs) { d.IsoAt = at; d.IsoBytes = IsoDirectory(d, root, files, dirs).Length; at += Blocks(d.IsoBytes); }
        if (at > Anchor) throw new IOException("trop de fichiers pour un DVD-Video");

        var head = new byte[(Partition + (files.Count > 0 ? files[0].Data : partitionLength)) * (long)S]; // everything before the first file's data
        var now = DateTimeOffset.Now;
        WriteIso(head, root, dirs, files, label, now, total, pathTableSize);
        WriteVolume(head, label, now, partitionLength, files.Count, dirs.Count, unique);
        WritePartition(head, root, dirs, files, label, now);

        if (Path.GetPathRoot(Path.GetFullPath(image)) is { Length: 3 } drive && new DriveInfo(drive).AvailableFreeSpace < total * S)
            throw new IOException($"pas assez de place sur {drive} pour l'image : il faut {total * S / 1e9:0.0} Go");
        using (var output = new FileStream(image, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            output.Write(head);
            long done = head.Length;
            var buffer = new byte[1 << 20];
            foreach (var f in files)
            {
                output.Position = (Partition + (long)f.Data) * S;
                using var input = new FileStream(f.File!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                if (input.Length != f.Size) throw new IOException($"« {f.File} » a changé pendant l'écriture de l'image");
                int n;
                while ((n = input.Read(buffer)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    done += n;
                    progress?.Report(Math.Min(1, (double)done / (total * S)));
                }
            }
            var anchor = new byte[S];
            AnchorPointer(anchor, 0, (int)(total - 1));
            output.Position = (total - 1) * S;
            output.Write(anchor); // also pads the last file to its sector
        }
        return video == null ? [] : Mismatches(video, files);
    }

    /// <summary>The folder as a tree of capital 8.3 names: folders of files, and files. Anything else cannot be on a DVD-Video's two file systems as BurnSmith writes them.</summary>
    static Node Tree(string folder)
    {
        var root = new Node("", null, true);
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            var name = entry.Name.ToUpperInvariant();
            if (entry is DirectoryInfo sub)
            {
                if (!DirName().IsMatch(name)) throw new IOException($"« {entry.Name} » : un dossier de DVD-Video a un nom de 8 lettres ou chiffres au plus (VIDEO_TS, AUDIO_TS)");
                var dir = new Node(name, null, true);
                foreach (var inner in sub.EnumerateFileSystemInfos())
                {
                    if (inner is DirectoryInfo) throw new IOException($"« {inner.FullName} » : un DVD-Video n'a pas de dossier dans {entry.Name}");
                    dir.Kids.Add(Leaf((FileInfo)inner));
                }
                root.Kids.Add(dir);
            }
            else root.Kids.Add(Leaf((FileInfo)entry));
        }
        return root;

        static Node Leaf(FileInfo file)
        {
            var name = file.Name.ToUpperInvariant();
            if (!FileName().IsMatch(name)) throw new IOException($"« {file.Name} » : un fichier de DVD-Video a un nom 8.3 (VTS_01_1.VOB)");
            if (file.Length > uint.MaxValue) throw new IOException($"« {file.Name} » dépasse 4 Go");
            return new Node(name, file.FullName, false) { Size = file.Length };
        }
    }

    /// <summary>
    /// What the IFOs say that the layout does not match: the title sets' first sector (VIDEO_TS.IFO's table of titles), each title set's menu and title VOBs
    /// and its BUP (its own IFO), all in sectors from where they start. Empty for a disc made by BurnSmith or any standard authoring.
    /// </summary>
    static List<string> Mismatches(Node video, List<Node> files)
    {
        var problems = new List<string>();
        var vmg = files.FirstOrDefault(f => f.Name == "VIDEO_TS.IFO");
        if (vmg == null) return ["VIDEO_TS.IFO manque"];
        int At(string name) => files.FirstOrDefault(f => f.Name == name) is { } f ? f.Data : -1;
        static uint Be32(byte[] b, int o) => o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)) : 0;
        var ifo = System.IO.File.ReadAllBytes(vmg.File!);
        if (ifo.Length < 0xC8 || !ifo.AsSpan(0, 12).SequenceEqual("DVDVIDEO-VMG"u8)) return ["VIDEO_TS.IFO n'est pas un IFO de DVD-Video"];
        var vmgMenu = Be32(ifo, 0xC0);
        if (vmgMenu != 0 && At("VIDEO_TS.VOB") - vmg.Data != vmgMenu) problems.Add($"VIDEO_TS.IFO : menu annoncé au secteur {vmgMenu}, placé à {At("VIDEO_TS.VOB") - vmg.Data}");
        if (At("VIDEO_TS.BUP") is var vmgBup and >= 0 && vmgBup - vmg.Data != Be32(ifo, 0x0C) + 1 - vmg.DataBlocks)
            problems.Add($"VIDEO_TS.IFO : copie BUP annoncée au secteur {Be32(ifo, 0x0C) + 1 - vmg.DataBlocks}, placée à {vmgBup - vmg.Data}");
        var srpt = (int)Be32(ifo, 0xC4) * S;
        var titles = srpt + 2 <= ifo.Length ? BinaryPrimitives.ReadUInt16BigEndian(ifo.AsSpan(srpt)) : 0;
        var sets = new SortedSet<int>();
        for (var i = 0; i < titles && srpt + 8 + 12 * i + 12 <= ifo.Length; i++)
        {
            int set = ifo[srpt + 8 + 12 * i + 6], start = (int)Be32(ifo, srpt + 8 + 12 * i + 8);
            sets.Add(set);
            var real = At($"VTS_{set:00}_0.IFO") - vmg.Data;
            if (real != start) problems.Add($"titre {i + 1} : le titre {set} est annoncé {start} secteurs après VIDEO_TS.IFO, il est à {real}");
        }
        foreach (var set in sets)
        {
            var name = $"VTS_{set:00}_0.IFO";
            if (files.FirstOrDefault(f => f.Name == name) is not { } vts) continue;
            var t = System.IO.File.ReadAllBytes(vts.File!);
            var menu = Be32(t, 0xC0);
            var title = Be32(t, 0xC4);
            var last = Be32(t, 0x0C);
            if (menu != 0 && At($"VTS_{set:00}_0.VOB") - vts.Data != menu) problems.Add($"{name} : menus annoncés au secteur {menu}, placés à {At($"VTS_{set:00}_0.VOB") - vts.Data}");
            if (At($"VTS_{set:00}_1.VOB") - vts.Data != title) problems.Add($"{name} : film annoncé au secteur {title}, placé à {At($"VTS_{set:00}_1.VOB") - vts.Data}");
            if (At($"VTS_{set:00}_0.BUP") is var bup and >= 0 && bup - vts.Data != last + 1 - vts.DataBlocks) problems.Add($"{name} : copie BUP annoncée au secteur {last + 1 - vts.DataBlocks}, placée à {bup - vts.Data}");
        }
        return problems;
    }

    static int Blocks(long bytes) => (int)((bytes + S - 1) / S);

    static int FidLength(string name) => (38 + (name.Length == 0 ? 0 : 1 + name.Length) + 3) & ~3;

    // ---------------------------------------------------------------- volume structures (ECMA-167 part 3, UDF 1.02 section 2.2)

    static void WriteVolume(byte[] b, string label, DateTimeOffset now, int partitionLength, int fileCount, int dirCount, long nextUnique)
    {
        // the recognition sequence, after ISO 9660's descriptors and terminator (16 and 17)
        foreach (var (sector, id) in new[] { (18, "BEA01"), (19, "NSR02"), (20, "TEA01") })
        {
            Encoding.ASCII.GetBytes(id).CopyTo(b, sector * S + 1);
            b[sector * S + 6] = 1;
        }
        foreach (var start in new[] { MainVds, ReserveVds })
        {
            var o = start * S; // primary volume descriptor
            W32(b, o + 16, 0);
            DString(b, o + 24, 32, label);
            W16(b, o + 56, 1); W16(b, o + 58, 1); W16(b, o + 60, 2); W16(b, o + 62, 2); W32(b, o + 64, 1); W32(b, o + 68, 1);
            DString(b, o + 72, 128, $"{now.ToUnixTimeSeconds():X8}{label}"); // the first 8 characters make the volume set unique
            CharSpec(b, o + 200); CharSpec(b, o + 264);
            Implementation(b, o + 388);
            Time(b, o + 376, now);
            Tag(b, o, 1, start, 512);

            o = (start + 1) * S; // implementation use: logical volume information
            W32(b, o + 16, 1);
            Regid(b, o + 20, "*UDF LV Info", [0x02, 0x01, 6]);
            CharSpec(b, o + 52);
            DString(b, o + 52 + 64, 128, label);
            Implementation(b, o + 52 + 300);
            Tag(b, o, 4, start + 1, 512);

            o = (start + 2) * S; // partition
            W32(b, o + 16, 2);
            W16(b, o + 20, 1); // allocated
            Regid(b, o + 24, "+NSR02", []);
            W32(b, o + 184, 1); // read only
            W32(b, o + 188, Partition);
            W32(b, o + 192, partitionLength);
            Implementation(b, o + 196);
            Tag(b, o, 5, start + 2, 512);

            o = (start + 3) * S; // logical volume
            W32(b, o + 16, 3);
            CharSpec(b, o + 20);
            DString(b, o + 84, 128, label);
            W32(b, o + 212, S);
            Regid(b, o + 216, "*OSTA UDF Compliant", [0x02, 0x01]);
            LongAd(b, o + 248, S, 0); // the file set descriptor
            W32(b, o + 264, 6); W32(b, o + 268, 1);
            Implementation(b, o + 272);
            W32(b, o + 432, 2 * S); W32(b, o + 436, Integrity);
            b[o + 440] = 1; b[o + 441] = 6; W16(b, o + 442, 1); W16(b, o + 444, 0); // type 1 map: volume 1, partition 0
            Tag(b, o, 6, start + 3, 446);

            o = (start + 4) * S; // unallocated space: none
            W32(b, o + 16, 4);
            Tag(b, o, 7, start + 4, 24);

            Tag(b, (start + 5) * S, 8, start + 5, 512); // terminator
        }

        var i = Integrity * S; // logical volume integrity: closed
        Time(b, i + 16, now);
        W32(b, i + 28, 1);
        W64(b, i + 40, nextUnique);
        W32(b, i + 72, 1); W32(b, i + 76, 46);
        W32(b, i + 80, 0); W32(b, i + 84, partitionLength);
        Implementation(b, i + 88);
        W32(b, i + 120, fileCount); W32(b, i + 124, dirCount);
        W16(b, i + 128, 0x102); W16(b, i + 130, 0x102); W16(b, i + 132, 0x102);
        Tag(b, i, 9, Integrity, 134);
        Tag(b, (Integrity + 1) * S, 8, Integrity + 1, 512);

        AnchorPointer(b, Anchor * S, Anchor);
    }

    static void AnchorPointer(byte[] b, int o, int location)
    {
        W32(b, o + 16, 16 * S); W32(b, o + 20, MainVds);
        W32(b, o + 24, 16 * S); W32(b, o + 28, ReserveVds);
        Tag(b, o, 2, location, 512);
    }

    // ---------------------------------------------------------------- file set, file entries, file identifiers (ECMA-167 part 4)

    static void WritePartition(byte[] b, Node root, List<Node> dirs, List<Node> files, string label, DateTimeOffset now)
    {
        int Abs(int block) => (Partition + block) * S;
        var o = Abs(0); // file set
        Time(b, o + 16, now);
        W16(b, o + 28, 3); W16(b, o + 30, 3); W32(b, o + 32, 1); W32(b, o + 36, 1);
        CharSpec(b, o + 48);
        DString(b, o + 112, 128, label);
        CharSpec(b, o + 240);
        DString(b, o + 304, 32, label);
        LongAd(b, o + 400, S, root.Entry);
        Regid(b, o + 416, "*OSTA UDF Compliant", [0x02, 0x01]);
        Tag(b, o, 256, 0, 512);
        Tag(b, Abs(1), 8, 1, 512);

        foreach (var d in dirs)
        {
            Entry(b, Abs(d.Entry), d.Entry, d, now, 1 + d.Kids.Count(k => k.Dir));
            var data = new byte[d.DataBlocks * S];
            var at = 0;
            at += Fid(data, at, d.Data, "", root.Entry, 0x0A); // the parent: the root, for the root too
            foreach (var k in d.Kids.OrderBy(k => k.Name, StringComparer.Ordinal)) at += Fid(data, at, d.Data, k.Name, k.Entry, (byte)(k.Dir ? 0x02 : 0));
            data.CopyTo(b, Abs(d.Data));
        }
        foreach (var f in files) Entry(b, Abs(f.Entry), f.Entry, f, now, 1);
    }

    static void Entry(byte[] b, int o, int block, Node n, DateTimeOffset now, int links)
    {
        W16(b, o + 16 + 4, 4); W16(b, o + 16 + 8, 1); // ICB strategy 4, one entry
        b[o + 16 + 11] = (byte)(n.Dir ? 4 : 5);
        W16(b, o + 16 + 18, 0); // short allocation descriptors
        W32(b, o + 36, uint.MaxValue); W32(b, o + 40, uint.MaxValue); // no owner, no group
        W32(b, o + 44, 0x14A5); // read and execute for everyone
        W16(b, o + 48, links);
        W64(b, o + 56, n.Size);
        W64(b, o + 64, n.DataBlocks);
        Time(b, o + 72, now); Time(b, o + 84, now); Time(b, o + 96, now);
        W32(b, o + 108, 1);
        Implementation(b, o + 128);
        W64(b, o + 160, n.Unique);
        var ads = 0;
        for (long done = 0; done < n.Size; done += MaxExtent, ads++)
        {
            W32(b, o + 176 + 8 * ads, (uint)Math.Min(MaxExtent, n.Size - done));
            W32(b, o + 180 + 8 * ads, (uint)(n.Data + done / S));
        }
        W32(b, o + 172, 8 * ads);
        Tag(b, o, 261, block, 176 + 8 * ads);
    }

    /// <summary>One file identifier at <paramref name="at"/> of a folder's data (which starts at partition block <paramref name="first"/>); returns its length.</summary>
    static int Fid(byte[] data, int at, int first, string name, int entry, byte kind)
    {
        W16(data, at + 16, 1);
        data[at + 18] = kind;
        data[at + 19] = (byte)(name.Length == 0 ? 0 : 1 + name.Length);
        LongAd(data, at + 20, S, entry);
        if (name.Length > 0) { data[at + 38] = 8; Encoding.ASCII.GetBytes(name).CopyTo(data, at + 39); }
        var length = FidLength(name);
        Tag(data, at, 257, first + at / S, length);
        return length;
    }

    // ---------------------------------------------------------------- ISO 9660 (the bridge: same data, level 1 names)

    static void WriteIso(byte[] b, Node root, List<Node> dirs, List<Node> files, string label, DateTimeOffset now, long total, int pathTableSize)
    {
        var o = 16 * S;
        b[o] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(b, o + 1); b[o + 6] = 1;
        Fill(b, o + 8, 32, "");
        Fill(b, o + 40, 32, Regex.Replace(label.ToUpperInvariant(), "[^A-Z0-9_]", "_"));
        Both32(b, o + 80, (uint)total);
        Both16(b, o + 120, 1); Both16(b, o + 124, 1); Both16(b, o + 128, S);
        Both32(b, o + 132, (uint)pathTableSize);
        var lTable = IsoArea;
        var mTable = IsoArea + Blocks(pathTableSize);
        W32(b, o + 140, lTable);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o + 148), (uint)mTable);
        Record(b, o + 156, root, "\0", now);
        Fill(b, o + 190, 128, ""); Fill(b, o + 318, 128, ""); Fill(b, o + 446, 128, ""); Fill(b, o + 574, 128, "BURNSMITH");
        Fill(b, o + 702, 37, ""); Fill(b, o + 739, 37, ""); Fill(b, o + 776, 37, "");
        var stamp = Encoding.ASCII.GetBytes(now.ToString("yyyyMMddHHmmss00"));
        foreach (var d in new[] { 813, 830, 864 }) { stamp.CopyTo(b, o + d); b[o + d + 16] = (byte)(sbyte)(now.Offset.TotalMinutes / 15); }
        Encoding.ASCII.GetBytes("0000000000000000").CopyTo(b, o + 847);
        b[o + 881] = 1;
        b[17 * S] = 255; Encoding.ASCII.GetBytes("CD001").CopyTo(b, 17 * S + 1); b[17 * S + 6] = 1;

        PathTable(dirs, true, out _).CopyTo(b, lTable * S);
        PathTable(dirs, false, out _).CopyTo(b, mTable * S);
        foreach (var d in dirs) IsoDirectory(d, root, files, dirs, now).CopyTo(b, d.IsoAt * S);
    }

    /// <summary>The path table: the root, then its folders by name, each with its first sector and its parent's number (1, the root).</summary>
    static byte[] PathTable(List<Node> dirs, bool littleEndian, out int size)
    {
        var table = new List<byte>();
        foreach (var d in dirs)
        {
            var id = d.Name.Length == 0 ? new byte[] { 0 } : Encoding.ASCII.GetBytes(d.Name);
            var rec = new byte[8 + id.Length + (id.Length & 1)];
            rec[0] = (byte)id.Length;
            if (littleEndian) { W32(rec, 2, d.IsoAt); W16(rec, 6, 1); }
            else { BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(2), (uint)d.IsoAt); BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(6), 1); }
            id.CopyTo(rec, 8);
            table.AddRange(rec);
        }
        size = table.Count;
        return [.. table];
    }

    /// <summary>A folder's records: itself, its parent, then its entries sorted by name, none across a sector's end.</summary>
    static byte[] IsoDirectory(Node d, Node root, List<Node> files, List<Node> dirs, DateTimeOffset now = default)
    {
        var records = new List<(Node Node, string Id)> { (d, "\0"), (d.Name.Length == 0 ? d : root, "\u0001") };
        records.AddRange(d.Kids.Select(k => (k, k.Dir ? k.Name : k.Name + ";1")).OrderBy(r => r.Item2, StringComparer.Ordinal));
        var bytes = new List<byte>();
        foreach (var (node, id) in records)
        {
            var rec = new byte[33 + id.Length + ((id.Length & 1) == 0 ? 1 : 0)];
            Record(rec, 0, node, id, now);
            if (bytes.Count % S + rec.Length > S) bytes.AddRange(new byte[S - bytes.Count % S]);
            bytes.AddRange(rec);
        }
        var length = Blocks(bytes.Count) * S;
        bytes.AddRange(new byte[length - bytes.Count]);
        return [.. bytes];
    }

    static void Record(byte[] b, int o, Node n, string id, DateTimeOffset now)
    {
        b[o] = (byte)(33 + id.Length + ((id.Length & 1) == 0 ? 1 : 0));
        Both32(b, o + 2, n.Dir ? (uint)n.IsoAt : (uint)(Partition + n.Data));
        Both32(b, o + 10, n.Dir ? (uint)Blocks(n.IsoBytes) * S : (uint)n.Size);
        b[o + 18] = (byte)Math.Max(0, now.Year - 1900); b[o + 19] = (byte)now.Month; b[o + 20] = (byte)now.Day;
        b[o + 21] = (byte)now.Hour; b[o + 22] = (byte)now.Minute; b[o + 23] = (byte)now.Second; b[o + 24] = (byte)(sbyte)(now.Offset.TotalMinutes / 15);
        b[o + 25] = (byte)(n.Dir ? 2 : 0);
        Both16(b, o + 28, 1);
        b[o + 32] = (byte)id.Length;
        Encoding.Latin1.GetBytes(id).CopyTo(b, o + 33);
    }

    // ---------------------------------------------------------------- fields

    /// <summary>A descriptor tag: identifier, version 2, the checksum of the tag and the CRC of the <paramref name="length"/> - 16 bytes after it.</summary>
    static void Tag(byte[] b, int o, int id, int location, int length)
    {
        W16(b, o, id); W16(b, o + 2, 2); W16(b, o + 6, 1);
        W16(b, o + 8, Crc(b.AsSpan(o + 16, length - 16))); W16(b, o + 10, length - 16);
        W32(b, o + 12, location);
        byte sum = 0;
        for (var i = 0; i < 16; i++) if (i != 4) sum += b[o + i];
        b[o + 4] = sum;
    }

    /// <summary>CRC-ITU-T (x^16 + x^12 + x^5 + 1, from 0), as ECMA-167 7.2.6 says.</summary>
    internal static ushort Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        foreach (var x in data)
        {
            crc ^= x << 8;
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
        }
        return (ushort)crc;
    }

    static void CharSpec(byte[] b, int o) => Encoding.ASCII.GetBytes("OSTA Compressed Unicode").CopyTo(b, o + 1);

    /// <summary>A fixed-size text field: compression 8 (one byte a character) or 16 (UTF-16), the text, its length in the last byte.</summary>
    static void DString(byte[] b, int o, int size, string text)
    {
        var wide = text.Any(c => c > 0xFF);
        var room = (size - 2) / (wide ? 2 : 1);
        if (text.Length > room) text = text[..room];
        if (text.Length == 0) return;
        b[o] = (byte)(wide ? 16 : 8);
        var chars = wide ? Encoding.BigEndianUnicode.GetBytes(text) : Encoding.Latin1.GetBytes(text);
        chars.CopyTo(b, o + 1);
        b[o + size - 1] = (byte)(1 + chars.Length);
    }

    static void Regid(byte[] b, int o, string id, byte[] suffix)
    {
        Encoding.ASCII.GetBytes(id).CopyTo(b, o + 1);
        suffix.CopyTo(b, o + 24);
    }

    static void Implementation(byte[] b, int o) => Regid(b, o, "*BurnSmith", [6, 0]); // Windows NT

    static void LongAd(byte[] b, int o, int length, int block) { W32(b, o, length); W32(b, o + 4, block); }

    static void Time(byte[] b, int o, DateTimeOffset t)
    {
        W16(b, o, 1 << 12 | ((int)t.Offset.TotalMinutes & 0xFFF)); // local time, its offset in minutes
        W16(b, o + 2, t.Year);
        b[o + 4] = (byte)t.Month; b[o + 5] = (byte)t.Day; b[o + 6] = (byte)t.Hour; b[o + 7] = (byte)t.Minute; b[o + 8] = (byte)t.Second;
    }

    static void Fill(byte[] b, int o, int size, string text)
    {
        for (var i = 0; i < size; i++) b[o + i] = i < text.Length ? (byte)text[i] : (byte)' ';
    }

    static void W16(byte[] b, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v);
    static void W32(byte[] b, int o, long v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), (uint)v);
    static void W64(byte[] b, int o, long v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), (ulong)v);
    static void Both16(byte[] b, int o, int v) { W16(b, o, v); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o + 2), (ushort)v); }
    static void Both32(byte[] b, int o, uint v) { W32(b, o, v); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o + 4), v); }
}
