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

    static string Bd(string name, IReadOnlyList<DiscTitle> titles, EncodeOptions? encode = null, MenuDesign? menu = null, double capacity = 23.3, Action<string>? log = null, bool uhd = false, bool hdmv = false)
    {
        var disc = F(name);
        BluRay.Build(new BluRayJob(titles, disc, menu, uhd, "CHECK", 5, encode ?? Small(), capacity, HdmvMenu: hdmv), log);
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
            AuditStreams();
            Sync();
            FitDisc();
            Signature();
            Popup();
            DvdAudio();
            Hdmv();
            Damaged();
            WorkFolder();
            DvdRefusals();
        }
        catch (FileNotFoundException e) { Check("the tools this check needs", false, e.Message); }
        finally { Tools.TryDelete(root); }
        Console.WriteLine(failed == 0 ? "check-bd: all passed" : $"check-bd: {failed} FAILED");
        return failed == 0 ? 0 : 1;
    }

    static void AuditStreams()
    {
        Part("declared audio must carry packets on folder and ISO", () =>
        {
            var source = Clip("auditac3.mkv", "color=s=1280x720:r=24000/1001:d=1", 48000, Fine, Ac3At48);
            var disc = Bd("auditac3", [new DiscTitle(source)]);
            var iso = F("audit-valid.iso");
            Iso.Build(disc, iso, "AUDIT", dvd: false);
            Check("the valid ISO contains the audio PID", DiscCheck.Image(iso).Count == 0);

            var clip = Stream(disc, 1);
            var muxed = File.ReadAllBytes(clip);
            var removed = 0;
            for (var p = 4; p < muxed.Length; p += 192)
            {
                if (((muxed[p + 1] & 31) << 8 | muxed[p + 2]) != 0x1100) continue;
                // Leave the PMT, CLPI, MPLS, packet sync, and file size intact.
                muxed[p + 1] = (byte)((muxed[p + 1] & 0xE0) | 31);
                muxed[p + 2] = 255;
                removed++;
            }
            File.WriteAllBytes(clip, muxed);
            bool Rejected(IEnumerable<string> problems) => problems.Any(p => p.Contains("00001.mpls")
                && p.Contains("00001.m2ts") && p.Contains("PID 0x1100 déclaré sans paquet utile"));
            Check("removing all audio packets invalidates the folder", removed > 0 && Rejected(DiscCheck.Folder(disc)));
            iso = F("audit-empty-audio.iso");
            Iso.Build(disc, iso, "AUDIT", dvd: false);
            Check("the same audio gap is rejected inside the ISO alone", Rejected(DiscCheck.Image(iso)));
        });

        Part("Blu-ray DD+ copy and fallback", () =>
        {
            var standalone = Clip("audit-eac3.mkv", "color=s=1280x720:r=24000/1001:d=3", 48000, Fine, ["-c:a", "eac3"]);
            var audio = BluRay.Probe(standalone).Audio.Single();
            Check("48 kHz E-AC-3 alone is not primary Blu-ray DD+", audio is { Codec: "eac3", SampleRate: 48000, DiscEac3: false });
            var copied = Bd("audit-eac3-copy", [new DiscTitle(standalone)], Small());
            string Hash(string input) => Tools.Run(ffmpeg,
                ["-v", "error", "-i", input, "-map", "0:a:0", "-c", "copy", "-f", "hash", "-"]);
            Check("standalone Dolby Digital Plus is copied byte for byte",
                BluRay.Probe(Stream(copied, 1)).Audio is [{ Codec: "eac3", SampleRate: 48000, Channels: > 0 }]
                && Hash(standalone) == Hash(Stream(copied, 1)));
            var clips = new[] { new Clip(0, 1), new Clip(2, 3) };
            var cut = Bd("audit-eac3-passages", [new DiscTitle(standalone, End: 3, Clips: clips)], Small());
            var expected = F("audit-eac3-passages.eac3");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", standalone, "-map", "0:a:0", "-c", "copy",
                "-bsf:a", "noise=drop='gte(pts*tb,1)*lt(pts*tb,2)'", "-f", "eac3", expected]);
            Check("separated passages retain the selected DD+ frames byte for byte", Hash(expected) == Hash(Stream(cut, 1)));
            var late = F("audit-eac3-late.mkv");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", standalone, "-itsoffset", "1.5", "-i", standalone,
                "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", late]);
            var lateDisc = Bd("audit-eac3-late-passages", [new DiscTitle(late, End: 3, Clips: clips)], Small());
            using var timing = System.Text.Json.JsonDocument.Parse(Tools.Run(Tools.Find("ffprobe.exe"),
                ["-v", "error", "-show_entries", "stream=codec_type,start_time", "-of", "json", Stream(lateDisc, 1)]));
            double Start(string type) => double.Parse(timing.RootElement.GetProperty("streams").EnumerateArray()
                .Single(s => s.GetProperty("codec_type").GetString() == type).GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture);
            var delay = Start("audio") - Start("video");
            Check("late Dolby Digital Plus starts after the kept opening, with the removed gap subtracted", Math.Abs(delay - 1) < 0.06, delay.ToString(CultureInfo.InvariantCulture));
            foreach (var (lossless, codec) in new[] { (true, "pcm_bluray"), (false, "ac3") })
            {
                var log = new List<string>();
                var disc = Bd($"audit-eac3-{codec}", [new DiscTitle(standalone)], Small() with { Lossless = lossless, CopyEac3 = false }, log: log.Add);
                var output = BluRay.Probe(Stream(disc, 1)).Audio;
                Check($"E-AC-3 fallback muxes {codec} with a 48 kHz soundtrack",
                    output is [{ SampleRate: 48000 }] && output[0].Codec == codec && log.Any(l => l.Contains("Atmos") && l.Contains("converti")));
            }
        });

        Part("UHD requires HEVC Main 10 even with source quality", () =>
        {
            var source = F("audit-main8.mkv");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "color=s=3840x2160:r=24000/1001:d=1",
                "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-x265-params", "pools=4:log-level=error:level-idc=5.1", source]);
            var log = new List<string>();
            var disc = Bd("audit-main10", [new DiscTitle(source)], new EncodeOptions(Preset: "ultrafast", Gpu: false), log: log.Add, uhd: true);
            Check("Main 8 input is converted and the disc contains Main 10 4:2:0",
                BluRay.Probe(source) is { Profile: "Main", PixFmt: "yuv420p" }
                && BluRay.Probe(Stream(disc, 1)) is { Profile: "Main 10", PixFmt: "yuv420p10le" }
                && log.Any(l => l.Contains("conversion HEVC Main 10")));
        });
    }

    static void DvdAudio()
    {
        DvdPart("DVD audio per title", () =>
        {
            var original = Clip("dvdaudio.mkv", "testsrc2=s=720x576:r=25:d=3", 48000, Fine, Ac3At48);
            var two = F("dvdaudio2.mkv");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", original, "-map", "0", "-map", "0:a", "-c", "copy",
                "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=fre", two]);
            var srt = F("dvdaudio.fra.srt");
            File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,300\nBonjour\n");
            var disc = F("dvdaudio");
            Dvd.Build(new DvdJob([new DiscTitle(two, Audio: [0, 1], Subtitles: [0], External: [new ExternalSubtitle(srt, "fra")]),
                new DiscTitle(original, Bonus: true, TrackLanguage: new Dictionary<string, string> { ["a0"] = "jpn" }),
                new DiscTitle(two, Audio: [1, 0], Bonus: true)], disc, new MenuDesign(), Encode: new EncodeOptions(DvdPal: true)));
            var iso = F("dvdaudio.iso");
            Iso.Build(disc, iso, "DVD_AUDIO", dvd: true);
            string[] Read(int title)
            {
                using var json = JsonDocument.Parse(Tools.Run(ffprobe, ["-v", "error", "-f", "dvdvideo", "-title", title.ToString(CultureInfo.InvariantCulture),
                    "-preindex", "1", "-count_packets", "-select_streams", "a", "-show_entries", "stream=id,nb_read_packets:stream_tags=language", "-of", "json", iso]));
                return [.. json.RootElement.GetProperty("streams").EnumerateArray()
                    .Select(s => $"{s.GetProperty("id")}:{s.GetProperty("tags").GetProperty("language")}:{s.TryGetProperty("nb_read_packets", out var count) && int.Parse(count.GetString()!, CultureInfo.InvariantCulture) > 0}")
                    .Order()];
            }
            Check("DVD: each title exposes only real audio streams with the correct language and physical index",
                Read(1).SequenceEqual(["0x80:eng:True", "0x81:fre:True"])
                && Read(2).SequenceEqual(["0x80:jpn:True"])
                && Read(3).SequenceEqual(["0x80:fre:True", "0x81:eng:True"]));
            using var sub = JsonDocument.Parse(Tools.Run(ffprobe, ["-v", "error", "-f", "dvdvideo", "-title", "2",
                "-count_packets", "-select_streams", "s", "-show_entries", "stream=nb_read_packets", "-of", "json", iso]));
            Check("DVD: an aligned subtitle slot in a bonus without cues carries a transparent SPU",
                sub.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.TryGetProperty("nb_read_packets", out var count)
                    && int.Parse(count.GetString()!, CultureInfo.InvariantCulture) > 0));
        });
    }

    /// <summary>Finding 2: a disc carries 48 kHz, whatever the sound was and whichever path made it.</summary>
    static void Sound()
    {
        Part("AAC 44.1 kHz", () =>
        {
            var s = Look(Stream(Bd("aac441", [new DiscTitle(Clip("aac441.mkv", Size720, 44100, Fine, Aac))]), 1));
            Check("AAC 44.1 kHz is decoded to LPCM 48 kHz by default", s is { Audio: "pcm_bluray", SampleRate: 48000 }, $"{s.Audio} {s.SampleRate}");
        });
        Part("AC-3 44.1 kHz", () =>
        {
            var s = Look(Stream(Bd("ac3441", [new DiscTitle(Clip("ac3441.mkv", Size720, 44100, Fine, ["-c:a", "ac3"]))]), 1));
            Check("AC-3 44.1 kHz is decoded to LPCM 48 kHz by default", s is { Audio: "pcm_bluray", SampleRate: 48000 }, $"{s.Audio} {s.SampleRate}");
        });
        Part("static BD-J menu loop", () =>
        {
            var film = Clip("silent-loop-film.mkv", Size720, 48000, Fine, Ac3At48);
            var disc = Bd("silent-loop", [new DiscTitle(film)], menu: new MenuDesign());
            var loop = Stream(disc, BluRay.LoopPlaylist);
            var playlist = Path.Combine(disc, "BDMV", "PLAYLIST", $"{BluRay.LoopPlaylist:00000}.mpls");
            using var jar = System.IO.Compression.ZipFile.OpenRead(Path.Combine(disc, "BDMV", "JAR", "00000.jar"));
            using var config = new StreamReader(jar.GetEntry("menu.txt")!.Open());
            Check("static BD-J menu plays a 10-second background with opaque menu images",
                config.ReadToEnd().Split('\n').Contains($"loop {BluRay.LoopPlaylist}")
                && jar.GetEntry("bg_root.jpg") != null && Math.Abs(Mpls.Read(playlist).Seconds - 10) < 0.1);
            var audio = Look(loop);
            var raw = F("silent-loop.pcm");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", loop, "-map", "0:a:0", "-f", "s16le", raw]);
            var samples = File.ReadAllBytes(raw);
            Check("the static background has silent stereo AC-3 at 48 kHz",
                audio is { Audio: "ac3", SampleRate: 48000 } &&
                BluRay.Probe(loop).Audio is [{ Channels: 2 }] && samples.Length > 0 && samples.All(b => b == 0));
        });
        Part("moving menu's own sound", () =>
        {
            var menu = TemplateCatalog.Default().Clone();
            menu.Loop = true;
            menu.LoopSeconds = 5;
            var disc = Bd("loop441", [new DiscTitle(Clip("loop441.mkv", Size720, 44100, Fine, Aac))], menu: menu);
            var s = Look(Stream(disc, BluRay.LoopPlaylist));
            Check("the moving menu's clip, sound taken from a 44.1 kHz film, is at 48 kHz", s.SampleRate == 48000, $"{s.Audio} {s.SampleRate}");
            var name = $"{BluRay.LoopPlaylist:00000}.mpls";
            var list = File.ReadAllBytes(Path.Combine(disc, "BDMV", "PLAYLIST", name));
            var playlist = Mpls.Read(list);
            var item = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(list.AsSpan(8)) + 10;
            var itemLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(list.AsSpan(item)) + 2;
            Check("moving menus link six plays of the same clip and jump back halfway through",
                playlist.Items.Count == 6 && playlist.Items.All(i => i.Clip == "00099")
                && Enumerable.Range(1, 5).All(i => (list[item + i * itemLength + 12] & 0x0f) == 5)
                && playlist.Marks.SequenceEqual([new Mpls.Mark(1, 0, playlist.Items[0].In), new Mpls.Mark(1, 3, playlist.Items[3].In)])
                && list.SequenceEqual(File.ReadAllBytes(Path.Combine(disc, "BDMV", "BACKUP", "PLAYLIST", name))));
            using var jar = System.IO.Compression.ZipFile.OpenRead(Path.Combine(disc, "BDMV", "JAR", "00000.jar"));
            using var config = new StreamReader(jar.GetEntry("menu.txt")!.Open());
            Check("the BD-J menu reads the return mark from its configuration without a still",
                config.ReadToEnd().Split('\n').Contains("loop-return-mark 1") && jar.GetEntry("loop-poster.jpg") == null);
        });
    }

    /// <summary>Finding 3: only what a player takes is copied; what it takes still is. The intro goes the same way as a title.</summary>
    static void Video()
    {
        Part("MPEG-2", () =>
        {
            var film = F("mpeg2.mkv");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", Size720, "-f", "lavfi", "-i", "sine=f=440:r=48000:d=2", "-map", "0:v", "-map", "1:a",
                               "-c:v", "mpeg2video", "-b:v", "8M", "-pix_fmt", "yuv420p", .. Ac3At48, film]);
            var codec = Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", Stream(Bd("mpeg2", [new DiscTitle(film)]), 1)]).Trim();
            Check("a Blu-ray-ready MPEG-2 video is copied, not made again as H.264", codec.StartsWith("mpeg2video", StringComparison.Ordinal), codec);
        });
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
            var disc = Bd("uhd", [new DiscTitle(F("uhd.mkv"))], new EncodeOptions(), menu: new MenuDesign(), uhd: true);
            var s = Look(Stream(disc, 1));
            Check("a 4K disc still copies its 10-bit HEVC source", s.PixFmt == "yuv420p10le", s.PixFmt);
            foreach (var name in new[] { "00000.bdjo", "00001.bdjo" })
            {
                var bdjo = File.ReadAllBytes(Path.Combine(disc, "BDMV", "BDJO", name));
                var applicationTable = (int)BinaryPrimitives.ReadUInt32BigEndian(bdjo.AsSpan(20));
                Check($"4K BD-J {name}: version 0300, profile 6 and application version 1.0.0, including the backup",
                    bdjo.AsSpan().StartsWith("BDJO0300"u8)
                    && bdjo.AsSpan(applicationTable + 26, 6).SequenceEqual(new byte[] { 0, 6, 1, 0, 0, 0 })
                    && bdjo.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(disc, "BDMV", "BACKUP", "BDJO", name))));
            }
            var id = File.ReadAllBytes(Path.Combine(disc, "CERTIFICATE", "id.bdmv"));
            Check("4K BD-J disc identifier uses version 0300, including the backup",
                id.AsSpan().StartsWith("BDID0300"u8) && id.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(disc, "CERTIFICATE", "BACKUP", "id.bdmv"))));
            var index = Path.Combine(disc, "BDMV", "index.bdmv");
            var originalIndex = File.ReadAllBytes(index);
            var badIndex = originalIndex.ToArray();
            var table = (int)BinaryPrimitives.ReadUInt32BigEndian(badIndex.AsSpan(8)) + 4;
            BinaryPrimitives.WriteUInt16BigEndian(badIndex.AsSpan(table + 6), 32767);
            "99999"u8.CopyTo(badIndex.AsSpan(table + 18));
            File.WriteAllBytes(index, badIndex);
            var badTargets = DiscCheck.Folder(disc);
            Check("disc check rejects missing First Playback and Top Menu targets",
                badTargets.Any(p => p.Contains("Première lecture, objet HDMV 32767 inexistant"))
                && badTargets.Any(p => p.Contains("Menu principal, descripteur BD-J 99999.bdjo absent")));
            File.WriteAllBytes(index, originalIndex[..8]);
            Check("disc check rejects a Blu-ray index with only the magic and version",
                DiscCheck.Folder(disc).Any(p => p.Contains("index.bdmv") && p.Contains("table des titres hors du fichier")));
            File.WriteAllBytes(index, originalIndex);
            var movieObject = Path.Combine(disc, "BDMV", "MovieObject.bdmv");
            var originalObject = File.ReadAllBytes(movieObject);
            File.WriteAllBytes(movieObject, originalObject[..^1]);
            Check("disc check rejects a truncated HDMV object table",
                DiscCheck.Folder(disc).Any(p => p.Contains("MovieObject.bdmv") && p.Contains("tronquée")));
            File.WriteAllBytes(movieObject, originalObject);
            var descriptor = Path.Combine(disc, "BDMV", "BDJO", "00000.bdjo");
            var valid = File.ReadAllBytes(descriptor);
            File.WriteAllBytes(descriptor, valid[..8]);
            Check("disc check rejects a BD-J descriptor with only the magic and version",
                DiscCheck.Folder(disc).Any(p => p.Contains("00000.bdjo") && p.Contains("en-tête")));
            var address = BinaryPrimitives.ReadUInt32BigEndian(valid.AsSpan(20));
            File.WriteAllBytes(descriptor, valid[..((int)address + 6)]);
            Check("disc check rejects a BD-J application table cut short",
                DiscCheck.Folder(disc).Any(p => p.Contains("00000.bdjo") && p.Contains("table d'applications")));
            File.WriteAllBytes(descriptor, valid);
            var playlist = Path.Combine(disc, "BDMV", "PLAYLIST", "00001.mpls");
            var validPlaylist = File.ReadAllBytes(playlist);
            File.WriteAllBytes(playlist, validPlaylist[..8]);
            Check("disc check rejects a Blu-ray playlist with no tables",
                DiscCheck.Folder(disc).Any(p => p.Contains("00001.mpls") && p.Contains("tronqué")));
            var brokenMark = validPlaylist.ToArray();
            var marks = (int)BinaryPrimitives.ReadUInt32BigEndian(brokenMark.AsSpan(12));
            BinaryPrimitives.WriteUInt16BigEndian(brokenMark.AsSpan(marks + 8), ushort.MaxValue);
            File.WriteAllBytes(playlist, brokenMark);
            Check("disc check rejects a chapter pointing to a nonexistent play item",
                DiscCheck.Folder(disc).Any(p => p.Contains("00001.mpls") && p.Contains("élément de lecture inexistant")));
            var firstItem = (int)BinaryPrimitives.ReadUInt32BigEndian(validPlaylist.AsSpan(8)) + 10;
            var start = BinaryPrimitives.ReadUInt32BigEndian(validPlaylist.AsSpan(firstItem + 14));
            var end = BinaryPrimitives.ReadUInt32BigEndian(validPlaylist.AsSpan(firstItem + 18));
            validPlaylist.CopyTo(brokenMark, 0);
            foreach (var (time, position) in new[] { (unchecked(start - 1), "before"), (unchecked(end + 1), "after") })
            {
                BinaryPrimitives.WriteUInt32BigEndian(brokenMark.AsSpan(marks + 10), time);
                File.WriteAllBytes(playlist, brokenMark);
                Check($"disc check rejects a chapter {position} its play item",
                    DiscCheck.Folder(disc).Any(p => p.Contains("00001.mpls") && p.Contains("repère hors des limites")));
            }
            File.WriteAllBytes(playlist, validPlaylist);
        });
        Part("3D side by side", () =>
        {
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=3840x1080:r=24:d=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                               "-metadata:s:v:0", "stereo_mode=left_right", F("sbs.mkv")]);
            Check("a full side-by-side clip is seen as 3D", BluRay.Probe(F("sbs.mkv")).Stereo == "sbs", BluRay.Probe(F("sbs.mkv")).Stereo);
            var size = Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v", "-show_entries", "stream=width,height", "-of", "csv=p=0", Stream(Bd("sbs", [new DiscTitle(F("sbs.mkv"))], new EncodeOptions(Preset: "ultrafast")), 1)]).Trim().Split('\n')[0].Trim();
            Check("it fills the 1080 picture as half side by side (no bars)", size == "1920,1080", size);
        });
        Part("forced subtitles placed last follow the audio", () =>
        {
            // French full, then French forced and English forced: the menu lists one track, French sound shows stream 2 and English sound stream 3 while off
            var (listed, forced) = BluRay.ForcedTracks(["fre", "eng"], [("fre", false), ("fra", true), ("eng", true)]);
            Check("forced tracks: one listed, one forced track per audio", listed == 1 && forced.SequenceEqual([(1, 2), (2, 3)]), $"{listed} {string.Join(",", forced)}");
            var (all, none) = BluRay.ForcedTracks(["fre"], [("fre", true), ("eng", false)]);
            Check("forced tracks: a forced track before an ordinary one stays listed", all == 2 && none.Count == 0, $"{all} {none.Count}");
        });
        Part("tsMuxeR listing of a 3D playlist", () =>
        {
            // as tsMuxeR lists a 3D disc's playlist: the right eye first, a track it cannot take (no Stream ID), the left eye listed again
            var listing = "Track ID:    4114\r\nStream ID:   V_MPEG4/ISO/MVC\r\nStream lang: \r\nTrack ID:    4113\r\nStream ID:   V_MPEG4/ISO/AVC\r\n"
                          + "Track ID:    4352\r\nStream ID:   A_DTS\r\nStream lang: eng\r\nTrack ID:    4400\r\nStream info: unknown\r\n"
                          + "Track ID:    4608\r\nStream ID:   S_HDMV/PGS\r\nStream info: Presentation Graphic Stream #0   3d-plane: 3\r\nStream lang: fra\r\n"
                          + "Track ID:    4113\r\nStream ID:   V_MPEG4/ISO/AVC\r\nFile #00000 name=a.ssif\r\nFile #00001 name=b.ssif\r\n";
            var t = BluRay.ListedTracks(listing);
            Check("tracks in order, each once", string.Join(' ', t.Select(x => x.Id)) == "4114 4352 4400 4608 4113", string.Join(' ', t.Select(x => x.Id)));
            Check("codec, language and depth read", t.Contains((4352, "A_DTS", "eng", -1)) && t.Contains((4608, "S_HDMV/PGS", "fra", 3)) && t.Contains((4400, "", "", -1)));
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
            List<byte[]> Clip3D(string name, int[] units, bool dependent = false)
            {
                var data = Noise(units.Sum() * 6144);
                const int ext = 0x20;
                var extent = dependent ? 0x44 : 0x38;
                var extentSize = 8 + 4 * units.Length;
                var program = extent + extentSize;
                var clpi = new byte[program + (dependent ? 38 : 0)];
                "HDMV0200"u8.CopyTo(clpi);
                void U32(int at, int value) => BinaryPrimitives.WriteUInt32BigEndian(clpi.AsSpan(at), (uint)value);
                U32(0x18, ext);
                U32(ext, clpi.Length - ext - 4);
                U32(ext + 4, extent - ext);
                clpi[ext + 11] = (byte)(dependent ? 2 : 1);
                U32(ext + 12, 0x00020004); // ExtentStartPoint
                U32(ext + 16, extent - ext);
                U32(ext + 20, extentSize);
                U32(extent, extentSize - 4);
                BinaryPrimitives.WriteUInt16BigEndian(clpi.AsSpan(extent + 6), (ushort)units.Length);
                if (dependent)
                {
                    U32(ext + 24, 0x00020005); // ProgramInfo_SS
                    U32(ext + 28, program - ext);
                    U32(ext + 32, 38);
                    U32(program, 34);
                    clpi[program + 5] = 1; // one program and one MVC stream
                    clpi[program + 12] = 1;
                    clpi[program + 14] = 0x10;
                    clpi[program + 15] = 0x12;
                    clpi[program + 16] = 21;
                    clpi[program + 17] = 0x20;
                }
                var pieces = new List<byte[]>();
                for (int i = 0, at = 0; i < units.Length; at += units[i++])
                {
                    U32(extent + 8 + 4 * i, at * 32); // source packets of 192 bytes
                    pieces.Add(data[(at * 6144)..((at + units[i]) * 6144)]);
                }
                File.WriteAllBytes(Path.Combine(stream, name + ".m2ts"), data);
                File.WriteAllBytes(Path.Combine(clipinf, name + ".clpi"), clpi);
                return pieces;
            }
            // 300 pieces each: more extents than one file entry holds (112), so its list goes on in continuation sectors, as with a real film (~1600 pieces)
            var (left, right) = (Clip3D("00202", [.. Enumerable.Range(0, 300).Select(_ => random.Next(1, 4))]),
                                 Clip3D("00203", [.. Enumerable.Range(0, 300).Select(_ => random.Next(1, 3))], dependent: true));
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
            Check("a Dolby Vision 8.1 clip is seen as single-layer Dolby Vision", info is { Hdr: "Dolby Vision", DualLayer: false, DolbyVisionProfile: "8.1", DolbyVisionHdr10Base: true }, $"{info.Hdr} {info.DualLayer}");
            var m2ts = Stream(Bd("uhddv", [new DiscTitle(dv)], new EncodeOptions(), uhd: true), 1);
            var pids = Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v", "-show_entries", "stream=id", "-of", "csv=p=0", m2ts]).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            // tsMuxeR puts a single-layer Dolby Vision track on the enhancement-layer PID (0x1015), with no base: no UHD player reads that disc
            Check("single-layer Dolby Vision becomes the HDR10 base on PID 0x1011, without its RPU", pids is ["0x1011"] && !FirstFrame(m2ts).Contains("Dolby Vision RPU") && BluRay.Probe(m2ts).Hdr == "HDR10", string.Join(' ', pids));

            foreach (var (profile, transferCode) in new[] { ("5", "2"), ("8.4", "18") })
            {
                var baseFile = F($"audit-dv-{profile}-base.hevc");
                var rpuFile = F($"audit-dv-{profile}.bin");
                var injected = F($"audit-dv-{profile}.hevc");
                Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", F("base.hevc"), "-c:v", "copy",
                    "-bsf:v", $"hevc_metadata=transfer_characteristics={transferCode}", "-f", "hevc", baseFile]);
                Tools.Run(Tools.Find("dovi_tool.exe"), ["generate", "-p", profile, "-j", F("rpu.json"), "-o", rpuFile]);
                Tools.Run(Tools.Find("dovi_tool.exe"), ["inject-rpu", "-i", baseFile, "--rpu-in", rpuFile, "-o", injected]);
                var source = Timed(injected);
                var probed = BluRay.Probe(source);
                Check($"Dolby Vision {profile} has no verified HDR10 base",
                    probed.DolbyVisionProfile == profile && !probed.DolbyVisionHdr10Base, $"{probed.DolbyVisionProfile}");
                foreach (var force in new[] { false, true })
                {
                    var dest = F($"reject-dv-{profile}-{force}");
                    var refused = false;
                    try { BluRay.Build(new BluRayJob([new DiscTitle(source)], dest, Uhd: true, Encode: new EncodeOptions(ForceReencode: force))); }
                    catch (InvalidDataException ex) { refused = ex.Message.Contains($"profil {profile}") && ex.Message.Contains("base HDR10 non établie"); }
                    Check($"Dolby Vision {profile} refuses unsafe {(force ? "conversion" : "copy")} before preparing output", refused && !Directory.Exists(dest));
                }
            }

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
            Check("HDR to SDR keeps the studio's SDR brightness (Mobius: Hable left Bright 15-20 % too dark against Netflix's SDR grade)", BluRay.ToSdr("HDR10").Contains("tonemap=mobius"), BluRay.ToSdr("HDR10"));
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
    /// settings pages for the audio choices actually present, drawn as transparent overlays; without it, nothing of the pop-up is on the disc.
    /// </summary>
    static void Popup()
    {
        Part("pop-up menu", () =>
        {
            var film = Clip("popfilm.mkv", "testsrc2=s=1280x720:r=24000/1001:d=6", 48000, Fine, Ac3At48);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", film, "-map", "0", "-map", "0:a", "-c", "copy", F("pop2audio.mkv")]); // two audio tracks: the settings page exists
            File.WriteAllText(F("forced.fr.srt"), "1\n00:00:01,000 --> 00:00:02,000\nParis\n");
            List<DiscTitle> titles =
            [
                new DiscTitle(F("pop2audio.mkv"), Audio: [0, 1], Subtitles: [0], ChapterAt: [0, 2, 4],
                    External: [new ExternalSubtitle(F("forced.fr.srt"), "fra", Forced: true)],
                    TrackLanguage: new Dictionary<string, string> { ["a0"] = "fre", ["a1"] = "eng" }),
                new DiscTitle(film, Bonus: true, EndAction: "stop")
            ];
            Dictionary<string, byte[]> Jar(string disc)
            {
                using var z = System.IO.Compression.ZipFile.OpenRead(Path.Combine(disc, "BDMV", "JAR", "00000.jar"));
                return z.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); });
            }
            var popupDisc = Bd("popup", titles, menu: new MenuDesign
            {
                Popup = true, MainMenuLabel = "Main", PageFadeMs = 200,
                CustomPages = [new("custom-extra", "Suppléments"), new("custom-more", "Autres", "custom-extra")],
                Directions = [new("custom-extra", "back", "right", "u1")],
                Elements =
                [
                    new() { Kind = "button", Page = "root", Target = "custom-extra", Text = "Suppléments", X = 20, Y = 410, W = 300 },
                    new() { Kind = "button", Page = "custom-extra", Target = "custom-more", Text = "Autres", X = 810, Y = 410, W = 300 }
                ]
            });
            var with = Jar(popupDisc);
            var txt = System.Text.Encoding.UTF8.GetString(with["menu.txt"]).Split('\n');
            bool Has(string start) => txt.Any(l => l.StartsWith(start, StringComparison.Ordinal));
            Check("BD-J : durée du fondu de page enregistrée", Has("page-fade 200"));
            Check("forced external French subtitles follow the French audio", Has("forced-track 1 1 1"));
            var navIso = F("popup.iso");
            Iso.Build(popupDisc, navIso, "CHECK", dvd: false);
            var cleanNavigation = DiscCheck.Image(navIso);
            Check("disc check reads valid Blu-ray chapter and forced subtitle routes from an ISO", cleanNavigation.Count == 0, string.Join(" ; ", cleanNavigation));
            var graph = DiscCheck.Navigation(navIso, audio: 2);
            var folderGraph = DiscCheck.Navigation(popupDisc, audio: 2);
            Check("navigation : même trace de parcours dans le dossier et l’ISO", graph.Problems.Count == 0
                && graph.Trace.SequenceEqual(folderGraph.Trace)
                && graph.Trace.Contains("Top Menu → root")
                && graph.Trace.Any(line => line.Contains("→ touche Pop-up → pop2"))
                && graph.Trace.Contains("custom-extra/u1 → OK → page custom-more")
                && graph.Trace.Contains("custom-more/back → OK → page custom-extra")
                && graph.Trace.Contains("custom-extra/back → droite → custom-extra/u1")
                && graph.Trace.Contains("Liste 00002 → fin configurée → arrêt")
                && graph.Trace.Any(line => line.StartsWith("Liste 00002, clip ") && line.Contains("| audio 1 ")),
                string.Join(" ; ", graph.Problems));
            var badDisc = F("popup-bad");
            foreach (var file in Directory.EnumerateFiles(popupDisc, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(badDisc, Path.GetRelativePath(popupDisc, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(badDisc, "BDMV", "JAR", "00000.jar"), System.IO.Compression.ZipArchiveMode.Update))
            {
                zip.GetEntry("menu.txt")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("menu.txt").Open());
                writer.Write(string.Join('\n', txt).Replace(" chapter 1 2\n", " chapter 1 99\n").Replace(" audio 2\n", " audio 99\n")
                    .Replace("forced-track 1 1 1\n", "forced-track 1 2 1\n") + "\npage unreachable bg_root.jpg\n");
            }
            var badIso = F("popup-bad.iso");
            Iso.Build(badDisc, badIso, "CHECK", dvd: false);
            Check("navigation : une page sans chemin d’entrée est signalée",
                DiscCheck.Navigation(badIso).Problems.Any(problem => problem.Contains("page unreachable inaccessible")));
            foreach (var (place, problems) in new[] { ("folder", DiscCheck.Folder(badDisc)), ("ISO", DiscCheck.Image(badIso)) })
                Check($"disc check finds a bad chapter, audio choice and forced subtitle language in the {place}",
                    problems.Any(p => p.Contains("chapitre inexistant")) && problems.Any(p => p.Contains("piste audio inexistante"))
                    && problems.Any(p => p.Contains("bouton popset/a2, titre 00001 : piste audio inexistante"))
                    && problems.Any(p => p.Contains("langue différente de la piste audio")),
                    string.Join(" ; ", problems));
            Check("pop-up: a bar per title, the film's with resume, chapters, settings and main menu",
                Has("page pop1 bg_pop1.png") && Has("page pop2 bg_pop2.png") && Has("button pop1 resume ") && Has("button pop1 chapters ") && Has("button pop1 settings ") && Has("button pop1 main "),
                string.Join(" | ", txt.Where(l => l.StartsWith("page pop", StringComparison.Ordinal))));
            Check("pop-up: the bonus (one chapter) has no chapters button", !Has("button pop2 chapters "));
            Check("pop-up: the bonus (one audio, no subtitles) has no settings button", !Has("button pop2 settings "));
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
            var bars = MenuBuilder.Popup(new MenuDesign { MainMenuLabel = "Main" }, new MenuContent(1, null, []),
                [(1, 1, ["French", "English"], []), (2, 1, ["English", "German"], []), (3, 1, ["French", "English"], []),
                 (4, 1, ["Japanese"], [("Bonus subtitles", 2)])]);
            Check("pop-up: the main menu button says what the design sets", bars[0].Ops.OfType<Label>().Any(l => l.Value == "Main"));
            string? Setup(int playlist) => bars.Single(p => p.Id == $"pop{playlist}").Buttons.Single(b => b.Id == "settings").Action.Page;
            Check("pop-up: different audio tracks get their own labels; identical choices share a page",
                Setup(1) == Setup(3) && Setup(1) != Setup(2)
                && bars.Single(p => p.Id == Setup(2)).Ops.OfType<Label>().Any(l => l.Value == "German"));
            var bonusSettings = bars.Single(p => p.Id == Setup(4));
            Check("pop-up: the bonus offers its own subtitle stream, skipping the invisible slot",
                bonusSettings.Buttons.Where(b => b.Action.Kind == "sub").Select(b => b.Action.Mark).SequenceEqual([0, 2]));
            var plainDisc = Bd("nopopup", titles, menu: new MenuDesign());
            var without = Jar(plainDisc);
            Check("no pop-up option: no pop-up page, the menu file as before", !without.Keys.Any(k => k.Contains("pop")) && !System.Text.Encoding.UTF8.GetString(without["menu.txt"]).Contains(" pop"));
            // the menu is the Top Menu and each video plays in its own title: a player refuses the Top Menu key inside the top menu title, where the menu used to play the episodes
            var index = File.ReadAllBytes(Path.Combine(plainDisc, "BDMV", "index.bdmv"));
            var table = (int)BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(8)) + 4;
            var objects = File.ReadAllBytes(Path.Combine(plainDisc, "BDMV", "MovieObject.bdmv"));
            int Word(byte[] file, int at) => BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at));
            var count = Word(objects, 48);
            var n = titles.Count;
            string Entry(int at) => $"{index[at] >> 6}{System.Text.Encoding.ASCII.GetString(index, at + 6, 5)}";
            var layout = string.Join(",", new[] { table + 12 }.Concat(Enumerable.Range(0, n + 1).Select(k => table + 26 + 12 * k)).Select(Entry));
            Check("BD-J menu: First Playback is the last HDMV object, the Top Menu the BD-J menu, title N plays video N (BDJO 00001), the last title the menu again",
                index[table] >> 6 == 1 && Word(index, table + 6) == count - 1 && Word(index, table + 24) == n + 1
                && layout == string.Join(",", ["200000", .. Enumerable.Repeat("200001", n), "200000"])
                && File.Exists(Path.Combine(plainDisc, "BDMV", "BDJO", "00001.bdjo")), $"{count} objects, {layout}");
            Check("BD-J menu: First Playback says the disc has just started and where the menu title is, then jumps there",
                objects.AsSpan().IndexOf([.. Nav.Set(BluRay.BdjStartRegister, 1), .. Nav.Set(BluRay.BdjMenuTitleRegister, n + 1), .. Nav.JumpTitle(n + 1)]) > 0);
            // a disc of before 1.0.33: the menu and every video in one BD-J title, no BDJO 00001; the repair puts it on today's titles, its menu kept, signed again
            var old = F("oldtopmenu");
            foreach (var f in Directory.EnumerateFiles(plainDisc, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(old, Path.GetRelativePath(plainDisc, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, true);
            }
            var oldIndex = index.ToArray();
            for (var k = 0; k < 2; k++) { oldIndex[table + 12 * k] = 0x80; oldIndex[table + 12 * k + 4] = 0xC0; "00000"u8.CopyTo(oldIndex.AsSpan(table + 12 * k + 6)); }
            File.WriteAllBytes(Path.Combine(old, "BDMV", "index.bdmv"), oldIndex);
            File.Delete(Path.Combine(old, "BDMV", "BDJO", "00001.bdjo"));
            File.Delete(Path.Combine(old, "BDMV", "BACKUP", "BDJO", "00001.bdjo"));
            var repaired = BluRay.RepairTopMenu(old);
            var oldBdmv = Path.Combine(old, "BDMV");
            var cert = (string file) => File.ReadAllBytes(Path.Combine(old, "CERTIFICATE", file));
            var newIndex = File.ReadAllBytes(Path.Combine(oldBdmv, "index.bdmv"));
            Check("repair-top-menu: an old BD-J disc gets one title per video, its menu kept, the jar signed again; a second run does nothing",
                repaired && newIndex.AsSpan(table + 12).SequenceEqual(index.AsSpan(table + 12)) && Jar(old)["menu.txt"].SequenceEqual(without["menu.txt"])
                && BdjSign.Verify(Path.Combine(oldBdmv, "JAR", "00000.jar"), cert("app.discroot.crt"), cert("id.bdmv")).Count == 0 && !BluRay.RepairTopMenu(old));
        });
    }

    /// <summary>
    /// The native (HDMV) menu: its graphics stream (colours brought down to what a page takes, a big picture cut into segments and read back whole), the
    /// commands a title runs, and a real disc: no BD-J on it, a playlist with a video clip and a graphics clip per page, the index and the movie objects.
    /// How a player shows it is read in libbluray's log (toolslctest.ps1), not here.
    /// </summary>
    static void Hdmv()
    {
        Part("HDMV menu", () =>
        {
            var noise = new Random(1);
            var big = new uint[1200 * 600]; // noise does not compress: more than one segment holds
            for (var i = 0; i < big.Length; i++) big[i] = i % 7 == 0 ? 0 : 0xFF000000u | (uint)noise.Next(1 << 24);
            var (palette, index) = IgStream.Quantize([big], IgStream.MaxColours);
            Check("HDMV: 254 colours at most, one for every pixel", palette.Length == IgStream.MaxColours && big.All(c => c >> 24 == 0 || index[c] >= 1), $"{palette.Length} colours");
            var stream = IgStream.Write(1920, 1080, 1, [new IgButton(1, 10, 20, 1200, 600, big, null, 1, 1, 1, 1, [Nav.JumpTitle(1)])], 1);
            var kinds = new List<byte>();
            var picture = new MemoryStream();
            var framed = true;
            for (var at = 0; at < stream.Length;)
            {
                var length = BinaryPrimitives.ReadUInt16BigEndian(stream.AsSpan(at + 11));
                framed &= stream[at] == 'I' && stream[at + 1] == 'G';
                kinds.Add(stream[at + 10]);
                if (stream[at + 10] == 0x15) picture.Write(stream, at + 17, length - 4); // after the picture's number, version and flags
                at += 13 + length;
            }
            var back = PgsStream.Unpack(picture.ToArray().AsSpan(7), 1200, 600); // after its length and size
            Check("HDMV: composition, palette, the picture in several segments, end mark", framed && kinds[0] == 0x18 && kinds[1] == 0x14 && kinds.Count(k => k == 0x15) > 1 && kinds[^1] == 0x80,
                string.Join(" ", kinds.Select(k => k.ToString("X2"))));
            Check("HDMV: the picture reads back pixel for pixel", Enumerable.Range(0, big.Length).All(i => back[i] == (big[i] >> 24 == 0 ? 0 : index[big[i]])));

            var title = HdmvMenu.TitleObject(1, 2, [(1, 3)], new PlaybackOptions(PlayAll: true));
            int At(byte[] command) => title.FindIndex(c => c.AsSpan().SequenceEqual(command));
            var plays = At(Nav.PlayPlaylist(1));
            Check("HDMV title: from the start or from the chapter asked for, then the next title, else the menu",
                plays > 0 && At(Nav.GoTo(plays)) == plays - 3 && At(Nav.GoTo(plays + 1)) == plays - 1 && At(Nav.JumpTitle(2)) == plays + 3 && At(Nav.JumpTitle(0)) == title.Count - 1);
            Check("HDMV title: subtitles off shows the forced track of the audio's language", At(Nav.SelectStreams(1, 3)) > 0 && At(Nav.SelectStreams(1, 3)) < plays);
            var bonus = HdmvMenu.TitleObject(3, 2, [(1, 2)], new PlaybackOptions(), audioCount: 1, subtitles: [1]);
            bool HasCommand(byte[] command) => bonus.Any(actual => actual.AsSpan().SequenceEqual(command));
            Check("HDMV bonus: defaults to its first audio and its own forced subtitle while retaining the menu choices",
                HasCommand(Nav.Set(17, 1)) && !HasCommand(Nav.Set(12, 1)) && HasCommand(Nav.SelectStreams(1, 2))
                && HasCommand(Nav.IfEqual(13, 1)) && !HasCommand(Nav.IfEqual(13, 2)));

            // two highlights that share 20 lines, and a dot on the first one's left edge
            var zones = new List<(uint[] Argb, int X, int Y, int W, int H)> { (new uint[600 * 100], 100, 200, 600, 100), (new uint[600 * 100], 100, 280, 600, 100), (new uint[24 * 24], 110, 238, 24, 24) };
            HdmvMenu.Separate(zones, 2);
            Check("HDMV: no two buttons of a page overlap (a player hides those that do), the dot kept whole",
                zones[2].W == 24 && zones.All(z => z.Argb.Length == z.W * z.H)
                && zones.SelectMany((a, i) => zones.Skip(i + 1).Select(b => (a, b))).All(p => p.a.X + p.a.W <= p.b.X || p.b.X + p.b.W <= p.a.X || p.a.Y + p.a.H <= p.b.Y || p.b.Y + p.b.H <= p.a.Y),
                string.Join(" ", zones.Select(z => $"{z.X},{z.Y} {z.W}x{z.H}")));

            var film = Clip("hdmvfilm.mkv", "testsrc2=s=1280x720:r=24000/1001:d=6", 48000, Fine, Ac3At48);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", film, "-map", "0", "-map", "0:a", "-c", "copy", F("hdmv2audio.mkv")]); // two audio tracks: the settings page exists
            var log = new List<string>();
            var bdmv = Path.Combine(Bd("hdmv", [new DiscTitle(F("hdmv2audio.mkv"), Audio: [0, 1], ChapterAt: [0, 2, 4]), new DiscTitle(film, Bonus: true)],
                                       menu: new MenuDesign { Popup = true }, log: log.Add, hdmv: true), "BDMV");
            Check("HDMV: no BD-J on the disc, and the log says what the menu leaves out", !new[] { "JAR", "BDJO" }.Any(d => Directory.Exists(Path.Combine(bdmv, d)) && Directory.EnumerateFiles(Path.Combine(bdmv, d)).Any()) && log.Any(l => l.Contains("pop-up")));
            var pages = Directory.GetFiles(Path.Combine(bdmv, "PLAYLIST"), "001??.mpls").Length;
            Check("HDMV: a playlist, a video clip and a graphics clip per page", pages >= 3 && Enumerable.Range(0, pages * 2).All(k => File.Exists(Path.Combine(bdmv, "STREAM", $"{HdmvMenu.FirstClip + k:00000}.m2ts"))), $"{pages} pages");
            Check("HDMV 2D: video and IG clip pairs do not create any SSIF files", Udf.Interleaved(Path.GetDirectoryName(bdmv)!).Count == 0);
            var indexFile = File.ReadAllBytes(Path.Combine(bdmv, "index.bdmv"));
            var table = (int)BinaryPrimitives.ReadUInt32BigEndian(indexFile.AsSpan(8)) + 4;
            int Word(byte[] file, int at) => BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at));
            // 12-byte entries (kind, then the movie object at +6): First Playback, Top Menu, the number of titles, the titles
            Check("HDMV: First Playback runs object 0, the Top Menu key object 4 (the main page), the two titles objects 2 and 3",
                indexFile[table] == 0x40 && Word(indexFile, table + 6) == 0 && Word(indexFile, table + 18) == 4 && Word(indexFile, table + 24) == 2 && Word(indexFile, table + 32) == 2 && Word(indexFile, table + 44) == 3);
            Check("HDMV: five movie objects", Word(File.ReadAllBytes(Path.Combine(bdmv, "MovieObject.bdmv")), 48) == 5);
            var key = HdmvMenu.TopMenuKey();
            Check("HDMV: the Top Menu key shows the main page, its remembered button kept only when it was the main page's",
                key[^2].AsSpan().SequenceEqual(Nav.Set(HdmvMenu.Page, HdmvMenu.FirstPlaylist)) && key[^1].AsSpan().SequenceEqual(Nav.JumpObject(1))
                && key[0].AsSpan().SequenceEqual(Nav.IfEqual(HdmvMenu.Page, HdmvMenu.FirstPlaylist)) && key[1].AsSpan().SequenceEqual(Nav.GoTo(3)) && key[2].AsSpan().SequenceEqual(Nav.Set(HdmvMenu.Selected, 0)));
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
        Part("a picture subtitle on a 25 fps title", () =>
        {
            var writer = new PgsSubtitles.Writer(1280, 720);
            (byte, byte, byte, byte)[] palette = [(0, 0, 0, 0), (255, 255, 255, 255)];
            writer.Add(4.0, 4.5, 600, 600, 64, 32, [.. Enumerable.Repeat((byte)1, 64 * 32)], palette);
            File.WriteAllBytes(F("late.sup"), writer.Finish());
            var clip = Clip("sub25.mkv", "testsrc=s=320x180:r=25:d=6", 48000, ["-pix_fmt", "yuv420p"], Aac);
            var ts = Stream(Bd("sub25", [new DiscTitle(clip, Subtitles: [0], External: [new ExternalSubtitle(F("late.sup"), "fre")])], Small("ultrafast")), 1);
            double First(string kind) => double.Parse(Tools.Run(ffprobe, ["-v", "error", "-select_streams", kind, "-show_entries", "packet=pts_time", "-of", "csv=p=0", ts])
                .Split('\n')[0].Trim().TrimEnd(','), CultureInfo.InvariantCulture);
            var at = First("s:0") - First("v:0");
            Check("its picture shows when the file says (given a frame rate, tsMuxeR re-timed it by 23.976 / 25: 3.84 s for 4)", Math.Abs(at - 4.0) < 0.1, at.ToString(CultureInfo.InvariantCulture));
        });
        Part("sound frames that overlap (scenes joined in a DVD rip)", () =>
        {
            // frames 50 and 51 carry the time of frame 49, and the rest follows two frames early: what a rip writes where two scenes meet
            var clip = Clip("overlap.mkv", "testsrc=s=320x180:r=25:d=4", 48000, ["-pix_fmt", "yuv420p"],
                [.. Ac3At48, "-bsf:a", "setts=ts='if(between(N,50,51),PREV_OUTPTS,if(gt(N,51),PTS-3072,PTS))'"]);
            int Frames(string file) => Tools.Run(ffprobe, ["-v", "error", "-select_streams", "a:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", file]).Split('\n').Count(l => l.Trim().Length > 0);
            var (before, after) = (Frames(clip), Frames(Stream(Bd("overlap", [new DiscTitle(clip)], Small("ultrafast")), 1)));
            Check("the two frames that play over the one before are left out (kept, the sound came 64 ms late from there on)", before - after == 2, $"{before} -> {after}");
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
