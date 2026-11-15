using BurnSmith.Core;

/// <summary>
/// burnsmith check-recap : « À savoir avant de démarrer » (Core.Recap) on sources made up here, so no video is read: a Blu-ray copies the H.264 1080p24 episodes
/// and converts the HEVC one, remakes its TrueHD 7.1 as Dolby Digital 5.1, says how full the disc is and what plays at the start; a DVD converts everything.
/// </summary>
static class RecapCheck
{
    public static int Run()
    {
        var failed = 0;
        void Check(string what, bool ok, IEnumerable<string> lines)
        {
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}{(ok ? "" : " : " + string.Join(" | ", lines))}");
        }
        var dir = Path.Combine(Path.GetTempPath(), "bs_recap_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // three 45-minute episodes, 500 MB each (the size is what the estimate and the copy's bit-rate check read); no byte written: SetLength
            var files = Enumerable.Range(1, 3).Select(i => Path.Combine(dir, $"ep{i}.mkv")).ToList();
            foreach (var f in files) using (var s = File.Create(f)) s.SetLength(500_000_000);
            AudioTrack Ac3 = new(0, "ac3", 6, "fre", ""), TrueHd = new(0, "truehd", 8, "fre", "", Lossless: true);
            SubtitleTrack Forced = new(0, "hdmv_pgs_subtitle", "fre", "", true);
            SourceInfo Source(string codec, AudioTrack audio) => new(codec, 1920, 1080, 24000 / 1001.0, audio.Codec, audio.Channels, 2700, [audio], [Forced],
                                                                    PixFmt: "yuv420p", Profile: "High", Level: 41);
            var sources = new Dictionary<string, SourceInfo> { [files[0]] = Source("h264", Ac3), [files[1]] = Source("hevc", TrueHd), [files[2]] = Source("h264", Ac3) };
            var titles = files.Select(f => new DiscTitle(f, Subtitles: [0])).ToList();
            var menu = new BurnSmith.Core.Menu.MenuDesign();
            var bd = Recap.For(new BluRayJob(titles, "", menu, Encode: new(Gpu: false), CapacityGB: 23.3), f => sources[f]);
            foreach (var line in bd) Console.WriteLine("     " + line);
            Check("Blu-ray : 2 épisodes copiés, 1 converti, avec une durée", bd[0].StartsWith("Vidéos copiées telles quelles : 2 ; converties : 1 (environ ") && bd[0].EndsWith("min)."), bd);
            Check("Blu-ray : le TrueHD 7.1 de l'épisode 2 refait en Dolby Digital 5.1, rien sur l'AC-3 copié", bd.Count(l => l.StartsWith("Le son ")) == 1 && bd.Contains("Le son TrueHD 7.1 de la vidéo 2 sera refait en Dolby Digital 5.1."), bd);
            Check("Blu-ray : le disque rempli à 6 % (1,5 Go sur 23,3)", bd.Any(l => l == "Le disque sera rempli à 6 %."), bd);
            Check("Blu-ray : français par défaut, sous-titres forcés actifs (menu BD-J)", bd[^1] == "Au démarrage : son Français 5.1, sous-titres forcés actifs.", bd);

            var shown = Recap.For(new BluRayJob([titles[0] with { DefaultSubtitle = 0 }], "", null, CapacityGB: 0), f => sources[f]);
            Check("Blu-ray d'une vidéo, sans limite : copiée, taille en Go, sous-titre choisi affiché", shown[0] == "La vidéo est copiée telle quelle, sans perte."
                  && shown[1].StartsWith("Environ ") && shown[1].EndsWith(" Go, sans limite de taille.") && shown[^1] == "Au démarrage : son Français 5.1, sous-titres Français (forcés) affichés.", shown);

            var dvd = Recap.For(new DvdJob(titles, "", menu, CapacityGB: 4.2), f => sources[f]);
            foreach (var line in dvd) Console.WriteLine("     " + line);
            Check("DVD : les 3 vidéos converties, le TrueHD refait, pas de sous-titres forcés suivant le son", dvd[0].StartsWith("Les 3 vidéos seront converties (environ ")
                  && dvd.Contains("Le son TrueHD 7.1 de la vidéo 2 sera refait en Dolby Digital 5.1.") && dvd[^1] == "Au démarrage : son Français 5.1, sans sous-titres.", dvd);
        }
        finally { Tools.TryDelete(dir); }
        Console.WriteLine(failed == 0 ? "check-recap: all good" : $"check-recap: {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
