using System.Text;
using BurnSmith.Core.Menu;

namespace BurnSmith.Core;

public static partial class BluRay
{
    internal sealed record MediaSpec(bool Loop, string? LoopFile, int LoopStart, int LoopSeconds, string? Music, int MusicStart, int MusicVolume, int MusicFade)
    {
        public static MediaSpec From(MenuDesign root, PageMedia pm) => new(
            pm.Loop ?? root.Loop,
            pm.LoopFile ?? root.LoopFile,
            pm.LoopStart ?? root.LoopStart,
            pm.LoopSeconds ?? root.LoopSeconds,
            pm.Music ?? root.Music,
            pm.MusicStart ?? root.MusicStart,
            pm.MusicVolume ?? root.MusicVolume,
            pm.MusicFade ?? root.MusicFade);

        public MenuDesign ToDesign(MenuDesign template)
        {
            var d = template.Clone();
            d.Loop = Loop;
            d.LoopFile = LoopFile;
            d.LoopStart = LoopStart;
            d.LoopSeconds = LoopSeconds;
            d.Music = Music;
            d.MusicStart = MusicStart;
            d.MusicVolume = MusicVolume;
            d.MusicFade = MusicFade;
            return d;
        }
    }

    internal static List<(string Video, double Time, Box Rect)> BluRayThumbnailClips(MenuPage page, IReadOnlyList<(string File, IReadOnlyList<double> Chapters, double Duration)> titles)
    {
        var clips = new List<(string, double, Box)>();
        if (!page.Id.StartsWith("scenes", StringComparison.Ordinal) && !page.Id.StartsWith("bonus", StringComparison.Ordinal)) return clips;
        foreach (var b in page.Buttons)
        {
            var titleIdx = b.Action.Playlist - MoviePlaylist;
            if (titleIdx < 0 || titleIdx >= titles.Count) continue;
            var t = titles[titleIdx];
            if (b.Action.Kind == "chapter")
            {
                if (b.Action.Mark >= 0 && b.Action.Mark < t.Chapters.Count)
                    clips.Add((t.File, t.Chapters[b.Action.Mark] + 1, b.Rect));
            }
            else if (b.Action.Kind == "play")
            {
                var at = Math.Max(0, Math.Min(t.Duration * 0.1, Math.Max(0, t.Duration - 10)));
                clips.Add((t.File, at, b.Rect));
            }
        }
        return clips;
    }

    /// <summary>Builds an encoded Blu-ray .ts clip compositing animated chapter/episode thumbnail excerpts over a static page image.</summary>
    internal static (string TsPath, string DeclaredFps) BuildAnimatedThumbsTs(
        BluRayJob job, string pageId, string bgImagePath, IReadOnlyList<(string Video, double Time, Box Rect)> clips,
        string? musicWav, int seconds, string work, Action<string> log)
    {
        var (w, h) = VideoSize(job);
        var (fps, declared) = DiscRate(0, h);
        var ts = Path.Combine(work, $"thumbs_{pageId}.ts");
        var dur = Math.Clamp(seconds, 6, 30);
        var args = new List<string> { "-y", "-v", "warning", "-loop", "1", "-framerate", fps, "-i", bgImagePath };
        var fc = new StringBuilder($"[0:v]scale={w}:{h},setsar=1,format=rgba[base0];");
        var last = "base0";
        var k = 1;
        foreach (var (video, time, rect) in clips)
        {
            args.AddRange(["-stream_loop", "-1", "-ss", time.ToString("0.###", Inv), "-t", dur.ToString(Inv), "-i", video]);
            int bw = Math.Max(2, ((int)Math.Round(rect.W * w / (double)MenuBuilder.W)) & ~1);
            int bh = Math.Max(2, ((int)Math.Round(rect.H * h / (double)MenuBuilder.H)) & ~1);
            int bx = ((int)Math.Round(rect.X * w / (double)MenuBuilder.W)) & ~1;
            int by = ((int)Math.Round(rect.Y * h / (double)MenuBuilder.H)) & ~1;
            fc.Append($"[{k}:v]scale={bw}:{bh}:force_original_aspect_ratio=increase,crop={bw}:{bh},setsar=1,setpts=PTS-STARTPTS,fps={fps}[v{k}];");
            fc.Append($"[{last}][v{k}]overlay={bx}:{by}:shortest=1[b{k}];");
            last = $"b{k}";
            k++;
        }
        var hasAudio = musicWav != null;
        if (musicWav != null) args.AddRange(["-i", musicWav]);
        else
        {
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
            hasAudio = true;
        }
        fc.Append($"[{last}]format=yuv420p[out]");
        args.AddRange(["-filter_complex", fc.ToString(), "-map", "[out]", "-map", $"{k}:a:0", "-t", dur.ToString(Inv), "-r", fps]);
        args.AddRange(MenuCodec(job.Uhd, "SDR", fps, h));
        if (hasAudio) args.AddRange(["-c:a", "ac3", "-b:a", "448k", "-ac", "2", "-ar", "48000"]);
        args.AddRange(["-sn", "-dn", ts]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        log($"menu: {clips.Count} vignettes animées sur {pageId}");
        return (ts, declared);
    }

    internal static void WriteSoundFiles(BluRayJob job, string logLabel, Action<string> log)
    {
        var sounds = job.Menu?.Sounds;
        if (sounds == null || string.IsNullOrWhiteSpace(sounds.Pack) || sounds.Pack.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;
        var data = SoundBank.BuildSoundBdmv(sounds);
        var bdmv = Path.Combine(job.OutDir, "BDMV");
        foreach (var dir in new[] { Path.Combine(bdmv, "AUXDATA"), Path.Combine(bdmv, "BACKUP", "AUXDATA") })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "sound.bdmv"), data);
        }
        log($"menu {logLabel}: bruitages télécommande ({sounds.Pack}) écrits dans AUXDATA\\sound.bdmv");
    }

    internal static void MuxLoopForSpec(BluRayJob job, MenuDesign menu, int playlistNumber, string? overrideTsPath, string work, Action<string> log, bool repeatClip = false, string? declaredOverride = null)
    {
        var musicFile = menu.ExistingMusic();
        var silent = !menu.Loop && musicFile == null;
        var seconds = silent ? SilentMenuLoopSeconds : LoopLength(menu, MaxLoopSeconds);
        var (w, h) = VideoSize(job);
        var ts = overrideTsPath ?? Path.Combine(work, $"loop_{playlistNumber}.ts");
        var (fps, declared) = DiscRate(0, h);
        var hasAudio = false;
        if (overrideTsPath != null)
        {
            declared = declaredOverride ?? declared;
            hasAudio = true;
        }
        else
        {
            var subWork = Path.Combine(work, $"loop_w_{playlistNumber}");
            Directory.CreateDirectory(subWork);
            var music = musicFile == null ? null : PrepareMusic(menu, musicFile, seconds, subWork, log);
            var args = new List<string> { "-y", "-v", "warning" };
            SourceInfo? src = null;
            var tone = "";
            if (menu.Loop)
            {
                var (file, probe, start, input) = LoopSource(menu, job.Titles[0], seconds);
                src = probe;
                (fps, declared) = DiscRate(Fps(job, src), h);
                tone = !job.Uhd && ToSdr(src.Hdr, src.Width, src.Height, w) is { Length: > 0 } sdr ? sdr + "," : ""; // a moving menu taken from an HDR film; a 4K disc keeps its HDR, like the film
                log($"menu: moving background ({playlistNumber}), {seconds}s from {Path.GetFileName(file)} at {start}s{(music != null ? " + music" : "")}");
                if (input[0] == "-stream_loop") log("menu: the clip is shorter than the loop, it plays again from its start");
                args.AddRange(input);
            }
            else
            {
                log(silent ? $"menu BD-J : boucle de fond silencieuse ({seconds} s)" : $"menu: music from {Path.GetFileName(musicFile)}, {seconds}s over a black picture");
                args.AddRange(["-f", "lavfi", "-i", $"color=c=black:s={w}x{h}:r={fps}:d={seconds}"]);
            }
            if (silent) args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
            else if (music != null) args.AddRange(["-i", music]);
            args.AddRange(["-map", "0:v:0"]);
            if (silent || music != null) args.AddRange(["-map", "1:a:0"]);
            else if (src is { Audio.Count: > 0 }) args.AddRange(["-map", "0:a:0"]);
            hasAudio = silent || music != null || src is { Audio.Count: > 0 };
            args.AddRange(["-t", seconds.ToString(Inv),
                "-vf", tone + (src?.SourceView ?? "") + Fit(w, h) + (src is { } loop ? HdMatrix(loop) : ""), "-r", fps]);
            args.AddRange(MenuCodec(job.Uhd, src?.Hdr ?? "SDR", fps, h));
            if (hasAudio) args.AddRange(["-c:a", "ac3", "-b:a", "448k", "-ac", "2", "-ar", "48000"]); // the first title's own sound can be anything
            if (hasAudio && menu.Loop) args.AddRange(["-af", "apad"]);
            args.AddRange(["-sn", "-dn", ts]);
            Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        }

        var meta = Path.Combine(work, $"loop_{playlistNumber}.meta");
        // Null packets keep small menu clips from reaching several play items in the player's initial read-ahead.
        var muxRate = menu.Loop || repeatClip ? $"--vbr --minbitrate=8000 --maxbitrate={(job.Uhd ? 60000 : 32000)}" : "--vbr";
        var lines = new List<string>
        {
            $"MUXOPT {(job.Uhd ? "--blu-ray-v3" : "--blu-ray")} --label={job.Label} {muxRate} --no-pcr-on-video-pid --new-audio-pes --mplsOffset={playlistNumber} --m2tsOffset={playlistNumber}",
            job.Uhd ? $"V_MPEGH/ISO/HEVC, \"{ts}\", fps={declared}, track=256" : $"V_MPEG4/ISO/AVC, \"{ts}\", fps={declared}, track=256",
        };
        if (hasAudio) lines.Add($"A_AC3, \"{ts}\", track=257");
        File.WriteAllLines(meta, lines);
        var dest = Path.Combine(work, "mux" + playlistNumber);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, dest], log);
        if (menu.Loop || repeatClip)
        {
            var name = $"{playlistNumber:00000}.mpls";
            var playlist = Path.Combine(dest, "BDMV", "PLAYLIST", name);
            var repeated = Mpls.RepeatMenuClip(File.ReadAllBytes(playlist));
            File.WriteAllBytes(playlist, repeated);
            File.WriteAllBytes(Path.Combine(dest, "BDMV", "BACKUP", "PLAYLIST", name), repeated);
        }
        Merge(dest, job.OutDir);
    }
}
