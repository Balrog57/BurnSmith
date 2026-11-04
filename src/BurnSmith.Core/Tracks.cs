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
/// </summary>
public sealed record DiscTitle(string Video, string? Name = null, IReadOnlyList<int>? Audio = null, IReadOnlyList<int>? Subtitles = null)
{
    public string DisplayName => Name ?? Path.GetFileNameWithoutExtension(Video);

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
