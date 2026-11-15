namespace BurnSmith.Core;

/// <summary>
/// « À savoir avant de démarrer » : what a build is about to do, in everyday French, read from the decisions the builds themselves make
/// (<see cref="BluRay.CopiesVideo"/>, <see cref="BluRay.TargetKbps"/>, <see cref="BluRay.BdAudio"/>, a DVD's MPEG-2 and AC-3, the size estimates, the default tracks).
/// The app shows it before « Démarrer », the CLI prints it with --recap. Videos are numbered as the job lists them.
/// </summary>
public static class Recap
{
    /// <param name="probe">What ffprobe says of a file (the app has it already); null = ask ffprobe.</param>
    public static List<string> For(BluRayJob job, Func<string, SourceInfo>? probe = null)
    {
        var sources = job.Titles.Select(t => t.Cut((probe ?? BluRay.Probe)(t.Video))).ToList();
        var kbps = BluRay.TargetKbps(job, sources, _ => { });
        var copied = job.Titles.Select((t, i) => BluRay.CopiesVideo(job, t, sources[i], kbps)).ToList();
        // shortcut: speeds guessed (times real time) from builds on Marc's PC, NVIDIA card or processor; measure them if the minutes are far off
        double Speed() => job.Encode?.Gpu != false && BluRay.Nvenc.Value ? (job.Uhd ? 1.5 : 4) : job.Uhd ? 0.25 : 1;
        var lossless = job.Encode?.Lossless == true;
        return Lines(job.Titles, sources, copied, (a, t) => BluRay.BdAudio(a, t, job.Uhd, lossless), Speed, BluRay.EstimateGB(job, sources), job.CapacityGB,
                     job.Menu != null); // forced subtitles follow the sound through the BD-J menu's player
    }

    public static List<string> For(DvdJob job, Func<string, SourceInfo>? probe = null)
    {
        var sources = job.Titles.Select(t => t.Cut((probe ?? BluRay.Probe)(t.Video))).ToList();
        // a DVD makes every picture again as MPEG-2 and every sound as AC-3 (Dvd.EncodeTitle)
        return Lines(job.Titles, sources, [.. job.Titles.Select(_ => false)], (_, _) => "ac3", () => 6,
                     Dvd.EstimateGB([.. sources.Zip(job.Titles, (s, t) => (s, t))], job.CapacityGB, job.Encode?.DvdKbps ?? 0), job.CapacityGB, false);
    }

    static List<string> Lines(IReadOnlyList<DiscTitle> titles, List<SourceInfo> sources, List<bool> copied, Func<AudioTrack, DiscTitle, string> soundTo,
                              Func<double> speed, double gb, double capacityGB, bool forcedFollowSound)
    {
        var lines = new List<string>();
        var made = Enumerable.Range(0, titles.Count).Where(i => !copied[i]).ToList();
        var time = made.Count == 0 ? "" : Duration(made.Sum(i => sources[i].Duration) / speed());
        lines.Add(made.Count == 0 ? (titles.Count == 1 ? "La vidéo est copiée telle quelle, sans perte." : $"Les {titles.Count} vidéos sont copiées telles quelles, sans perte.")
            : made.Count == titles.Count ? (titles.Count == 1 ? $"La vidéo sera convertie (environ {time})." : $"Les {titles.Count} vidéos seront converties (environ {time}).")
            : $"Vidéos copiées telles quelles : {titles.Count - made.Count} ; converties : {made.Count} (environ {time}).");

        // one sentence per kind of sound remade, with the videos it concerns
        string To(AudioTrack a, string codec) => Sound(codec, codec == a.Codec && a.Lossless, codec == "ac3" ? Math.Min(a.Channels, 6) : a.Channels); // AC-3 tops out at 5.1
        var remade = titles.SelectMany((t, i) => t.KeptAudio(sources[i]).Select(a => (Video: i + 1, From: Sound(a.Codec, a.Lossless, a.Channels), To: To(a, soundTo(a, t)))))
                           .Where(x => x.From != x.To).GroupBy(x => (x.From, x.To));
        foreach (var g in remade)
        {
            var videos = g.Select(x => x.Video).Distinct().ToList();
            lines.Add(titles.Count == 1 ? $"Le son {g.Key.From} sera refait en {g.Key.To}."
                : $"Le son {g.Key.From} {(videos.Count == titles.Count ? "de toutes les vidéos" : videos.Count == 1 ? $"de la vidéo {videos[0]}" : $"des vidéos {string.Join(", ", videos)}")} sera refait en {g.Key.To}.");
        }

        lines.Add(capacityGB <= 0 ? $"Environ {gb:0.0} Go, sans limite de taille."
            : gb > capacityGB ? $"Trop grand : environ {gb:0.0} Go pour {capacityGB:0.#} Go utilisables."
            : $"Le disque sera rempli à {Math.Round(gb / capacityGB * 100)} %.");

        // what plays when the disc starts: the first main video's default sound, and the subtitle shown
        var first = Math.Max(0, titles.ToList().FindIndex(t => !t.Intro && !t.Bonus));
        var audio = titles[first].KeptAudio(sources[first]);
        var subs = titles[first].KeptSubtitles(sources[first]).Where(s => s.Supported).ToList();
        var shown = subs.FirstOrDefault(s => s.Index == titles[first].DefaultSubtitle);
        var forced = forcedFollowSound && audio.Count > 0 && BluRay.ForcedTracks([.. audio.Select(a => a.Lang)], [.. subs.Select(s => (s.Lang, s.Forced))]).Forced.Any(f => f.Audio == 1);
        lines.Add(audio.Count == 0 ? "Au démarrage : pas de son."
            : $"Au démarrage : son {audio[0].Name}, {(shown != null ? $"sous-titres {shown.Name} affichés" : forced ? "sous-titres forcés actifs" : "sans sous-titres")}.");
        return lines;
    }

    /// <summary>"Dolby Digital 5.1", "TrueHD 7.1", "DTS-HD MA 5.1"…: a sound as a box or a player names it.</summary>
    static string Sound(string codec, bool lossless, int channels) => ((codec switch
    {
        "ac3" => "Dolby Digital", "eac3" => "Dolby Digital Plus", "truehd" => "TrueHD", "dts" => lossless ? "DTS-HD MA" : "DTS", "lpcm" => "PCM",
        "aac" => "AAC", "flac" => "FLAC", "opus" => "Opus", "mp3" => "MP3", "vorbis" => "Vorbis", _ when codec.StartsWith("pcm_") => "PCM", _ => codec.ToUpperInvariant(),
    }) + " " + Languages.ChannelLayout(channels)).Trim();

    static string Duration(double seconds) => seconds < 3600 ? $"{Math.Max(1, Math.Round(seconds / 60))} min" : $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60):00}";
}
