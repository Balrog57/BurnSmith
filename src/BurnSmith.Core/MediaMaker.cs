using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BurnSmith.Core;

/// <summary>
/// Videos made from something that is not one: photos shown one after the other (with or without music), a song with its cover. They come out as ordinary
/// 1080p25 H.264 + AC-3 files, so the rest of the disc building takes them like any other source (a Blu-ray copies them, a DVD re-encodes them).
/// </summary>
public static class MediaMaker
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Photos = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"];
    static readonly string[] Songs = [".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".ac3", ".mka"];
    const int Width = 1920, Height = 1080;
    const string FitPicture = "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1";
    const string Fit = FitPicture + ",fps=25,format=yuv420p";
    // the same Blu-ray settings as BluRay.Prepare, so a disc copies the video untouched
    static readonly string[] Video = ["-c:v", "libx264", "-preset", "fast", "-crf", "19", "-x264-params", "bluray-compat=1:level=41:vbv-maxrate=30000:vbv-bufsize=30000:keyint=25:slices=4:bframes=3:b-pyramid=strict:open-gop=0"];

    public static bool IsPhoto(string file) => Photos.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);
    public static bool IsSong(string file) => Songs.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    static string Ffmpeg => Tools.Find("ffmpeg.exe");

    /// <summary>One line of a concat list: ffmpeg wants forward slashes and a quote inside a quoted name written '\''.</summary>
    static string Quoted(string file) => "file '" + Path.GetFullPath(file).Replace('\\', '/').Replace("'", @"'\''") + "'";

    /// <summary>
    /// Photos shown <paramref name="secondsEach"/> seconds each, in the order given, fitted to 1920x1080 on black. Under the pictures: the <paramref name="music"/> files one
    /// after the other, started again when the photos last longer, faded out at the end (silence when there is none). Returns <paramref name="output"/>.
    /// shortcut: a phone photo's EXIF rotation is not applied, upgrade with an explicit -vf transpose when it matters.
    /// </summary>
    public static string Slideshow(IReadOnlyList<string> photos, double secondsEach, IReadOnlyList<string> music, string output, Action<string>? log = null)
    {
        if (photos.Count == 0) throw new ArgumentException("no photo for the slideshow");
        secondsEach = Math.Clamp(secondsEach, 1, 600);
        var total = photos.Count * secondsEach;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        // each photo is first fitted to 1080p on its own (a concat list cannot mix a png and a jpg), then the numbered pictures are shown secondsEach seconds each
        var work = output + ".photos";
        var songs = output + ".music.txt";
        Directory.CreateDirectory(work);
        try
        {
            for (var k = 0; k < photos.Count; k++)
                Tools.Run(Ffmpeg, ["-y", "-v", "error", "-i", photos[k], "-vf", FitPicture, "-frames:v", "1", "-q:v", "2", Path.Combine(work, $"{k + 1:0000}.jpg")]);
            var args = new List<string> { "-y", "-v", "warning", "-framerate", $"1000/{Math.Round(secondsEach * 1000)}", "-i", Path.Combine(work, "%04d.jpg") };
            if (music.Count > 0)
            {
                // each song is first made a FLAC of one format (a concat list cannot mix an mp3 and a flac), then listed again and again until they cover the photos (a concat input cannot be looped)
                var lengths = music.Select(m => Describe(m).Seconds).ToList();
                if (lengths.Sum() < 1) throw new InvalidDataException("no sound in the music files");
                var same = music.Select((m, k) => Path.Combine(work, $"music{k + 1:000}.flac")).ToList();
                for (var k = 0; k < music.Count; k++)
                    Tools.Run(Ffmpeg, ["-y", "-v", "error", "-i", music[k], "-vn", "-map", "0:a:0", "-ar", "48000", "-ac", "2", "-sample_fmt", "s16", "-c:a", "flac", same[k]]);
                var lines = new List<string>();
                for (double sum = 0; sum < total;) for (var k = 0; k < music.Count && sum < total; k++) { lines.Add(Quoted(same[k])); sum += lengths[k]; }
                File.WriteAllText(songs, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
                args.AddRange(["-f", "concat", "-safe", "0", "-i", songs]);
            }
            else args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
            args.AddRange(["-map", "0:v", "-map", "1:a", "-vf", "format=yuv420p", "-r", "25"]); // -r repeats each picture to 25 fps; the fps filter would drop the first photos here
            args.AddRange(Video);
            args.AddRange(["-c:a", "ac3", "-b:a", "192k", "-ac", "2", "-ar", "48000"]);
            if (music.Count > 0) args.AddRange(["-af", $"afade=t=out:st={Math.Max(0, total - 2).ToString(Inv)}:d=2"]);
            args.AddRange(["-t", total.ToString(Inv), output]);
            log?.Invoke($"Diaporama : {photos.Count} photos, {secondsEach:0.#} s chacune{(music.Count > 0 ? ", avec de la musique" : "")}");
            Tools.Run(Ffmpeg, args);
        }
        finally { Tools.TryDelete(work); File.Delete(songs); }
        return output;
    }

    /// <summary>Whether two videos can be put end to end without re-encoding: same picture (codec, profile and level, size, rate, HDR) and the same audio and subtitle streams in the same order.</summary>
    public static bool SameLayout(SourceInfo a, SourceInfo b) =>
        a.VideoCodec == b.VideoCodec && a.Width == b.Width && a.Height == b.Height && Math.Abs(a.Fps - b.Fps) < 0.01 && a.Hdr == b.Hdr && a.Encoding == b.Encoding
        && a.Audio.Select(t => (t.Codec, t.Channels)).SequenceEqual(b.Audio.Select(t => (t.Codec, t.Channels)))
        && a.Subtitles.Select(t => t.Codec).SequenceEqual(b.Subtitles.Select(t => t.Codec));

    /// <summary>The files one after the other in one mkv, streams copied (see <see cref="SameLayout"/>). Returns <paramref name="output"/>.</summary>
    public static string Join(IReadOnlyList<string> files, string output, Action<string>? log = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var list = output + ".join.txt";
        File.WriteAllText(list, string.Join('\n', files.Select(Quoted)) + "\n", new UTF8Encoding(false));
        try
        {
            log?.Invoke($"Fusion de {files.Count} vidéos");
            Tools.Run(Ffmpeg, ["-y", "-v", "warning", "-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-c", "copy", output]);
        }
        finally { File.Delete(list); }
        return output;
    }

    /// <summary>The length of an audio file and the title it carries, if any.</summary>
    public static (double Seconds, string? Title) Describe(string audio)
    {
        var json = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-of", "json", "-show_entries", "format=duration:format_tags", audio]);
        using var doc = JsonDocument.Parse(json);
        var format = doc.RootElement.GetProperty("format");
        var seconds = format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), Inv, out var s) ? s : 0;
        string? title = null;
        if (format.TryGetProperty("tags", out var tags))
            foreach (var tag in tags.EnumerateObject())
                if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase) && tag.Value.GetString() is { Length: > 0 } t) title = t;
        return (seconds, title);
    }

    /// <summary>The cover to show with a song: a picture of the same name or called cover / folder / front / album beside it, else the one inside the file, else null.</summary>
    public static string? CoverOf(string audio, string work)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(audio))!;
        var stem = Path.GetFileNameWithoutExtension(audio);
        foreach (var name in new[] { stem, "cover", "folder", "front", "album" })
            foreach (var ext in Photos)
                if (File.Exists(Path.Combine(dir, name + ext))) return Path.Combine(dir, name + ext);
        var inside = Path.Combine(work, Guid.NewGuid().ToString("N")[..8] + ".cover.jpg");
        try { Tools.Run(Ffmpeg, ["-y", "-v", "error", "-i", audio, "-an", "-frames:v", "1", inside]); return File.Exists(inside) ? inside : null; }
        catch (InvalidOperationException) { return null; } // no picture in the file
    }

    /// <summary>A song as a video: its cover (black without one) for as long as it plays. Returns <paramref name="output"/>.</summary>
    public static string Song(string audio, string? cover, string output, Action<string>? log = null)
    {
        var (seconds, _) = Describe(audio);
        if (seconds < 1) throw new InvalidDataException("no sound in " + Path.GetFileName(audio));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var args = new List<string> { "-y", "-v", "warning" };
        if (cover != null) args.AddRange(["-loop", "1", "-framerate", "25", "-i", cover]);
        else args.AddRange(["-f", "lavfi", "-i", $"color=c=black:s={Width}x{Height}:r=25"]);
        args.AddRange(["-i", audio, "-map", "0:v", "-map", "1:a:0", "-vf", Fit, "-r", "25"]);
        args.AddRange(Video);
        args.AddRange(["-c:a", "ac3", "-b:a", "448k", "-ac", "2", "-ar", "48000", "-t", seconds.ToString(Inv), output]);
        log?.Invoke($"Morceau : {Path.GetFileName(audio)} ({Timecode.Format(seconds)})");
        Tools.Run(Ffmpeg, args);
        return output;
    }
}
