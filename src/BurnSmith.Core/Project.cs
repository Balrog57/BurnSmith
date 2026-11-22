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
                             bool HdmvMenu = false, // Blu-ray: the menu as native HDMV navigation instead of the BD-J program (see Core.HdmvMenu)
                             bool MaxCompat = false) // « Compatibilité maximale »: see Compatible
{
    public const string Extension = ".burnsmith";

    /// <summary>
    /// « Compatibilité maximale » (`--max-compat`): the settings every player reads, set over the disc's own for the build — the native HDMV menu (the simple one:
    /// still pages, no pop-up), every sound in AC-3 (« Son sans perte » off) with a Dolby Digital track first (<see cref="DiscTitle.Ac3First"/>); a burn then
    /// goes at the slowest speed offered (<see cref="Iso.Speeds"/>). On a DVD only the sound applies.
    /// </summary>
    public static (List<DiscTitle> Titles, EncodeOptions Encode, bool Hdmv) Compatible(IEnumerable<DiscTitle> titles, EncodeOptions encode, Func<string, SourceInfo> probe) =>
        ([.. titles.Select(t => t.Ac3First(probe(t.Video)))], encode with { Lossless = false, CopyEac3 = false }, true);

    /// <summary>Known to fail on players, said before the job starts (null = nothing): a 100 GB disc (more than a BD-66 holds).</summary>
    public static string? Warning(string kind, double usableGb) =>
        kind != "dvd" && usableGb > 61 ? "Les disques de 100 Go sont refusés par beaucoup de platines : préférez deux disques de 50 Go." : null;

    /// <summary>Said with every Blu-ray, as a note and not as an error (Sony's own support says so).</summary>
    public const string Ps5Note = "À savoir : la PS5 ne lit aucun disque gravé, quel qu'il soit (Sony le dit dans son aide).";

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

    /// <summary>Where the window keeps the project it holds, rewritten after each change and reopened at its next start; never a file the user saved.</summary>
    public static string AutosaveFile() => Path.Combine(AppFiles.Root(), "autosave" + Extension);

    /// <summary>Writes <paramref name="json"/> beside <paramref name="file"/>, then puts it in its place: a crash half way leaves the former file whole.</summary>
    public static void WriteWhole(string file, string json)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, true);
    }

    /// <summary>The titles as the disc carries them: on a trial disc (<see cref="Trial"/> &gt; 0) each one cut to that many seconds, a passage at each chapter.</summary>
    public static List<DiscTitle> OnDisc(IReadOnlyList<DiscTitle> titles, int chapterMinutes, int trial, Func<string, SourceInfo> probe) =>
        trial <= 0 ? [.. titles] : [.. titles.Select(t => t.Trial(probe(t.Video), chapterMinutes, trial))];

    /// <summary>What <see cref="Build"/> puts on the disc: the usable size, the titles (cut for a trial disc), the menu.</summary>
    (double Usable, List<DiscTitle> List, MenuDesign? Menu) Disc(int trial)
    {
        var usable = CapacityGB > 0 ? CapacityGB : CapacityGB < 0 ? 0 : Kind == "dvd" ? 4.2 : Kind == "uhd" ? 93 : 23.3; // below 0 = no size limit, which the builds and the checks call 0
        var list = OnDisc(Titles ?? [], ChapterMinutes, trial, BluRay.Probe);
        return (usable, list, Menu || list.Count(t => !t.Intro) > 1 ? Design ?? TemplateCatalog.DefaultFor([.. list.Where(t => !t.Bonus && !t.Intro).Select(t => t.Video)]).Clone() : null);
    }

    /// <summary>Threshold above usable disc capacity (+30 %) where a manual multi-disc split is suggested to limit quality loss.</summary>
    public const double SplitSuggestionRatio = 1.30;

    /// <summary>Estimated size in GB at the project quality before fit-to-disc reduces the bit rate.</summary>
    public double EstimateUnfittedGB(IReadOnlyList<(DiscTitle Title, SourceInfo Src)> items, MenuDesign? menu = null)
    {
        if (items.Count == 0) return 0;
        var enc = Encode ?? new EncodeOptions(FitDisc: true);
        if (Kind == "dvd") return Dvd.EstimateGB(items.Select(t => (t.Src, t.Title)).ToList(), 0, enc.DvdKbps) * 1.05
            + (menu != null ? 0.2 : 0);
        return BluRay.EstimateGB(new BluRayJob(items.Select(t => t.Title).ToList(), "", menu,
            Kind == "uhd", Encode: enc with { FitDisc = false }, HdmvMenu: HdmvMenu), items.Select(t => t.Src).ToList());
    }

    /// <summary>True when a multi-video project exceeds the disc's usable capacity by at least 30 % before fit-to-disc compression.</summary>
    public bool ShouldSuggestSplit(out double estimatedGb, out double usableGb, IReadOnlyList<(DiscTitle Title, SourceInfo Src)>? probed = null)
    {
        usableGb = CapacityGB > 0 ? CapacityGB : CapacityGB < 0 ? 0 : Kind == "dvd" ? 4.2 : Kind == "uhd" ? 93 : 23.3;
        var clips = probed ?? (Titles ?? []).Select(t => (Title: t, Src: t.Cut(BluRay.Probe(t.Video)))).ToList();
        var menu = Menu || clips.Count(t => !t.Title.Intro) > 1 ? Design : null;
        estimatedGb = EstimateUnfittedGB(clips, menu);
        return clips.Count(t => !t.Title.Intro) > 1 && usableGb > 0 && estimatedGb >= usableGb * SplitSuggestionRatio;
    }

    /// <summary>Manual volume plan from ordered counts per disc (e.g. [5, 5] for two discs of 5 episodes).</summary>
    public List<(Project Disc, double EstimatedGb)> Volumes(string? folder, IReadOnlyList<int> counts)
    {
        if (counts.Count == 0 || counts.Any(c => c <= 0))
            throw new ArgumentException("Indiquez le nombre de vidéos par disque (entiers positifs, ex. 5,5).");
        var groups = new List<IReadOnlyList<int>>(counts.Count);
        var offset = 0;
        foreach (var count in counts)
        {
            groups.Add(Enumerable.Range(offset, count).ToList());
            offset += count;
        }
        return Volumes(folder, groups);
    }

    /// <summary>Manual volume plan from explicit 0-based indices into the non-intro titles for each disc.</summary>
    public List<(Project Disc, double EstimatedGb)> Volumes(string? folder, IReadOnlyList<IReadOnlyList<int>> discIndices,
        IReadOnlyList<(DiscTitle Title, SourceInfo Src)>? probed = null)
    {
        var (_, titles, menu) = probed != null
            ? (0.0, probed.Select(p => p.Title).ToList(), Menu || probed.Count(t => !t.Title.Intro) > 1 ? Design : null)
            : Disc(0);
        if (titles.Count(t => t.Intro) > 1 || !titles.Any(t => !t.Intro && !t.Bonus))
            throw new ArgumentException("Le projet doit contenir des épisodes et au plus une introduction.");
        menu ??= Design ?? TemplateCatalog.DefaultFor([.. titles.Where(t => !t.Intro && !t.Bonus).Select(t => t.Video)]).Clone();
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder ?? Out
            ?? throw new ArgumentException("Indiquez un dossier de sortie avec --out.")));
        var clips = probed ?? titles.Select(t => (Title: t, Src: t.Cut(BluRay.Probe(t.Video)))).ToList();
        var intro = clips.Where(t => t.Title.Intro).ToList();
        var episodes = clips.Where(t => !t.Title.Intro).ToList();
        if (discIndices.Count == 0 || discIndices.Any(g => g.Count == 0))
            throw new ArgumentException("Chaque disque doit contenir au moins une vidéo.");
        var seen = new HashSet<int>();
        foreach (var group in discIndices)
            foreach (var index in group)
                if (index < 0 || index >= episodes.Count || !seen.Add(index))
                    throw new ArgumentException($"La répartition manuelle ne correspond pas aux {episodes.Count} vidéos du projet.");
        if (seen.Count != episodes.Count)
            throw new ArgumentException($"La répartition manuelle doit couvrir exactement les {episodes.Count} vidéos du projet (actuellement {seen.Count}).");

        var result = new List<(Project Disc, double EstimatedGb)>(discIndices.Count);
        for (var i = 0; i < discIndices.Count; i++)
        {
            var n = i + 1;
            var group = discIndices[i].Select(idx => episodes[idx]).ToList();
            var items = intro.Concat(group).ToList();
            var design = menu.Clone();
            design.Title = $"{design.Title} · Disque {n}";
            result.Add((this with
            {
                Titles = items.Select(t => t.Title).ToList(),
                Design = design,
                Menu = true,
                Label = $"{Label ?? "BURNSMITH"}_{n:00}",
                Out = $"{output}_disque_{n:00}",
                Iso = true,
                Trial = 0
            }, EstimateUnfittedGB(items, design)));
        }
        return result;
    }

    /// <summary>« À savoir avant de démarrer » for this project (see Core.Recap): what <see cref="Build"/> would do, nothing built. `burnsmith project FILE --recap`.</summary>
    public List<string> Recap(int trial)
    {
        if (MaxCompat && Compatible(Titles ?? [], Encode ?? new(FitDisc: true), BluRay.Probe) is var (compatible, encode, hdmv))
            return (this with { MaxCompat = false, Titles = compatible, Encode = encode, HdmvMenu = hdmv }).Recap(trial);
        var (usable, list, menu) = Disc(trial);
        return Kind == "dvd" ? Core.Recap.For(new DvdJob(list, "", menu, ChapterMinutes, usable, Encode ?? new(FitDisc: true), Playback, Subtitles))
            : Core.Recap.For(new BluRayJob(list, "", menu, Kind == "uhd", "X", ChapterMinutes, Encode ?? new(FitDisc: true), usable, Playback, Subtitles, HdmvMenu));
    }

    // not "<folder>.iso": that name could belong to the user's own file
    public static string IsoFile(string folder) => Path.TrimEndingDirectorySeparator(folder) + "_BurnSmith.iso";

    /// <summary>
    /// Builds this project's disc as « Démarrer » does, without burning: the folder (<paramref name="folder"/>, else <see cref="Out"/>, with "_essai" for a trial disc
    /// so that the real disc's folder is never replaced by it), its structure checked, and the ISO image beside it when <see cref="Iso"/> is set.
    /// Returns the problems the checks found (none = a good disc). What `burnsmith project`, `burnsmith queue` and the app's queue run.
    /// </summary>
    public List<string> Build(string? folder, int trial, Action<string> log, CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        if (MaxCompat && Compatible(Titles ?? [], Encode ?? new(FitDisc: true), BluRay.Probe) is var (compatible, encode, hdmv))
            return (this with { MaxCompat = false, Titles = compatible, Encode = encode, HdmvMenu = hdmv }).Build(folder, trial, log, cancel, progress);
        var dir = Path.GetFullPath(folder ?? (Out is { Length: > 0 } ? Path.TrimEndingDirectorySeparator(Out) + (trial > 0 ? "_essai" : "") : throw new ArgumentException("the project names no output folder")));
        var (usable, list, menu) = Disc(trial);
        if (Warning(Kind, usable) is { } warning) log(warning);
        if (Kind != "dvd") log(Ps5Note);
        var label = Label is { Length: > 0 } ? Label : "BURNSMITH";
        if (trial > 0) log($"trial disc: {trial} s of each video, in {dir}");
        if (Kind == "dvd") Dvd.Build(new DvdJob(list, dir, menu, ChapterMinutes, usable, Encode ?? new(FitDisc: true), Playback, Subtitles), log, cancel, progress);
        else BluRay.Build(new BluRayJob(list, dir, menu, Kind == "uhd", label, ChapterMinutes, Encode ?? new(FitDisc: true), usable, Playback, Subtitles, HdmvMenu), log, cancel, progress);
        File.Delete(IsoFile(dir)); // an image of an earlier build must never be burnt by mistake
        var found = DiscCheck.Folder(dir, usable / 0.89);
        if (!Iso || found.Count > 0) return found;
        cancel.ThrowIfCancellationRequested();
        global::BurnSmith.Core.Iso.Build(dir, IsoFile(dir), label, Kind == "dvd", log, progress, cancel);
        found.AddRange(DiscCheck.Image(IsoFile(dir), dir, usable / 0.89));
        return found;
    }
}
