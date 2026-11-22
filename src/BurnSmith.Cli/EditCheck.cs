using BurnSmith.Core;
using BurnSmith.Core.Menu;

namespace BurnSmith.Cli;

static class EditCheck
{
    public static int Run(bool noDvd, Func<List<int>?, int, int, string, Action<string>, List<int>> SubsToKeep)
    {
        var failed = 0;
        void Check(string what, bool ok) { if (!ok) failed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        string Show(IEnumerable<double> times) => string.Join(',', times);
        var src = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [], []);
        var whole = new DiscTitle("x.mkv");
        var cutBoth = new DiscTitle("x.mkv", Start: 100, End: 400);
        Check("whole file keeps 600 s", whole.Length(src) == 600 && whole.Cut(src) == src);
        var fiveTracks = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [.. Enumerable.Range(0, 5).Select(i => new AudioTrack(i, "ac3", 6, "fre", ""))], []);
        var fitted = Dvd.VideoBitrate([(fiveTracks, new DiscTitle("x.mkv", Audio: [0, 1, 2, 3, 4]))], 4.2);
        Check("DVD: five 5.1 tracks leave the picture what a 10.08 Mb/s stream allows, a single stereo one the usual 8 Mb/s",
              fitted > 1_000_000 && fitted + 5 * 448_000 <= 9_800_000 && Dvd.VideoBitrate([(src with { Audio = [new(0, "ac3", 2, "fre", "")] }, whole)], 4.2) == 8_000_000);
        Check("start only: 500 s", new DiscTitle("x", Start: 100).Cut(src).Duration == 500);
        Check("end only: 400 s", new DiscTitle("x", End: 400).Cut(src).Duration == 400);
        Check("both: 300 s", cutBoth.Cut(src).Duration == 300);
        Check("end past the file is the end of the file", new DiscTitle("x", Start: 100, End: 9999).Length(src) == 500);
        Check("empty cut is refused", Throws(() => new DiscTitle("x", Start: 700).Cut(src)) && Throws(() => new DiscTitle("x", Start: 100, End: 100.5).Cut(src)));
        Check("disc setting: one every 5 min from 0", Show(whole.ChapterList(600, 5)) == "0,300");
        Check("no chapters: only 0", Show(new DiscTitle("x", ChapterAt: []).ChapterList(600, 5)) == "0");
        var twoClips = new DiscTitle("x", Start: 100, End: 400, ChapterAt: [150, 250, 320], Clips: [new(100, 200), new(300, 400)]);
        Check("two clips 100-200 and 300-400 keep 200 s; a chapter in the gap is dropped, one after it moves back 100 s; a gap filter is made",
              twoClips.Length(src) == 200 && twoClips.Cut(src).Duration == 200 && Show(twoClips.ChapterList(200, 5)) == "0,50,120"
              && twoClips.OnDisc(150) == null && twoClips.OnDisc(250) == 150 && twoClips.GapFilter(true) == "aselect='not(gte(t,100)*lt(t,200))',asetpts=N/SR/TB"
              && Throws(() => new DiscTitle("x", Start: 100, End: 400, Clips: [new(100, 300), new(250, 400)]).Cut(src)));
        Check("custom times are shifted by the start, 0 added, outside dropped", Show(new DiscTitle("x", Start: 100, End: 400, ChapterAt: [50, 100, 160, 250.4, 399.5, 500]).ChapterList(300, 5)) == "0,60,150");
        // a trial disc: five minutes of each video, a passage at each chapter, so the menu keeps every chapter
        var hour = src with { Duration = 3600 };
        var trial = whole.Trial(hour, 5);
        Check("trial of an hour with a chapter every 5 min: 12 passages of 25 s, 300 s in all, the 12 chapters kept, one every 25 s",
              trial.Clips!.Count == 12 && trial.Clips.All(c => c.To - c.From == 25) && trial.Length(hour) == 300 && Show(trial.ChapterList(300, 5)) == string.Join(',', Enumerable.Range(0, 12).Select(i => i * 25)));
        var trialOwn = new DiscTitle("x", Start: 100, ChapterAt: [700, 1900]).Trial(hour, 5);
        Check("trial of a title cut at its start, with its own chapters: a passage of 100 s at each, counted from the cut",
              Show(trialOwn.Clips!.Select(c => c.From)) == "100,700,1900" && trialOwn.Length(hour) == 300 && Show(trialOwn.ChapterList(300, 5)) == "0,100,200");
        Check("trial: a video of five minutes or less stays whole; one already cut in clips keeps its first five minutes",
              whole.Trial(src with { Duration = 300 }, 5) == whole
              && new DiscTitle("x", Start: 0, End: 2000, Clips: [new(0, 200), new(1000, 2000)]).Trial(hour, 5) is { Clips: [{ From: 0, To: 200 }, { From: 1000, To: 1100 }], End: 1100 });
        Check("trial: the titles of a disc are cut only when a trial is asked", Project.OnDisc([whole], 5, 0, _ => hour)[0] == whole && Project.OnDisc([whole], 5, 300, _ => hour)[0].Length(hour) == 300);
        // a project file: everything the window holds, read back the same
        var written = new Project("dvd", [new DiscTitle("a.mkv", "Film", [0, 1], [0, 2], 10, 500, 120, [60, 120], [new ExternalSubtitle("a.fr.srt", "fre")], DefaultSubtitle: 2, TrackLanguage: new Dictionary<string, string> { ["a0"] = "fre" },
                                                  Effects: new VideoEffects(Brightness: 10), Clips: [new(10, 200), new(300, 500)], SubtitleDelay: new Dictionary<int, double> { [2] = -1.5 }), new DiscTitle("b.mkv", Bonus: true)],
                                new MenuDesign { Title = "Mon disque" }, true, "MON_DISQUE", 10, new EncodeOptions(FitDisc: true, DvdPal: true), new PlaybackOptions(PlayAll: true), new SubtitleStyle("Arial", 125, "FFE600", 20), "DVD-9", 7.6, @"D:\Disques\Film", true, 300);
        var reread = Project.Parse(written.ToJson());
        Check("a project is read back as it was written: videos, tracks, cut, chapters, subtitle files and delays, effects, menu, disc options",
              reread.ToJson() == written.ToJson() && reread is { Kind: "dvd", Titles: [{ Name: "Film", End: 500, Volume: 120 } first, { Bonus: true }], Design.Title: "Mon disque", Subtitles.Size: 125, CapacityGB: 7.6, Trial: 300 }
              && first.SubtitleDelay![2] == -1.5 && first.Clips!.Count == 2 && first.Effects!.Brightness == 10 && first.External![0].Lang == "fre" && Show(first.ChapterAt!) == "60,120");
        static bool NotProject(string text) { try { Project.Parse(text); return false; } catch (InvalidDataException) { return true; } }
        Check("what is not a project is refused: another JSON file, a text, a disc kind that does not exist",
              NotProject("{\"Name\":\"a menu template\"}") && NotProject("not json") && NotProject(new Project("vhs", []).ToJson()));
        // the project the window keeps by itself: in the app's own folder, written whole, a video gone still reads (the window leaves it out)
        var keptDir = Directory.CreateTempSubdirectory("burnsmith-check-autosave").FullName;
        try
        {
            var keptFile = Path.Combine(keptDir, "autosave" + Project.Extension);
            Project.WriteWhole(keptFile, new Project("bd", [new DiscTitle("a.mkv")]).ToJson());
            Project.WriteWhole(keptFile, written.ToJson());
            Check("the kept project: under the app's folder, never the user's; rewritten whole, no .tmp left; it reads back with its videos, gone or not",
                  Path.GetDirectoryName(Project.AutosaveFile()) == AppFiles.Root() && Project.AutosaveFile().EndsWith(Project.Extension)
                  && !File.Exists(keptFile + ".tmp") && Project.Load(keptFile).ToJson() == written.ToJson());
        }
        finally { Tools.TryDelete(keptDir); }
        // subtitles: the delay of a track and the look of the text ones
        var editDir = Directory.CreateTempSubdirectory("burnsmith-check-edit").FullName;
        try
        {
            var cue = Path.Combine(editDir, "s.srt");
            string Shifted(double delay)
            {
                File.WriteAllText(cue, "1\n00:00:10,000 --> 00:00:12,000\nBonjour\n");
                new DiscTitle("x", SubtitleDelay: new Dictionary<int, double> { [3] = delay }).Delay(new SubtitleTrack(3, "subrip", "fre", "", false), cue, 60);
                return File.ReadAllText(cue).Split('\n').FirstOrDefault(l => l.Contains("-->")) ?? "";
            }
            Check("a subtitle's delay moves its cues: 1.5 s later, 11 s earlier (cut at the start of the video), none for another track or without delay",
                  Shifted(1.5) == "00:00:11,500 --> 00:00:13,500" && Shifted(-11) == "00:00:00,000 --> 00:00:01,000" && Shifted(0) == "00:00:10,000 --> 00:00:12,000"
                  && new DiscTitle("x", SubtitleDelay: new Dictionary<int, double> { [1] = 5 }) is var other && Shifted(0).Length > 0 && other.SubtitleDelay![1] == 5);
            Check("the text subtitles of a Blu-ray keep their usual look, and take the disc's font, size, colour and height when set (half as big on a 540-line picture)",
                  BluRay.TextSubtitleLook(null, 1080) == "font-name=\"Calibri\", font-size=60, font-color=0xffffffff, bottom-offset=40"
                  && BluRay.TextSubtitleLook(new SubtitleStyle("Arial", 150, "ffe600", 50), 1080) == "font-name=\"Arial\", font-size=90, font-color=0xffffe600, bottom-offset=440"
                  && BluRay.TextSubtitleLook(new SubtitleStyle(Color: "rouge"), 540) == "font-name=\"Calibri\", font-size=30, font-color=0xffffffff, bottom-offset=20");
            Check("a DVD's subtitle palette takes the disc's colour for the text, its edge halfway to black; the usual one without", DvdSubtitles.Colors(new SubtitleStyle(Color: "FFE600")) is [0, 0x101010, 0xFFE600, 0x7F7300, ..] && DvdSubtitles.Colors(null).SequenceEqual(DvdSubtitles.Rgb));
            // the chapters a file holds are read with it
            var marked = Path.Combine(editDir, "chapters.mkv");
            var marks = Path.Combine(editDir, "chapters.txt");
            File.WriteAllText(marks, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=2000\ntitle=Un\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=2000\nEND=4500\ntitle=Deux\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=4500\nEND=6000\ntitle=Trois\n");
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=6", "-i", marks, "-map_metadata", "1", "-map_chapters", "1", "-c:v", "libx264", "-preset", "ultrafast", marked]);
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", "-c:v", "libx264", "-preset", "ultrafast", Path.Combine(editDir, "plain.mkv")]);
            Check("the chapters of a file are read with it (0, 2 and 4.5 s); a file without chapters has none", BluRay.Probe(marked).Chapters is [0, 2, 4.5] && BluRay.Probe(Path.Combine(editDir, "plain.mkv")).Chapters == null);
            Check("their names are read too, one for one; none for a file without chapters", BluRay.Probe(marked).ChapterNames is ["Un", "Deux", "Trois"] && BluRay.Probe(Path.Combine(editDir, "plain.mkv")).ChapterNames == null);
            // a TrueHD and its AC-3 core, as a disc carries them: 100 units of 6 bytes (40 samples each), frames of 448 kb/s (1792 bytes, 1536 samples)
            var (units, core, both) = (Path.Combine(editDir, "a.thd"), Path.Combine(editDir, "a.ac3"), Path.Combine(editDir, "a.hd.ac3"));
            File.WriteAllBytes(units, [.. Enumerable.Range(0, 100).SelectMany(i => new byte[] { 0xA0, 0x03, 0, 0, (byte)i, 0xEE })]);
            File.WriteAllBytes(core, [.. Enumerable.Range(0, 5).SelectMany(i => new byte[] { 0x0B, 0x77, (byte)i }.Concat(new byte[1789]))]);
            BluRay.InterleaveTrueHd(units, core, 448, both);
            var woven = File.ReadAllBytes(both);
            Check("a TrueHD kept: each AC-3 frame of its core, then the TrueHD of the same 1536 samples (39 units, then 38), every unit there and no frame past the sound's end",
                  woven.Length == 3 * 1792 + 600 && woven[2] == 0 && woven[1792 + 4] == 0 && woven[1792 + 39 * 6 + 2] == 1 && woven[2 * 1792 + 39 * 6 + 4] == 39
                  && woven[2 * 1792 + 77 * 6 + 2] == 2 && woven[^2] == 99);
        }
        finally { Tools.TryDelete(editDir); }
        {
            var named = new DiscTitle("x", ChapterAt: [0, 200, 400], ChapterTitles: ["Un", null, " Trois "]);
            var late = named with { Start = 100 };
            Check("chapter names follow their chapters onto the disc: one for one, a chapter without name left to its number, the name of a chapter cut off with the start not moved to another",
                  named.ChapterNames(600, 5) is ["Un", null, "Trois"] && late.ChapterNames(500, 5) is [null, null, "Trois"] && late.ChapterList(500, 5) is [0, 100, 300]
                  && new DiscTitle("x").ChapterNames(600, 5) is [null, null] && (named with { ChapterAt = null }).ChapterNames(600, 5).All(n => n == null));
            var namedMenu = new MenuContent(1, null, [null, null, null]) { ChapterNames = ["Prologue", null, "Final"] };
            var scenes = MenuBuilder.Build(new MenuDesign(), namedMenu).SelectMany(p => p.Ops.OfType<Label>()).Select(t => t.Value).ToList();
            Check("the menu writes a chapter's name in its box, \"Chapitre N\" where it has none", scenes.Contains("Prologue") && scenes.Contains("Final") && scenes.Contains("Chapter 2") && !scenes.Contains("Chapter 1")); // a bare design speaks English
            Check("a sound delay: silence put before the track, or its start cut off, when the sound is made again (a DVD); nothing without delay or for another track",
                  new DiscTitle("x", AudioDelay: new Dictionary<int, double> { [1] = 0.25, [2] = -0.5 }) is var moved && moved.AudioDelayFilter(1) == "adelay=250:all=1"
                  && moved.AudioDelayFilter(2) == "atrim=start=0.5,asetpts=PTS-STARTPTS" && moved.AudioDelayFilter(0) == "" && new DiscTitle("x").AudioDelayFilter(0) == "");
            var (hd, flac, dtsMa) = (new AudioTrack(0, "truehd", 8, "eng", "", Lossless: true, Bits: 24), new AudioTrack(1, "flac", 2, "fre", "", Lossless: true, Bits: 16), new AudioTrack(2, "dts", 6, "eng", "", Lossless: true));
            Check("« Son sans perte »: a TrueHD is kept, FLAC becomes LPCM, a TrueHD made louder or cut too (it is decoded), DTS-HD is copied as ever; without the option, AC-3 as before",
                  BluRay.BdAudio(hd, new DiscTitle("x"), false, true) == "truehd" && BluRay.BdAudio(flac, new DiscTitle("x"), false, true) == "lpcm"
                  && BluRay.BdAudio(hd, new DiscTitle("x", Volume: 120), false, true) == "lpcm" && BluRay.BdAudio(hd, new DiscTitle("x", Start: 60), false, true) == "lpcm"
                  && BluRay.BdAudio(dtsMa, new DiscTitle("x"), false) == "dts" && BluRay.BdAudio(dtsMa with { SampleRate = 96000 }, new DiscTitle("x"), false) == "dts"
                  && BluRay.BdAudio(hd, new DiscTitle("x"), false) == "truehd" && BluRay.BdAudio(flac, new DiscTitle("x"), false) == "lpcm"
                  && BluRay.BdAudio(hd, new DiscTitle("x"), false, lossless: false) == "ac3"
                  && BluRay.BdAudio(new AudioTrack(3, "aac", 2, "fre", ""), new DiscTitle("x"), false) == "lpcm");
            var ac3 = new AudioTrack(0, "ac3", 6, "eng", "");
            var louder = new DiscTitle("x", AudioVolume: new Dictionary<int, double> { [0] = -3, [1] = 6.5 });
            Check("a track's volume: a dB filter for that track only, and an AC-3 that was copied is made again (a TrueHD kept becomes LPCM); 0 dB changes nothing",
                  louder.AudioVolumeFilter(0) == "volume=-3dB" && louder.AudioVolumeFilter(1) == "volume=6.5dB" && louder.AudioVolumeFilter(2) == ""
                  && new DiscTitle("x", AudioVolume: new Dictionary<int, double> { [0] = 0 }).AudioVolumeFilter(0) == ""
                  && BluRay.BdAudio(hd, louder, false, true) == "lpcm" && BluRay.BdAudio(hd, new DiscTitle("x", AudioVolume: new Dictionary<int, double> { [1] = 3 }), false, true) == "truehd"
                  && BluRay.BdAudio(new AudioTrack(0, "dts", 6, "eng", ""), louder, false) == "lpcm" && BluRay.BdAudio(new AudioTrack(0, "dts", 6, "eng", ""), new DiscTitle("x"), false) == "dts");
            var dub = new AudioTrack(0, "eac3", 6, "fre", "doublage", File: @"C:\dub.eac3");
            var ddp = dub with { SampleRate = 48000 };
            Check("new projects preserve audio quality and Dolby Digital Plus, including defaults read from older JSON without these fields",
                  new EncodeOptions() is { Lossless: true, CopyEac3: true }
                  && Project.Parse("{\"Titles\":[],\"Encode\":{}}").Encode is { Lossless: true, CopyEac3: true }
                  && Project.Parse("{\"Titles\":[],\"Encode\":{\"Lossless\":false,\"CopyEac3\":false}}").Encode is { Lossless: false, CopyEac3: false });
            Check("Dolby Digital Plus is copied by default, and decoded to LPCM if copy is disabled or unavailable",
                  BluRay.BdAudio(ddp, new DiscTitle("x"), false) == "eac3"
                  && BluRay.BdAudio(ddp with { DiscEac3 = true }, new DiscTitle("x"), false) == "eac3"
                  && BluRay.BdAudio(ddp, new DiscTitle("x"), false, copyEac3: false) == "lpcm"
                  && BluRay.BdAudio(ddp, new DiscTitle("x"), false, lossless: false, copyEac3: false) == "ac3"
                  && BluRay.BdAudio(ddp, louder, false) == "lpcm"
                  && BluRay.BdAudio(ddp with { SampleRate = 44100 }, new DiscTitle("x"), false) == "lpcm");
            var withDub = new DiscTitle("x", Start: 10, End: 70, Audio: [1, 2], AudioFiles: [dub], TrackLanguage: new Dictionary<string, string> { ["a2"] = "ger" });
            var dubbed = withDub.Cut(new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [ac3, ac3 with { Index = 1 }], []));
            var mapArgs = new List<string> { "-ss", "10", "-t", "60", "-i", "x" };
            var maps = withDub.AudioMaps(withDub.KeptAudio(dubbed), mapArgs, 60);
            Check("a sound file added to a video: one more audio track numbered after the video's own, its language set like theirs, read as a second input cut like the video",
                  dubbed.Audio.Count == 3 && dubbed.Audio[2] is { Index: 2, File: @"C:\dub.eac3", Lang: "ger" } && withDub.KeptAudio(dubbed).Count == 2
                  && maps is ["0:a:1", "1:a:0"] && string.Join(' ', mapArgs) == @"-ss 10 -t 60 -i x -ss 10 -t 60 -i C:\dub.eac3"
                  && withDub.Files.Contains(@"C:\dub.eac3") && new DiscTitle("x").AudioMaps([ac3], [], 5) is ["0:a:0"]);
        }
        Check("a release file name gets a readable default name: cut at the first tag, an episode named by its number and title, its series for the season; a name with spaces kept",
              new DiscTitle("Title.2019.1080p.BluRay.x264-GRP.mkv").DisplayName == "Title 2019" && DiscTitle.Readable("Title.2019.1080p.BluRay.x264-GRP.mkv") == ("Title 2019", null)
              && DiscTitle.Readable("Cobra.Kai.S01E01.Le.roi.des.tares.MULTi.AD.1080p.NF.WEB.DDP.H265-THESYNDiCATE.mkv") == ("Épisode 1 · Le roi des tares", "Cobra Kai Saison 1")
              && DiscTitle.Readable("The_French_Village_S01E02_x264-GRP.mkv") == ("Épisode 2", "The French Village Saison 1")
              && DiscTitle.Readable("Mon film 1080p.mkv") == ("Mon film 1080p", null) && new DiscTitle("x.mkv", "Mine").DisplayName == "Mine");
        Check("ImgBurn gets the chosen speed as its list shows it (MAX, 4x), the drive letter and its log file",
              string.Join(" ", Iso.ImgBurnArgs(@"C:\a.iso", "f:", 4, @"C:\l.log")).Contains("/DEST F /SPEED 4x /LOG C:\\l.log /SRC C:\\a.iso")
              && Iso.ImgBurnArgs(@"C:\a.iso", "F:", 0, "l").Contains("MAX") && Iso.Speeds[0] == 0);
        Check("ImgBurn verifies and leaves the disc in by default; without verify and with eject it is told so; 2x is offered and written as its list shows it",
              string.Join(" ", Iso.ImgBurnArgs(@"C:\a.iso", "F:", 0, "l")).Contains("/VERIFY YES /EJECT NO")
              && string.Join(" ", Iso.ImgBurnArgs(@"C:\a.iso", "F:", 2, "l", verify: false, eject: true)).Contains("/VERIFY NO /EJECT YES /DEST F /SPEED 2x")
              && Iso.Speeds.Contains(2) && Iso.UsedRewritable("?") == null);
        var tracked = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [new(0, "dts", 6, "und", ""), new(1, "ac3", 2, "eng", "")], [new(0, "subrip", "und", "", false), new(1, "hdmv_pgs_subtitle", "fre", "", false)]);
        var relabel = new DiscTitle("x", Audio: [0, 1], Subtitles: [0, 1], External: [new ExternalSubtitle("s.srt", "ger")], TrackLanguage: new Dictionary<string, string> { ["a0"] = "fre", ["s0"] = "eng", ["s2"] = "spa" });
        var relabelled = relabel.Cut(tracked);
        Check("a language set by hand goes on the video's own audio and subtitle tracks, the others keep theirs", relabelled.Audio[0].Lang == "fre" && relabelled.Audio[1].Lang == "eng" && relabelled.Subtitles[0].Lang == "eng" && relabelled.Subtitles[1].Lang == "fre");
        Check("a subtitle file keeps its own language (its track number is not a key of the video's tracks)", relabelled.Subtitles[2].File != null && relabelled.Subtitles[2].Lang == "ger");
        var told = new List<string>();
        Check("--sub with --sub-file: four numbers for four files on a film with none of its own give four tracks, not eight, in silence",
              SubsToKeep([0, 1, 2, 3], 0, 4, "film", told.Add).SequenceEqual([0, 1, 2, 3]) && told.Count == 0);
        Check("--sub and --sub-file: its own tracks as asked, then the files, every file kept", SubsToKeep([1], 2, 2, "film", told.Add).SequenceEqual([1, 2, 3]) && SubsToKeep(null, 1, 1, "film", told.Add).SequenceEqual([1]) && told.Count == 0);
        Check("--sub with numbers no track has: told, with the numbers and the counts, and left out",
              SubsToKeep([0, 1, 2, 3], 0, 0, "film", told.Add).Count == 0 && told.Count == 1 && told[0].Contains("--sub 0,1,2,3") && told[0].Contains("0 of its own") && SubsToKeep([0, 5], 1, 1, "film", told.Add).SequenceEqual([0, 1]) && told.Count == 2 && told[1].Contains("--sub 5:"));
        Check("the disc reads the new language: kept tracks and their names", relabel.KeptAudio(relabelled)[0].Name.StartsWith("Français") && relabel.KeptSubtitles(relabelled)[0].Name == "English" && new DiscTitle("x").KeptAudio(tracked)[0].Lang == "und");
        Check("two tracks of one language keep the detail of their title apart, forced said once",
              new AudioTrack(2, "eac3", 6, "fre", "French (Audio-Description)").Name == "Français (Audio-Description) 5.1" && new AudioTrack(0, "eac3", 6, "fre", "French").Name == "Français 5.1"
              && new SubtitleTrack(0, "subrip", "fre", "French (Forced)", true).Name == "Français (forcés)" && new SubtitleTrack(1, "subrip", "fre", "French (Forced)", false).Name == "Français (forcés)"
              && new SubtitleTrack(2, "subrip", "eng", "English (SDH)", false).Name == "English (SDH)");
        Check("no language set: the source is returned as it is", new DiscTitle("x", TrackLanguage: new Dictionary<string, string>()).Cut(tracked) == tracked);
        Check("one video's own interval replaces the disc's; a list wins over it", Show(new DiscTitle("x", ChapterMinutes: 2).ChapterList(600, 5)) == "0,120,240,360,480" && Show(new DiscTitle("x", ChapterAt: [30], ChapterMinutes: 2).ChapterList(600, 5)) == "0,30" && Show(new DiscTitle("x").ChapterList(600, 5)) == "0,300");
        // the output folder: only what is nothing but an earlier disc is emptied; one foreign file and the whole folder is left alone
        var outRoot = Path.Combine(Path.GetTempPath(), "bs_out_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string OutFolder(string name, params string[] files)
            {
                var dir = Path.Combine(outRoot, name);
                Directory.CreateDirectory(dir);
                foreach (var entry in files)
                {
                    var path = Path.Combine(dir, entry);
                    if (entry.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "x");
                }
                return dir;
            }
            static bool Refused(string dir) { try { Tools.PrepareOutput(dir); return false; } catch (IOException) { return true; } }
            var mixed = OutFolder("mixed", "VIDEO_TS/VTS_01_1.VOB", "precieux.txt", "Films/autre.mkv");
            Check("a disc folder with anything else in it is refused, and every file in it survives",
                  Refused(mixed) && File.Exists(Path.Combine(mixed, "VIDEO_TS", "VTS_01_1.VOB")) && File.Exists(Path.Combine(mixed, "precieux.txt")) && File.Exists(Path.Combine(mixed, "Films", "autre.mkv")));
            var mixedBd = OutFolder("mixedbd", "BDMV/index.bdmv", "CERTIFICATE/id.bdmv", "notes.txt");
            Check("the same beside a BDMV folder", Refused(mixedBd) &&File.Exists(Path.Combine(mixedBd, "notes.txt")) && File.Exists(Path.Combine(mixedBd, "BDMV", "index.bdmv")));
            var oldDvd = OutFolder("olddvd", "VIDEO_TS/VIDEO_TS.IFO", "AUDIO_TS/");
            var oldBd = OutFolder("oldbd", "BDMV/index.bdmv", "CERTIFICATE/id.bdmv");
            Check("a real earlier DVD or Blu-ray folder is still emptied for the new build", !Refused(oldDvd) && !Refused(oldBd) && !Directory.Exists(oldDvd) && !Directory.Exists(oldBd));
            // a DVD backup used as the source of a disc built into its own folder: refused before anything is read or deleted, the backup intact
            var backup = OutFolder("backup", "VIDEO_TS/VTS_01_1.VOB");
            var vob = Path.Combine(backup, "VIDEO_TS", "VTS_01_1.VOB");
            static bool RefusedBuild(Action build) { try { build(); return false; } catch (IOException) { return true; } }
            Check("a source inside the output folder (a VIDEO_TS backup) is refused by both builds, before its folder is emptied",
                  RefusedBuild(() => Dvd.Build(new DvdJob([new DiscTitle(vob)], backup))) && RefusedBuild(() => BluRay.Build(new BluRayJob([new DiscTitle(vob)], backup))) && File.Exists(vob));
            var noDisc = OutFolder("nodisc", "precieux.txt");
            var empty = OutFolder("empty");
            Check("a folder without any disc is refused; an empty or missing one is fine", Refused(noDisc) &&File.Exists(Path.Combine(noDisc, "precieux.txt")));
        }
        finally { if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true); }
        // the structure check on a disc built by hand: a tiny Blu-ray folder, then the same with something wrong
        var discDir = Path.Combine(Path.GetTempPath(), "bs_disc_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var bdmv = Path.Combine(discDir, "BDMV");
            foreach (var sub in new[] { "BACKUP", "PLAYLIST", "CLIPINF", "STREAM" }) Directory.CreateDirectory(Path.Combine(bdmv, sub));
            var clpi = new BinaryWriterBE();
            clpi.Str("HDMV0200", 8); clpi.Zero(4); clpi.U32(16);
            clpi.U32(2); clpi.Zero(2); // empty ProgramInfo, this structure-only fixture declares no streams
            File.WriteAllBytes(Path.Combine(bdmv, "CLIPINF/00001.clpi"), clpi.ToArray());
            var index = BurnSmith.Core.Disc.MovieObjectFile.Index([0], 0, ushort.MaxValue, 6, 1);
            var objects = BurnSmith.Core.Disc.MovieObjectFile.Write([[BurnSmith.Core.Disc.Nav.PlayPlaylist(1)]]);
            foreach (var folder in new[] { bdmv, Path.Combine(bdmv, "BACKUP") })
            {
                File.WriteAllBytes(Path.Combine(folder, "index.bdmv"), index);
                File.WriteAllBytes(Path.Combine(folder, "MovieObject.bdmv"), objects);
            }
            var playlist = new BinaryWriterBE();
            playlist.Str("MPLS0200", 8); playlist.U32(40); playlist.U32(100); playlist.Zero(24);
            playlist.U32(56); playlist.U16(0); playlist.U16(1); playlist.U16(0);
            playlist.U16(48); playlist.Str("00001M2TS", 9); playlist.Zero(39);
            playlist.U32(2); playlist.U16(0);
            File.WriteAllBytes(Path.Combine(bdmv, "PLAYLIST", "00001.mpls"), playlist.ToArray());
            var packets = new byte[192 * 50];
            for (var k = 0; k < 50; k++) packets[192 * k + 4] = 0x47;
            var stream = Path.Combine(bdmv, "STREAM", "00001.m2ts");
            File.WriteAllBytes(stream, packets);
            var good = DiscCheck.Folder(discDir);
            File.WriteAllBytes(stream, packets[..^1]);
            var shortStream = DiscCheck.Folder(discDir);
            File.WriteAllBytes(stream, packets);
            packets[192 * 7 + 4] = 0;
            File.WriteAllBytes(stream, packets);
            var noSync = DiscCheck.Folder(discDir);
            File.Delete(Path.Combine(bdmv, "CLIPINF", "00001.clpi"));
            File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", "MovieObject.bdmv"), [1]);
            var broken = DiscCheck.Folder(discDir);
            var iso = Path.Combine(discDir, "t.iso");
            var emptyDisc = Directory.CreateDirectory(Path.Combine(discDir, "empty")).FullName;
            BurnSmith.Core.Disc.UdfImage.Write(emptyDisc, iso, "CHECK");
            var image = File.ReadAllBytes(iso);
            var goodImage = DiscCheck.Image(iso);
            File.WriteAllBytes(iso, image[..^2048]);
            var cutImage = DiscCheck.Image(iso);
            File.WriteAllBytes(iso, image[..^7]);
            var oddImage = DiscCheck.Image(iso);
            Check("disc check: a tidy Blu-ray folder has no problem; a cut stream, a lost sync byte, a missing clip info and a different backup are each found; same for an image (whole sectors, UDF anchors)",
                  good.Count == 0 && shortStream.Count == 1 && shortStream[0].Contains("paquets entiers") && noSync.Count == 1 && noSync[0].Contains("synchronisation")
                  && broken.Count >= 2 && broken.Any(p => p.Contains("CLIPINF")) && broken.Any(p => p.Contains("BACKUP"))
                  && goodImage.Count == 0 && cutImage.Count == 1 && cutImage[0].Contains("tronquée") && oddImage.Any(p => p.Contains("secteurs")) && DiscCheck.Folder(Path.Combine(discDir, "nothing")).Count == 1);
        }
        finally { Directory.Delete(discDir, true); }
        // a BD-J menu disc: a movie object added after the film's (which keep their numbers) for First Playback; the Top Menu is the BD-J menu, title N plays video N, the last title is the menu
        var films = BurnSmith.Core.Disc.MovieObjectFile.Write([[BurnSmith.Core.Disc.Nav.PlayPlaylist(1)], [BurnSmith.Core.Disc.Nav.PlayPlaylist(2)]]);
        var withIntro = Bdmv.AppendObject(films, out var introObject, BurnSmith.Core.Disc.Nav.PlayPlaylist(98), BurnSmith.Core.Disc.Nav.JumpTitle(1));
        var plain = BurnSmith.Core.Disc.MovieObjectFile.Index([0, 1], 2, 3, 6, 1);
        var started = Bdmv.PatchIndex(plain, firstPlay: 3, videos: 2);
        var tableAt = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(started.AsSpan(8)) + 4;
        ushort At(int k) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(started.AsSpan(tableAt + k));
        Check("a BD-J menu disc: an object is added after the film's 0 and 1 (number 2, they keep their commands); First Playback runs HDMV object 3, the Top Menu and title 3 are BD-J object 00000, titles 1 and 2 BD-J object 00001",
              introObject == 2 && System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(withIntro.AsSpan(48)) == 3 && withIntro.Length == films.Length + 4 + 24
              && Bdmv.PrependToFilm(withIntro, BurnSmith.Core.Disc.Nav.SelectStreams(subtitle: 1)).Length == withIntro.Length + 12
              && started[tableAt] >> 6 == 1 && At(6) == 3 && At(24) == 3
              && string.Join(",", new[] { 12, 26, 38, 50 }.Select(o => $"{started[tableAt + o] >> 6}{started[tableAt + o + 4] >> 6}{System.Text.Encoding.ASCII.GetString(started, tableAt + o + 6, 5)}")) == "2300000,2200001,2200001,2200000"
              && started.AsSpan(0, 8).SequenceEqual("INDX0200"u8));
        var oldMenu = "page root bg.jpg\nbutton root play 0 0 9 9 - - - - - playall 1 7\nbutton b b1 0 0 9 9 - - - - - play 8\n"u8.ToArray();
        var chained = System.Text.Encoding.UTF8.GetString(BluRay.ChainEpisodes(oldMenu));
        Check("repair: an old menu with no play mode gets the default one over the episodes of its play-all button (7, the bonus 8 left alone); one that has a mode, or no play-all button, is kept",
              chained.StartsWith("playback menu 1 1 1 7\npage root", StringComparison.Ordinal) && BluRay.ChainEpisodes(BluRay.ChainEpisodes(oldMenu)).Length == chained.Length
              && BluRay.ChainEpisodes("page root bg.jpg\n"u8.ToArray()).Length == 17);
        var dts = new AudioTrack(0, "dts", 6, "", "");
        Check("audio copied only while it stays in step with the picture: a DTS track is kept when the title starts at 0, becomes AC-3 with a later start or another volume, and stays on a 4K disc",
              BluRay.BdAudio(dts, new DiscTitle("x"), false) == "dts" && BluRay.BdAudio(dts, new DiscTitle("x", Start: 90), false) == "lpcm" && BluRay.BdAudio(dts, new DiscTitle("x", Volume: 120), false) == "lpcm"
              && BluRay.BdAudio(dts, new DiscTitle("x", Start: 90), true) == "dts" && BluRay.BdAudio(dts, new DiscTitle("x", End: 500), false) == "dts");
        Check("photos and songs are told from videos by their extension", MediaMaker.IsPhoto("a.JPG") && MediaMaker.IsPhoto("b.png") && !MediaMaker.IsPhoto("c.mkv") && MediaMaker.IsSong("d.FLAC") && !MediaMaker.IsSong("e.mkv") && !MediaMaker.IsSong("f.jpg"));
        // the slideshow and the song video, made for real when ffmpeg is there: one frame per 1/25 s for every photo (a png and a jpg of different sizes), the cover or black, the length of the song
        var mediaDir = Path.Combine(Path.GetTempPath(), "bs_media_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var ffmpeg = Tools.Find("ffmpeg.exe");
            Directory.CreateDirectory(mediaDir);
            string F(string name) => Path.Combine(mediaDir, name);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=800x600:d=1", "-frames:v", "1", F("a.png")]);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "smptebars=s=1280x720:d=1", "-frames:v", "1", F("b.jpg")]);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "sine=f=440:d=3", "-metadata", "title=Premier", F("s.mp3")]);
            int Frames(string file) => int.Parse(Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-count_frames", "-select_streams", "v", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", file]).Trim());
            MediaMaker.Slideshow([F("a.png"), F("b.jpg")], 2, [F("s.mp3")], F("show.mkv"));
            var show = BluRay.Probe(F("show.mkv"));
            MediaMaker.Song(F("s.mp3"), F("b.jpg"), F("song.mkv"));
            var song = BluRay.Probe(F("song.mkv"));
            Check("slideshow: 2 photos of 2 s = 100 frames at 1920x1080 25 fps with AC-3 music; the working folder is gone; a song is 3 s with its title",
                  Frames(F("show.mkv")) == 100 && show is { Width: 1920, Height: 1080, AudioCodec: "ac3" } && Math.Abs(show.Duration - 4) < 0.2 && Math.Abs(show.Fps - 25) < 0.01
                  && !Directory.Exists(F("show.mkv.photos")) && Math.Abs(song.Duration - 3) < 0.2 && Frames(F("song.mkv")) == 75 && MediaMaker.Describe(F("s.mp3")).Title == "Premier");
            // an mp3 then a flac under 5 s of photos: the sound must last as long as the picture (a concat list of two codecs silently stops after the first)
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "sine=f=660:d=2", F("t.flac")]);
            MediaMaker.Slideshow([F("a.png"), F("b.jpg")], 2.5, [F("s.mp3"), F("t.flac")], F("mixed.mkv"));
            var lastSound = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-select_streams", "a", "-show_entries", "packet=pts_time", "-of", "csv=p=0", F("mixed.mkv")]).Trim().Split('\n').Last().Trim().TrimEnd(','); // ffmpeg 9 ends the last packet's line with a comma
            Check("a slideshow's music of two formats (mp3 then flac) plays until the end of the 5 s of photos", double.Parse(lastSound, System.Globalization.CultureInfo.InvariantCulture) > 4.9);
            MediaMaker.Slideshow([F("a.png"), F("b.jpg"), F("a.png")], 2, [], F("faded.mkv"), transition: "mix");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-ss", "2.33", "-i", F("faded.mkv"), "-frames:v", "1", "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "gray", F("mid.raw")]);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-ss", "1.0", "-i", F("faded.mkv"), "-frames:v", "1", "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "gray", F("first.raw")]);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-ss", "3.0", "-i", F("faded.mkv"), "-frames:v", "1", "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "gray", F("second.raw")]);
            int Grey(string f) => File.ReadAllBytes(F(f))[0];
            Check("slideshow transitions: 3 photos of 2 s keep 6 s (150 frames); a frame within the first join is neither photo; French names map to xfade, aucune to none",
                  Frames(F("faded.mkv")) == 150 && Grey("mid.raw") != Grey("first.raw") && Grey("mid.raw") != Grey("second.raw")
                  && MediaMaker.TransitionOf("Volet") == "wipeleft" && MediaMaker.TransitionOf("aucune") == null && MediaMaker.TransitionOf("n'importe") == "fade"
                  && !File.Exists(F("faded.mkv.graph.txt")));
            MediaMaker.Join([F("show.mkv"), F("show.mkv")], F("twice.mkv"));
            Check("two videos with the same streams join into one of the sum of both; a different size or track list does not match",
                  Frames(F("twice.mkv")) == 200 && Math.Abs(BluRay.Probe(F("twice.mkv")).Duration - 8) < 0.2 && MediaMaker.SameLayout(show, show)
                  && !MediaMaker.SameLayout(show, show with { Width = 1280, Height = 720 }) && !MediaMaker.SameLayout(show, show with { Audio = [] }) && !MediaMaker.SameLayout(show, show with { Fps = 24 }));
            // same size and codec but another H.264 profile and frame reordering: a stream copy keeps only the first file's parameter sets, so the second part could not decode on a player
            foreach (var (name, settings) in new[] { ("x.mkv", new[] { "-profile:v", "main", "-bf", "0" }), ("y.mkv", new[] { "-profile:v", "high", "-bf", "3" }) })
                Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=640x360:d=1", "-pix_fmt", "yuv420p", "-c:v", "libx264", .. settings, F(name)]);
            Check("two clips of one size and codec but another H.264 profile or reordering are not joined; the same clip is",
                  !MediaMaker.SameLayout(BluRay.Probe(F("x.mkv")), BluRay.Probe(F("y.mkv"))) && MediaMaker.SameLayout(BluRay.Probe(F("x.mkv")), BluRay.Probe(F("x.mkv"))));
            // the video editor's effects as one ffmpeg graph: turned, cropped, coloured, a watermark and a burnt subtitle from names with a space, an apostrophe, a comma, brackets and a semicolon (each special to ffmpeg's filter graph)
            File.Copy(F("b.jpg"), F("l'b.jpg"), true);
            Directory.CreateDirectory(F("d'été, [1]; b"));
            File.WriteAllText(F("d'été, [1]; b/s.srt"), "1\r\n00:00:00,000 --> 00:00:10,000\r\nBonjour\r\n");
            var fx = new VideoEffects(Rotate: 90, CropLeft: 40, CropRight: 60, CropTop: 10, Brightness: 20, WhiteBalance: 30, Look: "Sépia",
                                      Watermark: F("l'b.jpg"), WatermarkWidth: 0.25, WatermarkOpacity: 50, BurnSubtitle: F("d'été, [1]; b/s.srt"), BurnDelay: 2);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-i", F("a.png"), "-vf", fx.Vf(800, 600, 3, "null"), F("fx.png")]);
            var fxOut = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", F("fx.png")]).Trim();
            Check("video effects: rotation, crop, colour, watermark and a burnt subtitle make one graph ffmpeg runs; the picture is 600x800 less the crop",
                  fx.Size(800, 600) == (500, 790) && fxOut == "500,790" && new VideoEffects().IsEmpty && !fx.IsEmpty
                  && new VideoEffects().Take(fx, "crop") is { Rotate: 90, CropLeft: 40, Brightness: 0 });
            // the same graph on a real video cut like a title (-ss before -i), fitted to the disc frame as Blu-ray.Prepare does: every frame comes out
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-ss", "1", "-t", "2", "-i", F("show.mkv"), "-vf", fx.Vf(1920, 1080, 1, "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1"),
                "-r", "25", "-c:v", "libx264", "-preset", "ultrafast", "-an", F("fx.mkv")]);
            Check("video effects on a cut video: 2 s give 50 frames of 1920x1080", Frames(F("fx.mkv")) == 50 && BluRay.Probe(F("fx.mkv")) is { Width: 1920, Height: 1080 });
            // two clips of 1 s (0-1 and 3-4 of the 4 s slideshow) as Blu-ray.Prepare cuts them: 50 frames, 2 s of sound, a subtitle in the gap dropped and one after it moved back
            var clipped = new DiscTitle(F("show.mkv"), Start: 0, End: 4, Clips: [new(0, 1), new(3, 4)]);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-t", "4", "-i", F("show.mkv"), "-vf", "null," + clipped.GapFilter(false), "-af", clipped.GapFilter(true), "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "ac3", F("clips.mkv")]);
            File.WriteAllText(F("clips.srt"), "1\n00:00:00,200 --> 00:00:00,800\nA\n\n2\n00:00:01,500 --> 00:00:02,000\nB\n\n3\n00:00:03,500 --> 00:00:03,900\nC\n");
            clipped.CloseGaps(F("clips.srt"), 4);
            var clipSound = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-select_streams", "a", "-show_entries", "format=duration", "-of", "csv=p=0", F("clips.mkv")]).Trim();
            Check($"two clips of a video join into 2 s: 50 frames, the sound as long ({clipSound} s), the subtitles closed up",
                  Frames(F("clips.mkv")) == 50 && Math.Abs(double.Parse(clipSound, System.Globalization.CultureInfo.InvariantCulture) - 2) < 0.15
                  && File.ReadAllText(F("clips.srt")).Replace("\r", "") == "1\n00:00:00,200 --> 00:00:00,800\nA\n\n2\n00:00:01,500 --> 00:00:01,900\nC\n\n");
            // a video's own subtitle read whole and cut at 3 s: the cue at 3.5 s lands at 0.5 s (the input -ss this replaces put a real episode's cues 0.74 s late; this small file does not show it)
            File.WriteAllText(F("own.srt"), "1\n00:00:03,500 --> 00:00:04,000\nX\n");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc=s=320x240:d=5:r=25", "-i", F("own.srt"), "-c:v", "libx264", "-g", "50", "-c:s", "srt", F("own.mkv")]);
            BluRay.WriteOwnSubtitles(new DiscTitle(F("own.mkv"), Start: 3), [(new SubtitleTrack(0, "subrip", "fre", "", false), F("own_cut.srt"))], 2, _ => { });
            Check("a video's own subtitle is cut at the exact start", File.ReadAllText(F("own_cut.srt")).Contains("00:00:00,500 --> 00:00:01,000"));
            // a camcorder's 702x574 at 16:15 is 748 wide as seen: fitted into 1920x1080 it is 1408 wide (1080 * 748 / 574), not 1320 (the stored shape, squeezed)
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "color=white:s=702x574:d=1,setsar=16/15", "-frames:v", "1", "-vf", BluRay.Fit(1920, 1080) + ",crop=1920:1:0:540,format=gray", "-f", "rawvideo", F("row.raw")]);
            var seen = File.ReadAllBytes(F("row.raw")).Count(b => b > 128); // the white columns of the middle line
            Check($"non-square pixels are made square before the Blu-ray fit: a 702x574 16:15 picture is about 1408 wide on the disc (here {seen})", Math.Abs(seen - 1408) <= 4);
        }
        catch (FileNotFoundException e) { Check($"slideshow and song videos: {e.Message}", false); }
        finally { if (Directory.Exists(mediaDir)) Directory.Delete(mediaDir, true); }
        // a DVD with a start video, made for real with ffmpeg: the first-play command jumps to the title after the film, with and without a menu
        var introDir = Path.Combine(Path.GetTempPath(), "bs_dvdintro_" + Guid.NewGuid().ToString("N")[..8]);
        if (noDvd) Console.WriteLine("skip a DVD's start video, the Settings page after it, an NTSC start video on a PAL film (--no-dvd)");
        else try
        {
            var ffmpeg = Tools.Find("ffmpeg.exe");
            Directory.CreateDirectory(introDir);
            foreach (var (name, source) in new[] { ("film.mkv", "testsrc2=s=320x240:d=2"), ("intro.mkv", "smptebars=s=320x240:d=2") })
                Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", source, "-f", "lavfi", "-i", "sine=f=440:d=2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-c:a", "ac3", Path.Combine(introDir, name)]);
            (int Titles, string LastFirstPlayCommand) FirstPlay(string disc)
            {
                var d = File.ReadAllBytes(Path.Combine(disc, "VIDEO_TS", "VIDEO_TS.IFO"));
                int U32(int o) => (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
                int U16(int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
                var (fp, titleTable) = (U32(0x84), U32(0xC4) * 2048);
                var commands = fp + U16(fp + 0xE4); // the first-play chain's command table: counts, then 8 bytes a command
                var last = commands + 8 + 8 * (U16(commands) - 1);
                return (U16(titleTable), Convert.ToHexString(d.AsSpan(last, 8)));
            }
            var titles2 = new[] { new DiscTitle(Path.Combine(introDir, "film.mkv")), new DiscTitle(Path.Combine(introDir, "intro.mkv"), Intro: true) };
            Dvd.Build(new DvdJob(titles2, Path.Combine(introDir, "plain"), null));
            var noMenu = FirstPlay(Path.Combine(introDir, "plain"));
            Dvd.Build(new DvdJob(titles2, Path.Combine(introDir, "menu"), TemplateCatalog.Default().Clone()));
            var withMenu = FirstPlay(Path.Combine(introDir, "menu"));
            Check("a DVD's start video is the title after the film, which the first-play command jumps to (title 2 of 2), with a menu or without; the discs pass the structure check",
                  noMenu == (2, "3002000000020000") && withMenu == (2, "3002000000020000")
                  && DiscCheck.Folder(Path.Combine(introDir, "plain")).Count == 0 && DiscCheck.Folder(Path.Combine(introDir, "menu")).Count == 0);
    
            // what was only ever looked at in VLC, on one disc: an NTSC start video before a PAL film with two audio tracks, a default subtitle and a menu; read back from the IFO and the VOB
            var mixedDisc = Path.Combine(introDir, "mixed");
            var (palFilm, ntscIntro, srt) = (Path.Combine(introDir, "pal.mkv"), Path.Combine(introDir, "ntsc.mkv"), Path.Combine(introDir, "pal.srt"));
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", "-f", "lavfi", "-i", "sine=f=440:d=2", "-f", "lavfi", "-i", "sine=f=880:d=2", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-c:a", "ac3", palFilm]);
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "smptebars=s=320x240:r=30000/1001:d=2", "-f", "lavfi", "-i", "sine=f=220:d=2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-c:a", "ac3", ntscIntro]);
            File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nBonjour\n");
            Dvd.Build(new DvdJob([new DiscTitle(palFilm, Audio: [0, 1], Subtitles: [0], External: [new ExternalSubtitle(srt, "fre")], DefaultSubtitle: 0), new DiscTitle(ntscIntro, Intro: true)], mixedDisc, new MenuDesign(), Playback: new PlaybackOptions(PlayAll: false)));
            var vmg = File.ReadAllBytes(Path.Combine(mixedDisc, "VIDEO_TS", "VIDEO_TS.IFO"));
            var vts = File.ReadAllBytes(Path.Combine(mixedDisc, "VIDEO_TS", "VTS_01_0.IFO"));
            static int U16(byte[] d, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
            static int U32(byte[] d, int o) => (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
            static int Chain(byte[] d, int table, int number) => table + U32(d, table + 8 * number + 4); // a table of program chains: 8 bytes of head, then 8 a chain
            static string Hex(byte[] d, int o) => Convert.ToHexString(d.AsSpan(o, 8));
            static string Commands(byte[] d, int pgc, int first, int count) => string.Join(' ', Enumerable.Range(first, count).Select(i => Hex(d, pgc + U16(d, pgc + 0xE4) + 8 + 8 * i)));
            static string Pre(byte[] d, int pgc) => Commands(d, pgc, 0, U16(d, pgc + U16(d, pgc + 0xE4)));
            static string Post(byte[] d, int pgc) => Commands(d, pgc, U16(d, pgc + U16(d, pgc + 0xE4)), U16(d, pgc + U16(d, pgc + 0xE4) + 2));
            static int Sector(byte[] d, int pgc, int field) => U32(d, pgc + U16(d, pgc + 0xE8) + field); // of its first cell: 8 = first sector, 20 = last
            var titleChains = U32(vts, 0xCC) * 2048;
            var menuChains = U32(vts, 0xD0) * 2048 + U32(vts, U32(vts, 0xD0) * 2048 + 12); // the first language's
            var (filmPgc, introPgc) = (Chain(vts, titleChains, 1), Chain(vts, titleChains, 2));
            var pages = Enumerable.Range(1, U16(vts, menuChains)).Select(i => Chain(vts, menuChains, i)).Where(p => vts[p + 3] > 0).ToList(); // only the chains with a cell
            var menuVob = File.ReadAllBytes(Path.Combine(mixedDisc, "VIDEO_TS", "VTS_01_0.VOB"));
            var rootPci = Sector(vts, pages[0], 8) * 2048 + 0x2D; // its first navigation pack holds the buttons
            var rootButtons = Enumerable.Range(0, menuVob[rootPci + 0x71]).Select(i => Hex(menuVob, rootPci + 0x8E + 18 * i + 10)).ToList();
    
            Check("Settings after a start video: first play sets audio 1 and the default subtitle (g2, g3), and only then jumps to the start video, title 2",
                  Pre(vmg, U32(vmg, 0x84)) == "7100000100000000 7100000200010000 7100000300010000 7100000404000000 7100000500000000 3002000000020000");
            Check("Settings after a start video: its end shows the default subtitle (stream 0), then calls the menu just as the film's end does",
                  Post(vts, introPgc) == $"51000000C0000000 {Post(vts, filmPgc)}");
            Check("Settings after a start video: the root's button computes g6 = 64 g2 + g3 and picks one of the 4 settings pages, the disc's first state (65) being page 3",
                  pages.Count == 5 && rootButtons.Contains("7101000F0002000D")
                  && Post(vts, pages[0]).Contains("00A1000E00020005 3001000000000000 6100000600020000 7500000600400000 6300000600030000 20A4000600400002 20A4000600410003 20A4000600800004 20A4000600810005 2004000000000002"));
            Check("Settings after a start video: page 3 is the page of audio 1 and subtitle 1: choosing either stays on it, subtitle off goes to page 2",
                  Post(vts, pages[2]).Contains("7100000200010000 5104008000000003") && Post(vts, pages[2]).Contains("7100000300010000 51040000C0000003") && Post(vts, pages[2]).Contains("7100000300000000 51040000BE000002"));
    
            Check("Settings, one page for each pair: choosing subtitle 1 (the 4th button: audio 1, 2, off, 1) leaves its rank for the page it stays on, whose pre takes it back, else the first button",
                  Post(vts, pages[2]).Contains("7100000410000000 7100000500010000 7100000300010000 51040000C0000003") && Pre(vts, pages[2]) == "00B1000500010005 4600000000040000 7100000500000000 0001000000000006 5600000004000000 0000000000000000");
    
            var slice = Path.Combine(introDir, "intro-on-disc.vob");
            using (var titlesVob = File.OpenRead(Path.Combine(mixedDisc, "VIDEO_TS", "VTS_01_1.VOB")))
            {
                var bytes = new byte[(Sector(vts, introPgc, 20) - Sector(vts, introPgc, 8) + 1) * 2048];
                titlesVob.Position = Sector(vts, introPgc, 8) * 2048L;
                titlesVob.ReadExactly(bytes);
                File.WriteAllBytes(slice, bytes);
            }
            var probed = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-count_frames", "-show_entries", "stream=codec_type,width,height,r_frame_rate,nb_read_frames,sample_rate", "-of", "json", slice]);
            var streams = System.Text.Json.JsonDocument.Parse(probed).RootElement.GetProperty("streams").EnumerateArray().ToList();
            var picture = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
            var frames = int.Parse(picture.GetProperty("nb_read_frames").GetString()!);
            Check("an NTSC start video on a PAL film: the source really is 29.97 fps", Math.Abs(BluRay.Probe(ntscIntro).Fps - 29.97) < 0.01);
            Check("an NTSC start video on a PAL film: the disc's titles and menus are PAL (bits 4-5 of their video attributes)", (vts[0x200] >> 4 & 3) == 1 && (vts[0x100] >> 4 & 3) == 1);
            Check("an NTSC start video on a PAL film: its chain and the film's say 2 s at 25 fps", vts[introPgc + 6] == 2 && vts[introPgc + 7] >> 6 == 1 && vts[filmPgc + 6] == 2 && vts[filmPgc + 7] >> 6 == 1);
            Check($"an NTSC start video on a PAL film: its sectors on the disc are 720x576 at 25 fps, 2 s long ({frames} frames), with 48 kHz sound",
                  picture.GetProperty("width").GetInt32() == 720 && picture.GetProperty("height").GetInt32() == 576 && picture.GetProperty("r_frame_rate").GetString() == "25/1" && frames is >= 48 and <= 52
                  && streams.Any(s => s.GetProperty("codec_type").GetString() == "audio" && s.GetProperty("sample_rate").GetString() == "48000"));
            Check("that disc passes the structure check", DiscCheck.Folder(mixedDisc).Count == 0);
        }
        catch (FileNotFoundException e) { Check($"a DVD's start video: {e.Message}", false); }
        catch (InvalidOperationException e) { Check($"a DVD's start video: {e.Message.Split('\n')[0]}", false); } // a tool that failed: this disc cannot be made, which is a finding, not a crash of the check
        finally { if (Directory.Exists(introDir)) Directory.Delete(introDir, true); }
        SettingsCheck.Disc(Check, noDvd);
        Check("custom times in whole seconds, unique", Show(new DiscTitle("x", ChapterAt: [10.2, 10.4, 5]).ChapterList(600, 5)) == "0,5,10");
        var dir = Path.Combine(Path.GetTempPath(), "bs_extsub_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in new[] { "v.mkv", "v.srt", "v.fr.srt", "v.English.forced.ass", "v10.srt", "w.srt", "v.txt", "Dr.No.1962.en.srt", "film.fr.forced.srt", "Do It Now.srt", "2_English.srt" }) File.WriteAllText(Path.Combine(dir, f), "");
            var near = ExternalSubtitle.Near(Path.Combine(dir, "v.mkv"));
            Check("sidecar files: name, language, forced, other videos' files left alone", string.Join('|', near.Select(n => $"{Path.GetFileName(n.File)}={n.Lang}{(n.Forced ? "!" : "")}")) == "v.English.forced.ass=eng!|v.fr.srt=fre|v.srt=und");
            string Lang(string f, string video = "v.mkv") { var e = ExternalSubtitle.For(Path.Combine(dir, video), Path.Combine(dir, f)); return e.Lang + (e.Forced ? "!" : ""); }
            Check("language at the end of the name: Dr.No.1962.en = English not Norwegian, forced flag, a title with a language word has none, 2_English", Lang("Dr.No.1962.en.srt") == "eng" && Lang("film.fr.forced.srt") == "fre!" && Lang("Do It Now.srt") == "und" && Lang("2_English.srt") == "eng");
            Check("a file named after the video is read from what follows its name", Lang("v.fr.srt") == "fre" && Lang("v.English.forced.ass") == "eng!" && Lang("v.srt") == "und");
            Check("language typed by hand", ExternalSubtitle.Parse("ger=" + Path.Combine(dir, "v.fr.srt")).Lang == "ger" && ExternalSubtitle.Parse(Path.Combine(dir, "v.fr.srt")).Lang == "fre");
        }
        finally { Directory.Delete(dir, true); }
        var two = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [], [new(0, "subrip", "eng", "", false), new(1, "hdmv_pgs_subtitle", "fre", "", false)]);
        var withFiles = new DiscTitle("x", Start: 100, External: [new("a.fr.srt", "fre"), new("b.ass")]).Cut(two);
        Check("subtitle files come after the video's tracks, numbered on, the cut keeps them", withFiles.Subtitles.Select(t => $"{t.Index}{(t.File != null ? t.Codec : "")}").SequenceEqual(["0", "1", "2subrip", "3ass"]) && withFiles.Duration == 500);
        // a season whose episodes have not all the same tracks (Cobra Kai: some lack "English (Forced)"): the menu selects a subtitle by number, for every episode
        var e1 = two with { Subtitles = [new(0, "subrip", "fre", "French", false), new(1, "subrip", "eng", "English (Forced)", false), new(2, "subrip", "eng", "English (SDH)", false)] };
        var e2 = two with { Subtitles = [new(0, "subrip", "fre", "French", false), new(1, "subrip", "eng", "English (SDH)", false)] };
        static string Line(IEnumerable<SubtitleTrack> line) => string.Join(",", line.Select(t => t.Index));
        var byNumber = DiscTitle.LineUpSubtitles([new DiscTitle("e1", Subtitles: [0, 2]), new DiscTitle("e2", Subtitles: [0])], [e1, e2]); // copied by number: e2 has no track 2
        var shifted = DiscTitle.LineUpSubtitles([new DiscTitle("e1", Subtitles: [0, 1]), new DiscTitle("e2", Subtitles: [0, 1])], [e1, e2]); // e2's track 1 is the SDH one
        Check("a season's subtitles line up on the first episode by name: found at another number, a missing one keeps its place (as the first's), an extra comes after",
              Line(byNumber[0]) == "0,2" && Line(byNumber[1]) == "0,1" && Line(shifted[0]) == "0,1" && Line(shifted[1]) == "0,-1,1" && shifted[1][1].Name == "English (forcés)");
        var blankSup = PgsSubtitles.Blank(1920, 1080);
        Check("the invisible track that keeps a place: one picture on the disc's plane, nothing opaque, so a DVD makes a subpicture of it too",
              PgsSubtitles.Cues(blankSup, out var blankBad) is [{ } blank] && blankBad == 0 && blank.Alpha.All(a => a == 0) && PgsSubtitles.PlaneSize(blankSup) == (1920, 1080));
        var cues = "1\r\n00:00:01,000 --> 00:00:03,000\r\nbefore\r\n\r\n2\r\n00:00:05,000 --> 00:00:08,500\r\nstraddles\r\nthe start\r\n\r\n3\r\n00:00:12,000 --> 00:00:14,000\r\nkept\r\n\r\n4\r\n00:00:17,000 --> 00:00:19,000\r\nafter\r\n\r\n";
        Check("cut cues: before dropped, straddling cue shortened and moved, later ones dropped, renumbered",
              ExternalSubtitle.Cut(cues, 6, 8) == "1\n00:00:00,000 --> 00:00:02,500\nstraddles\nthe start\n\n2\n00:00:06,000 --> 00:00:08,000\nkept\n\n" && ExternalSubtitle.Cut(cues, 9, 2) == "" && ExternalSubtitle.Cut(cues, 0, 100).Contains("4\n00:00:17,000 --> 00:00:19,000\nafter"));
        // a PGS stream built by hand: 8x4 picture (1 = white, 2 = black, 0 = nothing), shown at 1 s and 5 s, cleared at 3 s and 8 s
        static byte[] Segment(byte kind, double seconds, params byte[] body) => BurnSmith.Core.Disc.PgsStream.Segment(kind, (uint)(seconds * 90000), (uint)(seconds * 90000), body);
        static byte[] Be(int value, int bytes) => [.. Enumerable.Range(0, bytes).Select(i => (byte)(value >> 8 * (bytes - 1 - i)))];
        byte[] SupSet(double at, bool shown, int x = 100, int y = 900, bool split = false)
        {
            var picture = BurnSmith.Core.Disc.PgsStream.Pack([1, 1, 1, 1, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 1, 0, 0, 2, 2, 0, 1, 2, 1, 2, 1, 2, 1, 2], 8, 4);
            byte[] placed = shown ? [.. Be(0, 2), 0, 0, .. Be(x, 2), .. Be(y, 2)] : [];
            byte[] composition = [.. Be(1920, 2), .. Be(1080, 2), 0x10, .. Be(1, 2), (byte)(shown ? 0x80 : 0), 0, 0, (byte)(shown ? 1 : 0), .. placed];
            var set = new List<byte[]> { Segment(0x16, at, composition), Segment(0x17, at, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0) };
            if (shown)
            {
                set.Add(Segment(0x14, at, [0, 0, 1, 235, 128, 128, 255, 2, 16, 128, 128, 255]));
                if (!split) set.Add(Segment(0x15, at, [.. Be(0, 2), 0, 0xC0, .. Be(picture.Length + 4, 3), .. Be(8, 2), .. Be(4, 2), .. picture]));
                else // the picture in two pieces: first (0x80) and last (0x40)
                {
                    set.Add(Segment(0x15, at, [.. Be(0, 2), 0, 0x80, .. Be(picture.Length + 4, 3), .. Be(8, 2), .. Be(4, 2), .. picture[..10]]));
                    set.Add(Segment(0x15, at, [.. Be(0, 2), 0, 0x40, .. picture[10..]]));
                }
            }
            set.Add(Segment(0x80, at));
            return [.. set.SelectMany(s => s)];
        }
        byte[] sup = [.. SupSet(1, true), .. SupSet(3, false), .. SupSet(5, true, 200), .. SupSet(8, false)];
        string Times(IEnumerable<PgsSubtitles.Cue> list) => string.Join('|', list.Select(c => FormattableString.Invariant($"{c.Start:0.##}-{c.End:0.##}")));
        var pictures = PgsSubtitles.Cues(sup, out var undecoded);
        Check("PGS pictures: shown until cleared, placed, 8x4 on a 1920x1080 plane, white and black kept, nothing transparent",
              Times(pictures) == "1-3|5-8" && undecoded == 0 && pictures[1].X == 200 && pictures[0].Y == 900 && pictures[0].Width == 8 && pictures[0].Height == 4 && pictures[0].PlaneWidth == 1920 && PgsSubtitles.PlaneSize(sup) == (1920, 1080)
              && pictures[0].Luma[0] == 255 && pictures[0].Luma[8] == 0 && pictures[0].Alpha[0] == 255 && pictures[0].Alpha[4] == 0);
        Check("PGS cut: a picture on screen at the start stays from 0, later ones move back, the clear after the end is dropped, the last lasts 3 s",
              Times(PgsSubtitles.Cues(PgsSubtitles.Cut(sup, 2, 4), out _)) == "0-1|3-6");
        Check("PGS cut: starting right after a clear keeps nothing old; nothing to show gives an empty file; the whole stream is unchanged",
              Times(PgsSubtitles.Cues(PgsSubtitles.Cut(sup, 3.5, 5), out _)) == "1.5-4.5" && PgsSubtitles.Cut(sup, 9, 5).Length == 0 && PgsSubtitles.Cut(sup, 0, 100).SequenceEqual(sup));
        var pieces = PgsSubtitles.Cues([.. SupSet(1, true, split: true), .. SupSet(3, false)], out _);
        Check("PGS picture sent in two pieces reads the same as in one", pieces.Count == 1 && pieces[0].Luma.SequenceEqual(pictures[0].Luma) && pieces[0].Alpha.SequenceEqual(pictures[0].Alpha));
        var notSup = false;
        try { PgsSubtitles.Cues([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14], out _); } catch (InvalidDataException) { notSup = true; }
        var pgsFile = new ExternalSubtitle("a.fr.sup", "fre").Track(2);
        Check("a .sup is a PGS track the disc can carry, named after its language; text tracks are not PGS", notSup && pgsFile.IsPgs && pgsFile.Supported && ExternalSubtitle.Extensions.Contains(".sup") && !new ExternalSubtitle("a.srt").Track(0).IsPgs);
        // VobSub built by hand: pictures in four colours, run-length coded in nibbles, field by field, cut into packs of a program stream
        static byte[] SpuField(byte[] px, int w, int h, int first, bool toEnd)
        {
            var n = new List<int>();
            for (var y = first; y < h; y += 2)
            {
                for (var x = 0; x < w;)
                {
                    int c = px[y * w + x], run = 1;
                    while (x + run < w && px[y * w + x + run] == c) run++;
                    if (x + run == w && run > 3 && toEnd) { n.AddRange([0, 0, 0, c]); break; } // 0 = to the end of the row
                    run = Math.Min(run, 255);
                    var code = run << 2 | c;
                    for (var d = (run < 4 ? 1 : run < 16 ? 2 : run < 64 ? 3 : 4) - 1; d >= 0; d--) n.Add(code >> 4 * d & 15);
                    x += run;
                }
                if (n.Count % 2 == 1) n.Add(0);
            }
            return [.. Enumerable.Range(0, n.Count / 2).Select(i => (byte)(n[2 * i] << 4 | n[2 * i + 1]))];
        }
        byte[] Spu(byte[] px, int w, int h, int x0, int y0, int stopTicks, bool toEnd)
        {
            byte[] top = SpuField(px, w, h, 0, toEnd), bottom = SpuField(px, w, h, 1, toEnd);
            var first = 4 + top.Length + bottom.Length;
            byte[] area = [(byte)(x0 >> 4), (byte)((x0 & 15) << 4 | (x0 + w - 1) >> 8), (byte)(x0 + w - 1), (byte)(y0 >> 4), (byte)((y0 & 15) << 4 | (y0 + h - 1) >> 8), (byte)(y0 + h - 1)];
            byte[] commands = [0x01, 0x03, 0x32, 0x10, 0x04, 0x8F, 0xF0, 0x05, .. area, 0x06, .. Be(4, 2), .. Be(4 + top.Length, 2), 0xFF];
            var second = first + 4 + commands.Length; // the sequence that hides it (none when stopTicks < 0: the sequence then points to itself)
            byte[] stop = stopTicks < 0 ? [] : [.. Be(stopTicks, 2), .. Be(second, 2), 0x02, 0xFF];
            return [.. Be(second + stop.Length, 2), .. Be(first, 2), .. top, .. bottom, .. Be(0, 2), .. Be(stopTicks < 0 ? first : second, 2), .. commands, .. stop];
        }
        static byte[] Pack(int substream, byte[] data, double? seconds)
        {
            var pts = seconds is { } t ? (long)(t * 90000) : 0;
            byte[] stamp = seconds == null ? [] : [(byte)(0x21 | (pts >> 30 & 7) << 1), (byte)(pts >> 22), (byte)((pts >> 15 & 0x7F) << 1 | 1), (byte)(pts >> 7), (byte)((pts & 0x7F) << 1 | 1)];
            byte[] pes = [0, 0, 1, 0xBD, .. Be(3 + stamp.Length + 1 + data.Length, 2), 0x81, (byte)(seconds == null ? 0 : 0x80), (byte)stamp.Length, .. stamp, (byte)substream, .. data];
            return [0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 1, 0x89, 0xC3, 0xF8, .. pes];
        }
        byte[] vobA = [.. Enumerable.Range(0, 12 * 6).Select(i => (byte)((i % 12 / 3 + i / 12) % 4))];
        for (var i = 24; i < 36; i++) vobA[i] = 2; // a whole row of one colour
        byte[] vobB = new byte[300 * 4];
        for (var x = 0; x < 300; x++) (vobB[x], vobB[300 + x], vobB[600 + x], vobB[900 + x]) = (1, (byte)(x < 70 ? 0 : 3), (byte)(x < 20 ? 2 : 1), (byte)(x % 2 == 0 ? 3 : 0));
        var vobDir = Path.Combine(Path.GetTempPath(), "bs_vobsub_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(vobDir);
        try
        {
            byte[] spuA = Spu(vobA, 12, 6, 100, 400, 200, false), spuB = Spu(vobB, 300, 4, 20, 30, -1, true), spuC = Spu(vobA, 12, 6, 7, 9, 100, false);
            var sub = new List<byte>();
            long at1 = sub.Count;
            sub.AddRange(Pack(0x20, spuA[..30], 1.5));                // the first subpicture in two packs, a packet of another stream between them
            long at3 = sub.Count;
            sub.AddRange(Pack(0x21, spuC, 2.0));
            sub.AddRange(Pack(0x20, spuA[30..], null));
            long at2 = sub.Count;
            sub.AddRange(Pack(0x20, spuB, 5.0));
            File.WriteAllBytes(Path.Combine(vobDir, "v.sub"), [.. sub]);
            File.WriteAllText(Path.Combine(vobDir, "v.idx"), $"# VobSub index file, v7 (do not modify this line!)\nsize: 720x480\npalette: 000000, ffffff, ff0000, 00ff00, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000\n\nid: fr, index: 0\n"
                + $"timestamp: 00:00:01:500, filepos: {at1:x9}\ntimestamp: 00:00:05:000, filepos: {at2:x9}\n\nid: xx, index: 1\ntimestamp: 00:00:02:000, filepos: {at3:x9}\n");
            var idx = VobSub.ReadIndex(Path.Combine(vobDir, "v.idx"));
            Check("VobSub index: size, 16 colours, one track per id with its language, its pictures' times and places",
                  idx.Width == 720 && idx.Height == 480 && idx.Palette.Length == 16 && idx.Palette[2] == 0xFF0000 && idx.Streams.Count == 2 && idx.Streams[0].Lang == "fre" && idx.Streams[1].Lang == "und" && idx.Streams[1].Number == 1
                  && idx.Streams[0].Cues.Count == 2 && idx.Streams[0].Cues[0] == (1.5, at1) && idx.Streams[0].Cues[1] == (5.0, at2));
            var decodedA = VobSub.Decode(spuA, idx.Palette);
            var decodedB = VobSub.Decode(spuB, idx.Palette);
            Check("VobSub picture: place and size, every pixel through the nibble codes of all lengths and the two fields, colours and opacity",
                  decodedA is { X: 100, Y: 400, Width: 12, Height: 6 } && decodedA.Pixels.SequenceEqual(vobA) && decodedA.Colors[1] == (255, 255, 255, 255) && decodedA.Colors[2] == (255, 0, 0, 255) && decodedA.Colors[3] == (0, 255, 0, 136) && decodedA.Colors[0].A == 0
                  && decodedB is { X: 20, Y: 30, Width: 300, Height: 4 } && decodedB.Pixels.SequenceEqual(vobB) && Math.Abs(decodedA.End - 200 * 1024 / 90000.0) < 1e-9 && double.IsNaN(decodedB.End));
            Check("VobSub: not a subpicture gives nothing, not an error", VobSub.Decode([1, 2, 3], idx.Palette) == null && VobSub.Decode([.. spuA[..20]], idx.Palette) == null && VobSub.ReadSpu([.. sub], 0, 0x25) == null);
            var vobPictures = VobSub.Read(Path.Combine(vobDir, "v.idx"), 0, out var vobSkipped);
            Check("VobSub: a subpicture cut into two packs with another stream's packet between them is read whole; shown at its time, hidden by its own command, else after 3 s or when the next shows",
                  vobSkipped == 0 && vobPictures.Count == 2 && vobPictures[0].Pixels.SequenceEqual(vobA) && Math.Abs(vobPictures[0].Start - 1.5) < 1e-9 && Math.Abs(vobPictures[0].End - (1.5 + 200 * 1024 / 90000.0)) < 1e-6
                  && Math.Abs(vobPictures[1].Start - 5.0) < 1e-9 && Math.Abs(vobPictures[1].End - 8.0) < 1e-9 && VobSub.Read(Path.Combine(vobDir, "v.idx"), 1, out _) is [{ X: 7, Y: 9 }]);
            var vobSup = VobSub.ToSup(Path.Combine(vobDir, "v.idx"), 0, 720, 480, out _);
            var vobCues = PgsSubtitles.Cues(vobSup, out var vobBad);
            Check("VobSub as PGS: same times, place and size on a 720x480 plane, white kept, the clear colour transparent",
                  vobBad == 0 && vobCues.Count == 2 && Math.Abs(vobCues[0].Start - 1.5) < 0.001 && Math.Abs(vobCues[0].End - vobPictures[0].End) < 0.001 && vobCues[0] is { X: 100, Y: 400, Width: 12, Height: 6, PlaneWidth: 720, PlaneHeight: 480 }
                  && Enumerable.Range(0, 72).All(i => vobCues[0].Alpha[i] == (vobA[i] == 0 ? 0 : vobA[i] == 3 ? 136 : 255)) && Enumerable.Range(0, 72).Where(i => vobA[i] == 1).All(i => vobCues[0].Luma[i] == 255)
                  && vobCues[1].Width == 300 && vobCues[1].Alpha[0] == 255 && vobCues[1].Alpha[300] == 0 && PgsSubtitles.PlaneSize(vobSup) == (720, 480));
            var vobBig = PgsSubtitles.Cues(VobSub.ToSup(Path.Combine(vobDir, "v.idx"), 0, 1440, 960, out _), out _);
            Check("VobSub scaled to a bigger plane: the picture grows with the frame, its middle keeps its opacity", vobBig.Count == 2 && vobBig[0] is { X: 200, Y: 800, Width: 24, Height: 12, PlaneWidth: 1440 } && vobBig[0].Alpha[6 * 24 + 6] > 0 && vobBig[1].Width == 600);
            var idxFile = Path.Combine(vobDir, "v.idx");
            var tracks = ExternalSubtitle.ParseAll(idxFile);
            var typedTrack = ExternalSubtitle.ParseAll("ger=" + idxFile);
            var missingSub = false;
            File.Move(Path.Combine(vobDir, "v.sub"), Path.Combine(vobDir, "w.sub"));
            try { ExternalSubtitle.ParseAll(idxFile); } catch (FileNotFoundException) { missingSub = true; }
            File.Move(Path.Combine(vobDir, "w.sub"), Path.Combine(vobDir, "v.sub"));
            var nearIdx = ExternalSubtitle.Near(Path.Combine(vobDir, "v.mkv"));
            File.Move(Path.Combine(vobDir, "v.sub"), Path.Combine(vobDir, "w.sub"));
            var nearNoSub = ExternalSubtitle.Near(Path.Combine(vobDir, "v.mkv"));
            File.Move(Path.Combine(vobDir, "w.sub"), Path.Combine(vobDir, "v.sub"));
            Check("an .idx beside the video is found with its .sub (one track per stream), and ignored without it", nearIdx.Count == 2 && nearIdx[1].Stream == 1 && nearNoSub.Count == 0);
            Check("an .idx is one track per stream, in the language it gives (and a .sub chosen stands for its .idx); typed language: one track; without its .sub: refused",
                  tracks is [{ Lang: "fre", Stream: 0 }, { Lang: "und", Stream: 1 }] && tracks[1].Track(5).IsPgs && typedTrack is [{ Lang: "ger", Stream: 0 }] && missingSub
                  && ExternalSubtitle.ForAll(Path.Combine(vobDir, "film.mkv"), Path.Combine(vobDir, "v.sub")).Count == 2 && ExternalSubtitle.Extensions.Contains(".idx"));
            Check("a VobSub inside a video: its codec data (ffprobe's hex dump) reads as the head of an .idx, the track is carried as pictures; the second track of an .idx without language is not named like the first",
                  VobSub.ExtradataText("00000000: 7369 7a65 3a20 3633 3278 3237 360a 7061  size: 632x276.pa\n00000010: 6c65 7474 653a 2066 6666 6666 66         lette: ffffff\n") == "size: 632x276\npalette: ffffff" && VobSub.ParseIndex("size: 632x276", 720, 576) is { Width: 632, Height: 276 } && VobSub.ParseIndex("", 720, 576) is { Width: 720, Height: 576 } && VobSub.ParseIndex("") is { Height: 480 }
                  && new SubtitleTrack(0, "dvd_subtitle", "fre", "", false) is { IsVobSub: true, IsPicture: true, Supported: true, IsPgs: false } && !new SubtitleTrack(0, "dvb_subtitle", "fre", "", false).Supported
                  && new ExternalSubtitle("a.idx", "und", Stream: 0).Track(2).Title == "a" && new ExternalSubtitle("a.idx", "und", Stream: 1).Track(3).Title == "a (2)");
            // an object too big for one segment goes in pieces and reads back whole
            var noise = new byte[1500 * 300];
            for (var i = 0; i < noise.Length; i++) noise[i] = (byte)(1 + (i * 7 + i / 3) % 200);
            var writer = new PgsSubtitles.Writer(1920, 1080);
            writer.Add(1, 2, 10, 20, 1500, 300, noise, [.. Enumerable.Range(0, 201).Select(i => ((byte)i, (byte)(255 - i), (byte)(i / 2), (byte)255))]);
            var bigSup = writer.Finish();
            var bigCues = PgsSubtitles.Cues(bigSup, out var bigBad);
            Check("PGS written in pieces: a picture bigger than one segment reads back whole", bigBad == 0 && bigCues.Count == 1 && bigCues[0] is { X: 10, Y: 20, Width: 1500, Height: 300 } && bigCues[0].Alpha.All(a => a == 255) && bigSup.Length > 2 * 65535);
        }
        finally { Directory.Delete(vobDir, true); }
        var choices = Languages.Choices.ToList();
        Check("languages to choose from: one per language, each code read back as itself, a name for each", choices.Count >= 20 && choices.Select(c => c.Name).Distinct().Count() == choices.Count
              && choices.All(c => c.Name.Length > 0 && Languages.Code(c.Code) == c.Code) && choices.Any(c => c.Code == "fre" && c.Name == "Français"));
        Check("same language: fre = fra, not ger, and an unknown language is only itself", Languages.Same("fre", "fra") && !Languages.Same("fre", "ger") && Languages.Same("und", "und") && !Languages.Same("und", "fre") && Languages.Same("xyz", "xyz"));
        Check("language words", Languages.Code("fr") == "fre" && Languages.Code("fra") == "fra" && Languages.Code("français") == "fre" && Languages.Code("German") == "ger" && Languages.Code("2017") == null && Languages.Code("it") == "ita");
        var three = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 600, [new(0, "ac3", 2, "fre", ""), new(1, "ac3", 2, "eng", ""), new(2, "dts", 6, "ger", "")], []);
        string Kept(DiscTitle t) => string.Join(',', t.KeptAudio(three).Select(a => a.Index));
        Check("default audio goes first, the others keep their order; a default that is not kept, null or already first change nothing",
              Kept(new DiscTitle("x", Audio: [0, 1, 2], DefaultAudio: 2)) == "2,0,1" && Kept(new DiscTitle("x", Audio: [0, 2], DefaultAudio: 1)) == "0,2" && Kept(new DiscTitle("x", Audio: [0, 1])) == "0,1"
              && Kept(new DiscTitle("x", Audio: [0, 1], DefaultAudio: 0)) == "0,1" && Kept(new DiscTitle("x", DefaultAudio: 1)) == "0");
        {
            var dtsFirst = new SourceInfo("h264", 1920, 1080, 24, "dts", 6, 600, [new(0, "dts", 6, "fre", ""), new(1, "ac3", 6, "eng", ""), new(2, "truehd", 8, "fre", "", Lossless: true)], []);
            var (compat, enc, hdmv) = Project.Compatible([new DiscTitle("x", Audio: [0, 1, 2]), new DiscTitle("x", Audio: [0, 1])], new EncodeOptions(Lossless: true, CopyEac3: true), _ => dtsFirst);
            Check("« Compatibilité maximale »: HDMV menu, no lossless sound; a DTS first gives way to a track of its language that becomes AC-3, never to another language; the slowest burn is 2x",
                  hdmv && !enc.Lossless && !enc.CopyEac3 && string.Join(',', compat[0].KeptAudio(dtsFirst).Select(a => a.Index)) == "2,0,1" && compat[1].DefaultAudio == null
                  && new DiscTitle("x", Audio: [1, 0]).Ac3First(dtsFirst).DefaultAudio == null && Iso.Speeds[^1] == 2);
            Check("warnings before a build: a 100 GB disc (Blu-ray 4K 93 GB usable) only; not a BD-66, a BD-50, a DVD or « Sans limite »",
                  Project.Warning("uhd", 93) != null && Project.Warning("bd", 93) != null && Project.Warning("uhd", 61) == null && Project.Warning("bd", 46.5) == null && Project.Warning("dvd", 93) == null && Project.Warning("uhd", 0) == null);
        }
        SubtitleTrack[] carried = [new(0, "subrip", "eng", "", false), new(2, "hdmv_pgs_subtitle", "fre", "", false)];
        Check("default subtitle: its 1-based number among the tracks on the disc, 0 when none, not chosen or not carried",
              new DiscTitle("x", DefaultSubtitle: 2).DefaultSubtitleNumber(carried) == 2 && new DiscTitle("x", DefaultSubtitle: 0).DefaultSubtitleNumber(carried) == 1 && new DiscTitle("x").DefaultSubtitleNumber(carried) == 0 && new DiscTitle("x", DefaultSubtitle: 1).DefaultSubtitleNumber(carried) == 0);
        byte[] cmdA = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], cmdB = [2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2], cmdC = [3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3];
        var movieObjects = Bdmv.PrependToFilm(BurnSmith.Core.Disc.MovieObjectFile.Write([[cmdA, cmdB], [cmdC]]), cmdC);
        Check("a command put first in the film's movie object: object 0 gains it before its own, the others stay, the file stays valid",
              movieObjects.Length == BurnSmith.Core.Disc.MovieObjectFile.Write([[cmdC, cmdA, cmdB], [cmdC]]).Length && movieObjects.SequenceEqual(BurnSmith.Core.Disc.MovieObjectFile.Write([[cmdC, cmdA, cmdB], [cmdC]]))
              && new Func<bool>(() => { try { Bdmv.PrependToFilm([1, 2, 3], cmdA); return false; } catch (InvalidDataException) { return true; } })());
        {
            // "Adapter au disque": small source files only fit as they are when they are copied; an HEVC film is re-encoded, so it gets a bit rate (else CRF 17 has no size bound)
            BluRayJob fit = new([new DiscTitle("not-there.mkv")], "", null, false, "X", 5, new EncodeOptions(FitDisc: true), 23.3);
            SourceInfo hevc = new("hevc", 1920, 1080, 23.976, "", 0, 3600, [], []), ready = hevc with { VideoCodec = "h264", PixFmt = "yuv420p", Profile = "High", Level = 41 };
            Check("fit disc: an HEVC source that fits is still given a bit rate, a Blu-ray-ready H.264 one is copied",
                  BluRay.EstimateGB(fit, [hevc]) > 1 && BluRay.EstimateGB(fit, [ready]) == 0);
        }
        Check("a 4K disc's MovieObject.bdmv (MOBJ0300, tsMuxeR --blu-ray-v3) takes a command and an object, and keeps its version",
              Bdmv.PrependToFilm(BurnSmith.Core.Disc.MovieObjectFile.Write([[cmdA]], "0300"), cmdC).SequenceEqual(BurnSmith.Core.Disc.MovieObjectFile.Write([[cmdC, cmdA]], "0300"))
              && Bdmv.AppendObject(BurnSmith.Core.Disc.MovieObjectFile.Write([[cmdA]], "0300"), out _, cmdB).AsSpan(0, 8).SequenceEqual("MOBJ0300"u8));
        var order = DiscTitle.MainFirst([new("b1.mkv", Bonus: true), new("m1.mkv"), new("b2.mkv", Bonus: true), new("m2.mkv")]);
        Check("disc order: the feature first, then the bonus, each group in its own order", string.Join(',', order.Select(t => t.Video)) == "m1.mkv,m2.mkv,b1.mkv,b2.mkv");
        Check("a disc of bonus only is refused", Throws(() => DiscTitle.MainFirst([new("b.mkv", Bonus: true)])) && DiscTitle.MainFirst([]).Count == 0);
        Check("typed times", Timecode.Parse("90") == 90 && Timecode.Parse("1:30") == 90 && Timecode.Parse("1:02:03,5") == 3723.5 && Timecode.Parse("1:x") == null && Timecode.Parse("1.5:00") == null && Timecode.Parse("") == null);
        Check("times shown", Timecode.Format(3723) == "1:02:03" && Timecode.Format(90.5) == "0:01:30.5");
        return failed == 0 ? 0 : 1;
    
        static bool Throws(Action act) { try { act(); return false; } catch (ArgumentException) { return true; } }
    }
}
