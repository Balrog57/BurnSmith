using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BurnSmith.Core;
using BurnSmith.Core.Menu;

namespace BurnSmith.Cli;

/// <summary>Disc authoring CLI commands: bd, dvd, project, volumes and queue.</summary>
internal static class CliBuild
{
    internal static string? Opt(List<string> a, string name) => a.IndexOf(name) is var i and >= 0 && i + 1 < a.Count ? a[i + 1] : null;
    internal static IEnumerable<string> OptAll(List<string> a, string name) { for (var i = 0; i + 1 < a.Count; i++) if (a[i] == name) yield return a[i + 1]; }
    internal static List<int>? Numbers(List<string> a, string name) => Opt(a, name)?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

    /// <summary>--sub numbers the video's own tracks, then its --sub-file ones, which are kept whatever it says, once.</summary>
    internal static List<int> SubsToKeep(List<int>? asked, int own, int files, string video, Action<string> tell)
    {
        var missing = (asked ?? []).Where(i => i < 0 || i >= own + files).Distinct().ToList();
        if (missing.Count > 0) tell($"warning: --sub {string.Join(',', missing)}: {video} has no such subtitle ({own} of its own, {files} from --sub-file)");
        return [.. (asked ?? []).Except(missing).Union(Enumerable.Range(own, files))];
    }

    static bool Hdmv(List<string> a) => Opt(a, "--menu-mode") switch
    {
        null or "bdj" => false,
        "hdmv" => true,
        var other => throw new ArgumentException($"--menu-mode: bdj or hdmv expected, not '{other}'")
    };

    static int Trial(List<string> a) => !a.Contains("--trial") ? 0 : int.TryParse(Opt(a, "--trial"), out var seconds) && seconds > 0 ? seconds : 300;

    static int Finished(List<string> a, int code)
    {
        if (!a.Contains("--shutdown")) return code;
        Tools.ShutdownSoon();
        Console.WriteLine("shutdown: Windows shuts down in 60 s (shutdown /a stops it)");
        return code;
    }

    static int Make(List<string> a, string kind, List<DiscTitle> list, string folder, MenuDesign? menu, string label, int minutes, EncodeOptions enc, double usableGb, PlaybackOptions? play, SubtitleStyle? look, int trial, double checkGb)
    {
        list = Project.OnDisc(list, minutes, trial, BluRay.Probe);
        var hdmv = Hdmv(a);
        if (a.Contains("--max-compat")) (list, enc, hdmv) = Project.Compatible(list, enc, BluRay.Probe);
        if (Project.Warning(kind, usableGb) is { } warning) Console.WriteLine("warning: " + warning);
        if (kind != "dvd") Console.WriteLine("note: " + Project.Ps5Note);
        if (trial > 0) Console.WriteLine($"trial disc: {trial} s of each video");
        var avchd = kind != "dvd" && a.Contains("--avchd");
        try
        {
            if (kind == "dvd") Dvd.Build(new DvdJob(list, folder, menu, minutes, usableGb, enc, play, look), Console.WriteLine);
            else BluRay.Build(new BluRayJob(list, folder, menu, kind == "uhd", label, minutes, enc, usableGb, play, look, hdmv, avchd), Console.WriteLine);
        }
        catch (InvalidDataException e) { Console.Error.WriteLine(e.Message); return 1; }
        var checkFolder = avchd && !Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).Equals("AVCHD", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(folder, "AVCHD") : folder;
        var found = DiscCheck.Folder(checkFolder, checkGb);
        Console.WriteLine(found.Count == 0 ? "disc structure checked: no problem" : "disc structure: " + string.Join(" ; ", found));
        return found.Count == 0 ? 0 : 1;
    }

    static int BuildProject(List<string> a, string file)
    {
        var p = Project.Load(file) is var saved && a.Contains("--max-compat") ? saved with { MaxCompat = true } : saved;
        var found = p.Build(Opt(a, "--out") is { } folder ? Path.GetFullPath(folder) : null, a.Contains("--trial") ? Trial(a) : p.Trial, Console.WriteLine);
        Console.WriteLine(found.Count == 0 ? "disc checked: no problem" : "disc check: " + string.Join(" ; ", found));
        return found.Count == 0 ? 0 : 1;
    }

    public static int RunProject(List<string> a)
    {
        if (a.Count < 2) return Usage();
        if (a.Contains("--recap"))
        {
            var recapped = Project.Load(a[1]);
            recapped.Recap(a.Contains("--trial") ? Trial(a) : recapped.Trial).ForEach(Console.WriteLine);
            return 0;
        }
        return Finished(a, BuildProject(a, a[1]));
    }

    public static int RunVolumes(List<string> a)
    {
        if (a.Count < 2) return Usage();
        Project loaded;
        try { loaded = Project.Load(a[1]); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        var splitArg = Opt(a, "--split");
        if (splitArg == null)
        {
            if (a.Contains("--build"))
            {
                Console.Error.WriteLine("La répartition est manuelle : indiquez le nombre de vidéos par disque avec --split N1,N2,... (ex. --split 5,5).");
                return 2;
            }
            var suggest = loaded.ShouldSuggestSplit(out var estimatedGb, out var usableGb);
            var episodes = (loaded.Titles ?? []).Where(t => !t.Intro).ToList();
            Console.WriteLine(usableGb > 0
                ? $"Taille estimée avant compression : {estimatedGb:0.00} Go / {usableGb:0.#} Go utilisables ({episodes.Count} vidéos)."
                : $"Taille estimée avant compression : {estimatedGb:0.00} Go ({episodes.Count} vidéos).");
            if (suggest)
            {
                var overPct = (int)Math.Round((estimatedGb / usableGb - 1) * 100);
                var discs = Math.Clamp((int)Math.Ceiling(estimatedGb / usableGb), 2, episodes.Count);
                Console.WriteLine($"Suggestion : la saison dépasse le disque de {overPct} %. Une répartition manuelle sur {discs} disques limiterait la perte de qualité (ex. --split …).");
            }
            for (var i = 0; i < episodes.Count; i++) Console.WriteLine($"  {i + 1}. {episodes[i].DisplayName}");
            return 0;
        }
        var parts = splitArg.Split(',', StringSplitOptions.TrimEntries);
        var counts = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var n) || n <= 0)
            {
                Console.Error.WriteLine("--split attend des entiers positifs séparés par des virgules (ex. --split 5,5).");
                return 2;
            }
            counts.Add(n);
        }
        List<(Project Disc, double EstimatedGb)> volumes;
        try { volumes = loaded.Volumes(Opt(a, "--out"), counts); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        foreach (var (volume, estimate) in volumes)
        {
            Console.WriteLine($"{volume.Out} ({estimate:0.00} Go estimés) :");
            foreach (var episode in volume.Titles!) Console.WriteLine($"  {episode.DisplayName}");
        }
        if (!a.Contains("--build")) return 0;
        foreach (var (volume, _) in volumes)
        {
            var problems = volume.Build(null, 0, Console.WriteLine);
            if (problems.Count == 0) continue;
            foreach (var problem in problems) Console.Error.WriteLine(problem);
            return 1;
        }
        return 0;
    }

    public static int RunQueue(List<string> a)
    {
        if (a.Count < 2) return Usage();
        var files = a.Skip(1).Where(f => f.EndsWith(Project.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        var failed = new List<string>();
        foreach (var (file, n) in files.Select((f, n) => (f, n + 1)))
        {
            Console.WriteLine($"queue {n}/{files.Count}: {file}");
            try { if (BuildProject(a, file) != 0) failed.Add(file); }
            catch (Exception e) when (e is not OperationCanceledException) { failed.Add(file); Console.Error.WriteLine($"failed: {file}: {e.Message}"); }
        }
        Console.WriteLine(failed.Count == 0 ? $"queue: {files.Count} disc(s) made" : $"queue: {files.Count - failed.Count} of {files.Count} disc(s) made, failed: {string.Join(", ", failed)}");
        return Finished(a, failed.Count == 0 && files.Count > 0 ? 0 : 1);
    }

    public static int RunBuild(List<string> a)
    {
        var videos = a.Select((v, i) => (v, i)).Where(x => x.v == "--video" && x.i + 1 < a.Count).Select(x => BluRay.MainClip(Path.GetFullPath(a[x.i + 1]))).ToList();
        if (a.Count == 0 || a[0] is not ("bd" or "dvd") || videos.Count == 0 || (Opt(a, "--out") ?? Opt(a, "--save-project")) is not { } outDir)
            return Usage();

        List<string> MainVideos()
        {
            var at = a.Select((v, i) => (v, i)).Where(x => x.v == "--video").Select(x => x.i).Append(a.Count).ToList();
            return [.. videos.Where((_, k) => videos.Count == 1 || !a.Skip(at[k]).Take(at[k + 1] - at[k]).Any(f => f is "--bonus" or "--intro"))];
        }

        var design = a.Contains("--no-menu") ? null
            : Opt(a, "--design") is { } saved && saved.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(saved)) ?? throw new InvalidOperationException($"not a template: {saved}")
            : (TemplateCatalog.Find(Opt(a, "--design")) ?? (Opt(a, "--design") is { } unknown ? throw new InvalidOperationException($"unknown template: {unknown}") : TemplateCatalog.DefaultFor(MainVideos()))).Clone();

        if (design != null && Opt(a, "--design") == null) Console.WriteLine($"menu template: {design.Name} (none given: the last one chosen in the app, else one that suits the videos)");
        if (design != null && Opt(a, "--menu-aspect") is { } shape)
            design = shape is "4:3" or "16:9" ? TemplateCatalog.Shaped(design, shape == "4:3") is var twin && twin != design ? twin.Clone() : design
                : throw new ArgumentException($"--menu-aspect: 4:3 or 16:9 expected, not '{shape}'");
        if (design != null) design.Language(Opt(a, "--menu-language") is not { } spoken ? design.ButtonLanguage
            : spoken is "fr" or "en" ? spoken : throw new ArgumentException($"--menu-language: fr or en expected, not '{spoken}'"));
        if (design != null && Opt(a, "--title") is { } title) design.Title = title;
        if (design != null && Opt(a, "--highlight") is { } hlStyle) design.HighlightStyle = hlStyle;
        if (design != null && a.Contains("--list")) design.SceneList = true;
        if (design != null && a.Contains("--animated-thumbs")) design.AnimatedThumbs = true;
        if (design != null && a.Contains("--popup")) design.Popup = true;
        if (design != null && a.Contains("--scenes-only")) design.ScenesOnly = true;
        if (design != null && a.Contains("--skip-first-scene")) design.SkipFirstScene = true;
        if (design != null && a.Contains("--bonus-text")) design.BonusText = true;
        if (design != null && Opt(a, "--popup-main") is { } popupMain) design.MainMenuLabel = popupMain;
        if (design != null && Opt(a, "--popup-resume") is { } popupResume) design.ResumeLabel = popupResume;
        if (design != null && Opt(a, "--credits") is { } credits) design.Credits = credits.Replace('|', '\n');
        foreach (var spec in OptAll(a, "--page-bg"))
        {
            var eq = spec.IndexOf('=');
            if (design == null || eq < 1 || !File.Exists(spec[(eq + 1)..])) throw new ArgumentException($"--page-bg: kind=picture expected (root=fond.jpg), with an existing picture and a menu: '{spec}'");
            design.PageBackgrounds[spec[..eq].Trim().ToLowerInvariant()] = Path.GetFullPath(spec[(eq + 1)..]);
        }
        if (design != null && Opt(a, "--button-language") is { } words)
            design.ButtonLanguage = words is "fr" or "en" ? words : throw new ArgumentException($"--button-language: fr or en expected, not '{words}'");
        if (design != null && int.TryParse(Opt(a, "--background-blur"), out var blurred)) design.BackgroundBlur = blurred;
        foreach (var spec in OptAll(a, "--element")) design?.Elements.Add(MenuElement.Parse(spec));
        if (design != null && Opt(a, "--titles") is { } byLanguage) design.TitlesByLanguage = byLanguage.Replace('=', ':').Replace('|', '\n');
        if (design != null && a.Contains("--loop"))
        {
            design.Loop = true;
            design.LoopFile = Opt(a, "--loop-file") is { } lf ? Path.GetFullPath(lf) : null;
            design.LoopStart = int.TryParse(Opt(a, "--loop-start"), out var ls) ? ls : 0;
        }
        if (design != null && Opt(a, "--loop-seconds") is { } lsecs) design.LoopSeconds = lsecs == "music" ? 0 : int.TryParse(lsecs, out var lsec) ? lsec : design.LoopSeconds;
        if (design != null && Opt(a, "--music") is { } mu)
        {
            design.Music = Path.GetFullPath(mu);
            if (int.TryParse(Opt(a, "--music-start"), out var ms)) design.MusicStart = ms;
            if (int.TryParse(Opt(a, "--music-volume"), out var mv)) design.MusicVolume = mv;
            if (int.TryParse(Opt(a, "--music-fade"), out var mf)) design.MusicFade = mf;
        }
        var chapters = int.TryParse(Opt(a, "--chapters"), out var m) ? m : 5;
        var videoAt = a.Select((v, i) => (v, i)).Where(x => x.v == "--video").Select(x => x.i).ToList();
        var titles = videos.Select((v, k) =>
        {
            var own = videoAt.Count == 1 ? a : a.Skip(videoAt[k]).Take(k + 1 < videoAt.Count ? videoAt[k + 1] - videoAt[k] : a.Count).ToList();
            string? Own(string name) => own.IndexOf(name) is var i and >= 0 && i + 1 < own.Count ? own[i + 1] : null;
            double? Time(string text) => text.Length == 0 ? null : Timecode.Parse(text) ?? throw new ArgumentException($"not a time: '{text}'");
            var clips = Own("--trim")?.Split(',').Select(r => r.Split('-', 2)).ToList() ?? [["", ""]];
            var trim = clips[0].Length > 1 ? [clips[0][0], clips[^1][1]] : clips[0];
            List<Clip>? clipList = clips.Count > 1 ? [.. clips.Select(c => new Clip(Time(c[0]) ?? throw new ArgumentException("--trim: each clip needs its start"), (c.Length > 1 ? Time(c[1]) : null) ?? throw new ArgumentException("--trim: each clip needs its end")))] : null;
            var volume = int.TryParse(Own("--volume"), out var vol) ? Math.Clamp(vol, 10, 300) : 100;
            IReadOnlyList<double>? chapterAt = own.Contains("--no-chapters") ? [] : Own("--chapter-at")?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => Time(t.Trim()) ?? 0).ToList();
            int? chapterMinutes = int.TryParse(Own("--chapter-every"), out var every) ? Math.Clamp(every, 1, 600) : null;
            IReadOnlyList<string?>? chapterNames = null;
            if (Own("--chapters-file") is { } chapFile)
            {
                var loaded = ChapterFiles.Load(chapFile);
                chapterAt = [.. loaded.Select(c => c.Time)];
                chapterNames = loaded.Any(c => c.Name != null) ? [.. loaded.Select(c => c.Name)] : null;
            }
            if (chapterAt == null && chapterMinutes == null && Opt(a, "--chapters") == null) (chapterAt, chapterNames) = (BluRay.Probe(v).Chapters, BluRay.Probe(v).ChapterNames);
            if (Own("--chapter-names") is { } named)
                chapterNames = chapterAt is { Count: > 0 } ? [.. named.Split('|').Select(n => n.Trim().Length > 0 ? n.Trim() : null)]
                    : throw new ArgumentException("--chapter-names names the chapters of --chapter-at, or those the file holds: this video has neither");
            Dictionary<int, double>? audioDelay = null;
            foreach (var pair in Own("--audio-delay")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
                (audioDelay ??= [])[pair.Split('=') is [var n, var s] && int.TryParse(n, out var track) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var late)
                    ? track : throw new ArgumentException($"--audio-delay: an audio track number and seconds are expected (0=0.2), not '{pair}'")] = late;
            Dictionary<int, double>? subDelay = null;
            foreach (var pair in Own("--sub-delay")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
                (subDelay ??= [])[pair.Split('=') is [var n, var s] && int.TryParse(n, out var track) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var late)
                    ? track : throw new ArgumentException($"--sub-delay: a subtitle number and seconds are expected (0=1.5), not '{pair}'")] = late;
            var files = own.Select((x, i) => (x, i)).Where(x => x.x == "--sub-file" && x.i + 1 < own.Count).SelectMany(x => ExternalSubtitle.ParseAll(own[x.i + 1])).ToList();
            List<int>? subs = Own("--sub") is "none" ? [] : Own("--sub") is { } ownSubs ? [.. ownSubs.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)] : Opt(a, "--sub") is "none" ? null : Numbers(a, "--sub");
            if (subs != null || files.Count > 0) subs = SubsToKeep(subs, BluRay.Probe(v).Subtitles.Count, files.Count, Path.GetFileName(v), Console.Error.WriteLine);
            int? defaultAudio = int.TryParse(Own("--default-audio"), out var da) ? da : null, defaultSub = int.TryParse(Own("--default-sub"), out var ds) ? ds : null;
            List<int>? audioKept = Own("--audio") is { } ownAudio ? [.. ownAudio.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)] : Numbers(a, "--audio");
            var audioFiles = own.Select((x, i) => (x, i)).Where(x => x.x == "--audio-file" && x.i + 1 < own.Count).Select(x =>
            {
                var spec = own[x.i + 1];
                var eq = spec.IndexOf('=');
                var lang = eq is 2 or 3 ? Languages.Code(spec[..eq]) : null;
                var file = lang == null ? spec : spec[(eq + 1)..];
                if (!File.Exists(file)) throw new ArgumentException($"--audio-file: file not found: {file}");
                var track = AudioTrack.FromFile(v, file);
                return lang == null ? track : track with { Lang = lang };
            }).ToList();
            if (audioFiles.Count > 0)
            {
                var videoAudio = BluRay.Probe(v).Audio;
                audioKept = [.. (audioKept ?? [.. videoAudio.Take(1).Select(t => t.Index)]).Union(audioFiles.Select((_, k) => videoAudio.Count + k))];
            }
            Dictionary<int, double>? audioVolume = null;
            foreach (var pair in Own("--audio-volume")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
                (audioVolume ??= [])[pair.Split('=') is [var n, var s] && int.TryParse(n, out var track) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var db) && Math.Abs(db) <= 30
                    ? track : throw new ArgumentException($"--audio-volume: an audio track number and dB from -30 to 30 are expected (0=-3), not '{pair}'")] = db;
            if (defaultAudio is { } wanted && !(audioKept ?? [0]).Contains(wanted)) throw new ArgumentException($"--default-audio {wanted} is not one of the audio tracks kept (--audio)");
            if (defaultSub is { } wantedSub && !(subs ?? []).Contains(wantedSub)) throw new ArgumentException($"--default-sub {wantedSub} is not one of the subtitles kept (--sub / --sub-file)");
            Dictionary<string, string>? trackLanguage = null;
            foreach (var pair in Own("--track-lang")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
            {
                var kv = pair.Split('=', 2);
                var code = kv.Length == 2 ? (kv[1].Trim() == "und" ? "und" : Languages.Code(kv[1].Trim())) : null;
                if (!Regex.IsMatch(kv[0].Trim(), "^[as][0-9]+$") || code == null) throw new ArgumentException($"--track-lang: a track and a language are expected (a0=fre,s1=eng), not '{pair}'");
                (trackLanguage ??= [])[kv[0].Trim()] = code;
            }
            return new DiscTitle(v, Own("--name"), audioKept, subs, Time(trim[0]) ?? 0, trim.Length > 1 ? Time(trim[1]) : null, volume, chapterAt, files.Count > 0 ? files : null, own.Contains("--bonus"), defaultAudio, defaultSub, chapterMinutes, own.Contains("--intro"), trackLanguage,
                                  Own("--effects") is { } fx ? JsonSerializer.Deserialize<VideoEffects>(File.ReadAllText(fx)) : null,
                                  clipList,
                                  Own("--thumb-at") is { } thumbAt ? Time(thumbAt) : null, Own("--thumb") is { } thumb ? Path.GetFullPath(thumb) : null,
                                  Own("--overview"),
                                  subDelay, audioDelay, chapterNames, audioFiles.Count > 0 ? audioFiles : null, audioVolume);
        }).ToList();

        var profile = BdProfile.Find(Opt(a, "--profile") ?? "BD25");
        var quality = Opt(a, "--quality") ?? "fit";
        if (quality is not ("fit" or "source") && !int.TryParse(quality, out _)) throw new ArgumentException($"--quality: source, fit or a number is expected, not '{quality}'");
        var rate = int.TryParse(quality, out var q) ? (q < 1000 ? q * 1000 : q) : 0;
        var encode = new EncodeOptions(a.Contains("--reencode"), int.TryParse(Opt(a, "--crf"), out var crf) ? crf : 17, Opt(a, "--preset") ?? "medium", int.TryParse(Opt(a, "--dvd-kbps"), out var kbps) ? kbps : 0,
                                       rate, quality == "fit", profile.Hd720, Gpu: !a.Contains("--cpu"),
                                       Fps: double.TryParse(Opt(a, "--fps"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ? fps : null,
                                       Lossless: !a.Contains("--no-lossless"), CopyEac3: !a.Contains("--no-copy-eac3"), NormalizeAudio: a.Contains("--loudnorm"));
        var capacity = double.TryParse(Opt(a, "--capacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) ? gb : profile.UsableGb;
        var playback = int.TryParse(Opt(a, "--play-mode"), out var mode) ? PlaybackOptions.FromMode(mode, !a.Contains("--no-rewind")) : a.Contains("--no-rewind") ? new PlaybackOptions(Rewind: false) : null;
        if (int.TryParse(Opt(a, "--auto-start"), out var autoStart))
            playback = (playback ?? new PlaybackOptions()) with { AutoStartSeconds = Math.Clamp(autoStart, 0, 300) };
        if (a.Contains("--remember-position")) playback = (playback ?? new PlaybackOptions()) with { RememberPosition = true };
        var hdrNits = int.TryParse(Opt(a, "--hdr-nits"), out var nits) ? nits : 203;
        if (design != null && Opt(a, "--hdr-nits") != null) design.HdrGraphicsNits = hdrNits;
        var subtitleLook = Opt(a, "--sub-font") == null && Opt(a, "--sub-size") == null && Opt(a, "--sub-color") == null && Opt(a, "--sub-position") == null && Opt(a, "--hdr-nits") == null ? null
            : new SubtitleStyle(Opt(a, "--sub-font"), int.TryParse(Opt(a, "--sub-size"), out var subSize) ? subSize : 100, Opt(a, "--sub-color")?.TrimStart('#') ?? "FFFFFF", int.TryParse(Opt(a, "--sub-position"), out var subPosition) ? subPosition : 0, hdrNits);

        if (a.Contains("--preview"))
        {
            BluRay.PreviewMenu(new BluRayJob(titles, Path.GetFullPath(outDir), design, a.Contains("--uhd"), Opt(a, "--label") ?? "BURNSMITH", chapters, encode, capacity, playback), Path.GetFullPath(outDir), Console.WriteLine, a.Contains("--safe-area"));
            return 0;
        }
        var dvdDisc = a[0] == "dvd";
        if (dvdDisc) (encode, capacity) = (encode with { DvdPal = a.Contains("--pal") ? true : a.Contains("--ntsc") ? false : null, DvdWide = !a.Contains("--4x3") }, Opt(a, "--capacity") != null ? capacity : 4.2);
        var discKind = dvdDisc ? "dvd" : a.Contains("--uhd") ? "uhd" : "bd";
        if (a.Contains("--recap"))
        {
            var recapTitles = Project.OnDisc(titles, chapters, Trial(a), BluRay.Probe);
            (dvdDisc ? Recap.For(new DvdJob(recapTitles, "", design, chapters, capacity, encode, playback, subtitleLook))
                     : Recap.For(new BluRayJob(recapTitles, "", design, discKind == "uhd", "X", chapters, encode, capacity, playback, subtitleLook, Hdmv(a), a.Contains("--avchd")))).ForEach(Console.WriteLine);
            return 0;
        }
        if (Opt(a, "--save-project") is { } projectFile)
        {
            new Project(discKind, titles, design, design != null, Opt(a, "--label"), chapters, encode, playback, subtitleLook, null, Opt(a, "--capacity") != null && capacity <= 0 ? -1 : capacity, Opt(a, "--out") is { } kept ? Path.GetFullPath(kept) : null, false, Trial(a), Hdmv(a), MaxCompat: a.Contains("--max-compat"), Avchd: a.Contains("--avchd")).Save(projectFile);
            Console.WriteLine("ok   " + Path.GetFullPath(projectFile));
            return 0;
        }
        return Finished(a, Make(a, discKind, titles, Path.GetFullPath(outDir), design, Opt(a, "--label") ?? "BURNSMITH", chapters, encode, capacity, playback, subtitleLook, Trial(a),
                    Opt(a, "--capacity") != null && capacity <= 0 ? 0 : Opt(a, "--capacity") != null ? capacity / 0.89 : dvdDisc ? 4.7 : profile.Gb));
    }

    internal static int Usage()
    {
        Console.Error.WriteLine("usage: burnsmith bd|dvd --video <file> [options...] --out <folder> [options...]  |  burnsmith project FILE.burnsmith  |  burnsmith volumes FILE.burnsmith [--split N1,N2]  |  burnsmith queue A.burnsmith ...  |  burnsmith --help");
        return 2;
    }
}
