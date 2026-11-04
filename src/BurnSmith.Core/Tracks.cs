using System.Globalization;

namespace BurnSmith.Core;

/// <summary><see cref="Index"/> counts audio streams of the source file only (ffmpeg's 0:a:N), not all streams.</summary>
public sealed record AudioTrack(int Index, string Codec, int Channels, string Lang, string Title)
{
    public string Name => $"{Languages.Display(Lang, Title)} {Languages.ChannelLayout(Channels)}".Trim();
}

/// <summary>
/// <see cref="Index"/> counts subtitle streams of the source file (ffmpeg's 0:s:N); a track read from a subtitle file
/// (<see cref="File"/>, see <see cref="ExternalSubtitle"/>) comes after those.
/// </summary>
public sealed record SubtitleTrack(int Index, string Codec, string Lang, string Title, bool Forced, string? File = null)
{
    public bool IsPgs => Codec == "hdmv_pgs_subtitle";
    public bool IsText => Codec is "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text";
    /// <summary>Image subtitles other than PGS (DVD VobSub, DVB) would need OCR: not carried.</summary>
    public bool Supported => IsPgs || IsText;
    public string Name => Languages.Display(Lang, Title) + (Forced ? " (forcés)" : "");
}

/// <summary>
/// A subtitle file kept beside the video (SRT, ASS, SSA or WebVTT; bitmap ones would need OCR): one more track of that video, numbered after
/// the video's own. <see cref="Lang"/> is an ISO 639-2 code, "und" when unknown.
/// </summary>
public sealed record ExternalSubtitle(string File, string Lang = "und", bool Forced = false)
{
    public static readonly string[] Extensions = [".srt", ".ass", ".ssa", ".vtt"];

    public SubtitleTrack Track(int index) => new(index, Path.GetExtension(File).ToLowerInvariant() switch { ".ass" => "ass", ".ssa" => "ssa", ".vtt" => "webvtt", _ => "subrip" },
                                                  Lang, Path.GetFileName(File), Forced, File);

    /// <summary>A file as typed: "film.fr.srt" (language from its name) or "fra=film.srt" (language by hand).</summary>
    public static ExternalSubtitle Parse(string text)
    {
        var eq = text.IndexOf('=');
        var lang = eq is 2 or 3 ? Languages.Code(text[..eq]) : null;
        var file = Path.GetFullPath(lang == null ? text : text[(eq + 1)..]);
        if (!System.IO.File.Exists(file)) throw new FileNotFoundException($"subtitle file not found: {file}", file);
        var name = Path.GetFileNameWithoutExtension(file);
        var found = Describe(file, name.Contains('.') ? name[name.IndexOf('.')..] : ""); // what follows the first dot: "film.fr" -> ".fr"
        return lang == null ? found : found with { Lang = lang };
    }

    /// <summary>The subtitle files named after the video ("film.srt", "film.fr.srt", "film.English.forced.ass"), by name.</summary>
    public static List<ExternalSubtitle> Near(string video)
    {
        var stem = Path.GetFileNameWithoutExtension(video);
        var list = new List<ExternalSubtitle>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(video))!).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()) || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Length > stem.Length && !".-_ ".Contains(name[stem.Length])) continue; // "Episode 10.srt" is not for "Episode 1"
                list.Add(Describe(file, name[stem.Length..]));
            }
        }
        catch (IOException) { } // a folder that went away: no sidecar files
        return list;
    }

    static readonly System.Text.RegularExpressions.Regex Timing = new(@"(\d+):(\d+):(\d+)[,.](\d+)\s*-->\s*(\d+):(\d+):(\d+)[,.](\d+)");

    /// <summary>
    /// The cues of an SRT text that fall in the part of the video kept (<paramref name="start"/> seconds from the start, <paramref name="length"/> long),
    /// moved back by <paramref name="start"/>, a cue that straddles an edge cut short, renumbered. (ffmpeg's own seek into a subtitle file keeps the cue
    /// before the start, whole.)
    /// </summary>
    public static string Cut(string srt, double start, double length)
    {
        static double Seconds(System.Text.RegularExpressions.Match m, int at) =>
            int.Parse(m.Groups[at].Value) * 3600 + int.Parse(m.Groups[at + 1].Value) * 60 + int.Parse(m.Groups[at + 2].Value) + int.Parse(m.Groups[at + 3].Value.PadRight(3, '0')[..3]) / 1000.0;
        static string Stamp(double s) => TimeSpan.FromMilliseconds(Math.Round(s * 1000)).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
        var cues = new List<string>();
        foreach (var block in System.Text.RegularExpressions.Regex.Split(srt.Replace("\r\n", "\n").Trim(), "\n{2,}"))
        {
            var lines = block.Split('\n');
            var at = Array.FindIndex(lines, l => Timing.IsMatch(l)); // the line after the cue number
            if (at < 0) continue;
            var m = Timing.Match(lines[at]);
            var from = Math.Max(0, Seconds(m, 1) - start);
            var to = Math.Min(length, Seconds(m, 5) - start);
            if (to > from) cues.Add($"{cues.Count + 1}\n{Stamp(from)} --> {Stamp(to)}\n{string.Join('\n', lines.Skip(at + 1))}\n");
        }
        return string.Join('\n', cues) + (cues.Count > 0 ? "\n" : ""); // a blank line after the last cue
    }

    /// <summary>The language and "forced" flag spelled in what follows the video's name: ".fr", "_English.forced".</summary>
    static ExternalSubtitle Describe(string file, string suffix)
    {
        var lang = "und";
        var forced = false;
        foreach (var token in suffix.Split(['.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("forced", StringComparison.OrdinalIgnoreCase) || token.Equals("forcé", StringComparison.OrdinalIgnoreCase) || token.Equals("forces", StringComparison.OrdinalIgnoreCase)) forced = true;
            else if (lang == "und" && Languages.Code(token) is { } code) lang = code;
        }
        return new ExternalSubtitle(file, lang, forced);
    }
}

/// <summary>
/// One video of the disc. <see cref="Audio"/> / <see cref="Subtitles"/> are the kept <see cref="AudioTrack.Index"/> /
/// <see cref="SubtitleTrack.Index"/> values in disc order; null = first audio only, no subtitles.
/// The cut (DVDFab / Yuhan "video edit"): <see cref="Start"/> and <see cref="End"/> are seconds of the source file (End null = to the end),
/// <see cref="Volume"/> a percentage of the sound, <see cref="ChapterAt"/> the chapter times in seconds of the source (null = the disc's
/// "one every N minutes", empty = no chapters).
/// </summary>
public sealed record DiscTitle(string Video, string? Name = null, IReadOnlyList<int>? Audio = null, IReadOnlyList<int>? Subtitles = null,
                               double Start = 0, double? End = null, int Volume = 100, IReadOnlyList<double>? ChapterAt = null,
                               IReadOnlyList<ExternalSubtitle>? External = null)
{
    public string DisplayName => Name ?? Path.GetFileNameWithoutExtension(Video);

    public bool Trimmed => Start > 0 || End != null;

    /// <summary>Seconds of the source that stay on the disc.</summary>
    public double Length(SourceInfo s) => Math.Max(0, Math.Min(End ?? s.Duration, s.Duration) - Math.Min(Start, s.Duration));

    /// <summary>
    /// The source as the disc sees it: its length is the part kept, so every size, bit rate, thumbnail and chapter worked out from a
    /// <see cref="SourceInfo"/> follows the cut without knowing about it.
    /// </summary>
    public SourceInfo Cut(SourceInfo s)
    {
        s = WithSubtitleFiles(s);
        if (!Trimmed) return s;
        var length = Length(s);
        if (length < 1) throw new ArgumentException($"{DisplayName}: la coupe ne laisse rien sur le disque (début {Start:0.#} s, fin {End:0.#} s, durée du fichier {s.Duration:0.#} s)");
        return s with { Duration = length, FileDuration = s.Duration };
    }

    /// <summary>The source with this video's <see cref="External"/> subtitle files added to its own subtitle tracks, numbered on from them.</summary>
    public SourceInfo WithSubtitleFiles(SourceInfo s) =>
        External is not { Count: > 0 } ? s : s with { Subtitles = [.. s.Subtitles, .. External.Select((e, k) => e.Track(s.Subtitles.Count + k))] };

    /// <summary>Chapter starts on the disc (seconds from the start of the part kept): always begins with 0, whole seconds, so no two share a second.</summary>
    public List<double> ChapterList(double length, int minutes) =>
        ChapterAt == null ? BluRay.Chapters(length, minutes)
        : ChapterAt.Select(t => Math.Round(t - Start)).Where(t => t > 0 && t < length - 1).Prepend(0).Distinct().Order().ToList();

    public IReadOnlyList<AudioTrack> KeptAudio(SourceInfo s) =>
        Audio == null ? s.Audio.Take(1).ToList() : Audio.Select(i => s.Audio.FirstOrDefault(a => a.Index == i)).OfType<AudioTrack>().ToList();

    public IReadOnlyList<SubtitleTrack> KeptSubtitles(SourceInfo s) =>
        Subtitles == null ? [] : Subtitles.Select(i => s.Subtitles.FirstOrDefault(t => t.Index == i)).OfType<SubtitleTrack>().ToList();
}

public static class Languages
{
    // ffprobe reports ISO 639-2/B ("fre"); DVD wants 639-1 ("fr"). Only the common ones: anything else shows as given.
    static readonly Dictionary<string, string> TwoLetter = new()
    {
        ["eng"] = "en", ["fre"] = "fr", ["fra"] = "fr", ["ger"] = "de", ["deu"] = "de", ["spa"] = "es", ["ita"] = "it", ["por"] = "pt",
        ["jpn"] = "ja", ["chi"] = "zh", ["zho"] = "zh", ["rus"] = "ru", ["dut"] = "nl", ["nld"] = "nl", ["kor"] = "ko", ["ara"] = "ar",
        ["pol"] = "pl", ["tur"] = "tr", ["swe"] = "sv", ["nor"] = "no", ["dan"] = "da", ["fin"] = "fi", ["cze"] = "cs", ["ces"] = "cs",
        ["hun"] = "hu", ["gre"] = "el", ["ell"] = "el", ["heb"] = "he", ["hin"] = "hi", ["tha"] = "th", ["vie"] = "vi", ["ukr"] = "uk",
        ["rum"] = "ro", ["ron"] = "ro",
    };

    public static string Iso1(string lang) => TwoLetter.GetValueOrDefault(lang, "xx"); // "xx" = unknown, accepted by dvdauthor

    /// <summary>Three letters for tsMuxeR; "und" when the file does not say.</summary>
    public static string Iso2(string lang) => lang.Length == 3 ? lang : "und";

    /// <summary>The ISO 639-2 code (the one ffprobe reports: "fre") of "fr", "fra", "French" or "français"; null when the word is none of those.</summary>
    public static string? Code(string word)
    {
        var w = word.ToLowerInvariant();
        if (TwoLetter.ContainsKey(w)) return w;
        foreach (var (three, two) in TwoLetter)
            if (w == two) return three; // the first one listed for this language is the B code
        foreach (var two in TwoLetter.Values.Distinct())
        {
            var culture = CultureInfo.GetCultureInfo(two);
            if (w == culture.EnglishName.ToLowerInvariant() || w == culture.NativeName.Split(' ')[0].ToLowerInvariant()) return TwoLetter.First(kv => kv.Value == two).Key;
        }
        return null;
    }

    /// <summary>"Français", "English"... in the language's own name; falls back to the stream title, then the raw code.</summary>
    public static string Display(string lang, string title)
    {
        if (TwoLetter.TryGetValue(lang, out var two))
        {
            var n = CultureInfo.GetCultureInfo(two).NativeName;
            var name = n.Split(' ')[0];
            return char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..];
        }
        return title.Length > 0 ? title : lang is "" or "und" ? "?" : lang;
    }

    public static string ChannelLayout(int channels) => channels switch { 1 => "1.0", 2 => "2.0", 6 => "5.1", 8 => "7.1", 0 => "", _ => channels + "ch" };
}
