using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BurnSmith.Core.Menu;
using static BurnSmith.Core.Menu.IndexedPng;

namespace BurnSmith.Core;

/// <summary>DVD-Video job: one title per entry of <see cref="Titles"/>, the bonus ones (<see cref="DiscTitle.Bonus"/>) after the others. <see cref="Menu"/> null = the disc starts straight on the movie (a single title only).</summary>
public sealed record DvdJob(IReadOnlyList<DiscTitle> Titles, string OutDir, MenuDesign? Menu = null, int ChapterMinutes = 5, double CapacityGB = 4.2, EncodeOptions? Encode = null);

/// <summary>ffmpeg (MPEG-2 + AC-3) -> spumux (button highlights, text subtitles) -> dvdauthor (VIDEO_TS with menus).</summary>
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

    public static double EstimateGB(IReadOnlyList<(SourceInfo Src, DiscTitle Title)> items, double capacityGB)
    {
        var v = VideoBitrate(items, capacityGB);
        return items.Sum(i => i.Src.Duration * (v + i.Title.KeptAudio(i.Src).Sum(AudioBps))) / 8 / 1e9;
    }

    /// <summary>One encoded title: the MPEG file, the audio and subtitle tracks that really are in it, its chapter times.</summary>
    sealed record Movie(string Mpg, List<AudioTrack> Audio, List<SubtitleTrack> Subtitles, List<double> ChapterTimes, int DefaultSubtitle);

    public static void Build(DvdJob job, Action<string>? log = null)
    {
        log ??= _ => { };
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
        var pal = Math.Abs(sources[0].Fps - 25) < 0.5 || Math.Abs(sources[0].Fps - 50) < 0.5; // the disc follows its first video
        var work = job.OutDir + "_work";
        Tools.PrepareOutput(job.OutDir);
        Directory.CreateDirectory(work);

        var videoBps = job.Encode is { DvdKbps: > 0 } manual ? Math.Clamp(manual.DvdKbps, 1500, 9000) * 1000 : VideoBitrate(items, job.CapacityGB); // by hand, else what fills the disc
        log($"movie: {(pal ? "PAL" : "NTSC")} {videoBps / 1000} kb/s");
        var movies = new List<Movie>();
        for (var i = 0; i < job.Titles.Count; i++) movies.Add(EncodeTitle(job, i + 1, sources[i], job.Titles[i], pal, videoBps, work, log));
        if (movies.Any(m => m.Audio.Count != movies[0].Audio.Count || m.Subtitles.Count != movies[0].Subtitles.Count))
            log("warning: the videos do not have the same number of audio/subtitle tracks; the menu follows the first one");
        Movie? introMovie = null;
        if (intro != null) // one audio track and no subtitles: nothing to choose while it plays, and no spumux pass
            introMovie = EncodeTitle(job, movies.Count + 1, introSource!, intro with { Audio = [.. intro.KeptAudio(introSource!).Take(1).Select(a => a.Index)], Subtitles = null, External = null, DefaultAudio = null, DefaultSubtitle = null }, pal, videoBps, work, log);

        var menuPages = new List<(MenuPage Page, string Mpg)>();
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

        log("author: dvdauthor");
        var xml = Path.Combine(work, "dvd.xml");
        File.WriteAllText(xml, AuthorXml(job.OutDir, pal, movies, mainCount, menuPages, restarts, introMovie).ToString());
        // this dvdauthor build wants exactly "-o dest -x script -h N" plus one more argument (it reads argv[7] unchecked and crashes without)
        Tools.Run(Tools.Find("dvdauthor.exe"), ["-o", job.OutDir, "-x", xml, "-h", "0", "-q"], log, env: new Dictionary<string, string> { ["VIDEO_FORMAT"] = pal ? "PAL" : "NTSC" });
        Directory.CreateDirectory(Path.Combine(job.OutDir, "AUDIO_TS")); // empty, but players and tools expect it
        Tools.TryDelete(work);
        log("done: " + job.OutDir);
    }

    static Movie EncodeTitle(DvdJob job, int number, SourceInfo src, DiscTitle title, bool pal, int videoBps, string work, Action<string> log)
    {
        var (w, h) = pal ? (720, 576) : (720, 480);
        var sar = pal ? "64/45" : "32/27";
        var audio = title.KeptAudio(src).Take(8).ToList(); // a DVD title carries at most 8 audio streams
        var movie = Path.Combine(work, $"movie{number}.mpg");
        // movie: square pixels -> 16:9 box with bars -> DVD frame size and aspect
        var ntscFilm = !pal && src.Fps < 25;
        var rate = pal ? "fps=25" : ntscFilm ? "fps=24000/1001,telecine=pattern=23" : "fps=30000/1001"; // film: real 3:2 pulldown
        // -ss / -t before -i cut the input itself: the video, the sound and the subtitle files written below all start and stop together
        var args = new List<string> { "-y", "-v", "warning" };
        if (title.Start > 0) args.AddRange(["-ss", title.Start.ToString(Inv)]);
        if (title.End != null) args.AddRange(["-t", src.Duration.ToString("0.###", Inv)]);
        args.AddRange(["-i", title.Video, "-map", "0:v:0"]);
        foreach (var a in audio) args.AddRange(["-map", $"0:a:{a.Index}"]);
        args.AddRange(["-vf", $"scale=trunc(iw*sar/2)*2:ih,setsar=1,scale=1024:576:force_original_aspect_ratio=decrease,pad=1024:576:(ow-iw)/2:(oh-ih)/2,scale={w}:{h},setsar={sar},{rate}",
            "-target", pal ? "pal-dvd" : "ntsc-dvd", "-aspect", "16:9", "-b:v", $"{videoBps / 1000}k"]);
        if (audio.Count == 0) args.Add("-an");
        for (var j = 0; j < audio.Count; j++)
        {
            args.AddRange([$"-c:a:{j}", "ac3", $"-b:a:{j}", $"{AudioBps(audio[j]) / 1000}k"]);
            if (audio[j].Channels > 6) args.AddRange([$"-ac:a:{j}", "6"]); // AC-3 tops out at 5.1
            if (title.Volume != 100) args.AddRange([$"-filter:a:{j}", $"volume={(title.Volume / 100.0).ToString("0.###", Inv)}"]);
        }
        if (ntscFilm) args.AddRange(["-flags", "+ildct+ilme", "-top", "1"]);
        args.AddRange(["-sn", "-dn", movie]);

        // text subtitles are drawn by DvdSubtitles, PGS pictures redrawn (spumux has no text renderer); other bitmap formats would need OCR
        var subs = new List<(SubtitleTrack Track, string File)>();
        foreach (var s in title.KeptSubtitles(src))
        {
            if (!s.Supported) { log($"subtitle '{s.Name}' skipped: {s.Codec} is an image format that needs OCR"); continue; }
            var file = Path.Combine(work, $"sub{number}_{subs.Count + 1}.{(s.IsPicture ? "sup" : "srt")}");
            if (s.File == null && !s.IsVobSub) // the video's own track; a VobSub or a subtitle file is converted apart, below
            {
                args.AddRange(["-map", $"0:s:{s.Index}", "-c:s", s.IsPgs ? "copy" : "srt"]);
                if (title.End != null) args.AddRange(["-t", src.Duration.ToString("0.###", Inv)]); // the input's -t lets later subtitle cues through
                args.AddRange(["-f", s.IsPgs ? "sup" : "srt", file]);
            }
            subs.Add((s, file));
        }
        log($"encode {number}: {title.DisplayName} (audio {audio.Count}, subtitles {subs.Count})");
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        foreach (var (s, file) in subs.Where(x => x.Track.File == null && x.Track.IsVobSub)) BluRay.WriteEmbeddedVobSub(title, s, src.Duration, file, (720, 480), log);
        foreach (var (s, file) in subs.Where(x => x.Track.File != null)) BluRay.WriteExternalSubtitle(title, s, src.Duration, file, (720, 480), log); // a VobSub becomes PGS on a 720x480 plane, which PgsStream maps onto the frame whatever its height
        // spumux refuses a file with no cue, and then the whole movie would lose its subtitles: a track with nothing to show in the part kept is left out
        subs.RemoveAll(x =>
        {
            var empty = !DvdSubtitles.HasCues(x.File);
            if (empty) log($"subtitle '{x.Track.Name}' skipped: no cue in the part kept");
            return empty;
        });

        var carried = new List<SubtitleTrack>();
        if (subs.Count > 0)
        {
            var withSubs = Path.Combine(work, $"movie{number}_sub.mpg");
            try
            {
                AddSubtitles(movie, withSubs, subs, pal, work, log);
                movie = withSubs;
                carried = subs.Select(s => s.Track).ToList();
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                log("subtitles dropped: " + e.Message.Split('\n')[0]);
            }
        }
        return new Movie(movie, audio, carried, title.ChapterList(src.Duration, job.ChapterMinutes), title.DefaultSubtitleNumber(carried));
    }

    /// <summary>One spumux pass that adds a subtitle stream per entry (cues drawn by <see cref="DvdSubtitles"/>).</summary>
    static void AddSubtitles(string movie, string output, List<(SubtitleTrack Track, string File)> subs, bool pal, string work, Action<string> log)
    {
        log("subtitles: render + spumux");
        var name = Path.GetFileNameWithoutExtension(movie);
        // this spumux build writes every subtitle as stream 0x20: add them last to first, moving the earlier ones up one id each time
        var current = movie;
        for (var k = subs.Count - 1; k >= 0; k--)
        {
            var (prefix, h) = ($"{name}_s{k + 1}_", pal ? 576 : 480);
            var spu = new XElement("subpictures", subs[k].Track.IsPicture ? DvdSubtitles.PgsStream(subs[k].File, work, prefix, 720, h, log)
                                                                        : DvdSubtitles.Stream(subs[k].File, work, prefix, 720, h, pal ? 64.0 / 45 : 32.0 / 27));
            var xml = Path.Combine(work, $"{name}_s{k + 1}.xml");
            new XDocument(spu).Save(xml);
            var next = k == 0 ? output : Path.Combine(work, $"{name}_pass{k}.mpg");
            Tools.Run(Tools.Find("spumux.exe"), [Path.GetFileName(xml), current, next], log, work);
            if (!File.Exists(next) || new FileInfo(next).Length < new FileInfo(movie).Length / 2)
                throw new InvalidOperationException("spumux produced no usable file");
            if (current != movie) File.Delete(current);
            if (k > 0) DvdSubtitles.ShiftStreams(next);
            current = next;
        }
    }

    /// <summary>Settings pages: one per (audio, subtitle) choice, so the page itself can show what is chosen.</summary>
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

    static List<(MenuPage, string)> BuildMenus(DvdJob job, List<SourceInfo> sources, List<Movie> movies, string work, bool pal, Action<string> log)
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
                    "-i", movies[title].Mpg, "-frames:v", "1", "-q:v", "2", "-vf", $"scale=trunc(iw*sar/2)*2:ih,setsar=1,scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th}", file]);
            }
            catch (InvalidOperationException) { log($"warning: no picture for {name} (ffmpeg could not read a frame)"); } // the menu then shows an empty frame
            return File.Exists(file) ? file : null;
        }
        var mainCount = job.Titles.Count(t => !t.Bonus);
        var series = mainCount > 1; // the extras do not make a series of a film
        TitleEntry Entry(int i, string name) => new(job.Titles[i].DisplayName, i + 1, Thumb(name, i, sources[i].Duration * 0.1, 480, 270));
        var content = new MenuContent(1, Thumb("preview", 0, sources[0].Duration * 0.1, 800, 450),
            series ? [] : movies[0].ChapterTimes.Select((t, i) => Thumb($"c{i + 1}", 0, t + 3, 480, 270)).ToList(),
            series ? Enumerable.Range(0, mainCount).Select(i => Entry(i, $"t{i + 1}")).ToList() : null,
            movies[0].Audio.Select(a => a.Name).ToList(), movies[0].Subtitles.Select(s => s.Name).ToList(),
            mainCount < job.Titles.Count ? Enumerable.Range(mainCount, job.Titles.Count - mainCount).Select(i => Entry(i, $"b{i - mainCount + 1}")).ToList() : null);

        var pages = MenuBuilder.Build(job.Menu!, content);
        if (pages.RemoveAll(p => p.Id == "settings") > 0)
        {
            var audioStates = content.AudioNames.Count > 1 ? content.AudioNames.Count : 1;
            var subStates = content.SubtitleNames.Count > 0 ? content.SubtitleNames.Count + 1 : 1;
            for (var a = 1; a <= audioStates; a++)
                for (var s = 0; s < subStates; s++)
                    pages.Add(MenuBuilder.WithElements(job.Menu!, MenuBuilder.Settings(job.Menu!, content, a, s, SettingsId(a, s)), content));
        }
        var loop = job.Menu!.Loop;
        // a moving menu: the pages are transparent overlays burned onto a clip of the film (or the design's own video) that plays and repeats
        var backgrounds = MenuRenderer.Sta(() => pages.Select(p => loop ? MenuRenderer.Overlay(job.Menu!, p) : MenuRenderer.Background(job.Menu!, p)).ToList());
        var loopSeconds = Math.Clamp(job.Menu.LoopSeconds, 5, 120);
        var (loopFile, loopSrc, loopStart) = loop ? BluRay.LoopSource(job.Menu, job.Titles[0], loopSeconds) : default;
        var musicFile = job.Menu.ExistingMusic();
        var music = musicFile == null ? null : BluRay.PrepareMusic(job.Menu, musicFile, loopSeconds, work, log);
        if (loop) log($"menu: moving background, {loopSeconds}s from {Path.GetFileName(loopFile)} at {loopStart}s{(music != null ? " + music" : "")}");
        else if (music != null) log($"menu: music from {Path.GetFileName(musicFile)}, {loopSeconds}s on every page");

        var result = new List<(MenuPage, string)>();
        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var bg = Path.Combine(work, $"menu_{p.Id}.{(loop ? "png" : "jpg")}");
            File.WriteAllBytes(bg, backgrounds[i]);
            var plain = Path.Combine(work, $"menu_{p.Id}_plain.mpg");
            var sar = pal ? "64/45" : "32/27";
            // animated thumbnails: each chapter button shows a short excerpt of its chapter inside its own frame (DVDFab's dynamic thumbnail)
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
                    "-ac", "2", "-c:a", "ac3", "-b:a", "192k", "-target", pal ? "pal-dvd" : "ntsc-dvd", "-aspect", "16:9", "-b:v", "4000k", plain]);
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
                var args = new List<string> { "-y", "-v", "warning", "-ss", loopStart.ToString(Inv), "-t", loopSeconds.ToString(Inv), "-i", loopFile };
                string audioMap;
                int overlayInput;
                if (music != null) { args.AddRange(["-i", music]); audioMap = "1:a:0"; overlayInput = 2; }
                else if (loopSrc.Audio.Count > 0) { audioMap = "0:a:0"; overlayInput = 1; }
                else { args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]); audioMap = "1:a:0"; overlayInput = 2; }
                args.AddRange(["-i", bg, "-filter_complex",
                    $"[0:v]scale=trunc(iw*sar/2)*2:ih,setsar=1,scale=1024:576:force_original_aspect_ratio=decrease,pad=1024:576:(ow-iw)/2:(oh-ih)/2,scale={w}:{h},setsar={sar},fps={(pal ? "25" : "30000/1001")}[v];" +
                    $"[{overlayInput}:v]scale={w}:{h},format=rgba[o];[v][o]overlay=format=auto,format=yuv420p[out]",
                    "-map", "[out]", "-map", audioMap, "-t", loopSeconds.ToString(Inv), "-ac", "2", "-c:a", "ac3", "-b:a", "192k",
                    "-target", pal ? "pal-dvd" : "ntsc-dvd", "-aspect", "16:9", "-b:v", "4000k", plain]);
                Tools.Run(ffmpeg, args, log);
            }

            // button highlights: the template's own artwork when it has one, else flat frames in 4 colours, scaled from the 1920x1080 layout
            var sx = w / (double)MenuBuilder.W;
            var sy = h / (double)MenuBuilder.H;
            var hl = new Canvas(w, h);
            var sel = new Canvas(w, h);
            // spumux reads the page button by button, so the model's per-button artwork is rasterised at the button's size on the page
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
                // (a marker such as DVDFab's selection/0.png is a small glyph: it keeps its own size instead of being stretched to the button)
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
            var sub = new XElement("spu", new XAttribute("start", "00:00:00.00"), new XAttribute("highlight", $"hl_{p.Id}.png"),
                new XAttribute("select", $"sel_{p.Id}.png"), new XAttribute("force", "yes"));
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
                sub.Add(new XElement("button", new XAttribute("name", b.Id),
                    new XAttribute("x0", x0), new XAttribute("y0", y0), new XAttribute("x1", x1), new XAttribute("y1", y1),
                    Nav("up", b.Up), Nav("down", b.Down), Nav("left", b.Left), Nav("right", b.Right)));
            }
            // one tint for the whole page (a subpicture has four colours): the artwork's own average, else the design's accent
            var accent = art.Select(x => x.Hl).FirstOrDefault(x => x != null) is { } first ? Average(first.Px, first.W, first.H) : Hex(job.Menu!.SelectionInk);
            Rgba[] palette = [new(0, 0, 0, 0), accent, new(255, 255, 255, 255), new(0, 0, 0, 0)];
            File.WriteAllBytes(Path.Combine(work, $"hl_{p.Id}.png"), Encode(hl, palette));
            File.WriteAllBytes(Path.Combine(work, $"sel_{p.Id}.png"), Encode(sel, palette));
            var spuXml = Path.Combine(work, $"spu_{p.Id}.xml");
            new XDocument(new XElement("subpictures", new XElement("stream", sub))).Save(spuXml);

            var final = Path.Combine(work, $"menu_{p.Id}.mpg");
            // this spumux build takes "script input output" (no stdin/stdout, no -m), PNG paths relative to the cwd
            Tools.Run(Tools.Find("spumux.exe"), [Path.GetFileName(spuXml), plain, final], log, work);
            result.Add((p, final));
        }
        return result;

        static XAttribute? Nav(string name, string? target) => target == null ? null : new XAttribute(name, target);
    }

    static Rgba Hex(string s) => new(Convert.ToByte(s.Substring(1, 2), 16), Convert.ToByte(s.Substring(3, 2), 16), Convert.ToByte(s.Substring(5, 2), 16), 255);

    /// <param name="mainCount">The first titles are the feature; the ones after them are bonus: "play all" stops at the last main title.</param>
    /// <param name="intro">The start video: the title after the last one, which the first-play jump goes to; it ends on the menu (or on the first title when there is none).</param>
    static XDocument AuthorXml(string dest, bool pal, List<Movie> movies, int mainCount, List<(MenuPage Page, string Mpg)> menus, bool loop, Movie? intro = null)
    {
        var fmt = pal ? "pal" : "ntsc";
        var series = movies.Count > 1;
        XElement Video() => new("video", new XAttribute("format", fmt), new XAttribute("aspect", "16:9"), new XAttribute("widescreen", "nopanscan"));
        string Chapters(Movie m) => string.Join(',', m.ChapterTimes.Select(t => TimeSpan.FromSeconds(t).ToString(@"h\:mm\:ss", Inv)));

        // menu PGCs are numbered in order: 1 = root, then the scene/episode pages, then the settings pages
        int MenuNumber(string pageId) => menus.FindIndex(m => m.Page.Id == pageId) + 1;
        var settings = menus.Where(m => m.Page.Id.StartsWith("settings_")).Select(m => m.Page.Id).ToList();
        var states = settings.Select(id => Regex.Match(id, @"settings_(\d+)_(\d+)")).Select(m => (A: int.Parse(m.Groups[1].Value), S: int.Parse(m.Groups[2].Value), Id: m.Value)).ToList();

        // g1 = 1 while "play all" runs, g2 = chosen audio (1-based), g3 = chosen subtitle (0 = off); they select the settings page to show
        // one compare per jump, on g6 = 64 * g2 + g3: dvdauthor compiles "if (g2 == A && g3 == S) jump menu N" (and the nested form) into a link that tests g2 only,
        // so the page always showed the first subtitle choice whatever g3 was
        string Enter() => states.Count == 0 ? "" :
            "g6 = g2; g6 *= 64; g6 += g3; " + string.Concat(states.Select(s => $"if (g6 == {64 * s.A + s.S}) jump menu {MenuNumber(s.Id)}; ")) + $"jump menu {MenuNumber(states[0].Id)};";

        string Command(MenuPage page, MenuButton b)
        {
            var a = b.Action;
            var here = Regex.Match(page.Id, @"settings_(\d+)_(\d+)");
            switch (a.Kind)
            {
                case "play": return (series ? "g1 = 0; " : "") + $"jump title {a.Playlist};";
                case "chapter": return $"jump title {a.Playlist} chapter {a.Mark + 1};";
                case "playall": return $"g1 = 1; jump title {a.Playlist};";
                case "audio": // stream numbers are 0-based on a DVD
                    return $"g2 = {a.Mark}; audio = {a.Mark - 1}; jump menu {MenuNumber(SettingsId(a.Mark, int.Parse(here.Groups[2].Value)))};";
                case "sub": // 62 = none; with the display bit (64) the stream number follows
                    return $"g3 = {a.Mark}; subtitle = {(a.Mark == 0 ? 62 : 64 + a.Mark - 1)}; jump menu {MenuNumber(SettingsId(int.Parse(here.Groups[1].Value), a.Mark))};";
                default: return a.Page == "settings" ? Enter() : $"jump menu {MenuNumber(a.Page!)};";
            }
        }

        var first = movies[0];
        XElement[] AudioEls() => first.Audio.Select(a => new XElement("audio", new XAttribute("format", "ac3"),
            new XAttribute("channels", Math.Min(6, Math.Max(1, a.Channels))), new XAttribute("lang", Languages.Iso1(a.Lang)))).ToArray();
        XElement[] SubEls() => first.Subtitles.Select(s => new XElement("subpicture", new XAttribute("lang", Languages.Iso1(s.Lang)))).ToArray();

        var menuEl = new XElement("menus", Video(), new XElement("audio", new XAttribute("lang", "en")), new XElement("subpicture", new XAttribute("lang", "en")));
        foreach (var (page, mpg) in menus)
        {
            var pgc = new XElement("pgc");
            if (page.Id == "root") pgc.Add(new XAttribute("entry", "root"));
            // a moving menu plays its clip and enters itself again; g4 / g5 carry the highlighted button over that restart (a fresh entry starts on the first button)
            if (loop) pgc.Add(new XElement("pre", "if (g5 == 1) { button = g4; g5 = 0; }"));
            pgc.Add(loop ? new XElement("vob", new XAttribute("file", mpg)) : new XElement("vob", new XAttribute("file", mpg), new XAttribute("pause", "inf")));
            foreach (var b in page.Buttons) pgc.Add(new XElement("button", new XAttribute("name", b.Id), Command(page, b)));
            if (loop) pgc.Add(new XElement("post", $"g4 = s8; g5 = 1; jump menu {MenuNumber(page.Id)};"));
            menuEl.Add(pgc);
        }

        var titleset = new XElement("titleset");
        if (menus.Count > 0) titleset.Add(menuEl);
        var titles = new XElement("titles", Video());
        titles.Add(AudioEls());
        titles.Add(SubEls());
        for (var i = 0; i < movies.Count; i++)
        {
            var pgc = new XElement("pgc", new XElement("vob", new XAttribute("file", movies[i].Mpg), new XAttribute("chapters", Chapters(movies[i]))));
            if (menus.Count == 0 && i == 0 && first.DefaultSubtitle > 0) pgc.AddFirst(new XElement("pre", $"subtitle = {63 + first.DefaultSubtitle};")); // no menu to start it: the first title does, once (stream numbers are 0-based, 64 = shown)
            if (menus.Count > 0)
                pgc.Add(new XElement("post", (i < mainCount - 1 ? $"if (g1 == 1) jump title {i + 2}; " : "") + "call menu;")); // after an episode of "play all": the next one, never a bonus
            titles.Add(pgc);
        }
        var setSubtitle = first.DefaultSubtitle > 0 ? $"subtitle = {63 + first.DefaultSubtitle}; " : "";
        if (intro != null) // after the last title; the film's default subtitle is chosen when it is over, not while it plays
            titles.Add(new XElement("pgc", new XElement("vob", new XAttribute("file", intro.Mpg)), new XElement("post", menus.Count > 0 ? setSubtitle + "call menu;" : "jump title 1;")));
        titleset.Add(titles);

        var vmgm = new XElement("vmgm");
        var toIntro = $"jump title {movies.Count + 1};";
        if (menus.Count > 0) vmgm.Add(new XElement("fpc", $"g1 = 0; g2 = 1; g3 = {first.DefaultSubtitle}; g4 = 1024; g5 = 0; " + (intro != null ? toIntro : setSubtitle + "jump titleset 1 menu;")));
        else if (intro != null) vmgm.Add(new XElement("fpc", toIntro));
        return new XDocument(new XElement("dvdauthor", new XAttribute("dest", dest), vmgm, titleset));
    }
}
