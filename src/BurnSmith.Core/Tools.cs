using System.Diagnostics;
using System.Text;

namespace BurnSmith.Core;

/// <summary>Finds and runs the external authoring tools (ffmpeg, tsMuxeR, ...). No tool path is hardwired.</summary>
public static class Tools
{
    static readonly Dictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders searched recursively, in order: BURNSMITH_TOOLS, then a "tools" folder next to the app or any parent.</summary>
    public static IEnumerable<string> SearchRoots()
    {
        if (Environment.GetEnvironmentVariable("BURNSMITH_TOOLS") is { Length: > 0 } env) yield return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "tools"))) yield return Path.Combine(d.FullName, "tools");
    }

    /// <summary>Opens the zip of tools the exe carries (ffmpeg, ffprobe, tsMuxeR); set by the app, null in a development build.</summary>
    public static Func<Stream?>? Payload { get; set; }

    /// <summary>Opens the zip of DVDFab / Leawo template pictures the exe carries (made by publish.ps1 from this PC's installs); null when none.</summary>
    public static Func<Stream?>? TemplatePayload { get; set; }

    static string? bundledDir, templatesDir;
    static readonly object bundledLock = new();

    /// <summary>The zip's contents unpacked under <see cref="AppFiles"/> (a few seconds on the first run of a build); null when this exe carries none.</summary>
    static string? UnpackZip(string name, Func<Stream?>? open, bool inLibrary = false)
    {
        if (open == null) return null;
        using var stream = open();
        if (stream == null) return null;
        return AppFiles.Unpack(name, dir => System.IO.Compression.ZipFile.ExtractToDirectory(stream, dir), inLibrary);
    }

    /// <summary>ffmpeg, ffprobe and tsMuxeR carried by the exe, unpacked to <c>BurnSmith-data\tools</c>; null when this exe carries none.</summary>
    public static string? BundledDir() { lock (bundledLock) return bundledDir ??= UnpackZip("tools", Payload); }

    /// <summary>The DVDFab / Leawo / Yuhan template folders carried by the exe, unpacked to <c>Documents\BurnSmith\Templates\carried</c>; null when this exe carries none.</summary>
    public static string? TemplatesDir() { lock (bundledLock) return templatesDir ??= UnpackZip("carried", TemplatePayload, true); }

    public static string Find(string exe)
    {
        if (cache.TryGetValue(exe, out var hit)) return hit;
        foreach (var root in SearchRoots())
        {
            var found = Directory.EnumerateFiles(root, exe, SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return cache[exe] = found;
        }
        if (BundledDir() is { } bundled && Directory.EnumerateFiles(bundled, exe, SearchOption.AllDirectories).FirstOrDefault() is { } inside)
            return cache[exe] = inside;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return cache[exe] = p;
        }
        throw new FileNotFoundException($"{exe} not found: put it under the tools folder, set BURNSMITH_TOOLS or add it to PATH (the released exe carries ffmpeg and tsMuxeR itself)");
    }

    /// <summary>
    /// Empties <paramref name="dir"/> before a build, but only when it is missing, empty, or an earlier disc folder
    /// (holds BDMV or VIDEO_TS). Anything else is refused so a wrongly picked folder is never wiped.
    /// </summary>
    public static void PrepareOutput(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var kind = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).ToList();
        if (kind.Count == 0) return;
        if (!kind.Any(n => n is "BDMV" or "VIDEO_TS" or "CERTIFICATE" or "AUDIO_TS"))
            throw new IOException($"'{dir}' is not empty and is not a disc folder: choose a new or empty folder.");
        Directory.Delete(dir, true);
    }

    /// <summary>Removes a temporary folder of ours; a locked file must not fail a build that already succeeded.</summary>
    public static void TryDelete(string dir)
    {
        if (Environment.GetEnvironmentVariable("BURNSMITH_KEEP_WORK") == "1") return; // debugging aid
        try { Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Runs a tool and returns its stdout; throws with the output tail when the exit code is not 0.</summary>
    public static string Run(string exe, IEnumerable<string> args, Action<string>? onLine = null, string? workDir = null,
                             IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        foreach (var a in args) psi.ArgumentList.Add(a);

        var all = new StringBuilder();
        var stdout = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        void Line(string? l, bool isOut)
        {
            if (l == null) return;
            lock (all) { all.AppendLine(l); if (isOut) stdout.AppendLine(l); }
            onLine?.Invoke(l);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data, true);
        p.ErrorDataReceived += (_, e) => Line(e.Data, false);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            var tail = string.Join('\n', all.ToString().Split('\n').TakeLast(15));
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {p.ExitCode}):\n{tail}");
        }
        return stdout.ToString();
    }
}
