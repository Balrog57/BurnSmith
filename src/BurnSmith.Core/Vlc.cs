using System.Diagnostics;
using Microsoft.Win32;

namespace BurnSmith.Core;

/// <summary>
/// « Lire sur l'ordinateur » : a finished disc folder or ISO opened in the VLC installed on this PC. Never the portable copy under tools\vlc:
/// it is a development test bench, not shipped.
/// </summary>
public static class Vlc
{
    public const string Site = "https://www.videolan.org/vlc/";

    /// <summary>vlc.exe from VLC's installer key (64 then 32 bits), else the usual Program Files folders; null when VLC is not installed.</summary>
    public static string? Find()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
                if (key?.GetValue(null) is string exe && File.Exists(exe)) return exe;
                if (key?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "vlc.exe"))) return Path.Combine(dir, "vlc.exe");
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "VideoLAN", "VLC", "vlc.exe")).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// VLC's command line for a disc: an ISO as it is, a folder as bluray:/// (it holds BDMV) or dvd:/// (VIDEO_TS), the BDMV or VIDEO_TS folder itself too.
    /// Quoted: a folder with a space (« JUDGE DREDD ») is otherwise two arguments. In the MRL % and # are escaped, VLC reads # as a title:chapter.
    /// </summary>
    public static string Arguments(string disc)
    {
        disc = Path.TrimEndingDirectorySeparator(Path.GetFullPath(disc));
        if (File.Exists(disc)) return $"\"{disc}\"";
        if (Path.GetFileName(disc).ToUpperInvariant() is "BDMV" or "VIDEO_TS") disc = Path.GetDirectoryName(disc)!;
        var scheme = Directory.Exists(Path.Combine(disc, "BDMV")) ? "bluray" : Directory.Exists(Path.Combine(disc, "VIDEO_TS")) ? "dvd"
            : throw new InvalidOperationException($"Ni BDMV ni VIDEO_TS dans {disc}, ni une image ISO.");
        return $"\"{scheme}:///{disc.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23")}\"";
    }

    /// <summary>Starts VLC on the disc and leaves it to the user; false when VLC is not installed. A path that is no disc throws, VLC or not.</summary>
    public static bool Play(string disc)
    {
        var args = Arguments(disc);
        if (Find() is not { } exe) return false;
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();
        return true;
    }

    /// <summary>What the user is told with a Blu-ray: VLC's libbluray runs a BD-J menu only with Java, else it plays the disc without its menu.</summary>
    public const string JavaNote = "Dans VLC, le menu d'un Blu-ray BD-J (le type de menu par défaut) ne s'affiche que si Java est installé sur l'ordinateur (en 64 bits pour un VLC 64 bits) ; sans Java, VLC lit le disque sans menu. Le menu HDMV n'a pas besoin de Java.";
}
