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
                               bool HdmvMenu = false); // the menu as native Blu-ray navigation (see HdmvMenu) instead of the BD-J program

/// <summary>
/// How the picture is made (the video mode and quality). Defaults: a Blu-ray source that already fits is copied, otherwise x264 at constant
/// quality <see cref="Crf"/> (lower = better, bigger); <see cref="DvdKbps"/> &gt; 0 sets the DVD video bit rate by hand (0 = fills the disc).
/// Blu-ray: <see cref="BdKbps"/> &gt; 0 = x264 at that video bit rate; <see cref="FitDisc"/> = "Adapter au disque" (the source is kept while it fits
/// <see cref="BluRayJob.CapacityGB"/>, else re-encoded at the bit rate that fills it); <see cref="Hd720"/> = a 1280x720 disc.
/// DVD: <see cref="DvdPal"/> forces PAL (true) or NTSC (false), null follows the first video; <see cref="DvdWide"/> false = 4:3 titles (the menu stays 16:9).
/// </summary>
public sealed record EncodeOptions(bool ForceReencode = false, int Crf = 17, string Preset = "medium", int DvdKbps = 0,
                                   int BdKbps = 0, bool FitDisc = false, bool Hd720 = false, bool? DvdPal = null, bool DvdWide = true, bool Gpu = true, double? Fps = null,
                                   bool Lossless = false); // Blu-ray, "Son sans perte": a TrueHD track is kept (with the AC-3 core a disc wants beside it), FLAC and PCM become LPCM, instead of AC-3

/// <summary>
/// The play mode (Blu-ray menu). <see cref="StartAtFirstTitle"/>: the disc starts on the first title instead of the menu.
/// <see cref="PlayAll"/>: the titles follow each other. <see cref="Rewind"/>: when everything has played, go back to the start (the menu, or the
/// first title) instead of stopping. Without <see cref="PlayAll"/> the disc returns to the menu after the title that was picked.
/// The default is the studios' series discs (Warner): the menu, a picked episode followed by the next ones, the menu after the last; bonuses never chain.
/// </summary>
public sealed record PlaybackOptions(bool StartAtFirstTitle = false, bool PlayAll = true, bool Rewind = true)
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
                                bool DvProfile5 = false, // Dolby Vision whose base is not HDR10 (IPT): nothing of it can be kept on a disc
                                string Stereo = "", // 3D packed in one picture: "sbs" (side by side), "tab" (top and bottom), "" = 2D
                                IReadOnlyList<double>? Chapters = null, // the file's own chapter starts, in seconds (an mkv's or mp4's); null = it has none
                                IReadOnlyList<string?>? ChapterNames = null, // their titles, one for one (null = the file names none)
                                bool Rotated = false) // shown turned (a phone's video): a disc has no such tag, so the picture is made again, turned (ffmpeg does it when decoding)
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

public static class BluRay
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
    public static string TextSubtitleLook(SubtitleStyle? style, int h)
    {
        style ??= new();
        var lines = Math.Min(h, 1080);
        return $"font-name=\"{style.Font ?? TextSubtitleFont}\", font-size={TextSubtitleSize * style.Scale * lines / 108000}, font-color=0xff{style.Rgb.ToLowerInvariant()}, bottom-offset={(TextSubtitleOffset + style.Lift * 8) * lines / 1080}";
    }

    /// <summary>Playlist / clip number of the moving menu's video.</summary>
    public const int LoopPlaylist = 99;

    /// <summary>Playlist / clip number of the video that plays when the disc starts (<see cref="DiscTitle.Intro"/>).</summary>
    public const int IntroPlaylist = 98;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static SourceInfo Probe(string file)
    {
        var json = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-of", "json", "-show_streams", "-show_format", "-show_chapters", file]);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
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
            s.TryGetProperty("bits_per_raw_sample", out var raw) && int.TryParse(raw.GetString(), out var bits) && bits > 0 ? bits : s.TryGetProperty("bits_per_sample", out var held) && held.TryGetInt32(out var size) ? size : 0)).ToList();
        var subs = Of("subtitle").Select((s, i) => new SubtitleTrack(i, Codec(s), Lang(s), Tag(s, "title"), Flag(s, "forced"))).ToList();
        if (!doc.RootElement.GetProperty("format").TryGetProperty("duration", out var duration)) // a picture has a video stream but no length
            throw new InvalidDataException("no duration: not a video file");
        var r = v.GetProperty("r_frame_rate").GetString()!.Split('/');
        var dolby = v.TryGetProperty("side_data_list", out var side) && side.EnumerateArray().Any(d => d.TryGetProperty("side_data_type", out var t) && (t.GetString() ?? "").Contains("DOVI"));
        var hdr = dolby ? "Dolby Vision" : (v.TryGetProperty("color_transfer", out var ct) ? ct.GetString() : null) switch { "smpte2084" => "HDR10", "arib-std-b67" => "HLG", _ => "SDR" };
        var (dual, p5) = (false, false);
        var packing = Tag(v, "stereo_mode") + (v.TryGetProperty("side_data_list", out var sides) ? string.Join(' ', sides.EnumerateArray().Select(d => d.TryGetProperty("type", out var t) ? t.ToString() : "")) : "");
        var stereo = Ssif(file) != null ? "mvc" : packing.Contains("left") || packing.Contains("side by side") ? "sbs" : packing.Contains("top") || packing.Contains("bottom") ? "tab" : "";
        if (hdr is "HDR10" or "Dolby Vision" && v.GetProperty("codec_name").GetString() == "hevc")
        {
            // Dolby Vision and HDR10+ ride on the frames: the first one tells (a raw stream has no Dolby Vision record in its header)
            var frame = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-of", "json", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_frames", file]);
            if (frame.Contains("\"Dolby Vision RPU Data\"")) (hdr, dual, p5) = ("Dolby Vision", frame.Contains("\"el_spatial_resampling_filter_flag\": 1"), frame.Contains("\"vdr_rpu_profile\": 0"));
            else if (frame.Contains("SMPTE2094-40")) hdr = "HDR10+";
        }
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
            PixFmt: v.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() ?? "" : "",
            Profile: v.TryGetProperty("profile", out var pr) ? pr.GetString() ?? "" : "",
            Level: v.TryGetProperty("level", out var lv) && lv.TryGetInt32(out var level) ? level : 0, DualLayer: dual, DvProfile5: p5, Stereo: stereo,
            Chapters: marks.Any(c => c.At > 0) ? [.. marks.Select(c => c.At)] : null,
            ChapterNames: marks.Any(c => c.At > 0) && marks.Any(c => c.Name.Length > 0) ? [.. marks.Select(c => c.Name.Length > 0 ? c.Name : null)] : null,
            Rotated: v.TryGetProperty("side_data_list", out var turns) && turns.EnumerateArray().Any(d => d.TryGetProperty("rotation", out var deg) && deg.TryGetDouble(out var a) && Math.Abs(a) % 360 != 0));
    }

    /// <summary>
    /// The video to add for a Blu-ray given whole: an .iso (opened read-only by Windows, and left open for the build) or a folder holding BDMV gives its biggest
    /// clip, the film; any other path is itself. The disc must not be encrypted (AACS): ffmpeg and tsMuxeR read only a copy without it.
    /// A film the disc plays in several clips is first joined into one (see <see cref="Joined"/>).
    /// shortcut: the playlist of the biggest clip is the film on the discs seen; one with several angles or decoy playlists may give another one.
    /// </summary>
    public static string MainClip(string path)
    {
        string root;
        if (File.Exists(path) && Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            var quoted = Path.GetFullPath(path).Replace("'", "''");
            var letter = Tools.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                $"$i = Get-DiskImage -ImagePath '{quoted}'; if (-not $i.Attached) {{ $i = Mount-DiskImage -ImagePath '{quoted}' -Access ReadOnly -PassThru }}; ($i | Get-Volume).DriveLetter"]).Trim();
            root = letter.Length == 1 ? letter + @":\" : throw new InvalidDataException($"{Path.GetFileName(path)}: Windows could not open the image");
        }
        else if (Directory.Exists(path)) root = path;
        else return path;
        var stream = new[] { Path.Combine(root, "BDMV", "STREAM"), Path.Combine(root, "STREAM") }.FirstOrDefault(Directory.Exists); // the disc, or its BDMV folder
        var name = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(path));
        var clip = (stream == null ? null : Directory.GetFiles(stream, "*.m2ts").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault())
                   ?? throw new InvalidDataException($"{name}: no Blu-ray film (BDMV\\STREAM) in it");
        return Joined(clip, name) is { } joined ? MainClip(joined) : clip; // the joined image has one clip: no second joining
    }

    /// <summary>The .ssif of a clip of a 3D Blu-ray (BDMV\STREAM\SSIF\N.ssif beside BDMV\STREAM\N.m2ts): both eyes, which tsMuxeR reads; null for any other video.</summary>
    public static string? Ssif(string video) =>
        Path.GetExtension(video).Equals(".m2ts", StringComparison.OrdinalIgnoreCase)
        && Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video))!, "SSIF", Path.GetFileNameWithoutExtension(video) + ".ssif") is var ssif && File.Exists(ssif) ? ssif : null;

    static int Pid(string? id) => id is { Length: > 2 } && id.StartsWith("0x") && int.TryParse(id.AsSpan(2), NumberStyles.HexNumber, Inv, out var n) ? n : -1;

    /// <summary>
    /// What the playlist of a clip taken from a Blu-ray says of its tracks, which the clip does not carry: by PID, the language and, for the subtitles of a
    /// 3D film, the depth they are shown at (3d-plane, -1 = none). Read by tsMuxeR from the biggest playlist that plays the clip; empty when there is none.
    /// shortcut: the biggest playlist is the film on the discs seen; a disc full of decoy playlists may give another one's, which only costs names and depths.
    /// </summary>
    public static Dictionary<int, (string Lang, int Plane)> PlaylistTracks(string video)
    {
        var found = new Dictionary<int, (string Lang, int Plane)>();
        if (MainPlaylist(video) is not { } mpls) return found;
        string listing;
        try { listing = Tools.Run(Tools.Find("tsMuxeR.exe"), [mpls]); }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException) { return found; } // names are a bonus
        foreach (var (id, _, lang, plane) in ListedTracks(listing)) found[id] = (lang, plane);
        return found;
    }

    /// <summary>The biggest playlist of the disc that plays a clip taken from it; null when the clip is not in a disc's BDMV\STREAM.</summary>
    static string? MainPlaylist(string video)
    {
        var stream = Path.GetDirectoryName(Path.GetFullPath(video))!;
        var playlists = Path.Combine(Path.GetDirectoryName(stream) ?? "", "PLAYLIST");
        if (!Path.GetFileName(stream).Equals("STREAM", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(playlists)) return null;
        var clip = Encoding.ASCII.GetBytes(Path.GetFileNameWithoutExtension(video) + "M2TS");
        return Directory.GetFiles(playlists, "*.mpls").Where(f => File.ReadAllBytes(f).AsSpan().IndexOf(clip) >= 0).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    /// <summary>The tracks of a tsMuxeR listing, in order: id, tsMuxeR's codec name (empty when it cannot take the track), language, 3D depth (-1 = none).</summary>
    public static List<(int Id, string Codec, string Lang, int Plane)> ListedTracks(string listing)
    {
        var tracks = new List<(int Id, string Codec, string Lang, int Plane)>();
        var at = -1;
        foreach (var line in listing.Split('\n'))
        {
            if (line.Split(':', 2) is not [var key, var value]) continue;
            (key, value) = (key.Trim(), value.Trim());
            if (key == "Track ID" && int.TryParse(value, out var id))
            {
                tracks.RemoveAll(t => t.Id == id); // a track listed again (the right eye's copy first) is read anew
                tracks.Add((id, "", "", -1));
                at = tracks.Count - 1;
            }
            else if (at < 0) continue;
            else if (key == "Stream ID") tracks[at] = tracks[at] with { Codec = value };
            else if (key == "Stream lang") tracks[at] = tracks[at] with { Lang = value };
            else if (key == "Stream info" && Regex.Match(value, @"3d-plane: (\d+)") is { Success: true } m) tracks[at] = tracks[at] with { Plane = int.Parse(m.Groups[1].Value, Inv) };
        }
        return tracks;
    }

    /// <summary>
    /// A film the disc plays as several clips in turn (seamless branching): tsMuxeR copies them, every track as it is (both eyes of a 3D film), into a
    /// Blu-ray image of one clip in %TEMP%\BurnSmith-joined, which then stands for the film. Null when its playlist has a single clip.
    /// shortcut: the image (as big as the film) stays until another film is joined, which deletes it; the add waits for the copy, a few minutes for a whole film.
    /// </summary>
    static string? Joined(string clip, string name)
    {
        if (MainPlaylist(clip) is not { } mpls) return null;
        var tsMuxer = Tools.Find("tsMuxeR.exe");
        var listing = Tools.Run(tsMuxer, [mpls]);
        if (Regex.Matches(listing, @"^File #\d+", RegexOptions.Multiline).Count < 2) return null;
        var dir = Path.Combine(Path.GetTempPath(), "BurnSmith-joined");
        Directory.CreateDirectory(dir);
        var iso = Path.Combine(dir, string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".iso");
        var key = $"{mpls} {new FileInfo(clip).Length}";
        if (File.Exists(iso + ".done") && File.ReadAllText(iso + ".done") == key) return iso;
        foreach (var old in Directory.GetFiles(dir))
        {
            if (old.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                try { Tools.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", $"Dismount-DiskImage -ImagePath '{old.Replace("'", "''")}' | Out-Null"]); } catch (InvalidOperationException) { }
            try { File.Delete(old); } catch (IOException) { } // still in use by a build: left for the next time
        }
        var meta = Path.Combine(dir, "join.meta");
        File.WriteAllLines(meta, ListedTracks(listing).Where(t => t.Codec.Length > 0).Select(t => $"{t.Codec}, \"{mpls}\", track={t.Id}"
                + (t.Lang.Length > 0 ? $", lang={t.Lang}" : "") + (t.Plane >= 0 ? $", 3d-plane={t.Plane}" : ""))
            .Prepend("MUXOPT --blu-ray --vbr --no-pcr-on-video-pid --label=JOINED"));
        Tools.Run(tsMuxer, [meta, iso]);
        File.WriteAllText(iso + ".done", key);
        return iso;
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
    sealed record Muxed(string Prepared, List<string> Audio, List<string> Subtitles, List<double> ChapterTimes, int DefaultSubtitle,
                        List<string> AudioLangs, List<(string Lang, bool Forced)> SubFlags);

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

    public static void Build(BluRayJob job, Action<string>? log, CancellationToken cancel) => Tools.WithCancel(cancel, () => Build(job, log));

    public static void Build(BluRayJob job, Action<string>? log = null)
    {
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
        var lined = job.Titles.Count > 1 ? DiscTitle.LineUpSubtitles(job.Titles, sources) : null;
        for (var i = 0; i < job.Titles.Count; i++)
        {
            var playlist = MoviePlaylist + i;
            // later titles are muxed on their own (playlist/clip numbers offset), then merged into the first disc folder
            var dest = i == 0 ? job.OutDir : Path.Combine(work, "mux" + playlist);
            if (i > 0 && Directory.Exists(dest)) Directory.Delete(dest, true);
            muxed.Add(MuxTitle(job, job.Titles[i], sources[i], lined?[i], playlist, dest, work, kbps, log));
            if (i > 0) Merge(dest, job.OutDir);
        }
        if (intro != null)
        {
            var introDest = Path.Combine(work, "mux" + IntroPlaylist);
            MuxTitle(job, intro, introSource!, null, IntroPlaylist, introDest, work, kbps, log);
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
            var (listed, forced) = ForcedTracks(muxed[0].AudioLangs, muxed[0].SubFlags);
            if (forced.Count > 0) log($"menu: {forced.Count} forced subtitle track(s) shown with their audio while subtitles are off, not listed");
            var content = new MenuContent(MoviePlaylist, Thumb("preview", 0, sources[0].Duration * 0.1, 800, 450),
                series ? [] : muxed[0].ChapterTimes.Select((t, i) => Thumb($"c{i + 1}", 0, t + 3, 480, 270)).ToList(),
                series ? Enumerable.Range(0, mainCount).Select(i => Entry(i, $"t{i + 1}")).ToList() : null,
                muxed[0].Audio, muxed[0].Subtitles.Take(listed).ToList(),
                mainCount < job.Titles.Count ? Enumerable.Range(mainCount, job.Titles.Count - mainCount).Select(i => Entry(i, $"b{i - mainCount + 1}")).ToList() : null)
                { ChapterNames = series ? null : job.Titles[0].ChapterNames(sources[0].Duration, job.ChapterMinutes) };
            var menuDir = Path.Combine(work, "menu");
            var loop = 0;
            var moving = job.Menu.Loop;
            if (!job.HdmvMenu && (moving || job.Menu.ExistingMusic() != null)) { MuxLoop(job, work, log); loop = LoopPlaylist; } // a native menu makes a clip per page
            var menuPages = MenuBuilder.Build(job.Menu, content);
            if (MenuBuilder.TracksLeftOut(menuPages, content) is { } leftOut) log(leftOut);
            var popup = job.Menu.Popup && !job.HdmvMenu ? MenuBuilder.Popup(job.Menu, content, muxed.Select((m, i) => (MoviePlaylist + i, m.ChapterTimes.Count)).ToList()) : null;
            if (popup != null) log($"menu: pop-up menu on {job.Titles.Count} title(s)");
            // the BD-J graphics plane is 1920x1080 on a 4K disc too (the player scales it up): 3840x2160 pages ran the xlet out of heap
            if (job.HdmvMenu)
            {
                HdmvMenu.Add(job, menuPages, mainCount, muxed[0].DefaultSubtitle <= listed ? muxed[0].DefaultSubtitle : 0, forced, intro != null, work, log);
                return;
            }
            MenuFiles.WriteBdj(job.Menu, menuPages, menuDir, loop, Math.Min(1, VideoSize(job).H / 1080.0), job.Playback ?? new(), mainCount, overlay: moving && loop > 0, defaultSubtitle: muxed[0].DefaultSubtitle <= listed ? muxed[0].DefaultSubtitle : 0, popup: popup, forced: forced); // the xlet chains only the main titles
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
    public static List<string> PreviewMenu(BluRayJob job, string dir, Action<string>? log = null)
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
        var files = MenuRenderer.Sta(() => pages.Select(p =>
        {
            var file = Path.Combine(dir, $"menu_{p.Id}.jpg");
            File.WriteAllBytes(file, MenuRenderer.Background(job.Menu, p));
            return file;
        }).ToList());
        foreach (var f in files) log($"preview: {f}");
        return files;
    }

    /// <summary>The rate the disc is made at: the one chosen in the settings, else the source's (<see cref="DiscRate"/> turns either into one a disc carries).</summary>
    static double Fps(BluRayJob job, SourceInfo s) => job.Encode?.Fps ?? s.Fps;

    internal static (string Ffmpeg, string TsMuxer) DiscRate(double fps, int height = 1080)
    {
        bool pal = Math.Abs(fps - 25) < 0.5 || Math.Abs(fps - 50) < 0.5, ntsc = Math.Abs(fps - 30) < 0.5 || Math.Abs(fps - 60) < 0.5;
        var doubled = height == 720 || height == 2160 && fps > 40;
        return pal ? doubled ? ("50", "50") : ("25", "25")
             : ntsc ? doubled ? ("60000/1001", "59.94") : ("30000/1001", "29.97")
             : Math.Abs(fps - 24) < 0.01 ? ("24", "24") : ("24000/1001", "23.976");
    }

    /// <summary>Whether this PC's ffmpeg can encode H.264 on an NVIDIA card: listed is not enough (the encoder is always built in), a one-frame test says it.</summary>
    public static readonly Lazy<bool> Nvenc = new(() =>
    {
        try { Tools.Run(Tools.Find("ffmpeg.exe"), ["-v", "error", "-f", "lavfi", "-i", "color=s=640x360:d=0.1", "-c:v", "h264_nvenc", "-f", "null", "-"]); return true; }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException) { return false; }
    }, LazyThreadSafetyMode.PublicationOnly); // a test cancelled with its build is asked again, not kept as the answer of every later build

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
    static int AudioKbps(AudioTrack a, DiscTitle t, BluRayJob job) => AudioMode(a, t, job.Uhd, job.Encode?.Lossless == true) switch
    {
        "copy" => a.Kbps > 0 ? a.Kbps : a.Codec switch { "dts" => 1536, _ => a.Channels > 2 ? 640 : 448 }, // what the file says it weighs
        "thd" => (a.Kbps > 0 ? a.Kbps : 4500) + 640, // with its AC-3 core
        "lpcm" => LpcmRate(a) / 1000 * Math.Min(a.Channels + a.Channels % 2, 8) * LpcmBits(a), // a disc stores its channels in pairs
        _ => a.Channels > 2 ? 640 : 448, // re-encoded as AC-3 (volume change, later start)
    };

    static double AudioAndMenuBytes(BluRayJob job, IReadOnlyList<SourceInfo> sources) =>
        job.Titles.Select((t, i) => t.KeptAudio(sources[i]).Sum(a => AudioKbps(a, t, job) * 125.0 * sources[i].Duration)).Sum() + MenuBytes(job);

    /// <summary>Longest moving menu: a whole song (Bright's is 3 min 57), its picture fits a BD-25 beside the film (5 min of 4K = 1.5 GB).</summary>
    internal const int MaxLoopSeconds = 300;

    static double MenuBytes(BluRayJob job) =>
        0.15e9 + (job.Menu is { } m ? LoopLength(m, MaxLoopSeconds) * (m.Loop && !job.HdmvMenu ? (job.Uhd ? 5e6 : 2.6e6) 
                                                         // HDMV: every page is a clip as long as the music (0.3 MB/s measured); pages guessed, four titles a page
                                                         : m.ExistingMusic() == null ? 0 : job.HdmvMenu ? 0.3e6 * (3 + job.Titles.Count / 4) : 0.1e6) : 0); // a moving clip, or a black one with music

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
        var asIs = FilesBytes(job, sources);
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
    static Muxed MuxTitle(BluRayJob job, DiscTitle title, SourceInfo src, IReadOnlyList<SubtitleTrack>? lined, int playlist, string dest, string work, int kbps, Action<string> log)
    {
        var audio = title.KeptAudio(src);
        // MPEG-TS, not Matroska: from an mkv tsMuxeR finds no SPS/PPS before the keyframes and writes ONE entry point in the clip info,
        // so a player cannot seek or jump to a chapter; from a TS it writes one per keyframe
        var prepared = Path.Combine(work, $"prepared{playlist}.ts");

        // subtitles the disc can carry: PGS is copied, text becomes SRT (tsMuxeR renders it); other image formats would need OCR
        var subs = Subtitles(title, src, lined, n => Path.Combine(work, $"sub{playlist}_{n}"), log);
        log($"prepare {playlist}: {src.VideoCodec} {src.Width}x{src.Height} {src.Fps:0.###}fps, audio {(audio.Count == 0 ? "none" : string.Join('+', audio.Select(a => a.Codec)))}, subtitles {subs.Count}");
        // a 3D Blu-ray film (MVC) keeps its right eye only as it is: no encoder makes MVC, and the two eyes must stay picture for picture
        var ssif = src.Stereo == "mvc" ? Ssif(title.Video) : null;
        var mvc = ssif != null && !job.Uhd && title.End == null && CopiesVideo(job, title, src, kbps);
        if (ssif != null && !mvc) log($"{title.DisplayName}: 3D lost, the disc gets the left eye: a 3D Blu-ray film stays 3D only copied whole as it is (no cut, effect or re-encode, not on a 4K disc)");
        Prepare(job, title, src, audio, [.. subs.Where(x => x.Track.Index >= 0)], prepared, kbps, log, noVideo: mvc);
        // a subtitle with nothing to show in the part kept is an empty file, which tsMuxeR cannot take
        Blanks(subs, lined != null, file => new FileInfo(file).Length == 0, job.Uhd ? (1920, 1080) : VideoSize(job), log);
        foreach (var (s, file) in subs.Where(x => x.Track.IsPgs && x.Track.File != null))
        {
            // the pictures of a PGS file are placed on a plane of its own size: 1920x1080 on a Blu-ray, also on a 4K one
            var (planeW, planeH) = job.Uhd ? (1920, 1080) : VideoSize(job);
            if (PgsSubtitles.PlaneSize(File.ReadAllBytes(file)) is { } plane && plane != (planeW, planeH))
                log($"warning: subtitle '{s.Name}' is made for a {plane.Width}x{plane.Height} screen, the disc is {planeW}x{planeH}: its pictures may not sit where they were meant to");
        }

        var fps = DiscRate(mvc ? src.Fps : Fps(job, src), VideoSize(job).H).TsMuxer; // a 3D film is copied: its own rate
        var chapterTimes = title.ChapterList(src.Duration, job.ChapterMinutes);
        var chapters = string.Join(';', chapterTimes.Select(t => TimeSpan.FromSeconds(t).ToString(@"hh\:mm\:ss\.fff", Inv)));
        var (w, h) = VideoSize(job);
        var lines = new List<string>
        {
            // PCR on its own PID, and for a copied H.264 the SEI / SPS a player needs added where missing: what Leawo's and Yuhan's tsMuxeR lines also ask
            // a 3D film takes two clips (left eye N, right eye N+1): numbered apart so that the next title's clip does not land on its right eye
            // --new-audio-pes: the sound in the packets a pressed Blu-ray carries it in (stream id 0xFD with its extension, read on a commercial disc; tsMuxeR writes
            // a plain 0xBD otherwise), which is also what tells a TrueHD from its AC-3 core
            $"MUXOPT {(job.Uhd ? "--blu-ray-v3" : "--blu-ray")} --label={job.Label} --vbr --no-pcr-on-video-pid --new-audio-pes --custom-chapters={chapters} --mplsOffset={playlist} --m2tsOffset={(mvc ? 200 + 2 * playlist : playlist)}",
        };
        var planes = new List<int>(); // by subtitle track: the depth a 3D film shows it at, -1 = on the screen
        var shifts = new List<int>(); // by audio track: how many ms it starts before (-) or after the picture in the source
        if (mvc)
        {
            // both eyes from the .ssif, so each right-eye picture pairs with its left one; the sound comes from prepared.ts. tsMuxeR starts every track with the
            // picture, so the sound gets back its own start in the source (a film cut from a disc may begin with sound a frame or more before its first picture)
            lines.Add($"V_MPEG4/ISO/AVC, \"{ssif}\", insertSEI, contSPS, fps={fps}, track=4113");
            lines.Add($"V_MPEG4/ISO/MVC, \"{ssif}\", fps={fps}, track=4114");
            var tracks = PlaylistTracks(title.Video);
            var streams = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "stream=codec_type,id,start_time", "-of", "csv=p=0", title.Video])
                .Split('\n').Select(l => l.Trim().Split(',')).Where(f => f.Length == 3).ToList();
            double Start(string[] f) => double.TryParse(f[2], NumberStyles.Float, Inv, out var t) ? t : 0;
            var picture = streams.FirstOrDefault(f => f[0] == "video") is { } v ? Start(v) : 0;
            shifts = [.. streams.Where(f => f[0] == "audio").Select(f => (int)Math.Round((Start(f) - picture) * 1000))];
            planes = [.. streams.Where(f => f[0] == "subtitle").Select(f => tracks.TryGetValue(Pid(f[1]), out var t) ? t.Plane : -1)];
            log($"{title.DisplayName}: 3D kept (Blu-ray 3D, left eye and right eye){(planes.Any(p => p >= 0) ? ", subtitles at their depth" : "")}");
        }
        else if (job.Uhd && src.DualLayer && CopiesVideo(job, title, src, kbps))
        {
            // Dolby Vision profile 7 as a UHD disc carries it: the HDR10 base and the enhancement layer (with the Dolby Vision data) in two tracks, which tsMuxeR puts on their own PIDs
            var (raw, bl, el) = (Path.Combine(work, $"dv{playlist}.hevc"), Path.Combine(work, $"bl{playlist}.hevc"), Path.Combine(work, $"el{playlist}.hevc"));
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-i", prepared, "-map", "0:v:0", "-c", "copy", "-f", "hevc", raw], log);
            Tools.Run(Tools.Find("dovi_tool.exe"), ["demux", raw, "-b", bl, "-e", el], log);
            File.Delete(raw);
            lines.Add($"V_MPEGH/ISO/HEVC, \"{bl}\", fps={fps}");
            lines.Add($"V_MPEGH/ISO/HEVC, \"{el}\", fps={fps}");
            log($"{title.DisplayName}: Dolby Vision kept (profile 7, base and enhancement layer)");
        }
        else
        {
            // a copied MPEG-2 (an early Blu-ray's film) stays MPEG-2: a Blu-ray carries it as well as H.264
            var mpeg2 = !job.Uhd && src.PlayerReadyMpeg2 && CopiesVideo(job, title, src, kbps);
            lines.Add($"{(job.Uhd ? "V_MPEGH/ISO/HEVC" : mpeg2 ? "V_MPEG-2" : "V_MPEG4/ISO/AVC")}, \"{prepared}\", {(job.Uhd || mpeg2 ? "" : "insertSEI, contSPS, ")}fps={fps}, track=256");
            if (mpeg2) log($"{title.DisplayName}: MPEG-2 copied as it is");
            // a re-encoded Dolby Vision title: its metadata on an enhancement layer made to match the new base (profile 7 MEL)
            if (job.Uhd && File.Exists(Path.ChangeExtension(prepared, ".rpu.bin")) && DolbyLayer(prepared, Path.ChangeExtension(prepared, ".rpu.bin"), DiscRate(Fps(job, src), h).Ffmpeg, log) is { } el)
            {
                lines.Add($"V_MPEGH/ISO/HEVC, \"{el}\", fps={fps}");
                log($"{title.DisplayName}: Dolby Vision kept (re-encoded, profile 7 MEL)");
            }
        }
        for (var j = 0; j < audio.Count; j++)
        {
            // the track's own start in the source (3D) and the delay asked for it: tsMuxeR moves the sound, whatever it is made of
            var shift = (audio[j].Index < shifts.Count ? shifts[audio[j].Index] : 0) + (int)Math.Round((title.AudioDelay?.GetValueOrDefault(audio[j].Index) ?? 0) * 1000);
            lines.Add(AudioMode(audio[j], title, job.Uhd, job.Encode?.Lossless == true) switch
            {
                "thd" => $"A_AC3, \"{AudioFile(prepared, j, "hd.ac3")}\"", // tsMuxeR finds the TrueHD between the AC-3 frames
                "lpcm" => $"A_LPCM, \"{AudioFile(prepared, j, "w64")}\"",
                _ => $"{AudioTag(BdAudio(audio[j], title, job.Uhd))}, \"{prepared}\", track={(mvc ? 256 : 257) + j}", // without picture, ffmpeg numbers the sound from PID 256
            } + $", lang={Languages.Iso2(audio[j].Lang)}" + (shift != 0 ? $", timeshift={shift}ms" : ""));
        }
        foreach (var (s, file) in subs)
            // no fps on a picture track: given one, tsMuxeR re-times the pictures by (rate written in the track) / fps, and a track made here says 23.976
            // whatever the video is, so on a 25 fps title every line came 4 % early (70 s at the end of a 28-minute bonus)
            lines.Add(s.IsPicture
                ? $"S_HDMV/PGS, \"{file}\", lang={Languages.Iso2(s.Lang)}" + (s.Index >= 0 && s.File == null && s.Index < planes.Count && planes[s.Index] >= 0 ? $", 3d-plane={planes[s.Index]}" : "")
                : $"S_TEXT/UTF8, \"{file}\", {TextSubtitleLook(job.Subtitles, h)}, font-border=5, text-align=center, video-width={w}, video-height={h}, fps={fps}, lang={Languages.Iso2(s.Lang)}");
        var meta = Path.Combine(work, $"disc{playlist}.meta");
        File.WriteAllLines(meta, lines);
        log($"mux {playlist}: tsMuxeR");
        try { Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, dest], log); }
        catch (InvalidOperationException) { foreach (var line in lines) log("meta: " + line); throw; } // the work folder goes with the failure: what was asked stays in the log
        // a 3D title's prepared.ts holds no picture to take thumbnails from
        return new Muxed(mvc ? title.Video : prepared, audio.Select(a => a.Name).ToList(), subs.Select(s => s.Track.Name).ToList(), chapterTimes, title.DefaultSubtitleNumber(subs.Select(s => s.Track).ToList()),
                         audio.Select(a => a.Lang).ToList(), subs.Select(s => (s.Track.Lang, s.Track.Forced)).ToList());
    }

    /// <summary>
    /// The subtitles a title carries, <paramref name="lined"/> when given, else its supported kept ones, each with the file it is written to (<paramref name="file"/>
    /// of its number, then .sup for pictures, .srt for text). Another image format (DVB, Teletext) would need OCR: left out.
    /// </summary>
    internal static List<(SubtitleTrack Track, string File)> Subtitles(DiscTitle title, SourceInfo src, IReadOnlyList<SubtitleTrack>? lined, Func<int, string> file, Action<string> log)
    {
        foreach (var s in title.KeptSubtitles(src).Where(t => !t.Supported)) log($"subtitle '{s.Name}' skipped: {s.Codec} is an image format that needs OCR");
        return [.. (lined ?? [.. title.KeptSubtitles(src).Where(t => t.Supported)]).Select((s, k) => (Track: s, File: $"{file(k + 1)}.{(s.IsPicture ? "sup" : "srt")}"))];
    }

    /// <summary>
    /// The subtitles that show nothing (<paramref name="empty"/> once written, or a track the title lacks: <see cref="SubtitleTrack.Index"/> -1). A title on its own
    /// leaves them out, a choice that would show nothing; one lined up with others (<paramref name="lined"/>) carries an invisible PGS track there instead,
    /// since the menu and the remote select a subtitle by its number, the same for every title.
    /// </summary>
    internal static void Blanks(List<(SubtitleTrack Track, string File)> subs, bool lined, Func<string, bool> empty, (int Width, int Height) plane, Action<string> log)
    {
        for (var k = 0; k < subs.Count; k++)
        {
            var (s, file) = subs[k];
            if (s.Index >= 0 && !empty(file)) continue;
            if (!lined) { log($"subtitle '{s.Name}' skipped: nothing to show in the part kept"); subs.RemoveAt(k--); continue; }
            log($"subtitle {k + 1} '{s.Name}': {(s.Index < 0 ? "not in this video" : "nothing to show in the part kept")}, an invisible track keeps its number");
            file = Path.ChangeExtension(file, ".sup");
            File.WriteAllBytes(file, PgsSubtitles.Blank(plane.Width, plane.Height));
            subs[k] = (s with { Codec = "hdmv_pgs_subtitle", File = null }, file);
        }
    }

    /// <summary>
    /// The file a moving menu plays, its probe and the second to start at: the design's file, else the first title, which counts
    /// <see cref="MenuDesign.LoopStart"/> from the start of the part kept (a cut-out intro never shows on the menu).
    /// </summary>
    /// A clip shorter than the loop is played again from its start until the loop ends (<c>Input</c> then carries ffmpeg's -stream_loop),
    /// so a 2-minute clip under a 4-minute song repeats instead of stopping half-way.
    internal static (string File, SourceInfo Src, int Start, string[] Input) LoopSource(MenuDesign menu, DiscTitle first, int seconds)
    {
        var file = menu.LoopFile is { Length: > 0 } f && File.Exists(f) ? f : first.Video;
        var src = Probe(file);
        var own = file == first.Video;
        var room = own ? first.Length(src) : src.Duration;
        var start = (own ? (int)first.Start : 0) + Math.Clamp(menu.LoopStart, 0, Math.Max(0, (int)room - seconds));
        var repeat = (own ? first.Start + room : room) - start < seconds - 0.5;
        string[] input = ["-ss", start.ToString(Inv), "-t", seconds.ToString(Inv), "-i", file];
        return (file, src, start, repeat ? ["-stream_loop", "-1", .. input] : input);
    }

    /// <summary>
    /// How long the menu loop lasts: <see cref="MenuDesign.LoopSeconds"/>, or with 0 the music's length from <see cref="MenuDesign.MusicStart"/>
    /// (the whole song, not cut), within 5 s and <paramref name="max"/> (300 on a Blu-ray, 120 on a DVD); when 0 and no music, the length of
    /// the design's own clip, else 30 s.
    /// </summary>
    internal static int LoopLength(MenuDesign menu, int max)
    {
        if (menu.LoopSeconds > 0) return Math.Clamp(menu.LoopSeconds, 5, max);
        // without music, a moving menu with a clip of its own (a template's moving background) lasts as long as that clip
        var music = menu.ExistingMusic() ?? (menu.Loop && menu.LoopFile is { Length: > 0 } clip && File.Exists(clip) ? clip : null);
        var shown = music == null ? "" : Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", music]).Trim();
        var from = menu.ExistingMusic() == null ? menu.LoopStart : menu.MusicStart;
        return double.TryParse(shown, NumberStyles.Float, Inv, out var length) ? Math.Clamp((int)Math.Floor(length - Math.Max(0, from)), 5, max) : Math.Min(30, max);
    }

    /// <summary>
    /// The menu music as one file any encoder takes: from <see cref="MenuDesign.MusicStart"/>, repeated to fill <paramref name="seconds"/>, at
    /// <see cref="MenuDesign.MusicVolume"/> %, faded in and out by <see cref="MenuDesign.MusicFade"/> seconds, 48 kHz stereo (a disc does not take 44.1 kHz).
    /// </summary>
    internal static string PrepareMusic(MenuDesign menu, string music, int seconds, string work, Action<string> log)
    {
        const double MusicLufs = -27; // integrated loudness of a film's soundtrack on disc, roughly: the menu should not shout over it
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
        // a song is mastered far louder than a film (Bright: menu song -11 LUFS, film -25): one linear gain down to a film's level, never up (no clipping)
        double? loudness = null;
        Tools.Run(ffmpeg, ["-hide_banner", "-nostats", "-i", part, "-af", "ebur128", "-f", "null", "-"],
            l => { if (Regex.Match(l, @"^\s*I:\s+(-?\d+(?:\.\d+)?) LUFS") is { Success: true } m) loudness = double.Parse(m.Groups[1].Value, Inv); });
        var gain = loudness is double lufs && lufs > MusicLufs ? Math.Pow(10, (MusicLufs - lufs) / 20) : 1;
        if (gain < 1) log($"menu music: {loudness:0.0} LUFS brought down to {MusicLufs} LUFS, a film's loudness");
        var filter = $"volume={(gain * Math.Clamp(menu.MusicVolume, 0, 300) / 100.0).ToString("0.#####", Inv)}"
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
        var seconds = LoopLength(menu, MaxLoopSeconds);
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
            var (file, probe, start, input) = LoopSource(menu, job.Titles[0], seconds);
            src = probe;
            (fps, declared) = DiscRate(Fps(job, src), h);
            tone = !job.Uhd && ToSdr(src.Hdr, src.Width, src.Height, w) is { Length: > 0 } sdr ? sdr + "," : ""; // a moving menu taken from an HDR film; a 4K disc keeps its HDR, like the film
            log($"menu: moving background, {seconds}s from {Path.GetFileName(file)} at {start}s{(music != null ? " + music" : "")}");
            if (input[0] == "-stream_loop") log("menu: the clip is shorter than the loop, it plays again from its start");
            args.AddRange(input);
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
            "-vf", tone + (src?.SourceView ?? "") + Fit(w, h) + (src is { } loop ? HdMatrix(loop) : ""), "-r", fps]);
        args.AddRange(MenuCodec(job.Uhd, src?.Hdr ?? "SDR", fps, h));
        if (hasAudio) args.AddRange(["-c:a", "ac3", "-b:a", "448k", "-ac", "2", "-ar", "48000"]); // the first title's own sound can be anything
        args.AddRange(["-sn", "-dn", ts]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);

        var meta = Path.Combine(work, "loop.meta");
        var lines = new List<string>
        {
            $"MUXOPT {(job.Uhd ? "--blu-ray-v3" : "--blu-ray")} --label={job.Label} --vbr --no-pcr-on-video-pid --new-audio-pes --mplsOffset={LoopPlaylist} --m2tsOffset={LoopPlaylist}",
            job.Uhd ? $"V_MPEGH/ISO/HEVC, \"{ts}\", fps={declared}, track=256" : $"V_MPEG4/ISO/AVC, \"{ts}\", fps={declared}, track=256",
        };
        if (hasAudio) lines.Add($"A_AC3, \"{ts}\", track=257");
        File.WriteAllLines(meta, lines);
        var dest = Path.Combine(work, "mux" + LoopPlaylist);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, dest], log);
        Merge(dest, job.OutDir);
    }

    /// <summary>How the video of a menu clip is encoded: 10-bit HEVC like a 4K film (see Prepare; only the base layer of a Dolby Vision film is taken, as HDR10), else H.264 within a Blu-ray's limits.</summary>
    internal static string[] MenuCodec(bool uhd, string hdr, string fps, int h) => uhd
        ? ["-c:v", "libx265", "-preset", "fast", "-crf", "20", "-pix_fmt", "yuv420p10le", "-dolbyvision", "0",
           "-x265-params", $"log-level=error:{X265Colors(hdr)}:uhd-bd=1:repeat-headers=1:aud=1:keyint=24:vbv-maxrate=40000:vbv-bufsize=40000"]
        : ["-c:v", "libx264", "-preset", "fast", "-crf", "19", "-pix_fmt", "yuv420p",
           "-x264-params", "bluray-compat=1:level=41:vbv-maxrate=20000:vbv-bufsize=20000:keyint=24:slices=4:bframes=3:b-pyramid=strict:open-gop=0" + Fields(fps, h)];

    /// <summary>Moves the playlist, clip info and stream files of a title muxed apart into the disc folder.</summary>
    internal static void Merge(string from, string into)
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
        "dts" => "A_DTS", _ => "A_AC3",
    };

    /// <summary>The codec a source audio ends up as on the disc: copied when a Blu-ray carries it and the volume stays, AC-3 otherwise; with "Son sans perte", TrueHD or LPCM.</summary>
    public static string BdAudio(AudioTrack a, DiscTitle t, bool uhd, bool lossless = false) => AudioMode(a, t, uhd, lossless) switch { "copy" => a.Codec, "thd" => "truehd", "lpcm" => "lpcm", _ => "ac3" };

    /// <summary>
    /// How a track goes on the disc: "copy" (as it is: AC-3, DTS and DTS-HD), "ac3" (made again), and with "Son sans perte" (<paramref name="lossless"/>) for a track
    /// without loss, "thd" (TrueHD kept as it is, an AC-3 core made beside it) or "lpcm" (decoded: FLAC, PCM, a TrueHD or DTS-HD that is cut or made louder).
    /// </summary>
    static string AudioMode(AudioTrack a, DiscTitle t, bool uhd, bool lossless) =>
        CopiesAudio(a, t, uhd) ? "copy" : !lossless || !a.Lossless ? "ac3"
        // 48 kHz only: tsMuxeR counts a TrueHD's samples against the core's 1536 at 48 kHz
        : a.Codec == "truehd" && a.SampleRate == 48000 && t.Volume == 100 && t.Start == 0 && t.Gaps.Count == 0 ? "thd" : "lpcm";

    /// <summary>A lossless track's file beside prepared.ts: tsMuxeR reads neither TrueHD nor LPCM out of ffmpeg's transport stream.</summary>
    static string AudioFile(string prepared, int track, string kind) => Path.ChangeExtension(prepared, $".a{track}.{kind}");

    /// <summary>A disc's LPCM is 48, 96 or 192 kHz, at 16 or 24 bits.</summary>
    static int LpcmRate(AudioTrack a) => a.SampleRate is 96000 or 192000 ? a.SampleRate : 48000;
    static int LpcmBits(AudioTrack a) => a.Bits is > 0 and <= 16 ? 16 : 24;

    /// <summary>
    /// A TrueHD track as a Blu-ray carries it: each AC-3 frame of its core (1536 samples) followed by the TrueHD access units of the same time (40 samples each
    /// at 48 kHz). tsMuxeR reads that raw file as one "A_AC3" track and finds the TrueHD in it.
    /// </summary>
    public static void InterleaveTrueHd(string trueHd, string core, int coreKbps, string output)
    {
        using var hd = File.OpenRead(trueHd);
        using var ac3 = File.OpenRead(core);
        using var into = File.Create(output);
        var frame = new byte[coreKbps * 4]; // an AC-3 frame at 48 kHz: 32 ms of its bit rate
        var unit = new byte[8192];
        for (long samples = 0, frames = 0; ;)
        {
            if (samples >= frames * 1536 && ac3.ReadAtLeast(frame, frame.Length, false) == frame.Length)
            {
                if (frame[0] != 0x0B || frame[1] != 0x77) throw new InvalidDataException("TrueHD: the AC-3 core is not a run of whole frames");
                into.Write(frame);
                frames++;
            }
            if (hd.ReadAtLeast(unit.AsSpan(0, 2), 2, false) < 2) break;
            var size = ((unit[0] & 0x0F) << 8 | unit[1]) * 2; // an access unit starts with its length, in 16-bit words
            if (size < 4 || hd.ReadAtLeast(unit.AsSpan(2, size - 2), size - 2, false) < size - 2) break;
            into.Write(unit, 0, size);
            samples += 40;
        }
    }

    /// <summary>
    /// A copied audio track keeps everything from the keyframe before a cut's start (stream copy does not drop it), while the re-encoded video begins on the exact frame: sound
    /// a second early. So a title that starts later has its sound re-encoded too (decoded, then cut exactly); a 4K disc copies both from the same keyframe and stays in step.
    /// Only 48 kHz is copied: a disc does not take 44.1 kHz.
    /// </summary>
    const string OverlapDrop = "noise=drop='if(lt(pts+duration/2,ld(0)),1,0*st(0,pts+duration))'"; // ld(0): where the last frame kept ends
    static bool CopiesAudio(AudioTrack a, DiscTitle t, bool uhd) => t.Volume == 100 && (t.Start == 0 || uhd) && t.Gaps.Count == 0 && a.SampleRate == 48000 && a.Codec is "ac3" or "dts"; // not TrueHD: a disc wants its AC-3 core, which an mkv or ffmpeg never carries (tsMuxeR: "the disk will not play")

    /// <summary>Whether the title's video goes on the disc as it is (stream copy) instead of being re-encoded; <see cref="TargetKbps"/> and <see cref="Prepare"/> must agree on it.</summary>
    static bool CopiesVideo(BluRayJob job, DiscTitle title, SourceInfo s, int kbps)
    {
        var (w, h) = VideoSize(job);
        var rate = DiscRate(Fps(job, s), h).Ffmpeg;
        var fpsOk = Math.Abs(s.Fps - Value(rate)) < 0.01; // the disc keeps the source's rate as it is (720p has no 25: such a source is made again at 50)
        var enc = job.Encode ?? new EncodeOptions();
        // a cut that starts later must be re-encoded to begin on the exact frame (a copy starts on the keyframe before it); a 4K passthrough copies it anyway
        var copyCut = title.Start == 0 || (job.Uhd && !enc.ForceReencode && kbps == 0);
        // a 4K source is 10-bit HEVC at its own levels: only the H.264 of a Blu-ray has a player's limits to check
        return !enc.ForceReencode && kbps == 0 && copyCut && (job.Uhd ? s.VideoCodec == "hevc" && s.PixFmt is "yuv420p10le" or "yuv420p" && s.Level <= 153 // 4:2:0 up to level 5.1, all a 4K player takes
                      // a file above 48 Mb/s on average, sound included, cannot hold a video within a Blu-ray's 40
                      : (s.PlayerReadyH264 || s.PlayerReadyMpeg2) && s.Hdr == "SDR" && Tools.SizeOf(title.Video) * 8.0 / Math.Max(1, s.FileDuration > 0 ? s.FileDuration : s.Duration) <= 48e6)
               && s.Width == w && s.Height == h && fpsOk && !s.Rotated
               && title.Effects is null or { IsEmpty: true } // effects are drawn into the picture, so it is made again
               && title.Gaps.Count == 0 // several clips are joined frame by frame
               // 1080 at 25 or 29.97 exists on a Blu-ray only as fields: a progressive stream is made again with the flags (last: it reads the file)
               && (Fields(rate, h).Length == 0 || FlaggedAsFields(title.Video));
    }

    /// <summary>
    /// Remuxes to prepared.ts (ffmpeg numbers the streams from PID 256: video = 256, kept audio = 257..), re-encoding only what a Blu-ray cannot carry,
    /// and writes the subtitle files in the same pass over the source.
    /// </summary>
    static void Prepare(BluRayJob job, DiscTitle title, SourceInfo s, IReadOnlyList<AudioTrack> audio, List<(SubtitleTrack Track, string File)> subs, string output, int kbps, Action<string> log,
                        bool noVideo = false) // noVideo: the sound and subtitles only (a 3D film's picture is muxed from the source itself)
    {
        var (w, h) = VideoSize(job);
        var enc = job.Encode ?? new EncodeOptions();
        var videoOk = CopiesVideo(job, title, s, kbps);
        if (job.Uhd && videoOk && title.Start > 0) log($"{title.DisplayName}: 4K is not re-encoded, so the cut starts on the keyframe before {Timecode.Format(title.Start)}");

        // -ss / -t before -i cut the input itself: the video, the sound and the subtitle files written below all start and stop together
        File.Delete(Path.ChangeExtension(output, ".rpu.bin")); // its presence tells MuxTitle to add the Dolby Vision layer
        var args = new List<string> { "-y", "-v", "warning" };
        if (title.Start > 0) args.AddRange(["-ss", title.Start.ToString(Inv)]);
        var span = s.Duration + title.GapLength; // the part read: from the first clip's start to the last one's end
        if (title.End != null) args.AddRange(["-t", span.ToString("0.###", Inv)]);
        args.AddRange(["-i", title.Video]);
        if (!noVideo) args.AddRange(["-map", "0:v:0"]);
        foreach (var a in audio) args.AddRange(["-map", $"0:a:{a.Index}"]);
        if (videoOk && !noVideo) args.AddRange(["-c:v", "copy"]);
        // a UHD disc takes Dolby Vision only as two tracks (profile 7): tsMuxeR puts a single-layer one on the enhancement-layer PID, with no base, and no player reads it
        if (videoOk && job.Uhd && s.Hdr == "Dolby Vision" && !s.DualLayer)
        {
            args.AddRange(["-bsf:v", "dovi_rpu=strip=1"]);
            log($"{title.DisplayName}: Dolby Vision without its enhancement layer (profile 5 or 8): copied, the disc keeps its HDR10 base only; 4K Conversion keeps it (profile 7 MEL)");
        }
        // effects are placed in the source's pixels: with some, the picture keeps its size until they are drawn
        var tone = job.Uhd ? "" : title.Effects is null or { IsEmpty: true } ? ToSdr(s.Hdr, s.Width, s.Height, w) : ToSdr(s.Hdr);
        var rate = DiscRate(Fps(job, s), h).Ffmpeg;
        // the NVIDIA card encodes several times faster (what DVDFab does). The processor only when the card cannot: 25i/29.97i fields (x264 writes them),
        // a 4K HDR10+ or Dolby Vision film (x265 carries their frame-by-frame metadata), or no NVIDIA card
        var cpuOnly = Fields(rate, h).Length > 0 ? "interlaced output" : job.Uhd && s.Hdr is "HDR10+" or "Dolby Vision" ? s.Hdr + " metadata" : !Nvenc.Value ? "no NVIDIA encoder" : null;
        var gpu = !videoOk && enc.Gpu && cpuOnly == null;
        if (!videoOk && enc.Gpu && !gpu) log($"{title.DisplayName}: encoded by the processor ({cpuOnly})");
        if (gpu) args.InsertRange(args.IndexOf("-i"), ["-hwaccel", "auto"]); // decoded by the card too (ffmpeg falls back to the processor by itself)
        if (!videoOk) args.AddRange(["-vf", (title.Effects ?? new()).Vf(s.Width, s.Height, title.Start, s.HalfStereo + Fit(w, h) + HdMatrix(s), tone) + (title.Gaps.Count > 0 ? "," + title.GapFilter(false) : ""),
                            "-r", rate, "-pix_fmt", !job.Uhd ? "yuv420p" : gpu ? "p010le" : "yuv420p10le"]);
        if (!videoOk) args.AddRange(gpu ? ["-c:v", job.Uhd ? "hevc_nvenc" : "h264_nvenc"] : ["-c:v", job.Uhd ? "libx265" : "libx264", "-preset", enc.Preset]);
        if (gpu)
        {
            // the limits x264's bluray-compat / x265's uhd-bd set below: level 4.1 (5.1 high tier), 1 s GOP, 3 B-frames without pyramid, access unit delimiters; two passes for an accurate size
            var maxrate = job.Uhd ? (kbps > 0 ? Math.Clamp(kbps + kbps / 2, 40000, 100000) : 100000) : kbps > 0 ? Math.Clamp(kbps + kbps / 4, 30000, 40000) : 30000;
            args.AddRange(["-preset", "p6", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres", "-spatial-aq", "1",
                           "-g", "24", "-bf", "3", "-b_ref_mode", "disabled", "-aud", "1", "-maxrate", maxrate + "k", "-bufsize", (job.Uhd ? maxrate : 30000) + "k"]);
            args.AddRange(job.Uhd ? ["-profile:v", "main10", "-level", "5.1", "-tier", "high"] : ["-profile:v", "high", "-level", "4.1", "-slices", "4"]);
            if (job.Uhd) // the colours named as for x265 (the scaler drops them); the HDR10 mastering and light levels come from the frames
            {
                var (prim, trc, matrix) = s.Hdr switch { "HDR10" => ("bt2020", "smpte2084", "bt2020nc"), "HLG" => ("bt2020", "arib-std-b67", "bt2020nc"), _ => ("bt709", "bt709", "bt709") };
                args.AddRange(["-color_primaries", prim, "-color_trc", trc, "-colorspace", matrix]);
            }
            args.AddRange(kbps > 0 ? ["-b:v", kbps.ToString(Inv) + "k"] : ["-cq", Math.Clamp(enc.Crf, 10, 28).ToString(Inv), "-b:v", "0"]);
        }
        else if (!videoOk && job.Uhd)
        {
            // 10-bit HEVC at level 5.1, 100 Mb/s at most. The colours are named here: ffmpeg does not pass them on through the scaler; the HDR10 mastering
            // and light levels come from the source's frames. Dolby Vision is kept as profile 7 MEL, an enhancement layer made apart (DolbyLayer), so the base
            // is held to a fixed structure; left to itself ffmpeg would write it single-layer, which a UHD disc cannot carry (see the copy above)
            var colors = X265Colors(s.Hdr);
            var maxrate = kbps > 0 ? Math.Clamp(kbps + kbps / 2, 40000, 100000) : 100000;
            args.AddRange(kbps > 0 ? ["-b:v", kbps.ToString(Inv) + "k"] : ["-crf", Math.Clamp(enc.Crf, 10, 28).ToString(Inv)]);
            var plus = s.Hdr == "HDR10+" ? Hdr10Plus(title, s, rate, Path.ChangeExtension(output, ".hdr10plus.json"), log) : null;
            var dv = s.Hdr == "Dolby Vision" ? DolbyVisionRpu(title, s, rate, Path.ChangeExtension(output, ".rpu.bin"), log) : null;
            // dhdr10-info: a folder named L'été must not close the quote
            args.AddRange(["-dolbyvision", "0", "-x265-params", $"log-level=error:{colors}:uhd-bd=1:repeat-headers=1:aud=1:keyint=24:vbv-maxrate={maxrate}:vbv-bufsize={maxrate}"
                           + (plus != null ? $":dhdr10-info='{plus.Replace('\\', '/').Replace("'", @"'\''")}'" : "") + (dv != null ? ":" + FixedGop : "")]);
        }
        else if (!videoOk)
        {
            // a rate asked for or fitted to the disc: average bit rate with a ceiling; else constant quality. 40 Mb/s is the level 4.1 limit
            var maxrate = kbps > 0 ? Math.Clamp(kbps + kbps / 4, 30000, 40000) : 30000;
            args.AddRange(kbps > 0 ? ["-b:v", kbps.ToString(Inv) + "k"] : ["-crf", Math.Clamp(enc.Crf, 10, 28).ToString(Inv)]);
            args.AddRange(["-x264-params", $"bluray-compat=1:level=41:vbv-maxrate={maxrate}:vbv-bufsize=30000:keyint=24:slices=4:bframes=3:b-pyramid=strict:open-gop=0{Fields(rate, h)}"]);
        }
        var af = string.Join(",", new[] { title.GapFilter(true), title.Volume != 100 ? $"volume={(title.Volume / 100.0).ToString("0.###", Inv)}" : "" }.Where(f => f.Length > 0));
        for (var j = 0; j < audio.Count; j++)
        {
            // a DVD rip joins its scenes with sound frames that overlap (the same time written two or three times); tsMuxeR plays every frame one after the
            // other, so the sound fell behind the picture at each join (0.55 s at the end of 13 deleted scenes): a frame that starts before the last one ends is left out
            // shortcut: overlaps under half a frame are kept and add up, and a hole in the sound is not filled; re-encode such a track (aresample=async=1) if one turns up
            if (CopiesAudio(audio[j], title, job.Uhd)) args.AddRange([$"-c:a:{j}", "copy", $"-bsf:a:{j}", OverlapDrop]);
            else
            {
                args.AddRange([$"-c:a:{j}", "ac3", $"-b:a:{j}", audio[j].Channels > 2 ? "640k" : "448k", $"-ar:a:{j}", "48000"]);
                if (audio[j].Channels > 6) args.AddRange([$"-ac:a:{j}", "6"]); // AC-3 tops out at 5.1
                if (af.Length > 0) args.AddRange([$"-filter:a:{j}", af]);
            }
        }
        args.AddRange(["-sn", "-dn", output]);
        // "Son sans perte": each lossless track once more, in a file of its own written in the same pass
        // shortcut: prepared.ts keeps their AC-3 too (unused, about 300 MB for a film) so that the other tracks keep their PIDs; map them out if the work folder's size matters
        for (var j = 0; j < audio.Count; j++)
        {
            var (a, mode) = (audio[j], AudioMode(audio[j], title, job.Uhd, enc.Lossless));
            if (mode == "thd")
            {
                // setts: an mkv times its units to the millisecond, two of them often the same, and ffmpeg warns about each one (their times are not written anyway)
                args.AddRange(["-map", $"0:a:{a.Index}", "-c:a", "copy", "-bsf:a", "setts=ts=N", "-f", "truehd", AudioFile(output, j, "thd")]);
                args.AddRange(["-map", $"0:a:{a.Index}", "-c:a", "ac3", "-b:a", a.Channels > 2 ? "640k" : "448k", "-ar", "48000", "-ac", Math.Min(a.Channels, 6).ToString(Inv), "-f", "ac3", AudioFile(output, j, "ac3")]);
                log($"{title.DisplayName}: {a.Name}: TrueHD kept as it is, with the AC-3 core a disc wants");
            }
            else if (mode == "lpcm")
            {
                // .w64: a film's 24-bit 5.1 passes the 4 GB of a .wav; a disc has no 6.1, so seven channels become 7.1
                args.AddRange(["-map", $"0:a:{a.Index}", "-c:a", LpcmBits(a) == 16 ? "pcm_s16le" : "pcm_s24le", "-ar", LpcmRate(a).ToString(Inv)]);
                if (a.Channels is 7 or > 8) args.AddRange(["-ac", "8"]);
                if (af.Length > 0) args.AddRange(["-af", af]);
                args.Add(AudioFile(output, j, "w64"));
                log($"{title.DisplayName}: {a.Name}: kept without loss, as LPCM {LpcmRate(a) / 1000} kHz {LpcmBits(a)} bits");
            }
        }
        if (!noVideo || audio.Count > 0) // without picture or sound there is nothing to write
            // one more try: a long encode once stopped with exit 255 and no message (a whole season lost) and went through when run again
            try { Tools.Run(Tools.Find("ffmpeg.exe"), args, log); }
            catch (InvalidOperationException e) { log($"conversion failed, trying once more: {e.Message.Split('\n')[0]}"); Tools.Run(Tools.Find("ffmpeg.exe"), args, log); }
        for (var j = 0; j < audio.Count; j++)
            if (AudioMode(audio[j], title, job.Uhd, enc.Lossless) == "thd")
            {
                InterleaveTrueHd(AudioFile(output, j, "thd"), AudioFile(output, j, "ac3"), audio[j].Channels > 2 ? 640 : 448, AudioFile(output, j, "hd.ac3"));
                File.Delete(AudioFile(output, j, "thd"));
                File.Delete(AudioFile(output, j, "ac3"));
            }
        WriteOwnSubtitles(title, subs.Where(x => x.Track.File == null && !x.Track.IsVobSub).ToList(), span, log); // a VobSub is made apart, the subtitle files too
        foreach (var (t, file) in subs.Where(x => x.Track.File != null)) WriteExternalSubtitle(title, t, span, file, job.Uhd ? (1920, 1080) : VideoSize(job), log);
        foreach (var (t, file) in subs.Where(x => x.Track.File == null && x.Track.IsVobSub)) WriteEmbeddedVobSub(title, t, span, file, job.Uhd ? (1920, 1080) : VideoSize(job), log);
        foreach (var (t, file) in subs) { title.Delay(t, file, span); title.CloseGaps(file, span); }
        foreach (var (t, file) in subs.Where(x => !x.Track.IsPicture)) TidySubtitle(file);
    }

    /// <summary>
    /// The HDR10+ metadata of the frames a re-encode keeps (from <see cref="DiscTitle.Start"/>, without the gaps), as the JSON x265 reads; null when it cannot be kept.
    /// x265 takes it frame by frame, so a disc rate other than the source's would put it on the wrong pictures.
    /// </summary>
    static string? Hdr10Plus(DiscTitle title, SourceInfo s, string rate, string json, Action<string> log)
    {
        if (Math.Abs(s.Fps - Value(rate)) >= 0.01) { log($"{title.DisplayName}: HDR10+ dropped, the frame rate changes (HDR10 stays)"); return null; }
        var tool = "";
        var whole = json + ".all.json";
        try { tool = Tools.Find("hdr10plus_tool.exe"); Tools.Run(tool, ["extract", title.Video, "-o", whole]); }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException) { log($"{title.DisplayName}: HDR10+ not read ({e.Message.Split('\n').Last()}), HDR10 stays"); return null; }
        Keep(tool, whole, json, Removed(title, s));
        log($"{title.DisplayName}: HDR10+ kept");
        return json;
    }

    /// <summary>The source frames a re-encode leaves out (before <see cref="DiscTitle.Start"/>, in the gaps), as the "a-b" ranges hdr10plus_tool's and dovi_tool's editors take.</summary>
    static List<string> Removed(DiscTitle title, SourceInfo s)
    {
        int Frame(double t) => (int)Math.Ceiling(t * s.Fps - 0.001); // ffmpeg keeps the frames from this time on
        var first = Frame(title.Start);
        return (first > 0 ? [$"0-{first - 1}"] : new List<string>())
            .Concat(title.Gaps.Select(g => (From: first + Frame(g.From), To: first + Frame(g.To) - 1)).Where(g => g.To >= g.From).Select(g => $"{g.From}-{g.To}")).ToList();
    }

    /// <summary>Writes <paramref name="whole"/>'s metadata less the <paramref name="remove"/> frames to <paramref name="kept"/>, with the tool's editor.</summary>
    static void Keep(string tool, string whole, string kept, List<string> remove)
    {
        if (remove.Count == 0) { File.Move(whole, kept, true); return; }
        File.WriteAllText(kept + ".edit.json", JsonSerializer.Serialize(new { remove }));
        Tools.Run(tool, ["editor", whole, "-j", kept + ".edit.json", "-o", kept]);
    }

    /// <summary>
    /// The structure x265 is held to when a re-encode keeps Dolby Vision: a fixed GOP (no adaptive B frames, no scene cuts), so the enhancement layer
    /// made apart (<see cref="DolbyLayer"/>) has the very same picture types and order as the base, as a player pairs them.
    /// </summary>
    const string FixedGop = "b-adapt=0:scenecut=0:bframes=3:keyint=24:min-keyint=24:open-gop=0";

    /// <summary>
    /// The Dolby Vision metadata of the frames a re-encode keeps, made profile 7 MEL (dovi_tool mode 1: an enhancement layer that adds nothing to the picture),
    /// the form a UHD disc carries; null when it cannot be kept (a frame rate that changes, a profile 5 source whose base is not HDR10).
    /// </summary>
    static string? DolbyVisionRpu(DiscTitle title, SourceInfo s, string rate, string rpu, Action<string> log)
    {
        if (Math.Abs(s.Fps - Value(rate)) >= 0.01) { log($"{title.DisplayName}: Dolby Vision dropped, the frame rate changes (HDR10 stays)"); return null; }
        var tool = "";
        var whole = rpu + ".all.bin";
        try
        {
            tool = Tools.Find("dovi_tool.exe");
            if (s.DvProfile5) { log($"{title.DisplayName}: Dolby Vision profile 5 has no HDR10 base, not kept (its colours come out wrong)"); return null; }
            // dovi_tool reads Matroska and raw HEVC; anything else (a .m2ts, an .mp4) goes through a raw copy of the video first
            var input = Path.GetExtension(title.Video).ToLowerInvariant() is ".mkv" or ".hevc" or ".h265" or ".265" ? title.Video : rpu + ".hevc";
            if (input != title.Video) Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-i", title.Video, "-map", "0:v:0", "-c", "copy", "-f", "hevc", input]);
            try { Tools.Run(tool, ["-m", "1", "extract-rpu", input, "-o", whole]); }
            finally { if (input != title.Video) File.Delete(input); }
        }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException)
        {
            log($"{title.DisplayName}: Dolby Vision not read ({e.Message.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()}), HDR10 stays");
            return null;
        }
        Keep(tool, whole, rpu, Removed(title, s));
        return rpu;
    }

    /// <summary>
    /// The enhancement layer of a re-encoded Dolby Vision title: a black 1920x1080 HEVC with the base's frame count and structure (<see cref="FixedGop"/>),
    /// carrying the profile 7 MEL metadata. tsMuxeR puts it on its own PID next to the base. Returns the file, or null when the frames do not match.
    /// </summary>
    static string? DolbyLayer(string prepared, string rpu, string rate, Action<string> log)
    {
        var ffprobe = Tools.Find("ffprobe.exe");
        var frames = int.Parse(Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", prepared]).Trim().Split('\n')[0].Trim(), Inv);
        var dovi = Tools.Find("dovi_tool.exe");
        var summary = Tools.Run(dovi, ["info", "-i", rpu, "--summary"]);
        var have = int.Parse(System.Text.RegularExpressions.Regex.Match(summary, @"Frames: (\d+)").Groups[1].Value, Inv);
        if (have < frames) { log($"warning: Dolby Vision has {have} frames for {frames} pictures, not kept (HDR10 stays)"); return null; }
        if (have > frames) Keep(dovi, rpu, rpu + ".fit.bin", [$"{frames}-{have - 1}"]); // the end a cut leaves out
        var fitted = have > frames ? rpu + ".fit.bin" : rpu;
        var black = Path.ChangeExtension(rpu, ".el0.hevc");
        var el = Path.ChangeExtension(rpu, ".el.hevc");
        Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-f", "lavfi", "-i", $"color=c=black:s=1920x1080:r={rate}", "-frames:v", frames.ToString(Inv),
            "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-x265-params", $"log-level=error:{FixedGop}:repeat-headers=1:aud=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc", "-f", "hevc", black], log);
        Tools.Run(dovi, ["inject-rpu", "-i", black, "--rpu-in", fitted, "-o", el], log);
        File.Delete(black);
        return el;
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

    internal static bool IsUnicode(byte[] bytes)
    {
        if (bytes.Length >= 2 && (bytes[0], bytes[1]) is (0xFF, 0xFE) or (0xFE, 0xFF)) return true; // UTF-16 byte order mark
        try { new System.Text.UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (System.Text.DecoderFallbackException) { return false; }
    }

    /// <summary>GPR the disc's start sets to 1 and the Top Menu key to 0 before they jump to title 1, the BD-J menu: the xlet starts on the first title only when it reads 1.</summary>
    public const int BdjStartRegister = 109;

    /// <summary>The files of a jar but its manifest and signature (fresh archive: ZipArchiveMode.Update corrupted entries of the prebuilt jar; its manifest gives way to the signed one).</summary>
    static List<(string Name, byte[] Data)> JarFiles(string jar)
    {
        var entries = new List<(string, byte[])>();
        using var z = ZipFile.OpenRead(jar);
        foreach (var e in z.Entries.Where(e => e.Length > 0 && !e.FullName.StartsWith("META-INF/", StringComparison.Ordinal)))
        {
            using var from = e.Open();
            using var bytes = new MemoryStream();
            from.CopyTo(bytes);
            entries.Add((e.FullName, bytes.ToArray()));
        }
        return entries;
    }

    /// <summary>GPR the disc's start sets to the number of the title that holds the menu again, after the videos' titles: a video's title goes back there at its end.</summary>
    public const int BdjMenuTitleRegister = 114;

    /// <summary>Application ID of the xlet in the videos' titles: not the menu's (0x4000), so a player stops one and starts the other at each change of title.</summary>
    public const ushort BdjPlayerAppId = 0x4001;

    /// <summary>Adds the BD-J menu on top of the tsMuxeR disc: jar, BDJOs, and the index.bdmv and movie object that start it (BACKUP copies included).</summary>
    static void AddMenu(BluRayJob job, string menuDir, bool intro, Action<string> log)
    {
        log("menu: BD-J");
        var bdmv = Path.Combine(job.OutDir, "BDMV");
        foreach (var d in new[] { "JAR", "BDJO", "AUXDATA", @"BACKUP\BDJO" }) Directory.CreateDirectory(Path.Combine(bdmv, d));

        var entries = JarFiles(Assets.Find(@"bdj\menu.jar"));
        foreach (var f in Directory.EnumerateFiles(menuDir)) entries.Add((Path.GetFileName(f), File.ReadAllBytes(f)));
        BdjSign.Write(Path.Combine(bdmv, "JAR", "00000.jar"), entries, job.OutDir);
        TitleLayout(bdmv, job.Titles.Count, job.Uhd ? Bdmv.HaviUhd1080 : job.Encode?.Hd720 == true ? Bdmv.HaviHd720 : Bdmv.HaviHd1080, intro);
    }

    /// <summary>
    /// The titles of a BD-J menu disc, as the studios' discs lay them out: the Top Menu is the menu (BDJO 00000), title N plays video N (playlist N, BDJO 00001,
    /// the same xlet under another application ID), and title <paramref name="videos"/> + 1 is the menu again, where a video's title goes back at its end. The Top Menu
    /// key is then never pressed inside the top menu title while a video plays, which a player refuses ("opération interdite"). First Playback is an HDMV object
    /// that says the disc has just started (GPR109) and where the menu title is (GPR114), plays the intro, and jumps to the menu title.
    /// </summary>
    static void TitleLayout(string bdmv, int videos, int havi, bool intro)
    {
        foreach (var (name, appId) in new[] { ("00000", (ushort)0x4000), ("00001", BdjPlayerAppId) })
        {
            var bdjo = Bdmv.Bdjo("org.burnsmith.menu.Menu", havi, appId: appId);
            File.WriteAllBytes(Path.Combine(bdmv, "BDJO", name + ".bdjo"), bdjo);
            File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", "BDJO", name + ".bdjo"), bdjo);
        }
        var menuTitle = videos + 1;
        var firstPlay = -1;
        foreach (var file in new[] { Path.Combine(bdmv, "MovieObject.bdmv"), Path.Combine(bdmv, "BACKUP", "MovieObject.bdmv") }.Where(File.Exists))
            File.WriteAllBytes(file, Bdmv.AppendObject(File.ReadAllBytes(file), out firstPlay,
                [Disc.Nav.Set(BdjStartRegister, 1), Disc.Nav.Set(BdjMenuTitleRegister, menuTitle), .. intro ? [Disc.Nav.PlayPlaylist(IntroPlaylist)] : Array.Empty<byte[]>(), Disc.Nav.JumpTitle(menuTitle)]));
        foreach (var index in new[] { Path.Combine(bdmv, "index.bdmv"), Path.Combine(bdmv, "BACKUP", "index.bdmv") })
            if (File.Exists(index)) File.WriteAllBytes(index, Bdmv.PatchIndex(File.ReadAllBytes(index), firstPlay, videos));
    }

    /// <summary>
    /// A menu.txt with no "playback" line (a disc made with the old default: back to the menu after the picked title) gets today's default, the studios' series discs:
    /// a picked episode goes on to the next ones, back to the menu after the last. The episodes are those of the menu's "play all" button, so bonuses stay alone; without one it is kept.
    /// </summary>
    public static byte[] ChainEpisodes(byte[] menuTxt)
    {
        var text = Encoding.UTF8.GetString(menuTxt);
        var lines = text.Split('\n');
        if (lines.Any(l => l.StartsWith("playback ", StringComparison.Ordinal))) return menuTxt;
        var all = lines.Select(l => l.Trim().Split(' ')).Where(w => w.Length >= 3 && w[^3] == "playall").Select(w => (First: int.Parse(w[^2], Inv), Count: int.Parse(w[^1], Inv))).OrderByDescending(t => t.Count).FirstOrDefault(); // none: (0, 0)
        return all.Count > 1 ? Encoding.UTF8.GetBytes($"playback menu 1 1 {all.First} {all.Count}\n" + text) : menuTxt;
    }

    /// <summary>
    /// A BD-J menu disc made before 1.0.33 (the menu and every video in one title, where a player refuses the Top Menu key during a video) put on today's titles and
    /// xlet, in its folder <paramref name="discRoot"/>: the menu it carries (menu.txt and its pictures) is kept, with today's default play mode when it had none (see ChainEpisodes),
    /// and the jar is signed again. A disc already on today's titles only gets that play mode. Returns false for a disc that does not need anything.
    /// </summary>
    public static bool RepairTopMenu(string discRoot)
    {
        var bdmv = Path.Combine(discRoot, "BDMV");
        var jar = Path.Combine(bdmv, "JAR", "00000.jar");
        var menuBdjo = Path.Combine(bdmv, "BDJO", "00000.bdjo");
        if (!File.Exists(jar) || !File.Exists(menuBdjo)) return false; // no BD-J menu
        var layout = !File.Exists(Path.Combine(bdmv, "BDJO", "00001.bdjo")); // not yet one title per video
        var files = JarFiles(jar);
        var menu = files.Where(e => !e.Name.EndsWith(".class", StringComparison.Ordinal)).Select(e => e.Name == "menu.txt" ? (e.Name, ChainEpisodes(e.Data)) : e).ToList();
        if (!layout && menu.SequenceEqual(files.Where(e => !e.Name.EndsWith(".class", StringComparison.Ordinal)))) return false;
        foreach (var f in Directory.EnumerateFiles(discRoot, "*", SearchOption.AllDirectories)) // files copied out of an image are read-only
            File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.ReadOnly);
        var old = File.ReadAllBytes(menuBdjo);
        var havi = old[(int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(old.AsSpan(8)) + 4 + 5] >> 4; // terminal info: length, font name, then the HAVi configuration
        var videos = Enumerable.Range(MoviePlaylist, IntroPlaylist - MoviePlaylist).TakeWhile(n => File.Exists(Path.Combine(bdmv, "PLAYLIST", $"{n:00000}.mpls"))).Count();
        var xlet = JarFiles(Assets.Find(@"bdj\menu.jar"));
        File.Delete(jar); // read above; the signer writes a new file
        BdjSign.Write(jar, [.. xlet, .. menu], discRoot);
        // the objects an earlier start left in MovieObject.bdmv stay there, unused
        if (layout) TitleLayout(bdmv, videos, havi, File.Exists(Path.Combine(bdmv, "PLAYLIST", $"{IntroPlaylist:00000}.mpls")));
        return true;
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
