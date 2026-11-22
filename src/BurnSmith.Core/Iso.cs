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

    static readonly string[] VirtualDriveMarkers =
        ["MSFT VIRTUAL", "VIRTUAL DVD", "VIRTUAL CD", "CLONEDRIVE", "DAEMON", "DTSOFT", "ALCOHOL", "MAGICISO", "POWERISO", "ULTRAISO", "VBOX CD", "QEMU DVD", "VMWARE"];

    /// <summary>True when a drive model/name identifies a virtual mounted optical drive that cannot physically burn media.</summary>
    public static bool IsVirtualDriveName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && VirtualDriveMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The burners IMAPI2 sees, each with the disc in it (<see cref="Imapi.Drive.State"/>), plus, when ImgBurn can burn them, the other optical drives Windows lists.
    /// Any drive IMAPI2 knows as read-only or matching a virtual optical device is excluded.
    /// </summary>
    public static List<Imapi.Drive> Drives()
    {
        List<Imapi.Drive> seen;
        try { seen = Imapi.Drives().Where(d => d.Letter.Length > 0).ToList(); }
        catch (Exception) { seen = []; } // IMAPI2 unavailable
        var drives = seen.Where(d => d.Writes && !IsVirtualDriveName(d.Name))
            .Select(d => d with { State = DiscState(Disc(d.Letter), new DriveInfo(d.Letter).IsReady) }).ToList();
        if (ImgBurn() == null) return drives;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom))
        {
            var letter = d.Name.TrimEnd('\\');
            if (seen.Any(x => x.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase))) continue;
            var label = "";
            try { if (d.IsReady) label = d.VolumeLabel; } catch (IOException) { }
            if (IsVirtualDriveName(label)) continue;
            drives.Add(new Imapi.Drive(letter, "Lecteur optique", letter));
        }
        return drives;
    }

    /// <summary>The disc in a burner, or null: none, one Windows cannot write, or a drive only ImgBurn reaches.</summary>
    public static Imapi.Media? Disc(string letter)
    {
        try { return Imapi.Inspect(letter); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException or InvalidCastException) { return null; }
    }

    /// <summary>« BD-R vierge, 25 Go », « BD-RE déjà écrit », « pas de disque », or « disque non inscriptible » for a disc that is there (<paramref name="present"/>) but cannot be written.</summary>
    public static string DiscState(Imapi.Media? disc, bool present) =>
        disc == null ? (present ? "disque non inscriptible" : "pas de disque") : disc.Blank ? $"{disc.Kind} vierge, {disc.TotalBytes / 1e9:0.#} Go" : $"{disc.Kind} déjà écrit";

    /// <summary>
    /// True when the disc holds less than <paramref name="usableGb"/> (what the project fills, <c>Output.Usable</c>). A blank disc says its size, a rewritable one
    /// its size once erased; a written write-once disc cannot be told (it only says what it holds), so it is not judged here.
    /// </summary>
    public static bool TooSmall(Imapi.Media disc, double usableGb) =>
        (disc.Blank || disc.Kind.Contains("RW") || disc.Kind.Contains("RAM") || disc.Kind == "BD-RE") && disc.TotalBytes > 0 && disc.TotalBytes < usableGb * 1e9;

    /// <summary>The write speeds offered, × the disc's 1x (0 = the drive's fastest): values found in ImgBurn's list for DVD as for Blu-ray.</summary>
    public static readonly int[] Speeds = [0, 8, 6, 4, 2];

    /// <summary>ImgBurn's log of the last burn, next to burnsmith.log.</summary>
    public static string ImgBurnLog() => Path.Combine(Path.GetDirectoryName(AppFiles.ErrorLog())!, "imgburn.log");

    /// <summary>
    /// DVDFab's switches (DVDFab64.exe): no question asked, it waits for a disc, closes on success and stays open on an error so the user can read it.
    /// /SPEED must match an entry of ImgBurn's drop-down list ("4x", its ReadMe); the log is written when ImgBurn closes.
    /// </summary>
    public static List<string> ImgBurnArgs(string iso, string drive, int speed, string logFile, bool verify = true, bool eject = false) =>
        ["/MODE", "ISOWRITE", "/WAITFORMEDIA", "/START", "/CLOSESUCCESS", "/IGNORELOCKVOLUME", "/NOIMAGEDETAILS", "/VERIFY", verify ? "YES" : "NO", "/EJECT", eject ? "YES" : "NO",
         "/DEST", drive[..1].ToUpperInvariant(), "/SPEED", speed > 0 ? $"{speed}x" : "MAX", "/LOG", logFile, "/SRC", iso];

    /// <summary>
    /// ImgBurn's log shows a verify that ran to its end: a "Verifying" line, then "Operation Successfully Completed!" after it.
    /// ImgBurn exits 0 without one: verify aborted (tray left open after "Cycling Tray before Verify") or never started (Axel F, 2026-11-15, BD-RE on a USB BU40N).
    /// </summary>
    public static bool ImgBurnVerified(IEnumerable<string> logLines) =>
        logLines.SkipWhile(l => !l.Contains("Verifying")).Any(l => l.Contains("Operation Successfully Completed!"));

    static readonly string[] Rewritable = ["CD-RW", "DVD+RW", "DVD-RW", "DVD+RW DL", "BD-RE"];

    /// <summary>The kind ("BD-RE") of the rewritable disc in <paramref name="drive"/> when it already holds data; null when it is blank, write-once, or IMAPI2 cannot see it.</summary>
    public static string? UsedRewritable(string drive)
    {
        try { return Imapi.Inspect(drive) is { Blank: false } m && Rewritable.Contains(m.Kind) ? m.Kind : null; }
        catch (Exception e) when (e is ArgumentException or PlatformNotSupportedException or System.Runtime.InteropServices.COMException or InvalidCastException) { return null; }
    }

    /// <summary>
    /// Burns an image to the disc in <paramref name="drive"/> ("F:") at <paramref name="speed"/> (× 1x, 0 = fastest), then reads it back unless <paramref name="verify"/> is false,
    /// and ejects it with <paramref name="eject"/>. A rewritable disc that holds data is erased first with <paramref name="erase"/>, else refused.
    /// </summary>
    /// <returns>False when ImgBurn wrote the disc but did not complete a requested verification.</returns>
    public static bool Burn(string isoFile, string drive, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken cancel = default, int speed = 0,
                            bool verify = true, bool eject = false, bool erase = false)
    {
        var iso = Path.GetFullPath(isoFile);
        if (!File.Exists(iso)) throw new FileNotFoundException("image not found", iso);
        if (drive.Length < 2 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':') throw new ArgumentException("drive must look like F:", nameof(drive));
        if (UsedRewritable(drive) is { } used)
        {
            if (!erase) throw new InvalidOperationException($"Le {used} dans {drive[..2]} contient déjà des données : effacez-le avant de graver (burnsmith burn … --erase, ou burnsmith erase {drive[..2]}).");
            Imapi.Erase(drive, log);
        }
        if (ImgBurn() is not { } exe)
        {
            Imapi.Burn(iso, drive, log, progress, verify, cancel, speed);
            if (eject) Imapi.Eject(drive);
            return true;
        }
        log?.Invoke($"burn: ImgBurn {Path.GetFileName(iso)} -> {drive[..2]} ({new FileInfo(iso).Length / 1e9:0.00} GB, {(speed > 0 ? $"{speed}x" : "vitesse max")})");
        var imgLog = ImgBurnLog();
        try { File.Delete(imgLog); } catch (IOException) { } // an older burn's log must not pass for this one's
        try { RunImgBurn(exe, ImgBurnArgs(iso, drive, speed, imgLog, verify, eject), verify, log, progress, cancel); }
        catch (InvalidOperationException e) when (File.Exists(imgLog))
        {
            // ImgBurn prints nothing: the reason is in its log (exit 1 = could not start, 2 = write failed, 3 = verify failed)
            throw new InvalidOperationException($"{e.Message.Split('\n')[0]}\n{string.Join("\n", File.ReadLines(imgLog).TakeLast(15))}\n(journal complet : {imgLog})", e);
        }
        if (File.Exists(imgLog)) log?.Invoke($"journal d'ImgBurn : {imgLog}");
        if (!verify) { log?.Invoke("done: disc written, without verification (as asked)"); return true; }
        if (!File.Exists(imgLog) || !ImgBurnVerified(File.ReadLines(imgLog)))
        { log?.Invoke($"attention : disque écrit mais pas contrôlé, ImgBurn n'a pas relu le disque (tiroir resté ouvert ?) - burnsmith verify IMAGE {drive[..2]} le contrôle"); return false; }
        log?.Invoke("done: disc written and verified");
        return true;
    }

    static void RunImgBurn(string exe, List<string> args, bool verify, Action<string>? log, IProgress<double>? progress, CancellationToken cancel)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("ImgBurn n'a pas pu démarrer.");
        var phase = 0; // 0 = writing, 1 = verifying
        var lastPct = -1;
        while (!proc.WaitForExit(500))
        {
            if (cancel.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new OperationCanceledException("Gravure annulée.", cancel);
            }
            try
            {
                proc.Refresh();
                var title = proc.MainWindowTitle ?? "";
                if (title.Contains("Verify", StringComparison.OrdinalIgnoreCase) && phase == 0)
                {
                    phase = 1;
                    lastPct = -1;
                    log?.Invoke("burn: vérification du disque en cours (ImgBurn)…");
                }
                var m = System.Text.RegularExpressions.Regex.Match(title, @"(\d{1,3})%");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var pct) && pct is >= 0 and <= 100 && pct != lastPct)
                {
                    lastPct = pct;
                    var frac = verify ? (phase == 0 ? pct * 0.005 : 0.5 + pct * 0.005) : pct * 0.01;
                    progress?.Report(Math.Clamp(frac, 0, 1));
                }
            }
            catch { }
        }
        if (proc.ExitCode != 0) throw new InvalidOperationException($"ImgBurn.exe failed (exit {proc.ExitCode})");
        progress?.Report(1);
    }
}
