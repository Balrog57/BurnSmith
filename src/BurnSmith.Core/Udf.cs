using System.Buffers.Binary;
using System.Text;

namespace BurnSmith.Core;

/// <summary>
/// A Blu-ray image written by BurnSmith itself (UDF 2.50 with a metadata partition), for what IMAPI cannot do: a 3D film's .ssif, the file a 3D player reads,
/// made of the very sectors of the film's two .m2ts (left eye, right eye) laid out in turns, so the 3D takes no room twice. tsMuxeR writes the two .m2ts
/// and records where they are cut in their .clpi, but makes the .ssif only in an image of its own, which cannot hold a menu.
/// The layout is the one of tsMuxeR's iso_writer.cpp (Apache 2.0, github.com/justdan96/tsMuxer), whose images players read.
/// </summary>
public static class Udf
{
    const int S = 2048, Block = 65536, Partition = 576 * 1024 / S, MetaStart = 640 * 1024 / S;
    const long MaxExtent = 0x40000000 - S; // an extent's length has 30 bits
    const int InEntry = (S - 216 - 32) / 16, InContinuation = (S - 24 - 32) / 16;
    const string Id = "*BurnSmith";

    sealed class Node(string name, Node? parent, int type, int id)
    {
        public readonly string Name = name;
        public readonly Node? Parent = parent;
        public readonly int Type = type, Id = id; // 4 directory, 5 file, 249 real-time file (stream), 13 system stream directory
        public readonly List<Node> Files = [], Dirs = [];
        public readonly List<(long Lbn, long Length)> Extents = [];
        public long Size;
        public int Lbn, Sectors;
        public bool IsDir => Type is 4 or 13;
        public int DirBytes => 40 + Files.Concat(Dirs).Sum(c => (38 + c.Name.Length + 1 + 3) / 4 * 4);
    }

    /// <summary>
    /// The 3D films of a disc folder as tsMuxeR writes them: base clip N and dependent clip N+1, both with the points their streams are cut at
    /// (ExtentStartPoint of the .clpi, in bytes). Empty for a 2D disc.
    /// </summary>
    public static List<(string Base, string Dependent, List<long> BaseCuts, List<long> DependentCuts)> Interleaved(string folder)
    {
        var list = new List<(string, string, List<long>, List<long>)>();
        var clipinf = Path.Combine(folder, "BDMV", "CLIPINF");
        if (!Directory.Exists(clipinf)) return list;
        foreach (var clpi in Directory.GetFiles(clipinf, "*.clpi").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(clpi);
            if (!int.TryParse(name, out var n) || list.Any(p => p.Item2 == name) || ExtentStarts(clpi) is not { } b) continue;
            var dependent = $"{n + 1:00000}";
            if (ExtentStarts(Path.Combine(clipinf, dependent + ".clpi")) is { } d && d.Count == b.Count) list.Add((name, dependent, b, d));
        }
        return list;
    }

    /// <summary>The ExtentStartPoint of a .clpi (extension data 2/4), as byte offsets in its .m2ts; null when it has none (a 2D clip).</summary>
    static List<long>? ExtentStarts(string clpi)
    {
        if (!File.Exists(clpi)) return null;
        var c = File.ReadAllBytes(clpi);
        uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(o));
        if (c.Length < 0x1C || U32(0x18) is var ext && ext == 0 || ext + 12 > c.Length) return null;
        for (var i = 0; i < c[ext + 11]; i++)
        {
            var e = (int)ext + 12 + i * 12;
            if (e + 12 > c.Length) return null;
            if (BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(e)) != 2 || BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(e + 2)) != 4) continue;
            var p = (int)(ext + U32(e + 4));
            var count = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(p + 6));
            return p + 8 + 4 * count > c.Length ? null : [.. Enumerable.Range(0, count).Select(k => U32(p + 8 + 4 * k) * 192L)];
        }
        return null;
    }

    /// <summary>Writes the image of <paramref name="folder"/>: every file as it is, plus BDMV\STREAM\SSIF\N.ssif for each 3D film (<see cref="Interleaved"/>).</summary>
    public static void Write(string folder, string isoFile, string label, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var nextId = 16;
        var root = new Node("", null, 4, 0);
        void Walk(string dir, Node node)
        {
            foreach (var f in Directory.GetFiles(dir).Order(StringComparer.Ordinal))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                node.Files.Add(new Node(Path.GetFileName(f), node, ext is ".m2ts" or ".ssif" ? 249 : 5, nextId++) { Size = new FileInfo(f).Length });
            }
            foreach (var d in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
            {
                var sub = new Node(Path.GetFileName(d), node, 4, nextId++);
                node.Dirs.Add(sub);
                Walk(d, sub);
            }
        }
        Walk(folder, root);
        Node Child(Node dir, string name) => dir.Dirs.Concat(dir.Files).FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                             ?? throw new InvalidDataException($"{name} missing in {folder}");
        var stream = Child(Child(root, "BDMV"), "STREAM");
        var pairs = Interleaved(folder);
        Node? ssifDir = null;
        if (pairs.Count > 0 && !stream.Dirs.Any(d => d.Name == "SSIF")) stream.Dirs.Add(ssifDir = new Node("SSIF", stream, 4, nextId++));
        ssifDir ??= stream.Dirs.FirstOrDefault(d => d.Name == "SSIF");

        // the data, in sectors from the first one after the metadata (placed later): each 3D film as dependent then base piece, in turns
        var copies = new List<(string Source, long Offset, long Length, long Sector)>();
        long cur = 0;
        void Place(Node file, string source, long offset, long length, Node? shared = null)
        {
            for (var done = 0L; done < length; done += MaxExtent)
            {
                var part = Math.Min(MaxExtent, length - done);
                file.Extents.Add((cur, part));
                if (shared != null)
                {
                    var last = shared.Extents.Count - 1;
                    if (last >= 0 && shared.Extents[last] is var (l, n) && l + n / S == cur && n % S == 0 && n + part <= MaxExtent) shared.Extents[last] = (l, n + part);
                    else shared.Extents.Add((cur, part));
                    shared.Size += part;
                }
                copies.Add((source, offset + done, part, cur));
                cur += (part + S - 1) / S;
            }
        }
        var paired = pairs.SelectMany(p => new[] { p.Base + ".m2ts", p.Dependent + ".m2ts" }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Lay(Node node, string dir)
        {
            foreach (var f in node.Files.ToList())
            {
                if (f.Extents.Count > 0) continue; // a .ssif, made of its film's pieces
                if (node == stream && paired.Contains(f.Name))
                {
                    if (pairs.FirstOrDefault(p => p.Base + ".m2ts" == f.Name) is not { Base: not null } pair) continue; // the dependent clip is laid with its base
                    var dep = Child(stream, pair.Dependent + ".m2ts");
                    var ssif = new Node(pair.Base + ".ssif", ssifDir, 249, nextId++);
                    ssifDir!.Files.Add(ssif);
                    var (bFile, dFile) = (Path.Combine(dir, f.Name), Path.Combine(dir, dep.Name));
                    var (bCuts, dCuts) = (pair.BaseCuts.Append(f.Size).ToList(), pair.DependentCuts.Append(dep.Size).ToList());
                    for (var i = 0; i + 1 < bCuts.Count; i++)
                    {
                        // the .ssif runs through every piece: only the very last may end inside a sector (tsMuxeR fills its clips to 6144-byte units)
                        if ((dCuts[i + 1] - dCuts[i]) % S != 0 || (bCuts[i + 1] - bCuts[i]) % S != 0 && i + 2 < bCuts.Count)
                            throw new InvalidDataException($"{f.Name} / {dep.Name}: a 3D piece does not end on a sector");
                        Place(dep, dFile, dCuts[i], dCuts[i + 1] - dCuts[i], ssif);
                        Place(f, bFile, bCuts[i], bCuts[i + 1] - bCuts[i], ssif);
                    }
                    log?.Invoke($"image: {f.Name} and {dep.Name} in turns, read as {ssif.Name} by a 3D player ({bCuts.Count - 1} pieces each)");
                }
                else if (f.Size > 0) Place(f, Path.Combine(dir, f.Name), 0, f.Size);
            }
            foreach (var d in node.Dirs.Where(d => d != ssifDir || Directory.Exists(Path.Combine(dir, d.Name)))) Lay(d, Path.Combine(dir, d.Name));
        }
        Lay(root, folder);

        // the metadata partition: file set and terminating descriptors, then each directory (entry + identifiers) and file (entry + continuations)
        var system = new Node("", null, 13, 0);
        var mapping = new Node("*UDF Unique ID Mapping Data", system, 5, 0);
        system.Files.Add(mapping);
        int files = 0, dirs = 0;
        var entries = new List<(int Id, int Parent, int Lbn)>();
        int Allocate(Node n, int sector)
        {
            if (n.IsDir) { if (n.Type == 4) dirs++; n.Sectors = 1 + (n.DirBytes + S - 1) / S; }
            else { if (n.Id != 0) files++; n.Sectors = 1 + Continuations(n.Extents.Count); }
            n.Lbn = sector;
            sector += n.Sectors;
            if (n.Id != 0) entries.Add((n.Id, n.Parent!.Lbn, n.Lbn));
            foreach (var f in n.Files) sector = Allocate(f, sector);
            foreach (var d in n.Dirs) sector = Allocate(d, sector);
            return sector;
        }
        var systemLbn = Allocate(root, 2);
        var metaEnd = Allocate(system, systemLbn);
        var total = copies.Sum(c => c.Length);
        var metaLen = Block * Math.Max(2 + (int)Math.Ceiling(total / 16e9), (metaEnd * S + Block - 1) / Block); // what tsMuxeR reserves, more if needed
        if (entries.Count > (Block - 48) / 16) throw new InvalidDataException("too many files for one image");

        // where everything goes, in sectors of the image
        var mapLbn = MetaStart + metaLen / S;
        var dataStart = mapLbn + Block / S;
        var dataEnd = dataStart + cur;
        var mirrorEntry = dataEnd + (Block / S - 1 - dataEnd % (Block / S) + Block / S) % (Block / S); // the mirror's data starts on 64 K
        var mirror = mirrorEntry + 1;
        var partitionEnd = mirror + metaLen / S;
        var sectors = partitionEnd + 288;
        foreach (var n in All(root)) for (var i = 0; i < n.Extents.Count; i++) n.Extents[i] = (n.Extents[i].Lbn + dataStart - Partition, n.Extents[i].Length);

        var mapData = new byte[48 + 16 * entries.Count];
        Ascii(mapData, 1, Id);
        W32(mapData, 36, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var o = 48 + 16 * i;
            W32(mapData, o, entries[i].Id); W32(mapData, o + 4, entries[i].Parent); W32(mapData, o + 8, entries[i].Lbn); W16(mapData, o + 12, 1); W16(mapData, o + 14, 1);
        }
        mapping.Extents.Add((mapLbn - Partition, mapData.Length));
        mapping.Size = mapData.Length;

        var now = DateTimeOffset.Now;
        var meta = new byte[metaLen];
        Fsd(meta.AsSpan(0, S), label, systemLbn, now);
        Terminating(meta.AsSpan(S, S), 1);
        foreach (var n in All(root).Concat(All(system))) WriteNode(meta, n, now);

        log?.Invoke($"image: {isoFile} ({sectors * (long)S / 1e9:0.00} GB, UDF 2.50 written by BurnSmith)");
        using (var o = new FileStream(isoFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            o.SetLength(sectors * (long)S);
            void At(long sector, ReadOnlySpan<byte> data) { o.Position = sector * S; o.Write(data); }
            var b = new byte[S];
            foreach (var (s, name) in new[] { (16, "BEA01"), (17, "NSR03"), (18, "TEA01") })
            {
                Array.Clear(b); Ascii(b, 1, name); b[6] = 1; At(s, b);
            }
            Volume(o, 32, label, partitionEnd, mirror, now);
            Volume(o, partitionEnd + 32, label, partitionEnd, mirror, now);
            Array.Clear(b); Integrity(b, 64, files, dirs, nextId, partitionEnd, metaLen, now); At(64, b);
            Array.Clear(b); Terminating(b, 65); At(65, b);
            foreach (var s in new[] { 256, partitionEnd + 31, sectors - 1 }) { Array.Clear(b); Anchor(b, s, partitionEnd); At(s, b); }
            Array.Clear(b); MetadataEntry(b, 250, 0, MetaStart - Partition, metaLen, now); At(Partition, b);
            Array.Clear(b); MetadataEntry(b, 251, (int)(mirrorEntry - Partition), (int)(mirror - Partition), metaLen, now); At(mirrorEntry, b);
            At(MetaStart, meta);
            At(mirror, meta);
            At(mapLbn, mapData);

            var buffer = new byte[4 << 20];
            long copied = 0;
            foreach (var (source, offset, length, sector) in copies)
            {
                using var i = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                i.Position = offset;
                o.Position = (dataStart + sector) * S;
                for (var left = length; left > 0;)
                {
                    cancel.ThrowIfCancellationRequested();
                    var n = i.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n == 0) throw new EndOfStreamException($"{source} is shorter than when it was measured");
                    o.Write(buffer, 0, n);
                    left -= n;
                    copied += n;
                    progress?.Report(copied / (double)Math.Max(1, total));
                }
            }
        }
    }

    static IEnumerable<Node> All(Node n) => new[] { n }.Concat(n.Files.SelectMany(All)).Concat(n.Dirs.SelectMany(All));

    /// <summary>The sectors of allocation extent descriptors a file needs past its entry (how tsMuxeR splits them, a lone last extent kept in place of the link).</summary>
    static int Continuations(int extents)
    {
        var end = Math.Min(InEntry, extents);
        if (extents - end == 1) end++;
        var count = 0;
        while (end < extents)
        {
            end = Math.Min(end + InContinuation, extents);
            if (extents - end == 1) end++;
            count++;
        }
        return count;
    }

    static void WriteNode(byte[] meta, Node n, DateTimeOffset now)
    {
        var at = n.Lbn * S;
        if (n.IsDir)
        {
            var data = meta.AsSpan(at + S, n.DirBytes);
            var o = 0;
            Fid(data, ref o, n.Lbn + 1, 0x0A, "", n.Parent?.Lbn ?? n.Lbn, n.Parent?.Id ?? 0);
            foreach (var c in n.Files.Concat(n.Dirs)) Fid(data, ref o, n.Lbn + 1 + o / S, c.IsDir ? 0x02 : c.Id == 0 ? 0x10 : 0, c.Name, c.Lbn, c.Id);
            var e = meta.AsSpan(at, S);
            Entry(e, n.Lbn, n.Type, n.DirBytes, n.Dirs.Count + 1, 0x14A5, false, n.Id, now);
            W32(e, 212, 8);
            W32(e, 216, n.DirBytes); W32(e, 220, n.Lbn + 1); // short AD in the metadata partition
            Crc(e, 224);
            return;
        }
        var entry = meta.AsSpan(at, S);
        Entry(entry, n.Lbn, n.Type, n.Size, 1, 0x1084, n.Name.StartsWith("*UDF"), n.Id, now); // the unique id mapping file is a named stream
        entry[34] |= 1; // long ADs
        var x = n.Extents;
        var end = Math.Min(InEntry, x.Count);
        if (x.Count - end == 1) end++;
        var p = 216;
        for (var i = 0; i < end; i++, p += 16) LongAd(entry[p..], x[i].Length, x[i].Lbn, 0, 0);
        var sector = n.Lbn;
        if (end < x.Count) { LongAd(entry[p..], S | 0xC0000000L, sector + 1, 1, 0); p += 16; }
        W32(entry, 212, p - 216);
        Crc(entry, p);
        while (end < x.Count)
        {
            sector++;
            var aed = meta.AsSpan(sector * S, S);
            Tag(aed, 258, sector);
            var start = end;
            end = Math.Min(end + InContinuation, x.Count);
            if (x.Count - end == 1) end++;
            p = 24;
            for (var i = start; i < end; i++, p += 16) LongAd(aed[p..], x[i].Length, x[i].Lbn, 0, 0);
            if (end < x.Count) { LongAd(aed[p..], S | 0xC0000000L, sector + 1, 1, 0); p += 16; }
            W32(aed, 20, p - 24);
            Crc(aed, p);
        }
    }

    /// <summary>A file identifier descriptor (tag location: the sector it starts in), padded to 4 bytes.</summary>
    static void Fid(Span<byte> dir, ref int o, int sector, int characteristics, string name, int icb, int id)
    {
        var f = dir[o..];
        Tag(f, 257, sector);
        W16(f, 16, 1);
        f[18] = (byte)characteristics;
        f[19] = (byte)(name.Length == 0 ? 0 : name.Length + 1);
        LongAd(f[20..], S, icb, 1, id);
        var len = 38;
        if (name.Length > 0) { f[38] = 8; Latin1(f[39..], name, name.Length); len += name.Length + 1; }
        else len += 2;
        len = (len + 3) / 4 * 4;
        Crc(f, len);
        o += len;
    }

    /// <summary>The common part of an extended file entry (ICB tag, owner, permissions, sizes, times, unique id).</summary>
    static void Entry(Span<byte> e, int lbn, int type, long length, int links, int permissions, bool stream, int id, DateTimeOffset now)
    {
        Tag(e, 266, lbn);
        W16(e, 20, 4); W16(e, 24, 1); e[27] = (byte)type; W16(e, 34, 0x20 | (stream ? 0x2000 : 0));
        W32(e, 36, -1); W32(e, 40, -1);
        W32(e, 44, type >= 250 ? 0 : permissions);
        W16(e, 48, type >= 250 ? 0 : links);
        W64(e, 56, length); W64(e, 64, length);
        W64(e, 72, (length + S - 1) / S);
        foreach (var t in new[] { 80, 92, 104, 116 }) Time(e[t..], now);
        W32(e, 128, 1);
        Ascii(e, 169, Id);
        W32(e, 200, id);
    }

    static void MetadataEntry(Span<byte> e, int type, int lbn, int at, int length, DateTimeOffset now)
    {
        Entry(e, lbn, type, length, 0, 0, false, 0, now);
        W32(e, 212, 8); W32(e, 216, length); W32(e, 220, at);
        Crc(e, 224);
    }

    static void Fsd(Span<byte> b, string label, int systemLbn, DateTimeOffset now)
    {
        Tag(b, 256, 0);
        Time(b[16..], now);
        W16(b, 28, 3); W16(b, 30, 3); W32(b, 32, 1); W32(b, 36, 1);
        Ascii(b, 49, "OSTA Compressed Unicode"); DString(b[112..], label, 128);
        Ascii(b, 241, "OSTA Compressed Unicode"); DString(b[304..], label, 32);
        LongAd(b[400..], S, 2, 1, 0);
        Regid(b[416..], "*OSTA UDF Compliant"); b[442] = 3;
        LongAd(b[464..], S, systemLbn, 1, 0);
        Crc(b, 512);
    }

    static void Terminating(Span<byte> b, int sector) { Tag(b, 8, sector); Crc(b, 512); }

    /// <summary>A volume descriptor sequence (primary, implementation use, partition, logical volume, unallocated space, terminating) from <paramref name="first"/>.</summary>
    static void Volume(FileStream o, long first, string label, long partitionEnd, long mirror, DateTimeOffset now)
    {
        var b = new byte[S];
        void Put(long sector) { o.Position = sector * S; o.Write(b); Array.Clear(b); }
        var s = (int)first;
        Tag(b, 1, s);
        DString(b.AsSpan(24), label, 32);
        W16(b, 56, 1); W16(b, 58, 1); W16(b, 60, 2); W16(b, 62, 2); W32(b, 64, 1); W32(b, 68, 1);
        DString(b.AsSpan(72), $"{(uint)label.GetHashCode():X8}        {label}", 128);
        Ascii(b, 201, "OSTA Compressed Unicode"); Ascii(b, 265, "OSTA Compressed Unicode");
        Ascii(b, 345, Id); Time(b.AsSpan(376), now); Ascii(b, 389, Id); Ascii(b, 420, Id);
        b[488] = 1;
        Crc(b, 512); Put(s);

        Tag(b, 4, s + 1);
        W32(b, 16, 1);
        Regid(b.AsSpan(20), "*UDF LV Info");
        Ascii(b, 53, "OSTA Compressed Unicode"); DString(b.AsSpan(116), label, 128);
        Ascii(b, 0x161, Id); Ascii(b, 0x180, Id);
        Crc(b, 512); Put(s + 1);

        Tag(b, 5, s + 2);
        W32(b, 16, 2); W16(b, 20, 1); W16(b, 22, 0); Ascii(b, 25, "+NSR03");
        W32(b, 184, 1); W32(b, 188, Partition); W32(b, 192, (int)(partitionEnd - Partition));
        Ascii(b, 0xC5, Id); Ascii(b, 0xE4, Id);
        Crc(b, 512); Put(s + 2);

        Tag(b, 6, s + 3);
        W32(b, 16, 3); Ascii(b, 21, "OSTA Compressed Unicode"); DString(b.AsSpan(84), label, 128);
        W32(b, 212, S); Regid(b.AsSpan(216), "*OSTA UDF Compliant"); b[242] = 3;
        LongAd(b.AsSpan(248), 2 * S, 0, 1, 0); // the file set descriptor (and its terminator)
        W32(b, 264, 6 + 64); W32(b, 268, 2);
        Ascii(b, 273, Id); Ascii(b, 304, Id);
        W32(b, 432, 0x8000); W32(b, 436, 64);
        b[440] = 1; b[441] = 6; W16(b, 442, 1); W16(b, 444, 0);
        b[446] = 2; b[447] = 64; Regid(b.AsSpan(450), "*UDF Metadata Partition");
        W16(b, 482, 1); W16(b, 484, 0); W32(b, 486, 0); W32(b, 490, (int)(mirror - 1 - Partition)); W32(b, 494, -1);
        W32(b, 498, 32); W16(b, 502, 32); b[504] = 1;
        Crc(b, 510); Put(s + 3);

        Tag(b, 7, s + 4); W32(b, 16, 4); Crc(b, 24); Put(s + 4);
        Terminating(b, s + 5); Put(s + 5);
    }

    static void Integrity(Span<byte> b, int sector, int files, int dirs, int nextId, long partitionEnd, int metaLen, DateTimeOffset now)
    {
        Tag(b, 9, sector);
        Time(b[16..], now);
        W32(b, 28, 1); W32(b, 40, nextId);
        W32(b, 72, 2); W32(b, 76, 46 + Id.Length);
        W32(b, 88, (int)(partitionEnd - Partition)); W32(b, 92, metaLen / S);
        Ascii(b, 97, Id);
        W32(b, 128, files); W32(b, 132, dirs); W16(b, 136, 0x250); W16(b, 138, 0x250); W16(b, 140, 0x250);
        Ascii(b, 142, Id);
        Crc(b, 142 + Id.Length);
    }

    static void Anchor(Span<byte> b, long sector, long partitionEnd)
    {
        Tag(b, 2, (int)sector);
        W32(b, 16, 0x8000); W32(b, 20, 32); W32(b, 24, 0x8000); W32(b, 28, (int)(partitionEnd + 32));
        Crc(b, 512);
    }

    // ---- encoding ----

    static void Tag(Span<byte> b, int id, int location) { W16(b, 0, id); W16(b, 2, 3); W16(b, 6, 1); W32(b, 12, location); }

    static void Crc(Span<byte> b, int length)
    {
        ushort crc = 0;
        foreach (var x in b[16..length])
        {
            crc ^= (ushort)(x << 8);
            for (var k = 0; k < 8; k++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        W16(b, 8, crc); W16(b, 10, length - 16);
        byte sum = 0;
        for (var i = 0; i < 16; i++) if (i != 4) sum += b[i];
        b[4] = sum;
    }

    static void LongAd(Span<byte> b, long length, long lbn, int partition, int id) { W32(b, 0, (int)length); W32(b, 4, (int)lbn); W16(b, 8, partition); W32(b, 12, id); }

    static void Time(Span<byte> b, DateTimeOffset t)
    {
        W16(b, 0, (1 << 12) | ((int)t.Offset.TotalMinutes & 0xFFF));
        W16(b, 2, t.Year); b[4] = (byte)t.Month; b[5] = (byte)t.Day; b[6] = (byte)t.Hour; b[7] = (byte)t.Minute; b[8] = (byte)t.Second;
    }

    /// <summary>A d-string of <paramref name="field"/> bytes: compression id 8 (one byte per character), the text, its length in the last byte.</summary>
    static void DString(Span<byte> b, string text, int field)
    {
        if (text.Length == 0) return;
        var n = Math.Min(text.Length, field - 2);
        b[0] = 8;
        Latin1(b[1..], text, n);
        b[field - 1] = (byte)(n + 1);
    }

    static void Latin1(Span<byte> b, string text, int n) { for (var i = 0; i < n; i++) b[i] = text[i] < 256 ? (byte)text[i] : (byte)'_'; }

    /// <summary>An entity identifier with the UDF 2.50 suffix.</summary>
    static void Regid(Span<byte> b, string id) { Encoding.ASCII.GetBytes(id).CopyTo(b[1..]); b[24] = 0x50; b[25] = 0x02; }

    static void Ascii(Span<byte> b, int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b[at..]);
    static void W16(Span<byte> b, int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b[at..], (ushort)v);
    static void W32(Span<byte> b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b[at..], v);
    static void W64(Span<byte> b, int at, long v) => BinaryPrimitives.WriteInt64LittleEndian(b[at..], v);
}
