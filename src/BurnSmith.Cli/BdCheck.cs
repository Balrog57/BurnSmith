using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BurnSmith.Core;
using BurnSmith.Core.Hdmv;
using BurnSmith.Core.Menu;

/// <summary>
/// burnsmith check-bd : what a player needs of a disc, which check-disc (structure only) cannot see. Short clips made with ffmpeg go through the real builds, and ffprobe
/// reads what came out: 48 kHz sound, 8-bit video at level 4.1 at most, picture and sound that end together; plus the DVD refusals, the work folder and damaged PGS pictures.
/// Every "bad" clip is otherwise fit to be copied (H.264 at the disc's size and rate, starting at 0), so that it is its own defect that must get it re-encoded.
/// </summary>
static class BdCheck
{
    static int failed;
    static string root = "", ffmpeg = "", ffprobe = "";
    static bool noDvd;

    static void Check(string what, bool ok, string shown = "")
    {
        if (!ok) failed++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}{(shown.Length > 0 ? " : " + shown : "")}");
    }

    /// <summary>One part of the check: a build that throws fails that part, not the whole run.</summary>
    static void Part(string what, Action work)
    {
        try { work(); }
        catch (Exception e) { Check(what, false, $"{e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
    }

    /// <summary>A part that needs dvdauthor or spumux, not redistributable, so absent from CI: only --no-dvd passes over it, and says so; otherwise a missing tool fails it.</summary>
    static void DvdPart(string what, Action work)
    {
        if (noDvd) Console.WriteLine($"skip {what} (--no-dvd)");
        else Part(what, work);
    }

    static string F(string name) => Path.Combine(root, name);

    /// <summary>A clip from ffmpeg's own sources; <paramref name="video"/> and <paramref name="sound"/> are encoder options. H.264 High without B frames unless told otherwise: a re-encode shows, it has some.</summary>
    static string Clip(string name, string source, int sampleRate, string[] video, string[] sound)
    {
        var seconds = Regex.Match(source, @"d=([\d.]+)").Groups[1].Value;
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", source, "-f", "lavfi", "-i", $"sine=f=440:r={sampleRate}:d={seconds}",
                           "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "veryfast", "-bf", "0", .. video, .. sound, F(name)]);
        return F(name);
    }

    static readonly string[] Ac3At48 = ["-c:a", "ac3", "-b:a", "192k"]; // not the 448k a re-encode gives
    static readonly string[] Aac = ["-c:a", "aac"];
    static readonly string[] Fine = ["-pix_fmt", "yuv420p", "-level", "4.1"];
    const string Size720 = "testsrc2=s=1280x720:r=24000/1001:d=2";

    /// <summary>What ffprobe sees in a stream file of the disc.</summary>
    sealed record Seen(string PixFmt, string Profile, int Level, string Rate, int Reorder, string Audio, int SampleRate, int AudioBits);

    static string Stream(string disc, int number) => Path.Combine(disc, "BDMV", "STREAM", $"{number:00000}.m2ts");

    static Seen Look(string m2ts)
    {
        using var doc = JsonDocument.Parse(Tools.Run(ffprobe, ["-v", "error", "-of", "json", "-show_streams", m2ts]));
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var v = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
        var a = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        static string Text(JsonElement s, string name) => s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var p) ? p.ToString() : "";
        return new Seen(Text(v, "pix_fmt"), Text(v, "profile"), int.TryParse(Text(v, "level"), out var level) ? level : 0, Text(v, "r_frame_rate"), int.TryParse(Text(v, "has_b_frames"), out var reorder) ? reorder : -1,
                        Text(a, "codec_name"), int.TryParse(Text(a, "sample_rate"), out var rate) ? rate : 0, int.TryParse(Text(a, "bit_rate"), out var bits) ? bits : 0);
    }

    /// <summary>When a stream's last packet ends, in seconds: what a player reaches.</summary>
    static double End(string m2ts, string stream)
    {
        var last = 0.0;
        foreach (var line in Tools.Run(ffprobe, ["-v", "error", "-select_streams", stream, "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", m2ts]).Split('\n'))
        {
            var cells = line.Trim().Split(',');
            if (cells.Length > 0 && double.TryParse(cells[0], CultureInfo.InvariantCulture, out var pts))
                last = Math.Max(last, pts + (cells.Length > 1 && double.TryParse(cells[1], CultureInfo.InvariantCulture, out var d) ? d : 0));
        }
        return last;
    }

    static EncodeOptions Small(string preset = "veryfast", bool reencode = false, bool fit = false) => new(reencode, Preset: preset, FitDisc: fit, Hd720: true);

    static string Bd(string name, IReadOnlyList<DiscTitle> titles, EncodeOptions? encode = null, MenuDesign? menu = null, double capacity = 23.3, Action<string>? log = null, bool uhd = false)
    {
        var disc = F(name);
        BluRay.Build(new BluRayJob(titles, disc, menu, uhd, "CHECK", 5, encode ?? Small(), capacity), log);
        Check($"{name}: structure", DiscCheck.Folder(disc).Count == 0, string.Join(" ; ", DiscCheck.Folder(disc)));
        return disc;
    }

    public static int Run(bool skipDvd)
    {
        noDvd = skipDvd;
        root = Path.Combine(Path.GetTempPath(), "bs_bd_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            ffmpeg = Tools.Find("ffmpeg.exe");
            ffprobe = Tools.Find("ffprobe.exe");
            Sound();
            Video();
            Sync();
            FitDisc();
            Damaged();
            WorkFolder();
            DvdRefusals();
        }
        catch (FileNotFoundException e) { Check("the tools this check needs", false, e.Message); }
        finally { Tools.TryDelete(root); }
        Console.WriteLine(failed == 0 ? "check-bd: all passed" : $"check-bd: {failed} FAILED");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Finding 2: a disc carries 48 kHz, whatever the sound was and whichever path made it.</summary>
    static void Sound()
    {
        Part("AAC 44.1 kHz", () =>
        {
            var s = Look(Stream(Bd("aac441", [new DiscTitle(Clip("aac441.mkv", Size720, 44100, Fine, Aac))]), 1));
            Check("AAC 44.1 kHz becomes AC-3 48 kHz", s is { Audio: "ac3", SampleRate: 48000 }, $"{s.Audio} {s.SampleRate}");
        });
        Part("AC-3 44.1 kHz", () =>
        {
            var s = Look(Stream(Bd("ac3441", [new DiscTitle(Clip("ac3441.mkv", Size720, 44100, Fine, ["-c:a", "ac3"]))]), 1));
            Check("AC-3 44.1 kHz is not copied: it becomes 48 kHz", s is { Audio: "ac3", SampleRate: 48000 }, $"{s.Audio} {s.SampleRate}");
        });
        Part("moving menu's own sound", () =>
        {
            var menu = TemplateCatalog.Default().Clone();
            menu.Loop = true;
            menu.LoopSeconds = 5;
            var s = Look(Stream(Bd("loop441", [new DiscTitle(Clip("loop441.mkv", Size720, 44100, Fine, Aac))], menu: menu), BluRay.LoopPlaylist));
            Check("the moving menu's clip, sound taken from a 44.1 kHz film, is at 48 kHz", s.SampleRate == 48000, $"{s.Audio} {s.SampleRate}");
        });
    }

    /// <summary>Finding 3: only what a player takes is copied; what it takes still is. The intro goes the same way as a title.</summary>
    static void Video()
    {
        Part("10-bit", () =>
        {
            var s = Look(Stream(Bd("tenbit", [new DiscTitle(Clip("tenbit.mkv", Size720, 48000, ["-pix_fmt", "yuv420p10le"], Ac3At48))]), 1));
            Check("10-bit H.264 is re-encoded to 8-bit", s.PixFmt == "yuv420p" && !s.Profile.Contains("10"), $"{s.PixFmt} {s.Profile}");
        });
        Part("level 5.1", () =>
        {
            var s = Look(Stream(Bd("level51", [new DiscTitle(Clip("level51.mkv", Size720, 48000, ["-pix_fmt", "yuv420p", "-level", "5.1"], Ac3At48))]), 1));
            Check("level 5.1 H.264 is re-encoded to 4.1 at most", s.Level is > 0 and <= 41, $"level {s.Level}");
        });
        var fine = "";
        Part("compatible clip", () =>
        {
            fine = Clip("fine.mkv", Size720, 48000, Fine, Ac3At48);
            var s = Look(Stream(Bd("fine", [new DiscTitle(fine)]), 1));
            Check("8-bit level 4.1 H.264 with 48 kHz AC-3: both are COPIED (no B frames, 192 kb/s as in the source)",
                  s is { PixFmt: "yuv420p", Level: 41, Reorder: 0, Audio: "ac3", SampleRate: 48000, AudioBits: 192000 }, s.ToString());
            var again = Look(Stream(Bd("forced", [new DiscTitle(fine)], Small(reencode: true)), 1));
            Check("that test does tell a copy: the same clip re-encoded has B frames", again.Reorder > 0, $"has_b_frames {again.Reorder}");
        });
        Part("intro video", () =>
        {
            var intro = Clip("intro10.mkv", Size720, 44100, ["-pix_fmt", "yuv420p10le"], Aac);
            var s = Look(Stream(Bd("intro", [new DiscTitle(fine), new DiscTitle(intro, Intro: true)]), BluRay.IntroPlaylist));
            Check("an intro video gets the same treatment: 8-bit, 48 kHz", s is { PixFmt: "yuv420p", SampleRate: 48000 }, $"{s.PixFmt} {s.SampleRate}");
        });
        Part("4K", () =>
        {
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=3840x2160:r=24:d=1", "-f", "lavfi", "-i", "sine=f=440:d=1", "-map", "0:v", "-map", "1:a",
                               "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-pix_fmt", "yuv420p10le", "-c:a", "ac3", F("uhd.mkv")]);
            var s = Look(Stream(Bd("uhd", [new DiscTitle(F("uhd.mkv"))], new EncodeOptions(), uhd: true), 1));
            Check("a 4K disc still copies its 10-bit HEVC source", s.PixFmt == "yuv420p10le", s.PixFmt);
        });
    }

    /// <summary>Finding 4: the rate the video is made at is the rate tsMuxeR is told. Long enough to show 0.1 %: 240 ms over 240 s, less a constant 80 ms (a rate change adds two frames at the end).</summary>
    static void Sync()
    {
        Part("24.000 fps", () =>
        {
            var clip = Clip("fps24.mkv", "testsrc=s=320x180:r=24:d=240", 48000, ["-pix_fmt", "yuv420p"], Aac);
            var m2ts = Stream(Bd("fps24", [new DiscTitle(clip)], Small("ultrafast")), 1);
            var (video, sound) = (End(m2ts, "v"), End(m2ts, "a"));
            Check("a re-encoded 24.000 fps source: picture and sound end within 50 ms", Math.Abs(video - sound) < 0.05, $"video {video:0.000} s, sound {sound:0.000} s");
            Check("it stays at 24 fps", Look(m2ts).Rate == "24/1", Look(m2ts).Rate);
        });
        Part("50 fps", () =>
        {
            var clip = Clip("fps50.mkv", "testsrc=s=320x180:r=50:d=3", 48000, ["-pix_fmt", "yuv420p"], Aac);
            var m2ts = Stream(Bd("fps50", [new DiscTitle(clip)], Small("ultrafast")), 1);
            var (video, sound) = (End(m2ts, "v"), End(m2ts, "a"));
            Check("a 50 fps source becomes 25 fps, as on a DVD, in step (within the two frames a rate change adds at the end)", Look(m2ts).Rate == "25/1" && Math.Abs(video - sound) < 0.1, $"{Look(m2ts).Rate}, video {video:0.000} s, sound {sound:0.000} s");
        });
    }

    /// <summary>Finding 10: "fit to disc" leaves room for the intro. The film is padded by an attachment: a file too big for the disc, encoded in no time.</summary>
    static void FitDisc()
    {
        Part("fit to disc", () =>
        {
            File.WriteAllBytes(F("padding.bin"), new byte[8_000_000]);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=320x180:r=24:d=2", "-f", "lavfi", "-i", "sine=f=440:d=2", "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "ac3",
                               "-attach", F("padding.bin"), "-metadata:s:t", "mimetype=application/octet-stream", F("film.mkv")]);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=320x180:r=24:d=2", "-f", "lavfi", "-i", "sine=f=440:d=2", "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "ac3", F("fitintro.mkv")]);
            int Kbps(string name, IReadOnlyList<DiscTitle> titles)
            {
                var lines = new List<string>();
                Bd(name, titles, Small("ultrafast", fit: true), capacity: 0.1563, log: l => { lock (lines) lines.Add(l); }); // 6 MB left after what the disc always takes
                return lines.Select(l => Regex.Match(l, @"re-encoded at (\d+) kb/s")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).FirstOrDefault();
            }
            var (alone, with) = (Kbps("fitalone", [new DiscTitle(F("film.mkv"))]), Kbps("fitwith", [new DiscTitle(F("film.mkv")), new DiscTitle(F("fitintro.mkv"), Intro: true)]));
            Check("twice the seconds to share the disc: an intro as long as the film halves the fitted bit rate", alone > 0 && with > alone * 0.4 && with < alone * 0.6, $"{alone} kb/s alone, {with} kb/s with the intro");
        });
    }

    /// <summary>Finding 12: a PGS picture cut short is left out; it costs neither the other pictures nor the build.</summary>
    static void Damaged()
    {
        static string Outcome(byte[] data, int w, int h)
        {
            try { Pgs.Unrle(data, w, h); return "decoded"; }
            catch (InvalidDataException) { return "refused"; }
            catch (Exception e) { return e.GetType().Name; }
        }
        Part("RLE", () =>
        {
            byte[] pixels = [0, 0, 1, 1, 1, 1, 2, 0, .. Enumerable.Repeat((byte)1, 100), .. new byte[92]];
            var rle = Pgs.Rle(pixels, 100, 2);
            var cut = Enumerable.Range(0, rle.Length).Select(n => Outcome(rle[..n], 100, 2)).Distinct().ToList();
            Check("RLE data cut short anywhere is refused as bad data, or decodes when only the last end-of-line went", cut.Contains("refused") && cut.All(o => o is "refused" or "decoded"), string.Join(",", cut));
            Check("the whole data still decodes to the same pixels", Pgs.Unrle(rle, 100, 2).SequenceEqual(pixels));
            byte[][] ends = [[0], [0, 0x40], [0, 0x80], [0, 0xC0, 5]];
            var shown = ends.Select(e => Outcome(e, 2, 2)).ToList();
            Check("each of the four cuts inside a run", shown.All(o => o == "refused"), string.Join(",", shown));
            Check("a damaged size of 65535 x 65535 is refused, not a crash", Outcome([1, 1], 65535, 65535) == "refused", Outcome([1, 1], 65535, 65535));
        });
        Part("damaged .sup", () =>
        {
            var writer = new PgsSubtitles.Writer(1920, 1080);
            (byte, byte, byte, byte)[] palette = [(0, 0, 0, 0), (255, 255, 255, 255)];
            byte[] picture = [.. Enumerable.Repeat((byte)1, 64 * 32)];
            writer.Add(0.2, 0.7, 800, 900, 64, 32, picture, palette);
            writer.Add(1.0, 1.7, 800, 900, 64, 32, picture, palette);
            var whole = writer.Finish();
            var output = new MemoryStream();
            var done = false;
            for (var p = 0; p < whole.Length;)
            {
                var size = whole[p + 11] << 8 | whole[p + 12];
                var kind = whole[p + 10];
                if (kind == 0x15 && !done) // the first picture loses the last byte of its data
                {
                    output.Write(Pgs.Segment(kind, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(whole.AsSpan(p + 2)), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(whole.AsSpan(p + 6)), whole.AsSpan(p + 13, size - 1)));
                    done = true;
                }
                else output.Write(whole, p, 13 + size);
                p += 13 + size;
            }
            File.WriteAllBytes(F("damaged.sup"), output.ToArray());
            var cues = PgsSubtitles.Cues(output.ToArray(), out var skipped);
            Check("a .sup with one picture cut short: that one is left out, the other stays", cues.Count == 1 && skipped == 1, $"{cues.Count} cues, {skipped} skipped");
        });
        DvdPart("a DVD with a damaged .sup", () =>
        {
            var lines = new List<string>();
            var clip = Clip("subclip.mkv", "testsrc=s=320x240:r=25:d=2", 48000, ["-pix_fmt", "yuv420p"], Ac3At48);
            Dvd.Build(new DvdJob([new DiscTitle(clip, Subtitles: [0], External: [new ExternalSubtitle(F("damaged.sup"), "fre")])], F("subdvd")), l => { lock (lines) lines.Add(l); });
            Check("a DVD with that .sup builds, and keeps its subtitles", DiscCheck.Folder(F("subdvd")).Count == 0 && lines.Any(l => l.Contains("spumux")) && !lines.Any(l => l.Contains("subtitles dropped")), string.Join(" ; ", DiscCheck.Folder(F("subdvd"))));
        });
    }

    /// <summary>Finding 11: a failed build leaves no work folder, and a folder from an earlier crash is not reused.</summary>
    static void WorkFolder()
    {
        var clip = "";
        Part("clip", () => clip = Clip("work.mkv", "testsrc=s=320x240:r=25:d=2", 48000, ["-pix_fmt", "yuv420p"], Ac3At48));
        File.WriteAllBytes(F("garbage.sup"), new byte[64]); // read after the video is made: the build fails half way
        var broken = new DiscTitle(clip, Subtitles: [0], External: [new ExternalSubtitle(F("garbage.sup"), "fre")]);
        var builds = new (string Name, Action<DiscTitle, string> Build)[]
        {
            ("Blu-ray", (t, o) => BluRay.Build(new BluRayJob([t], o, null, false, "CHECK", 5, Small("ultrafast"), 23.3))),
            ("DVD", (t, o) => Dvd.Build(new DvdJob([t], o))),
        };
        foreach (var (name, build) in builds)
        {
            if (noDvd && name == "DVD") { Console.WriteLine("skip DVD: a failed build leaves no work folder, nothing of an earlier crash is reused (--no-dvd)"); continue; }
            var failing = F("failing" + name);
            var threw = false;
            try { build(broken, failing); }
            catch (InvalidDataException) { threw = true; }
            Check($"{name}: a build that fails leaves no work folder", threw && !Directory.Exists(failing + "_work"), threw ? "the work folder is still there" : "the build did not fail");
            Part($"{name}: stale work folder", () =>
            {
                var again = F("stale" + name);
                Directory.CreateDirectory(again + "_work");
                File.WriteAllText(Path.Combine(again + "_work", "stale.txt"), "from a crash");
                Environment.SetEnvironmentVariable("BURNSMITH_KEEP_WORK", "1"); // the only way to see what the build worked with
                try { build(new DiscTitle(clip), again); }
                finally { Environment.SetEnvironmentVariable("BURNSMITH_KEEP_WORK", null); }
                Check($"{name}: nothing of an earlier crash is in the work folder", !File.Exists(Path.Combine(again + "_work", "stale.txt")));
            });
        }
    }

    /// <summary>Runs a copy of this program out of reach of the tools folder, with only what is listed for it to find: the only honest way to see a tool missing.</summary>
    static (int Exit, string Output, bool TimedOut) Isolated(string cli, string tools, int seconds, params string[] arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(cli, "BurnSmith.Cli.exe")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in arguments) psi.ArgumentList.Add(x);
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, Path.GetDirectoryName(ffmpeg), Path.GetDirectoryName(ffprobe));
        psi.Environment["BURNSMITH_TOOLS"] = tools;
        using var p = new Process { StartInfo = psi };
        var text = new System.Text.StringBuilder();
        DataReceivedEventHandler add = (_, e) => { lock (text) text.AppendLine(e.Data); };
        p.OutputDataReceived += add;
        p.ErrorDataReceived += add;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var ended = p.WaitForExit(seconds * 1000);
        if (!ended) p.Kill(true);
        p.WaitForExit();
        return (ended ? p.ExitCode : -1, text.ToString(), !ended);
    }

    /// <summary>Finding 7: a DVD that cannot be finished is refused before anything is encoded, and what does not need spumux still builds without it.</summary>
    static void DvdRefusals()
    {
        Part("DVD sums", () =>
        {
            var hour = new SourceInfo("h264", 1920, 1080, 24, "ac3", 2, 3600, [new(0, "ac3", 2, "fre", "")], [new(0, "subrip", "fre", "", false)]);
            long Needed(DiscTitle t) => Dvd.NeededBytes(new DvdJob([t], ""), [(hour, t)], 4_000_000);
            var plain = Needed(new DiscTitle("x")); // (4 Mb/s + 192 kb/s) for an hour = 1.886 GB, twice, plus 5 %
            Check("space needed: the MPEG and its VOB; one more copy with subtitles", Math.Abs(plain - 3.96e9) < 0.01e9 && Math.Abs(Needed(new DiscTitle("x", Subtitles: [0])) - 5.94e9) < 0.01e9, $"{plain}");
            Check("spumux is needed for a menu or a subtitle, not for a start video's nothing",
                  Dvd.NeededTools(new DvdJob([new DiscTitle("x")], "")).SequenceEqual(["dvdauthor.exe"]) && Dvd.NeededTools(new DvdJob([new DiscTitle("x", Subtitles: [0], Intro: true), new DiscTitle("y")], "")).SequenceEqual(["dvdauthor.exe"])
                  && Dvd.NeededTools(new DvdJob([new DiscTitle("x", Subtitles: [0])], "")).Contains("spumux.exe") && Dvd.NeededTools(new DvdJob([new DiscTitle("x")], "", new MenuDesign())).Contains("spumux.exe"));
        });
        Part("DVD refusals", () =>
        {
            var cli = F("cli");
            Directory.CreateDirectory(cli);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(cli, Path.GetFileName(file)));
            var nothing = F("no-tools");
            Directory.CreateDirectory(nothing);
            var clip = Clip("dvdclip.mkv", "testsrc=s=320x240:r=25:d=2", 48000, ["-pix_fmt", "yuv420p"], Ac3At48);
            File.WriteAllText(F("dvdclip.fr.srt"), "1\n00:00:00,500 --> 00:00:01,500\nBonjour\n");

            void Refused(string what, string missing, string tools, string name, params string[] more)
            {
                var result = Isolated(cli, tools, 120, ["dvd", "--video", clip, "--out", F(name), .. more]);
                Check(what, result.Exit != 0 && result.Output.Contains(missing) && !result.Output.Contains("encode 1") && !Directory.Exists(F(name) + "_work"),
                      $"exit {result.Exit}, {(result.Output.Contains("encode 1") ? "it encoded first" : "nothing encoded")}, {(result.Output.Contains(missing) ? "names " + missing : "does not name " + missing)}");
            }
            Refused("no dvdauthor: refused before anything is encoded", "dvdauthor.exe", nothing, "nodvdauthor", "--no-menu");
            if (noDvd) { Console.WriteLine("skip no spumux refused or still building, no room on the drive (--no-dvd)"); return; }
            var real = Path.GetDirectoryName(Tools.Find("dvdauthor.exe"))!;
            var noSpumux = F("no-spumux");
            Directory.CreateDirectory(noSpumux);
            foreach (var file in Directory.EnumerateFiles(real).Where(x => !Path.GetFileName(x).Equals("spumux.exe", StringComparison.OrdinalIgnoreCase))) File.Copy(file, Path.Combine(noSpumux, Path.GetFileName(file)));
            Refused("no spumux, a subtitle: refused before anything is encoded, not \"subtitles dropped\"", "spumux.exe", noSpumux, "nospumuxsub", "--no-menu", "--sub-file", F("dvdclip.fr.srt"));
            Refused("no spumux, a menu: refused before anything is encoded", "spumux.exe", noSpumux, "nospumuxmenu");
            var plain = Isolated(cli, noSpumux, 120, "dvd", "--video", clip, "--out", F("plaindvd"), "--no-menu");
            Check("no spumux, neither menu nor subtitle: the DVD still builds", plain.Exit == 0, plain.Output.Split('\n').TakeLast(4).FirstOrDefault() ?? "");

            // far more than any drive holds at the lowest rate; a file this long is a few frames, so only the sums see it
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "color=s=64x64:r=1/36000:d=360000000", "-frames:v", "10000", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", F("huge.mkv")]);
            var full = Isolated(cli, real, 30, "dvd", "--video", F("huge.mkv"), "--out", F("hugedvd"), "--no-menu");
            Check("no room on the drive: refused before anything is encoded", full.Exit != 0 && !full.TimedOut && !full.Output.Contains("encode 1") && !Directory.Exists(F("hugedvd") + "_work"),
                  full.TimedOut ? "still encoding after 30 s" : full.Output.Split('\n').FirstOrDefault() ?? "");
        });
    }
}
