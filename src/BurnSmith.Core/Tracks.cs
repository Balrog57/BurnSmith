using System.Globalization;

namespace BurnSmith.Core;

/// <summary><see cref="Index"/> counts audio streams of the source file only (ffmpeg's 0:a:N), not all streams.</summary>
public sealed record AudioTrack(int Index, string Codec, int Channels, string Lang, string Title)
{
    public string Name => $"{Languages.Display(Lang, Title)} {Languages.ChannelLayout(Channels)}".Trim();
}

/// <summary><see cref="Index"/> counts subtitle streams of the source file only (ffmpeg's 0:s:N).</summary>
public sealed record SubtitleTrack(int Index, string Codec, string Lang, string Title, bool Forced)
{
    public bool IsPgs => Codec == "hdmv_pgs_subtitle";
    public bool IsText => Codec is "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text";
    /// <summary>Image subtitles other than PGS (DVD VobSub, DVB) would need OCR: not carried.</summary>
    public bool Supported => IsPgs || IsText;
    public string Name => Languages.Display(Lang, Title) + (Forced ? " (forcés)" : "");
}

/// <summary>
/// One video of the disc. <see cref="Audio"/> / <see cref="Subtitles"/> are the kept <see cref="AudioTrack.Index"/> /
/// <see cref="SubtitleTrack.Index"/> values in disc order; null = first audio only, no subtitles.
/// The cut (DVDFab / Yuhan "video edit"): <see cref="Start"/> and <see cref="End"/> are seconds of the source file (End null = to the end),
/// <see cref="Volume"/> a percentage of the sound, <see cref="ChapterAt"/> the chapter times in seconds of the source (null = the disc's
/// "one every N minutes", empty = no chapters).
/// </summary>
public sealed record DiscTitle(string Video, string? Name = null, IReadOnlyList<int>? Audio = null, IReadOnlyList<int>? Subtitles = null,
                               double Start = 0, double? End = null, int Volume = 100, IReadOnlyList<double>? ChapterAt = null)
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
        if (!Trimmed) return s;
        var length = Length(s);
        if (length < 1) throw new ArgumentException($"{DisplayName}: la coupe ne laisse rien sur le disque (début {Start:0.#} s, fin {End:0.#} s, durée du fichier {s.Duration:0.#} s)");
        return s with { Duration = length };
    }

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
