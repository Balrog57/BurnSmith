using System.Globalization;
using System.Text.RegularExpressions;
using BurnSmith.Core.DvdVideo;
using BurnSmith.Core.Menu;
using static BurnSmith.Core.Menu.IndexedPng;

namespace BurnSmith.Core;

/// <summary>DVD-Video job: one title per entry of <see cref="Titles"/>, the bonus ones (<see cref="DiscTitle.Bonus"/>) after the others. <see cref="Menu"/> null = the disc starts straight on the movie (a single title only).
/// <see cref="Playback"/>: what the disc does at the start and after a title (null = back to the menu after the picked title).</summary>
public sealed record DvdJob(IReadOnlyList<DiscTitle> Titles, string OutDir, MenuDesign? Menu = null, int ChapterMinutes = 5, double CapacityGB = 4.2, EncodeOptions? Encode = null,
                            PlaybackOptions? Playback = null);

/// <summary>ffmpeg (MPEG-2 + AC-3, then the subpictures muxed in: button highlights, subtitles) -> <see cref="Authoring"/> (VIDEO_TS with menus).</summary>
public static class Dvd
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static int AudioBps(AudioTrack a) => a.Channels > 2 ? 448_000 : 192_000;

    /// <summary>Video bit rate that fills <paramref name="capacityGB"/> (usable GB) with all titles and their kept audio, capped at 8 Mb/s.</summary>
    public static int VideoBitrate(IReadOnlyList<(SourceInfo Src, DiscTitle Title)> items, double capacityGB)
    {
        var seconds = items.Sum(i => i.Src.Duration);
        var audioBits = items.Sum(i => i.Src.Duration * i.Title.KeptAudio(i.Src).Sum(AudioBps));
        return (int)Math.Clamp((capacityGB * 1e9 * 8 * 0.96 - audioBits) / seconds, 1_000_000, 8_000_000);
    }

    /// <param name="kbps">The video bit rate chosen by hand (0 = what fills the disc).</param>
    public static double EstimateGB(IReadOnlyList<(SourceInfo Src, DiscTitle Title)> items, double capacityGB, int kbps = 0)
    {
        var v = kbps > 0 ? Math.Clamp(kbps, 1500, 9000) * 1000 : VideoBitrate(items, capacityGB);
        return items.Sum(i => i.Src.Duration * (v + i.Title.KeptAudio(i.Src).Sum(AudioBps))) / 8 / 1e9;
    }

    /// <summary>One encoded title: the MPEG file, the audio and subtitle tracks that really are in it, its chapter times.</summary>
    sealed record Movie(string Mpg, List<AudioTrack> Audio, List<SubtitleTrack> Subtitles, List<double> ChapterTimes, int DefaultSubtitle);

    public static void Build(DvdJob job, Action<string>? log, CancellationToken cancel) => Tools.WithCancel(cancel, () => Build(job, log));

    public static void Build(DvdJob job, Action<string>? log = null)
    {
        log ??= _ => { };
        Tools.KeepInputs(DiscTitle.Inputs(job.Titles, job.Menu), job.OutDir, job.OutDir + "_work");
        var intro = job.Titles.FirstOrDefault(t => t.Intro);
        if (job.Titles.Count(t => t.Intro) > 1) throw new ArgumentException("une seule vidéo de début est possible");
        job = job with { Titles = [.. job.Titles.Where(t => !t.Intro)] }; // the start video is no title of the menu: it is the title the first-play jump goes to
        if (job.Titles.Count == 0) throw new ArgumentException("no video to put on the disc");
        if (job.Titles.Count > 1 && job.Menu == null) throw new InvalidOperationException("several videos need a menu to choose from");
        job = job with { Titles = DiscTitle.MainFirst(job.Titles) }; // titles 1..mainCount are the feature, the bonus ones follow
        var mainCount = job.Titles.Count(t => !t.Bonus);
        var sources = job.Titles.Select(t => t.Cut(BluRay.Probe(t.Video))).ToList(); // durations from here on are the parts kept
        var items = sources.Zip(job.Titles, (s, t) => (Src: s, Title: t)).ToList();
        var introSource = intro?.Cut(BluRay.Probe(intro.Video));
        if (introSource != null) items.Add((introSource, intro!)); // it takes room on the disc too
        var pal = job.Encode?.DvdPal ?? (Math.Abs(sources[0].Fps - 25) < 0.5 || Math.Abs(sources[0].Fps - 50) < 0.5); // chosen, else the disc follows its first video
        var work = job.OutDir + "_work";
        var videoBps = job.Encode is { DvdKbps: > 0 } manual ? Math.Clamp(manual.DvdKbps, 1500, 9000) * 1000 : VideoBitrate(items, job.CapacityGB); // by hand, else what fills the disc
        // the fitted rate stops at 1 Mb/s: past that (a season with three languages on a DVD-5) the disc is too small, as the Blu-ray build says too
        if (EstimateGB(items, job.CapacityGB, job.Encode?.DvdKbps ?? 0) is var gb && gb > job.CapacityGB)
            log($"warning: about {gb:0.0} GB, more than the {job.CapacityGB:0.#} GB of the disc: keep fewer audio tracks or take a bigger disc");
        // before the earlier disc is emptied and anything is encoded: a job that cannot finish is told at once
        CheckRoom(job.OutDir, NeededBytes(job, items, videoBps));
        Tools.FreshFolder(work); // first: a locked file there must not cost the earlier disc
        try { Tools.PrepareOutput(job.OutDir); Author(job, intro, introSource, sources, videoBps, pal, mainCount, work, log); }
        finally { Tools.TryDelete(work); } // temporary videos of many GB: a failed build must not leave them behind
        log("done: " + job.OutDir);
    }

    /// <summary>
    /// The disc space a build takes at its fullest, in the work folder and the output (one drive, the first being named after the other): each MPEG stays there while its VOB
    /// is written, and muxing the subtitles in makes one more copy of it. 5 % for the program stream around the video and the sound.
    /// shortcut: the menu is a rough ceiling (its pages are not known yet, a looping page weighs about 0.5 MB/s in three copies), work it out from the pages if it refuses a build that would fit.
    /// </summary>
    public static long NeededBytes(DvdJob job, IReadOnlyList<(SourceInfo Src, DiscTitle Title)> items, int videoBps)
    {
        var movies = items.Sum(i => i.Src.Duration * (videoBps + i.Title.KeptAudio(i.Src).Take(8).Sum(AudioBps))) / 8;
        var copies = job.Titles.Any(t => !t.Intro && t.Subtitles is { Count: > 0 }) ? 3 : 2;
        var menu = job.Menu is not { } m ? 0 : 0.2e9 + (m.Loop || m.ExistingMusic() != null ? BluRay.LoopLength(m, 120) * 18e6 : 0);
        return (long)(movies * copies * 1.05 + menu);
    }

    /// <summary>Refuses a build the output's drive has no room for; the earlier disc in that folder counts as free, it goes first.</summary>
    static void CheckRoom(string outDir, long needed)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outDir))!).AvailableFreeSpace;
            if (Directory.Exists(outDir)) free += new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return; } // a network folder has no drive to ask: ffmpeg will say when it is full
        if (free < needed) throw new IOException($"Pas assez de place pour ce DVD sur {Path.GetPathRoot(Path.GetFullPath(outDir))} : il faut environ {needed / 1e9:0.0} Go pendant la création, {free / 1e9:0.0} Go sont libres.");
    }

    static void Author(DvdJob job, DiscTitle? intro, SourceInfo? introSource, List<SourceInfo> sources, int videoBps, bool pal, int mainCount, string work, Action<string> log)
    {
        log($"movie: {(pal ? "PAL" : "NTSC")} {videoBps / 1000} kb/s");
        var movies = new List<Movie>();
        var lined = job.Titles.Count > 1 ? DiscTitle.LineUpSubtitles(job.Titles, sources) : null;
        for (var i = 0; i < job.Titles.Count; i++) movies.Add(EncodeTitle(job, i + 1, sources[i], job.Titles[i], lined?[i], pal, videoBps, work, log));
        if (movies.Any(m => m.Audio.Count != movies[0].Audio.Count)) // the subtitles are lined up (LineUpSubtitles): only a title's extra ones come after the menu's
            log("warning: the videos do not have the same number of audio tracks; the menu follows the first one");
        Movie? introMovie = null;
        if (intro != null) // one audio track and no subtitles: nothing to choose while it plays, and no subtitle pass
            introMovie = EncodeTitle(job, movies.Count + 1, introSource!, intro with { Audio = [.. intro.KeptAudio(introSource!).Take(1).Select(a => a.Index)], Subtitles = null, External = null, DefaultAudio = null, DefaultSubtitle = null }, null, pal, videoBps, work, log);

        var menuPages = new List<MenuOut>();
        // a menu that plays on its own (moving clip or music) starts again when it ends: each page is its own PGC, so the sound restarts when another page opens
        var restarts = job.Menu is { } design && (design.Loop || design.ExistingMusic() != null);
        if (job.Menu != null) menuPages = BuildMenus(job, sources, movies, work, pal, log);
        if (restarts)
        {
            var menuBytes = menuPages.Sum(m => new FileInfo(m.Mpg).Length);
            log($"menu: {menuPages.Count} pages, {menuBytes / 1e6:0} MB on the disc");
            // shortcut: the bit rate above is fixed before the menu exists, so it cannot reserve this; warn instead, lower the loop length or the pages to fix
            if (movies.Sum(m => new FileInfo(m.Mpg).Length) + menuBytes > job.CapacityGB * 1e9)
                log("warning: the movie and the looping menu together may not fit the disc; shorten the menu loop or use a lower quality");
        }

        Authoring.Write(DiscOf(pal, movies, mainCount, menuPages, restarts, introMovie, job.Encode?.DvdWide ?? true, job.Playback ?? new()), job.OutDir, log);
        Directory.CreateDirectory(Path.Combine(job.OutDir, "AUDIO_TS")); // empty, but players and tools expect it
    }

    /// <summary><paramref name="lined"/>: this title's subtitles lined up with the other titles' (<see cref="DiscTitle.LineUpSubtitles"/>), null when it is alone.</summary>
    static Movie EncodeTitle(DvdJob job, int number, SourceInfo src, DiscTitle title, IReadOnlyList<SubtitleTrack>? lined, bool pal, int videoBps, string work, Action<string> log)
    {
        var (w, h) = pal ? (720, 576) : (720, 480);
        var wide = job.Encode?.DvdWide ?? true;
        var sar = wide ? (pal ? "64/45" : "32/27") : pal ? "16/15" : "8/9";
        var audio = title.KeptAudio(src).Take(8).ToList(); // a DVD title carries at most 8 audio streams
        var movie = Path.Combine(work, $"movie{number}.mpg");
        // movie: square pixels -> 16:9 (or 4:3) box with bars -> DVD frame size and aspect
        var ntscFilm = !pal && src.Fps < 25;
        var rate = pal ? "fps=25" : ntscFilm ? "fps=24000/1001,telecine=pattern=23,setfield=tff" : "fps=30000/1001"; // film: real 3:2 pulldown, top field first (ffmpeg 9 removed -top; without this the file says bottom first)
        // -ss / -t before -i cut the input itself: the video, the sound and the subtitle files written below all start and stop together
        var args = new List<string> { "-y", "-v", "warning" };
        if (title.Start > 0) args.AddRange(["-ss", title.Start.ToString(Inv)]);
        var span = src.Duration + title.GapLength; // the part read: from the first clip's start to the last one's end
        if (title.End != null) args.AddRange(["-t", span.ToString("0.###", Inv)]);
        args.AddRange(["-i", title.Video, "-map", "0:v:0"]);
        foreach (var a in audio) args.AddRange(["-map", $"0:a:{a.Index}"]);
        var fit = $"{src.HalfStereo}scale=trunc(iw*sar/2)*2:ih,setsar=1,scale={(wide ? 1024 : 768)}:576:force_original_aspect_ratio=decrease,pad={(wide ? 1024 : 768)}:576:(ow-iw)/2:(oh-ih)/2,scale={w}:{h}:in_color_matrix={(src.Height >= 720 || src.Hdr != "SDR" ? "bt709" : "bt601")}:out_color_matrix=bt601,setsar={sar},{rate}"; // a player decodes a DVD as BT.601
        var plain = title.Effects is null or { IsEmpty: true }; // effects are placed in the source's pixels: with some, the picture keeps its size until they are drawn
        var vf = (title.Effects ?? new()).Vf(src.Width, src.Height, title.Start, fit, plain ? BluRay.ToSdr(src.Hdr, src.Width, src.Height, wide ? 1024 : 768) : BluRay.ToSdr(src.Hdr)); // a DVD has no HDR
        if (title.GapFilter(false) is { Length: > 0 } dropV) vf += "," + dropV;
        var af = string.Join(",", new[] { title.GapFilter(true), title.Volume != 100 ? $"volume={(title.Volume / 100.0).ToString("0.###", Inv)}" : "" }.Where(f => f.Length > 0));
        args.AddRange(["-vf", vf,
            "-target", pal ? "pal-dvd" : "ntsc-dvd", "-aspect", wide ? "16:9" : "4:3", "-b:v", $"{videoBps / 1000}k"]);
        if (audio.Count == 0) args.Add("-an");
        for (var j = 0; j < audio.Count; j++)
        {
            args.AddRange([$"-b:a:{j}", $"{AudioBps(audio[j]) / 1000}k"]); // -target already asks for AC-3: naming it again only earns a warning from ffmpeg
            if (audio[j].Channels > 6) args.AddRange([$"-ac:a:{j}", "6"]); // AC-3 tops out at 5.1
            if (af.Length > 0) args.AddRange([$"-filter:a:{j}", af]);
        }
        if (ntscFilm) args.AddRange(["-flags", "+ildct+ilme"]);
        args.AddRange(["-sn", "-dn", movie]);

        // text subtitles are drawn by DvdSubtitles, PGS pictures redrawn; other bitmap formats would need OCR
        var subs = BluRay.Subtitles(title, src, lined, n => Path.Combine(work, $"sub{number}_{n}"), log);
        var real = subs.Where(x => x.Track.Index >= 0).ToList();
        log($"encode {number}: {title.DisplayName} (audio {audio.Count}, subtitles {subs.Count})");
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        BluRay.WriteOwnSubtitles(title, real.Where(x => x.Track.File == null && !x.Track.IsVobSub).ToList(), span, log); // a VobSub or a subtitle file is converted apart, below
        foreach (var (s, file) in real.Where(x => x.Track.File == null && x.Track.IsVobSub)) BluRay.WriteEmbeddedVobSub(title, s, span, file, (720, 480), log);
        foreach (var (s, file) in real.Where(x => x.Track.File != null)) BluRay.WriteExternalSubtitle(title, s, span, file, (720, 480), log); // a VobSub becomes PGS on a 720x480 plane, which PgsStream maps onto the frame whatever its height
        foreach (var (_, file) in real) title.CloseGaps(file, span);
        BluRay.Blanks(subs, lined != null, file => !DvdSubtitles.HasCues(file), (720, 480), log);
        // a DVD title has 32 subtitle streams (0x20-0x3F): a 33rd would overwrite the chain's next fields, then fail the build after the encode
        if (subs.Count > 32)
        {
            log($"warning: {subs.Count} subtitles kept, a DVD carries 32: the last {subs.Count - 32} are left out");
            subs.RemoveRange(32, subs.Count - 32);
        }

        var carried = new List<SubtitleTrack>();
        if (subs.Count > 0)
        {
            var withSubs = Path.Combine(work, $"movie{number}_sub.mpg");
            try
            {
                AddSubtitles(movie, withSubs, subs, pal, wide, work, log);
                movie = withSubs;
                carried = subs.Select(s => s.Track).ToList();
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                log("subtitles dropped: " + e.Message.Split('\n')[0]);
            }
        }
        var chapters = title.ChapterList(src.Duration, job.ChapterMinutes);
        if (chapters.Count > 99) // a DVD title takes 99 chapters (one-byte counts wrap at 256): spread over the whole film, the menu's scenes follow
        {
            log($"warning: {title.DisplayName}: {chapters.Count} chapters, a DVD title takes 99: 99 kept, spread over the whole video");
            chapters = [.. Enumerable.Range(0, 99).Select(i => chapters[i * chapters.Count / 99])];
        }
        return new Movie(movie, audio, carried, chapters, title.DefaultSubtitleNumber(carried));
    }

    /// <summary>
    /// One ffmpeg pass that adds a subtitle stream per entry, in order (0x20, 0x21...): each track's cues drawn by <see cref="DvdSubtitles"/> into
    /// subpictures, written as VobSub, copied into the movie. Its timestamps are kept (-copyts), the cues being moved by the movie's first one.
    /// </summary>
    static void AddSubtitles(string movie, string output, List<(SubtitleTrack Track, string File)> subs, bool pal, bool wide, string work, Action<string> log)
    {
        log("subtitles: render + mux");
        var name = Path.GetFileNameWithoutExtension(movie);
        var h = pal ? 576 : 480;
        var start = VideoStart(movie);
        var args = new List<string> { "-y", "-v", "warning", "-i", movie };
        for (var k = 0; k < subs.Count; k++)
        {
            var pictures = subs[k].Track.IsPicture ? DvdSubtitles.PgsPictures(subs[k].File, 720, h, log) : DvdSubtitles.Pictures(subs[k].File, 720, h);
            var idx = Path.Combine(work, $"{name}_s{k + 1}.idx");
            Spu.WriteVobSub(idx, pictures, 720, h, Languages.Iso1(subs[k].Track.Lang), DvdSubtitles.Rgb, [0, 1, 2, 3], [0, 15, 15, 15], false, start);
            args.AddRange(["-i", idx]);
        }
        args.AddRange(["-map", "0:v", "-map", "0:a?"]);
        for (var k = 0; k < subs.Count; k++) args.AddRange(["-map", $"{k + 1}:0"]);
        args.AddRange([.. Remux, output]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        if (!File.Exists(output) || new FileInfo(output).Length < new FileInfo(movie).Length / 2)
            throw new InvalidOperationException("the subtitle pass produced no usable file");
    }

    /// <summary>Copy into a DVD program stream (2048-byte packs, a navigation pack at each picture group), the timestamps kept.</summary>
    static readonly string[] Remux = ["-c", "copy", "-copyts", "-f", "dvd", "-muxrate", "10080000", "-packetsize", "2048"];

    /// <summary>The first video timestamp of a program stream, in seconds: where its time 0 is.</summary>
    static double VideoStart(string mpg)
    {
        var text = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=start_time", "-of", "default=nw=1:nk=1", mpg]).Trim();
        return double.TryParse(text, NumberStyles.Float, Inv, out var s) ? s : 0;
    }

    /// <summary>
    /// Settings pages: one per (audio, subtitle) choice, so the page itself can show what is chosen.
    /// Un modèle à liste unique a <c>settings_audio_A</c> et <c>settings_sub_S</c> à la place : chacune ne marque que sa catégorie, le produit des deux ferait dépasser 1 Go à un menu animé.
    /// </summary>
    static string SettingsId(int audio, int sub) => $"settings_{audio}_{sub}";

    /// <summary>
    /// The chapter buttons of a scene page with the excerpt that should play in each: the movie's MPEG, the chapter's time on the disc, and the
    /// button's rectangle. Empty when the page has no chapter button (a menu page, a settings page) or the chapter is unknown.
    /// </summary>
    static List<(string Movie, double Time, Box Rect)> Clips(MenuPage page, List<Movie> movies)
    {
        var clips = new List<(string, double, Box)>();
        if (!page.Id.StartsWith("scenes", StringComparison.Ordinal)) return clips;
        foreach (var b in page.Buttons)
        {
            if (b.Action.Kind != "chapter") continue;
            var title = b.Action.Playlist - 1;
            if (title < 0 || title >= movies.Count) continue;
            var times = movies[title].ChapterTimes;
            if (b.Action.Mark < 0 || b.Action.Mark >= times.Count) continue;
            clips.Add((movies[title].Mpg, times[b.Action.Mark], b.Rect));
        }
        return clips;
    }

    /// <summary>A menu page on the disc: its program stream (the highlight subpicture in it), its colours, its buttons' rectangles on the frame.</summary>
    sealed record MenuOut(MenuPage Page, string Mpg, int[] Colors, List<(int X0, int Y0, int X1, int Y1)> Rects);

    static List<MenuOut> BuildMenus(DvdJob job, List<SourceInfo> sources, List<Movie> movies, string work, bool pal, Action<string> log)
    {
        log("menu: render");
        var ffmpeg = Tools.Find("ffmpeg.exe");
        var (w, h) = pal ? (720, 576) : (720, 480);
        var thumbs = Path.Combine(work, "thumbs");
        Directory.CreateDirectory(thumbs);
        string? Thumb(string name, int title, double t, int tw, int th)
        {
            var file = Path.Combine(thumbs, name + ".jpg");
            try
            {
                Tools.Run(ffmpeg, ["-y", "-v", "error", "-ss", Math.Max(0, Math.Min(t, sources[title].Duration - 1)).ToString("0.###", Inv),
                    "-i", movies[title].Mpg, "-frames:v", "1", "-q:v", "2", "-vf", $"{sources[title].OneView}scale=trunc(iw*sar/2)*2:ih,setsar=1,scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th}", file]);
            }
            catch (InvalidOperationException) { log($"warning: no picture for {name} (ffmpeg could not read a frame)"); } // the menu then shows an empty frame
            return File.Exists(file) ? file : null;
        }
        var mainCount = job.Titles.Count(t => !t.Bonus);
        var series = mainCount > 1; // the extras do not make a series of a film
        // a picture chosen for the title (see DiscTitle.ThumbImage), fitted like a frame; unreadable = the frame of the video
        string? Cover(string name, string image, int tw, int th)
        {
            var file = Path.Combine(thumbs, name + ".jpg");
            try { Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", image, "-frames:v", "1", "-q:v", "2", "-vf", $"scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th}", file]); }
            catch (InvalidOperationException) { log($"warning: {Path.GetFileName(image)} is not a picture ffmpeg can read: the thumbnail comes from the video"); return null; }
            return file;
        }
        TitleEntry Entry(int i, string name) => new(job.Titles[i].DisplayName, i + 1, (job.Titles[i].ThumbImage is { } img ? Cover(name, img, 480, 270) : null)
            ?? Thumb(name, i, job.Titles[i].ThumbAt ?? sources[i].Duration * 0.1, 480, 270), job.Titles[i].Overview);
        var content = new MenuContent(1, Thumb("preview", 0, sources[0].Duration * 0.1, 800, 450),
            series ? [] : movies[0].ChapterTimes.Select((t, i) => Thumb($"c{i + 1}", 0, t + 3, 480, 270)).ToList(),
            series ? Enumerable.Range(0, mainCount).Select(i => Entry(i, $"t{i + 1}")).ToList() : null,
            movies[0].Audio.Select(a => a.Name).ToList(), movies[0].Subtitles.Select(s => s.Name).ToList(),
            mainCount < job.Titles.Count ? Enumerable.Range(mainCount, job.Titles.Count - mainCount).Select(i => Entry(i, $"b{i - mainCount + 1}")).ToList() : null);

        var pages = MenuBuilder.Build(job.Menu!, content);
        if (MenuBuilder.TracksLeftOut(pages, content) is { } leftOut) log(leftOut);
        var settingsPages = MenuBuilder.SettingsIds(job.Menu!, content);
        if (pages.RemoveAll(p => settingsPages.Contains(p.Id)) > 0)
        {
            var audioStates = content.AudioNames.Count > 1 ? content.AudioNames.Count : 1;
            var subStates = content.SubtitleNames.Count > 0 ? content.SubtitleNames.Count + 1 : 1;
            if (settingsPages.Contains("settings"))
                for (var a = 1; a <= audioStates; a++)
                    for (var s = 0; s < subStates; s++)
                        pages.Add(MenuBuilder.WithElements(job.Menu!, MenuBuilder.Settings(job.Menu!, content, a, s, SettingsId(a, s)), content));
            if (settingsPages.Contains("settings_audio"))
                for (var a = 1; a <= audioStates; a++)
                    pages.Add(MenuBuilder.WithElements(job.Menu!, MenuBuilder.Settings(job.Menu!, content, a, null, $"settings_audio_{a}"), content));
            if (settingsPages.Contains("settings_sub"))
                for (var s = 0; s < subStates; s++)
                    pages.Add(MenuBuilder.WithElements(job.Menu!, MenuBuilder.Settings(job.Menu!, content, null, s, $"settings_sub_{s}"), content));
        }
        var loop = job.Menu!.Loop;
        // a moving menu: the pages are transparent overlays burned onto a clip of the film (or the design's own video) that plays and repeats
        var backgrounds = MenuRenderer.Sta(() => pages.Select(p => loop ? MenuRenderer.Overlay(job.Menu!, p) : MenuRenderer.Background(job.Menu!, p)).ToList());
        var loopSeconds = BluRay.LoopLength(job.Menu, 120);
        var (loopFile, loopSrc, loopStart, loopInput) = loop ? BluRay.LoopSource(job.Menu, job.Titles[0], loopSeconds) : default;
        var musicFile = job.Menu.ExistingMusic();
        var music = musicFile == null ? null : BluRay.PrepareMusic(job.Menu, musicFile, loopSeconds, work, log);
        if (loop) log($"menu: moving background, {loopSeconds}s from {Path.GetFileName(loopFile)} at {loopStart}s{(music != null ? " + music" : "")}");
        else if (music != null) log($"menu: music from {Path.GetFileName(musicFile)}, {loopSeconds}s on every page");

        var result = new List<MenuOut>();
        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var bg = Path.Combine(work, $"menu_{p.Id}.{(loop ? "png" : "jpg")}");
            File.WriteAllBytes(bg, backgrounds[i]);
            var plain = Path.Combine(work, $"menu_{p.Id}_plain.mpg");
            var sar = pal ? "64/45" : "32/27";
            // animated thumbnails: each chapter button shows a short excerpt of its chapter inside its own frame (a dynamic thumbnail)
            var clips = !loop && job.Menu!.AnimatedThumbs && music == null ? Clips(p, movies) : [];
            if (!loop && clips.Count > 0)
            {
                var dur = 8.0; // the page lasts as long as a still one; every excerpt is cut to that length
                var args = new List<string> { "-y", "-v", "warning", "-loop", "1", "-framerate", pal ? "25" : "30000/1001", "-i", bg };
                var fc = new System.Text.StringBuilder($"[0:v]scale={w}:{h},setsar={sar}[base0];");
                var last = "base0";
                var k = 1;
                foreach (var (movie, time, rect) in clips)
                {
                    args.AddRange(["-ss", time.ToString(Inv), "-t", dur.ToString(Inv), "-i", movie]);
                    int Sx(double v, int size) => Math.Max(2, ((int)Math.Round(v * size / (double)MenuBuilder.W)) & ~1);
                    int Sy(double v, int size) => Math.Max(2, ((int)Math.Round(v * size / (double)MenuBuilder.H)) & ~1);
                    var bw = Sx(rect.W, w); var bh = Sy(rect.H, h);
                    var bx = (int)Math.Round(rect.X * w / (double)MenuBuilder.W) & ~1;
                    var by = (int)Math.Round(rect.Y * h / (double)MenuBuilder.H) & ~1;
                    fc.Append($"[{k}:v]scale={bw}:{bh},setsar=1,setpts=PTS-STARTPTS,fps={(pal ? "25" : "30000/1001")}[v{k}];");
                    fc.Append($"[{last}][v{k}]overlay={bx}:{by}[b{k}];");
                    last = $"b{k}";
                    k++;
                }
                fc.Append($"[{last}]format=yuv420p[out]");
                args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
                args.AddRange(["-filter_complex", fc.ToString(), "-map", "[out]", "-map", $"{k}:a:0", "-t", dur.ToString(Inv),
                    "-ac", "2", "-b:a", "192k", "-target", pal ? "pal-dvd" : "ntsc-dvd", "-aspect", "16:9", "-b:v", "4000k", plain]);
                Tools.Run(ffmpeg, args, log);
                log($"menu: {clips.Count} vignettes animées sur {p.Id}");
            }
            else if (!loop)
            {
                // still image + the music, else silent AC-3 so the menu VOB carries the same streams as a normal title
                var still = new List<string> { "-y", "-v", "warning", "-loop", "1", "-framerate", pal ? "25" : "30000/1001", "-i", bg };
                still.AddRange(music != null ? ["-i", music] : ["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
                still.AddRange(["-t", (music != null ? loopSeconds : 8).ToString(Inv),
                    "-vf", $"scale={w}:{h},setsar={sar},format=yuv420p", "-target", pal ? "pal-dvd" : "ntsc-dvd", "-aspect", "16:9",
                    "-b:v", music != null ? "800k" : "3000k", plain]); // a still picture needs little: the pages are many and each lasts as long as the music
                Tools.Run(ffmpeg, still);
            }
            else
            {
                // the clip, the page drawn over it, and its sound (music repeated, else the clip's own, else silence)
                var args = new List<string> { "-y", "-v", "warning" };
                args.AddRange(loopInput); // -stream_loop when the clip is shorter than the loop
                string audioMap;
                int overlayInput;
                if (music != null) { args.AddRange(["-i", music]); audioMap = "1:a:0"; overlayInput = 2; }
                else if (loopSrc.Audio.Count > 0) { audioMap = "0:a:0"; overlayInput = 1; }
                else { args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]); audioMap = "1:a:0"; overlayInput = 2; }
                args.AddRange(["-i", bg, "-filter_complex",
                    $"[0:v]scale=trunc(iw*sar/2)*2:ih,setsar=1,scale=1024:576:force_original_aspect_ratio=decrease,pad=1024:576:(ow-iw)/2:(oh-ih)/2,scale={w}:{h},setsar={sar},fps={(pal ? "25" : "30000/1001")}[v];" +
                    $"[{overlayInput}:v]scale={w}:{h},format=rgba[o];[v][o]overlay=format=auto,format=yuv420p[out]",
                    "-map", "[out]", "-map", audioMap, "-t", loopSeconds.ToString(Inv), "-ac", "2", "-b:a", "192k",
                    "-target", pal ? "pal-dvd" : "ntsc-dvd", "-aspect", "16:9", "-b:v", "4000k", plain]);
                Tools.Run(ffmpeg, args, log);
            }

            // button highlights: the template's own artwork when it has one, else flat frames in 4 colours, scaled from the 1920x1080 layout
            var sx = w / (double)MenuBuilder.W;
            var sy = h / (double)MenuBuilder.H;
            var hl = new Canvas(w, h);
            var sel = new Canvas(w, h);
            // the model's per-button artwork is rasterised at the button's size on the page
            var art = MenuRenderer.Sta(() => p.Buttons.Select(b =>
            {
                int bw = (int)Math.Round(b.Rect.W * sx), bh = (int)Math.Round(b.Rect.H * sy);
                (byte[] Px, int W, int H)? At(string? image, string? mask)
                {
                    if (image == null) return null;
                    try { return MenuRenderer.Raster(image, mask, bw, bh); }
                    catch (InvalidDataException) { return null; } // an unreadable picture must not cost the whole disc: the frame stands in
                }
                // the artwork of the hovered state, and the model's own picture for the chosen state when it carries one
                // (a marker such as a small selection glyph: it keeps its own size instead of being stretched to the button)
                (byte[] Px, int W, int H)? Marker(string? image)
                {
                    if (image == null) return null;
                    var natural = MenuRenderer.Size(image) is var (iw, ih) ? ((int)Math.Round(iw * sx), (int)Math.Round(ih * sy)) : (bw, bh);
                    var (mw, mh) = (Math.Min(bw, Math.Max(1, natural.Item1)), Math.Min(bh, Math.Max(1, natural.Item2)));
                    try { return MenuRenderer.Raster(image, null, mw, mh); }
                    catch (InvalidDataException) { return null; }
                }
                return (Hl: At(b.DvdHighlightImage, b.DvdHighlightMask), Sel: Marker(b.DvdSelectImage), W: bw, H: bh);
            }).ToList());
            var rects = new List<(int X0, int Y0, int X1, int Y1)>();
            for (var k = 0; k < p.Buttons.Count; k++)
            {
                var b = p.Buttons[k];
                var r = b.Rect.Inflate(MenuBuilder.FocusMargin / 2);
                int x0 = (int)(r.X * sx) & ~1, y0 = (int)(r.Y * sy) & ~1;
                int x1 = (int)((r.X + r.W) * sx), y1 = (int)((r.Y + r.H) * sy) & ~1;
                if (art[k].Hl is { } a)
                {
                    hl.Stamp(a.Px, a.W, a.H, x0, y0, 1); // the artwork's shape, in the one tint a subpicture can spend on it
                    var s = art[k].Sel ?? a; // the model's marker for the chosen state, else the silhouette of the hovered artwork
                    // a marker keeps its own size, centred on the button, instead of covering it
                    var centred = art[k].Sel != null; // a marker of its own is centred; the hover silhouette fills the button
                    var at = centred ? (x0 + (art[k].W - s.W) / 2, y0 + (art[k].H - s.H) / 2) : (x0, y0);
                    sel.Stamp(s.Px, s.W, s.H, at.Item1, at.Item2, 2);
                }
                else
                {
                    hl.Frame(x0, y0, x1, y1, 3, 1);
                    sel.Frame(x0, y0, x1, y1, 4, 2);
                }
                rects.Add((Math.Max(0, x0), Math.Max(0, y0), Math.Min(w - 1, x1), Math.Min(h - 1, y1))); // a button at the left or top edge: its focus margin starts off screen, and -2 would fill the colour bits
            }
            // one subpicture for both states: code 1 = shown when selected, 2 = when activated, 3 = both; hidden until a button is lit
            var codes = new byte[w * h];
            for (var c = 0; c < codes.Length; c++) codes[c] = (byte)((hl.Pixels[c] != 0 ? 1 : 0) | (sel.Pixels[c] != 0 ? 2 : 0));
            // one tint for the whole page (a subpicture has four colours): the artwork's own average, else the design's accent
            var accent = art.Select(x => x.Hl).FirstOrDefault(x => x != null) is { } first ? Average(first.Px, first.W, first.H) : Hex(job.Menu!.SelectionInk);
            int[] clut = [0x000000, accent.R << 16 | accent.G << 8 | accent.B, 0xFFFFFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
            var idx = Path.Combine(work, $"hl_{p.Id}.idx");
            Spu.WriteVobSub(idx, [new Spu.Picture(0, null, codes, w, h)], w, h, "en", clut, [0, 0, 0, 0], [0, 0, 0, 0], true, VideoStart(plain));

            var final = Path.Combine(work, $"menu_{p.Id}.mpg");
            Tools.Run(ffmpeg, ["-y", "-v", "warning", "-i", plain, "-i", idx, "-map", "0:v", "-map", "0:a?", "-map", "1:0", .. Remux, final], log);
            result.Add(new MenuOut(p, final, clut, rects));
        }
        return result;
    }

    static Rgba Hex(string s) => new(Convert.ToByte(s.Substring(1, 2), 16), Convert.ToByte(s.Substring(3, 2), 16), Convert.ToByte(s.Substring(5, 2), 16), 255);

    /// <param name="mainCount">The first titles are the feature; the ones after them are bonus: "play all" stops at the last main title.</param>
    /// <param name="intro">The start video: the title after the last one, which the first-play jump goes to; it ends on the menu (or on the first title when there is none).</param>
    /// <param name="wide">false = 4:3 titles; the menus stay 16:9 (their pictures are drawn that way).</param>
    /// <param name="play">Start on the menu or on the first title; after a title, the next main one (play all) or the menu; at the very end, start again or stop.</param>
    static DvdDisc DiscOf(bool pal, List<Movie> movies, int mainCount, List<MenuOut> menus, bool loop, Movie? intro, bool wide, PlaybackOptions play)
    {
        var series = movies.Count > 1;
        // menu PGCs are numbered in order: 1 = root, then the scene/episode pages, then the settings pages
        int MenuNumber(string pageId) => menus.FindIndex(m => m.Page.Id == pageId) + 1;
        var settings = menus.Where(m => m.Page.Id.StartsWith("settings_")).Select(m => m.Page.Id).ToList();
        var states = settings.Select(id => Regex.Match(id, @"settings_(\d+)_(\d+)")).Where(m => m.Success).Select(m => (A: int.Parse(m.Groups[1].Value), S: int.Parse(m.Groups[2].Value), Id: m.Value)).ToList();

        // g1 = 1 while "play all" runs, g2 = chosen audio (1-based), g3 = chosen subtitle (0 = off); they select the settings page to show,
        // with one compare per jump, on g6 = 64 * g2 + g3
        List<ulong> Enter()
        {
            if (states.Count == 0) return [];
            List<ulong> c = [Vm.SetReg(6, 2), Vm.Mul(6, 64), Vm.AddReg(6, 3)];
            c.AddRange(states.Select(s => Vm.LinkPgc(MenuNumber(s.Id), 6, 64 * s.A + s.S)));
            c.Add(Vm.LinkPgc(MenuNumber(states[0].Id)));
            return c;
        }

        // la page d'une catégorie, celle du choix en cours : l'autre registre n'y entre pas
        List<ulong> EnterChoice(string target, int register)
        {
            var mine = settings.Where(id => id.StartsWith(target + "_", StringComparison.Ordinal)).ToList();
            if (mine.Count == 0) return [];
            List<ulong> c = [.. mine.Select(id => Vm.LinkPgc(MenuNumber(id), register, int.Parse(id[(target.Length + 1)..])))];
            c.Add(Vm.LinkPgc(MenuNumber(mine[0])));
            return c;
        }

        // a choice jumps to another page, where a fresh entry would select the first button: it leaves its row's number for that page's pre (a button's number on a DVD is 1024 times its rank)
        List<ulong> KeepRow(string target, MenuButton b) => [Vm.Set(4, 1024 * (menus.First(m => m.Page.Id == target).Page.Buttons.FindIndex(x => x.Id == b.Id) + 1)), Vm.Set(5, 1)];

        List<ulong> Command(MenuPage page, MenuButton b)
        {
            var a = b.Action;
            var here = Regex.Match(page.Id, @"settings_(\d+)_(\d+)");
            switch (a.Kind)
            {
                case "play": return series ? [Vm.Set(1, play.PlayAll ? 1 : 0), Vm.JumpVtsTitle(a.Playlist)] : [Vm.JumpVtsTitle(a.Playlist)]; // play all: any picked title goes on to the next ones
                case "chapter": return [Vm.JumpVtsChapter(a.Playlist, a.Mark + 1)];
                case "playall": return [Vm.Set(1, 1), Vm.JumpVtsTitle(a.Playlist)];
                case "audio": // stream numbers are 0-based on a DVD
                    var audioPage = here.Success ? SettingsId(a.Mark, int.Parse(here.Groups[2].Value)) : $"settings_audio_{a.Mark}";
                    return [.. KeepRow(audioPage, b), Vm.Set(2, a.Mark), Vm.Audio(a.Mark - 1, MenuNumber(audioPage))];
                case "sub": // 62 = none; with the display bit (64) the stream number follows
                    var subPage = here.Success ? SettingsId(int.Parse(here.Groups[1].Value), a.Mark) : $"settings_sub_{a.Mark}";
                    return [.. KeepRow(subPage, b), Vm.Set(3, a.Mark), Vm.Subtitle(a.Mark == 0 ? 62 : 64 + a.Mark - 1, MenuNumber(subPage))];
                default:
                    return a.Page switch
                    {
                        "settings" => Enter(),
                        "settings_audio" => EnterChoice(a.Page, 2),
                        "settings_sub" => EnterChoice(a.Page, 3),
                        _ => [Vm.LinkPgc(MenuNumber(a.Page!))],
                    };
            }
        }

        int Index(MenuPage page, string? id) => id == null ? 0 : page.Buttons.FindIndex(x => x.Id == id) + 1;
        var menuChains = menus.Select((m, n) =>
        {
            // a moving menu plays its clip and enters itself again; g4 / g5 carry the highlighted button over that restart, and over a choice on a settings page;
            // any other entry starts on the first button
            List<ulong> pre = loop || m.Page.Id.StartsWith("settings_", StringComparison.Ordinal)
                ? [Vm.Goto(5, 5, Vm.Ne, 1), Vm.ButtonReg(4), Vm.Set(5, 0), Vm.Goto(6), Vm.Button(1024), Vm.Nop()]
                : [Vm.Button(1024)];
            List<ulong> post = loop ? [Vm.SetReg(4, Vm.Sprm | 8), Vm.Set(5, 1, n + 1)] : [];
            var buttons = m.Page.Buttons.Select((b, k) =>
            {
                var cmds = Command(m.Page, b);
                return new Button(m.Rects[k].X0, m.Rects[k].Y0, m.Rects[k].X1, m.Rects[k].Y1, Index(m.Page, b.Up), Index(m.Page, b.Down), Index(m.Page, b.Left), Index(m.Page, b.Right),
                                  cmds.Count == 0 ? [Vm.Nop()] : cmds);
            }).ToList();
            // selected: codes 1 and 3 in the accent (CLUT 1); activated: codes 2 and 3 in white (CLUT 2); opaque
            byte[] highlight = [0, 1, 0, 1, 0, 15, 0, 15, 0, 0, 2, 2, 0, 0, 15, 15];
            return new Chain(m.Mpg, [], pre, post, Still: !loop, Buttons: buttons, Colors: m.Colors, Highlight: highlight);
        }).ToList();

        var first = movies[0];
        int[] subColors = [.. DvdSubtitles.Rgb];
        var titles = new List<Chain>();
        // after the last main title of "play all": the start again, or stop
        var atEnd = !play.Rewind ? Vm.Exit() : play.StartAtFirstTitle ? Vm.JumpVtsTitle(1) : Vm.CallRootMenu();
        for (var i = 0; i < movies.Count; i++)
        {
            List<ulong> pre = menus.Count == 0 && i == 0 && first.DefaultSubtitle > 0 ? [Vm.Subtitle(63 + first.DefaultSubtitle)] : []; // no menu to start it: the first title does, once (stream numbers are 0-based, 64 = shown)
            if (menus.Count == 0) { titles.Add(new Chain(movies[i].Mpg, movies[i].ChapterTimes, pre, [Vm.Exit()], Still: true, Colors: subColors)); continue; } // the last picture stays, then the disc stops
            // after an episode of "play all" (g1 = 1): the next one, never a bonus; a picked title alone goes back to the menu
            List<ulong> post = i < mainCount - 1 ? [Vm.Goto(3, 1, Vm.Ne, 1), Vm.JumpVtsTitle(i + 2), Vm.CallRootMenu()]
                : i == mainCount - 1 && play.PlayAll ? [Vm.Goto(3, 1, Vm.Ne, 1), atEnd, Vm.CallRootMenu()]
                : [Vm.CallRootMenu()];
            titles.Add(new Chain(movies[i].Mpg, movies[i].ChapterTimes, pre, post, Colors: subColors));
        }
        List<ulong> setSubtitle = first.DefaultSubtitle > 0 ? [Vm.Subtitle(63 + first.DefaultSubtitle)] : [];
        if (intro != null) // after the last title; the film's default subtitle is chosen when it is over, not while it plays
            titles.Add(new Chain(intro.Mpg, [], [], [.. setSubtitle, menus.Count > 0 && !play.StartAtFirstTitle ? Vm.CallRootMenu() : Vm.JumpVtsTitle(1)], Colors: subColors));

        var toIntro = Vm.JumpTitle(movies.Count + 1);
        // "first title then all": g1 = 1 makes it go on
        List<ulong> start = intro != null ? [toIntro] : [.. setSubtitle, play.StartAtFirstTitle ? Vm.JumpTitle(1) : Vm.JumpRootMenu()];
        List<ulong> fp = menus.Count > 0 ? [Vm.Set(1, play.StartAtFirstTitle ? 1 : 0), Vm.Set(2, 1), Vm.Set(3, first.DefaultSubtitle), Vm.Set(4, 1024), Vm.Set(5, 0), .. start]
            : [intro != null ? toIntro : Vm.JumpTitle(1)];
        return new DvdDisc(pal, menuChains, titles, fp,
            [.. first.Audio.Select(a => new AudioStream(Math.Min(6, Math.Max(1, a.Channels)), Languages.Iso1(a.Lang)))],
            [.. first.Subtitles.Select(s => Languages.Iso1(s.Lang))], wide);
    }
}
