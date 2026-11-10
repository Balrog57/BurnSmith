namespace BurnSmith.Core;

/// <summary>
/// ISO images through Windows' own IMAPI2FS (see <see cref="Imapi"/>), UDF 2.50 for Blu-ray, UDF 1.02 for DVD-Video.
/// Burning through ImgBurn when it is installed (the installer sets it up: it reaches drives IMAPI2 does not see), else IMAPI2.
/// </summary>
public static class Iso
{
    public static void Build(string discFolder, string isoFile, string label, bool dvd, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        // a 3D film needs its .ssif to share the sectors of its two clips, which IMAPI cannot write
        if (!dvd && Udf.Interleaved(discFolder).Count > 0)
        {
            // under another name until it is whole, as Imapi.BuildImage does: a cancelled image already has its full size and its descriptors
            var part = Path.GetFullPath(isoFile) + ".part";
            try { Udf.Write(discFolder, part, label, log, progress, cancel); }
            catch { try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
            File.Move(part, Path.GetFullPath(isoFile), true);
        }
        else Imapi.BuildImage(discFolder, Path.GetFullPath(isoFile), label, dvd, log, progress, cancel);
    }

    /// <summary>ImgBurn.exe: in a tools folder or on the PATH (<see cref="Tools.Find"/>), else where its own setup puts it; null when absent.</summary>
    public static string? ImgBurn()
    {
        try { return Tools.Find("ImgBurn.exe"); }
        catch (FileNotFoundException)
        {
            return new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
                .Select(f => Path.Combine(Environment.GetFolderPath(f), "ImgBurn", "ImgBurn.exe")).FirstOrDefault(File.Exists);
        }
    }

    /// <summary>The burners IMAPI2 sees, plus, when ImgBurn can burn them, the other optical drives Windows lists.</summary>
    public static List<Imapi.Drive> Drives()
    {
        List<Imapi.Drive> drives;
        try { drives = Imapi.Drives().Where(d => d.Letter.Length > 0).ToList(); }
        catch (Exception) { drives = []; } // IMAPI2 unavailable
        if (ImgBurn() == null) return drives;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom))
        {
            var letter = d.Name.TrimEnd('\\');
            if (!drives.Any(x => x.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase))) drives.Add(new Imapi.Drive(letter, "Lecteur optique", letter));
        }
        return drives;
    }

    /// <summary>The write speeds offered, × the disc's 1x (0 = the drive's fastest): values found in ImgBurn's list for DVD as for Blu-ray.</summary>
    public static readonly int[] Speeds = [0, 8, 6, 4];

    /// <summary>ImgBurn's log of the last burn, next to burnsmith.log.</summary>
    public static string ImgBurnLog() => Path.Combine(Path.GetDirectoryName(AppFiles.ErrorLog())!, "imgburn.log");

    /// <summary>
    /// DVDFab's switches (DVDFab64.exe): no question asked, it waits for a disc, closes on success and stays open on an error so the user can read it.
    /// /SPEED must match an entry of ImgBurn's drop-down list ("4x", its ReadMe); the log is written when ImgBurn closes.
    /// </summary>
    public static List<string> ImgBurnArgs(string iso, string drive, int speed, string logFile) =>
        ["/MODE", "ISOWRITE", "/WAITFORMEDIA", "/START", "/CLOSESUCCESS", "/IGNORELOCKVOLUME", "/NOIMAGEDETAILS", "/VERIFY", "YES",
         "/DEST", drive[..1].ToUpperInvariant(), "/SPEED", speed > 0 ? $"{speed}x" : "MAX", "/LOG", logFile, "/SRC", iso];

    /// <summary>Burns an image to the blank disc in <paramref name="drive"/> ("F:") at <paramref name="speed"/> (× 1x, 0 = fastest) and verifies it.</summary>
    public static void Burn(string isoFile, string drive, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken cancel = default, int speed = 0)
    {
        var iso = Path.GetFullPath(isoFile);
        if (ImgBurn() is not { } exe) { Imapi.Burn(iso, drive, log, progress, verify: true, cancel, speed); return; }
        if (!File.Exists(iso)) throw new FileNotFoundException("image not found", iso);
        if (drive.Length < 2 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':') throw new ArgumentException("drive must look like F:", nameof(drive));
        log?.Invoke($"burn: ImgBurn {Path.GetFileName(iso)} -> {drive[..2]} ({new FileInfo(iso).Length / 1e9:0.00} GB, {(speed > 0 ? $"{speed}x" : "vitesse max")})");
        var imgLog = ImgBurnLog();
        try { File.Delete(imgLog); } catch (IOException) { } // an older burn's log must not pass for this one's
        // shortcut: untested on real media, and no progress reaches the app (ImgBurn's window shows it)
        try { Tools.WithCancel(cancel, () => Tools.Run(exe, ImgBurnArgs(iso, drive, speed, imgLog), outlivesApp: true)); } // a crash of the app must not spoil the disc being written
        catch (InvalidOperationException e) when (File.Exists(imgLog))
        {
            // ImgBurn prints nothing: the reason is in its log (exit 1 = could not start, 2 = write failed, 3 = verify failed)
            throw new InvalidOperationException($"{e.Message.Split('\n')[0]}\n{string.Join("\n", File.ReadLines(imgLog).TakeLast(15))}\n(journal complet : {imgLog})", e);
        }
        if (File.Exists(imgLog)) log?.Invoke($"journal d'ImgBurn : {imgLog}");
        log?.Invoke("done: disc written");
    }
}
