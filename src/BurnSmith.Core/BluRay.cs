using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BurnSmith.Core.Menu;

namespace BurnSmith.Core;

/// <summary>Blu-ray / UHD Blu-ray job. <see cref="Menu"/> null = the disc starts straight on the movie (no menu).</summary>
public sealed record BluRayJob(string Video, string OutDir, MenuDesign? Menu = null, bool Uhd = false,
                               string Label = "BURNSMITH", int ChapterMinutes = 5);

public sealed record SourceInfo(string VideoCodec, int Width, int Height, double Fps, string AudioCodec, int AudioChannels, double Duration);

public static class BluRay
{
    /// <summary>Playlist / clip number given to the movie; menu actions "play 1" refer to it.</summary>
    public const int MoviePlaylist = 1;

    public static SourceInfo Probe(string file)
    {
        var json = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-of", "json", "-show_streams", "-show_format", file]);
        using var doc = JsonDocument.Parse(json);
        JsonElement? Stream(string type) => doc.RootElement.GetProperty("streams").EnumerateArray()
            .Where(s => s.GetProperty("codec_type").GetString() == type).Select(s => (JsonElement?)s).FirstOrDefault();
        var v = Stream("video") ?? throw new InvalidDataException("no video stream in " + file);
        var a = Stream("audio");
        var r = v.GetProperty("r_frame_rate").GetString()!.Split('/');
        return new SourceInfo(
            v.GetProperty("codec_name").GetString()!, v.GetProperty("width").GetInt32(), v.GetProperty("height").GetInt32(),
            double.Parse(r[0], CultureInfo.InvariantCulture) / double.Parse(r[1], CultureInfo.InvariantCulture),
            a?.GetProperty("codec_name").GetString() ?? "", a?.GetProperty("channels").GetInt32() ?? 0,
            double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture));
    }

    /// <summary>Chapter start times (seconds): one every <paramref name="minutes"/>, never a chapter shorter than 1 minute.</summary>
    public static List<double> Chapters(double duration, int minutes)
    {
        var step = Math.Max(1, minutes) * 60.0;
        var list = new List<double>();
        for (var t = 0.0; t < duration - 60 || t == 0; t += step) list.Add(t);
        return list;
    }

    public static void Build(BluRayJob job, Action<string>? log = null)
    {
        log ??= _ => { };
        var src = Probe(job.Video);
        var work = Path.Combine(job.OutDir + "_work");
        Tools.PrepareOutput(job.OutDir);
        Directory.CreateDirectory(work);

        var prepared = Path.Combine(work, "prepared.mkv");
        log($"prepare: {src.VideoCodec} {src.Width}x{src.Height} {src.Fps:0.###}fps, audio {src.AudioCodec}");
        Prepare(job, src, prepared, log);

        var fps = Math.Round(Math.Abs(src.Fps - 25) < 0.01 ? 25 : Math.Abs(src.Fps - 24) < 0.01 ? 24 : 23.976, 3);
        var chapterTimes = Chapters(src.Duration, job.ChapterMinutes);
        var chapters = string.Join(';', chapterTimes
            .Select(t => TimeSpan.FromSeconds(t).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)));
        var meta = Path.Combine(work, "disc.meta");
        File.WriteAllLines(meta,
        [
            $"MUXOPT {(job.Uhd ? "--blu-ray-v3" : "--blu-ray")} --label={job.Label} --vbr --custom-chapters={chapters} --mplsOffset={MoviePlaylist} --m2tsOffset={MoviePlaylist}",
            $"{(job.Uhd ? "V_MPEGH/ISO/HEVC" : "V_MPEG4/ISO/AVC")}, \"{prepared}\", fps={fps.ToString(CultureInfo.InvariantCulture)}, track=1",
            $"{AudioTag(src.AudioCodec)}, \"{prepared}\", track=2, lang=eng",
        ]);
        log("mux: tsMuxeR");
        Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, job.OutDir], log);

        // disc title shown by players and Windows (BDMV/META/DL/bdmt_*.xml); only English, see shortcut
        var metaDir = Path.Combine(job.OutDir, "BDMV", "META", "DL");
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, "bdmt_eng.xml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><disclib xmlns=\"urn:BDA:bdmv;disclib\"><di:discinfo xmlns:di=\"urn:BDA:bdmv;discinfo\"><di:title><di:name>"
            + System.Security.SecurityElement.Escape(job.Menu?.Title ?? job.Label) + "</di:name></di:title></di:discinfo></disclib>"); // shortcut: one language, no thumbnail; add per-language files when the UI has a language setting

        if (job.Menu != null)
        {
            log("menu: render");
            var thumbs = Path.Combine(work, "thumbs");
            Directory.CreateDirectory(thumbs);
            string? Thumb(string name, double t, int w, int h)
            {
                var file = Path.Combine(thumbs, name + ".jpg");
                Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-ss", Math.Max(0, Math.Min(t, src.Duration - 0.5)).ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", prepared, "-frames:v", "1", "-q:v", "2", "-vf", $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}", file]);
                return File.Exists(file) ? file : null;
            }
            var content = new MenuContent(MoviePlaylist, Thumb("preview", src.Duration * 0.1, 800, 450),
                chapterTimes.Select((t, i) => Thumb($"c{i + 1}", t + 3, 480, 270)).ToList());
            var menuDir = Path.Combine(work, "menu");
            MenuFiles.WriteBdj(job.Menu, MenuBuilder.Build(job.Menu, content), menuDir);
            AddMenu(job, menuDir, log);
        }
        log("done: " + job.OutDir);
    }

    static string AudioTag(string codec) => codec switch
    {
        "dts" => "A_DTS", "truehd" => "A_MLP", _ => "A_AC3",
    };

    /// <summary>Remuxes to prepared.mkv (video = track 1, first audio = track 2), re-encoding only what a Blu-ray cannot carry.</summary>
    static void Prepare(BluRayJob job, SourceInfo s, string output, Action<string> log)
    {
        var fpsOk = Math.Abs(s.Fps - 23.976) < 0.01 || Math.Abs(s.Fps - 24) < 0.01 || Math.Abs(s.Fps - 25) < 0.01;
        var (w, h) = job.Uhd ? (3840, 2160) : (1920, 1080);
        var videoOk = s.VideoCodec == (job.Uhd ? "hevc" : "h264") && s.Width == w && s.Height == h && fpsOk;
        var audioOk = s.AudioCodec is "ac3" or "dts" or "truehd";
        if (job.Uhd && !videoOk)
            // shortcut: UHD sources must already be 2160p HEVC; HDR-aware re-encoding is not implemented yet
            throw new NotSupportedException("UHD needs a 3840x2160 HEVC source at 23.976/24/25 fps");

        var args = new List<string> { "-y", "-v", "warning", "-i", job.Video, "-map", "0:v:0", "-map", "0:a:0?" };
        if (videoOk) args.AddRange(["-c:v", "copy"]);
        else args.AddRange(["-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1",
                            "-r", Math.Abs(s.Fps - 25) < 0.5 ? "25" : "24000/1001",
                            "-c:v", "libx264", "-preset", "medium", "-crf", "17", "-pix_fmt", "yuv420p",
                            "-x264-params", "bluray-compat=1:level=41:vbv-maxrate=30000:vbv-bufsize=30000:keyint=24:slices=4:bframes=3:b-pyramid=strict:open-gop=0"]);
        if (audioOk) args.AddRange(["-c:a", "copy"]);
        else args.AddRange(["-c:a", "ac3", "-b:a", s.AudioChannels > 2 ? "640k" : "448k"]);
        args.Add(output);
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
