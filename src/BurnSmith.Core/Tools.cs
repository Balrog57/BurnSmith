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

    public static string Find(string exe)
    {
        if (cache.TryGetValue(exe, out var hit)) return hit;
        foreach (var root in SearchRoots())
        {
            var found = Directory.EnumerateFiles(root, exe, SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return cache[exe] = found;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return cache[exe] = p;
        }
        throw new FileNotFoundException($"{exe} not found: put it under the tools folder, set BURNSMITH_TOOLS or add it to PATH");
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
