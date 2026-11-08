using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BurnSmith.Core.Menu;

namespace BurnSmith.Core;

/// <summary>
/// Blu-ray / UHD Blu-ray job: one playlist per entry of <see cref="Titles"/>, in order, the bonus ones (<see cref="DiscTitle.Bonus"/>) after the others.
/// <see cref="Menu"/> null = the disc starts straight on the movie (a single title only).
/// </summary>
public sealed record BluRayJob(IReadOnlyList<DiscTitle> Titles, string OutDir, MenuDesign? Menu = null, bool Uhd = false,
                               string Label = "BURNSMITH", int ChapterMinutes = 5, EncodeOptions? Encode = null,
                               double CapacityGB = 0, PlaybackOptions? Playback = null);

/// <summary>
/// How the picture is made (the video mode and quality). Defaults: a Blu-ray source that already fits is copied, otherwise x264 at constant
/// quality <see cref="Crf"/> (lower = better, bigger); <see cref="DvdKbps"/> &gt; 0 sets the DVD video bit rate by hand (0 = fills the disc).
/// Blu-ray: <see cref="BdKbps"/> &gt; 0 = x264 at that video bit rate; <see cref="FitDisc"/> = "Adapter au disque" (the source is kept while it fits
/// <see cref="BluRayJob.CapacityGB"/>, else re-encoded at the bit rate that fills it); <see cref="Hd720"/> = a 1280x720 disc.
/// DVD: <see cref="DvdPal"/> forces PAL (true) or NTSC (false), null follows the first video; <see cref="DvdWide"/> false = 4:3 titles (the menu stays 16:9).
/// </summary>
public sealed record EncodeOptions(bool ForceReencode = false, int Crf = 17, string Preset = "medium", int DvdKbps = 0,
                                   int BdKbps = 0, bool FitDisc = false, bool Hd720 = false, bool? DvdPal = null, bool DvdWide = true);

/// <summary>
/// The play mode (Blu-ray menu). <see cref="StartAtFirstTitle"/>: the disc starts on the first title instead of the menu.
/// <see cref="PlayAll"/>: the titles follow each other. <see cref="Rewind"/>: when everything has played, go back to the start (the menu, or the
/// first title) instead of stopping. Without <see cref="PlayAll"/> the disc returns to the menu after the title that was picked.
/// </summary>
public sealed record PlaybackOptions(bool StartAtFirstTitle = false, bool PlayAll = false, bool Rewind = true)
{
    /// <summary>The 3 radio buttons: 0 = menu then all titles, 1 = first title then all, 2 = back to the menu after the picked title.</summary>
    public int Mode => PlayAll ? (StartAtFirstTitle ? 1 : 0) : 2;
    public static PlaybackOptions FromMode(int mode, bool rewind) => new(mode == 1, mode != 2, mode == 2 || rewind);
}

/// <summary>The output: the disc size and the picture size of a Blu-ray.</summary>
public sealed record BdProfile(string Name, double Gb, double UsableGb, bool Hd720)
{
    // usable = nominal size minus file system and menu overhead (BD9 / BD5: Blu-ray structure on DVD-sized media)
    public static readonly BdProfile[] All =
    [
        new("BD50 1080p/i", 50, 46.5, false), new("BD50 720p", 50, 46.5, true),
        new("BD25 1080p/i", 25, 23.3, false), new("BD25 720p", 25, 23.3, true),
        new("BD9 1080p/i", 8.5, 7.6, false), new("BD9 720p", 8.5, 7.6, true),
        new("BD5 1080p/i", 4.7, 4.2, false), new("BD5 720p", 4.7, 4.2, true),
    ];

    public static BdProfile Find(string name) => All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
        || p.Name.Replace(" 1080p/i", "").Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("unknown output profile: " + name);
}

/// <summary>
/// <see cref="AudioCodec"/> / <see cref="AudioChannels"/> describe the first audio stream (empty when there is none). <see cref="Duration"/> is what goes on the disc:
/// after <see cref="DiscTitle.Cut"/> the part kept, with the whole file's length in <see cref="FileDuration"/> (0 = not cut).
/// </summary>
public sealed record SourceInfo(string VideoCodec, int Width, int Height, double Fps, string AudioCodec, int AudioChannels, double Duration,
                                IReadOnlyList<AudioTrack> Audio, IReadOnlyList<SubtitleTrack> Subtitles, string Hdr = "SDR", double FileDuration = 0,
                                string Encoding = "", string PixFmt = "", string Profile = "", int Level = 0) // Encoding: the video's profile / level / reorder depth / reference frames, which a stream-copy join must not mix
{
    /// <summary>What a Blu-ray player takes from H.264 as it is: 8-bit 4:2:0 at level 4.1 at most (ffprobe says -99 for a level it cannot read).</summary>
    public bool PlayerReadyH264 => VideoCodec == "h264" && PixFmt == "yuv420p" && Level is > 0 and <= 41 && !Profile.Contains("10");
}

public static class BluRay
{
    /// <summary>Playlist / clip number of the first title; title N is playlist N.</summary>
    public const int MoviePlaylist = 1;

    /// <summary>Playlist / clip number of the moving menu's video.</summary>
    public const int LoopPlaylist = 99;

    /// <summary>Playlist / clip number of the video that plays when the disc starts (<see cref="DiscTitle.Intro"/>).</summary>
    public const int IntroPlaylist = 98;

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
            s.TryGetProperty("channels", out var c) ? c.GetInt32() : 0, Tag(s, "language"), Tag(s, "title"),
            s.TryGetProperty("sample_rate", out var rate) && int.TryParse(rate.GetString(), out var hz) ? hz : 0)).ToList();
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
            double.Parse(duration.GetString()!, Inv), audio, subs, hdr,
            Encoding: string.Join('/', new[] { "profile", "level", "has_b_frames", "refs" }.Select(n => v.TryGetProperty(n, out var p) ? p.ToString() : "")),
            PixFmt: v.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() ?? "" : "",
            Profile: v.TryGetProperty("profile", out var pr) ? pr.GetString() ?? "" : "",
            Level: v.TryGetProperty("level", out var lv) && lv.TryGetInt32(out var level) ? level : 0);
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
    sealed record Muxed(string Prepared, List<string> Audio, List<string> Subtitles, List<double> ChapterTimes, int DefaultSubtitle);

    public static void Build(BluRayJob job, Action<string>? log, CancellationToken cancel) => Tools.WithCancel(cancel, () => Build(job, log));

    public static void Build(BluRayJob job, Action<string>? log = null)
    {
        log ??= _ => { };
        Tools.KeepInputs(DiscTitle.Inputs(job.Titles, job.Menu), job.OutDir, job.OutDir + "_work");
        var intro = job.Titles.FirstOrDefault(t => t.Intro);
        if (job.Titles.Count(t => t.Intro) > 1) throw new ArgumentException("une seule vidéo de début est possible");
        job = job with { Titles = [.. job.Titles.Where(t => !t.Intro)] }; // the intro is no title: it is muxed apart and starts the disc
        if (job.Titles.Count == 0) throw new ArgumentException("no video to put on the disc");
        if (job.Titles.Count > 1 && job.Menu == null) throw new InvalidOperationException("several videos need a menu to choose from");
        job = job with { Titles = DiscTitle.MainFirst(job.Titles) }; // playlists 1..mainCount are the feature, the bonus ones follow
        var mainCount = job.Titles.Count(t => !t.Bonus);
        var sources = job.Titles.Select(t => t.Cut(Probe(t.Video))).ToList(); // durations from here on are the parts kept
        var introSource = intro?.Cut(Probe(intro.Video));
        var work = job.OutDir + "_work";
        Tools.FreshFolder(work); // first: a locked file there must not cost the earlier disc
        try { Tools.PrepareOutput(job.OutDir); Author(job, intro, introSource, sources, mainCount, work, log); }
        finally { Tools.TryDelete(work); } // temporary videos of many GB: a failed build must not leave them behind
        log("done: " + job.OutDir);
    }

    static void Author(BluRayJob job, DiscTitle? intro, SourceInfo? introSource, List<SourceInfo> sources, int mainCount, string work, Action<string> log)
    {
        var kbps = intro == null ? TargetKbps(job, sources, log) : TargetKbps(job with { Titles = [.. job.Titles, intro] }, [.. sources, introSource!], log); // the intro takes room on the disc too
        var muxed = new List<Muxed>();
        for (var i = 0; i < job.Titles.Count; i++)
        {
            var playlist = MoviePlaylist + i;
            // later titles are muxed on their own (playlist/clip numbers offset), then merged into the first disc folder
            var dest = i == 0 ? job.OutDir : Path.Combine(work, "mux" + playlist);
            if (i > 0 && Directory.Exists(dest)) Directory.Delete(dest, true);
            muxed.Add(MuxTitle(job, job.Titles[i], sources[i], playlist, dest, work, kbps, log));
            if (i > 0) Merge(dest, job.OutDir);
        }
        if (intro != null)
        {
            var introDest = Path.Combine(work, "mux" + IntroPlaylist);
            MuxTitle(job, intro, introSource!, IntroPlaylist, introDest, work, kbps, log);
            Merge(introDest, job.OutDir);
        }

        // disc title and thumbnails shown by players and Windows (BDMV/META/DL: bdmt_*.xml, thumbnail_WxH.jpg, the two sizes of commercial discs)
        var metaDir = Path.Combine(job.OutDir, "BDMV", "META", "DL");
        Directory.CreateDirectory(metaDir);
        var thumbnails = "";
        foreach (var (tw, th) in new[] { (416, 240), (640, 360) })
        {
            var jpg = $"thumbnail_{tw}x{th}.jpg";
            try // a picture for players and Windows is a bonus: a frame ffmpeg cannot decode must not cost the whole build
            {
                Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-ss", (sources[0].Duration * 0.1).ToString("0.###", Inv), "-i", muxed[0].Prepared, "-frames:v", "1", "-q:v", "3",
                    "-vf", $"scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th}", Path.Combine(metaDir, jpg)]);
            }
            catch (InvalidOperationException) { log("warning: no thumbnail for the disc (ffmpeg could not read a frame)"); }
            if (File.Exists(Path.Combine(metaDir, jpg))) thumbnails += $"<di:thumbnail href=\"{jpg}\" />";
        }
        // one file per language: a player shows the one that matches its own language setting, bdmt_eng.xml being the fallback every disc carries
        void Bdmt(string lang, string name) => File.WriteAllText(Path.Combine(metaDir, $"bdmt_{lang}.xml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><disclib xmlns=\"urn:BDA:bdmv;disclib\"><di:discinfo xmlns:di=\"urn:BDA:bdmv;discinfo\"><di:title><di:name>"
            + System.Security.SecurityElement.Escape(name) + "</di:name></di:title>"
            + (thumbnails.Length > 0 ? $"<di:description>{thumbnails}</di:description>" : "") + $"<di:language>{lang}</di:language></di:discinfo></disclib>");
        var byLanguage = job.Menu?.LanguageTitles() ?? [];
        if (!byLanguage.Any(t => t.Lang == "eng")) Bdmt("eng", job.Menu?.Title ?? job.Label);
        foreach (var (lang, name) in byLanguage) Bdmt(lang, name);

        if (job.Menu != null)
        {
            log("menu: render");
            var thumbs = Path.Combine(work, "thumbs");
            Directory.CreateDirectory(thumbs);
            string? Thumb(string name, int title, double t, int w, int h)
            {
                var file = Path.Combine(thumbs, name + ".jpg");
                try
                {
                    Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-ss", Math.Max(0, Math.Min(t, sources[title].Duration - 1)).ToString("0.###", Inv),
                        "-i", muxed[title].Prepared, "-frames:v", "1", "-q:v", "2", "-vf", $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}", file]);
                }
                catch (InvalidOperationException) { log($"warning: no picture for {name} (ffmpeg could not read a frame)"); } // the menu then shows an empty frame
                return File.Exists(file) ? file : null;
            }
            var series = mainCount > 1; // the extras do not make a series of a film
            TitleEntry Entry(int i, string name) => new(job.Titles[i].DisplayName, MoviePlaylist + i, Thumb(name, i, sources[i].Duration * 0.1, 480, 270));
            var content = new MenuContent(MoviePlaylist, Thumb("preview", 0, sources[0].Duration * 0.1, 800, 450),
                series ? [] : muxed[0].ChapterTimes.Select((t, i) => Thumb($"c{i + 1}", 0, t + 3, 480, 270)).ToList(),
                series ? Enumerable.Range(0, mainCount).Select(i => Entry(i, $"t{i + 1}")).ToList() : null,
                muxed[0].Audio, muxed[0].Subtitles,
                mainCount < job.Titles.Count ? Enumerable.Range(mainCount, job.Titles.Count - mainCount).Select(i => Entry(i, $"b{i - mainCount + 1}")).ToList() : null);
            var menuDir = Path.Combine(work, "menu");
            var loop = 0;
            var moving = job.Menu.Loop;
            if ((moving || job.Menu.ExistingMusic() != null) && job.Uhd) log("moving menu and music skipped: a 4K disc needs a 2160p clip, which BurnSmith does not make yet");
            else if (moving || job.Menu.ExistingMusic() != null) { MuxLoop(job, work, log); loop = LoopPlaylist; }
            var menuPages = MenuBuilder.Build(job.Menu, content);
            if (MenuBuilder.TracksLeftOut(menuPages, content) is { } leftOut) log(leftOut);
            MenuFiles.WriteBdj(job.Menu, menuPages, menuDir, loop, VideoSize(job).H / 1080.0, job.Playback, mainCount, overlay: moving && loop > 0, defaultSubtitle: muxed[0].DefaultSubtitle); // the xlet chains only the main titles
            AddMenu(job, menuDir, log);
            if (intro != null) StartWithIntro(job.OutDir, job.Playback?.StartAtFirstTitle == true);
        }
        else if (muxed[0].DefaultSubtitle > 0 || intro != null)
        {
            // no menu: the film's own movie object plays the intro, then selects the subtitle, before it plays the film (the BACKUP copy is kept alike)
            List<byte[]> before = [];
            if (intro != null) before.Add(Disc.Nav.PlayPlaylist(IntroPlaylist));
            if (muxed[0].DefaultSubtitle > 0) before.Add(Disc.Nav.SelectStreams(subtitle: muxed[0].DefaultSubtitle));
            foreach (var movieObject in new[] { Path.Combine(job.OutDir, "BDMV", "MovieObject.bdmv"), Path.Combine(job.OutDir, "BDMV", "BACKUP", "MovieObject.bdmv") }.Where(File.Exists))
                File.WriteAllBytes(movieObject, Bdmv.PrependToFilm(File.ReadAllBytes(movieObject), [.. before]));
            if (intro != null) log($"intro: {intro.DisplayName} plays first");
            if (muxed[0].DefaultSubtitle > 0) log($"subtitle {muxed[0].DefaultSubtitle} shown from the start");
        }
    }

    /// <summary>
    /// A menu disc that starts with the intro: First Playback becomes a new movie object that plays <see cref="IntroPlaylist"/> and then jumps to the top menu (the BD-J menu),
    /// in MovieObject.bdmv and index.bdmv and their BACKUP copies. When the disc starts on the first title it jumps to title 1 instead, the same BD-J object:
    /// the top menu sets the title register to 0, which the xlet reads as the Top Menu key and so shows the menu.
    /// </summary>
    static void StartWithIntro(string outDir, bool startAtFirstTitle)
    {
        var bdmv = Path.Combine(outDir, "BDMV");
        var number = -1;
        foreach (var file in new[] { Path.Combine(bdmv, "MovieObject.bdmv"), Path.Combine(bdmv, "BACKUP", "MovieObject.bdmv") })
            File.WriteAllBytes(file, Bdmv.AppendObject(File.ReadAllBytes(file), out number, Disc.Nav.PlayPlaylist(IntroPlaylist), Disc.Nav.JumpTitle(startAtFirstTitle ? 1 : 0)));
        foreach (var file in new[] { Path.Combine(bdmv, "index.bdmv"), Path.Combine(bdmv, "BACKUP", "index.bdmv") })
            File.WriteAllBytes(file, Bdmv.FirstPlayToObject(File.ReadAllBytes(file), number));
    }

    /// <summary>
    /// The one frame rate of a video on a Blu-ray, as ffmpeg and as tsMuxeR write it (tsMuxeR reads "24000/1001" as 24000 and sizes the playlist at 1/1000 of the clip). The two must
    /// agree: a video made at 23.976 and declared as 24 ends 0.1 % before its sound. 24 is told from 23.976 by a narrow margin. The rates a disc of that
    /// <paramref name="height"/> may carry: 1080 has 25 and 29.97 (as fields, see <see cref="Fields"/>), so 50 / 59.94 lose every other picture, as on a DVD; 720 has only
    /// 50 and 59.94, so 25 / 29.97 show each picture twice; 4K has all of them. Any other rate (15, 20...) becomes 23.976.
    /// </summary>
    internal static (string Ffmpeg, string TsMuxer) DiscRate(double fps, int height = 1080)
    {
        bool pal = Math.Abs(fps - 25) < 0.5 || Math.Abs(fps - 50) < 0.5, ntsc = Math.Abs(fps - 30) < 0.5 || Math.Abs(fps - 60) < 0.5;
        var doubled = height == 720 || height == 2160 && fps > 40;
        return pal ? doubled ? ("50", "50") : ("25", "25")
             : ntsc ? doubled ? ("60000/1001", "59.94") : ("30000/1001", "29.97")
             : Math.Abs(fps - 24) < 0.01 ? ("24", "24") : ("24000/1001", "23.976");
    }

    /// <summary>x264's extra settings for a 1080 picture at 25 or 29.97: a Blu-ray has those only as fields, so the progressive pictures are flagged as such (PsF).</summary>
    static string Fields(string rate, int height) => height == 1080 && rate is "25" or "30000/1001" ? ":fake-interlaced=1:pic-struct=1" : "";

    /// <summary>
    /// An HDR picture (PQ or HLG, BT.2020) made SDR BT.709 for a disc that cannot show HDR (a 1080p Blu-ray, a DVD): copied as it is, a player shows it grey and flat.
    /// The input is named, not read from the file; Hable keeps detail in the highlights. Empty for an SDR source. A picture wider than <paramref name="maxWidth"/>
    /// (a 4K film for a 1080p disc) is made smaller first: the conversion, in floating point, then has a quarter of the pixels.
    /// </summary>
    public static string ToSdr(string hdr, int width = 0, int height = 0, int maxWidth = 0) => hdr == "SDR" ? ""
        : (maxWidth > 0 && width > maxWidth ? $"scale={maxWidth}:{(int)Math.Round(height * (double)maxWidth / width / 2) * 2}," : "") + $"zscale=tin={(hdr == "HLG" ? "arib-std-b67" : "smpte2084")}:min=bt2020nc:pin=bt2020:rin=tv:t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    /// <summary>A standard-definition picture (BT.601 colours) put on an HD disc, which a player decodes as BT.709: the colours are converted, else reds turn orange.</summary>
    static string HdMatrix(SourceInfo s) => s.Height < 720 && s.Hdr == "SDR" ? ",scale=in_color_matrix=bt601:out_color_matrix=bt709" : "";

    /// <summary>
    /// Whether a video's H.264 says its pictures are fields (frame_mbs_only_flag 0: interlaced, or progressive flagged as PsF), read from its first picture.
    /// Kept per file (path, size, date): the window's size estimate asks again at every change. False when it cannot be read: the video is then made again.
    /// </summary>
    public static bool FlaggedAsFields(string video)
    {
        var file = new FileInfo(video);
        if (!file.Exists) return false;
        return FieldsSeen.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), _ =>
        {
            var flagged = false;
            try
            {
                Tools.Run(Tools.Find("ffmpeg.exe"), ["-hide_banner", "-i", video, "-map", "0:v:0", "-c", "copy", "-bsf:v", "trace_headers", "-frames:v", "1", "-f", "null", "-"],
                          l => flagged |= l.Contains("frame_mbs_only_flag") && l.TrimEnd().EndsWith(" 0 = 0"));
            }
            catch (Exception e) when (e is InvalidOperationException or FileNotFoundException or System.ComponentModel.Win32Exception) { return false; } // not H.264, or no ffmpeg
            return flagged;
        });
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, DateTime), bool> FieldsSeen = new();

    /// <summary>A rate as ffmpeg writes it ("24000/1001", "25") as pictures a second.</summary>
    static double Value(string rate) => rate.Split('/') is [var n, var d] ? double.Parse(n, Inv) / double.Parse(d, Inv) : double.Parse(rate, Inv);

    /// <summary>Picture size of the disc: 3840x2160 (4K), 1280x720 (a 720p profile) or 1920x1080.</summary>
    static (int W, int H) VideoSize(BluRayJob job) => job.Uhd ? (3840, 2160) : job.Encode?.Hd720 == true ? (1280, 720) : (1920, 1080);

    /// <summary>Rough kb/s of what an audio track weighs on the disc (copied as it is, or re-encoded as AC-3 at 448 / 640).</summary>
    static int AudioKbps(AudioTrack a, DiscTitle t, bool uhd) => !CopiesAudio(a, t, uhd) ? (a.Channels > 2 ? 640 : 448) // re-encoded as AC-3 (volume change, later start)
        : a.Codec switch { "truehd" => 3500, "dts" => 1536, _ => a.Channels > 2 ? 640 : 448 };

    static double AudioAndMenuBytes(BluRayJob job, IReadOnlyList<SourceInfo> sources) =>
        job.Titles.Select((t, i) => t.KeptAudio(sources[i]).Sum(a => AudioKbps(a, t, job.Uhd) * 125.0 * sources[i].Duration)).Sum()
        + 0.15e9 + (job.Menu is { } m ? Math.Clamp(m.LoopSeconds, 5, 120) * (m.Loop ? 2.6e6 : m.ExistingMusic() != null ? 0.1e6 : 0) : 0); // a moving clip, or a black one with music

    /// <summary>Size in GB the disc will take: the source files when they are kept, else video at the bit rate asked for or fitted, plus audio and menu.</summary>
    public static double EstimateGB(BluRayJob job, IReadOnlyList<SourceInfo> sources)
    {
        var kbps = TargetKbps(job, sources, _ => { });
        return kbps == 0 ? FilesBytes(job, sources) / 1e9 : (kbps * 125.0 * sources.Sum(s => s.Duration) + AudioAndMenuBytes(job, sources)) / 1e9;
    }

    /// <summary>
    /// Bytes of the source files, a cut title counting for the part kept.
    /// shortcut: proportional to the length, not to what that stretch really weighs; a cut that starts later is re-encoded at constant quality, so the size is approximate.
    /// </summary>
    static double FilesBytes(BluRayJob job, IReadOnlyList<SourceInfo> sources) =>
        job.Titles.Select((t, i) => Tools.SizeOf(t.Video) * (sources[i].FileDuration > 0 ? sources[i].Duration / sources[i].FileDuration : 1)).Sum();

    /// <summary>
    /// The video bit rate (kb/s) every title is re-encoded at, 0 = keep the source's own quality (copied when the disc accepts it, else constant quality).
    /// A rate asked for wins; "Adapter au disque" keeps the source while its files fit the usable size, else takes what is left of the disc
    /// after the audio and the menu, with a margin because a one-pass encode does not hit its target exactly.
    /// </summary>
    internal static int TargetKbps(BluRayJob job, IReadOnlyList<SourceInfo> sources, Action<string> log)
    {
        var enc = job.Encode ?? new EncodeOptions();
        var top = job.Uhd ? 60000 : 35000; // x265 on a 4K disc, x264 at level 4.1 on a Blu-ray
        if (enc.BdKbps > 0) return Math.Clamp(enc.BdKbps, 2000, top);
        if (!enc.FitDisc || job.CapacityGB <= 0) return 0;
        var seconds = sources.Sum(s => s.Duration);
        var room = job.CapacityGB * 1e9 - AudioAndMenuBytes(job, sources);
        var asIs = FilesBytes(job, sources);
        // the files' size only says something when they are copied: a re-encode at constant quality has no size bound (an HEVC source often doubles as H.264)
        var copied = job.Titles.Select((t, i) => CopiesVideo(job, t, sources[i], 0)).All(c => c);
        if (asIs <= room && copied)
        {
            log($"fit disc: the source ({asIs / 1e9:0.0} GB) fits as it is");
            return 0;
        }
        var kbps = (int)Math.Clamp(room * 8 / seconds / 1000 * 0.94, 2000, top);
        log(asIs <= room ? $"fit disc: the video must be re-encoded, at {kbps} kb/s to fill {job.CapacityGB:0.#} GB at most"
                         : $"fit disc: {asIs / 1e9:0.0} GB do not fit {job.CapacityGB:0.#} GB, re-encoded at {kbps} kb/s");
        if (room * 8 / seconds / 1000 * 0.94 < 2000) log("warning: even at the lowest bit rate this does not fit the disc");
        return kbps;
    }

    static Muxed MuxTitle(BluRayJob job, DiscTitle title, SourceInfo src, int playlist, string dest, string work, int kbps, Action<string> log)
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
            subs.Add((s, Path.Combine(work, $"sub{playlist}_{subs.Count + 1}.{(s.IsPicture ? "sup" : "srt")}")));
        }
        log($"prepare {playlist}: {src.VideoCodec} {src.Width}x{src.Height} {src.Fps:0.###}fps, audio {(audio.Count == 0 ? "none" : string.Join('+', audio.Select(a => a.Codec)))}, subtitles {subs.Count}");
        Prepare(job, title, src, audio, subs, prepared, kbps, log);
        // a subtitle with nothing to show in the part kept is an empty file, which tsMuxeR cannot take
        subs.RemoveAll(x =>
        {
            var empty = new FileInfo(x.File).Length == 0;
            if (empty) log($"subtitle '{x.Track.Name}' skipped: nothing to show in the part kept");
            return empty;
        });
        foreach (var (s, file) in subs.Where(x => x.Track.IsPgs && x.Track.File != null))
        {
            // the pictures of a PGS file are placed on a plane of its own size: 1920x1080 on a Blu-ray, also on a 4K one
            var (planeW, planeH) = job.Uhd ? (1920, 1080) : VideoSize(job);
            if (PgsSubtitles.PlaneSize(File.ReadAllBytes(file)) is { } plane && plane != (planeW, planeH))
                log($"warning: subtitle '{s.Name}' is made for a {plane.Width}x{plane.Height} screen, the disc is {planeW}x{planeH}: its pictures may not sit where they were meant to");
        }

        var fps = DiscRate(src.Fps, VideoSize(job).H).TsMuxer;
        var chapterTimes = title.ChapterList(src.Duration, job.ChapterMinutes);
        var chapters = string.Join(';', chapterTimes.Select(t => TimeSpan.FromSeconds(t).ToString(@"hh\:mm\:ss\.fff", Inv)));
        var (w, h) = VideoSize(job);
        var lines = new List<string>
        {
            $"MUXOPT {(job.Uhd ? "--blu-ray-v3" : "--blu-ray")} --label={job.Label} --vbr --custom-chapters={chapters} --mplsOffset={playlist} --m2tsOffset={playlist}",
            $"{(job.Uhd ? "V_MPEGH/ISO/HEVC" : "V_MPEG4/ISO/AVC")}, \"{prepared}\", fps={fps}, track=256",
        };
        for (var j = 0; j < audio.Count; j++)
            lines.Add($"{AudioTag(BdAudio(audio[j], title, job.Uhd))}, \"{prepared}\", track={257 + j}, lang={Languages.Iso2(audio[j].Lang)}");
        foreach (var (s, file) in subs)
            lines.Add(s.IsPicture
                ? $"S_HDMV/PGS, \"{file}\", fps={fps}, lang={Languages.Iso2(s.Lang)}"
                : $"S_TEXT/UTF8, \"{file}\", font-name=\"Arial\", font-size={65 * h / 1080}, font-color=0xffffffff, bottom-offset=24, font-border=5, text-align=center, video-width={w}, video-height={h}, fps={fps}, lang={Languages.Iso2(s.Lang)}");
        var meta = Path.Combine(work, $"disc{playlist}.meta");
        File.WriteAllLines(meta, lines);
        log($"mux {playlist}: tsMuxeR");
        Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, dest], log);
        return new Muxed(prepared, audio.Select(a => a.Name).ToList(), subs.Select(s => s.Track.Name).ToList(), chapterTimes, title.DefaultSubtitleNumber(subs.Select(s => s.Track).ToList()));
    }

    /// <summary>
    /// The file a moving menu plays, its probe and the second to start at: the design's file, else the first title, which counts
    /// <see cref="MenuDesign.LoopStart"/> from the start of the part kept (a cut-out intro never shows on the menu).
    /// </summary>
    internal static (string File, SourceInfo Src, int Start) LoopSource(MenuDesign menu, DiscTitle first, int seconds)
    {
        var file = menu.LoopFile is { Length: > 0 } f && File.Exists(f) ? f : first.Video;
        var src = Probe(file);
        var own = file == first.Video;
        var room = own ? first.Length(src) : src.Duration;
        return (file, src, (own ? (int)first.Start : 0) + Math.Clamp(menu.LoopStart, 0, Math.Max(0, (int)room - seconds)));
    }

    /// <summary>
    /// The menu music as one file any encoder takes: from <see cref="MenuDesign.MusicStart"/>, repeated to fill <paramref name="seconds"/>, at
    /// <see cref="MenuDesign.MusicVolume"/> %, faded in and out by <see cref="MenuDesign.MusicFade"/> seconds, 48 kHz stereo (a disc does not take 44.1 kHz).
    /// </summary>
    internal static string PrepareMusic(MenuDesign menu, string music, int seconds, string work, Action<string> log)
    {
        var ffmpeg = Tools.Find("ffmpeg.exe");
        // not Probe: it wants a video stream
        var shown = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", music]).Trim();
        if (!double.TryParse(shown, CultureInfo.InvariantCulture, out var length)) throw new InvalidDataException($"{Path.GetFileName(music)}: not an audio file");
        var start = Math.Max(0, menu.MusicStart);
        if (start >= length - 1)
        {
            log($"warning: the music lasts {length:0} s, so it starts at 0 instead of {start} s");
            start = 0;
        }
        var part = Path.Combine(work, "music_part.wav");
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-ss", start.ToString(Inv), "-i", music, "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "48000", part], log); // -vn: cover art is a video stream
        if (new FileInfo(part).Length < 100_000) throw new InvalidDataException($"{Path.GetFileName(music)}: nothing to play from {start} s"); // looping an empty file never ends
        var fade = Math.Clamp(menu.MusicFade, 0, seconds / 2);
        var filter = $"volume={(Math.Clamp(menu.MusicVolume, 0, 300) / 100.0).ToString("0.###", Inv)}"
            + (fade > 0 ? $",afade=t=in:st=0:d={fade},afade=t=out:st={seconds - fade}:d={fade}" : "");
        var output = Path.Combine(work, "music.wav");
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-stream_loop", "-1", "-i", part, "-t", seconds.ToString(Inv), "-af", filter, "-ac", "2", "-ar", "48000", output], log);
        return output;
    }

    /// <summary>
    /// The clip the menu plays behind its pages, muxed apart as playlist <see cref="LoopPlaylist"/> and merged into the disc:
    /// a moving menu = <see cref="MenuDesign.LoopSeconds"/> seconds of video (the design's file, else the first title) re-encoded as 1080p H.264;
    /// with only music = a black picture of that length. Its sound is the design's music (<see cref="PrepareMusic"/>), else the clip's own.
    /// </summary>
    static void MuxLoop(BluRayJob job, string work, Action<string> log)
    {
        var menu = job.Menu!;
        var seconds = Math.Clamp(menu.LoopSeconds, 5, 120);
        var musicFile = menu.ExistingMusic();
        var music = musicFile == null ? null : PrepareMusic(menu, musicFile, seconds, work, log);
        var (w, h) = VideoSize(job);
        var ts = Path.Combine(work, "loop.ts");
        var args = new List<string> { "-y", "-v", "warning" };
        SourceInfo? src = null;
        var (fps, declared) = DiscRate(0);
        var tone = "";
        if (menu.Loop)
        {
            var (file, probe, start) = LoopSource(menu, job.Titles[0], seconds);
            src = probe;
            (fps, declared) = DiscRate(src.Fps, h);
            tone = ToSdr(src.Hdr, src.Width, src.Height, w) is { Length: > 0 } sdr ? sdr + "," : ""; // a moving menu taken from an HDR film
            log($"menu: moving background, {seconds}s from {Path.GetFileName(file)} at {start}s{(music != null ? " + music" : "")}");
            args.AddRange(["-ss", start.ToString(Inv), "-t", seconds.ToString(Inv), "-i", file]);
        }
        else
        {
            log($"menu: music from {Path.GetFileName(musicFile)}, {seconds}s over a black picture");
            args.AddRange(["-f", "lavfi", "-i", $"color=c=black:s={w}x{h}:r={fps}:d={seconds}"]);
        }
        if (music != null) args.AddRange(["-i", music]);
        args.AddRange(["-map", "0:v:0"]);
        if (music != null) args.AddRange(["-map", "1:a:0"]);
        else if (src is { Audio.Count: > 0 }) args.AddRange(["-map", "0:a:0"]);
        var hasAudio = music != null || src is { Audio.Count: > 0 };
        args.AddRange(["-t", seconds.ToString(Inv),
            "-vf", tone + Fit(w, h) + (src is { } loop ? HdMatrix(loop) : ""), "-r", fps,
            "-c:v", "libx264", "-preset", "fast", "-crf", "19", "-pix_fmt", "yuv420p",
            "-x264-params", "bluray-compat=1:level=41:vbv-maxrate=20000:vbv-bufsize=20000:keyint=24:slices=4:bframes=3:b-pyramid=strict:open-gop=0" + Fields(fps, h)]);
        if (hasAudio) args.AddRange(["-c:a", "ac3", "-b:a", "448k", "-ac", "2", "-ar", "48000"]); // the first title's own sound can be anything
        args.AddRange(["-sn", "-dn", ts]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);

        var meta = Path.Combine(work, "loop.meta");
        var lines = new List<string>
        {
            $"MUXOPT --blu-ray --label={job.Label} --vbr --mplsOffset={LoopPlaylist} --m2tsOffset={LoopPlaylist}",
            $"V_MPEG4/ISO/AVC, \"{ts}\", fps={declared}, track=256",
        };
        if (hasAudio) lines.Add($"A_AC3, \"{ts}\", track=257");
        File.WriteAllLines(meta, lines);
        var dest = Path.Combine(work, "mux" + LoopPlaylist);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, dest], log);
        Merge(dest, job.OutDir);
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

    /// <summary>The codec a source audio ends up as on the disc: copied when a Blu-ray carries it and the volume stays, AC-3 otherwise.</summary>
    public static string BdAudio(AudioTrack a, DiscTitle t, bool uhd) => CopiesAudio(a, t, uhd) ? a.Codec : "ac3";

    /// <summary>
    /// A copied audio track keeps everything from the keyframe before a cut's start (stream copy does not drop it), while the re-encoded video begins on the exact frame: sound
    /// a second early. So a title that starts later has its sound re-encoded too (decoded, then cut exactly); a 4K disc copies both from the same keyframe and stays in step.
    /// Only 48 kHz is copied: a disc does not take 44.1 kHz.
    /// </summary>
    static bool CopiesAudio(AudioTrack a, DiscTitle t, bool uhd) => t.Volume == 100 && (t.Start == 0 || uhd) && t.Gaps.Count == 0 && a.SampleRate == 48000 && a.Codec is "ac3" or "dts" or "truehd";

    /// <summary>Whether the title's video goes on the disc as it is (stream copy) instead of being re-encoded; <see cref="TargetKbps"/> and <see cref="Prepare"/> must agree on it.</summary>
    static bool CopiesVideo(BluRayJob job, DiscTitle title, SourceInfo s, int kbps)
    {
        var (w, h) = VideoSize(job);
        var rate = DiscRate(s.Fps, h).Ffmpeg;
        var fpsOk = Math.Abs(s.Fps - Value(rate)) < 0.01; // the disc keeps the source's rate as it is (720p has no 25: such a source is made again at 50)
        var enc = job.Encode ?? new EncodeOptions();
        // a cut that starts later must be re-encoded to begin on the exact frame (a copy starts on the keyframe before it); a 4K passthrough copies it anyway
        var copyCut = title.Start == 0 || (job.Uhd && !enc.ForceReencode && kbps == 0);
        // a 4K source is 10-bit HEVC at its own levels: only the H.264 of a Blu-ray has a player's limits to check
        return !enc.ForceReencode && kbps == 0 && copyCut && (job.Uhd ? s.VideoCodec == "hevc" : s.PlayerReadyH264 && s.Hdr == "SDR") && s.Width == w && s.Height == h && fpsOk
               && title.Effects is null or { IsEmpty: true } // effects are drawn into the picture, so it is made again
               && title.Gaps.Count == 0 // several clips are joined frame by frame
               // 1080 at 25 or 29.97 exists on a Blu-ray only as fields: a progressive stream is made again with the flags (last: it reads the file)
               && (Fields(rate, h).Length == 0 || FlaggedAsFields(title.Video));
    }

    /// <summary>
    /// Remuxes to prepared.ts (ffmpeg numbers the streams from PID 256: video = 256, kept audio = 257..), re-encoding only what a Blu-ray cannot carry,
    /// and writes the subtitle files in the same pass over the source.
    /// </summary>
    static void Prepare(BluRayJob job, DiscTitle title, SourceInfo s, IReadOnlyList<AudioTrack> audio, List<(SubtitleTrack Track, string File)> subs, string output, int kbps, Action<string> log)
    {
        var (w, h) = VideoSize(job);
        var enc = job.Encode ?? new EncodeOptions();
        var videoOk = CopiesVideo(job, title, s, kbps);
        if (job.Uhd && videoOk && title.Start > 0) log($"{title.DisplayName}: 4K is not re-encoded, so the cut starts on the keyframe before {Timecode.Format(title.Start)}");

        // -ss / -t before -i cut the input itself: the video, the sound and the subtitle files written below all start and stop together
        var args = new List<string> { "-y", "-v", "warning" };
        if (title.Start > 0) args.AddRange(["-ss", title.Start.ToString(Inv)]);
        var span = s.Duration + title.GapLength; // the part read: from the first clip's start to the last one's end
        if (title.End != null) args.AddRange(["-t", span.ToString("0.###", Inv)]);
        args.AddRange(["-i", title.Video, "-map", "0:v:0"]);
        foreach (var a in audio) args.AddRange(["-map", $"0:a:{a.Index}"]);
        if (videoOk) args.AddRange(["-c:v", "copy"]);
        // effects are placed in the source's pixels: with some, the picture keeps its size until they are drawn
        var tone = job.Uhd ? "" : title.Effects is null or { IsEmpty: true } ? ToSdr(s.Hdr, s.Width, s.Height, w) : ToSdr(s.Hdr);
        var rate = DiscRate(s.Fps, h).Ffmpeg;
        if (!videoOk) args.AddRange(["-vf", (title.Effects ?? new()).Vf(s.Width, s.Height, title.Start, Fit(w, h) + HdMatrix(s), tone) + (title.Gaps.Count > 0 ? "," + title.GapFilter(false) : ""),
                            "-r", rate,
                            "-c:v", job.Uhd ? "libx265" : "libx264", "-preset", enc.Preset, "-pix_fmt", job.Uhd ? "yuv420p10le" : "yuv420p"]);
        if (!videoOk && job.Uhd)
        {
            // 10-bit HEVC at level 5.1, 100 Mb/s at most. The colours are named here: ffmpeg does not pass them on through the scaler; the HDR10 mastering
            // and light levels come from the source's frames. shortcut: Dolby Vision keeps only its HDR10 base layer (a profile 5 source has no such base and comes out wrong)
            var colors = s.Hdr switch { "HDR10" or "Dolby Vision" => "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1", "HLG" => "colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc", _ => "colorprim=bt709:transfer=bt709:colormatrix=bt709" };
            var maxrate = kbps > 0 ? Math.Clamp(kbps + kbps / 2, 40000, 100000) : 100000;
            args.AddRange(kbps > 0 ? ["-b:v", kbps.ToString(Inv) + "k"] : ["-crf", Math.Clamp(enc.Crf, 10, 28).ToString(Inv)]);
            args.AddRange(["-x265-params", $"log-level=error:{colors}:uhd-bd=1:repeat-headers=1:aud=1:keyint=24:vbv-maxrate={maxrate}:vbv-bufsize={maxrate}"]);
        }
        else if (!videoOk)
        {
            // a rate asked for or fitted to the disc: average bit rate with a ceiling; else constant quality. 40 Mb/s is the level 4.1 limit
            var maxrate = kbps > 0 ? Math.Clamp(kbps + kbps / 4, 30000, 40000) : 30000;
            args.AddRange(kbps > 0 ? ["-b:v", kbps.ToString(Inv) + "k"] : ["-crf", Math.Clamp(enc.Crf, 10, 28).ToString(Inv)]);
            args.AddRange(["-x264-params", $"bluray-compat=1:level=41:vbv-maxrate={maxrate}:vbv-bufsize=30000:keyint=24:slices=4:bframes=3:b-pyramid=strict:open-gop=0{Fields(rate, h)}"]);
        }
        for (var j = 0; j < audio.Count; j++)
        {
            if (CopiesAudio(audio[j], title, job.Uhd)) args.AddRange([$"-c:a:{j}", "copy"]);
            else
            {
                args.AddRange([$"-c:a:{j}", "ac3", $"-b:a:{j}", audio[j].Channels > 2 ? "640k" : "448k", $"-ar:a:{j}", "48000"]);
                if (audio[j].Channels > 6) args.AddRange([$"-ac:a:{j}", "6"]); // AC-3 tops out at 5.1
                var af = string.Join(",", new[] { title.GapFilter(true), title.Volume != 100 ? $"volume={(title.Volume / 100.0).ToString("0.###", Inv)}" : "" }.Where(f => f.Length > 0));
                if (af.Length > 0) args.AddRange([$"-filter:a:{j}", af]);
            }
        }
        args.AddRange(["-sn", "-dn", output]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        WriteOwnSubtitles(title, subs.Where(x => x.Track.File == null && !x.Track.IsVobSub).ToList(), span, log); // a VobSub is made apart, the subtitle files too
        foreach (var (t, file) in subs.Where(x => x.Track.File != null)) WriteExternalSubtitle(title, t, span, file, job.Uhd ? (1920, 1080) : VideoSize(job), log);
        foreach (var (t, file) in subs.Where(x => x.Track.File == null && x.Track.IsVobSub)) WriteEmbeddedVobSub(title, t, span, file, job.Uhd ? (1920, 1080) : VideoSize(job), log);
        foreach (var (_, file) in subs) title.CloseGaps(file, span);
        foreach (var (t, file) in subs.Where(x => !x.Track.IsPicture)) TidySubtitle(file);
    }

    /// <summary>
    /// A picture fitted into the disc's <paramref name="w"/> x <paramref name="h"/> frame with bars, as it is seen: non-square pixels (a DVD rip, a camcorder's
    /// 702x574 at 16:15) are first made square, else the fit would read the stored size and squeeze the picture.
    /// </summary>
    public static string Fit(int w, int h) => $"scale=trunc(iw*sar/2)*2:ih,setsar=1,scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1";

    /// <summary>
    /// ffmpeg turns ASS into SRT with a font tag around each line and {\an8}-style codes: tsMuxeR then renders no subtitle at all (found with an
    /// ASS track: the stream came out empty), so both go; italics, bold and underline stay.
    /// </summary>
    static void TidySubtitle(string file)
    {
        var text = File.ReadAllText(file);
        var clean = System.Text.RegularExpressions.Regex.Replace(text, @"\{\\[^}]*\}|</?font[^>]*>", "");
        if (clean != text) File.WriteAllText(file, clean);
    }

    /// <summary>
    /// The video's own text and PGS tracks, read whole in one pass and cut at the exact times like a subtitle file. Not in the picture's pass: an input -ss
    /// starts the subtitles at the keyframe before the cut (found on a real episode: every cue 0.74 s late), where the decoded picture and sound start on the exact frame.
    /// </summary>
    public static void WriteOwnSubtitles(DiscTitle title, IReadOnlyList<(SubtitleTrack Track, string File)> subs, double span, Action<string> log)
    {
        if (subs.Count == 0) return;
        var args = new List<string> { "-y", "-v", "warning", "-i", title.Video };
        foreach (var (t, file) in subs) args.AddRange(["-map", $"0:s:{t.Index}", "-c:s", t.IsPgs ? "copy" : "srt", "-f", t.IsPgs ? "sup" : "srt", file]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        foreach (var (t, file) in subs)
            if (t.IsPgs) File.WriteAllBytes(file, PgsSubtitles.Cut(File.ReadAllBytes(file), title.Start, span));
            else File.WriteAllText(file, ExternalSubtitle.Cut(File.ReadAllText(file), title.Start, span));
    }

    /// <summary>A VobSub inside the video as the PGS file the disc tools take: made for a <paramref name="plane"/>-sized screen (<see cref="VobSub.FromVideo"/>), cut like the video.</summary>
    internal static void WriteEmbeddedVobSub(DiscTitle title, SubtitleTrack track, double length, string output, (int Width, int Height) plane, Action<string> log)
    {
        var sup = VobSub.FromVideo(title.Video, track.Index, plane.Width, plane.Height, Path.GetDirectoryName(output)!, out var skipped);
        log($"VobSub in {Path.GetFileName(title.Video)}: {track.Name}, made into PGS for a {plane.Width}x{plane.Height} screen" + (skipped > 0 ? $"; {skipped} picture(s) could not be read and are left out" : ""));
        File.WriteAllBytes(output, PgsSubtitles.Cut(sup, title.Start, length));
    }

    /// <summary>
    /// A subtitle file beside the video as the SRT the disc tools take (a .sup stays one, cut by <see cref="PgsSubtitles.Cut"/>; an .idx + .sub is made into one for a
    /// <paramref name="plane"/>-sized screen first, <see cref="VobSub.ToSup"/>): cut like the video (<see cref="ExternalSubtitle.Cut"/>), read as
    /// Unicode when it is one, else as Windows-1252 (what French subtitle files usually are),
    /// (on a Blu-ray the text tracks are then tidied by <see cref="TidySubtitle"/>).
    /// </summary>
    internal static void WriteExternalSubtitle(DiscTitle title, SubtitleTrack track, double length, string output, (int Width, int Height) plane, Action<string> log)
    {
        if (track.IsPgs) // pictures: cut by their own timing, not converted
        {
            var vobSub = Path.GetExtension(track.File!).Equals(".idx", StringComparison.OrdinalIgnoreCase);
            var skipped = 0;
            var sup = vobSub ? VobSub.ToSup(track.File!, track.Stream, plane.Width, plane.Height, out skipped) : File.ReadAllBytes(track.File!);
            if (vobSub) log($"VobSub {Path.GetFileName(track.File!)}: {track.Name}, made into PGS for a {plane.Width}x{plane.Height} screen" + (skipped > 0 ? $"; {skipped} picture(s) could not be read and are left out" : ""));
            File.WriteAllBytes(output, PgsSubtitles.Cut(sup, title.Start, length));
            return;
        }
        var args = new List<string> { "-y", "-v", "error" };
        if (!IsUnicode(File.ReadAllBytes(track.File!))) args.AddRange(["-sub_charenc", "CP1252"]);
        args.AddRange(["-i", track.File!, "-c:s", "srt", "-f", "srt", output]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        File.WriteAllText(output, ExternalSubtitle.Cut(File.ReadAllText(output), title.Start, length)); // not ffmpeg's -ss/-t: they keep a cue that ended before the start
    }

    static bool IsUnicode(byte[] bytes)
    {
        if (bytes.Length >= 2 && (bytes[0], bytes[1]) is (0xFF, 0xFE) or (0xFE, 0xFF)) return true; // UTF-16 byte order mark
        try { new System.Text.UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (System.Text.DecoderFallbackException) { return false; }
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

        var bdjo = Bdmv.Bdjo("org.burnsmith.menu.Menu", job.Uhd ? Bdmv.HaviUhd1080 : job.Encode?.Hd720 == true ? Bdmv.HaviHd720 : Bdmv.HaviHd1080);
        File.WriteAllBytes(Path.Combine(bdmv, "BDJO", "00000.bdjo"), bdjo);
        File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", "BDJO", "00000.bdjo"), bdjo);
        foreach (var index in new[] { Path.Combine(bdmv, "index.bdmv"), Path.Combine(bdmv, "BACKUP", "index.bdmv") })
            if (File.Exists(index)) File.WriteAllBytes(index, Bdmv.PatchIndex(File.ReadAllBytes(index)));
    }
}

/// <summary>Finds files shipped in the assets folder next to the app (in a development build, or any parent: what goes on a disc must not come from a folder a normal user can create, such as C:\assets), else the copy embedded in the assembly.</summary>
public static class Assets
{
    public static string Find(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = Tools.BundledDir() == null ? d.Parent : null)
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
