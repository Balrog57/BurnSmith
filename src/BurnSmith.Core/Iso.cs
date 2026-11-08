namespace BurnSmith.Core;

/// <summary>
/// ISO images through Windows' own IMAPI2FS (see <see cref="Imapi"/>), UDF 2.50 for Blu-ray, UDF 1.02 for DVD-Video.
/// Burning through ImgBurn when it is installed (the installer sets it up: it reaches drives IMAPI2 does not see), else IMAPI2.
/// </summary>
public static class Iso
{
    public static void Build(string discFolder, string isoFile, string label, bool dvd, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        Imapi.BuildImage(discFolder, Path.GetFullPath(isoFile), label, dvd, log, progress, cancel);
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

    /// <summary>Burns an image to the blank disc in <paramref name="drive"/> ("F:") and verifies it.</summary>
    public static void Burn(string isoFile, string drive, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var iso = Path.GetFullPath(isoFile);
        if (ImgBurn() is not { } exe) { Imapi.Burn(iso, drive, log, progress, verify: true, cancel); return; }
        if (!File.Exists(iso)) throw new FileNotFoundException("image not found", iso);
        if (drive.Length < 2 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':') throw new ArgumentException("drive must look like F:", nameof(drive));
        log?.Invoke($"burn: ImgBurn {Path.GetFileName(iso)} -> {drive[..2]} ({new FileInfo(iso).Length / 1e9:0.00} GB)");
        // DVDFab's switches (DVDFab64.exe): no question asked, it waits for a disc, closes on success and stays open on an error so the user can read it.
        // shortcut: untested on real media, and no progress reaches the app (ImgBurn's window shows it)
        Tools.WithCancel(cancel, () => Tools.Run(exe, ["/MODE", "ISOWRITE", "/WAITFORMEDIA", "/START", "/CLOSESUCCESS", "/IGNORELOCKVOLUME", "/NOIMAGEDETAILS",
            "/VERIFY", "YES", "/DEST", drive[..1].ToUpperInvariant(), "/SPEED", "MAX", "/SRC", iso], outlivesApp: true)); // a crash of the app must not spoil the disc being written
        log?.Invoke("done: disc written");
    }
}
