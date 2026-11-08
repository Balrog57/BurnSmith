using System.Globalization;

namespace BurnSmith.Core;

/// <summary><see cref="Index"/> counts audio streams of the source file only (ffmpeg's 0:a:N), not all streams. <see cref="SampleRate"/>, <see cref="Kbps"/> (average bit rate): 0 when the file does not say.</summary>
public sealed record AudioTrack(int Index, string Codec, int Channels, string Lang, string Title, int SampleRate = 48000, int Kbps = 0)
{
    public string Name => $"{Languages.Display(Lang, Title)} {Languages.ChannelLayout(Channels)}".Trim();
}

/// <summary>
/// <see cref="Index"/> counts subtitle streams of the source file (ffmpeg's 0:s:N); a track read from a subtitle file
/// (<see cref="File"/>, see <see cref="ExternalSubtitle"/>) comes after those. <see cref="Stream"/>: which track of a VobSub .idx (one holds several languages).
/// </summary>
public sealed record SubtitleTrack(int Index, string Codec, string Lang, string Title, bool Forced, string? File = null, int Stream = 0)
{
    public bool IsPgs => Codec == "hdmv_pgs_subtitle";
    public bool IsText => Codec is "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text";
    /// <summary>A VobSub inside the video (what a DVD rip keeps in an mkv): made into PGS like an .idx.</summary>
    public bool IsVobSub => Codec == "dvd_subtitle";
    /// <summary>Pictures, carried as a .sup file once they are PGS.</summary>
    public bool IsPicture => IsPgs || IsVobSub;
    /// <summary>PGS is copied on a Blu-ray and redrawn on a DVD (a VobSub is made into PGS first); other image subtitles (DVB, Teletext) would need OCR: not carried.</summary>
    public bool Supported => IsPicture || IsText;
    public string Name => Languages.Display(Lang, Title) + (Forced || Title.EndsWith("(forced)", StringComparison.OrdinalIgnoreCase) ? " (forcés)" : "");
}

/// <summary>
/// A subtitle file kept beside the video (SRT, ASS, SSA, WebVTT, PGS as a .sup, or VobSub as an .idx with its .sub; the other bitmap ones would need OCR): one more
/// track of that video, numbered after the video's own. <see cref="Lang"/> is an ISO 639-2 code, "und" when unknown. An .idx holds one track per language:
/// <see cref="Stream"/> says which one this is.
/// </summary>
public sealed record ExternalSubtitle(string File, string Lang = "und", bool Forced = false, int Stream = 0)
{
    public static readonly string[] Extensions = [".srt", ".ass", ".ssa", ".vtt", ".sup", ".idx"];

    public SubtitleTrack Track(int index) => new(index, Path.GetExtension(File).ToLowerInvariant() switch { ".ass" => "ass", ".ssa" => "ssa", ".vtt" => "webvtt", ".sup" or ".idx" => "hdmv_pgs_subtitle", _ => "subrip" },
                                                  Lang, Path.GetFileNameWithoutExtension(File) + (Stream > 0 ? $" ({Stream + 1})" : ""), Forced, File, Stream); // the name stands in for the language the file does not say; an .idx has one track per stream

    /// <summary>
    /// What a file stands for: itself, or for an .idx one track per language it holds (the language the .idx gives, else the one the name says; with
    /// <paramref name="typed"/> the language was typed by hand: only the track in that language, else the first). An .idx needs its .sub.
    /// </summary>
    static List<ExternalSubtitle> Split(ExternalSubtitle e, bool typed = false)
    {
        if (!Path.GetExtension(e.File).Equals(".idx", StringComparison.OrdinalIgnoreCase)) return [e];
        if (!System.IO.File.Exists(VobSub.SubFile(e.File))) throw new FileNotFoundException($"the .sub of {Path.GetFileName(e.File)} is missing", VobSub.SubFile(e.File));
        var streams = VobSub.ReadIndex(e.File).Streams;
        if (streams.Count == 0) return [e];
        if (typed) return [e with { Stream = Math.Max(0, streams.ToList().FindIndex(s => Languages.Same(s.Lang, e.Lang))) }];
        return [.. streams.Select((s, i) => e with { Stream = i, Lang = streams.Count > 1 && s.Lang != "und" || e.Lang == "und" ? s.Lang : e.Lang })];
    }

    /// <summary><see cref="Parse"/> for a file that may stand for several tracks (an .idx).</summary>
    public static List<ExternalSubtitle> ParseAll(string text) => Split(Parse(text), text.IndexOf('=') is 2 or 3 && Languages.Code(text[..text.IndexOf('=')]) != null);

    /// <summary><see cref="For"/> for a file that may stand for several tracks (an .idx; a chosen .sub stands for its .idx).</summary>
    public static List<ExternalSubtitle> ForAll(string video, string file)
    {
        if (Path.GetExtension(file).Equals(".sub", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(Path.ChangeExtension(file, ".idx"))) file = Path.ChangeExtension(file, ".idx");
        return Split(For(video, file));
    }

    /// <summary>
    /// A file as typed: "fra=film.srt" (language by hand), else the language at the end of its name ("film.fr.srt", "Dr.No.1962.en.srt",
    /// "2_English.srt"; a title that merely contains a language word, "Do It Now.srt", has none).
    /// </summary>
    public static ExternalSubtitle Parse(string text)
    {
        var eq = text.IndexOf('=');
        var lang = eq is 2 or 3 ? Languages.Code(text[..eq]) : null;
        var file = Path.GetFullPath(lang == null ? text : text[(eq + 1)..]);
        if (!System.IO.File.Exists(file)) throw new FileNotFoundException($"subtitle file not found: {file}", file);
        var name = Path.GetFileNameWithoutExtension(file);
        var found = Describe(file, name.IndexOfAny(Separators) is var cut and >= 0 ? name[cut..] : "", lastOnly: true); // what follows the first word of the name
        return lang == null ? found : found with { Lang = lang };
    }

    /// <summary>A file chosen for <paramref name="video"/>: read the way <see cref="Near"/> does when it is named after it, else as <see cref="Parse"/>.</summary>
    public static ExternalSubtitle For(string video, string file)
    {
        var stem = Path.GetFileNameWithoutExtension(video);
        var name = Path.GetFileNameWithoutExtension(file);
        return Sibling(stem, name) ? Describe(file, name[stem.Length..]) : Parse(file);
    }

    static readonly char[] Separators = ['.', '_', '-', ' '];

    /// <summary>"film.fr" is a file of "film"; "film 2.fr" and "film10.fr" are not.</summary>
    static bool Sibling(string stem, string name) =>
        name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && (name.Length == stem.Length || Separators.Contains(name[stem.Length]));

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
                if (Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && Sibling(stem, name) && (!file.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) || System.IO.File.Exists(VobSub.SubFile(file))))
                    list.AddRange(Split(Describe(file, name[stem.Length..]))); // "Episode 10.srt" is not for "Episode 1"; an .idx without its .sub is no subtitle
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a folder that went away or cannot be listed: no sidecar files
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

    /// <summary>
    /// The language and "forced" flag spelled in <paramref name="suffix"/> (".fr", "_English.forced"): the last language word counts, since it
    /// comes last, before "forced"; with <paramref name="lastOnly"/> only the last word that is not "forced" may be one.
    /// </summary>
    static ExternalSubtitle Describe(string file, string suffix, bool lastOnly = false)
    {
        var lang = "und";
        var forced = false;
        var decided = false;
        foreach (var token in suffix.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (token.Equals("forced", StringComparison.OrdinalIgnoreCase) || token.Equals("forcé", StringComparison.OrdinalIgnoreCase) || token.Equals("forces", StringComparison.OrdinalIgnoreCase)) forced = true;
            else if (!decided)
            {
                if (Languages.Code(token) is { } code) { lang = code; decided = true; }
                else decided = lastOnly;
            }
        }
        return new ExternalSubtitle(file, lang, forced);
    }
}

/// <summary>
/// One video of the disc. <see cref="Audio"/> / <see cref="Subtitles"/> are the kept <see cref="AudioTrack.Index"/> /
/// <see cref="SubtitleTrack.Index"/> values in disc order; null = first audio only, no subtitles.
/// The cut ("video edit"): <see cref="Start"/> and <see cref="End"/> are seconds of the source file (End null = to the end),
/// <see cref="Volume"/> a percentage of the sound, <see cref="ChapterAt"/> the chapter times in seconds of the source (null = the disc's
/// "one every N minutes", empty = no chapters). <see cref="ChapterMinutes"/>: with no <see cref="ChapterAt"/>, one every N minutes for this video
/// (null = the disc's setting). <see cref="Intro"/>: a video that plays once when the disc starts, before the menu (before the film when there is no menu);
/// it is no title of the menu or of "Play all". On a DVD it is the title after the last one, which the first-play command jumps to.
/// <see cref="Bonus"/>: an extra (trailer, making-of) that is not part of the main feature: it comes after the main videos on the disc, is left out of
/// "Play" / "Play all", and is listed on the menu's Bonus page.
/// <see cref="DefaultAudio"/> / <see cref="DefaultSubtitle"/> (an <see cref="AudioTrack.Index"/> / <see cref="SubtitleTrack.Index"/> among those kept): what
/// plays and shows when the disc starts. Null: the first audio, no subtitles. The default audio goes first, so it is audio 1 everywhere (the menu's
/// pages, a DVD's first stream, the first of a Blu-ray's list); the default subtitle is selected by the disc itself (<see cref="DefaultSubtitleNumber"/>).
/// <see cref="Effects"/>: rotation, crop, colour, watermark and burnt subtitle drawn into the picture (null = none).
/// <see cref="Clips"/>: the parts kept when there are several (DVDFab's "output clips"), in order and in seconds of the source; <see cref="Start"/> and
/// <see cref="End"/> are then the first one's start and the last one's end, and what lies between two clips (<see cref="Gaps"/>) is left out of the disc.
/// <see cref="TrackLanguage"/>: languages set by hand on the video's own tracks (key <see cref="LanguageKey"/>, value an ISO 639-2 code), for a track the file leaves "und" or mislabels.
/// </summary>
/// <summary>One part of a video kept on the disc, from <see cref="From"/> to <see cref="To"/> seconds of the source.</summary>
public sealed record Clip(double From, double To);

public sealed record DiscTitle(string Video, string? Name = null, IReadOnlyList<int>? Audio = null, IReadOnlyList<int>? Subtitles = null,
                               double Start = 0, double? End = null, int Volume = 100, IReadOnlyList<double>? ChapterAt = null,
                               IReadOnlyList<ExternalSubtitle>? External = null, bool Bonus = false, int? DefaultAudio = null, int? DefaultSubtitle = null,
                               int? ChapterMinutes = null, bool Intro = false, IReadOnlyDictionary<string, string>? TrackLanguage = null,
                               VideoEffects? Effects = null, IReadOnlyList<Clip>? Clips = null)
{
    public string DisplayName => Name ?? Path.GetFileNameWithoutExtension(Video);

    /// <summary>Every file the title reads: the video, its subtitle files, and the effects' burnt subtitle and watermark.</summary>
    public IEnumerable<string?> Files => [Video, .. External?.Select(e => e.File) ?? [], Effects?.BurnSubtitle, Effects?.Watermark];

    /// <summary>The files a build reads (titles and menu), which its output and work folders must not hold: see <see cref="Tools.KeepInputs"/>.</summary>
    public static IEnumerable<string?> Inputs(IEnumerable<DiscTitle> titles, Menu.MenuDesign? menu) =>
        [.. titles.SelectMany(t => t.Files), menu?.LoopFile, menu?.Music];

    public bool Trimmed => Start > 0 || End != null || Gaps.Count > 0;

    /// <summary>
    /// The disc order: the main feature's videos first, then the bonus ones (each group keeps its order). A title is its playlist number,
    /// so "Play all" and the chain of episodes (playlists first..first+count) can never run on into a bonus.
    /// </summary>
    public static List<DiscTitle> MainFirst(IReadOnlyList<DiscTitle> titles)
    {
        if (titles.Count > 0 && titles.All(t => t.Bonus)) throw new ArgumentException("au moins une vidéo doit rester le film principal (toutes sont marquées Bonus)");
        return [.. titles.Where(t => !t.Bonus), .. titles.Where(t => t.Bonus)];
    }

    /// <summary>Seconds of the source that stay on the disc.</summary>
    public double Length(SourceInfo s) => Math.Max(0, Math.Min(End ?? s.Duration, s.Duration) - Math.Min(Start, s.Duration)) - GapLength;

    /// <summary>The parts left out between two <see cref="Clips"/>, in seconds from <see cref="Start"/> (none with a single part).</summary>
    public IReadOnlyList<(double From, double To)> Gaps =>
        Clips is not { Count: > 1 } ? [] : [.. Clips.Zip(Clips.Skip(1)).Where(p => p.Second.From > p.First.To).Select(p => (p.First.To - Start, p.Second.From - Start))];

    public double GapLength => Gaps.Sum(g => g.To - g.From);

    /// <summary>
    /// A time of the source counted from <see cref="Start"/> as a time on the disc: the gaps before it are taken off; null inside a gap.
    /// </summary>
    public double? OnDisc(double t)
    {
        var shift = 0.0;
        foreach (var (from, to) in Gaps)
        {
            if (t < from) break;
            if (t < to) return null;
            shift += to - from;
        }
        return t - shift;
    }

    /// <summary>
    /// The ffmpeg filter that drops the gaps and closes them up (video, or audio with <paramref name="audio"/>), to put after the rest of the chain; "" without gaps.
    /// The times are those of the input cut by -ss, so they count from <see cref="Start"/>.
    /// </summary>
    public string GapFilter(bool audio)
    {
        if (Gaps.Count == 0) return "";
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var drop = string.Join("+", Gaps.Select(g => $"gte(t,{N(g.From)})*lt(t,{N(g.To)})"));
        return audio ? $"aselect='not({drop})',asetpts=N/SR/TB" : $"select='not({drop})',setpts=N/FRAME_RATE/TB";
    }

    /// <summary>
    /// A subtitle file (.sup pictures, else SRT) written for the whole span from <see cref="Start"/>, <paramref name="span"/> seconds long, with the gaps taken
    /// out like the picture. shortcut: a picture still shown when a gap begins stays until the next one (its clear set falls in the gap); write a clear set if it shows.
    /// </summary>
    public void CloseGaps(string file, double span)
    {
        if (Gaps.Count == 0 || !File.Exists(file)) return;
        var parts = new List<(double From, double To)>();
        var at = 0.0;
        foreach (var (from, to) in Gaps) { parts.Add((at, from)); at = to; }
        parts.Add((at, span));
        var offset = 0.0;
        if (file.EndsWith(".sup", StringComparison.OrdinalIgnoreCase))
        {
            var sup = File.ReadAllBytes(file);
            var output = new MemoryStream();
            foreach (var (from, to) in parts)
            {
                output.Write(PgsSubtitles.Cut(PgsSubtitles.Cut(sup, from, to - from), -offset, 1e6)); // a negative start moves the part later
                offset += to - from;
            }
            File.WriteAllBytes(file, output.ToArray());
            return;
        }
        var srt = File.ReadAllText(file);
        var joined = new System.Text.StringBuilder();
        foreach (var (from, to) in parts)
        {
            joined.Append(ExternalSubtitle.Cut(ExternalSubtitle.Cut(srt, from, to - from), -offset, 1e6)).AppendLine();
            offset += to - from;
        }
        File.WriteAllText(file, ExternalSubtitle.Cut(joined.ToString(), 0, 1e6)); // numbered again from 1
    }

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
        if (Clips is { Count: > 1 } && Clips.Zip(Clips.Skip(1)).Any(p => p.Second.From < p.First.To || p.First.To <= p.First.From))
            throw new ArgumentException($"{DisplayName}: les clips doivent se suivre sans se chevaucher");
        return s with { Duration = length, FileDuration = s.Duration };
    }

    /// <summary>The source with this video's <see cref="External"/> subtitle files added to its own subtitle tracks, numbered on from them.</summary>
    public SourceInfo WithSubtitleFiles(SourceInfo s)
    {
        if (External is { Count: > 0 }) s = s with { Subtitles = [.. s.Subtitles, .. External.Select((e, k) => e.Track(s.Subtitles.Count + k))] };
        if (TrackLanguage is not { Count: > 0 }) return s;
        // a subtitle file has its own language (see ExternalSubtitle), so only the video's own tracks are relabelled; every list, menu and disc reads the tracks through here
        return s with
        {
            Audio = [.. s.Audio.Select(a => a with { Lang = TrackLanguage.GetValueOrDefault(LanguageKey(true, a.Index), a.Lang) })],
            Subtitles = [.. s.Subtitles.Select(t => t.File != null ? t : t with { Lang = TrackLanguage.GetValueOrDefault(LanguageKey(false, t.Index), t.Lang) })],
        };
    }

    /// <summary>The key of a track in <see cref="TrackLanguage"/>: "a0" is audio track 0 of the file, "s1" its subtitle track 1.</summary>
    public static string LanguageKey(bool audio, int index) => (audio ? "a" : "s") + index;

    /// <summary>Chapter starts on the disc (seconds from the start of the part kept): always begins with 0, whole seconds, so no two share a second.</summary>
    public List<double> ChapterList(double length, int minutes) =>
        ChapterAt == null ? BluRay.Chapters(length, ChapterMinutes ?? minutes)
        : ChapterAt.Select(t => OnDisc(t - Start)).OfType<double>().Select(t => Math.Round(t)).Where(t => t > 0 && t < length - 1).Prepend(0).Distinct().Order().ToList();

    /// <summary>The audio tracks in disc order: the default one first, the others in the order kept.</summary>
    public IReadOnlyList<AudioTrack> KeptAudio(SourceInfo s)
    {
        var kept = Audio == null ? s.Audio.Take(1).ToList() : Audio.Select(i => s.Audio.FirstOrDefault(a => a.Index == i)).OfType<AudioTrack>().ToList();
        return kept.FindIndex(a => a.Index == DefaultAudio) is > 0 and var at ? [kept[at], .. kept.Where((_, i) => i != at)] : kept;
    }

    /// <summary>The default subtitle's number among the tracks that really went on the disc (1-based, as a player counts them); 0 = none shown.</summary>
    public int DefaultSubtitleNumber(IReadOnlyList<SubtitleTrack> carried) =>
        DefaultSubtitle is { } d ? carried.Select(t => t.Index).ToList().IndexOf(d) + 1 : 0;

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

    public static string Iso1(string lang) => TwoLetter.GetValueOrDefault(lang, "xx"); // "xx" = unknown

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

    /// <summary>The languages a subtitle file can be set to: one per language, as (ISO 639-2 code as ffprobe reports it, name in the language's own tongue).</summary>
    public static IEnumerable<(string Code, string Name)> Choices =>
        TwoLetter.GroupBy(kv => kv.Value).Select(g => (g.First().Key, Display(g.First().Key, "")));

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> name the same language ("fre" and "fra"), or are the same code.</summary>
    public static bool Same(string a, string b) => a == b || (TwoLetter.ContainsKey(a) && TwoLetter.ContainsKey(b) && Iso1(a) == Iso1(b));

    /// <summary>"Français", "English"... in the language's own name; falls back to the stream title, then the raw code.</summary>
    public static string Display(string lang, string title)
    {
        if (TwoLetter.TryGetValue(lang, out var two))
        {
            var n = CultureInfo.GetCultureInfo(two).NativeName;
            var name = n.Split(' ')[0];
            // the title's bracketed detail tells two tracks of one language apart ("French (Audio-Description)" was a second "Français 5.1"); a subtitle says "forced" itself
            var detail = title.EndsWith(')') && title.LastIndexOf('(') is >= 0 and var open ? title[open..] : "";
            if (detail.Length <= 2 || detail.Equals("(forced)", StringComparison.OrdinalIgnoreCase)) detail = "";
            return char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..] + (detail.Length > 0 ? " " + detail : "");
        }
        return title.Length > 0 ? title : lang is "" or "und" ? "?" : lang;
    }

    public static string ChannelLayout(int channels) => channels switch { 1 => "1.0", 2 => "2.0", 6 => "5.1", 8 => "7.1", 0 => "", _ => channels + "ch" };
}
