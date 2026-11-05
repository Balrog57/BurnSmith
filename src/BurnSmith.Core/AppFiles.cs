namespace BurnSmith.Core;

/// <summary>
/// Where the exe puts what it carries and what the user keeps.
/// <list type="bullet">
/// <item>Programs (ffmpeg and tsMuxeR): a <c>BurnSmith-data</c> folder next to the exe, so a portable copy keeps everything in one place, or
/// <c>%LOCALAPPDATA%\BurnSmith\files</c> when that folder cannot be written (Program Files, once installed).</item>
/// <item>The template library (the open-source templates, the DVDFab / Leawo / Yuhan pictures the exe carries, and the user's own templates and
/// images): <c>Documents\BurnSmith\Templates</c>, a folder the user can see and back up.</item>
/// </list>
/// One sub-folder per payload, redone when the exe changes.
/// </summary>
public static class AppFiles
{
    static readonly object Gate = new();
    static string? root, library;

    /// <summary>Identifies this build of the exe, so an unpacked folder from another build is replaced.</summary>
    static string Build()
    {
        if (Environment.ProcessPath is not { } p || !File.Exists(p)) return "dev";
        var f = new FileInfo(p);
        return $"{f.Length:x}-{f.LastWriteTimeUtc.Ticks:x}";
    }

    /// <summary><paramref name="dir"/> when files can be written there (created if needed), else null.</summary>
    static string? Writable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return dir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string Root()
    {
        lock (Gate)
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            return root ??= Writable(Path.Combine(exeDir, "BurnSmith-data"))
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BurnSmith", "files");
        }
    }

    /// <summary>Errors nobody caught: <c>burnsmith.log</c> under <see cref="Root"/>. The field first: <see cref="Root"/> waits for an unpack in progress, and a crash must not.</summary>
    public static string ErrorLog() => Path.Combine(root ?? Root(), "burnsmith.log");

    static readonly object LogGate = new();

    /// <summary>Adds an error to the log (<paramref name="file"/>, else <see cref="ErrorLog"/>), which starts again past 1 MB and keeps one older file. Never throws.</summary>
    public static void LogError(string where, Exception error, string? file = null)
    {
        try
        {
            lock (LogGate)
            {
                file ??= ErrorLog();
                if (File.Exists(file) && new FileInfo(file).Length > 1 << 20) File.Move(file, file + ".old", true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception) { } // called while something already went wrong: losing a log line is the lesser harm
    }

    /// <summary><c>Documents\BurnSmith\Templates</c> (the real Documents folder, wherever Windows or OneDrive put it); under <see cref="Root"/> if that cannot be written.</summary>
    public static string Library()
    {
        lock (Gate)
            return library ??= Writable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BurnSmith", "Templates"))
                ?? Path.Combine(Root(), "Templates");
    }

    /// <summary>
    /// A folder of the user's own files in the library. What an earlier version kept in <paramref name="oldDir"/> (under %APPDATA%) is copied
    /// there once, and the old files stay where they were.
    /// </summary>
    public static string UserFolder(string name, string oldDir)
    {
        var dir = Path.Combine(Library(), name);
        if (Directory.Exists(dir) || !Directory.Exists(oldDir)) return dir;
        try
        {
            foreach (var file in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(dir, Path.GetRelativePath(oldDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the old folder still has them; the next run tries again if nothing was made
        return dir;
    }

    /// <summary>
    /// Makes <c>name</c> (under <see cref="Library"/> when <paramref name="inLibrary"/>, else under <see cref="Root"/>) hold what
    /// <paramref name="extract"/> writes, unless it already does for this build, and returns it.
    /// The folder is built beside and renamed into place, so an interrupted run never leaves a half-filled folder that looks ready.
    /// </summary>
    public static string Unpack(string name, Action<string> extract, bool inLibrary = false)
    {
        lock (Gate)
        {
            var dir = Path.Combine(inLibrary ? Library() : Root(), name);
            var ready = Path.Combine(dir, ".ready");
            var build = Build();
            if (File.Exists(ready) && File.ReadAllText(ready) == build) return dir;

            var tmp = $"{dir}.{Environment.ProcessId}.tmp";
            Tools.TryDelete(tmp);
            Directory.CreateDirectory(tmp);
            extract(tmp);
            File.WriteAllText(Path.Combine(tmp, ".ready"), build);
            try
            {
                if (Directory.Exists(dir))
                {
                    // an earlier build's folder goes aside in one step (it fails as a whole, leaving the folder intact, when another build holds a file in it), then is deleted
                    var old = $"{dir}.{Environment.ProcessId}.old";
                    Directory.Move(dir, old);
                    Tools.TryDelete(old);
                }
                Directory.Move(tmp, dir);
            }
            catch (IOException) when (File.Exists(ready) && File.ReadAllText(ready) == build) { Tools.TryDelete(tmp); } // another instance unpacked it first
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && Directory.Exists(dir))
            {
                // another build (still running) holds a file of the folder: keep what is there rather than fail the start; the next start of this build tries again
                Tools.TryDelete(tmp);
            }
            return dir;
        }
    }
}
