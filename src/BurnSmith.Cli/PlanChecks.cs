using System.Buffers.Binary;
using System.IO.Compression;
using BurnSmith.Core;
using BurnSmith.Core.Menu;

/// <summary>
/// Automated checks for Phase 1+ deliverables: BD-J jar bytecode version, manual season split &amp; +30 % suggestion,
/// BD-J navigation with submenus, directional links, auto-start, remember-position, page fade and popup shade.
/// </summary>
static class PlanChecks
{
    static int failures;

    static void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}{(ok || detail.Length == 0 ? "" : " — " + detail)}");
        if (!ok) failures++;
    }

    public static int Run()
    {
        failures = 0;
        var root = Path.Combine(Path.GetTempPath(), "bs_plan_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            CheckBdjJar();
            CheckManualSeasonSplit(root);
            CheckAudioRealignmentAndPaths(root);
            CheckNavigationMap();
            CheckBdjNavigationAndOptions(root);
        }
        finally { Tools.TryDelete(root); }
        Console.WriteLine(failures == 0 ? "all ok" : $"{failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    static void CheckBdjJar()
    {
        using var stream = typeof(BluRay).Assembly.GetManifestResourceStream("assets/bdj/menu.jar");
        Check("bdj: embedded menu.jar is present", stream != null);
        if (stream == null) return;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var cls in new[] { "org/burnsmith/menu/Menu.class", "org/burnsmith/menu/ResumeStore.class" })
        {
            var entry = zip.GetEntry(cls);
            if (entry == null) { Check($"bdj: {cls} present in menu.jar", false); continue; }
            using var s = entry.Open();
            var head = new byte[8];
            s.ReadExactly(head);
            var magic = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4));
            var major = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6, 2));
            Check($"bdj: {cls} is valid Java 1.3 bytecode (major 47)", magic == 0xCAFEBABE && major == 47, $"magic=0x{magic:X8} major={major}");
        }
    }

    static void CheckManualSeasonSplit(string root)
    {
        var ep = new SourceInfo("h264", 1920, 1080, 24, "ac3", 2, 3600, [new(0, "ac3", 2, "fre", "")], []);
        var introSrc = new SourceInfo("h264", 1920, 1080, 24, "ac3", 2, 10, [new(0, "ac3", 2, "fre", "")], []);
        var introTitle = new DiscTitle(Path.Combine(root, "intro.mkv"), "Intro", Intro: true);
        var eps = Enumerable.Range(1, 4).Select(i => new DiscTitle(Path.Combine(root, $"ep{i}.mkv"), $"Épisode {i}")).ToList();
        var probed = new List<(DiscTitle, SourceInfo)> { (introTitle, introSrc) }.Concat(eps.Select(e => (e, ep))).ToList();

        // 4h at 18 Mbps = ~32.4 GB > 23.3 * 1.30 (30.29 GB) -> suggests split
        var overProject = new Project("bd", probed.Select(p => p.Item1).ToList(), new MenuDesign { Title = "Ma Série" },
            CapacityGB: 23.3, Out: Path.Combine(root, "serie"), Encode: new EncodeOptions(FitDisc: true, BdKbps: 18000));
        Check("volumes: suggests manual split when season exceeds BD25 by >= 30 %",
            overProject.ShouldSuggestSplit(out var estOver, out var usable, probed) && estOver >= usable * Project.SplitSuggestionRatio,
            $"est={estOver:0.00} usable={usable:0.0}");

        // 4h at 12 Mbps = ~21.6 GB < 23.3 * 1.30 -> no suggestion
        var fitProject = overProject with { Encode = new EncodeOptions(FitDisc: true, BdKbps: 12000) };
        Check("volumes: does not suggest split when under +30 % threshold",
            !fitProject.ShouldSuggestSplit(out var estFit, out _, probed), $"est={estFit:0.00}");

        // Single video > +30 % -> cannot split a single video
        var single = overProject with { Titles = [eps[0]] };
        Check("volumes: does not suggest split for a single video",
            !single.ShouldSuggestSplit(out _, out _, [(eps[0], ep)]));

        // Manual split [0,1] and [2,3] -> 2 discs, intro repeated on both, ISO enabled
        var vols = overProject.Volumes(Path.Combine(root, "out"), [[0, 1], [2, 3]], probed);
        Check("volumes: manual split produces 2 ordered discs with intro on each",
            vols.Count == 2
            && vols[0].Disc.Titles!.Count == 3 && vols[0].Disc.Titles![0].Intro && vols[0].Disc.Design!.Title == "Ma Série · Disque 1" && vols[0].Disc.Iso
            && vols[1].Disc.Titles!.Count == 3 && vols[1].Disc.Titles![0].Intro && vols[1].Disc.Design!.Title == "Ma Série · Disque 2" && vols[1].Disc.Iso);

        // Invalid manual split (missing episode) -> throws ArgumentException
        var threw = false;
        try { overProject.Volumes(Path.Combine(root, "out"), [[0, 1], [2]], probed); }
        catch (ArgumentException) { threw = true; }
        Check("volumes: rejects incomplete manual split", threw);
    }

    static void CheckAudioRealignmentAndPaths(string root)
    {
        var ep1Src = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 120,
            [new(0, "ac3", 6, "fre", ""), new(1, "ac3", 6, "eng", "")], []);
        var ep2Src = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 120,
            [new(0, "ac3", 6, "eng", ""), new(1, "ac3", 6, "fre", "")], []);
        var bonusSrc = new SourceInfo("h264", 1920, 1080, 24, "ac3", 2, 60,
            [new(0, "ac3", 2, "jpn", "")], []);
        var t1 = new DiscTitle("ep1.mkv", Audio: [0, 1]);
        var t2 = new DiscTitle("ep2.mkv", Audio: [0, 1]);
        var tb = new DiscTitle("bonus.mkv", Audio: [0], Bonus: true);
        var lined = DiscTitle.LineUpAudio([t1, t2, tb], [ep1Src, ep2Src, bonusSrc]);
        Check("audio: LineUpAudio reorders reversed episode tracks to match episode 1",
            lined[1].Count == 2 && lined[1][0].Lang == "fre" && lined[1][1].Lang == "eng",
            string.Join(",", lined[1].Select(a => a.Lang)));
        Check("audio: LineUpAudio preserves single-language bonus at slot 1",
            lined[2].Count == 1 && lined[2][0].Lang == "jpn");

        // Relative / moved project path resolution
        var movedDir = Path.Combine(root, "moved");
        Directory.CreateDirectory(movedDir);
        var actualVideo = Path.Combine(movedDir, "film.mkv");
        File.WriteAllText(actualVideo, "stub");
        var movedProj = new Project("bd", [new DiscTitle(@"Z:\old-pc\folder\film.mkv")]);
        var resolved = movedProj.ResolvePaths(movedDir);
        Check("project: ResolvePaths locates moved video beside .burnsmith file",
            resolved.Titles![0].Video == actualVideo, resolved.Titles![0].Video);
    }

    static void CheckNavigationMap()
    {
        var goodPages = new List<MenuPage>
        {
            new("root", [], [new("play", new(0, 0, 100, 40), new("play", null, 1, 0)), new("scenes", new(0, 50, 100, 40), new("page", "scenes1", 0, 0))]),
            new("scenes1", [], [new("back", new(0, 0, 100, 40), new("page", "root", 0, 0))])
        };
        var goodReport = MenuNavigationMap.Analyze(goodPages, new PlaybackOptions(), "Blu-ray BD-J");
        Check("navmap: valid menu produces zero warnings", goodReport.Warnings.Count == 0, string.Join(" ; ", goodReport.Warnings));

        var orphanPages = new List<MenuPage>
        {
            new("root", [], [new("play", new(0, 0, 100, 40), new("play", null, 1, 0))]),
            new("orphan", [], [])
        };
        var badReport = MenuNavigationMap.Analyze(orphanPages, new PlaybackOptions(), "DVD");
        Check("navmap: detects unreachable and dead-end pages",
            badReport.Warnings.Any(w => w.Contains("inaccessible")) && badReport.Warnings.Any(w => w.Contains("sans issue")));
    }

    static void CheckBdjNavigationAndOptions(string root)
    {
        var ffmpeg = Tools.Find("ffmpeg.exe");
        var clip = Path.Combine(root, "clip.mkv");
        Tools.Run(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", "color=s=320x240:r=24:d=2",
            "-f", "lavfi", "-i", "sine=f=440:r=48000:d=2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "ac3", "-ar", "48000", clip]);

        var design = new MenuDesign { Title = "Test Nav", Popup = true, PopupShade = 65, PageFadeMs = 150 };
        design.CustomPages.Add(new MenuDesign.CustomMenuPage("custom-extras", "Suppléments"));
        design.Elements.Add(new MenuElement { Kind = "button", Page = "root", Text = "Suppléments", Target = "custom-extras", NavigationId = "u-sub", X = 100, Y = 800, W = 320, H = 70 });
        design.Elements.Add(new MenuElement { Kind = "button", Page = "custom-extras", Text = "Retour", Target = "root", NavigationId = "u-back", X = 100, Y = 800, W = 320, H = 70 });
        design.Directions.Add(new MenuDesign.MenuDirection("root", "play", "down", "u-sub"));

        var outDir = Path.Combine(root, "bdj_nav");
        var playback = new PlaybackOptions(AutoStartSeconds: 15, RememberPosition: true);
        var job = new BluRayJob([new DiscTitle(clip, "Film", EndAction: "menu")], outDir, design, false, "NAV", 5,
            new EncodeOptions(ForceReencode: true, Preset: "ultrafast", Gpu: false), 23.3, playback);
        BluRay.Build(job);

        var (trace, problems) = DiscCheck.Navigation(outDir);
        Check("navigation: check-navigation finds zero problems on BD-J with submenu and directional link",
            problems.Count == 0 && trace.Count > 0, string.Join(" ; ", problems));

        using var jar = ZipFile.OpenRead(Path.Combine(outDir, "BDMV", "JAR", "00000.jar"));
        using var reader = new StreamReader(jar.GetEntry("menu.txt")!.Open());
        var menuTxt = reader.ReadToEnd();
        Check("bdj: menu.txt encodes auto-start, resume-key, page-fade, title-end and custom submenu",
            menuTxt.Contains("auto-start 15")
            && menuTxt.Contains("resume-key ")
            && menuTxt.Contains("page-fade 150")
            && menuTxt.Contains("title-end 1 menu")
            && menuTxt.Contains("page custom-extras"),
            menuTxt.Split('\n').FirstOrDefault(l => l.StartsWith("page ")) ?? "");

        // HDMV animated background + native HDMV pop-up menu
        var hdmvDir = Path.Combine(root, "hdmv_adv");
        var hdmvDesign = new MenuDesign { Title = "Test HDMV", Loop = true, LoopSeconds = 5, Popup = true, HdrGraphicsNits = 203 };
        var hdmvJob = new BluRayJob([new DiscTitle(clip, "Film HDMV")], hdmvDir, hdmvDesign, false, "HDMV", 5,
            new EncodeOptions(ForceReencode: true, Preset: "ultrafast", Gpu: false), 23.3, HdmvMenu: true);
        BluRay.Build(hdmvJob);
        var hdmvProblems = DiscCheck.Folder(hdmvDir);
        Check("hdmv: animated background + native pop-up passes DiscCheck.Folder",
            hdmvProblems.Count == 0, string.Join(" ; ", hdmvProblems));
        Check("hdr: TextSubtitleLook scales peak white for UHD HDR calibration",
            BluRay.TextSubtitleLook(new SubtitleStyle(HdrNits: 203), 1080, uhd: true).Contains("font-color=0xff818181"),
            BluRay.TextSubtitleLook(new SubtitleStyle(HdrNits: 203), 1080, uhd: true));

        // Dedicated AVCHD (USB / SD) layout: <out>/AVCHD/BDMV passes DiscCheck.Folder
        var avchdDir = Path.Combine(root, "avchd_out");
        var avchdJob = new BluRayJob([new DiscTitle(clip, "Film AVCHD")], avchdDir, null, false, "AVCHD", 5,
            new EncodeOptions(ForceReencode: true, Preset: "ultrafast", Gpu: false), 4.2, Avchd: true);
        BluRay.Build(avchdJob);
        var avchdRoot = Path.Combine(avchdDir, "AVCHD");
        var avchdProblems = DiscCheck.Folder(avchdRoot);
        Check("avchd: builds valid AVCHD/BDMV layout passing DiscCheck.Folder",
            Directory.Exists(Path.Combine(avchdRoot, "BDMV")) && avchdProblems.Count == 0,
            string.Join(" ; ", avchdProblems));
    }
}
