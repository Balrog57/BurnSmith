using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BurnSmith.Core.Menu;

namespace BurnSmith.Core;

/// <summary>
/// Blu-ray / UHD Blu-ray job: one playlist per entry of <see cref="Titles"/>, in order, the bonus ones (<see cref="DiscTitle.Bonus"/>) after the others.
/// <see cref="Menu"/> null = the disc starts straight on the movie (a single title only).
/// </summary>
public sealed record BluRayJob(IReadOnlyList<DiscTitle> Titles, string OutDir, MenuDesign? Menu = null, bool Uhd = false,
                               string Label = "BURNSMITH", int ChapterMinutes = 5, EncodeOptions? Encode = null,
                               double CapacityGB = 0, PlaybackOptions? Playback = null, SubtitleStyle? Subtitles = null,
                               bool HdmvMenu = false, bool Avchd = false); // HdmvMenu: native navigation; Avchd: USB/SD AVCHD/BDMV layout

/// <summary>
/// How the picture is made (the video mode and quality). Defaults: a Blu-ray source that already fits is copied, otherwise x264 at constant
/// quality <see cref="Crf"/> (lower = better, bigger); <see cref="DvdKbps"/> &gt; 0 sets the DVD video bit rate by hand (0 = fills the disc).
/// Blu-ray: <see cref="BdKbps"/> &gt; 0 = x264 at that video bit rate; <see cref="FitDisc"/> = "Adapter au disque" (the source is kept while it fits
/// <see cref="BluRayJob.CapacityGB"/>, else re-encoded at the bit rate that fills it); <see cref="Hd720"/> = a 1280x720 disc.
/// DVD: <see cref="DvdPal"/> forces PAL (true) or NTSC (false), null follows the first video; <see cref="DvdWide"/> false = 4:3 titles (the menu stays 16:9).
/// </summary>
public sealed record EncodeOptions(bool ForceReencode = false, int Crf = 17, string Preset = "medium", int DvdKbps = 0,
                                   int BdKbps = 0, bool FitDisc = false, bool Hd720 = false, bool? DvdPal = null, bool DvdWide = true, bool Gpu = true, double? Fps = null,
                                   bool Lossless = true, bool CopyEac3 = true, bool NormalizeAudio = false);

/// <summary>
/// The play mode (Blu-ray menu). <see cref="StartAtFirstTitle"/>: the disc starts on the first title instead of the menu.
/// <see cref="PlayAll"/>: the titles follow each other. <see cref="Rewind"/>: when everything has played, go back to the start (the menu, or the
/// first title) instead of stopping. Without <see cref="PlayAll"/> the disc returns to the menu after the title that was picked.
/// The default is the studios' series discs (Warner): the menu, a picked episode followed by the next ones, the menu after the last; bonuses never chain.
/// </summary>
public sealed record PlaybackOptions(bool StartAtFirstTitle = false, bool PlayAll = true, bool Rewind = true, int AutoStartSeconds = 0, bool RememberPosition = false)
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
        new("MiniBD 1080p/i", 7.8, 7.25, false), new("MiniBD 720p", 7.8, 7.25, true), // the 8 cm Blu-ray of camcorders
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
                                string Encoding = "", string PixFmt = "", string Profile = "", int Level = 0, // Encoding: the video's profile / level / reorder depth / reference frames, which a stream-copy join must not mix
                                bool DualLayer = false, // Dolby Vision with its enhancement layer in the same track (profile 7, a UHD Blu-ray's)
                                bool DvProfile5 = false, // retained for callers of probe
                                string Stereo = "", // 3D packed in one picture: "sbs" (side by side), "tab" (top and bottom), "" = 2D
                                IReadOnlyList<double>? Chapters = null, // the file's own chapter starts, in seconds (an mkv's or mp4's); null = it has none
                                IReadOnlyList<string?>? ChapterNames = null, // their titles, one for one (null = the file names none)
                                bool Rotated = false, string DolbyVisionProfile = "", bool DolbyVisionHdr10Base = false, bool Interlaced = false) // shown turned (a phone's video): a disc has no such tag, so the picture is made again, turned (ffmpeg does it when decoding)
{
    /// <summary>
    /// A full-size 3D picture (each view as large as a 2D one: 3840x1080 side by side) made half-size, the frame-packed 3D a 3D TV reads from a 2D disc:
    /// its pixels are declared half as wide (or tall), and the fit to the disc does the rest. A half-size one (1920x1080) already is.
    /// </summary>
    public string HalfStereo => Stereo == "sbs" && Width / 2.0 / Height > 1.5 ? "setsar=1/2," : Stereo == "tab" && Width * 2.0 / Height > 1.5 ? "setsar=2," : "";

    /// <summary>One view (the left, or the top), back to its shape: what a menu thumbnail shows of a 3D picture.</summary>
    public string OneView => Stereo == "sbs" ? "crop=iw/2:ih:0:0,scale=iw*2:ih," : Stereo == "tab" ? "crop=iw:ih/2:0:0,scale=iw:ih*2," : "";

    /// <summary><see cref="OneView"/> taken from this source itself (a moving menu cut from the film), which may be full-size.</summary>
    public string SourceView => HalfStereo.Length == 0 ? OneView : Stereo == "sbs" ? "crop=iw/2:ih:0:0," : "crop=iw:ih/2:0:0,";
    /// <summary>What a Blu-ray player takes from H.264 as it is: 8-bit 4:2:0 at level 4.1 at most (ffprobe says -99 for a level it cannot read).</summary>
    public bool PlayerReadyH264 => VideoCodec == "h264" && PixFmt == "yuv420p" && Level is > 0 and <= 41 && !Profile.Contains("10");
    /// <summary>MPEG-2 as a Blu-ray carries it too (8-bit 4:2:0, main profile): the film of an early Blu-ray, copied instead of made again as H.264.</summary>
    public bool PlayerReadyMpeg2 => VideoCodec == "mpeg2video" && PixFmt == "yuv420p" && Profile == "Main";
}

public static partial class BluRay
{
    /// <summary>Playlist / clip number of the first title; title N is playlist N.</summary>
    public const int MoviePlaylist = 1;

    /// <summary>
    /// Text subtitles of a Blu-ray, on a 1080-line picture: Calibri (a Windows font, drawn into the pictures, not carried), capitals 42 pixels tall and
    /// 46 pixels of margin under the text, as measured on a disc made by DVDFab. tsMuxeR's own Arial 65 and 24 (Yuhan's too) gave capitals of 48
    /// pixels and wider lines, the relative size found too big on a DVD.
    /// </summary>
    public const int TextSubtitleSize = 60, TextSubtitleOffset = 40;
    public const string TextSubtitleFont = "Calibri";

    /// <summary>The font, size, colour and height tsMuxeR draws a text subtitle with on a picture <paramref name="h"/> lines tall: the ones above, changed by the disc's <see cref="SubtitleStyle"/>.</summary>
    public static string TextSubtitleLook(SubtitleStyle? style, int h, bool uhd = false)
    {
        style ??= new();
        var lines = Math.Min(h, 1080);
        var rgb = uhd && style.CalibratedNits is > 0 and < 400 ? ScaleHexRgbForHdr(style.Rgb, style.CalibratedNits) : style.Rgb;
        return $"font-name=\"{style.Font ?? TextSubtitleFont}\", font-size={TextSubtitleSize * style.Scale * lines / 108000}, font-color=0xff{rgb.ToLowerInvariant()}, bottom-offset={(TextSubtitleOffset + style.Lift * 8) * lines / 1080}";
    }

    public static string ScaleHexRgbForHdr(string rgb, int nits)
    {
        if (rgb.Length != 6 || nits is <= 0 or >= 400) return rgb;
        var factor = Math.Clamp(nits, 80, 400) / 400.0;
        var r = Math.Clamp((int)Math.Round(Convert.ToInt32(rgb[..2], 16) * factor), 0, 255);
        var g = Math.Clamp((int)Math.Round(Convert.ToInt32(rgb[2..4], 16) * factor), 0, 255);
        var b = Math.Clamp((int)Math.Round(Convert.ToInt32(rgb[4..6], 16) * factor), 0, 255);
        return $"{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>Playlist / clip number of the moving menu's video.</summary>
    public const int LoopPlaylist = 99;

    /// <summary>Playlist / clip number of the video that plays when the disc starts (<see cref="DiscTitle.Intro"/>).</summary>
    public const int IntroPlaylist = 98;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly string[] LateAudioProbe = ["-analyzeduration", "60000000", "-probesize", "350000000"];

    public static SourceInfo Probe(string file)
    {
        var ffprobe = Tools.Find("ffprobe.exe");
        string[] fields = ["-of", "json", "-show_streams", "-show_format", "-show_chapters", file];
        using var initial = JsonDocument.Parse(Tools.Run(ffprobe, ["-v", "error", .. fields]));
        var streams = initial.RootElement.GetProperty("streams").EnumerateArray().ToList();
        // Some disc soundtracks begin after the logos. Re-probe only when the initial transport probe lacks their parameters.
        using var extended = streams.Any(s => s.TryGetProperty("codec_name", out var codec) && codec.GetString() == "eac3"
            && (!s.TryGetProperty("sample_rate", out var sampleRate) || sampleRate.GetString() == "0"
                || !s.TryGetProperty("channels", out var channels) || channels.GetInt32() == 0))
            ? JsonDocument.Parse(Tools.Run(ffprobe, ["-v", "error", .. LateAudioProbe, .. fields])) : null;
        var doc = extended ?? initial;
        if (extended != null) streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
        List<JsonElement> Of(string type) => streams.Where(s => s.GetProperty("codec_type").GetString() == type).ToList();
        static string Tag(JsonElement s, string name) => s.TryGetProperty("tags", out var t) && t.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        static bool Flag(JsonElement s, string name) => s.TryGetProperty("disposition", out var d) && d.TryGetProperty(name, out var v) && v.GetInt32() == 1;
        // what a track weighs: mkvmerge's average (BPS, the HD part of DTS-HD / TrueHD included), else ffprobe's (for DTS-HD often the core alone)
        static int Kbps(JsonElement s) => (int)((long.TryParse(Tag(s, "BPS") is { Length: > 0 } b ? b : Tag(s, "BPS-eng"), out var bps) ? bps
                                                 : s.TryGetProperty("bit_rate", out var br) && long.TryParse(br.GetString(), out var bits) ? bits : 0) / 1000);

        // a clip taken from a Blu-ray has its languages in the playlist, not in itself
        var playlist = Path.GetExtension(file).Equals(".m2ts", StringComparison.OrdinalIgnoreCase) ? PlaylistTracks(file) : [];
        string Lang(JsonElement s) => Tag(s, "language") is { Length: > 0 } l ? l
            : s.TryGetProperty("id", out var pid) && Pid(pid.GetString()) is var n && playlist.TryGetValue(n, out var t) ? t.Lang : "";
        // ffprobe leaves codec_name out for a track it cannot name: the file is still read, that track is then one the disc cannot take
        string Codec(JsonElement s) => s.TryGetProperty("codec_name", out var name) ? name.GetString() ?? "" : "";
        // cover art is a video stream too
        var v = Of("video").FirstOrDefault(s => !Flag(s, "attached_pic"));
        if (v.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("no video stream in " + file);
        var audio = Of("audio").Select((s, i) => new AudioTrack(i, Codec(s),
            s.TryGetProperty("channels", out var c) ? c.GetInt32() : 0, Lang(s), Tag(s, "title"),
            s.TryGetProperty("sample_rate", out var rate) && int.TryParse(rate.GetString(), out var hz) ? hz : 0, Kbps(s),
            Codec(s) is "truehd" or "mlp" or "flac" or "alac" || Codec(s).StartsWith("pcm_", StringComparison.Ordinal) || s.TryGetProperty("profile", out var kind) && kind.GetString() == "DTS-HD MA",
            s.TryGetProperty("bits_per_raw_sample", out var raw) && int.TryParse(raw.GetString(), out var bits) && bits > 0 ? bits : s.TryGetProperty("bits_per_sample", out var held) && held.TryGetInt32(out var size) ? size : 0,
            DiscEac3: Codec(s) == "eac3" && DiscDolbyDigitalPlus.Probe(file, i))).ToList();
        var subs = Of("subtitle").Select((s, i) => new SubtitleTrack(i, Codec(s), Lang(s), Tag(s, "title"), Flag(s, "forced"))).ToList();
        if (!doc.RootElement.GetProperty("format").TryGetProperty("duration", out var duration)) // a picture has a video stream but no length
            throw new InvalidDataException("no duration: not a video file");
        var r = v.GetProperty("r_frame_rate").GetString()!.Split('/');
        var dovi = v.TryGetProperty("side_data_list", out var side)
            ? side.EnumerateArray().FirstOrDefault(d => d.TryGetProperty("dv_profile", out _)) : default;
        int DoviField(string field) => dovi.ValueKind != JsonValueKind.Undefined && dovi.TryGetProperty(field, out var value) ? value.GetInt32() : 0;
        string VideoField(string field) => v.TryGetProperty(field, out var value) ? value.GetString() ?? "" : "";
        var dvProfile = DoviField("dv_profile");
        var compatibility = DoviField("dv_bl_signal_compatibility_id");
        var hasDoviRecord = dovi.ValueKind != JsonValueKind.Undefined;
        var hdr = VideoField("color_transfer") switch { "smpte2084" => "HDR10", "arib-std-b67" => "HLG", _ => "SDR" };
        var pqBase = hdr == "HDR10" && VideoField("color_primaries") == "bt2020" && VideoField("color_space") == "bt2020nc";
        var packing = Tag(v, "stereo_mode") + (v.TryGetProperty("side_data_list", out var sides) ? string.Join(' ', sides.EnumerateArray().Select(d => d.TryGetProperty("type", out var t) ? t.ToString() : "")) : "");
        var stereo = Ssif(file) != null ? "mvc" : packing.Contains("left") || packing.Contains("side by side") ? "sbs" : packing.Contains("top") || packing.Contains("bottom") ? "tab" : "";
        var dual = false;
        if (VideoField("codec_name") == "hevc")
        {
            // Elementary HEVC and some containers have no DOVI record; the first access units carry the RPU.
            var frame = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-of", "json", "-select_streams", "v:0", "-read_intervals", "%+#8", "-show_frames", file]);
            dual = frame.Contains("\"el_spatial_resampling_filter_flag\": 1");
            if (frame.Contains("\"Dolby Vision RPU Data\"") && frame.Contains("\"vdr_rpu_profile\": 0"))
                dvProfile = 5;
            else if (frame.Contains("\"Dolby Vision RPU Data\"") && !hasDoviRecord)
            {
                dvProfile = dual ? 7 : 8;
                compatibility = dvProfile == 8 ? hdr == "HLG" ? 4 : pqBase ? 1 : 0 : 0;
            }
            else if (frame.Contains("SMPTE2094-40")) hdr = "HDR10+";
        }
        if (dvProfile != 0) hdr = "Dolby Vision";
        var dvHdr10 = pqBase && (dvProfile == 7 || dvProfile == 8 && compatibility == 1);
        var dolbyProfile = dvProfile == 0 ? "" : dvProfile == 8 ? $"8.{compatibility}" : dvProfile.ToString(Inv);
        // the file's chapters (a single one, at the start, is none)
        var marks = doc.RootElement.TryGetProperty("chapters", out var listed)
            ? listed.EnumerateArray().Select(c => (At: c.TryGetProperty("start_time", out var at) && double.TryParse(at.GetString(), NumberStyles.Float, Inv, out var t) ? t : -1, Name: Tag(c, "title")))
                    .Where(c => c.At >= 0).DistinctBy(c => c.At).OrderBy(c => c.At).ToList() : [];
        return new SourceInfo(
            v.GetProperty("codec_name").GetString()!, v.GetProperty("width").GetInt32(), v.GetProperty("height").GetInt32(),
            double.Parse(r[0], Inv) / double.Parse(r[1], Inv),
            audio.FirstOrDefault()?.Codec ?? "", audio.FirstOrDefault()?.Channels ?? 0,
            double.Parse(duration.GetString()!, Inv), audio, subs, hdr,
            Encoding: string.Join('/', new[] { "profile", "level", "has_b_frames", "refs" }.Select(n => v.TryGetProperty(n, out var p) ? p.ToString() : "")),
            PixFmt: VideoField("pix_fmt"),
            Profile: VideoField("profile"),
            Level: v.TryGetProperty("level", out var lv) && lv.TryGetInt32(out var level) ? level : 0, DualLayer: dvProfile == 7 && dual, DvProfile5: dvProfile == 5, Stereo: stereo, DolbyVisionProfile: dolbyProfile, DolbyVisionHdr10Base: dvHdr10,
            Chapters: marks.Any(c => c.At > 0) ? [.. marks.Select(c => c.At)] : null,
            ChapterNames: marks.Any(c => c.At > 0) && marks.Any(c => c.Name.Length > 0) ? [.. marks.Select(c => c.Name.Length > 0 ? c.Name : null)] : null,
            Rotated: v.TryGetProperty("side_data_list", out var turns) && turns.EnumerateArray().Any(d => d.TryGetProperty("rotation", out var deg) && deg.TryGetDouble(out var a) && Math.Abs(a) % 360 != 0),
            Interlaced: VideoField("field_order") is "tt" or "bb" or "tb" or "bt");
    }

    static int Pid(string? id) => id is { Length: > 2 } && id.StartsWith("0x") && int.TryParse(id.AsSpan(2), NumberStyles.HexNumber, Inv, out var n) ? n : -1;

    /// <summary>Chapter start times (seconds): one every <paramref name="minutes"/>, never a chapter shorter than 1 minute.</summary>
    public static List<double> Chapters(double duration, int minutes)
    {
        var step = Math.Max(1, minutes) * 60.0;
        var list = new List<double>();
        for (var t = 0.0; t < duration - 60 || t == 0; t += step) list.Add(t);
        return list;
    }

    /// <summary>What one muxed title gives the menu: its preview source and the names of the tracks that really went on the disc.</summary>
    sealed record Muxed(string Prepared, List<string> Audio, List<SubtitleTrack> Subtitles, List<double> ChapterTimes, int DefaultSubtitle,
                        List<string> AudioLangs);

    /// <summary>
    /// The forced subtitle tracks placed last, each in the language of an audio track ("French (Forced)" after "French"): the menu does not list them,
    /// it shows the one of the audio's language while subtitles are off. Returns how many tracks the menu lists and, by audio stream, the forced stream.
    /// </summary>
    public static (int Listed, List<(int Audio, int Sub)> Forced) ForcedTracks(IReadOnlyList<string> audioLangs, IReadOnlyList<(string Lang, bool Forced)> subs)
    {
        var listed = subs.Count;
        string Key(string lang) => Languages.Iso1(Languages.Code(lang) ?? lang); // "fre", "fra" and "fr" are one language
        while (listed > 0 && subs[listed - 1].Forced && audioLangs.Any(a => Key(a) == Key(subs[listed - 1].Lang))) listed--;
        var forced = audioLangs.Select((lang, a) => (Audio: a + 1, Sub: Enumerable.Range(listed, subs.Count - listed).FirstOrDefault(i => Key(subs[i].Lang) == Key(lang), -1) + 1))
                               .Where(f => f.Sub > 0).ToList();
        return (listed, forced);
    }

    /// <param name="progress">Fills as the videos are converted (or copied), each by its share of their total length.</param>
    public static void Build(BluRayJob job, Action<string>? log, CancellationToken cancel, IProgress<double>? progress = null) => Tools.WithCancel(cancel, () => Build(job, log), progress);

    public static void Build(BluRayJob job, Action<string>? log = null)
    {
        if (job.Avchd && !Path.GetFileName(Path.TrimEndingDirectorySeparator(job.OutDir)).Equals("AVCHD", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(job.OutDir);
            Build(job with { OutDir = Path.Combine(job.OutDir, "AVCHD") }, log);
            return;
        }
        log ??= _ => { };
        Tools.KeepInputs(DiscTitle.Inputs(job.Titles, job.Menu), job.OutDir, job.OutDir + "_work");
        var intro = job.Titles.FirstOrDefault(t => t.Intro);
        if (job.Titles.Count(t => t.Intro) > 1) throw new ArgumentException("une seule vidéo de début est possible");
        job = job with { Titles = [.. job.Titles.Where(t => !t.Intro)] }; // the intro is no title: it is muxed apart and starts the disc
        if (job.Titles.Count == 0) throw new ArgumentException("no video to put on the disc");
        if (MoviePlaylist + job.Titles.Count > IntroPlaylist) throw new ArgumentException($"{job.Titles.Count} vidéos : un disque en porte {IntroPlaylist - MoviePlaylist} au plus (les numéros suivants sont ceux de la vidéo de début et du menu)");
        if (job.Titles.Count > 1 && job.Menu == null) throw new InvalidOperationException("several videos need a menu to choose from");
        job = job with { Titles = DiscTitle.MainFirst(job.Titles) }; // playlists 1..mainCount are the feature, the bonus ones follow
        var mainCount = job.Titles.Count(t => !t.Bonus);
        var sources = job.Titles.Select(t => t.Cut(Probe(t.Video))).ToList(); // durations from here on are the parts kept
        var introSource = intro?.Cut(Probe(intro.Video));
        for (var i = 0; i < sources.Count; i++) RequireSupportedDolbyBase(sources[i], job.Titles[i].DisplayName);
        if (introSource != null) RequireSupportedDolbyBase(introSource, intro!.DisplayName);
        if (job.Menu is { Loop: true } menu && menu.LoopFile is { Length: > 0 } backdrop && File.Exists(backdrop))
            RequireSupportedDolbyBase(Probe(backdrop), Path.GetFileName(backdrop));
        // Before wiping the previous disc or running hours of encoding: verify the output drive can hold the output + peak working files.
        CheckRoom(job.OutDir, NeededBytes(job, sources, intro, introSource));
        var work = job.OutDir + "_work";
        Tools.FreshFolder(work); // first: a locked file there must not cost the earlier disc
        try { Tools.PrepareOutput(job.OutDir); Author(job, intro, introSource, sources, mainCount, work, log); }
        finally { Tools.TryDelete(work); } // temporary videos of many GB: a failed build must not leave them behind
        log("done: " + job.OutDir);
    }

    /// <summary>Peak disk space needed on the output drive (final BDMV + peak temporary prepared stream of the largest title).</summary>
    public static long NeededBytes(BluRayJob job, IReadOnlyList<SourceInfo> sources, DiscTitle? intro = null, SourceInfo? introSource = null)
    {
        var allTitles = intro == null ? job.Titles : [.. job.Titles, intro];
        var allSources = introSource == null ? sources : [.. sources, introSource];
        var discBytes = (long)(EstimateGB(job with { Titles = allTitles }, allSources) * 1e9);
        var largest = allSources.Count == 0 ? 0L : allSources.Select((s, i) =>
        {
            var share = Math.Max(0.1, s.Duration / Math.Max(1, allSources.Sum(x => x.Duration)));
            return (long)(discBytes * share);
        }).Max();
        // Final BDMV + largest title's work copy + 250 MB safety margin
        return (long)((discBytes + largest) * 1.05) + 250_000_000L;
    }

    static void CheckRoom(string outDir, long needed)
    {
        long free;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(outDir))!;
            free = new DriveInfo(root).AvailableFreeSpace;
            if (Directory.Exists(outDir)) free += new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return; }
        if (free < needed)
            throw new IOException($"Pas assez de place pour ce Blu-ray sur {Path.GetPathRoot(Path.GetFullPath(outDir))} : il faut environ {needed / 1e9:0.0} Go pendant la création, {free / 1e9:0.0} Go sont libres.");
    }

    static void Author(BluRayJob job, DiscTitle? intro, SourceInfo? introSource, List<SourceInfo> sources, int mainCount, string work, Action<string> log)
    {
        var kbps = intro == null ? TargetKbps(job, sources, log) : TargetKbps(job with { Titles = [.. job.Titles, intro] }, [.. sources, introSource!], log); // the intro takes room on the disc too
        var muxed = new List<Muxed>();
        var lined = job.Titles.Count > 1 ? DiscTitle.LineUpSubtitles(job.Titles, sources) : null;
        var linedAudio = job.Titles.Count > 1 ? DiscTitle.LineUpAudio(job.Titles, sources) : null;
        var total = Math.Max(1, sources.Sum(s => s.Duration) + (introSource?.Duration ?? 0));
        var converted = 0.0;
        for (var i = 0; i < job.Titles.Count; i++)
        {
            Tools.Converting(converted / total, sources[i].Duration / total);
            converted += sources[i].Duration;
            var playlist = MoviePlaylist + i;
            // later titles are muxed on their own (playlist/clip numbers offset), then merged into the first disc folder
            var dest = i == 0 ? job.OutDir : Path.Combine(work, "mux" + playlist);
            if (i > 0 && Directory.Exists(dest)) Directory.Delete(dest, true);
            muxed.Add(MuxTitle(job, job.Titles[i], sources[i], lined?[i], linedAudio?[i], playlist, dest, work, kbps, log));
            if (i > 0) Merge(dest, job.OutDir);
        }
        if (intro != null)
        {
            var introDest = Path.Combine(work, "mux" + IntroPlaylist);
            Tools.Converting(converted / total, introSource!.Duration / total);
            MuxTitle(job, intro, introSource!, null, null, IntroPlaylist, introDest, work, kbps, log);
            Merge(introDest, job.OutDir);
        }
        // tsMuxeR marks a disc as 3D (SS_content_exist_flag) when its first title is; a later 3D title needs the mark too. The image writer makes the .ssif (see Udf)
        if (Udf.Interleaved(job.OutDir).Count > 0)
            foreach (var index in new[] { Path.Combine(job.OutDir, "BDMV", "index.bdmv"), Path.Combine(job.OutDir, "BDMV", "BACKUP", "index.bdmv") }.Where(File.Exists))
            {
                var bytes = File.ReadAllBytes(index);
                bytes[44] |= 0x20; // AppInfoBDMV, after its length: reserved, initial_output_mode_preference, SS_content_exist_flag
                File.WriteAllBytes(index, bytes);
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
                        "-i", muxed[title].Prepared, "-frames:v", "1", "-q:v", "2", "-vf", $"{sources[title].OneView}scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}", file]);
                }
                catch (InvalidOperationException) { log($"warning: no picture for {name} (ffmpeg could not read a frame)"); } // the menu then shows an empty frame
                return File.Exists(file) ? file : null;
            }
            var series = mainCount > 1; // the extras do not make a series of a film
            // a picture chosen for the title (see DiscTitle.ThumbImage), fitted like a frame; unreadable = the frame of the video
            string? Cover(string name, string image, int w, int h)
            {
                var file = Path.Combine(thumbs, name + ".jpg");
                try { Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-i", image, "-frames:v", "1", "-q:v", "2", "-vf", $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}", file]); }
                catch (InvalidOperationException) { log($"warning: {Path.GetFileName(image)} is not a picture ffmpeg can read: the thumbnail comes from the video"); return null; }
                return file;
            }
            TitleEntry Entry(int i, string name) => new(job.Titles[i].DisplayName, MoviePlaylist + i, (job.Titles[i].ThumbImage is { } img ? Cover(name, img, 480, 270) : null)
                ?? Thumb(name, i, job.Titles[i].ThumbAt ?? sources[i].Duration * 0.1, 480, 270), job.Titles[i].Overview);
            var (listed, forced) = ForcedTracks(muxed[0].AudioLangs, muxed[0].Subtitles.Select(s => (s.Lang, s.Forced)).ToList());
            if (forced.Count > 0) log($"menu: {forced.Count} forced subtitle track(s) shown with their audio while subtitles are off, not listed");
            var content = new MenuContent(MoviePlaylist, Thumb("preview", 0, sources[0].Duration * 0.1, 800, 450),
                series ? [] : muxed[0].ChapterTimes.Select((t, i) => Thumb($"c{i + 1}", 0, t + 3, 480, 270)).ToList(),
                series ? Enumerable.Range(0, mainCount).Select(i => Entry(i, $"t{i + 1}")).ToList() : null,
                muxed[0].Audio, muxed[0].Subtitles.Take(listed).Select(s => s.Name).ToList(),
                mainCount < job.Titles.Count ? Enumerable.Range(mainCount, job.Titles.Count - mainCount).Select(i => Entry(i, $"b{i - mainCount + 1}")).ToList() : null)
                { ChapterNames = series ? null : job.Titles[0].ChapterNames(sources[0].Duration, job.ChapterMinutes) };
            var menuDir = Path.Combine(work, "menu");
            var menuPages = MenuBuilder.Build(job.Menu, content);
            if (MenuBuilder.TracksLeftOut(menuPages, content) is { } leftOut) log(leftOut);
            foreach (var unsafeLine in SafeArea.Warnings(menuPages, job.Menu)) log(unsafeLine);
            var subtitleTracks = muxed.Select(m =>
            {
                var (visible, automatic) = ForcedTracks(m.AudioLangs, m.Subtitles.Select(s => (s.Lang, s.Forced)).ToList());
                return (Visible: m.Subtitles.Take(visible).Select((s, k) => (Track: s, Number: k + 1))
                    .Where(s => s.Track.Index >= 0).Select(s => (s.Track.Name, s.Number)).ToList(),
                    Forced: automatic.Where(f => m.Subtitles[f.Sub - 1].Index >= 0).ToList());
            }).ToList();
            var popup = job.Menu.Popup && !job.HdmvMenu ? MenuBuilder.Popup(job.Menu, content,
                muxed.Select((m, i) => (MoviePlaylist + i, m.ChapterTimes.Count, (IReadOnlyList<string>)m.Audio,
                    (IReadOnlyList<(string Name, int Number)>)subtitleTracks[i].Visible))) : null;
            if (popup != null) log($"menu: pop-up menu on {job.Titles.Count} title(s)");
            // the BD-J graphics plane is 1920x1080 on a 4K disc too (the player scales it up): 3840x2160 pages ran the xlet out of heap
            var titleSources = muxed.Select((m, i) => (File: m.Prepared, Chapters: (IReadOnlyList<double>)m.ChapterTimes, Duration: sources[i].Duration)).ToList();
            if (job.HdmvMenu)
            {
                HdmvMenu.Add(job, menuPages, mainCount, muxed[0].DefaultSubtitle <= listed ? muxed[0].DefaultSubtitle : 0,
                    subtitleTracks.Select((s, i) => (muxed[i].Audio.Count, (IReadOnlyList<int>)s.Visible.Select(t => t.Number).ToList(),
                        (IReadOnlyList<(int Audio, int Sub)>)s.Forced)).ToList(), intro != null, work, log, titleSources);
                return;
            }
            WriteSoundFiles(job, "BD-J", log);
            var menuScale = Math.Min(1, VideoSize(job).H / 1080.0);
            MuxLoop(job, work, log);
            var baseSpec = MediaSpec.From(job.Menu, job.Menu.MediaFor("root"));
            var specPlaylists = new Dictionary<MediaSpec, int> { [baseSpec] = LoopPlaylist };
            var pageLoops = new Dictionary<string, (int Playlist, bool Overlay, IReadOnlyList<Box>? Cutouts)>();
            var nextExtraPlaylist = LoopPlaylist - 1;
            var animDir = Path.Combine(work, "anim_thumbs");
            foreach (var p in menuPages)
            {
                var pSpec = MediaSpec.From(job.Menu, job.Menu.MediaFor(p.Id));
                var thumbClips = !pSpec.Loop && job.Menu.AnimatedThumbs ? BluRayThumbnailClips(p, titleSources) : [];
                if (thumbClips.Count > 0)
                {
                    Directory.CreateDirectory(animDir);
                    var staticBg = Path.Combine(animDir, $"base_{p.Id}.jpg");
                    File.WriteAllBytes(staticBg, MenuRenderer.Sta(() => MenuRenderer.Background(job.Menu, p, menuScale)));
                    var secs = LoopLength(pSpec.ToDesign(job.Menu), MaxLoopSeconds);
                    var mFile = pSpec.ToDesign(job.Menu).ExistingMusic();
                    var mWav = mFile == null ? null : PrepareMusic(pSpec.ToDesign(job.Menu), mFile, secs, Path.Combine(animDir, p.Id), log);
                    var (ts, decl) = BuildAnimatedThumbsTs(job, $"bdj_{p.Id}", staticBg, thumbClips, mWav, secs, animDir, log);
                    var pl = nextExtraPlaylist--;
                    MuxLoopForSpec(job, pSpec.ToDesign(job.Menu), pl, ts, work, log, repeatClip: true, declaredOverride: decl);
                    pageLoops[p.Id] = (pl, true, thumbClips.Select(c => c.Rect).ToList());
                    continue;
                }
                if (!pSpec.Equals(baseSpec))
                {
                    if (!specPlaylists.TryGetValue(pSpec, out var pl))
                    {
                        pl = nextExtraPlaylist--;
                        specPlaylists[pSpec] = pl;
                        MuxLoopForSpec(job, pSpec.ToDesign(job.Menu), pl, null, work, log);
                    }
                    pageLoops[p.Id] = (pl, pSpec.Loop, null);
                }
                else if (pSpec.Loop != job.Menu.Loop)
                {
                    pageLoops[p.Id] = (LoopPlaylist, pSpec.Loop, null);
                }
            }
            var moving = job.Menu.Loop;
            MenuFiles.WriteBdj(job.Menu, menuPages, menuDir, LoopPlaylist, menuScale, job.Playback ?? new(), mainCount,
                overlay: moving, defaultSubtitle: muxed[0].DefaultSubtitle <= listed ? muxed[0].DefaultSubtitle : 0, popup: popup,
                subtitleTracks: subtitleTracks.Select((s, i) => (MoviePlaylist + i, s.Visible.Select(t => t.Number), (IReadOnlyList<(int Audio, int Sub)>)s.Forced)),
                log: log, loopReturnMark: moving ? Mpls.MenuLoopReturnMark : -1,
                titleEnds: job.Titles.Select((title, index) => (MoviePlaylist + index, title.EndAction)),
                hdrGraphicsNits: job.Uhd ? job.Menu.HdrGraphicsNits : 0,
                pageLoops: pageLoops);
            AddMenu(job, menuDir, intro != null, log);
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
    /// The one frame rate of a video on a Blu-ray, as ffmpeg and as tsMuxeR write it (tsMuxeR reads "24000/1001" as 24000 and sizes the playlist at 1/1000 of the clip). The two must
    /// agree: a video made at 23.976 and declared as 24 ends 0.1 % before its sound. 24 is told from 23.976 by a narrow margin. The rates a disc of that
    /// <paramref name="height"/> may carry: 1080 has 25 and 29.97 (as fields, see <see cref="Fields"/>), so 50 / 59.94 lose every other picture, as on a DVD; 720 has only
    /// 50 and 59.94, so 25 / 29.97 show each picture twice; 4K has all of them. Any other rate (15, 20...) becomes 23.976.
    /// </summary>
    /// <summary>
    /// The menu pages of <paramref name="job"/> as the disc will show them (menu_PAGE.jpg in <paramref name="dir"/>), without encoding anything: the same tracks, names,
    /// thumbnails and hidden forced subtitles as <see cref="Build"/>, pictures taken from the sources. What the CLI's --preview gives an AI instead of the editor's view.
    /// </summary>
    public static List<string> PreviewMenu(BluRayJob job, string dir, Action<string>? log = null, bool safeArea = false)
    {
        log ??= _ => { };
        if (job.Menu == null) throw new ArgumentException("no menu to preview (--no-menu)");
        Directory.CreateDirectory(dir);
        job = job with { Titles = DiscTitle.MainFirst([.. job.Titles.Where(t => !t.Intro)]) }; // as Build: the intro is no title, the feature comes first
        var sources = job.Titles.Select(t => t.Cut(Probe(t.Video))).ToList();
        var mainCount = job.Titles.Count(t => !t.Bonus);
        // shortcut: a picture at Start + t of the file (a title cut into several clips may show a moment of a dropped part); exact enough to judge a layout
        string? Frame(string name, int i, double t, int w, int h)
        {
            var file = Path.Combine(dir, $"thumb_{name}.jpg");
            var image = job.Titles[i].ThumbImage;
            try
            {
                Tools.Run(Tools.Find("ffmpeg.exe"), image != null && name[0] is 't' or 'b' // a picture chosen for the title (--thumb), as Build's Cover
                    ? ["-y", "-v", "error", "-i", image, "-frames:v", "1", "-q:v", "2", "-vf", $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}", file]
                    : ["-y", "-v", "error", "-ss", (job.Titles[i].Start + Math.Max(0, Math.Min(t, sources[i].Duration - 1))).ToString("0.###", Inv), "-i", job.Titles[i].Video,
                       "-frames:v", "1", "-q:v", "2", "-vf", $"{sources[i].OneView}scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}", file]);
            }
            catch (InvalidOperationException) { log($"warning: no picture for {name}"); }
            return File.Exists(file) ? file : null;
        }
        var series = mainCount > 1;
        TitleEntry Entry(int i, string name) => new(job.Titles[i].DisplayName, MoviePlaylist + i, Frame(name, i, job.Titles[i].ThumbAt ?? sources[i].Duration * 0.1, 480, 270), job.Titles[i].Overview);
        var audio = job.Titles[0].KeptAudio(sources[0]);
        var subs = job.Titles[0].KeptSubtitles(sources[0]).Where(s => s.Supported).ToList();
        var (listed, _) = ForcedTracks([.. audio.Select(a => a.Lang)], [.. subs.Select(s => (s.Lang, s.Forced))]);
        var content = new MenuContent(MoviePlaylist, Frame("preview", 0, sources[0].Duration * 0.1, 800, 450),
            series ? [] : job.Titles[0].ChapterList(sources[0].Duration, job.ChapterMinutes).Select((t, i) => Frame($"c{i + 1}", 0, t + 3, 480, 270)).ToList(),
            series ? Enumerable.Range(0, mainCount).Select(i => Entry(i, $"t{i + 1}")).ToList() : null,
            [.. audio.Select(a => a.Name)], [.. subs.Take(listed).Select(s => s.Name)],
            mainCount < job.Titles.Count ? Enumerable.Range(mainCount, job.Titles.Count - mainCount).Select(i => Entry(i, $"b{i - mainCount + 1}")).ToList() : null)
            { ChapterNames = series ? null : job.Titles[0].ChapterNames(sources[0].Duration, job.ChapterMinutes) };
        var pages = MenuBuilder.Build(job.Menu, content);
        if (MenuBuilder.TracksLeftOut(pages, content) is { } leftOut) log(leftOut);
        foreach (var unsafeLine in SafeArea.Warnings(pages, job.Menu)) log(unsafeLine);
        var files = MenuRenderer.Sta(() => pages.Select(p =>
        {
            var file = Path.Combine(dir, $"menu_{p.Id}.jpg");
            File.WriteAllBytes(file, MenuRenderer.Background(job.Menu, p) is var jpeg && safeArea ? SafeArea.Over(jpeg, p, job.Menu) : jpeg); // --safe-area: the frames a TV may crop drawn over it
            return file;
        }).ToList());
        foreach (var f in files) log($"preview: {f}");
        return files;
    }

    /// <summary>The rate the disc is made at: the one chosen in the settings, else the source's (<see cref="DiscRate"/> turns either into one a disc carries).</summary>
    internal static double Fps(BluRayJob job, SourceInfo s) => job.Encode?.Fps ?? s.Fps;

    internal static (string Ffmpeg, string TsMuxer) DiscRate(double fps, int height = 1080)
    {
        bool pal = Math.Abs(fps - 25) < 0.5 || Math.Abs(fps - 50) < 0.5, ntsc = Math.Abs(fps - 30) < 0.5 || Math.Abs(fps - 60) < 0.5;
        var doubled = height == 720 || height == 2160 && fps > 40;
        return pal ? doubled ? ("50", "50") : ("25", "25")
             : ntsc ? doubled ? ("60000/1001", "59.94") : ("30000/1001", "29.97")
             : Math.Abs(fps - 24) < 0.01 ? ("24", "24") : ("24000/1001", "23.976");
    }

    /// <summary>Hardware video encoder vendor detected on this PC (priority: NVIDIA NVENC -> Intel QuickSync -> AMD AMF -> CPU).</summary>
    public enum GpuVendor { None, Nvidia, Intel, Amd }

    static bool ProbeEncoder(string codec)
    {
        try { Tools.Run(Tools.Find("ffmpeg.exe"), ["-v", "error", "-f", "lavfi", "-i", "color=s=640x360:d=0.1", "-c:v", codec, "-f", "null", "-"]); return true; }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException) { return false; }
    }

    /// <summary>First working hardware H.264 encoder on this PC (NVIDIA NVENC, Intel QSV, AMD AMF, or None).</summary>
    public static readonly Lazy<(GpuVendor Vendor, string Label)> HardwareGpu = new(() =>
    {
        if (ProbeEncoder("h264_nvenc")) return (GpuVendor.Nvidia, "NVIDIA NVENC");
        if (ProbeEncoder("h264_qsv")) return (GpuVendor.Intel, "Intel QuickSync");
        if (ProbeEncoder("h264_amf")) return (GpuVendor.Amd, "AMD AMF");
        return (GpuVendor.None, "Processeur (CPU)");
    }, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>First working hardware 10-bit HEVC encoder on this PC for UHD 4K discs.</summary>
    public static readonly Lazy<GpuVendor> HardwareGpuHevc = new(() =>
    {
        var vendor = HardwareGpu.Value.Vendor;
        if (vendor == GpuVendor.Nvidia && ProbeEncoder("hevc_nvenc")) return GpuVendor.Nvidia;
        if (vendor == GpuVendor.Intel && ProbeEncoder("hevc_qsv")) return GpuVendor.Intel;
        if (vendor == GpuVendor.Amd && ProbeEncoder("hevc_amf")) return GpuVendor.Amd;
        return GpuVendor.None;
    }, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>Whether this PC has a working hardware GPU encoder (NVIDIA NVENC, Intel QSV or AMD AMF).</summary>
    public static bool HasGpu => HardwareGpu.Value.Vendor != GpuVendor.None;

    /// <summary>Human-readable label of the detected GPU encoder.</summary>
    public static string GpuLabel => HardwareGpu.Value.Label;

    /// <summary>Whether this PC's ffmpeg can encode H.264 on an NVIDIA card (retained for existing callers).</summary>
    public static readonly Lazy<bool> Nvenc = new(() => HardwareGpu.Value.Vendor == GpuVendor.Nvidia, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>x264's extra settings for a 1080 picture at 25 or 29.97: a Blu-ray has those only as fields, so the progressive pictures are flagged as such (PsF).</summary>
    static string Fields(string rate, int height) => height == 1080 && rate is "25" or "30000/1001" ? ":fake-interlaced=1:pic-struct=1" : "";

    /// <summary>
    /// An HDR picture (PQ or HLG, BT.2020) made SDR BT.709 for a disc that cannot show HDR (a 1080p Blu-ray, a DVD): copied as it is, a player shows it grey and flat.
    /// The input is named, not read from the file; Mobius keeps the studio's own SDR brightness (Bright 4K against Netflix's 1080p SDR: mean level 71/33/27
    /// for 71/33/26, where Hable gave 57/27/23, a film 15-20 % too dark). Empty for an SDR source. A picture wider than <paramref name="maxWidth"/>
    /// (a 4K film for a 1080p disc) is made smaller first: the conversion, in floating point, then has a quarter of the pixels.
    /// </summary>
    public static string ToSdr(string hdr, int width = 0, int height = 0, int maxWidth = 0) => hdr == "SDR" ? ""
        : (maxWidth > 0 && width > maxWidth ? $"scale={maxWidth}:{(int)Math.Round(height * (double)maxWidth / width / 2) * 2}," : "") + $"zscale=tin={(hdr == "HLG" ? "arib-std-b67" : "smpte2084")}:min=bt2020nc:pin=bt2020:rin=tv:t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    /// <summary>A standard-definition picture (BT.601 colours) put on an HD disc, which a player decodes as BT.709: the colours are converted, else reds turn orange.</summary>
    static string X265Colors(string hdr) => hdr switch { "HDR10" or "HDR10+" or "Dolby Vision" => "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1", "HLG" => "colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc", _ => "colorprim=bt709:transfer=bt709:colormatrix=bt709" };

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
                          l => flagged |= (l.Contains("frame_mbs_only_flag") || l.Contains("progressive_sequence")) && l.TrimEnd().EndsWith(" 0 = 0")); // the second: MPEG-2's own flag
            }
            catch (Exception e) when (e is InvalidOperationException or FileNotFoundException or System.ComponentModel.Win32Exception) { return false; } // not H.264, or no ffmpeg
            return flagged;
        });
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, DateTime), bool> FieldsSeen = new();

    /// <summary>A rate as ffmpeg writes it ("24000/1001", "25") as pictures a second.</summary>
    static double Value(string rate) => rate.Split('/') is [var n, var d] ? double.Parse(n, Inv) / double.Parse(d, Inv) : double.Parse(rate, Inv);

    /// <summary>Picture size of the disc: 3840x2160 (4K), 1280x720 (a 720p profile) or 1920x1080.</summary>
    internal static (int W, int H) VideoSize(BluRayJob job) => job.Uhd ? (3840, 2160) : job.Encode?.Hd720 == true ? (1280, 720) : (1920, 1080);

    /// <summary>Rough kb/s of what an audio track weighs on the disc (copied as it is, or re-encoded as AC-3 at 448 / 640).</summary>
    static int AudioKbps(AudioTrack a, DiscTitle t, BluRayJob job) => AudioMode(a, t, job.Uhd, job.Encode?.Lossless != false, job.Encode?.CopyEac3 != false, job.Encode?.NormalizeAudio == true) switch
    {
        "copy" => a.Kbps > 0 ? a.Kbps : a.Codec switch { "dts" when a.Lossless => 6000, "dts" => 1536, _ => a.Channels > 2 ? 640 : 448 },
        "thd" => (a.Kbps > 0 ? a.Kbps : 4500) + 640, // with its AC-3 core
        "lpcm" => LpcmRate(a) / 1000 * Math.Min(a.Channels + a.Channels % 2, 8) * LpcmBits(a), // a disc stores its channels in pairs
        _ => a.Channels > 2 ? 640 : 448, // re-encoded as AC-3 (volume change, later start)
    };

    static double AudioAndMenuBytes(BluRayJob job, IReadOnlyList<SourceInfo> sources) =>
        job.Titles.Select((t, i) => t.KeptAudio(sources[i]).Sum(a => AudioKbps(a, t, job) * 125.0 * sources[i].Duration)).Sum() + MenuBytes(job);

    static double AudioGrowthBytes(BluRayJob job, IReadOnlyList<SourceInfo> sources) =>
        job.Titles.Select((t, i) => t.KeptAudio(sources[i]).Sum(a =>
        {
            var mode = AudioMode(a, t, job.Uhd, job.Encode?.Lossless != false, job.Encode?.CopyEac3 != false, job.Encode?.NormalizeAudio == true);
            var extra = a.File != null ? AudioKbps(a, t, job) : mode switch
            {
                "thd" => a.Channels > 2 ? 640 : 448,
                "lpcm" => Math.Max(0, AudioKbps(a, t, job) - a.Kbps),
                _ => 0,
            };
            return extra * 125.0 * sources[i].Duration;
        })).Sum();

    /// <summary>Longest moving menu: a whole song (Bright's is 3 min 57), its picture fits a BD-25 beside the film (5 min of 4K = 1.5 GB).</summary>
    internal const int MaxLoopSeconds = 300;

    const int SilentMenuLoopSeconds = 10;

    static double MenuBytes(BluRayJob job)
    {
        const double allowance = 0.15e9;
        if (job.Menu is not { } menu) return allowance;
        var music = menu.ExistingMusic() != null;
        // HDMV carries one clip per page with music (0.3 MB/s measured).
        if (job.HdmvMenu) return allowance + (music ? LoopLength(menu, MaxLoopSeconds) * 0.3e6 * (3 + job.Titles.Count / 4) : 0);
        if (menu.Loop) return allowance + LoopLength(menu, MaxLoopSeconds) * (job.Uhd ? 5e6 : 2.6e6);
        return allowance + (music ? LoopLength(menu, MaxLoopSeconds) : SilentMenuLoopSeconds) * 0.1e6;
    }

    /// <summary>Size in GB the disc will take: the source files when they are kept, else video at the bit rate asked for or fitted, plus audio and menu.</summary>
    public static double EstimateGB(BluRayJob job, IReadOnlyList<SourceInfo> sources)
    {
        var kbps = TargetKbps(job, sources, _ => { });
        return kbps == 0 ? (FilesBytes(job, sources) + AudioGrowthBytes(job, sources)) / 1e9
            : (kbps * 125.0 * sources.Sum(s => s.Duration) + AudioAndMenuBytes(job, sources)) / 1e9;
    }

    /// <summary>
    /// Bytes of the source files, a cut title counting for the part kept.
    /// shortcut: proportional to the length, not to what that stretch really weighs; a cut that starts later is re-encoded at constant quality, so the size is approximate.
    /// </summary>
    static double FilesBytes(BluRayJob job, IReadOnlyList<SourceInfo> sources) =>
        job.Titles.Select((t, i) => (sources[i].Stereo == "mvc" && Ssif(t.Video) is { } both
                                        // a 3D film: both eyes, without the languages left out (a disc's film carries them all)
                                        ? Tools.SizeOf(both) - sources[i].Audio.Except(t.KeptAudio(sources[i])).Sum(a => AudioKbps(a, t, job) * 125.0 * (sources[i].FileDuration > 0 ? sources[i].FileDuration : sources[i].Duration))
                                        : Tools.SizeOf(t.Video))
                                    * (sources[i].FileDuration > 0 ? sources[i].Duration / sources[i].FileDuration : 1)).Sum();

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
        var asIs = FilesBytes(job, sources) + AudioGrowthBytes(job, sources);
        // the files' size only says something when they are copied: a re-encode at constant quality has no size bound (an HEVC source often doubles as H.264)
        var copied = job.Titles.Select((t, i) => CopiesVideo(job, t, sources[i], 0)).All(c => c);
        // copied files already hold their sound, and a copy does not overshoot like an encoder: the disc's real size (the profiles' sizes are GiB: a BD-50 is 46.5), 1 % kept for the file system
        // an .mkv or .mp4 grows by about 5 % once it is a transport stream (192-byte packets that carry 184): a 24.3 GB film would be said to fit a BD-25 and then be refused
        var asStream = job.Titles.All(t => Path.GetExtension(t.Video).ToLowerInvariant() is ".m2ts" or ".mts" or ".ts" or ".ssif") ? asIs : asIs * 1.05;
        if (asStream + MenuBytes(job) <= job.CapacityGB * (1 << 30) * 0.99 && copied)
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

    /// <summary><paramref name="lined"/>: this title's subtitles lined up with the other titles' (<see cref="DiscTitle.LineUpSubtitles"/>), null when it is alone.</summary>
}
