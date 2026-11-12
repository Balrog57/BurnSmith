using System.Text.Json;
using BurnSmith.Core.Menu;

namespace BurnSmith.Core;

/// <summary>
/// A disc being prepared, as the file the app saves and reopens (<see cref="Extension"/>, JSON) and as one step of its undo: the videos with their settings,
/// the menu and the disc's options. <see cref="Kind"/>: dvd | bd | uhd. <see cref="Label"/> and <see cref="Out"/>: null = follow the first video's name.
/// <see cref="Output"/>: the name of the disc size ("BD25 1080p/i"; null = the kind's usual one), <see cref="CapacityGB"/> what it holds (0 = the kind's usual, below 0 = no size limit). <see cref="Trial"/>: seconds of each video on a trial disc, 0 = the real one.
/// shortcut: files are named by their full path, so a project moved to another PC opens without its videos; write them relative to the project if that is missed.
/// </summary>
public sealed record Project(string Kind = "bd", IReadOnlyList<DiscTitle>? Titles = null, MenuDesign? Design = null, bool Menu = true, string? Label = null,
                             int ChapterMinutes = 5, EncodeOptions? Encode = null, PlaybackOptions? Playback = null, SubtitleStyle? Subtitles = null,
                             string? Output = null, double CapacityGB = 0, string? Out = null, bool Iso = false, int Trial = 0,
                             bool HdmvMenu = false) // Blu-ray: the menu as native HDMV navigation instead of the BD-J program (see Core.HdmvMenu)
{
    public const string Extension = ".burnsmith";

    // nulls are written: left out, a design's property set to null would come back as the class's default
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Throws <see cref="InvalidDataException"/> for a text that is not a project.</summary>
    public static Project Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // every JSON object would read as an empty project: this one must name its videos
            var read = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(nameof(Titles), out _) ? JsonSerializer.Deserialize<Project>(json, Json) : null;
            if (read is { Kind: "dvd" or "bd" or "uhd" } && read.Titles?.All(t => t is { Video.Length: > 0 }) != false) return read;
        }
        catch (JsonException) { }
        throw new InvalidDataException("ce fichier n'est pas un projet BurnSmith");
    }

    public void Save(string file) => File.WriteAllText(file, ToJson());

    public static Project Load(string file) => Parse(File.ReadAllText(file));

    /// <summary>The titles as the disc carries them: on a trial disc (<see cref="Trial"/> &gt; 0) each one cut to that many seconds, a passage at each chapter.</summary>
    public static List<DiscTitle> OnDisc(IReadOnlyList<DiscTitle> titles, int chapterMinutes, int trial, Func<string, SourceInfo> probe) =>
        trial <= 0 ? [.. titles] : [.. titles.Select(t => t.Trial(probe(t.Video), chapterMinutes, trial))];

    // not "<folder>.iso": that name could belong to the user's own file
    public static string IsoFile(string folder) => Path.TrimEndingDirectorySeparator(folder) + "_BurnSmith.iso";

    /// <summary>
    /// Builds this project's disc as « Démarrer » does, without burning: the folder (<paramref name="folder"/>, else <see cref="Out"/>, with "_essai" for a trial disc
    /// so that the real disc's folder is never replaced by it), its structure checked, and the ISO image beside it when <see cref="Iso"/> is set.
    /// Returns the problems the checks found (none = a good disc). What `burnsmith project`, `burnsmith queue` and the app's queue run.
    /// </summary>
    public List<string> Build(string? folder, int trial, Action<string> log, CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        var dir = Path.GetFullPath(folder ?? (Out is { Length: > 0 } ? Path.TrimEndingDirectorySeparator(Out) + (trial > 0 ? "_essai" : "") : throw new ArgumentException("the project names no output folder")));
        var usable = CapacityGB > 0 ? CapacityGB : CapacityGB < 0 ? 0 : Kind == "dvd" ? 4.2 : Kind == "uhd" ? 93 : 23.3; // below 0 = no size limit, which the builds and the checks call 0
        var list = OnDisc(Titles ?? [], ChapterMinutes, trial, BluRay.Probe);
        var menu = Menu || list.Count(t => !t.Intro) > 1 ? Design ?? TemplateCatalog.Default().Clone() : null;
        var label = Label is { Length: > 0 } ? Label : "BURNSMITH";
        if (trial > 0) log($"trial disc: {trial} s of each video, in {dir}");
        if (Kind == "dvd") Dvd.Build(new DvdJob(list, dir, menu, ChapterMinutes, usable, Encode ?? new(FitDisc: true), Playback, Subtitles), log, cancel);
        else BluRay.Build(new BluRayJob(list, dir, menu, Kind == "uhd", label, ChapterMinutes, Encode ?? new(FitDisc: true), usable, Playback, Subtitles, HdmvMenu), log, cancel);
        File.Delete(IsoFile(dir)); // an image of an earlier build must never be burnt by mistake
        var found = DiscCheck.Folder(dir, usable / 0.89);
        if (!Iso) return found;
        cancel.ThrowIfCancellationRequested();
        global::BurnSmith.Core.Iso.Build(dir, IsoFile(dir), label, Kind == "dvd", log, progress, cancel);
        found.AddRange(DiscCheck.Image(IsoFile(dir), dir, usable / 0.89));
        return found;
    }
}
