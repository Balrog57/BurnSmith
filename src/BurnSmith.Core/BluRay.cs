using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BurnSmith.Core.Menu;

namespace BurnSmith.Core;

/// <summary>
/// Blu-ray / UHD Blu-ray job: one playlist per entry of <see cref="Titles"/>, in order.
/// <see cref="Menu"/> null = the disc starts straight on the movie (a single title only).
/// </summary>
public sealed record BluRayJob(IReadOnlyList<DiscTitle> Titles, string OutDir, MenuDesign? Menu = null, bool Uhd = false,
                               string Label = "BURNSMITH", int ChapterMinutes = 5);

/// <summary><see cref="AudioCodec"/> / <see cref="AudioChannels"/> describe the first audio stream (empty when there is none).</summary>
public sealed record SourceInfo(string VideoCodec, int Width, int Height, double Fps, string AudioCodec, int AudioChannels, double Duration,
                                IReadOnlyList<AudioTrack> Audio, IReadOnlyList<SubtitleTrack> Subtitles, string Hdr = "SDR");

public static class BluRay
{
    /// <summary>Playlist / clip number of the first title; title N is playlist N.</summary>
    public const int MoviePlaylist = 1;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static SourceInfo Probe(string file)
    {
        var json = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-of", "json", "-show_streams", "-show_format", file]);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
        List<JsonElement> Of(string type) => streams.Where(s => s.GetProperty("codec_type").GetString() == type).ToList();
        static string Tag(JsonElement s, string name) => s.TryGetProperty("tags", out var t) && t.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        static bool Flag(JsonElement s, string name) => s.TryGetProperty("disposition", out var d) && d.TryGetProperty(name, out var v) && v.GetInt32() == 1;

        // cover art is a video stream too
        var v = Of("video").FirstOrDefault(s => !Flag(s, "attached_pic"));
        if (v.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("no video stream in " + file);
        var audio = Of("audio").Select((s, i) => new AudioTrack(i, s.GetProperty("codec_name").GetString()!,
            s.TryGetProperty("channels", out var c) ? c.GetInt32() : 0, Tag(s, "language"), Tag(s, "title"))).ToList();
        var subs = Of("subtitle").Select((s, i) => new SubtitleTrack(i, s.GetProperty("codec_name").GetString() ?? "", Tag(s, "language"), Tag(s, "title"), Flag(s, "forced"))).ToList();
        if (!doc.RootElement.GetProperty("format").TryGetProperty("duration", out var duration)) // a picture has a video stream but no length
            throw new InvalidDataException("no duration: not a video file");
        var r = v.GetProperty("r_frame_rate").GetString()!.Split('/');
        var dolby = v.TryGetProperty("side_data_list", out var side) && side.EnumerateArray().Any(d => d.TryGetProperty("side_data_type", out var t) && (t.GetString() ?? "").Contains("DOVI"));
        var hdr = dolby ? "Dolby Vision" : (v.TryGetProperty("color_transfer", out var ct) ? ct.GetString() : null) switch { "smpte2084" => "HDR10", "arib-std-b67" => "HLG", _ => "SDR" };
        return new SourceInfo(
            v.GetProperty("codec_name").GetString()!, v.GetProperty("width").GetInt32(), v.GetProperty("height").GetInt32(),
            double.Parse(r[0], Inv) / double.Parse(r[1], Inv),
            audio.FirstOrDefault()?.Codec ?? "", audio.FirstOrDefault()?.Channels ?? 0,
            double.Parse(duration.GetString()!, Inv), audio, subs, hdr);
    }

    /// <summary>Chapter start times (seconds): one every <paramref name="minutes"/>, never a chapter shorter than 1 minute.</summary>
    public static List<double> Chapters(double duration, int minutes)
    {
        var step = Math.Max(1, minutes) * 60.0;
        var list = new List<double>();
        for (var t = 0.0; t < duration - 60 || t == 0; t += step) list.Add(t);
        return list;
    }

    /// <summary>What one muxed title gives the menu: its preview source and the names of the tracks that really went on the disc.</summary>
    sealed record Muxed(string Prepared, List<string> Audio, List<string> Subtitles, List<double> ChapterTimes);

    public static void Build(BluRayJob job, Action<string>? log = null)
    {
        log ??= _ => { };
        if (job.Titles.Count == 0) throw new ArgumentException("no video to put on the disc");
        if (job.Titles.Count > 1 && job.Menu == null) throw new InvalidOperationException("several videos need a menu to choose from");
        var sources = job.Titles.Select(t => Probe(t.Video)).ToList();
        var work = job.OutDir + "_work";
        Tools.PrepareOutput(job.OutDir);
        Directory.CreateDirectory(work);

        var muxed = new List<Muxed>();
        for (var i = 0; i < job.Titles.Count; i++)
        {
            var playlist = MoviePlaylist + i;
            // later titles are muxed on their own (playlist/clip numbers offset), then merged into the first disc folder
            var dest = i == 0 ? job.OutDir : Path.Combine(work, "mux" + playlist);
            if (i > 0 && Directory.Exists(dest)) Directory.Delete(dest, true);
            muxed.Add(MuxTitle(job, job.Titles[i], sources[i], playlist, dest, work, log));
            if (i > 0) Merge(dest, job.OutDir);
        }

        // disc title and thumbnails shown by players and Windows (BDMV/META/DL: bdmt_*.xml, thumbnail_WxH.jpg, the two sizes of commercial discs)
        var metaDir = Path.Combine(job.OutDir, "BDMV", "META", "DL");
        Directory.CreateDirectory(metaDir);
        var thumbnails = "";
        foreach (var (tw, th) in new[] { (416, 240), (640, 360) })
        {
            var jpg = $"thumbnail_{tw}x{th}.jpg";
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-ss", (sources[0].Duration * 0.1).ToString("0.###", Inv), "-i", muxed[0].Prepared, "-frames:v", "1", "-q:v", "3",
                "-vf", $"scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th}", Path.Combine(metaDir, jpg)]);
            if (File.Exists(Path.Combine(metaDir, jpg))) thumbnails += $"<di:thumbnail href=\"{jpg}\" />";
        }
        File.WriteAllText(Path.Combine(metaDir, "bdmt_eng.xml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><disclib xmlns=\"urn:BDA:bdmv;disclib\"><di:discinfo xmlns:di=\"urn:BDA:bdmv;discinfo\"><di:title><di:name>"
            + System.Security.SecurityElement.Escape(job.Menu?.Title ?? job.Label) + "</di:name></di:title>"
            + (thumbnails.Length > 0 ? $"<di:description>{thumbnails}</di:description>" : "") + "</di:discinfo></disclib>"); // shortcut: one language; add per-language files when the UI has a language setting

        if (job.Menu != null)
        {
            log("menu: render");
            var thumbs = Path.Combine(work, "thumbs");
            Directory.CreateDirectory(thumbs);
            string? Thumb(string name, int title, double t, int w, int h)
            {
                var file = Path.Combine(thumbs, name + ".jpg");
                Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-ss", Math.Max(0, Math.Min(t, sources[title].Duration - 0.5)).ToString("0.###", Inv),
                    "-i", muxed[title].Prepared, "-frames:v", "1", "-q:v", "2", "-vf", $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}", file]);
                return File.Exists(file) ? file : null;
            }
            var series = job.Titles.Count > 1;
            var content = new MenuContent(MoviePlaylist, Thumb("preview", 0, sources[0].Duration * 0.1, 800, 450),
                series ? [] : muxed[0].ChapterTimes.Select((t, i) => Thumb($"c{i + 1}", 0, t + 3, 480, 270)).ToList(),
                series ? job.Titles.Select((t, i) => new TitleEntry(t.DisplayName, MoviePlaylist + i, Thumb($"t{i + 1}", i, sources[i].Duration * 0.1, 480, 270))).ToList() : null,
                muxed[0].Audio, muxed[0].Subtitles);
            var menuDir = Path.Combine(work, "menu");
            MenuFiles.WriteBdj(job.Menu, MenuBuilder.Build(job.Menu, content), menuDir);
            AddMenu(job, menuDir, log);
        }
        Tools.TryDelete(work);
        log("done: " + job.OutDir);
    }

    static Muxed MuxTitle(BluRayJob job, DiscTitle title, SourceInfo src, int playlist, string dest, string work, Action<string> log)
    {
        var audio = title.KeptAudio(src);
        // MPEG-TS, not Matroska: from an mkv tsMuxeR finds no SPS/PPS before the keyframes and writes ONE entry point in the clip info,
        // so a player cannot seek or jump to a chapter; from a TS it writes one per keyframe
        var prepared = Path.Combine(work, $"prepared{playlist}.ts");

        // subtitles the disc can carry: PGS is copied, text becomes SRT (tsMuxeR renders it); other image formats would need OCR
        var subs = new List<(SubtitleTrack Track, string File)>();
        foreach (var s in title.KeptSubtitles(src))
        {
            if (!s.Supported) { log($"subtitle '{s.Name}' skipped: {s.Codec} is an image format that needs OCR"); continue; }
            subs.Add((s, Path.Combine(work, $"sub{playlist}_{subs.Count + 1}.{(s.IsPgs ? "sup" : "srt")}")));
        }
        log($"prepare {playlist}: {src.VideoCodec} {src.Width}x{src.Height} {src.Fps:0.###}fps, audio {(audio.Count == 0 ? "none" : string.Join('+', audio.Select(a => a.Codec)))}, subtitles {subs.Count}");
        Prepare(job, title, src, audio, subs, prepared, log);

        var fps = Math.Round(Math.Abs(src.Fps - 25) < 0.01 ? 25 : Math.Abs(src.Fps - 24) < 0.01 ? 24 : 23.976, 3).ToString(Inv);
        var chapterTimes = Chapters(src.Duration, job.ChapterMinutes);
        var chapters = string.Join(';', chapterTimes.Select(t => TimeSpan.FromSeconds(t).ToString(@"hh\:mm\:ss\.fff", Inv)));
        var (w, h) = job.Uhd ? (3840, 2160) : (1920, 1080);
        var lines = new List<string>
        {
            $"MUXOPT {(job.Uhd ? "--blu-ray-v3" : "--blu-ray")} --label={job.Label} --vbr --custom-chapters={chapters} --mplsOffset={playlist} --m2tsOffset={playlist}",
            $"{(job.Uhd ? "V_MPEGH/ISO/HEVC" : "V_MPEG4/ISO/AVC")}, \"{prepared}\", fps={fps}, track=256",
        };
        for (var j = 0; j < audio.Count; j++)
            lines.Add($"{AudioTag(BdAudio(audio[j].Codec))}, \"{prepared}\", track={257 + j}, lang={Languages.Iso2(audio[j].Lang)}");
        foreach (var (s, file) in subs)
            lines.Add(s.IsPgs
                ? $"S_HDMV/PGS, \"{file}\", fps={fps}, lang={Languages.Iso2(s.Lang)}"
                : $"S_TEXT/UTF8, \"{file}\", font-name=\"Arial\", font-size=65, font-color=0xffffffff, bottom-offset=24, font-border=5, text-align=center, video-width={w}, video-height={h}, fps={fps}, lang={Languages.Iso2(s.Lang)}");
        var meta = Path.Combine(work, $"disc{playlist}.meta");
        File.WriteAllLines(meta, lines);
        log($"mux {playlist}: tsMuxeR");
        Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, dest], log);
        return new Muxed(prepared, audio.Select(a => a.Name).ToList(), subs.Select(s => s.Track.Name).ToList(), chapterTimes);
    }

    /// <summary>Moves the playlist, clip info and stream files of a title muxed apart into the disc folder.</summary>
    static void Merge(string from, string into)
    {
        foreach (var sub in new[] { @"BDMV\PLAYLIST", @"BDMV\CLIPINF", @"BDMV\STREAM", @"BDMV\BACKUP\PLAYLIST", @"BDMV\BACKUP\CLIPINF" })
        {
            var src = Path.Combine(from, sub);
            if (!Directory.Exists(src)) continue;
            Directory.CreateDirectory(Path.Combine(into, sub));
            foreach (var f in Directory.EnumerateFiles(src)) File.Move(f, Path.Combine(into, sub, Path.GetFileName(f)), true);
        }
    }

    static string AudioTag(string codec) => codec switch
    {
        "dts" => "A_DTS", "truehd" => "A_MLP", _ => "A_AC3",
    };

    /// <summary>The codec a source audio ends up as on the disc: copied when a Blu-ray carries it, AC-3 otherwise.</summary>
    static string BdAudio(string codec) => codec is "ac3" or "dts" or "truehd" ? codec : "ac3";

    /// <summary>
    /// Remuxes to prepared.ts (ffmpeg numbers the streams from PID 256: video = 256, kept audio = 257..), re-encoding only what a Blu-ray cannot carry,
    /// and writes the subtitle files in the same pass over the source.
    /// </summary>
    static void Prepare(BluRayJob job, DiscTitle title, SourceInfo s, IReadOnlyList<AudioTrack> audio, List<(SubtitleTrack Track, string File)> subs, string output, Action<string> log)
    {
        var fpsOk = Math.Abs(s.Fps - 23.976) < 0.01 || Math.Abs(s.Fps - 24) < 0.01 || Math.Abs(s.Fps - 25) < 0.01;
        var (w, h) = job.Uhd ? (3840, 2160) : (1920, 1080);
        var videoOk = s.VideoCodec == (job.Uhd ? "hevc" : "h264") && s.Width == w && s.Height == h && fpsOk;
        if (job.Uhd && !videoOk)
            // shortcut: UHD sources must already be 2160p HEVC; HDR-aware re-encoding is not implemented yet
            throw new NotSupportedException("UHD needs a 3840x2160 HEVC source at 23.976/24/25 fps");

        var args = new List<string> { "-y", "-v", "warning", "-i", title.Video, "-map", "0:v:0" };
        foreach (var a in audio) args.AddRange(["-map", $"0:a:{a.Index}"]);
        if (videoOk) args.AddRange(["-c:v", "copy"]);
        else args.AddRange(["-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1",
                            "-r", Math.Abs(s.Fps - 25) < 0.5 ? "25" : "24000/1001",
                            "-c:v", "libx264", "-preset", "medium", "-crf", "17", "-pix_fmt", "yuv420p",
                            "-x264-params", "bluray-compat=1:level=41:vbv-maxrate=30000:vbv-bufsize=30000:keyint=24:slices=4:bframes=3:b-pyramid=strict:open-gop=0"]);
        for (var j = 0; j < audio.Count; j++)
        {
            if (BdAudio(audio[j].Codec) == audio[j].Codec) args.AddRange([$"-c:a:{j}", "copy"]);
            else args.AddRange([$"-c:a:{j}", "ac3", $"-b:a:{j}", audio[j].Channels > 2 ? "640k" : "448k"]);
        }
        args.AddRange(["-sn", "-dn", output]);
        foreach (var (t, file) in subs)
            args.AddRange(["-map", $"0:s:{t.Index}", "-c:s", t.IsPgs ? "copy" : "srt", "-f", t.IsPgs ? "sup" : "srt", file]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
    }

    /// <summary>Adds the BD-J menu on top of the tsMuxeR disc: jar, BDJO, and an index.bdmv that starts the menu (BACKUP copies included).</summary>
    static void AddMenu(BluRayJob job, string menuDir, Action<string> log)
    {
        log("menu: BD-J");
        var bdmv = Path.Combine(job.OutDir, "BDMV");
        foreach (var d in new[] { "JAR", "BDJO", "AUXDATA", @"BACKUP\BDJO" }) Directory.CreateDirectory(Path.Combine(bdmv, d));

        // fresh archive: ZipArchiveMode.Update corrupted entries of the prebuilt jar
        var jar = Path.Combine(bdmv, "JAR", "00000.jar");
        using (var baseJar = ZipFile.OpenRead(Assets.Find(@"bdj\menu.jar")))
        using (var z = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            foreach (var e in baseJar.Entries.Where(e => e.Length > 0))
            {
                using var from = e.Open();
                using var to = z.CreateEntry(e.FullName, CompressionLevel.Optimal).Open();
                from.CopyTo(to);
            }
            foreach (var f in Directory.EnumerateFiles(menuDir))
                z.CreateEntryFromFile(f, Path.GetFileName(f));
        }

        var bdjo = Bdmv.Bdjo("org.burnsmith.menu.Menu", job.Uhd ? Bdmv.HaviUhd1080 : Bdmv.HaviHd1080);
        File.WriteAllBytes(Path.Combine(bdmv, "BDJO", "00000.bdjo"), bdjo);
        File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", "BDJO", "00000.bdjo"), bdjo);
        foreach (var index in new[] { Path.Combine(bdmv, "index.bdmv"), Path.Combine(bdmv, "BACKUP", "index.bdmv") })
            if (File.Exists(index)) File.WriteAllBytes(index, Bdmv.PatchIndex(File.ReadAllBytes(index)));
    }
}

/// <summary>Finds files shipped in the assets folder (next to the app or any parent), else the copy embedded in the assembly (portable exe).</summary>
public static class Assets
{
    public static string Find(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "assets", relative);
            if (File.Exists(p)) return p;
        }
        var name = "assets/" + relative.Replace('\\', '/');
        using var s = typeof(Assets).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException("asset not found: " + relative);
        var file = Path.Combine(Path.GetTempPath(), "BurnSmith", name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var o = File.Create(file)) s.CopyTo(o);
        return file;
    }
}
