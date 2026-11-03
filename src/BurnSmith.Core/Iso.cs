using System.Diagnostics;

namespace BurnSmith.Core;

/// <summary>ISO images and burning, through ImgBurn's command line (UDF 2.50 for Blu-ray, UDF 1.02 for DVD-Video).</summary>
public static class Iso
{
    static string Exe()
    {
        try { return Tools.Find("ImgBurn.exe"); }
        catch (FileNotFoundException)
        {
            foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var p = Path.Combine(Environment.GetFolderPath(pf), "ImgBurn", "ImgBurn.exe");
                if (File.Exists(p)) return p;
            }
            throw;
        }
    }

    static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    /// <summary>
    /// ImgBurn answers every problem with a modal dialog and then waits for a click forever, so callers validate first and
    /// this adds a time limit that kills it. <paramref name="visible"/>: false starts it minimized (ISO build), true leaves
    /// the window up (burning: the user may have to insert a disc).
    /// </summary>
    static void Run(IEnumerable<string> args, TimeSpan limit, bool visible)
    {
        var psi = new ProcessStartInfo(Exe())
        {
            UseShellExecute = true,
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Minimized,
            Arguments = string.Join(' ', args.Concat(["/NOIMAGEDETAILS", "/NOSAVELOG", "/PORTABLE", "/START", "/CLOSE"]).Select(Quote)),
        };
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(limit))
        {
            p.Kill(true);
            throw new TimeoutException("ImgBurn did not finish in time (a window may have been waiting for an answer).");
        }
        if (p.ExitCode != 0) throw new InvalidOperationException($"ImgBurn failed (exit {p.ExitCode}).");
    }

    static string Folder(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static double SizeGB(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1e9;

    public static void Build(string discFolder, string isoFile, string label, bool dvd, Action<string>? log = null)
    {
        log ??= _ => { };
        var folder = Folder(discFolder);
        if (!Directory.Exists(Path.Combine(folder, dvd ? "VIDEO_TS" : "BDMV")))
            throw new DirectoryNotFoundException($"'{folder}' does not contain a {(dvd ? "VIDEO_TS" : "BDMV")} folder.");
        label = string.Concat(label.Where(c => !char.IsControl(c))).Trim();
        if (label.Length == 0) label = "BURNSMITH"; // without one ImgBurn asks in a dialog
        if (label.Length > 32) label = label[..32];

        log("iso: ImgBurn");
        var iso = Path.GetFullPath(isoFile);
        Run(["/MODE", "BUILD", "/BUILDMODE", "IMAGEFILE", "/SRC", folder, "/DEST", iso, "/FILESYSTEM", "UDF", "/UDFREVISION", dvd ? "1.02" : "2.50",
             "/VOLUMELABEL", label, "/ROOTFOLDER", "YES", "/OVERWRITE", "YES"], TimeSpan.FromMinutes(10 + SizeGB(folder) * 2), visible: false);
        if (!File.Exists(iso) || new FileInfo(iso).Length == 0) throw new IOException("ImgBurn did not create the image.");
        log("done: " + iso);
    }

    /// <summary>Burns an image to the disc in <paramref name="drive"/> ("F:") and verifies it. shortcut: untested on real media here; speed is the drive's maximum.</summary>
    public static void Burn(string isoFile, string drive, Action<string>? log = null)
    {
        log ??= _ => { };
        if (!File.Exists(isoFile)) throw new FileNotFoundException("image not found", isoFile);
        if (drive.Length != 2 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':') throw new ArgumentException("drive must look like F:", nameof(drive));
        if (!DriveInfo.GetDrives().Any(d => d.DriveType == DriveType.CDRom && d.Name.StartsWith(drive, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{drive} is not an optical drive");

        log($"burn: ImgBurn -> {drive}");
        var gb = new FileInfo(isoFile).Length / 1e9;
        Run(["/MODE", "WRITE", "/SRC", Path.GetFullPath(isoFile), "/DEST", drive.ToUpperInvariant(), "/SPEED", "MAX", "/VERIFY", "YES"],
            TimeSpan.FromMinutes(30 + gb * 4), visible: true);
        log("done: disc written");
    }
}
