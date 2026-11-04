using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace BurnSmith.Core;

/// <summary>
/// Disc images and burning through Windows' own IMAPI2 / IMAPI2FS, so there is no burner to install or bundle.
/// Late-bound COM (dynamic) as in futureburn (MIT, sp00nznet): no interop assembly, no NuGet package.
/// </summary>
public static class Imapi
{
    public sealed record Drive(string Id, string Name, string Letter);
    public sealed record Media(string Kind, bool Blank, long FreeBytes, long TotalBytes);

    const int Sector = 2048;
    const int FsiUdf = 4; // FsiFileSystemUDF
    static readonly Guid DataEvents = new("2735413C-7F64-5B0F-8F00-5D77AFBE261E"); // DDiscFormat2DataEvents, dispid 0x200 = Update

    static readonly string[] MediaKinds =
    [
        "inconnu", "CD-ROM", "CD-R", "CD-RW", "DVD-ROM", "DVD-RAM", "DVD+R", "DVD+RW", "DVD+R DL", "DVD-R", "DVD-RW", "DVD-R DL",
        "disque", "DVD+RW DL", "HD DVD-ROM", "HD DVD-R", "HD DVD-RAM", "BD-ROM", "BD-R", "BD-RE",
    ];

    static dynamic Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new PlatformNotSupportedException($"{progId} is not available (Windows only)"))!;

    static void Release(object? com) { if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); }

    /// <summary>The recorders Windows sees. Empty when no optical burner is connected or the drive is not visible to the system.</summary>
    public static List<Drive> Drives()
    {
        dynamic master = Create("IMAPI2.MsftDiscMaster2");
        var list = new List<Drive>();
        try
        {
            int count = master.Count;
            for (var i = 0; i < count; i++)
            {
                string id = master[i];
                dynamic recorder = Create("IMAPI2.MsftDiscRecorder2");
                try
                {
                    recorder.InitializeDiscRecorder(id);
                    var letters = (recorder.VolumePathNames as Array)?.Cast<string>().ToArray() ?? [];
                    list.Add(new Drive(id, $"{((string)recorder.VendorId).Trim()} {((string)recorder.ProductId).Trim()}", letters.FirstOrDefault()?.TrimEnd('\\') ?? ""));
                }
                finally { Release(recorder); }
            }
        }
        finally { Release(master); }
        return list;
    }

    static Drive FindDrive(string letter)
    {
        if (letter.Length < 2 || !char.IsAsciiLetter(letter[0]) || letter[1] != ':') throw new ArgumentException("drive must look like F:", nameof(letter));
        return Drives().FirstOrDefault(d => d.Letter.StartsWith(letter[..2], StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{letter[..2]} is not a disc burner that Windows can see");
    }

    static dynamic OpenRecorder(Drive drive)
    {
        dynamic recorder = Create("IMAPI2.MsftDiscRecorder2");
        recorder.InitializeDiscRecorder(drive.Id);
        return recorder;
    }

    /// <summary>What is in the drive (null when it is empty or Windows cannot read it).</summary>
    public static Media? Inspect(string letter)
    {
        var recorder = OpenRecorder(FindDrive(letter));
        dynamic format = Create("IMAPI2.MsftDiscFormat2Data");
        try
        {
            format.Recorder = recorder;
            if (!(bool)format.IsCurrentMediaSupported(recorder)) return null;
            int kind = format.CurrentPhysicalMediaType;
            return new Media(kind >= 0 && kind < MediaKinds.Length ? MediaKinds[kind] : $"type {kind}", (bool)format.MediaHeuristicallyBlank,
                             Convert.ToInt64(format.FreeSectorsOnMedia) * Sector, Convert.ToInt64(format.TotalSectorsOnMedia) * Sector);
        }
        catch (COMException) { return null; }
        finally { Release(format); Release(recorder); }
    }

    /// <summary>
    /// Builds a UDF image of <paramref name="folder"/>'s contents: UDF 2.50 for Blu-ray, 1.02 for DVD-Video (IMAPI2FS, in Windows).
    /// </summary>
    public static void BuildImage(string folder, string isoFile, string label, bool dvd, Action<string>? log = null, IProgress<double>? progress = null)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(Path.Combine(folder, dvd ? "VIDEO_TS" : "BDMV")))
            throw new DirectoryNotFoundException($"'{folder}' does not contain a {(dvd ? "VIDEO_TS" : "BDMV")} folder.");
        label = string.Concat(label.Where(c => !char.IsControl(c))).Trim();
        if (label.Length == 0) label = "BURNSMITH";
        if (label.Length > 32) label = label[..32];

        log?.Invoke("iso: IMAPI2FS (UDF " + (dvd ? "1.02" : "2.50") + ")");
        dynamic fsi = Create("IMAPI2FS.MsftFileSystemImage");
        try
        {
            fsi.FileSystemsToCreate = FsiUdf;
            fsi.UDFRevision = dvd ? 0x102 : 0x250;
            fsi.FreeMediaBlocks = 60_000_000; // its default is a CD's size; the real capacity is checked when burning
            fsi.VolumeName = label;
            fsi.Root.AddTree(folder, false);
            dynamic result = fsi.CreateResultImage();
            try
            {
                long total = Convert.ToInt64(result.TotalBlocks) * Convert.ToInt32(result.BlockSize);
                var stream = (IStream)result.ImageStream;
                try
                {
                    using var output = File.Create(isoFile);
                    var buffer = new byte[1 << 20];
                    var read = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        long done = 0;
                        while (true)
                        {
                            stream.Read(buffer, buffer.Length, read);
                            var n = Marshal.ReadInt32(read);
                            if (n == 0) break;
                            output.Write(buffer, 0, n);
                            done += n;
                            progress?.Report(Math.Min(1, (double)done / total));
                        }
                    }
                    finally { Marshal.FreeHGlobal(read); }
                }
                finally { Release(stream); }
            }
            finally { Release(result); }
        }
        finally { Release(fsi); }
        if (UdfRevision(isoFile) is not { } rev) throw new IOException("The image written by Windows has no UDF volume.");
        log?.Invoke($"done: {isoFile} (UDF {rev.Major}.{rev.Minor:00}, {new FileInfo(isoFile).Length / 1e9:0.00} GB)");
    }

    /// <summary>The UDF revision recorded in an image (looked up in its first 4 MiB), or null when it has none.</summary>
    public static (int Major, int Minor)? UdfRevision(string isoFile)
    {
        var head = new byte[4 << 20];
        using var f = File.OpenRead(isoFile);
        var n = f.Read(head, 0, head.Length);
        var at = head.AsSpan(0, n).IndexOf("*OSTA UDF Compliant"u8);
        if (at < 0 || at + 25 > n) return null;
        var rev = head[at + 24] << 8 | head[at + 23]; // 2 bytes after the 23-byte identifier, little-endian, BCD: 0x0250 = 2.50
        return (rev >> 8, (rev >> 4 & 0xF) * 10 + (rev & 0xF));
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern IStream SHCreateStreamOnFileEx(string file, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template);

    /// <summary>
    /// Writes an image to the blank disc in <paramref name="letter"/> ("F:"), closes the disc and, with <paramref name="verify"/>, reads it back.
    /// shortcut: not tried on real media in this project's test PC (no burner visible there); speed is the drive's choice.
    /// </summary>
    public static void Burn(string isoFile, string letter, Action<string>? log = null, IProgress<double>? progress = null,
                            bool verify = true, CancellationToken cancel = default)
    {
        if (!File.Exists(isoFile)) throw new FileNotFoundException("image not found", isoFile);
        var size = new FileInfo(isoFile).Length;
        if (size == 0 || size % Sector != 0) throw new InvalidDataException("not a disc image (size is not a multiple of 2048 bytes)");
        var drive = FindDrive(letter);
        var media = Inspect(letter) ?? throw new InvalidOperationException($"No disc Windows can write to in {letter} (insert a blank disc).");
        if (!media.Blank) throw new InvalidOperationException($"The {media.Kind} in {letter} is not blank: erase it first (rewritable) or insert a blank disc.");
        if (media.Kind is "CD-ROM" or "DVD-ROM" or "BD-ROM") throw new InvalidOperationException($"{media.Kind} cannot be written.");
        if (size > media.FreeBytes) throw new InvalidOperationException($"The image ({size / 1e9:0.00} GB) does not fit on the {media.Kind} ({media.FreeBytes / 1e9:0.00} GB free).");

        log?.Invoke($"burn: {Path.GetFileName(isoFile)} -> {letter} ({media.Kind}, {size / 1e9:0.00} GB)");
        var recorder = OpenRecorder(drive);
        dynamic format = Create("IMAPI2.MsftDiscFormat2Data");
        IStream? source = null;
        try
        {
            format.Recorder = recorder;
            format.ClientName = "BurnSmith";
            format.ForceMediaToBeClosed = true;
            var start = Environment.TickCount64;
            Action<object, object> update = (_, args) =>
            {
                try
                {
                    dynamic a = args;
                    long count = Convert.ToInt64(a.SectorCount), written = Convert.ToInt64(a.LastWrittenLba) - Convert.ToInt64(a.StartLba);
                    var action = Convert.ToInt32(a.CurrentAction); // 0 checking, 1 formatting, 2 initialising, 3 calibrating, 4 writing, 5 finalising, 6 done
                    if (action == 4 && count > 0) progress?.Report(Math.Clamp((double)written / count, 0, 1));
                    else if (action == 5) log?.Invoke("burn: finalisation du disque…");
                }
                catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            };
            ComEventsHelper.Combine(format, DataEvents, 0x200, update);
            using var registration = cancel.Register(() => { try { format.CancelWrite(); } catch (COMException) { } });
            source = SHCreateStreamOnFileEx(Path.GetFullPath(isoFile), 0x20, 0, false, IntPtr.Zero); // STGM_READ | STGM_SHARE_DENY_WRITE
            try { format.Write(source); }
            catch (COMException e) when (cancel.IsCancellationRequested) { throw new OperationCanceledException("Gravure annulée.", e, cancel); }
            finally { ComEventsHelper.Remove(format, DataEvents, 0x200, update); }
            log?.Invoke($"burn: écrit en {(Environment.TickCount64 - start) / 1000} s");
        }
        finally { Release(source); Release(format); Release(recorder); }

        if (verify) Verify(isoFile, letter, log, progress, cancel);
        log?.Invoke("done: disc written");
    }

    /// <summary>Reads the disc back through its drive and compares every byte with the image.</summary>
    public static void Verify(string isoFile, string letter, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        log?.Invoke("verify: lecture du disque…");
        const int chunk = 1 << 20;
        var a = new byte[chunk];
        var b = new byte[chunk];
        using var image = File.OpenRead(isoFile);
        using var disc = File.OpenHandle($@"\\.\{letter[..2]}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long offset = 0, total = image.Length;
        while (offset < total)
        {
            cancel.ThrowIfCancellationRequested();
            var n = (int)Math.Min(chunk, total - offset);
            image.ReadExactly(a, 0, n);
            if (RandomAccess.Read(disc, b.AsSpan(0, n), offset) != n || !a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n)))
                throw new IOException($"Verification failed near {offset / 1048576} MiB: the disc does not match the image.");
            offset += n;
            progress?.Report((double)offset / total);
        }
        log?.Invoke("verify: identique à l'image");
    }

    /// <summary>Erases a rewritable disc (BD-RE, DVD±RW) quickly.</summary>
    public static void Erase(string letter, Action<string>? log = null)
    {
        var recorder = OpenRecorder(FindDrive(letter));
        dynamic erase = Create("IMAPI2.MsftDiscFormat2Erase");
        try
        {
            erase.Recorder = recorder;
            erase.ClientName = "BurnSmith";
            erase.FullErase = false;
            log?.Invoke($"erase: {letter}");
            erase.EraseMedia();
        }
        finally { Release(erase); Release(recorder); }
    }
}
