using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BurnSmith.Core;

/// <summary>Finds and runs the external authoring tools (ffmpeg, tsMuxeR, ...). No tool path is hardwired.</summary>
public static class Tools
{
    static readonly ConcurrentDictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>As <see cref="SearchOption.AllDirectories"/> does, nothing skipped, except that a locked sub-folder does not end the search.</summary>
    static readonly EnumerationOptions Everywhere = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>
    /// Folders searched recursively, in order: BURNSMITH_TOOLS, then a "tools" folder next to the app and, in a development build only, in any parent:
    /// an installed app must not run what a normal user can drop in C:\tools. Lazy, so a tool found early does not wait for the unpack behind <see cref="BundledDir"/>.
    /// </summary>
    public static IEnumerable<string> SearchRoots()
    {
        if (Environment.GetEnvironmentVariable("BURNSMITH_TOOLS") is { Length: > 0 } env && Directory.Exists(env)) yield return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = BundledDir() == null ? d.Parent : null)
            if (Directory.Exists(Path.Combine(d.FullName, "tools"))) yield return Path.Combine(d.FullName, "tools");
    }

    /// <summary>Opens the zip of tools the exe carries (ffmpeg, ffprobe, tsMuxeR); set by the app, null in a development build.</summary>
    public static Func<Stream?>? Payload { get; set; }

    /// <summary>Opens the zip of DVDFab / Leawo template pictures the exe carries (made by publish.ps1 from this PC's installs); null when none.</summary>
    public static Func<Stream?>? TemplatePayload { get; set; }

    static string? bundledDir, templatesDir;
    static readonly object bundledLock = new();

    /// <summary>The zip's contents unpacked under <see cref="AppFiles"/> (a few seconds when it carries something new); null when this exe carries none.</summary>
    static string? UnpackZip(string name, Func<Stream?>? open, bool inLibrary = false)
    {
        if (open == null) return null;
        using var stream = open();
        if (stream == null) return null;
        return AppFiles.Unpack(name, AppFiles.Stamp(stream), dir => System.IO.Compression.ZipFile.ExtractToDirectory(stream, dir), inLibrary);
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
            var found = Directory.EnumerateFiles(root, exe, Everywhere).FirstOrDefault();
            if (found != null) return cache[exe] = found;
        }
        if (BundledDir() is { } bundled && Directory.EnumerateFiles(bundled, exe, Everywhere).FirstOrDefault() is { } inside)
            return cache[exe] = inside;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) // an empty entry would mean the current folder
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return cache[exe] = p;
        }
        throw new FileNotFoundException($"{exe} introuvable : indiquez son dossier dans la variable d'environnement BURNSMITH_TOOLS, ajoutez-le au PATH ou placez-le dans un dossier tools à côté de BurnSmith (BurnSmith fournit ffmpeg, ffprobe et tsMuxeR, mais ni dvdauthor ni spumux).");
    }

    /// <summary>
    /// Empties <paramref name="dir"/> before a build, but only when it is missing, empty, or holds nothing but an earlier disc
    /// (BDMV, CERTIFICATE, VIDEO_TS, AUDIO_TS). Anything else is refused so a wrongly picked folder is never wiped.
    /// </summary>
    public static void PrepareOutput(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var kind = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).ToList();
        if (kind.Count == 0) return;
        if (!kind.All(n => n is "BDMV" or "VIDEO_TS" or "CERTIFICATE" or "AUDIO_TS"))
            throw new IOException($"'{dir}' is not empty and is not just a disc folder: choose a new or empty folder.");
        Directory.Delete(dir, true);
    }

    /// <summary>A work folder of ours, created empty: what a crashed build left there must never be reused, so unlike <see cref="TryDelete"/> this one fails on a locked file.</summary>
    public static void FreshFolder(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
    }

    /// <summary>Removes a temporary folder of ours; a locked file must not fail a build that already succeeded.</summary>
    public static void TryDelete(string dir)
    {
        if (Environment.GetEnvironmentVariable("BURNSMITH_KEEP_WORK") == "1") return; // debugging aid
        try { Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The size of a source file, 0 when it is gone (a USB drive unplugged): what the window shows must survive that.</summary>
    public static long SizeOf(string file) => new FileInfo(file) is { Exists: true } f ? f.Length : 0;

    static readonly AsyncLocal<CancellationToken> ambient = new();
    static readonly HashSet<Process> running = [];
    static bool killedAll;

    /// <summary>
    /// Every <see cref="Run"/> made by <paramref name="work"/>, on whatever thread, ends with an <see cref="OperationCanceledException"/> when <paramref name="cancel"/> fires, its process tree killed.
    /// Ambient, not a parameter: a build reaches the tools through some thirty calls, and one that forgot the token could not be stopped.
    /// </summary>
    public static void WithCancel(CancellationToken cancel, Action work)
    {
        var before = ambient.Value;
        ambient.Value = cancel;
        try { work(); }
        finally { ambient.Value = before; }
    }

    static void Kill(Process p)
    {
        try { p.Kill(true); }
        catch (Exception) { } // it just ended, or Windows refuses: this runs inside Cancel() and while exiting, where nothing can be done about it
    }

    /// <summary>For an app that is closing: ends every tool still running and refuses to start another, so none outlives the app.</summary>
    public static void KillAll()
    {
        lock (running)
        {
            killedAll = true;
            foreach (var p in running)
            {
                Kill(p);
                try { p.WaitForExit(2000); } // its files can then be deleted
                catch (Exception) { }
            }
        }
    }

    /// <summary>Runs a tool and returns its stdout; throws with the output tail when the exit code is not 0, <see cref="OperationCanceledException"/> when cancelled (see <see cref="WithCancel"/>).</summary>
    public static string Run(string exe, IEnumerable<string> args, Action<string>? onLine = null, string? workDir = null,
                             IReadOnlyDictionary<string, string>? env = null)
    {
        var cancel = ambient.Value;
        cancel.ThrowIfCancellationRequested();
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
        lock (running)
        {
            if (killedAll) throw new OperationCanceledException();
            p.Start();
            running.Add(p);
        }
        try
        {
            using var stop = cancel.Register(() => Kill(p));
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
        }
        finally { lock (running) running.Remove(p); }
        cancel.ThrowIfCancellationRequested(); // before the exit code: a killed tool did not fail
        if (p.ExitCode != 0)
        {
            var tail = string.Join('\n', all.ToString().Split('\n').TakeLast(15));
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed (exit {p.ExitCode}):\n{tail}");
        }
        return stdout.ToString();
    }
}
