using System.Diagnostics;
using BurnSmith.Core;

/// <summary>
/// burnsmith check-reliability : Cancel stops a real encode at once, through the real builds, as a cancelled build (no tool failure, no process left, no work folder);
/// half an image is not kept; a source that went away does not stop the sums; the error log stays small and never throws. What only a window can do is not here.
/// </summary>
static class ReliabilityCheck
{
    static int failed;
    static string root = "";

    static void Check(string what, bool ok, string shown = "")
    {
        if (!ok) failed++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}{(shown.Length > 0 ? " : " + shown : "")}");
    }

    static void Part(string what, Action work)
    {
        try { work(); }
        catch (Exception e) { Check(what, false, $"{e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
    }

    static string F(string name) => Path.Combine(root, name);

    /// <summary>The ffmpeg processes working on this run's files, by their command line: other programs on this PC may run ffmpeg too.</summary>
    static int Alive(bool kill = false)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        var find = $"$p = @(Get-CimInstance Win32_Process -Filter \"name='ffmpeg.exe'\" | Where-Object {{ $_.CommandLine -like '*{Path.GetFileName(root)}*' }}); ";
        foreach (var x in new[] { "-NoProfile", "-Command", find + (kill ? "$p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; " : "") + "$p.Count" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return int.Parse(text.Trim());
    }

    static void WaitForEncoder()
    {
        var sw = Stopwatch.StartNew();
        while (Alive() == 0 && sw.Elapsed.TotalSeconds < 60) { }
    }

    /// <summary>A real build of a video far too long to end by itself, cancelled while ffmpeg encodes it.</summary>
    static void CancelBuild(string name, Action<string, Action<string>, CancellationToken> build)
    {
        var output = F(name);
        var lines = new List<string>();
        using var cancel = new CancellationTokenSource();
        var running = Task.Run(() => build(output, l => { lock (lines) lines.Add(l); }, cancel.Token));
        WaitForEncoder();
        Thread.Sleep(1000);
        var before = Alive();
        var workBefore = Directory.Exists(output + "_work");
        Check($"{name}: the encode is really running, with its work folder", before > 0 && workBefore, $"{before} ffmpeg");
        var sw = Stopwatch.StartNew();
        cancel.Cancel();
        var ended = running.Wait(0) || Task.WaitAny([running], 30_000) == 0;
        sw.Stop();
        Exception? error = running.Exception?.GetBaseException();
        Check($"{name}: the build stops within 5 s", ended && sw.Elapsed.TotalSeconds < 5, $"{sw.Elapsed.TotalSeconds:0.00} s");
        Check($"{name}: it ends as cancelled, not as a tool failure", error is OperationCanceledException, error == null ? (ended ? "no exception" : "still running") : $"{error.GetType().Name}: {error.Message.Split('\n')[0]}");
        Check($"{name}: no ffmpeg left", Alive() == 0, $"{Alive()} left");
        Check($"{name}: the work folder is gone", !Directory.Exists(output + "_work"));
        lock (lines) Check($"{name}: it does not say it is done", !lines.Any(l => l.StartsWith("done")));
        if (!ended) { Alive(kill: true); Task.WaitAny([running], 30_000); } // the defect is back: the next parts must not see this encode
    }

    public static int Run(bool noDvd)
    {
        root = Path.Combine(Path.GetTempPath(), "bs_rel_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var ffmpeg = Tools.Find("ffmpeg.exe");
            var clip = F("long.mkv");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "color=s=64x64:r=24:d=3000", "-f", "lavfi", "-i", "sine=f=440:d=3000", "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "ac3", clip]);
            var encode = new EncodeOptions(true, Preset: "veryslow");

            Part("Blu-ray", () => CancelBuild("bd", (o, l, c) => BluRay.Build(new BluRayJob([new DiscTitle(clip)], o, null, false, "CHECK", 5, encode, 23.3), l, c)));
            if (noDvd) Console.WriteLine("skip Cancel on a real DVD encode (--no-dvd)");
            else Part("DVD", () => CancelBuild("dvd", (o, l, c) => Dvd.Build(new DvdJob([new DiscTitle(clip)], o), l, c)));

            Part("already cancelled", () =>
            {
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                var what = "";
                try { BluRay.Build(new BluRayJob([new DiscTitle(clip)], F("late"), null, false, "CHECK", 5, encode, 23.3), null, cancel.Token); }
                catch (OperationCanceledException) { what = "cancelled"; }
                Check("cancelled before it starts: nothing is run or made", what == "cancelled" && !Directory.Exists(F("late") + "_work") && !Directory.Exists(F("late")));
            });

            Part("app ended by force", () =>
            {
                // the app alone is killed, as a crash or the Task Manager ends it: KillAll never runs. The control leaves its tool out of the job
                foreach (var free in new[] { true, false })
                {
                    var args = new List<string> { "hold-tool", F("hold.txt") };
                    if (free) args.Add("--free");
                    using var app = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false, CreateNoWindow = true })!;
                    var seen = SpinWait.SpinUntil(() => Alive() == 1, 20000);
                    app.Kill();
                    app.WaitForExit();
                    Thread.Sleep(1000);
                    var left = Alive(kill: true);
                    Check(free ? "control: a tool left out of the app's job outlives an app ended by force" : "a tool ends with an app ended by force (crash, Task Manager)",
                          seen && left == (free ? 1 : 0), $"seen {seen}, still running {left}");
                }
            });

            Part("process tree", () =>
            {
                // ffmpeg under cmd holds the pipes after cmd goes, and floods them: a kill of cmd alone, or a read that blocks, would hang here
                using var cancel = new CancellationTokenSource();
                var count = 0L;
                var run = Task.Run(() => Tools.WithCancel(cancel.Token, () => Tools.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    ["/c", ffmpeg, "-y", "-v", "debug", "-i", clip, "-c:v", "libx264", "-preset", "veryslow", "-f", "null", "-"], _ => Interlocked.Increment(ref count))));
                WaitForEncoder();
                while (Interlocked.Read(ref count) < 100 && !run.IsCompleted) Thread.Sleep(50);
                var sw = Stopwatch.StartNew();
                cancel.Cancel();
                var ended = Task.WaitAny([run], 20_000) == 0;
                Check("a tool under another tool, flooding its output: killed with its parent, no hang", ended && run.Exception?.GetBaseException() is OperationCanceledException && Alive() == 0, $"{sw.Elapsed.TotalSeconds:0.00} s, {Interlocked.Read(ref count)} lines read, {Alive()} left");
            });

            Part("a real failure", () =>
            {
                var what = "";
                try { Tools.Run(ffmpeg, ["-v", "error", "-i", F("no-such-file.mkv"), "-f", "null", "-"]); }
                catch (InvalidOperationException e) { what = e.Message; }
                Check("a tool that fails, with nothing cancelled, still reports the failure", what.Contains("failed (exit"), what.Split('\n')[0]);
                using var unused = new CancellationTokenSource();
                Tools.WithCancel(unused.Token, () => Check("a token not fired changes nothing", Tools.Run(ffmpeg, ["-version"]).Contains("ffmpeg version")));
            });

            Part("image", () =>
            {
                var disc = F("imgdisc");
                Directory.CreateDirectory(Path.Combine(disc, "BDMV"));
                File.WriteAllBytes(Path.Combine(disc, "BDMV", "big.bin"), new byte[16 << 20]);
                using var cancel = new CancellationTokenSource();
                var iso = F("img.iso");
                var what = "";
                try { Iso.Build(disc, iso, "CHECK", false, null, new Now(_ => cancel.Cancel()), cancel.Token); }
                catch (OperationCanceledException) { what = "cancelled"; }
                Check("an image cancelled half way stops, and is not kept", what == "cancelled" && !File.Exists(iso) && !File.Exists(iso + ".part"), what);
                Iso.Build(disc, iso, "CHECK", false);
                Check("uncancelled, the same image is made", new FileInfo(iso).Length > 16 << 20 && !File.Exists(iso + ".part"));
                var before = File.GetLastWriteTimeUtc(iso);
                try { Iso.Build(disc, iso, "CHECK", false, null, new Now(_ => cancel.Cancel()), cancel.Token); } catch (OperationCanceledException) { }
                Check("a cancelled new image leaves the older one of that name whole", File.GetLastWriteTimeUtc(iso) == before && new FileInfo(iso).Length > 16 << 20);
            });

            Part("a source that went away", () =>
            {
                var gone = F("gone.mkv");
                var info = new SourceInfo("h264", 1920, 1080, 24, "ac3", 2, 600, [], []);
                Check("the size of a missing file is 0", Tools.SizeOf(gone) == 0);
                Check("the disc's estimate survives it", BluRay.EstimateGB(new BluRayJob([new DiscTitle(gone)], "", null, false, "X", 5, new EncodeOptions(FitDisc: true), 23.3), [info]) >= 0);
            });

            Part("error log", () =>
            {
                var log = F("errors.log");
                AppFiles.LogError("essai", new IOException("une erreur"), log);
                Check("an error is written with its details", File.ReadAllText(log).Contains("essai") && File.ReadAllText(log).Contains("une erreur"));
                var big = new InvalidDataException(new string('x', 200_000));
                for (var i = 0; i < 30; i++) AppFiles.LogError("essai", big, log);
                var total = new FileInfo(log).Length + new FileInfo(log + ".old").Length;
                Check("past 1 MB it starts again, keeping one older file: at most about 2 MB after 6 MB of errors", total < 2_500_000 && File.Exists(log + ".old"), $"{total / 1000} kB");
                AppFiles.LogError("essai", big, root); // a folder: the write cannot succeed
                AppFiles.LogError("essai", big, "Z:\\no\\such\\<folder>\\x.log");
                Check("a write that fails does not throw", true);
                Check("the log is next to the app's other files", Path.GetDirectoryName(AppFiles.ErrorLog()) == AppFiles.Root(), AppFiles.ErrorLog());
            });

            // last: it is for good, in this process
            Part("closing", () =>
            {
                var run = Task.Run(() => Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", clip, "-c:v", "libx264", "-preset", "veryslow", "-f", "null", "-"]));
                WaitForEncoder();
                var before = Alive();
                Tools.KillAll();
                Check("closing the app ends the tool still running", before > 0 && Alive() == 0, $"{before} then {Alive()}");
                var what = "";
                try { Tools.Run(ffmpeg, ["-version"]); }
                catch (OperationCanceledException) { what = "refused"; }
                Check("and no other can start while it closes", what == "refused");
                Task.WaitAny([run], 5000);
            });
        }
        catch (FileNotFoundException e) { Check("the tools this check needs", false, e.Message); }
        finally { Alive(kill: true); Tools.TryDelete(root); }
        Console.WriteLine(failed == 0 ? "check-reliability: all passed" : $"check-reliability: {failed} FAILED");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Reports on the worker's own thread (<see cref="Progress{T}"/> would post it to another).</summary>
    sealed class Now(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
