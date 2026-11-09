using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BurnSmith.Core;
using BurnSmith.Core.Disc;
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

    /// <summary>A part that builds a real DVD (slow): only --no-dvd passes over it, and says so.</summary>
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

    /// <summary>Whether the stream's H.264 is flagged as fields (frame_mbs_only_flag 0): how a Blu-ray carries 1080 at 25 or 29.97.</summary>
    static bool Fields(string m2ts) => BluRay.FlaggedAsFields(m2ts);

    /// <summary>Where each file of an image's <paramref name="folder"/> starts (sector) and its size, read through the image's ISO 9660 side.</summary>
    static Dictionary<string, (uint At, uint Size)> IsoFiles(string iso, string folder)
    {
        using var file = File.OpenRead(iso);
        byte[] Read(long sector, long bytes) { var b = new byte[bytes]; file.Position = sector * 2048; file.ReadExactly(b); return b; }
        Dictionary<string, (uint, uint)> List(uint at, uint size)
        {
            var d = Read(at, size);
            var found = new Dictionary<string, (uint, uint)>();
            for (var o = 0; o < d.Length;)
            {
                if (d[o] == 0) { o = (o / 2048 + 1) * 2048; continue; } // records never cross a sector's end
                found[System.Text.Encoding.ASCII.GetString(d, o + 33, d[o + 32]).Split(';')[0]] = (BitConverter.ToUInt32(d, o + 2), BitConverter.ToUInt32(d, o + 10));
                o += d[o];
            }
            return found;
        }
        var pvd = Read(16, 2048);
        var (at, size) = List(BitConverter.ToUInt32(pvd, 158), BitConverter.ToUInt32(pvd, 166))[folder];
        return List(at, size);
    }

    /// <summary>The colour at the centre of a stream's first picture, decoded with <paramref name="matrix"/> (bt709 for a Blu-ray, bt601 for a DVD), as a player does.</summary>
    static (int R, int G, int B) Centre(string video, string matrix)
    {
        var raw = F("centre.rgb");
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", video, "-vf", $"crop=8:8:iw/2:ih/2,scale=in_color_matrix={matrix},format=rgb24", "-frames:v", "1", "-f", "rawvideo", raw]);
        var px = File.ReadAllBytes(raw);
        return (px[0], px[1], px[2]);
    }

    /// <summary>A flat red clip whose colours are written with <paramref name="matrix"/>, and said to be.</summary>
    static string Red(string name, string size, string matrix) => Clip(name, $"color=c=red:s={size}:r=25:d=2", 48000,
        ["-vf", $"format=rgb24,scale=out_color_matrix={matrix}:out_range=tv,format=yuv420p", "-colorspace", matrix == "bt709" ? "bt709" : "smpte170m", .. Fine], Ac3At48);

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
            Signature();
            Popup();
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
        Part("3D side by side", () =>
        {
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=3840x1080:r=24:d=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                               "-metadata:s:v:0", "stereo_mode=left_right", F("sbs.mkv")]);
            Check("a full side-by-side clip is seen as 3D", BluRay.Probe(F("sbs.mkv")).Stereo == "sbs", BluRay.Probe(F("sbs.mkv")).Stereo);
            var size = Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v", "-show_entries", "stream=width,height", "-of", "csv=p=0", Stream(Bd("sbs", [new DiscTitle(F("sbs.mkv"))], new EncodeOptions(Preset: "ultrafast")), 1)]).Trim().Split('\n')[0].Trim();
            Check("it fills the 1080 picture as half side by side (no bars)", size == "1920,1080", size);
        });
        Part("Blu-ray 3D image", () =>
        {
            // a disc folder as tsMuxeR writes a 3D film (no encoder makes MVC, so the clips are noise): 00202 the left eye, 00203 the right eye, cut in
            // 6144-byte units at the points their .clpi record; plus enough files that the STREAM directory takes more than one sector, and an empty one
            var disc = F("disc3d");
            var (stream, clipinf) = (Path.Combine(disc, "BDMV", "STREAM"), Path.Combine(disc, "BDMV", "CLIPINF"));
            Directory.CreateDirectory(stream);
            Directory.CreateDirectory(clipinf);
            var random = new Random(3);
            byte[] Noise(int n) { var b = new byte[n]; random.NextBytes(b); return b; }
            List<byte[]> Clip3D(string name, int[] units)
            {
                var data = Noise(units.Sum() * 6144);
                var clpi = new byte[0x38 + 8 + 4 * units.Length];
                "HDMV0200"u8.CopyTo(clpi);
                BinaryPrimitives.WriteUInt32BigEndian(clpi.AsSpan(0x18), 0x20); // extension data
                clpi[0x2B] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(clpi.AsSpan(0x2C), 2); BinaryPrimitives.WriteUInt16BigEndian(clpi.AsSpan(0x2E), 4); // ExtentStartPoint
                BinaryPrimitives.WriteUInt32BigEndian(clpi.AsSpan(0x30), 24);
                BinaryPrimitives.WriteUInt16BigEndian(clpi.AsSpan(0x38 + 6), (ushort)units.Length);
                var pieces = new List<byte[]>();
                for (int i = 0, at = 0; i < units.Length; at += units[i++])
                {
                    BinaryPrimitives.WriteUInt32BigEndian(clpi.AsSpan(0x38 + 8 + 4 * i), (uint)(at * 32)); // source packets of 192 bytes
                    pieces.Add(data[(at * 6144)..((at + units[i]) * 6144)]);
                }
                File.WriteAllBytes(Path.Combine(stream, name + ".m2ts"), data);
                File.WriteAllBytes(Path.Combine(clipinf, name + ".clpi"), clpi);
                return pieces;
            }
            // 300 pieces each: more extents than one file entry holds (112), so its list goes on in continuation sectors, as with a real film (~1600 pieces)
            var (left, right) = (Clip3D("00202", [.. Enumerable.Range(0, 300).Select(_ => random.Next(1, 4))]), Clip3D("00203", [.. Enumerable.Range(0, 300).Select(_ => random.Next(1, 3))]));
            for (var i = 0; i < 80; i++) File.WriteAllBytes(Path.Combine(stream, $"{300 + i:00000}.m2ts"), Noise(random.Next(1, 5000)));
            File.WriteAllBytes(Path.Combine(stream, "00400.m2ts"), []);
            File.WriteAllBytes(Path.Combine(disc, "BDMV", "index.bdmv"), Noise(120));
            var iso = F("disc3d.iso");
            Iso.Build(disc, iso, "CHECK3D", dvd: false);
            string Shell(string command) => Tools.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command]).Trim();
            var drive = Shell($"(Mount-DiskImage -ImagePath '{iso}' -Access ReadOnly -PassThru | Get-Volume).DriveLetter") + @":\";
            try
            {
                var differ = Directory.GetFiles(disc, "*", SearchOption.AllDirectories)
                    .Where(f => !File.Exists(drive + Path.GetRelativePath(disc, f)) || !File.ReadAllBytes(f).SequenceEqual(File.ReadAllBytes(drive + Path.GetRelativePath(disc, f)))).ToList();
                Check("Windows reads every file of BurnSmith's own image as it was (83 in STREAM, one empty)", differ.Count == 0, string.Join(' ', differ.Select(Path.GetFileName)));
                var ssif = drive + @"BDMV\STREAM\SSIF\00202.ssif";
                var expected = left.Zip(right, (l, r) => r.Concat(l)).SelectMany(p => p).ToArray();
                Check("the .ssif is both eyes in turns, right eye first, from the same sectors (no room taken twice)",
                      File.Exists(ssif) && File.ReadAllBytes(ssif).SequenceEqual(expected) && new FileInfo(iso).Length < 3 * 1024 * 1024 + expected.Length + 80 * 8192,
                      File.Exists(ssif) ? $"{new FileInfo(ssif).Length} bytes, image {new FileInfo(iso).Length}" : "no .ssif");
            }
            finally { Shell($"Dismount-DiskImage -ImagePath '{iso}' | Out-Null"); }
        });
        Part("4K HDR10+ and Dolby Vision", () =>
        {
            // a raw HDR10 stream, then the same with HDR10+ (hdr10plus_tool) or a Dolby Vision profile 8.1 RPU (dovi_tool) put in, timed by a plain tsMuxeR TS
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=3840x2160:r=24:d=2", "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
                               "-x265-params", "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1:repeat-headers=1:keyint=12", "-f", "hevc", F("base.hevc")]);
            string Timed(string hevc)
            {
                File.WriteAllLines(F("timed.meta"), ["MUXOPT --vbr", $"V_MPEGH/ISO/HEVC, \"{hevc}\", fps=24"]);
                Tools.Run(Tools.Find("tsMuxeR.exe"), [F("timed.meta"), Path.ChangeExtension(hevc, ".ts")]);
                return Path.ChangeExtension(hevc, ".ts");
            }
            string FirstFrame(string file) => Tools.Run(ffprobe, ["-v", "error", "-of", "json", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_frames", file]);
            var frames = string.Join(',', Enumerable.Range(0, 48).Select(i => $"{{\"LuminanceParameters\":{{\"AverageRGB\":1200,\"LuminanceDistributions\":{{\"DistributionIndex\":[1,5,10,25,50,75,90,95,99],\"DistributionValues\":[10,50,100,300,600,900,1500,2000,4000]}},\"MaxScl\":[5000,6000,4000]}},\"NumberOfWindows\":1,\"TargetedSystemDisplayMaximumLuminance\":0,\"SceneFrameIndex\":{i},\"SceneId\":0,\"SequenceFrameIndex\":{i}}}"));
            File.WriteAllText(F("hdr10plus.json"), $"{{\"JSONInfo\":{{\"HDR10plusProfile\":\"A\",\"Version\":\"1.0\"}},\"SceneInfo\":[{frames}],\"SceneInfoSummary\":{{\"SceneFirstFrameIndex\":[0],\"SceneFrameNumbers\":[48]}},\"ToolInfo\":{{\"Tool\":\"check\",\"Version\":\"1\"}}}}");
            Tools.Run(Tools.Find("hdr10plus_tool.exe"), ["inject", "-i", F("base.hevc"), "-j", F("hdr10plus.json"), "-o", F("plus.hevc")]);
            var plus = Timed(F("plus.hevc"));
            Check("an HDR10+ clip is seen as HDR10+", BluRay.Probe(plus).Hdr == "HDR10+", BluRay.Probe(plus).Hdr);
            Check("a 4K disc keeps its HDR10+ metadata", FirstFrame(Stream(Bd("uhdplus", [new DiscTitle(plus)], new EncodeOptions(), uhd: true), 1)).Contains("SMPTE2094-40"));

            File.WriteAllText(F("rpu.json"), "{\"cm_version\":\"V40\",\"profile\":\"8.1\",\"length\":48,\"level6\":{\"max_display_mastering_luminance\":1000,\"min_display_mastering_luminance\":1,\"max_content_light_level\":1000,\"max_frame_average_light_level\":400}}");
            Tools.Run(Tools.Find("dovi_tool.exe"), ["generate", "-j", F("rpu.json"), "-o", F("rpu.bin")]);
            Tools.Run(Tools.Find("dovi_tool.exe"), ["inject-rpu", "-i", F("base.hevc"), "--rpu-in", F("rpu.bin"), "-o", F("dv.hevc")]);
            var dv = Timed(F("dv.hevc"));
            var info = BluRay.Probe(dv);
            Check("a Dolby Vision 8.1 clip is seen as single-layer Dolby Vision", info is { Hdr: "Dolby Vision", DualLayer: false }, $"{info.Hdr} {info.DualLayer}");
            var m2ts = Stream(Bd("uhddv", [new DiscTitle(dv)], new EncodeOptions(), uhd: true), 1);
            var pids = Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v", "-show_entries", "stream=id", "-of", "csv=p=0", m2ts]).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            // tsMuxeR puts a single-layer Dolby Vision track on the enhancement-layer PID (0x1015), with no base: no UHD player reads that disc
            Check("single-layer Dolby Vision becomes the HDR10 base on PID 0x1011, without its RPU", pids is ["0x1011"] && !FirstFrame(m2ts).Contains("Dolby Vision RPU"), string.Join(' ', pids));

            // re-encoded, it is kept as profile 7 MEL: an enhancement layer made apart, with the base's frames and structure
            var mel = Stream(Bd("uhdmel", [new DiscTitle(dv, Start: 0.5)], new EncodeOptions(ForceReencode: true, Preset: "ultrafast"), uhd: true), 1);
            var melPids = Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v", "-show_entries", "stream=id", "-of", "csv=p=0", mel]).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            Check("re-encoded Dolby Vision gets its enhancement layer on PID 0x1015", melPids is ["0x1011", "0x1015"], string.Join(' ', melPids));
            string Types(int track)
            {
                Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", mel, "-map", $"0:v:{track}", "-c", "copy", "-f", "hevc", F($"layer{track}.hevc")]);
                return string.Concat(Tools.Run(ffprobe, ["-v", "error", "-show_entries", "frame=pict_type", "-of", "csv=p=0", F($"layer{track}.hevc")]).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && "IPB".Contains(l[0])).Select(l => l[0])); // side data adds lines of its own
            }
            var (bl, el) = (Types(0), Types(1));
            Check("the enhancement layer has the base's frames, in the same structure", bl.Length > 0 && bl == el, $"{bl.Length} / {el.Length}");
            Tools.Run(Tools.Find("dovi_tool.exe"), ["extract-rpu", F("layer1.hevc"), "-o", F("mel.bin")]);
            var summary = Tools.Run(Tools.Find("dovi_tool.exe"), ["info", "-i", F("mel.bin"), "--summary"]);
            Check("its metadata is profile 7 MEL, one per frame", summary.Contains("Profile: 7 (MEL)") && summary.Contains($"Frames: {bl.Length}"), summary.Split('\n').FirstOrDefault(l => l.Contains("Profile"))?.Trim() ?? "");
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
            var m2ts = Stream(Bd("fps50", [new DiscTitle(clip)], new EncodeOptions(Preset: "ultrafast")), 1);
            var (video, sound) = (End(m2ts, "v"), End(m2ts, "a"));
            Check("on a 1080 disc a 50 fps source becomes 25 fps, as on a DVD, in step (within the two frames a rate change adds at the end)", Look(m2ts).Rate == "25/1" && Math.Abs(video - sound) < 0.1, $"{Look(m2ts).Rate}, video {video:0.000} s, sound {sound:0.000} s");
            Check("1080 at 25 fps is flagged as fields (PsF), the only way a Blu-ray carries it", Fields(m2ts));
            var hd = Look(Stream(Bd("fps50hd", [new DiscTitle(clip)], Small("ultrafast")), 1));
            Check("on a 720p disc, which has 50 and no 25, it stays 50 fps", hd.Rate == "50/1", hd.Rate);
        });
        Part("29.97 fps", () =>
        {
            var clip = Clip("fps2997.mkv", "testsrc=s=320x180:r=30000/1001:d=3", 48000, ["-pix_fmt", "yuv420p"], Aac);
            var m2ts = Stream(Bd("fps2997", [new DiscTitle(clip)], new EncodeOptions(Preset: "ultrafast")), 1);
            var (video, sound) = (End(m2ts, "v"), End(m2ts, "a"));
            Check("a 29.97 fps source keeps every picture on a 1080 disc (it lost one in five to 23.976), flagged as fields, in step",
                  Look(m2ts).Rate == "30000/1001" && Fields(m2ts) && Math.Abs(video - sound) < 0.1, $"{Look(m2ts).Rate}, video {video:0.000} s, sound {sound:0.000} s");
            var pal = Look(Stream(Bd("fps25hd", [new DiscTitle(Clip("fps25.mkv", "testsrc=s=320x180:r=25:d=2", 48000, ["-pix_fmt", "yuv420p"], Aac))], Small("ultrafast")), 1));
            var hd = Look(Stream(Bd("fps2997hd", [new DiscTitle(clip)], Small("ultrafast")), 1));
            Check("a 720p disc has no 25 nor 29.97: each picture is shown twice, 50 and 59.94", pal.Rate == "50/1" && hd.Rate == "60000/1001", $"{pal.Rate}, {hd.Rate}");
        });
        Part("1080 at 25 fps", () =>
        {
            var progressive = Clip("p25.mkv", "testsrc2=s=1920x1080:r=25:d=2", 48000, Fine, Ac3At48);
            var made = Stream(Bd("p25", [new DiscTitle(progressive)], new EncodeOptions(Preset: "ultrafast")), 1);
            Check("a progressive 1080p25 H.264 a player could take is made again, flagged as fields, not copied as it is", Look(made).Reorder > 0 && Fields(made), $"has_b_frames {Look(made).Reorder}");
            var psf = Clip("psf25.mkv", "testsrc2=s=1920x1080:r=25:d=2", 48000, [.. Fine, "-x264-params", "fake-interlaced=1:pic-struct=1"], Ac3At48);
            var kept = Stream(Bd("psf25", [new DiscTitle(psf)], new EncodeOptions(Preset: "ultrafast")), 1);
            Check("the same flagged as fields (PsF, or a real 1080i) is copied", Look(kept).Reorder == 0 && Fields(kept), $"has_b_frames {Look(kept).Reorder}");
            var hd = Look(Stream(Bd("p25hd", [new DiscTitle(Clip("p25hd.mkv", "testsrc2=s=1280x720:r=25:d=2", 48000, Fine, Ac3At48))], Small("ultrafast")), 1));
            Check("a 720p25 H.264 a player could take is still made again, at 50 (720p has no 25)", hd.Rate == "50/1", hd.Rate);
        });
        Part("colours of a standard-definition source", () =>
        {
            var m2ts = Stream(Bd("sd601", [new DiscTitle(Red("sd601.mkv", "720x576", "bt601"))], new EncodeOptions(Preset: "ultrafast")), 1);
            var c = Centre(m2ts, "bt709");
            Check("a BT.601 red stays red on a Blu-ray, decoded as BT.709 (not turned orange)", c.R > 245 && c.G < 12, $"rgb {c}");
        });
        DvdPart("colours of an HD source on a DVD", () =>
        {
            Dvd.Build(new DvdJob([new DiscTitle(Red("hd709.mkv", "1280x720", "bt709"))], F("hd709")));
            var c = Centre(Path.Combine(F("hd709"), "VIDEO_TS", "VTS_01_1.VOB"), "bt601");
            Check("a BT.709 red stays red on a DVD, decoded as BT.601", c.R > 245 && c.G < 12, $"rgb {c}");
        });
        Part("HDR on a 1080 disc", () =>
        {
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=24000/1001:d=1", "-f", "lavfi", "-i", "sine=f=440:d=1", "-map", "0:v", "-map", "1:a",
                               "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc", "-c:a", "ac3", F("hdr10.mkv")]);
            Check("the test clip is seen as HDR10", BluRay.Probe(F("hdr10.mkv")).Hdr == "HDR10", BluRay.Probe(F("hdr10.mkv")).Hdr);
            var m2ts = Stream(Bd("hdr1080", [new DiscTitle(F("hdr10.mkv"))], new EncodeOptions(Preset: "ultrafast")), 1);
            var colours = Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v", "-show_entries", "stream=color_transfer,color_primaries", "-of", "csv=p=0", m2ts]).Trim().Split('\n')[0].Trim(); // an m2ts lists its stream twice (program, stream)
            Check("its PQ picture is made SDR BT.709 (copied as it is, a player shows it grey)", colours == "bt709,bt709", colours);
            Check("a 4K HDR film is made 1080 wide before it is converted (a quarter of the pixels to convert)", BluRay.ToSdr("HDR10", 3840, 2160, 1920).StartsWith("scale=1920:1080,zscale="), BluRay.ToSdr("HDR10", 3840, 2160, 1920)[..30]);
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

    /// <summary>The menu's signature: a signed jar passes what a player checks; one changed after signing, or signed under another disc root, does not.</summary>
    static void Signature()
    {
        Part("BD-J signature", () =>
        {
            (string, byte[])[] files = [("org/burnsmith/menu/Menu.class", [0xCA, 0xFE, 0xBA, 0xBE]), ("menu.txt", "x"u8.ToArray()),
                                        ("hl_" + new string('é', 40) + "_a_name_longer_than_one_72_byte_manifest_line.png", [1, 2, 3])]; // the manifest's continued lines
            string Signed(string name)
            {
                Directory.CreateDirectory(F(name));
                BdjSign.Write(Path.Combine(F(name), "00000.jar"), files, F(name));
                return Path.Combine(F(name), "00000.jar");
            }
            var (jar, other) = (Signed("signed"), Signed("other"));
            byte[] Cert(string file) => File.ReadAllBytes(Path.Combine(F("signed"), "CERTIFICATE", file));
            var found = BdjSign.Verify(jar, Cert("app.discroot.crt"), Cert("id.bdmv"));
            Check("signed menu: what a player checks holds", found.Count == 0, string.Join(" ; ", found));
            Check("a menu signed under another disc root: refused", BdjSign.Verify(other, Cert("app.discroot.crt"), Cert("id.bdmv")).Count > 0);
            using (var z = System.IO.Compression.ZipFile.Open(jar, System.IO.Compression.ZipArchiveMode.Update))
            {
                z.GetEntry("menu.txt")!.Delete();
                using var s = z.CreateEntry("menu.txt").Open();
                s.Write("y"u8);
            }
            Check("a file changed after signing: refused", BdjSign.Verify(jar, Cert("app.discroot.crt"), Cert("id.bdmv")).Count > 0);
        });
    }

    /// <summary>
    /// The pop-up menu: with the option, the menu file has a "pop" page per title (resume, chapters, settings, main menu), its chapter pages and the
    /// shared settings page, drawn as transparent overlays; without it, nothing of the pop-up is on the disc.
    /// </summary>
    static void Popup()
    {
        Part("pop-up menu", () =>
        {
            var film = Clip("popfilm.mkv", "testsrc2=s=1280x720:r=24000/1001:d=6", 48000, Fine, Ac3At48);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", film, "-map", "0", "-map", "0:a", "-c", "copy", F("pop2audio.mkv")]); // two audio tracks: the settings page exists
            List<DiscTitle> titles = [new DiscTitle(F("pop2audio.mkv"), Audio: [0, 1], ChapterAt: [0, 2, 4]), new DiscTitle(film, Bonus: true)];
            Dictionary<string, byte[]> Jar(string disc)
            {
                using var z = System.IO.Compression.ZipFile.OpenRead(Path.Combine(disc, "BDMV", "JAR", "00000.jar"));
                return z.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); });
            }
            var with = Jar(Bd("popup", titles, menu: new MenuDesign { Popup = true, MainMenuLabel = "Main" }));
            var txt = System.Text.Encoding.UTF8.GetString(with["menu.txt"]).Split('\n');
            bool Has(string start) => txt.Any(l => l.StartsWith(start, StringComparison.Ordinal));
            Check("pop-up: a bar per title, the film's with resume, chapters, settings and main menu",
                Has("page pop1 bg_pop1.png") && Has("page pop2 bg_pop2.png") && Has("button pop1 resume ") && Has("button pop1 chapters ") && Has("button pop1 settings ") && Has("button pop1 main "),
                string.Join(" | ", txt.Where(l => l.StartsWith("page pop", StringComparison.Ordinal))));
            Check("pop-up: the bonus (one chapter) has no chapters button", !Has("button pop2 chapters "));
            Check("pop-up: the film's 3 chapters seek in its own playlist", Has("button popc1_1 c3 ") && txt.Any(l => l.StartsWith("button popc1_1 c3 ", StringComparison.Ordinal) && l.EndsWith(" chapter 1 2", StringComparison.Ordinal)));
            Check("pop-up: settings page with its own marker offset, back leads to the bar",
                txt.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^page popset bg_popset\.png -?\d+$")) && txt.Any(l => l.StartsWith("button popset back ", StringComparison.Ordinal) && l.EndsWith(" popup", StringComparison.Ordinal)));
            Check("pop-up: main menu and resume actions", txt.Any(l => l.StartsWith("button pop1 main ", StringComparison.Ordinal) && l.EndsWith(" menu", StringComparison.Ordinal))
                && txt.Any(l => l.StartsWith("button pop1 resume ", StringComparison.Ordinal) && l.EndsWith(" resume", StringComparison.Ordinal)));
            // the film shows through: the top left corner of the bar's picture is fully transparent, its bottom is not
            var bar = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(with["bg_pop1.png"]), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(bar, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            byte Alpha(int x, int y) { var px = new byte[4]; bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0); return px[3]; }
            Check("pop-up: transparent over the film, solid on the bar", Alpha(0, 0) == 0 && Alpha(5, bgra.PixelHeight - 5) > 100, $"alpha {Alpha(0, 0)} / {Alpha(5, bgra.PixelHeight - 5)}");
            var bars = MenuBuilder.Popup(new MenuDesign { MainMenuLabel = "Main" }, new MenuContent(1, null, []), [(1, 1)]);
            Check("pop-up: the main menu button says what the design sets", bars[0].Ops.OfType<Label>().Any(l => l.Value == "Main"));
            var without = Jar(Bd("nopopup", titles, menu: new MenuDesign()));
            Check("no pop-up option: no pop-up page, the menu file as before", !without.Keys.Any(k => k.Contains("pop")) && !System.Text.Encoding.UTF8.GetString(without["menu.txt"]).Contains(" pop"));
        });
    }

    /// <summary>Finding 12: a PGS picture cut short is left out; it costs neither the other pictures nor the build.</summary>
    static void Damaged()
    {
        static string Outcome(byte[] data, int w, int h)
        {
            try { PgsStream.Unpack(data, w, h); return "decoded"; }
            catch (InvalidDataException) { return "refused"; }
            catch (Exception e) { return e.GetType().Name; }
        }
        Part("RLE", () =>
        {
            byte[] pixels = [0, 0, 1, 1, 1, 1, 2, 0, .. Enumerable.Repeat((byte)1, 100), .. new byte[92]];
            var rle = PgsStream.Pack(pixels, 100, 2);
            var cut = Enumerable.Range(0, rle.Length).Select(n => Outcome(rle[..n], 100, 2)).Distinct().ToList();
            Check("RLE data cut short anywhere is refused as bad data, or decodes when only the last end-of-line went", cut.Contains("refused") && cut.All(o => o is "refused" or "decoded"), string.Join(",", cut));
            Check("the whole data still decodes to the same pixels", PgsStream.Unpack(rle, 100, 2).SequenceEqual(pixels));
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
                    output.Write(PgsStream.Segment(kind, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(whole.AsSpan(p + 2)), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(whole.AsSpan(p + 6)), whole.AsSpan(p + 13, size - 1)));
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
            Check("a DVD with that .sup builds, and keeps its subtitles", DiscCheck.Folder(F("subdvd")).Count == 0 && lines.Any(l => l.Contains("subtitles: render + mux")) && !lines.Any(l => l.Contains("subtitles dropped")), string.Join(" ; ", DiscCheck.Folder(F("subdvd"))));
        });
        DvdPart("a film on a DVD", () =>
        {
            var clip = Clip("filmdvd.mkv", "testsrc=s=320x240:r=24000/1001:d=2", 48000, ["-pix_fmt", "yuv420p"], Ac3At48);
            Dvd.Build(new DvdJob([new DiscTitle(clip)], F("filmdvd")));
            var shown = Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v", "-show_entries", "stream=field_order,r_frame_rate", "-of", "csv=p=0", Path.Combine(F("filmdvd"), "VIDEO_TS", "VTS_01_1.VOB")]).Trim();
            Check("a 24 fps film becomes 3:2 pulldown at 29.97, top field first", shown.StartsWith("tt,30000/1001"), shown);

            var log = new List<string>();
            Imapi.BuildImage(F("filmdvd"), F("film.iso"), "Film", dvd: true, log.Add);
            var placed = IsoFiles(F("film.iso"), "VIDEO_TS");
            var vmg = File.ReadAllBytes(Path.Combine(F("filmdvd"), "VIDEO_TS", "VIDEO_TS.IFO"));
            var srpt = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(0xC4)) * 2048;
            var announced = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(srpt + 16)); // title 1's title set, in sectors from VIDEO_TS.IFO
            var real = placed["VTS_01_0.IFO"].At - placed["VIDEO_TS.IFO"].At;
            Check("in the DVD image the title set is where VIDEO_TS.IFO says (a player goes by sectors, not by names)", real == announced, $"announced {announced}, placed {real}");
            Check("each title set in the standard's order: IFO, menus, film, then BUP", placed["VIDEO_TS.IFO"].At < placed["VIDEO_TS.BUP"].At
                  && placed["VTS_01_0.IFO"].At < placed["VTS_01_1.VOB"].At && placed["VTS_01_1.VOB"].At < placed["VTS_01_0.BUP"].At,
                  string.Join(", ", placed.Where(p => p.Key.Contains('.')).OrderBy(p => p.Value.At).Select(p => $"{p.Key} {p.Value.At}")));
            Check("the image's own check finds nothing out of place", !log.Any(l => l.StartsWith("warning")), string.Join(" ; ", log));
            using (var image = File.OpenRead(F("film.iso")))
            {
                var anchor = new byte[2048];
                image.Position = 256 * 2048;
                image.ReadExactly(anchor);
                Check("UDF 1.02 with its anchor at sector 256", anchor[0] == 2 && Imapi.UdfRevision(F("film.iso")) == (1, 2), $"tag {anchor[0]}");
            }
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

    /// <summary>Finding 7: a DVD that cannot be finished is refused before anything is encoded; a DVD needs no authoring tool besides ffmpeg.</summary>
    static void DvdRefusals()
    {
        Part("DVD sums", () =>
        {
            var hour = new SourceInfo("h264", 1920, 1080, 24, "ac3", 2, 3600, [new(0, "ac3", 2, "fre", "")], [new(0, "subrip", "fre", "", false)]);
            long Needed(DiscTitle t) => Dvd.NeededBytes(new DvdJob([t], ""), [(hour, t)], 4_000_000);
            var plain = Needed(new DiscTitle("x")); // (4 Mb/s + 192 kb/s) for an hour = 1.886 GB, twice, plus 5 %
            Check("space needed: the MPEG and its VOB; one more copy with subtitles", Math.Abs(plain - 3.96e9) < 0.01e9 && Math.Abs(Needed(new DiscTitle("x", Subtitles: [0])) - 5.94e9) < 0.01e9, $"{plain}");
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
            if (noDvd) { Console.WriteLine("skip a menu and a subtitle with ffmpeg alone, no room on the drive (--no-dvd)"); return; }
            var built = Isolated(cli, nothing, 300, "dvd", "--video", clip, "--out", F("plaindvd"), "--sub-file", F("dvdclip.fr.srt"));
            Check("a menu and a subtitle, no tool but ffmpeg: the DVD builds", built.Exit == 0 && File.Exists(Path.Combine(F("plaindvd"), "VIDEO_TS", "VIDEO_TS.IFO")), built.Output.Split('\n').TakeLast(4).FirstOrDefault() ?? "");

            // far more than any drive holds at the lowest rate; a file this long is a few frames, so only the sums see it
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "color=s=64x64:r=1/36000:d=360000000", "-frames:v", "10000", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", F("huge.mkv")]);
            var full = Isolated(cli, nothing, 30, "dvd", "--video", F("huge.mkv"), "--out", F("hugedvd"), "--no-menu");
            Check("no room on the drive: refused before anything is encoded", full.Exit != 0 && !full.TimedOut && !full.Output.Contains("encode 1") && !Directory.Exists(F("hugedvd") + "_work"),
                  full.TimedOut ? "still encoding after 30 s" : full.Output.Split('\n').FirstOrDefault() ?? "");
        });
    }
}
