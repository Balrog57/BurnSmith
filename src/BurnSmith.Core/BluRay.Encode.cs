using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BurnSmith.Core.Menu;

namespace BurnSmith.Core;

public static partial class BluRay
{
    static Muxed MuxTitle(BluRayJob job, DiscTitle title, SourceInfo src, IReadOnlyList<SubtitleTrack>? lined, IReadOnlyList<AudioTrack>? linedAudio, int playlist, string dest, string work, int kbps, Action<string> log)
    {
        var audio = linedAudio ?? title.KeptAudio(src);
        // MPEG-TS, not Matroska: from an mkv tsMuxeR finds no SPS/PPS before the keyframes and writes ONE entry point in the clip info,
        // so a player cannot seek or jump to a chapter; from a TS it writes one per keyframe
        var prepared = Path.Combine(work, $"prepared{playlist}.ts");

        // subtitles the disc can carry: PGS is copied, text becomes SRT (tsMuxeR renders it); other image formats would need OCR
        var subs = Subtitles(title, src, lined, n => Path.Combine(work, $"sub{playlist}_{n}"), log);
        log($"prepare {playlist}: {src.VideoCodec} {src.Width}x{src.Height} {src.Fps:0.###}fps, audio {(audio.Count == 0 ? "none" : string.Join('+', audio.Select(a => a.Codec)))}, subtitles {subs.Count}");
        // a 3D Blu-ray film (MVC) keeps its right eye only as it is: no encoder makes MVC, and the two eyes must stay picture for picture
        var ssif = src.Stereo == "mvc" ? Ssif(title.Video) : null;
        var mvc = ssif != null && !job.Uhd && title.End == null && CopiesVideo(job, title, src, kbps);
        if (ssif != null && !mvc) log($"{title.DisplayName}: 3D lost, the disc gets the left eye: a 3D Blu-ray film stays 3D only copied whole as it is (no cut, effect or re-encode, not on a 4K disc)");
        if (!mvc || audio.Count > 0)
        {
            var settings = new
            {
                job.Uhd, job.Encode, kbps, mvc, src, Audio = audio,
                title.Start, title.End, title.Gaps, title.Effects, title.Volume, title.AudioVolume
            };
            var inputs = EncodeCache.Inputs(title, audio).Append(Tools.Find("ffmpeg.exe")).Append(Tools.Find("ffprobe.exe"));
            if (job.Uhd && src.Hdr == "Dolby Vision") inputs = inputs.Append(EncodeCache.OptionalTool("dovi_tool.exe"));
            if (job.Uhd && src.Hdr == "HDR10+") inputs = inputs.Append(EncodeCache.OptionalTool("hdr10plus_tool.exe"));
            EncodeCache.Prepare(prepared, settings, inputs, () => Prepare(job, title, src, audio, prepared, kbps, log, noVideo: mvc), log);
        }
        PrepareSubtitles(job, title, src, [.. subs.Where(x => x.Track.Index >= 0)], log);
        // a subtitle with nothing to show in the part kept is an empty file, which tsMuxeR cannot take
        Blanks(subs, lined != null, file => new FileInfo(file).Length == 0, job.Uhd ? (1920, 1080) : VideoSize(job), log);
        foreach (var (s, file) in subs.Where(x => x.Track.IsPgs && x.Track.File != null))
        {
            // the pictures of a PGS file are placed on a plane of its own size: 1920x1080 on a Blu-ray, also on a 4K one
            var (planeW, planeH) = job.Uhd ? (1920, 1080) : VideoSize(job);
            if (PgsSubtitles.PlaneSize(File.ReadAllBytes(file)) is { } plane && plane != (planeW, planeH))
                log($"warning: subtitle '{s.Name}' is made for a {plane.Width}x{plane.Height} screen, the disc is {planeW}x{planeH}: its pictures may not sit where they were meant to");
        }

        var fps = DiscRate(mvc ? src.Fps : Fps(job, src), VideoSize(job).H).TsMuxer; // a 3D film is copied: its own rate
        var chapterTimes = title.ChapterList(src.Duration, job.ChapterMinutes);
        var chapters = string.Join(';', chapterTimes.Select(t => TimeSpan.FromSeconds(t).ToString(@"hh\:mm\:ss\.fff", Inv)));
        var (w, h) = VideoSize(job);
        var lines = new List<string>
        {
            // PCR on its own PID, and for a copied H.264 the SEI / SPS a player needs added where missing: what Leawo's and Yuhan's tsMuxeR lines also ask
            // a 3D film takes two clips (left eye N, right eye N+1): numbered apart so that the next title's clip does not land on its right eye
            // --new-audio-pes: the sound in the packets a pressed Blu-ray carries it in (stream id 0xFD with its extension, read on a commercial disc; tsMuxeR writes
            // a plain 0xBD otherwise), which is also what tells a TrueHD from its AC-3 core
            $"MUXOPT {(job.Uhd ? "--blu-ray-v3" : "--blu-ray")} --label={job.Label} --vbr --no-pcr-on-video-pid --new-audio-pes --custom-chapters={chapters} --mplsOffset={playlist} --m2tsOffset={(mvc ? 200 + 2 * playlist : playlist)}",
        };
        var planes = new List<int>(); // by subtitle track: the depth a 3D film shows it at, -1 = on the screen
        var shifts = new List<int>(); // by audio track: how many ms it starts before (-) or after the picture in the source
        var copyEac3 = job.Encode?.CopyEac3 != false;
        var lossless = job.Encode?.Lossless != false;
        var normalize = job.Encode?.NormalizeAudio == true;
        var preparedAudioStarts = !mvc && audio.Any(a => a.Codec == "eac3")
            ? PreparedAudioStarts(prepared) : [];
        if (mvc)
        {
            // both eyes from the .ssif, so each right-eye picture pairs with its left one; the sound comes from prepared.ts. tsMuxeR starts every track with the
            // picture, so the sound gets back its own start in the source (a film cut from a disc may begin with sound a frame or more before its first picture)
            lines.Add($"V_MPEG4/ISO/AVC, \"{ssif}\", insertSEI, contSPS, fps={fps}, track=4113");
            lines.Add($"V_MPEG4/ISO/MVC, \"{ssif}\", fps={fps}, track=4114");
            var tracks = PlaylistTracks(title.Video);
            var streams = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "stream=codec_type,id,start_time", "-of", "csv=p=0", title.Video])
                .Split('\n').Select(l => l.Trim().Split(',')).Where(f => f.Length == 3).ToList();
            double Start(string[] f) => double.TryParse(f[2], NumberStyles.Float, Inv, out var t) ? t : 0;
            var picture = streams.FirstOrDefault(f => f[0] == "video") is { } v ? Start(v) : 0;
            shifts = [.. streams.Where(f => f[0] == "audio").Select(f => (int)Math.Round((Start(f) - picture) * 1000))];
            planes = [.. streams.Where(f => f[0] == "subtitle").Select(f => tracks.TryGetValue(Pid(f[1]), out var t) ? t.Plane : -1)];
            log($"{title.DisplayName}: 3D kept (Blu-ray 3D, left eye and right eye){(planes.Any(p => p >= 0) ? ", subtitles at their depth" : "")}");
        }
        else if (job.Uhd && src.DualLayer && CopiesVideo(job, title, src, kbps))
        {
            // Dolby Vision profile 7 as a UHD disc carries it: the HDR10 base and the enhancement layer (with the Dolby Vision data) in two tracks, which tsMuxeR puts on their own PIDs
            var (raw, bl, el) = (Path.Combine(work, $"dv{playlist}.hevc"), Path.Combine(work, $"bl{playlist}.hevc"), Path.Combine(work, $"el{playlist}.hevc"));
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-i", prepared, "-map", "0:v:0", "-c", "copy", "-f", "hevc", raw], log);
            Tools.Run(Tools.Find("dovi_tool.exe"), ["demux", raw, "-b", bl, "-e", el], log);
            File.Delete(raw);
            lines.Add($"V_MPEGH/ISO/HEVC, \"{bl}\", fps={fps}");
            lines.Add($"V_MPEGH/ISO/HEVC, \"{el}\", fps={fps}");
            log($"{title.DisplayName}: Dolby Vision kept (profile 7, base and enhancement layer)");
        }
        else
        {
            // a copied MPEG-2 (an early Blu-ray's film) stays MPEG-2: a Blu-ray carries it as well as H.264
            var mpeg2 = !job.Uhd && src.PlayerReadyMpeg2 && CopiesVideo(job, title, src, kbps);
            lines.Add($"{(job.Uhd ? "V_MPEGH/ISO/HEVC" : mpeg2 ? "V_MPEG-2" : "V_MPEG4/ISO/AVC")}, \"{prepared}\", {(job.Uhd || mpeg2 ? "" : "insertSEI, contSPS, ")}fps={fps}, track=256");
            if (mpeg2) log($"{title.DisplayName}: MPEG-2 copied as it is");
            // a re-encoded Dolby Vision title: its metadata on an enhancement layer made to match the new base (profile 7 MEL)
            var el = Path.ChangeExtension(prepared, ".rpu.el.hevc");
            if (job.Uhd && File.Exists(el))
            {
                lines.Add($"V_MPEGH/ISO/HEVC, \"{el}\", fps={fps}");
                log($"{title.DisplayName}: Dolby Vision kept (re-encoded, profile 7 MEL)");
            }
        }
        var tsTrack = 0;
        for (var j = 0; j < audio.Count; j++)
        {
            var mode = AudioMode(audio[j], title, job.Uhd, lossless, copyEac3, normalize);
            var inTs = NeedsTsAudio(mvc, audio[j], mode);
            var pidIndex = tsTrack;
            if (inTs) tsTrack++;
            var start = audio[j].Index < shifts.Count ? shifts[audio[j].Index] : 0;
            // tsMuxeR starts each muxed soundtrack on the picture; keep the actual start of a late DD+ track after copy or conversion.
            if (audio[j].Codec == "eac3" && pidIndex < preparedAudioStarts.Count) start = preparedAudioStarts[pidIndex];
            if (mode == "copy" && audio[j] is { Codec: "eac3", DiscEac3: false } && title.Gaps.Count > 0)
                start -= (int)Math.Round(1000 * title.Gaps.Sum(g => Math.Clamp(start / 1000.0 - g.From, 0, g.To - g.From)));
            var shift = start + (int)Math.Round((title.AudioDelay?.GetValueOrDefault(audio[j].Index) ?? 0) * 1000);
            if (mode == "copy" && audio[j] is { Codec: "eac3", DiscEac3: false })
                DiscDolbyDigitalPlus.Extract(prepared, $"0:i:0x{(mvc ? 256 : 257) + pidIndex:X}", AudioFile(prepared, j, "eac3"));
            lines.Add(mode switch
            {
                "thd" => $"A_AC3, \"{AudioFile(prepared, j, "hd.ac3")}\"", // tsMuxeR finds the TrueHD between the AC-3 frames
                "lpcm" => $"A_LPCM, \"{AudioFile(prepared, j, "w64")}\"",
                "copy" when audio[j] is { Codec: "eac3", DiscEac3: false } => $"A_AC3, \"{AudioFile(prepared, j, "eac3")}\"",
                "copy" => $"{AudioTag(audio[j].Codec)}, \"{prepared}\", track={(mvc ? 256 : 257) + pidIndex}",
                _ => $"A_AC3, \"{prepared}\", track={(mvc ? 256 : 257) + pidIndex}", // without picture, ffmpeg numbers the sound from PID 256
            } + $", lang={Languages.Iso2(audio[j].Lang)}" + (shift != 0 ? $", timeshift={shift}ms" : ""));
        }
        foreach (var (s, file) in subs)
            // no fps on a picture track: given one, tsMuxeR re-times the pictures by (rate written in the track) / fps, and a track made here says 23.976
            // whatever the video is, so on a 25 fps title every line came 4 % early (70 s at the end of a 28-minute bonus)
            lines.Add(s.IsPicture
                ? $"S_HDMV/PGS, \"{file}\", lang={Languages.Iso2(s.Lang)}" + (s.Index >= 0 && s.File == null && s.Index < planes.Count && planes[s.Index] >= 0 ? $", 3d-plane={planes[s.Index]}" : "")
                : $"S_TEXT/UTF8, \"{file}\", {TextSubtitleLook(job.Subtitles, h, job.Uhd)}, font-border=5, text-align=center, video-width={w}, video-height={h}, fps={fps}, lang={Languages.Iso2(s.Lang)}");
        if (job.HdmvMenu && job.Menu is { Popup: true } && playlist != IntroPlaylist)
        {
            var popupIgs = HdmvMenu.BuildPopupIgs(job, playlist, audio.Count, subs.Count, fps, work);
            var planeScale = Math.Min(1, h / 1080.0);
            lines.Add($"S_HDMV/IGS, \"{popupIgs}\", lang=eng, fps={fps}, video-width={MenuRenderer.Px(MenuBuilder.W, planeScale)}, video-height={MenuRenderer.Px(MenuBuilder.H, planeScale)}");
            log($"menu HDMV : pop-up intégré sur le titre {playlist}");
        }
        var meta = Path.Combine(work, $"disc{playlist}.meta");
        File.WriteAllLines(meta, lines);
        log($"mux {playlist}: tsMuxeR");
        try { Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, dest], log); }
        catch (InvalidOperationException) { foreach (var line in lines) log("meta: " + line); throw; } // the work folder goes with the failure: what was asked stays in the log
        var muxedAudio = Mpls.Read(Path.Combine(dest, "BDMV", "PLAYLIST", $"{playlist:00000}.mpls")).Items[0].Audio;
        if (muxedAudio.Count != audio.Count)
            throw new InvalidDataException($"{title.DisplayName} : liste {playlist:00000}.mpls, {muxedAudio.Count} pistes audio multiplexées sur {audio.Count} demandées.");
        if (job.Uhd)
        {
            var clip = Path.Combine(dest, "BDMV", "STREAM", $"{playlist:00000}.m2ts");
            using var probe = JsonDocument.Parse(Tools.Run(Tools.Find("ffprobe.exe"),
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,profile,pix_fmt,color_transfer,color_primaries,color_space", "-of", "json", clip]));
            var video = probe.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
            string Field(string name) => video.ValueKind == JsonValueKind.Object && video.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
            if (!IsUhdMain10(Field("codec_name"), Field("profile"), Field("pix_fmt")))
                throw new InvalidDataException($"{title.DisplayName} : clip UHD {playlist:00000}.m2ts sans vidéo HEVC Main 10 4:2:0.");
            if (src.DolbyVisionHdr10Base && (Field("color_transfer"), Field("color_primaries"), Field("color_space")) != ("smpte2084", "bt2020", "bt2020nc"))
                throw new InvalidDataException($"{title.DisplayName} : le clip UHD {playlist:00000}.m2ts a perdu la signalisation de sa base HDR10.");
        }
        if (copyEac3 && audio.Any(a => a.Codec == "eac3" && CopiesAudio(a, title, job.Uhd, copyEac3, normalize)))
        {
            var clip = Path.Combine(dest, "BDMV", "STREAM", $"{playlist:00000}.m2ts");
            for (var j = 0; j < audio.Count; j++)
            {
                if (audio[j].Codec != "eac3" || !CopiesAudio(audio[j], title, job.Uhd, copyEac3, normalize)) continue;
                var raw = AudioFile(prepared, j, "check.eac3");
                var problem = $"{title.DisplayName} : flux Dolby Digital Plus invérifiable après multiplexage pour {audio[j].Name}.";
                try
                {
                    if (muxedAudio[j].Coding == 0x84)
                    {
                        DiscDolbyDigitalPlus.Extract(clip, $"0:i:0x{muxedAudio[j].Pid:X}", raw);
                        if (audio[j].DiscEac3 ? DiscDolbyDigitalPlus.HasCoreAndExtension(raw) :
                            DiscDolbyDigitalPlus.IsStandalone(raw, audio[j].Channels)) continue;
                    }
                    throw new InvalidDataException(problem);
                }
                catch (InvalidOperationException ex) { throw new InvalidDataException(problem, ex); }
                finally { File.Delete(raw); }
            }
        }
        // a 3D title's prepared.ts holds no picture to take thumbnails from
        var carried = subs.Select(s => s.Track).ToList();
        return new Muxed(mvc ? title.Video : prepared, audio.Select(a => a.Name).ToList(), carried, chapterTimes, title.DefaultSubtitleNumber(carried),
                         audio.Select(a => a.Lang).ToList());
    }

    /// <summary>
    /// The subtitles a title carries, <paramref name="lined"/> when given, else its supported kept ones, each with the file it is written to (<paramref name="file"/>
    /// of its number, then .sup for pictures, .srt for text). Another image format (DVB, Teletext) would need OCR: left out.
    /// </summary>
    internal static List<(SubtitleTrack Track, string File)> Subtitles(DiscTitle title, SourceInfo src, IReadOnlyList<SubtitleTrack>? lined, Func<int, string> file, Action<string> log)
    {
        foreach (var s in title.KeptSubtitles(src).Where(t => !t.Supported)) log($"subtitle '{s.Name}' skipped: {s.Codec} is an image format that needs OCR");
        return [.. (lined ?? [.. title.KeptSubtitles(src).Where(t => t.Supported)]).Select((s, k) => (Track: s, File: $"{file(k + 1)}.{(s.IsPicture ? "sup" : "srt")}"))];
    }

    /// <summary>
    /// The subtitles that show nothing (<paramref name="empty"/> once written, or a track the title lacks: <see cref="SubtitleTrack.Index"/> -1). A title on its own
    /// leaves them out, a choice that would show nothing; one lined up with others (<paramref name="lined"/>) carries an invisible PGS track there instead,
    /// since the menu and the remote select a subtitle by its number, the same for every title.
    /// </summary>
    internal static void Blanks(List<(SubtitleTrack Track, string File)> subs, bool lined, Func<string, bool> empty, (int Width, int Height) plane, Action<string> log)
    {
        for (var k = 0; k < subs.Count; k++)
        {
            var (s, file) = subs[k];
            if (s.Index >= 0 && !empty(file)) continue;
            if (!lined) { log($"subtitle '{s.Name}' skipped: nothing to show in the part kept"); subs.RemoveAt(k--); continue; }
            log($"subtitle {k + 1} '{s.Name}': {(s.Index < 0 ? "not in this video" : "nothing to show in the part kept")}, an invisible track keeps its number");
            file = Path.ChangeExtension(file, ".sup");
            File.WriteAllBytes(file, PgsSubtitles.Blank(plane.Width, plane.Height));
            subs[k] = (s with { Index = -1, Codec = "hdmv_pgs_subtitle", File = null }, file);
        }
    }

    /// <summary>
    /// The file a moving menu plays, its probe and the second to start at: the design's file, else the first title, which counts
    /// <see cref="MenuDesign.LoopStart"/> from the start of the part kept (a cut-out intro never shows on the menu).
    /// </summary>
    /// A clip shorter than the loop is played again from its start until the loop ends (<c>Input</c> then carries ffmpeg's -stream_loop),
    /// so a 2-minute clip under a 4-minute song repeats instead of stopping half-way.
    internal static (string File, SourceInfo Src, int Start, string[] Input) LoopSource(MenuDesign menu, DiscTitle first, int seconds)
    {
        var file = menu.LoopFile is { Length: > 0 } f && File.Exists(f) ? f : first.Video;
        var src = Probe(file);
        var own = file == first.Video;
        var room = own ? first.Length(src) : src.Duration;
        var start = (own ? (int)first.Start : 0) + Math.Clamp(menu.LoopStart, 0, Math.Max(0, (int)room - seconds));
        var repeat = (own ? first.Start + room : room) - start < seconds - 0.5;
        string[] input = ["-ss", start.ToString(Inv), "-t", seconds.ToString(Inv), "-i", file];
        return (file, src, start, repeat ? ["-stream_loop", "-1", .. input] : input);
    }

    /// <summary>
    /// How long the menu loop lasts: <see cref="MenuDesign.LoopSeconds"/>, or with 0 the music's length from <see cref="MenuDesign.MusicStart"/>
    /// (the whole song, not cut), within 5 s and <paramref name="max"/> (300 on a Blu-ray, 120 on a DVD); when 0 and no music, the length of
    /// the design's own clip, else 30 s.
    /// </summary>
    internal static int LoopLength(MenuDesign menu, int max)
    {
        if (menu.LoopSeconds > 0) return Math.Clamp(menu.LoopSeconds, 5, max);
        // without music, a moving menu with a clip of its own (a template's moving background) lasts as long as that clip
        var music = menu.ExistingMusic() ?? (menu.Loop && menu.LoopFile is { Length: > 0 } clip && File.Exists(clip) ? clip : null);
        var shown = music == null ? "" : Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", music]).Trim();
        var from = menu.ExistingMusic() == null ? menu.LoopStart : menu.MusicStart;
        return double.TryParse(shown, NumberStyles.Float, Inv, out var length) ? Math.Clamp((int)Math.Floor(length - Math.Max(0, from)), 5, max) : Math.Min(30, max);
    }

    /// <summary>
    /// The menu music as one file any encoder takes: from <see cref="MenuDesign.MusicStart"/>, repeated to fill <paramref name="seconds"/>, at
    /// <see cref="MenuDesign.MusicVolume"/> %, faded in and out by <see cref="MenuDesign.MusicFade"/> seconds, 48 kHz stereo (a disc does not take 44.1 kHz).
    /// </summary>
    internal static string PrepareMusic(MenuDesign menu, string music, int seconds, string work, Action<string> log)
    {
        const double MusicLufs = -27; // integrated loudness of a film's soundtrack on disc, roughly: the menu should not shout over it
        var ffmpeg = Tools.Find("ffmpeg.exe");
        // not Probe: it wants a video stream
        var shown = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", music]).Trim();
        if (!double.TryParse(shown, CultureInfo.InvariantCulture, out var length)) throw new InvalidDataException($"{Path.GetFileName(music)}: not an audio file");
        var start = Math.Max(0, menu.MusicStart);
        if (start >= length - 1)
        {
            log($"warning: the music lasts {length:0} s, so it starts at 0 instead of {start} s");
            start = 0;
        }
        var part = Path.Combine(work, "music_part.wav");
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-ss", start.ToString(Inv), "-i", music, "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "48000", part], log); // -vn: cover art is a video stream
        if (new FileInfo(part).Length < 100_000) throw new InvalidDataException($"{Path.GetFileName(music)}: nothing to play from {start} s"); // looping an empty file never ends
        var fade = Math.Clamp(menu.MusicFade, 0, seconds / 2);
        // a song is mastered far louder than a film (Bright: menu song -11 LUFS, film -25): one linear gain down to a film's level, never up (no clipping)
        double? loudness = null;
        Tools.Run(ffmpeg, ["-hide_banner", "-nostats", "-i", part, "-af", "ebur128", "-f", "null", "-"],
            l => { if (Regex.Match(l, @"^\s*I:\s+(-?\d+(?:\.\d+)?) LUFS") is { Success: true } m) loudness = double.Parse(m.Groups[1].Value, Inv); });
        var gain = loudness is double lufs && lufs > MusicLufs ? Math.Pow(10, (MusicLufs - lufs) / 20) : 1;
        if (gain < 1) log($"menu music: {loudness:0.0} LUFS brought down to {MusicLufs} LUFS, a film's loudness");
        var filter = $"volume={(gain * Math.Clamp(menu.MusicVolume, 0, 300) / 100.0).ToString("0.#####", Inv)}"
            + (fade > 0 ? $",afade=t=in:st=0:d={fade},afade=t=out:st={seconds - fade}:d={fade}" : "");
        var output = Path.Combine(work, "music.wav");
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-stream_loop", "-1", "-i", part, "-t", seconds.ToString(Inv), "-af", filter, "-ac", "2", "-ar", "48000", output], log);
        return output;
    }

    /// <summary>
    /// The clip the menu plays behind its pages, muxed apart as playlist <see cref="LoopPlaylist"/> and merged into the disc:
    /// a moving menu = <see cref="MenuDesign.LoopSeconds"/> seconds of video (the design's file, else the first title) re-encoded as 1080p H.264;
    /// A static menu uses black video behind its opaque graphics. Its audio is the chosen music or generated silence.
    /// </summary>
    static void MuxLoop(BluRayJob job, string work, Action<string> log)
    {
        var menu = job.Menu!;
        var musicFile = menu.ExistingMusic();
        var silent = !menu.Loop && musicFile == null;
        var seconds = silent ? SilentMenuLoopSeconds : LoopLength(menu, MaxLoopSeconds);
        var music = musicFile == null ? null : PrepareMusic(menu, musicFile, seconds, work, log);
        var (w, h) = VideoSize(job);
        var ts = Path.Combine(work, "loop.ts");
        var args = new List<string> { "-y", "-v", "warning" };
        SourceInfo? src = null;
        var (fps, declared) = DiscRate(0);
        var tone = "";
        if (menu.Loop)
        {
            var (file, probe, start, input) = LoopSource(menu, job.Titles[0], seconds);
            src = probe;
            (fps, declared) = DiscRate(Fps(job, src), h);
            tone = !job.Uhd && ToSdr(src.Hdr, src.Width, src.Height, w) is { Length: > 0 } sdr ? sdr + "," : ""; // a moving menu taken from an HDR film; a 4K disc keeps its HDR, like the film
            log($"menu: moving background, {seconds}s from {Path.GetFileName(file)} at {start}s{(music != null ? " + music" : "")}");
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
        var hasAudio = silent || music != null || src is { Audio.Count: > 0 };
        args.AddRange(["-t", seconds.ToString(Inv),
            "-vf", tone + (src?.SourceView ?? "") + Fit(w, h) + (src is { } loop ? HdMatrix(loop) : ""), "-r", fps]);
        args.AddRange(MenuCodec(job.Uhd, src?.Hdr ?? "SDR", fps, h));
        if (hasAudio) args.AddRange(["-c:a", "ac3", "-b:a", "448k", "-ac", "2", "-ar", "48000"]); // the first title's own sound can be anything
        if (hasAudio && menu.Loop) args.AddRange(["-af", "apad"]);
        args.AddRange(["-sn", "-dn", ts]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);

        var meta = Path.Combine(work, "loop.meta");
        // Null packets keep small menu clips from reaching several play items in the player's initial read-ahead.
        var muxRate = menu.Loop ? $"--vbr --minbitrate=8000 --maxbitrate={(job.Uhd ? 60000 : 32000)}" : "--vbr";
        var lines = new List<string>
        {
            $"MUXOPT {(job.Uhd ? "--blu-ray-v3" : "--blu-ray")} --label={job.Label} {muxRate} --no-pcr-on-video-pid --new-audio-pes --mplsOffset={LoopPlaylist} --m2tsOffset={LoopPlaylist}",
            job.Uhd ? $"V_MPEGH/ISO/HEVC, \"{ts}\", fps={declared}, track=256" : $"V_MPEG4/ISO/AVC, \"{ts}\", fps={declared}, track=256",
        };
        if (hasAudio) lines.Add($"A_AC3, \"{ts}\", track=257");
        File.WriteAllLines(meta, lines);
        var dest = Path.Combine(work, "mux" + LoopPlaylist);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, dest], log);
        if (menu.Loop)
        {
            var name = $"{LoopPlaylist:00000}.mpls";
            var playlist = Path.Combine(dest, "BDMV", "PLAYLIST", name);
            var repeated = Mpls.RepeatMenuClip(File.ReadAllBytes(playlist));
            File.WriteAllBytes(playlist, repeated);
            File.WriteAllBytes(Path.Combine(dest, "BDMV", "BACKUP", "PLAYLIST", name), repeated);
        }
        Merge(dest, job.OutDir);
    }

    /// <summary>How the video of a menu clip is encoded: 10-bit HEVC like a 4K film (see Prepare; only the base layer of a Dolby Vision film is taken, as HDR10), else H.264 within a Blu-ray's limits.</summary>
    internal static string[] MenuCodec(bool uhd, string hdr, string fps, int h) => uhd
        ? ["-c:v", "libx265", "-preset", "fast", "-crf", "20", "-pix_fmt", "yuv420p10le", "-dolbyvision", "0",
           "-x265-params", $"log-level=error:{X265Colors(hdr)}:uhd-bd=1:repeat-headers=1:aud=1:keyint=24:vbv-maxrate=40000:vbv-bufsize=40000"]
        : ["-c:v", "libx264", "-preset", "fast", "-crf", "19", "-pix_fmt", "yuv420p",
           "-x264-params", "bluray-compat=1:level=41:vbv-maxrate=20000:vbv-bufsize=20000:keyint=24:slices=4:bframes=3:b-pyramid=strict:open-gop=0" + Fields(fps, h)];

    /// <summary>Moves the playlist, clip info and stream files of a title muxed apart into the disc folder.</summary>
    internal static void Merge(string from, string into)
    {
        foreach (var sub in new[] { @"BDMV\PLAYLIST", @"BDMV\CLIPINF", @"BDMV\STREAM", @"BDMV\BACKUP\PLAYLIST", @"BDMV\BACKUP\CLIPINF" })
        {
            var src = Path.Combine(from, sub);
            if (!Directory.Exists(src)) continue;
            Directory.CreateDirectory(Path.Combine(into, sub));
            foreach (var f in Directory.EnumerateFiles(src)) File.Move(f, Path.Combine(into, sub, Path.GetFileName(f)), true);
        }
    }

    static string AudioTag(string codec) => codec switch
    {
        "dts" => "A_DTS", _ => "A_AC3",
    };

    static List<int> PreparedAudioStarts(string prepared)
    {
        // Probe past the opening logos even if a soundtrack first starts during the film.
        using var probe = JsonDocument.Parse(Tools.Run(Tools.Find("ffprobe.exe"),
            ["-v", "error", "-analyzeduration", "60000000", "-probesize", "350000000", "-show_entries", "stream=codec_type,start_time", "-of", "json", prepared]));
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToList();
        static double? Start(JsonElement stream) =>
            stream.TryGetProperty("start_time", out var time) && double.TryParse(time.GetString(), NumberStyles.Float, Inv, out var seconds) ? seconds : null;
        var video = Start(streams.First(s => s.GetProperty("codec_type").GetString() == "video")) ?? 0;
        return [.. streams.Where(s => s.GetProperty("codec_type").GetString() == "audio")
            .Select(s => (int)Math.Round(((Start(s) ?? video) - video) * 1000))];
    }

    /// <summary>The codec a source audio ends up as on the disc: copied when a Blu-ray carries it and the volume stays, AC-3 otherwise; with "Son sans perte", TrueHD or LPCM.</summary>
    public static string BdAudio(AudioTrack a, DiscTitle t, bool uhd, bool lossless = true, bool copyEac3 = true, bool normalize = false) =>
        AudioMode(a, t, uhd, lossless, copyEac3, normalize) switch { "copy" => a.Codec, "thd" => "truehd", "lpcm" => "lpcm", _ => "ac3" };

    /// <summary>
    /// Copies compatible streams, carries TrueHD with companion AC-3 and uses LPCM when audio has to be decoded in quality mode.
    /// </summary>
    static string AudioMode(AudioTrack a, DiscTitle t, bool uhd, bool lossless, bool copyEac3, bool normalize = false) =>
        CopiesAudio(a, t, uhd, copyEac3, normalize) ? "copy" : !lossless ? "ac3"
        // 48 kHz only: tsMuxeR counts a TrueHD's samples against the core's 1536 at 48 kHz
        : !normalize && a.Codec == "truehd" && a.SampleRate == 48000 && t.Volume == 100 && t.AudioVolumeFilter(a.Index) == "" && t.Start == 0 && t.Gaps.Count == 0 ? "thd" : "lpcm";

    /// <summary>A lossless track's file beside prepared.ts: tsMuxeR reads neither TrueHD nor LPCM out of ffmpeg's transport stream.</summary>
    static string AudioFile(string prepared, int track, string kind) => Path.ChangeExtension(prepared, $".a{track}.{kind}");

    /// <summary>A disc's LPCM is 48, 96 or 192 kHz, at 16 or 24 bits.</summary>
    static int LpcmRate(AudioTrack a) => a.SampleRate > 96000 && a.Channels <= 6 ? 192000 : a.SampleRate > 48000 ? 96000 : 48000;
    static int LpcmBits(AudioTrack a) => a.Bits is > 0 and <= 16 ? 16 : 24;

    /// <summary>
    /// A TrueHD track as a Blu-ray carries it: each AC-3 frame of its core (1536 samples) followed by the TrueHD access units of the same time (40 samples each
    /// at 48 kHz). tsMuxeR reads that raw file as one "A_AC3" track and finds the TrueHD in it.
    /// </summary>
    public static void InterleaveTrueHd(string trueHd, string core, int coreKbps, string output)
    {
        using var hd = File.OpenRead(trueHd);
        using var ac3 = File.OpenRead(core);
        using var into = File.Create(output);
        var frame = new byte[coreKbps * 4]; // an AC-3 frame at 48 kHz: 32 ms of its bit rate
        var unit = new byte[8192];
        for (long samples = 0, frames = 0; ;)
        {
            if (samples >= frames * 1536 && ac3.ReadAtLeast(frame, frame.Length, false) == frame.Length)
            {
                if (frame[0] != 0x0B || frame[1] != 0x77) throw new InvalidDataException("TrueHD: the AC-3 core is not a run of whole frames");
                into.Write(frame);
                frames++;
            }
            if (hd.ReadAtLeast(unit.AsSpan(0, 2), 2, false) < 2) break;
            var size = ((unit[0] & 0x0F) << 8 | unit[1]) * 2; // an access unit starts with its length, in 16-bit words
            if (size < 4 || hd.ReadAtLeast(unit.AsSpan(2, size - 2), size - 2, false) < size - 2) break;
            into.Write(unit, 0, size);
            samples += 40;
        }
    }

    /// <summary>
    /// A copied audio track keeps everything from the keyframe before a cut's start (stream copy does not drop it), while the re-encoded video begins on the exact frame: sound
    /// a second early. So a title that starts later has its sound re-encoded too (decoded, then cut exactly); a 4K disc copies both from the same keyframe and stays in step.
    /// The disc carries Dolby Digital at 48 kHz and DTS-HD can also use 96 or 192 kHz.
    /// </summary>
    const string OverlapDrop = "noise=drop='if(lt(pts+duration/2,ld(0)),1,0*st(0,pts+duration))'"; // ld(0): where the last frame kept ends
    static bool CopiesAudio(AudioTrack a, DiscTitle t, bool uhd, bool copyEac3, bool normalize = false) =>
        !normalize && t.Volume == 100 && t.AudioVolumeFilter(a.Index) == "" && (t.Start == 0 || uhd)
        && (t.Gaps.Count == 0 || t.Start == 0 && a is { Codec: "eac3", DiscEac3: false })
        && (a.Codec == "dts" && (a.SampleRate is 48000 or 96000 || a.SampleRate == 192000 && a.Channels <= 6)
            || a.SampleRate == 48000 && (a.Codec == "ac3" || copyEac3 && a.Codec == "eac3"));

    static void RequireSupportedDolbyBase(SourceInfo source, string title)
    {
        if (source.Hdr == "Dolby Vision" && !source.DolbyVisionHdr10Base)
            throw new InvalidDataException($"{title} : Dolby Vision profil {source.DolbyVisionProfile}, base HDR10 non établie. Cette source nécessite une conversion colorimétrique préalable.");
    }

    static bool IsUhdMain10(string codec, string profile, string pixelFormat) =>
        codec == "hevc" && profile == "Main 10" && pixelFormat == "yuv420p10le";

    /// <summary>Whether the title's video goes on the disc as it is (stream copy) instead of being re-encoded; <see cref="TargetKbps"/> and <see cref="Prepare"/> must agree on it.</summary>
    internal static bool CopiesVideo(BluRayJob job, DiscTitle title, SourceInfo s, int kbps)
    {
        var (w, h) = VideoSize(job);
        var rate = DiscRate(Fps(job, s), h).Ffmpeg;
        var fpsOk = Math.Abs(s.Fps - Value(rate)) < 0.01; // the disc keeps the source's rate as it is (720p has no 25: such a source is made again at 50)
        var enc = job.Encode ?? new EncodeOptions();
        // a cut that starts later must be re-encoded to begin on the exact frame (a copy starts on the keyframe before it); a 4K passthrough copies it anyway
        var copyCut = title.Start == 0 || (job.Uhd && !enc.ForceReencode && kbps == 0);
        // Copy a UHD picture only when the bitstream is already Main 10, 4:2:0.
        return !enc.ForceReencode && kbps == 0 && copyCut && (job.Uhd ? IsUhdMain10(s.VideoCodec, s.Profile, s.PixFmt) && s.Level is > 0 and <= 153 // 4:2:0 up to level 5.1, all a 4K player takes
                      // a file above 48 Mb/s on average, sound included, cannot hold a video within a Blu-ray's 40
                      : (s.PlayerReadyH264 || s.PlayerReadyMpeg2) && s.Hdr == "SDR" && Tools.SizeOf(title.Video) * 8.0 / Math.Max(1, s.FileDuration > 0 ? s.FileDuration : s.Duration) <= 48e6)
               && s.Width == w && s.Height == h && fpsOk && !s.Rotated
               && title.Effects is null or { IsEmpty: true } // effects are drawn into the picture, so it is made again
               && title.Gaps.Count == 0 // several clips are joined frame by frame
               // 1080 at 25 or 29.97 exists on a Blu-ray only as fields: a progressive stream is made again with the flags (last: it reads the file)
               && (Fields(rate, h).Length == 0 || FlaggedAsFields(title.Video));
    }

    static bool NeedsTsAudio(bool noVideo, AudioTrack a, string mode) => noVideo || mode is not ("thd" or "lpcm") || a.Codec == "eac3";

    static double KeyFrameBefore(string video, double start)
    {
        if (start <= 0) return 0;
        try
        {
            var windowStart = Math.Max(0, start - 15).ToString("0.###", Inv);
            var outText = Tools.Run(Tools.Find("ffprobe.exe"),
                ["-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey", "-read_intervals", $"{windowStart}%+20",
                 "-show_entries", "packet=pts_time", "-of", "csv=p=0", video]);
            var best = 0.0;
            var found = false;
            foreach (var line in outText.Split('\n'))
                if (double.TryParse(line.Trim(), NumberStyles.Float, Inv, out var pts) && pts <= start + 0.01 && pts >= best)
                { best = pts; found = true; }
            return found ? best : start;
        }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException) { return start; }
    }

    /// <summary>
    /// Remuxes to prepared.ts (ffmpeg numbers the streams from PID 256: video = 256, kept audio = 257..), re-encoding only what a Blu-ray cannot carry,
    /// and writes the subtitle files in the same pass over the source.
    /// </summary>
    static void Prepare(BluRayJob job, DiscTitle title, SourceInfo s, IReadOnlyList<AudioTrack> audio, string output, int kbps, Action<string> log,
                        bool noVideo = false, bool forceCpu = false) // noVideo: the sound and subtitles only (a 3D film's picture is muxed from the source itself)
    {
        var (w, h) = VideoSize(job);
        var enc = job.Encode ?? new EncodeOptions();
        var videoOk = CopiesVideo(job, title, s, kbps);
        if (job.Uhd && !videoOk && !IsUhdMain10(s.VideoCodec, s.Profile, s.PixFmt))
            log($"{title.DisplayName} : copie UHD impossible ({s.VideoCodec} {s.Profile}, {s.PixFmt}), conversion HEVC Main 10 4:2:0.");
        if (job.Uhd && videoOk && title.Start > 0) log($"{title.DisplayName}: 4K is not re-encoded, so the cut starts on the keyframe before {Timecode.Format(title.Start)}");

        // -ss / -t before -i cut the input itself: the video, the sound and the subtitle files written below all start and stop together
        File.Delete(Path.ChangeExtension(output, ".rpu.bin")); // its presence tells MuxTitle to add the Dolby Vision layer
        var args = new List<string> { "-y", "-v", "warning" };
        if (title.Start > 0) args.AddRange(["-ss", title.Start.ToString(Inv)]);
        var span = s.Duration + title.GapLength; // the part read: from the first clip's start to the last one's end
        if (title.End != null) args.AddRange(["-t", span.ToString("0.###", Inv)]);
        if (audio.Any(a => a.Codec == "eac3" && a.File == null)) args.AddRange(LateAudioProbe);
        args.AddRange(["-i", title.Video]);
        double? audioStart = videoOk && job.Uhd && title.Start > 0 && audio.Any(a => a.File != null) ? KeyFrameBefore(title.Video, title.Start) : null;
        var maps = title.AudioMaps(audio, args, span, audioStart); // a sound file added to the video is one more input
        var modes = audio.Select(a => AudioMode(a, title, job.Uhd, enc.Lossless, enc.CopyEac3, enc.NormalizeAudio)).ToArray();
        if (!noVideo) args.AddRange(["-map", "0:v:0"]);
        for (var j = 0; j < audio.Count; j++)
            if (NeedsTsAudio(noVideo, audio[j], modes[j])) args.AddRange(["-map", maps[j]]);
        if (videoOk && !noVideo) args.AddRange(["-c:v", "copy"]);
        // a UHD disc takes Dolby Vision only as two tracks (profile 7): tsMuxeR puts a single-layer one on the enhancement-layer PID, with no base, and no player reads it
        if (videoOk && job.Uhd && s.Hdr == "Dolby Vision" && !s.DualLayer)
        {
            args.AddRange(["-bsf:v", "dovi_rpu=strip=1"]);
            log($"{title.DisplayName}: Dolby Vision {s.DolbyVisionProfile} : copie de la base HDR10 vérifiée, métadonnées Dolby Vision retirées.");
        }
        // effects are placed in the source's pixels: with some, the picture keeps its size until they are drawn
        var tone = job.Uhd ? "" : title.Effects is null or { IsEmpty: true } ? ToSdr(s.Hdr, s.Width, s.Height, w) : ToSdr(s.Hdr);
        var autoDeint = s.Interlaced && (title.Effects?.Deinterlace ?? "auto") == "auto" ? "bwdif=mode=0:parity=auto:deint=1" : "";
        var firstFilter = string.Join(",", new[] { autoDeint, tone }.Where(f => f.Length > 0));
        var rate = DiscRate(Fps(job, s), h).Ffmpeg;
        // Hardware GPU (NVIDIA NVENC -> Intel QuickSync -> AMD AMF). CPU when the GPU cannot: 25i/29.97i fields (x264 PsF), 4K HDR10+/DV, or no compatible GPU.
        var vendor = forceCpu ? GpuVendor.None : job.Uhd ? HardwareGpuHevc.Value : HardwareGpu.Value.Vendor;
        var cpuOnly = forceCpu ? "repli CPU" : Fields(rate, h).Length > 0 ? "interlaced output" : job.Uhd && s.Hdr is "HDR10+" or "Dolby Vision" ? s.Hdr + " metadata" : vendor == GpuVendor.None ? "aucun encodeur GPU compatible" : null;
        var gpu = !videoOk && enc.Gpu && cpuOnly == null;
        if (!videoOk && enc.Gpu && !gpu) log($"{title.DisplayName}: encoded by the processor ({cpuOnly})");
        else if (gpu) log($"{title.DisplayName}: encodage matériel ({HardwareGpu.Value.Label})");
        if (gpu && vendor == GpuVendor.Nvidia) args.InsertRange(args.IndexOf("-i"), ["-hwaccel", "auto"]);
        if (!videoOk) args.AddRange(["-vf", (title.Effects ?? new()).Vf(s.Width, s.Height, title.Start, s.HalfStereo + Fit(w, h) + HdMatrix(s), firstFilter) + (title.Gaps.Count > 0 ? "," + title.GapFilter(false) : ""),
                            "-r", rate, "-pix_fmt", !job.Uhd ? "yuv420p" : gpu ? "p010le" : "yuv420p10le"]);
        if (gpu)
        {
            var maxrate = job.Uhd ? (kbps > 0 ? Math.Clamp(kbps + kbps / 2, 40000, 100000) : 100000) : kbps > 0 ? Math.Clamp(kbps + kbps / 4, 30000, 40000) : 30000;
            if (vendor == GpuVendor.Nvidia)
            {
                args.AddRange(["-c:v", job.Uhd ? "hevc_nvenc" : "h264_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres", "-spatial-aq", "1",
                               "-g", "24", "-bf", "3", "-b_ref_mode", "disabled", "-aud", "1", "-maxrate", maxrate + "k", "-bufsize", (job.Uhd ? maxrate : 30000) + "k"]);
                args.AddRange(job.Uhd ? ["-profile:v", "main10", "-level", "5.1", "-tier", "high"] : ["-profile:v", "high", "-level", "4.1", "-slices", "4"]);
                args.AddRange(kbps > 0 ? ["-b:v", kbps.ToString(Inv) + "k"] : ["-cq", Math.Clamp(enc.Crf, 10, 28).ToString(Inv), "-b:v", "0"]);
            }
            else if (vendor == GpuVendor.Intel)
            {
                args.AddRange(["-c:v", job.Uhd ? "hevc_qsv" : "h264_qsv", "-preset", "slower", "-g", "24", "-bf", "3", "-aud", "1",
                               "-maxrate", maxrate + "k", "-bufsize", (job.Uhd ? maxrate : 30000) + "k"]);
                args.AddRange(job.Uhd ? ["-profile:v", "main10", "-level:v", "5.1"] : ["-profile:v", "high", "-level:v", "4.1"]);
                args.AddRange(kbps > 0 ? ["-b:v", kbps.ToString(Inv) + "k"] : ["-global_quality", Math.Clamp(enc.Crf + 2, 14, 30).ToString(Inv)]);
            }
            else // AMD AMF
            {
                args.AddRange(["-c:v", job.Uhd ? "hevc_amf" : "h264_amf", "-quality", "quality", "-g", "24", "-aud", "1",
                               "-maxrate", maxrate + "k", "-bufsize", (job.Uhd ? maxrate : 30000) + "k"]);
                args.AddRange(job.Uhd ? ["-profile:v", "main10", "-level", "5.1"] : ["-profile:v", "high", "-level", "4.1"]);
                args.AddRange(kbps > 0 ? ["-rc", "vbr_peak", "-b:v", kbps.ToString(Inv) + "k"] : ["-rc", "cqp", "-qp_i", Math.Clamp(enc.Crf + 1, 14, 28).ToString(Inv), "-qp_p", Math.Clamp(enc.Crf + 2, 14, 30).ToString(Inv)]);
            }
            if (job.Uhd)
            {
                var (prim, trc, matrix) = s.Hdr switch { "HDR10" => ("bt2020", "smpte2084", "bt2020nc"), "HLG" => ("bt2020", "arib-std-b67", "bt2020nc"), _ => ("bt709", "bt709", "bt709") };
                args.AddRange(["-color_primaries", prim, "-color_trc", trc, "-colorspace", matrix]);
            }
        }
        else if (!videoOk && job.Uhd)
        {
            args.AddRange(["-c:v", "libx265", "-preset", enc.Preset]);
            var colors = X265Colors(s.Hdr);
            var maxrate = kbps > 0 ? Math.Clamp(kbps + kbps / 2, 40000, 100000) : 100000;
            args.AddRange(kbps > 0 ? ["-b:v", kbps.ToString(Inv) + "k"] : ["-crf", Math.Clamp(enc.Crf, 10, 28).ToString(Inv)]);
            var plus = s.Hdr == "HDR10+" ? Hdr10Plus(title, s, rate, Path.ChangeExtension(output, ".hdr10plus.json"), log) : null;
            var dv = s.Hdr == "Dolby Vision" ? DolbyVisionRpu(title, s, rate, Path.ChangeExtension(output, ".rpu.bin"), log) : null;
            args.AddRange(["-profile:v", "main10", "-dolbyvision", "0", "-x265-params", $"log-level=error:{colors}:uhd-bd=1:repeat-headers=1:aud=1:keyint=24:vbv-maxrate={maxrate}:vbv-bufsize={maxrate}"
                           + (plus != null ? $":dhdr10-info='{plus.Replace('\\', '/').Replace("'", @"'\''")}'" : "") + (dv != null ? ":" + FixedGop : "")]);
        }
        else if (!videoOk)
        {
            args.AddRange(["-c:v", "libx264", "-preset", enc.Preset]);
            var maxrate = kbps > 0 ? Math.Clamp(kbps + kbps / 4, 30000, 40000) : 30000;
            args.AddRange(kbps > 0 ? ["-b:v", kbps.ToString(Inv) + "k"] : ["-crf", Math.Clamp(enc.Crf, 10, 28).ToString(Inv)]);
            args.AddRange(["-x264-params", $"bluray-compat=1:level=41:vbv-maxrate={maxrate}:vbv-bufsize=30000:keyint=24:slices=4:bframes=3:b-pyramid=strict:open-gop=0{Fields(rate, h)}"]);
        }
        string Af(int track) => string.Join(",", new[] { title.GapFilter(true), title.Volume != 100 ? $"volume={(title.Volume / 100.0).ToString("0.###", Inv)}" : "", title.AudioVolumeFilter(track), enc.NormalizeAudio ? "loudnorm=I=-16:TP=-1.5:LRA=11" : "" }.Where(f => f.Length > 0));
        var tsOut = 0;
        for (var j = 0; j < audio.Count; j++)
        {
            if (title.AudioVolumeFilter(audio[j].Index) is { Length: > 0 } louder && AudioMode(audio[j], title with { AudioVolume = null }, job.Uhd, enc.Lossless, enc.CopyEac3, enc.NormalizeAudio) is var kept && kept != modes[j])
                log($"{title.DisplayName}: {audio[j].Name}: {louder}, so the track is made again as {BdAudio(audio[j], title, job.Uhd, enc.Lossless, enc.CopyEac3, enc.NormalizeAudio)} instead of {(kept == "copy" ? "copied" : "kept as TrueHD")}");
            if (audio[j].Codec == "eac3" && !CopiesAudio(audio[j], title, job.Uhd, enc.CopyEac3, enc.NormalizeAudio))
            {
                var fallback = enc.Lossless ? "LPCM" : "AC-3 48 kHz";
                log($"{title.DisplayName} : {audio[j].Name}, Dolby Digital Plus (copie désactivée ou incompatible avec les réglages) converti en {fallback}. Les éventuels objets Atmos ne sont pas conservés.");
            }
            if (!NeedsTsAudio(noVideo, audio[j], modes[j])) continue;
            var outIdx = tsOut++;
            if (CopiesAudio(audio[j], title, job.Uhd, enc.CopyEac3, enc.NormalizeAudio))
            {
                var filter = OverlapDrop;
                if (title.Gaps.Count > 0)
                {
                    var gaps = string.Join("+", title.Gaps.Select(g => $"gte(pts*tb,{g.From.ToString(Inv)})*lt(pts*tb,{g.To.ToString(Inv)})"));
                    filter = $"noise=drop='{gaps}',{OverlapDrop}";
                }
                args.AddRange([$"-c:a:{outIdx}", "copy", $"-bsf:a:{outIdx}", filter]);
                if (audio[j].Codec == "eac3") log($"{title.DisplayName} : {audio[j].Name}, Dolby Digital Plus copié"
                    + (audio[j].DiscEac3 ? " avec son noyau AC-3 et son extension dépendante." : " sans conversion ; lecture sur platine à vérifier."));
            }
            else
            {
                args.AddRange([$"-c:a:{outIdx}", "ac3", $"-b:a:{outIdx}", audio[j].Channels > 2 ? "640k" : "448k", $"-ar:a:{outIdx}", "48000"]);
                if (audio[j].Channels > 6) args.AddRange([$"-ac:a:{outIdx}", "6"]);
                if (Af(audio[j].Index) is { Length: > 0 } af) args.AddRange([$"-filter:a:{outIdx}", af]);
            }
        }
        args.AddRange(["-sn", "-dn", output]);
        // "Son sans perte": TrueHD + AC-3 core or LPCM .w64 written directly without duplicating unused AC-3 in prepared.ts
        for (var j = 0; j < audio.Count; j++)
        {
            var (a, mode) = (audio[j], modes[j]);
            if (mode == "thd")
            {
                args.AddRange(["-map", maps[j], "-c:a", "copy", "-bsf:a", "setts=ts=N", "-f", "truehd", AudioFile(output, j, "thd")]);
                args.AddRange(["-map", maps[j], "-c:a", "ac3", "-b:a", a.Channels > 2 ? "640k" : "448k", "-ar", "48000", "-ac", Math.Min(a.Channels, 6).ToString(Inv), "-f", "ac3", AudioFile(output, j, "ac3")]);
                log($"{title.DisplayName}: {a.Name}: TrueHD kept as it is, with the AC-3 core a disc wants");
            }
            else if (mode == "lpcm")
            {
                args.AddRange(["-map", maps[j], "-c:a", LpcmBits(a) == 16 ? "pcm_s16le" : "pcm_s24le", "-ar", LpcmRate(a).ToString(Inv)]);
                if (a.Channels is 7 or > 8) args.AddRange(["-ac", "8"]);
                if (Af(a.Index) is { Length: > 0 } af) args.AddRange(["-af", af]);
                args.Add(AudioFile(output, j, "w64"));
                log($"{title.DisplayName}: {a.Name}: décodé en LPCM {LpcmRate(a) / 1000} kHz {LpcmBits(a)} bits.");
            }
        }
        if (!noVideo || audio.Count > 0)
        {
            try { Tools.Run(Tools.Find("ffmpeg.exe"), args, log, seconds: s.Duration); }
            catch (InvalidOperationException e) when (gpu)
            {
                log($"encodage GPU échoué ({e.Message.Split('\n')[0]}), bascule sur le processeur (CPU)…");
                Prepare(job, title, s, audio, output, kbps, log, noVideo, forceCpu: true);
                return;
            }
            catch (InvalidOperationException e) { log($"conversion failed, trying once more: {e.Message.Split('\n')[0]}"); Tools.Run(Tools.Find("ffmpeg.exe"), args, log, seconds: s.Duration); }
        }
        for (var j = 0; j < audio.Count; j++)
            if (modes[j] == "thd")
            {
                InterleaveTrueHd(AudioFile(output, j, "thd"), AudioFile(output, j, "ac3"), audio[j].Channels > 2 ? 640 : 448, AudioFile(output, j, "hd.ac3"));
                File.Delete(AudioFile(output, j, "thd"));
                File.Delete(AudioFile(output, j, "ac3"));
            }
        var rpu = Path.ChangeExtension(output, ".rpu.bin");
        if (job.Uhd && File.Exists(rpu)) DolbyLayer(output, rpu, rate, log);
    }

    static void PrepareSubtitles(BluRayJob job, DiscTitle title, SourceInfo s, List<(SubtitleTrack Track, string File)> subs, Action<string> log)
    {
        var span = s.Duration + title.GapLength;
        WriteOwnSubtitles(title, subs.Where(x => x.Track.File == null && !x.Track.IsVobSub).ToList(), span, log); // a VobSub is made apart, the subtitle files too
        foreach (var (t, file) in subs.Where(x => x.Track.File != null)) WriteExternalSubtitle(title, t, span, file, job.Uhd ? (1920, 1080) : VideoSize(job), log);
        foreach (var (t, file) in subs.Where(x => x.Track.File == null && x.Track.IsVobSub)) WriteEmbeddedVobSub(title, t, span, file, job.Uhd ? (1920, 1080) : VideoSize(job), log);
        foreach (var (t, file) in subs) { title.Delay(t, file, span); title.CloseGaps(file, span); }
        foreach (var (t, file) in subs.Where(x => !x.Track.IsPicture)) TidySubtitle(file);
        if (job.Subtitles is { } st && (st.Lift > 0 || st.Scale != 100 || (job.Uhd && st.CalibratedNits is > 0 and < 400)))
            foreach (var (_, file) in subs.Where(x => x.Track.IsPicture && File.Exists(x.File) && Tools.SizeOf(x.File) > 0))
                File.WriteAllBytes(file, Disc.PgsStream.Transform(File.ReadAllBytes(file), st.Lift, st.Scale, job.Uhd ? st.CalibratedNits : 0));
    }

    /// <summary>
    /// The HDR10+ metadata of the frames a re-encode keeps (from <see cref="DiscTitle.Start"/>, without the gaps), as the JSON x265 reads; null when it cannot be kept.
    /// x265 takes it frame by frame, so a disc rate other than the source's would put it on the wrong pictures.
    /// </summary>
    static string? Hdr10Plus(DiscTitle title, SourceInfo s, string rate, string json, Action<string> log)
    {
        if (Math.Abs(s.Fps - Value(rate)) >= 0.01) { log($"{title.DisplayName}: HDR10+ dropped, the frame rate changes (HDR10 stays)"); return null; }
        var tool = "";
        var whole = json + ".all.json";
        try { tool = Tools.Find("hdr10plus_tool.exe"); Tools.Run(tool, ["extract", title.Video, "-o", whole]); }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException) { log($"{title.DisplayName}: HDR10+ not read ({e.Message.Split('\n').Last()}), HDR10 stays"); return null; }
        Keep(tool, whole, json, Removed(title, s));
        log($"{title.DisplayName}: HDR10+ kept");
        return json;
    }

    /// <summary>The source frames a re-encode leaves out (before <see cref="DiscTitle.Start"/>, in the gaps), as the "a-b" ranges hdr10plus_tool's and dovi_tool's editors take.</summary>
    static List<string> Removed(DiscTitle title, SourceInfo s)
    {
        int Frame(double t) => (int)Math.Ceiling(t * s.Fps - 0.001); // ffmpeg keeps the frames from this time on
        var first = Frame(title.Start);
        return (first > 0 ? [$"0-{first - 1}"] : new List<string>())
            .Concat(title.Gaps.Select(g => (From: first + Frame(g.From), To: first + Frame(g.To) - 1)).Where(g => g.To >= g.From).Select(g => $"{g.From}-{g.To}")).ToList();
    }

    /// <summary>Writes <paramref name="whole"/>'s metadata less the <paramref name="remove"/> frames to <paramref name="kept"/>, with the tool's editor.</summary>
    static void Keep(string tool, string whole, string kept, List<string> remove)
    {
        if (remove.Count == 0) { File.Move(whole, kept, true); return; }
        File.WriteAllText(kept + ".edit.json", JsonSerializer.Serialize(new { remove }));
        Tools.Run(tool, ["editor", whole, "-j", kept + ".edit.json", "-o", kept]);
    }

    /// <summary>
    /// The structure x265 is held to when a re-encode keeps Dolby Vision: a fixed GOP (no adaptive B frames, no scene cuts), so the enhancement layer
    /// made apart (<see cref="DolbyLayer"/>) has the very same picture types and order as the base, as a player pairs them.
    /// </summary>
    const string FixedGop = "b-adapt=0:scenecut=0:bframes=3:keyint=24:min-keyint=24:open-gop=0";

    /// <summary>
    /// The Dolby Vision metadata of the frames a re-encode keeps, made profile 7 MEL (dovi_tool mode 1: an enhancement layer that adds nothing to the picture),
    /// the form a UHD disc carries; null when it cannot be kept (a frame rate that changes, a profile 5 source whose base is not HDR10).
    /// </summary>
    static string? DolbyVisionRpu(DiscTitle title, SourceInfo s, string rate, string rpu, Action<string> log)
    {
        if (Math.Abs(s.Fps - Value(rate)) >= 0.01) { log($"{title.DisplayName}: Dolby Vision dropped, the frame rate changes (HDR10 stays)"); return null; }
        var tool = "";
        var whole = rpu + ".all.bin";
        try
        {
            tool = Tools.Find("dovi_tool.exe");
            // dovi_tool reads Matroska and raw HEVC; anything else (a .m2ts, an .mp4) goes through a raw copy of the video first
            var input = Path.GetExtension(title.Video).ToLowerInvariant() is ".mkv" or ".hevc" or ".h265" or ".265" ? title.Video : rpu + ".hevc";
            if (input != title.Video) Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-i", title.Video, "-map", "0:v:0", "-c", "copy", "-f", "hevc", input]);
            try { Tools.Run(tool, ["-m", "1", "extract-rpu", input, "-o", whole]); }
            finally { if (input != title.Video) File.Delete(input); }
        }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException)
        {
            log($"{title.DisplayName}: Dolby Vision not read ({e.Message.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()}), HDR10 stays");
            return null;
        }
        Keep(tool, whole, rpu, Removed(title, s));
        return rpu;
    }

    /// <summary>
    /// The enhancement layer of a re-encoded Dolby Vision title: a black 1920x1080 HEVC with the base's frame count and structure (<see cref="FixedGop"/>),
    /// carrying the profile 7 MEL metadata. tsMuxeR puts it on its own PID next to the base. Returns the file, or null when the frames do not match.
    /// </summary>
    static string? DolbyLayer(string prepared, string rpu, string rate, Action<string> log)
    {
        var ffprobe = Tools.Find("ffprobe.exe");
        var frames = int.Parse(Tools.Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", prepared]).Trim().Split('\n')[0].Trim(), Inv);
        var dovi = Tools.Find("dovi_tool.exe");
        var summary = Tools.Run(dovi, ["info", "-i", rpu, "--summary"]);
        var have = int.Parse(System.Text.RegularExpressions.Regex.Match(summary, @"Frames: (\d+)").Groups[1].Value, Inv);
        if (have < frames) { log($"warning: Dolby Vision has {have} frames for {frames} pictures, not kept (HDR10 stays)"); return null; }
        if (have > frames) Keep(dovi, rpu, rpu + ".fit.bin", [$"{frames}-{have - 1}"]); // the end a cut leaves out
        var fitted = have > frames ? rpu + ".fit.bin" : rpu;
        var black = Path.ChangeExtension(rpu, ".el0.hevc");
        var el = Path.ChangeExtension(rpu, ".el.hevc");
        Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-f", "lavfi", "-i", $"color=c=black:s=1920x1080:r={rate}", "-frames:v", frames.ToString(Inv),
            "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-x265-params", $"log-level=error:{FixedGop}:repeat-headers=1:aud=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc", "-f", "hevc", black], log);
        Tools.Run(dovi, ["inject-rpu", "-i", black, "--rpu-in", fitted, "-o", el], log);
        File.Delete(black);
        return el;
    }

    /// <summary>
    /// A picture fitted into the disc's <paramref name="w"/> x <paramref name="h"/> frame with bars, as it is seen: non-square pixels (a DVD rip, a camcorder's
    /// 702x574 at 16:15) are first made square, else the fit would read the stored size and squeeze the picture.
    /// </summary>
    public static string Fit(int w, int h) => $"scale=trunc(iw*sar/2)*2:ih,setsar=1,scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1";

    /// <summary>
    /// ffmpeg turns ASS into SRT with a font tag around each line and {\an8}-style codes: tsMuxeR then renders no subtitle at all (found with an
    /// ASS track: the stream came out empty), so both go; italics, bold and underline stay.
    /// </summary>
    static void TidySubtitle(string file)
    {
        var text = File.ReadAllText(file);
        var clean = System.Text.RegularExpressions.Regex.Replace(text, @"\{\\[^}]*\}|</?font[^>]*>", "");
        if (clean != text) File.WriteAllText(file, clean);
    }

    /// <summary>
    /// The video's own text and PGS tracks, read whole in one pass and cut at the exact times like a subtitle file. Not in the picture's pass: an input -ss
    /// starts the subtitles at the keyframe before the cut (found on a real episode: every cue 0.74 s late), where the decoded picture and sound start on the exact frame.
    /// </summary>
    public static void WriteOwnSubtitles(DiscTitle title, IReadOnlyList<(SubtitleTrack Track, string File)> subs, double span, Action<string> log)
    {
        if (subs.Count == 0) return;
        var args = new List<string> { "-y", "-v", "warning", "-i", title.Video };
        foreach (var (t, file) in subs) args.AddRange(["-map", $"0:s:{t.Index}", "-c:s", t.IsPgs ? "copy" : "srt", "-f", t.IsPgs ? "sup" : "srt", file]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        foreach (var (t, file) in subs)
            if (t.IsPgs) File.WriteAllBytes(file, PgsSubtitles.Cut(File.ReadAllBytes(file), title.Start, span));
            else File.WriteAllText(file, ExternalSubtitle.Cut(File.ReadAllText(file), title.Start, span));
    }

    /// <summary>A VobSub inside the video as the PGS file the disc tools take: made for a <paramref name="plane"/>-sized screen (<see cref="VobSub.FromVideo"/>), cut like the video.</summary>
    internal static void WriteEmbeddedVobSub(DiscTitle title, SubtitleTrack track, double length, string output, (int Width, int Height) plane, Action<string> log)
    {
        var sup = VobSub.FromVideo(title.Video, track.Index, plane.Width, plane.Height, Path.GetDirectoryName(output)!, out var skipped);
        log($"VobSub in {Path.GetFileName(title.Video)}: {track.Name}, made into PGS for a {plane.Width}x{plane.Height} screen" + (skipped > 0 ? $"; {skipped} picture(s) could not be read and are left out" : ""));
        File.WriteAllBytes(output, PgsSubtitles.Cut(sup, title.Start, length));
    }

    /// <summary>
    /// A subtitle file beside the video as the SRT the disc tools take (a .sup stays one, cut by <see cref="PgsSubtitles.Cut"/>; an .idx + .sub is made into one for a
    /// <paramref name="plane"/>-sized screen first, <see cref="VobSub.ToSup"/>): cut like the video (<see cref="ExternalSubtitle.Cut"/>), read as
    /// Unicode when it is one, else as Windows-1252 (what French subtitle files usually are),
    /// (on a Blu-ray the text tracks are then tidied by <see cref="TidySubtitle"/>).
    /// </summary>
    internal static void WriteExternalSubtitle(DiscTitle title, SubtitleTrack track, double length, string output, (int Width, int Height) plane, Action<string> log)
    {
        if (track.IsPgs) // pictures: cut by their own timing, not converted
        {
            var vobSub = Path.GetExtension(track.File!).Equals(".idx", StringComparison.OrdinalIgnoreCase);
            var skipped = 0;
            var sup = vobSub ? VobSub.ToSup(track.File!, track.Stream, plane.Width, plane.Height, out skipped) : File.ReadAllBytes(track.File!);
            if (vobSub) log($"VobSub {Path.GetFileName(track.File!)}: {track.Name}, made into PGS for a {plane.Width}x{plane.Height} screen" + (skipped > 0 ? $"; {skipped} picture(s) could not be read and are left out" : ""));
            File.WriteAllBytes(output, PgsSubtitles.Cut(sup, title.Start, length));
            return;
        }
        var args = new List<string> { "-y", "-v", "error" };
        if (!IsUnicode(File.ReadAllBytes(track.File!))) args.AddRange(["-sub_charenc", "CP1252"]);
        args.AddRange(["-i", track.File!, "-c:s", "srt", "-f", "srt", output]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args, log);
        File.WriteAllText(output, ExternalSubtitle.Cut(File.ReadAllText(output), title.Start, length)); // not ffmpeg's -ss/-t: they keep a cue that ended before the start
    }

    internal static bool IsUnicode(byte[] bytes)
    {
        if (bytes.Length >= 2 && (bytes[0], bytes[1]) is (0xFF, 0xFE) or (0xFE, 0xFF)) return true; // UTF-16 byte order mark
        try { new System.Text.UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (System.Text.DecoderFallbackException) { return false; }
    }

    /// <summary>GPR the disc's start sets to 1 and the Top Menu key to 0 before they jump to title 1, the BD-J menu: the xlet starts on the first title only when it reads 1.</summary>
    public const int BdjStartRegister = 109;

    /// <summary>The files of a jar but its manifest and signature (fresh archive: ZipArchiveMode.Update corrupted entries of the prebuilt jar; its manifest gives way to the signed one).</summary>
}
