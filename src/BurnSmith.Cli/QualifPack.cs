using System.Text;
using BurnSmith.Core;
using BurnSmith.Core.Menu;

namespace BurnSmith.Cli;

/// <summary>
/// Generates a compact hardware qualification pack (4 discs + checklist) ready to burn onto BD-RE or copy to USB/SD
/// to validate all hardware-dependent items of docs/COMPATIBILITE.md on a living-room player.
/// </summary>
static class QualifPack
{
    public static int Run(string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(outDir, "_sources");
        Directory.CreateDirectory(work);
        var ffmpeg = Tools.Find("ffmpeg.exe");

        try
        {
            // 1) Compact 12-second 1080p SDR clip with 2 distinct audio tones (French 880 Hz, English 440 Hz) + 2 SRT subtitles
            var clip1080 = Path.Combine(work, "mire_1080p.mkv");
            var subFr = Path.Combine(work, "mire_1080p.fr.srt");
            var subEn = Path.Combine(work, "mire_1080p.en.srt");
            File.WriteAllText(subFr, "1\n00:00:01,000 --> 00:00:05,000\nSous-titre français — Chapitre 1\n\n2\n00:00:06,500 --> 00:00:11,000\nSous-titre français — Chapitre 2\n", new UTF8Encoding(false));
            File.WriteAllText(subEn, "1\n00:00:01,000 --> 00:00:05,000\nEnglish subtitle — Chapter 1\n\n2\n00:00:06,500 --> 00:00:11,000\nEnglish subtitle — Chapter 2\n", new UTF8Encoding(false));

            Console.WriteLine("1/5 · Génération de la mire 1080p SDR (2 pistes audio, 2 chapitres)…");
            Tools.Run(ffmpeg, [
                "-y", "-v", "error",
                "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=24000/1001:d=12",
                "-f", "lavfi", "-i", "sine=f=880:r=48000:d=12",
                "-f", "lavfi", "-i", "sine=f=440:r=48000:d=12",
                "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-x264-params", "bluray-compat=1:level=41:keyint=24:slices=4:bframes=3:b-pyramid=strict:open-gop=0",
                "-c:a", "ac3", "-b:a", "192k", "-ar", "48000",
                "-metadata:s:a:0", "language=fre", "-metadata:s:a:1", "language=eng",
                clip1080
            ]);

            var title1080 = new DiscTitle(clip1080, "Mire 1080p", Audio: [0, 1], Subtitles: [0, 1], ChapterAt: [0, 6], ChapterTitles: ["Début", "Milieu"],
                External: [new ExternalSubtitle(subFr, "fre"), new ExternalSubtitle(subEn, "eng")]);

            // 2) 01_BD_HDMV_Anim_Popup.iso
            Console.WriteLine("2/5 · Fabrication de 01_BD_HDMV_Anim_Popup.iso…");
            var dirHdmv = Path.Combine(work, "01_hdmv");
            var hdmvDesign = new MenuDesign { Title = "Qualif HDMV", Loop = true, LoopSeconds = 6, Popup = true };
            BluRay.Build(new BluRayJob([title1080], dirHdmv, hdmvDesign, false, "QUALIF_HDMV", 5,
                new EncodeOptions(Preset: "veryfast", Gpu: false), 23.3, HdmvMenu: true), Console.WriteLine);
            AssertClean("01_BD_HDMV_Anim_Popup", DiscCheck.Folder(dirHdmv));
            Iso.Build(dirHdmv, Path.Combine(outDir, "01_BD_HDMV_Anim_Popup.iso"), "QUALIF_HDMV", false, Console.WriteLine);

            // 3) 02_BD_BDJ_Fade_Resume.iso
            Console.WriteLine("3/5 · Fabrication de 02_BD_BDJ_Fade_Resume.iso…");
            var dirBdj = Path.Combine(work, "02_bdj");
            var bdjDesign = new MenuDesign { Title = "Qualif BD-J", Loop = true, LoopSeconds = 6, Popup = true, PageFadeMs = 200 };
            BluRay.Build(new BluRayJob([title1080], dirBdj, bdjDesign, false, "QUALIF_BDJ", 5,
                new EncodeOptions(Preset: "veryfast", Gpu: false, NormalizeAudio: true), 23.3,
                new PlaybackOptions(AutoStartSeconds: 15, RememberPosition: true)), Console.WriteLine);
            AssertClean("02_BD_BDJ_Fade_Resume", DiscCheck.Folder(dirBdj));
            Iso.Build(dirBdj, Path.Combine(outDir, "02_BD_BDJ_Fade_Resume.iso"), "QUALIF_BDJ", false, Console.WriteLine);

            // 4) 03_UHD_HDR10_203nits.iso
            Console.WriteLine("4/5 · Fabrication de 03_UHD_HDR10_203nits.iso…");
            var clipUhd = Path.Combine(work, "mire_uhd_hdr10.mkv");
            Tools.Run(ffmpeg, [
                "-y", "-v", "error",
                "-f", "lavfi", "-i", "testsrc2=s=3840x2160:r=24000/1001:d=8",
                "-f", "lavfi", "-i", "sine=f=880:r=48000:d=8",
                "-map", "0:v:0", "-map", "1:a:0",
                "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
                "-x265-params", "log-level=error:uhd-bd=1:hdr10=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:repeat-headers=1:aud=1:keyint=24",
                "-c:a", "ac3", "-b:a", "448k", "-ar", "48000", "-metadata:s:a:0", "language=fre",
                clipUhd
            ]);
            var titleUhd = new DiscTitle(clipUhd, "Mire UHD HDR10", Audio: [0], Subtitles: [0], ChapterAt: [0, 4],
                External: [new ExternalSubtitle(subFr, "fre")]);
            var dirUhd = Path.Combine(work, "03_uhd");
            var uhdDesign = new MenuDesign { Title = "Qualif UHD 203 nits", Popup = true, HdrGraphicsNits = 203 };
            BluRay.Build(new BluRayJob([titleUhd], dirUhd, uhdDesign, true, "QUALIF_UHD", 5,
                new EncodeOptions(Preset: "ultrafast", Gpu: false), 23.3, Subtitles: new SubtitleStyle(HdrNits: 203)), Console.WriteLine);
            AssertClean("03_UHD_HDR10_203nits", DiscCheck.Folder(dirUhd));
            Iso.Build(dirUhd, Path.Combine(outDir, "03_UHD_HDR10_203nits.iso"), "QUALIF_UHD", false, Console.WriteLine);

            // 5) 04_AVCHD_USB/AVCHD/BDMV
            Console.WriteLine("5/5 · Fabrication de 04_AVCHD_USB/AVCHD…");
            var dirAvchd = Path.Combine(outDir, "04_AVCHD_USB");
            BluRay.Build(new BluRayJob([title1080], dirAvchd, null, false, "QUALIF_AVCHD", 5,
                new EncodeOptions(Preset: "veryfast", Gpu: false), 4.2, Avchd: true), Console.WriteLine);
            AssertClean("04_AVCHD_USB", DiscCheck.Folder(dirAvchd));

            File.WriteAllText(Path.Combine(outDir, "CHECKLIST-PLATINE.md"),
                """
                # Checklist de qualification platine de salon (BurnSmith)

                Graver les ISO sur BD-RE (2× recommandé) et copier `04_AVCHD_USB\AVCHD` à la racine d'une clé USB / carte SD FAT32 ou exFAT.

                - [ ] **01_BD_HDMV_Anim_Popup.iso** :
                  - [ ] Le fond animé tourne en boucle sans coupure.
                  - [ ] Touche **Pop-up** pendant la lecture : barre lisible (`Reprendre`, `Menu principal`, `Audio 1/2`, `Sous-titres`), changement de piste effectif.
                - [ ] **02_BD_BDJ_Fade_Resume.iso** :
                  - [ ] Fondu au noir fluide (200 ms) entre Menu principal et Chapitres/Réglages.
                  - [ ] Démarrage automatique après 15 s sans touche.
                  - [ ] Stopper / éjecter / réinsérer le disque : reprise automatique de la lecture (si stockage persistant autorisé par la platine).
                - [ ] **03_UHD_HDR10_203nits.iso** :
                  - [ ] Le téléviseur bascule en HDR10 sur le film ; menus, pop-up et sous-titres restent confortables (203 nits, non éblouissants).
                - [ ] **04_AVCHD_USB/AVCHD** :
                  - [ ] Reconnu comme dossier AVCHD sur clé USB / carte SD et lu avec les deux pistes audio et les sous-titres.
                """, new UTF8Encoding(false));

            Console.WriteLine($"ok   pack de qualification prêt dans {outDir}");
            return 0;
        }
        finally { Tools.TryDelete(work); }
    }

    static void AssertClean(string name, List<string> problems)
    {
        if (problems.Count > 0) throw new InvalidOperationException($"{name} : {string.Join(" ; ", problems)}");
    }
}
