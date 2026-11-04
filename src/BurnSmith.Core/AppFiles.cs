namespace BurnSmith.Core;

/// <summary>
/// Where the exe unpacks what it carries (ffmpeg and tsMuxeR, the open-source menu library, the DVDFab / Leawo template pictures):
/// a <c>BurnSmith-data</c> folder next to the exe, so a portable copy keeps everything in one place, or
/// <c>%LOCALAPPDATA%\BurnSmith\files</c> when that folder cannot be written (Program Files). One sub-folder per payload,
/// redone when the exe changes.
/// </summary>
public static class AppFiles
{
    static readonly object Gate = new();
    static string? root;

    /// <summary>Identifies this build of the exe, so an unpacked folder from another build is replaced.</summary>
    static string Build()
    {
        if (Environment.ProcessPath is not { } p || !File.Exists(p)) return "dev";
        var f = new FileInfo(p);
        return $"{f.Length:x}-{f.LastWriteTimeUtc.Ticks:x}";
    }

    public static string Root()
    {
        lock (Gate)
        {
            if (root != null) return root;
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var beside = Path.Combine(exeDir, "BurnSmith-data");
            try
            {
                Directory.CreateDirectory(beside);
                var probe = Path.Combine(beside, $".write{Environment.ProcessId}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return root = beside;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BurnSmith", "files");
            }
        }
    }

    /// <summary>
    /// Makes <c>Root()\name</c> hold what <paramref name="extract"/> writes, unless it already does for this build, and returns it.
    /// The folder is built beside and renamed into place, so an interrupted run never leaves a half-filled folder that looks ready.
    /// </summary>
    public static string Unpack(string name, Action<string> extract)
    {
        lock (Gate)
        {
            var dir = Path.Combine(Root(), name);
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
                if (Directory.Exists(dir)) Directory.Delete(dir, true); // an earlier build
                Directory.Move(tmp, dir);
            }
            catch (IOException) when (File.Exists(ready) && File.ReadAllText(ready) == build) { Tools.TryDelete(tmp); } // another instance unpacked it first
            return dir;
        }
    }
}
