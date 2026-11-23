using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BurnSmith.Core;
using BurnSmith.Core.Disc;
using BurnSmith.Core.DvdVideo;
using BurnSmith.Core.Menu;

namespace BurnSmith.Tests;

public sealed class CoreUnitTests
{
    [Fact]
    public void Project_ShouldSuggestSplit_OnlyAbove30PercentWithMultipleVideos()
    {
        var ep = new SourceInfo("h264", 1920, 1080, 24, "ac3", 2, 3600, [new(0, "ac3", 2, "fre", "")], []);
        var eps = Enumerable.Range(1, 4).Select(i => new DiscTitle($"ep{i}.mkv", $"Épisode {i}")).ToList();
        var probed = eps.Select(e => (e, ep)).ToList();

        var over = new Project("bd", eps, new MenuDesign { Title = "Série" }, CapacityGB: 23.3, Out: @"C:\out",
            Encode: new EncodeOptions(FitDisc: true, BdKbps: 18000));
        Assert.True(over.ShouldSuggestSplit(out var estOver, out var usable, probed));
        Assert.True(estOver >= usable * Project.SplitSuggestionRatio);

        var under = over with { Encode = new EncodeOptions(FitDisc: true, BdKbps: 12000) };
        Assert.False(under.ShouldSuggestSplit(out _, out _, probed));

        var single = over with { Titles = [eps[0]] };
        Assert.False(single.ShouldSuggestSplit(out _, out _, [(eps[0], ep)]));
    }

    [Fact]
    public void Project_ManualVolumes_ValidatesAndBuildsOrderedDiscs()
    {
        var ep = new SourceInfo("h264", 1920, 1080, 24, "ac3", 2, 1800, [new(0, "ac3", 2, "fre", "")], []);
        var intro = new DiscTitle("intro.mkv", "Intro", Intro: true);
        var eps = Enumerable.Range(1, 4).Select(i => new DiscTitle($"ep{i}.mkv", $"Ép {i}")).ToList();
        var probed = new List<(DiscTitle, SourceInfo)> { (intro, ep) }.Concat(eps.Select(e => (e, ep))).ToList();
        var proj = new Project("bd", probed.Select(p => p.Item1).ToList(), new MenuDesign { Title = "Saison" }, CapacityGB: 23.3, Out: @"C:\out");

        var vols = proj.Volumes(@"C:\out", [[0, 1], [2, 3]], probed);
        Assert.Equal(2, vols.Count);
        Assert.Equal("Saison · Disque 1", vols[0].Disc.Design!.Title);
        Assert.Equal("Saison · Disque 2", vols[1].Disc.Design!.Title);
        Assert.True(vols[0].Disc.Titles![0].Intro);
        Assert.True(vols[1].Disc.Titles![0].Intro);
        Assert.True(vols[0].Disc.Iso);

        Assert.Throws<ArgumentException>(() => proj.Volumes(@"C:\out", [[0, 1], [2]], probed));
        Assert.Throws<ArgumentException>(() => proj.Volumes(@"C:\out", [[0, 1], [1, 2, 3]], probed));
    }

    [Fact]
    public void Project_ResolvePaths_FindsMovedVideoBesideProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs_unit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var moved = Path.Combine(dir, "movie.mkv");
            File.WriteAllText(moved, "x");
            var proj = new Project("bd", [new DiscTitle(@"Z:\old\drive\movie.mkv")]);
            var resolved = proj.ResolvePaths(dir);
            Assert.Equal(moved, resolved.Titles![0].Video);
        }
        finally { Tools.TryDelete(dir); }
    }

    [Fact]
    public void Tracks_LineUpAudio_ReordersReversedEpisodeAndKeepsMonolingualBonus()
    {
        var s1 = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 100, [new(0, "ac3", 6, "fre", ""), new(1, "ac3", 6, "eng", "")], []);
        var s2 = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 100, [new(0, "ac3", 6, "eng", ""), new(1, "ac3", 6, "fre", "")], []);
        var sb = new SourceInfo("h264", 1920, 1080, 24, "ac3", 2, 60, [new(0, "ac3", 2, "jpn", "")], []);
        var t1 = new DiscTitle("e1.mkv", Audio: [0, 1]);
        var t2 = new DiscTitle("e2.mkv", Audio: [0, 1]);
        var tb = new DiscTitle("b.mkv", Audio: [0], Bonus: true);

        var lined = DiscTitle.LineUpAudio([t1, t2, tb], [s1, s2, sb]);
        Assert.Equal(["fre", "eng"], lined[1].Select(a => a.Lang));
        Assert.Equal(["jpn"], lined[2].Select(a => a.Lang));
    }

    [Fact]
    public void PgsStream_Transform_LiftsScalesAndCalibratesHdrLuma()
    {
        // Build minimal valid PGS display set (PCS + WDS + PDS + ODS + END)
        var pcs = new byte[19];
        BinaryPrimitives.WriteUInt16BigEndian(pcs.AsSpan(0), 1920);
        BinaryPrimitives.WriteUInt16BigEndian(pcs.AsSpan(2), 1080);
        pcs[10] = 1; // 1 composition object
        BinaryPrimitives.WriteUInt16BigEndian(pcs.AsSpan(15), 800); // X
        BinaryPrimitives.WriteUInt16BigEndian(pcs.AsSpan(17), 900); // Y

        var wds = new byte[10];
        wds[0] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(wds.AsSpan(2), 800);
        BinaryPrimitives.WriteUInt16BigEndian(wds.AsSpan(4), 900);
        BinaryPrimitives.WriteUInt16BigEndian(wds.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt16BigEndian(wds.AsSpan(8), 4);

        var pds = new byte[] { 0, 0, 1, 235, 128, 128, 255 }; // entry 1: peak white Y=235

        var pixels = Enumerable.Repeat((byte)1, 16).ToArray();
        var packed = PgsStream.Pack(pixels, 4, 4);
        var ods = new byte[11 + packed.Length];
        ods[3] = 0xC0;
        var dataLen = packed.Length + 4;
        ods[4] = (byte)(dataLen >> 16); ods[5] = (byte)(dataLen >> 8); ods[6] = (byte)dataLen;
        BinaryPrimitives.WriteUInt16BigEndian(ods.AsSpan(7), 4);
        BinaryPrimitives.WriteUInt16BigEndian(ods.AsSpan(9), 4);
        packed.CopyTo(ods, 11);

        using var ms = new MemoryStream();
        ms.Write(PgsStream.Segment(0x16, 90000, 90000, pcs));
        ms.Write(PgsStream.Segment(0x17, 90000, 90000, wds));
        ms.Write(PgsStream.Segment(0x14, 90000, 90000, pds));
        ms.Write(PgsStream.Segment(0x15, 90000, 90000, ods));
        ms.Write(PgsStream.Segment(0x80, 90000, 90000, []));

        var transformed = PgsStream.Transform(ms.ToArray(), liftPercent: 20, scalePercent: 150, hdrNits: 203);
        // Check PCS Y shifted up from 900 by 20 * 1080 / 200 = 108 -> 792
        var newY = BinaryPrimitives.ReadUInt16BigEndian(transformed.AsSpan(13 + 17));
        Assert.Equal(792, newY);
        // Verify HDR text calibration helper too
        Assert.Equal("818181", BluRay.ScaleHexRgbForHdr("FFFFFF", 203));
        Assert.Equal("FFFFFF", BluRay.ScaleHexRgbForHdr("FFFFFF", 400));
    }

    [Fact]
    public void DiscSimulator_RunsUnifiedFlowAcrossBdjHdmvAndDvd()
    {
        var pages = new List<MenuPage>
        {
            new("root", [], [new("play", new(0, 0, 100, 40), new("play", null, 1, 0)) { Down = "scenes" }, new("scenes", new(0, 50, 100, 40), new("page", "scenes1", 0, 0)) { Up = "play" }]),
            new("scenes1", [], [new("back", new(0, 0, 100, 40), new("page", "root", 0, 0))])
        };

        var sim = new DiscSimulator(pages, new PlaybackOptions(PlayAll: true, Rewind: true, RememberPosition: true), "BD-J", mainCount: 2);
        sim.StartDisc();
        Assert.Equal("root", sim.CurrentPageId);
        sim.Press("Enter"); // plays title 1
        Assert.Equal(1, sim.PlayingTitle);
        sim.Press("Popup");
        Assert.True(sim.PopupOpen);
        sim.Press("End"); // chains to title 2
        Assert.Equal(2, sim.PlayingTitle);
        sim.Press("Menu"); // saves resume point and returns to menu
        Assert.Equal("root", sim.CurrentPageId);
        Assert.NotNull(sim.ResumePoint);
        sim.StartDisc(); // resumes title 2
        Assert.Equal(2, sim.PlayingTitle);

        var dvdSim = new DiscSimulator(pages, new PlaybackOptions(), "DVD");
        dvdSim.StartDisc();
        dvdSim.Press("Enter");
        dvdSim.Press("Popup");
        Assert.False(dvdSim.PopupOpen);
    }

    [Fact]
    public void PsdReader_ParsesValidRawRgbPsd()
    {
        // Minimal 2x2 8-bit RGB PSD (header + empty sections + raw composite)
        using var ms = new MemoryStream();
        ms.Write("8BPS"u8);
        ms.Write([0, 1]); // version 1
        ms.Write(new byte[6]); // reserved
        ms.Write([0, 3]); // 3 channels (R, G, B)
        ms.Write([0, 0, 0, 2]); // height 2
        ms.Write([0, 0, 0, 2]); // width 2
        ms.Write([0, 8]); // 8-bit depth
        ms.Write([0, 3]); // RGB color mode
        ms.Write([0, 0, 0, 0]); // color mode data len = 0
        ms.Write([0, 0, 0, 0]); // image resources len = 0
        ms.Write([0, 0, 0, 0]); // layer & mask len = 0
        ms.Write([0, 0]); // raw compression
        ms.Write([255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0]); // 4 red pixels

        var (composite, layers) = PsdReader.Parse(ms.ToArray());
        Assert.NotNull(composite);
        Assert.True(composite!.Length > 8);
        Assert.Empty(layers);
    }

    [Fact]
    public void Nav_PopupOffAndLinkMark_EncodeExpectedOpcodes()
    {
        var popupOff = Nav.PopupOff();
        Assert.Equal(Nav.Size, popupOff.Length);
        Assert.Equal(0x11, popupOff[0]);
        Assert.Equal(0x0B, popupOff[3]);

        var linkMark = Nav.LinkMark(3);
        Assert.Equal(0x22, linkMark[0]);
        Assert.Equal(0x85, linkMark[1]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(linkMark.AsSpan(4)));
    }

    [Fact]
    public void BluRay_BdAudio_ReencodesWhenLoudnessNormalizationEnabled()
    {
        var ac3 = new AudioTrack(0, "ac3", 6, "fre", "", 48000, 640);
        var thd = new AudioTrack(1, "truehd", 6, "eng", "", 48000, 4500, Lossless: true);
        var title = new DiscTitle("film.mkv");

        Assert.Equal("ac3", BluRay.BdAudio(ac3, title, uhd: false, lossless: true, copyEac3: true, normalize: false));
        Assert.Equal("lpcm", BluRay.BdAudio(ac3, title, uhd: false, lossless: true, copyEac3: true, normalize: true));
        Assert.Equal("ac3", BluRay.BdAudio(ac3, title, uhd: false, lossless: false, copyEac3: true, normalize: true));
        Assert.Equal("truehd", BluRay.BdAudio(thd, title, uhd: false, lossless: true, copyEac3: true, normalize: false));
        Assert.Equal("lpcm", BluRay.BdAudio(thd, title, uhd: false, lossless: true, copyEac3: true, normalize: true));
    }

    [Fact]
    public void MenuRenderer_PopupButtonAndHdrImageScaling_ProduceExpectedPixels()
    {
        MenuRenderer.Sta(() =>
        {
            var normal = MenuRenderer.PopupButtonPixels("Reprendre", 200, 64, 180, false, "Segoe UI", "#E50914");
            var focused = MenuRenderer.PopupButtonPixels("Reprendre", 200, 64, 220, true, "Segoe UI", "#E50914");
            Assert.Equal(200 * 64, normal.Length);
            Assert.NotEqual(normal, focused);
            // Verify white text pixels exist inside the button interior
            Assert.Contains(normal, px => (px >> 24) > 200 && ((px >> 16) & 255) > 220 && ((px >> 8) & 255) > 220);

            var design = new MenuDesign { Title = "HDR" };
            var page = new MenuPage("root", [new Label("HDR", new(100, 100, 400, 80), 48, true)], []);
            var png = MenuRenderer.Overlay(design, page, 0.25);
            var calibrated = MenuRenderer.ScaleImageForHdr(png, 203, jpeg: false);
            Assert.True(calibrated.Length > 0);
            Assert.NotEqual(png, calibrated);
            return true;
        });
    }

    [Fact]
    public void ChapterFiles_ParsesOgmXmlAndSimpleAndRoundTrips()
    {
        var ogm = """
            CHAPTER01=00:00:00.000
            CHAPTER01NAME=Introduction
            CHAPTER02=00:05:30.500
            CHAPTER02NAME=Le duel
            """;
        var parsedOgm = ChapterFiles.Parse(ogm);
        Assert.Equal(2, parsedOgm.Count);
        Assert.Equal(0, parsedOgm[0].Time);
        Assert.Equal("Introduction", parsedOgm[0].Name);
        Assert.Equal(330.5, parsedOgm[1].Time);
        Assert.Equal("Le duel", parsedOgm[1].Name);

        var xml = """
            <Chapters><EditionEntry>
              <ChapterAtom><ChapterTimeStart>00:00:00.000000000</ChapterTimeStart><ChapterDisplay><ChapterString>Prologue</ChapterString></ChapterDisplay></ChapterAtom>
              <ChapterAtom><ChapterTimeStart>00:12:15.250000000</ChapterTimeStart><ChapterDisplay><ChapterString>Final</ChapterString></ChapterDisplay></ChapterAtom>
            </EditionEntry></Chapters>
            """;
        var parsedXml = ChapterFiles.Parse(xml);
        Assert.Equal(2, parsedXml.Count);
        Assert.Equal("Prologue", parsedXml[0].Name);
        Assert.Equal(735.25, parsedXml[1].Time);

        var exported = ChapterFiles.FormatOgm([0, 330.5], ["Introduction", "Le duel"]);
        var roundTrip = ChapterFiles.Parse(exported);
        Assert.Equal(parsedOgm, roundTrip);
    }

    [Fact]
    public void PgsSubtitles_CloseGaps_InjectsClearSetAtGapBoundary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs_pgs_gap_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var supPath = Path.Combine(dir, "sub.sup");
            var writer = new PgsSubtitles.Writer(1920, 1080);
            // Cue stays visible across [4.0, 12.0]; gap removes [6.0, 10.0], so clear set at 12.0 falls outside part 1 [0..6]
            writer.Add(4.0, 12.0, 100, 900, 8, 8, Enumerable.Repeat((byte)1, 64).ToArray(), [(0, 0, 0, 0), (255, 255, 255, 255)]);
            File.WriteAllBytes(supPath, writer.Finish());

            var title = new DiscTitle("clip.mkv", Clips: [new Clip(0, 6), new Clip(10, 15)]);
            title.CloseGaps(supPath, 15);
            var closed = File.ReadAllBytes(supPath);
            Assert.False(PgsSubtitles.EndsVisible(closed));
        }
        finally { Tools.TryDelete(dir); }
    }

    [Fact]
    public void VideoEffects_DeinterlaceAndDenoise_EmitExpectedFfmpegFilters()
    {
        var fx = new VideoEffects(Deinterlace: "on", Denoise: 50);
        Assert.False(fx.IsEmpty);
        var vf = fx.Vf(1920, 1080, 0, "scale=1920:1080");
        Assert.Contains("bwdif=mode=0:parity=auto:deint=1", vf);
        Assert.Contains("hqdn3d=", vf);
    }

    [Fact]
    public void Iso_IsVirtualDriveName_FiltersKnownVirtualDrivers()
    {
        Assert.True(Iso.IsVirtualDriveName("Msft Virtual DVD-ROM"));
        Assert.True(Iso.IsVirtualDriveName("ELBY CLONEDRIVE SCSI"));
        Assert.True(Iso.IsVirtualDriveName("DTSOFT BDROM"));
        Assert.False(Iso.IsVirtualDriveName("HL-DT-ST BD-RE BU40N"));
        Assert.False(Iso.IsVirtualDriveName("PIONEER BD-RW BDR-XD08"));
    }

    [Fact]
    public void BluRay_NeededBytes_AccountsForOutputAndLargestWorkClip()
    {
        var ep = new SourceInfo("h264", 1920, 1080, 24, "ac3", 6, 3600, [new(0, "ac3", 6, "fre", "")], []);
        var job = new BluRayJob([new DiscTitle("a.mkv"), new DiscTitle("b.mkv")], @"C:\out",
            Encode: new EncodeOptions(BdKbps: 10000));
        var needed = BluRay.NeededBytes(job, [ep, ep]);
        var discBytes = (long)(BluRay.EstimateGB(job, [ep, ep]) * 1e9);
        Assert.True(needed > discBytes * 1.4);
    }

    [Fact]
    public void DvdAuthoring_MultiVts_GeneratesValidTwoVtsDiscAndUdfImage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs_mvts_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var mpg = Path.Combine(dir, "t.mpg");
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-f", "lavfi", "-i", "color=s=720x576:r=25:d=2",
                "-f", "lavfi", "-i", "sine=f=440:r=48000:d=2", "-target", "pal-dvd", "-b:v", "2000k", "-b:a", "192k", mpg]);

            var outDir = Path.Combine(dir, "disc");
            var vts1Chain = new Chain(mpg, [0.0], [], [Vm.Set(8, 2), Vm.CallTitleMenu()]);
            var vts2Chain = new Chain(mpg, [0.0], [], [Vm.Exit()]);
            var disc = new DvdDisc(
                Pal: true,
                Menus: [],
                Titles: [vts1Chain],
                FirstPlay: [Vm.Set(8, 0), Vm.JumpTitle(1)],
                Audio: [new AudioStream(2, "fr")],
                Subtitles: [],
                Wide: true,
                ExtraSets: [new TitleSet([vts2Chain], [new AudioStream(2, "ja")], [], Wide: true)]);

            Authoring.Write(disc, outDir);
            Directory.CreateDirectory(Path.Combine(outDir, "AUDIO_TS"));

            var problems = DiscCheck.Folder(outDir);
            Assert.Empty(problems);
            Assert.True(File.Exists(Path.Combine(outDir, "VIDEO_TS", "VTS_01_0.IFO")));
            Assert.True(File.Exists(Path.Combine(outDir, "VIDEO_TS", "VTS_02_0.IFO")));
            var vmg = File.ReadAllBytes(Path.Combine(outDir, "VIDEO_TS", "VIDEO_TS.IFO"));
            Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(0x3E)));

            var iso = Path.Combine(dir, "mvts.iso");
            var udfProblems = UdfImage.Write(outDir, iso, "MVTS_TEST");
            Assert.Empty(udfProblems);
            Assert.Empty(DiscCheck.Image(iso, outDir));
        }
        finally { Tools.TryDelete(dir); }
    }

    [Fact]
    public void Translations_EnTsv_CoversAllVisibleXamlStrings()
    {
        // Walk up from bin directory to repository root
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BurnSmith.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var appDir = Path.Combine(root!.FullName, "src", "BurnSmith.App");
        var tsvPath = Path.Combine(appDir, "en.tsv");
        Assert.True(File.Exists(tsvPath));

        var translated = File.ReadAllLines(tsvPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .Select(l => l.Split('\t')[0])
            .ToHashSet(StringComparer.Ordinal);
        var exempt = new HashSet<string>(StringComparer.Ordinal)
        {
            "BurnSmith", "OK", "Kbps", "NTSC", "PAL", "16:9", "4:3", "1:1", "BD-J", "HDMV",
            "DVD", "Blu-ray", "Blu-ray 4K", "AVCHD", "HDR", "Audio", "x", "×", "G", "I", "S", "Français", "English"
        };
        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(appDir, "*.xaml"))
        {
            if (Path.GetFileName(file).Equals("App.xaml", StringComparison.OrdinalIgnoreCase)) continue;
            var doc = XDocument.Load(file);
            foreach (var elem in doc.Descendants())
                foreach (var attrName in new[] { "Title", "Header", "Content", "Text", "ToolTip" })
                {
                    var val = elem.Attribute(attrName)?.Value;
                    if (string.IsNullOrEmpty(val) || val.StartsWith('{') || val.Length <= 1) continue;
                    if (!Regex.IsMatch(val, "[A-Za-zÀ-ÿ]{2,}") || exempt.Contains(val)) continue;
                    if (!translated.Contains(val)) missing.Add($"{Path.GetFileName(file)}: {val}");
                }
        }
        Assert.True(missing.Count == 0, "Untranslated XAML strings missing in en.tsv:\n" + string.Join('\n', missing.Distinct()));
    }

    [Fact]
    public void DiscCheck_ValidatesUhdVersionConsistencyAndBluRayAudioSampleRate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs_bdchk_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var bdmv = Path.Combine(dir, "BDMV");
            foreach (var sub in new[] { "BACKUP", "PLAYLIST", "CLIPINF", "STREAM" })
                Directory.CreateDirectory(Path.Combine(bdmv, sub));

            // Empty CLPI (no declared streams)
            var clpi = new BinaryWriterBE();
            clpi.Str("HDMV0300", 8); clpi.Zero(4); clpi.U32(16);
            clpi.U32(2); clpi.Zero(2);
            File.WriteAllBytes(Path.Combine(bdmv, "CLIPINF", "00001.clpi"), clpi.ToArray());

            // Minimal 192-byte M2TS packets carrying a PES start on PID 0x1100 (audio) so transport check passes
            var packets = new byte[192 * 4];
            for (var k = 0; k < 4; k++)
            {
                packets[192 * k + 4] = 0x47;
                packets[192 * k + 5] = (byte)(0x40 | (0x1100 >> 8)); // PUSI + PID high
                packets[192 * k + 6] = 0x00;                         // PID low
                packets[192 * k + 7] = 0x10;                         // payload only
                packets[192 * k + 8] = 0x00;
                packets[192 * k + 9] = 0x00;
                packets[192 * k + 10] = 0x01;
                packets[192 * k + 11] = 0xBD;
            }
            File.WriteAllBytes(Path.Combine(bdmv, "STREAM", "00001.m2ts"), packets);

            static byte[] MakeMpls(byte audioRateCode)
            {
                // PlayItem with 1 AC-3 audio stream on PID 0x1100
                var stn = new BinaryWriterBE();
                stn.U16(0); // length placeholder
                stn.U16(0); // reserved
                stn.U8(0); stn.U8(1); stn.U8(0); stn.U8(0); // 0 video, 1 audio, 0 pg, 0 ig
                stn.Zero(8); // secondary counts + reserved
                // stream_entry: len=9, type=1, pid=0x1100, pad
                stn.U8(9); stn.U8(1); stn.U16(0x1100); stn.Zero(6);
                // stream_attributes: len=5, coding=0x81 (AC-3), pres=3|rate, lang="fre"
                stn.U8(5); stn.U8(0x81); stn.U8((3 << 4) | (audioRateCode & 15)); stn.Str("fre", 3);
                var stnBytes = stn.ToArray();
                BinaryPrimitives.WriteUInt16BigEndian(stnBytes.AsSpan(0), (ushort)(stnBytes.Length - 2));

                var item = new BinaryWriterBE();
                item.U16(0); // length placeholder
                item.Str("00001M2TS", 9);
                item.Zero(3); // flags + stc
                item.U32(45000); item.U32(90000); // in / out
                item.Zero(12); // UO mask + flags + still
                item.Bytes(stnBytes);
                var itemBytes = item.ToArray();
                BinaryPrimitives.WriteUInt16BigEndian(itemBytes.AsSpan(0), (ushort)(itemBytes.Length - 2));

                var playList = new BinaryWriterBE();
                playList.U32(6 + itemBytes.Length);
                playList.U16(0); playList.U16(1); playList.U16(0);
                playList.Bytes(itemBytes);
                var playListBytes = playList.ToArray();

                var marksAt = 40 + playListBytes.Length;
                var mpls = new BinaryWriterBE();
                mpls.Str("MPLS0300", 8);
                mpls.U32(40); mpls.U32(marksAt); mpls.Zero(24);
                mpls.Bytes(playListBytes);
                mpls.U32(2); mpls.U16(0); // empty marks
                return mpls.ToArray();
            }

            // Build UHD INDX0300 + MOBJ0300
            var index = MovieObjectFile.Index([0], 0, ushort.MaxValue, 6, 1);
            "INDX0300"u8.CopyTo(index);
            var objects = MovieObjectFile.Write([[Nav.PlayPlaylist(1)]], "0300");
            foreach (var folder in new[] { bdmv, Path.Combine(bdmv, "BACKUP") })
            {
                File.WriteAllBytes(Path.Combine(folder, "index.bdmv"), index);
                File.WriteAllBytes(Path.Combine(folder, "MovieObject.bdmv"), objects);
            }
            File.WriteAllBytes(Path.Combine(bdmv, "PLAYLIST", "00001.mpls"), MakeMpls(1)); // 48 kHz

            Assert.Empty(DiscCheck.Folder(dir));

            // Mismatched MOBJ0200 on UHD INDX0300 disc must be flagged
            var mobj0200 = MovieObjectFile.Write([[Nav.PlayPlaylist(1)]], "0200");
            File.WriteAllBytes(Path.Combine(bdmv, "MovieObject.bdmv"), mobj0200);
            File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", "MovieObject.bdmv"), mobj0200);
            Assert.Contains(DiscCheck.Folder(dir), p => p.Contains("MovieObject.bdmv") && p.Contains("UHD"));

            // Restore MOBJ0300 and inject invalid audio sample rate code (e.g. 2, not in 48/96/192 kHz)
            File.WriteAllBytes(Path.Combine(bdmv, "MovieObject.bdmv"), objects);
            File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", "MovieObject.bdmv"), objects);
            File.WriteAllBytes(Path.Combine(bdmv, "PLAYLIST", "00001.mpls"), MakeMpls(2));
            Assert.Contains(DiscCheck.Folder(dir), p => p.Contains("fréquence d'échantillonnage non conforme Blu-ray"));
        }
        finally { Tools.TryDelete(dir); }
    }

    [Fact]
    public void MenuBuilder_NativeOverridesConvertToFreeAndBlankWorkCleanly()
    {
        var content = new MenuContent(1, null, Enumerable.Repeat<string?>(null, 4).ToList(), null, ["Français 5.1", "English 5.1"], ["Français"]);
        var design = BuiltInTemplates.All().First();
        design.Title = "Mon Film";

        // 1. Native override moves & restyles Play button and hides Settings button
        design.NativeOverrides.Add(new NativeElementOverride { Page = "root", Id = "play", X = 120, Y = 500, W = 500, H = 130, Text = "Lancer" });
        design.NativeOverrides.Add(new NativeElementOverride { Page = "root", Id = "settings", Hidden = true });
        design.DefaultButtons["root"] = "scenes";

        var pages = MenuBuilder.Build(design, content);
        var root = pages[0];
        Assert.Equal("scenes", root.Buttons[0].Id); // DefaultButtons promotes to index 0
        Assert.DoesNotContain(root.Buttons, b => b.Id == "settings");
        var playBtn = root.Buttons.First(b => b.Id == "play");
        Assert.Equal(new Box(120, 500, 500, 130), playBtn.Rect);
        Assert.Contains(root.Ops.Select(o => o is StyledOp s ? s.Inner : o).OfType<Label>(), l => l.Value == "Lancer");

        // 2. ConvertPageToFree converts remaining root elements and clears NativeOverrides
        MenuBuilder.ConvertPageToFree(design, root);
        Assert.Contains("root", design.FreePages);
        Assert.Empty(design.NativeOverrides);
        Assert.Contains(design.Elements, e => e.IsButton && e.NavigationId == "play" && e.X == 120);

        var rebuilt = MenuBuilder.Build(design, content)[0];
        Assert.Equal(root.Buttons.Count, rebuilt.Buttons.Count);

        // 3. Blank template produces free root page with Play button and editable title
        var blank = BuiltInTemplates.Blank("Test Vierge");
        var blankRoot = MenuBuilder.Build(blank, content)[0];
        Assert.Single(blankRoot.Buttons);
        Assert.Contains(blankRoot.Ops.Select(o => o is StyledOp s ? s.Inner : o).OfType<Text>(), t => t.Value == "Test Vierge");
    }

    [Fact]
    public void MenuValidator_DetectsOverlapDvdLimitOrphanAndSafeArea()
    {
        var d = new MenuDesign { Name = "Test 16-9" };
        // Overlapping buttons
        var b1 = new MenuButton("b1", new Box(100, 100, 300, 100), new MenuAction("play", Playlist: 1)) { Right = "b2" };
        var b2 = new MenuButton("b2", new Box(200, 120, 300, 100), new MenuAction("play", Playlist: 1)) { Left = "b1" };
        // Orphan button (not reachable from b1) & outside safe area (X=10)
        var b3 = new MenuButton("b3", new Box(10, 400, 200, 80), new MenuAction("page", Page: "root"));
        var page = new MenuPage("root", [], [b1, b2, b3]);

        var issues = MenuValidator.Validate(d, page, "dvd");
        Assert.Contains(issues, i => i.Kind == "overlap");
        Assert.Contains(issues, i => i.Kind == "orphan" && i.ButtonId == "b3");
        Assert.Contains(issues, i => i.Kind == "safe");

        // DVD 16:9 limit > 18 buttons
        var twenty = Enumerable.Range(0, 20).Select(k =>
            new MenuButton($"k{k}", new Box(120 + (k % 5) * 320, 100 + (k / 5) * 200, 200, 80), new MenuAction("play", Playlist: 1))).ToList();
        var limitIssues = MenuValidator.Validate(d, new MenuPage("scenes1", [], twenty), "dvd");
        Assert.Equal(2, limitIssues.Count(i => i.Kind == "limit"));
    }

    [Fact]
    public void SoundBank_BuildSoundBdmvAndWav_EncodeValidHeaders()
    {
        var snd = new MenuSoundSettings { Pack = "cinema", Volume = 85 };
        var bdmv = SoundBank.BuildSoundBdmv(snd);
        Assert.True(bdmv.Length > 80);
        Assert.Equal("BCLK0200", System.Text.Encoding.ASCII.GetString(bdmv, 0, 8));
        Assert.Equal(2, bdmv[45]); // 2 sound entries (move + select)

        var wav = SoundBank.BuildWav(snd, true);
        Assert.True(wav.Length > 44);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(wav, 8, 8));
    }

    [Fact]
    public void MenuBuilder_PopupStyle_CustomDockAndButtonsApplied()
    {
        var d = new MenuDesign
        {
            Title = "Pop Test",
            Popup = true,
            PopupLayout = new PopupStyle
            {
                Dock = "left",
                ButtonWidth = 300,
                ButtonHeight = 90,
                ShowChapters = false,
                ShowSettings = false,
                Fill = "#112233",
            },
        };
        var content = new MenuContent(1, null, [null, null], null, ["FR", "EN"], ["FR"]);
        var pages = MenuBuilder.Popup(d, content, [(1, 2, ["FR", "EN"], [("FR", 1)])]);
        var pop = pages.First(p => p.Id == "pop1");
        Assert.Equal(2, pop.Buttons.Count); // only Resume and MainMenu (Chapters & Settings disabled)
        Assert.All(pop.Buttons, b => Assert.Equal(300, b.Rect.W));
        Assert.Contains(pop.Ops.OfType<Fill>(), f => f.Color == "#112233" && f.Rect.X == 0 && f.Rect.H == MenuBuilder.H);
    }

    [Fact]
    public void MenuRenderer_StyledOp_Activated_And_CursorFrames_Render()
    {
        var d = new MenuDesign { Title = "FX", CursorPreset = "ring", Accent = "#38BDF8" };
        var frames = MenuRenderer.BuiltInCursorFrames("ring", "#38BDF8");
        Assert.NotNull(frames);
        Assert.Equal(6, frames!.Length);
        Assert.All(frames, f => Assert.True(File.Exists(f)));

        var btn = new MenuButton("play", new Box(200, 800, 360, 100), new MenuAction("play", Playlist: 1));
        var op = new MenuElement
        {
            Kind = "text", Text = "Bonjour", X = 150, Y = 200, W = 600, H = 120, Size = 60,
            ElementOpacity = 80, Rotation = 15, ShadowColor = "#000000", ShadowBlur = 6, ShadowOffsetX = 4, ShadowOffsetY = 4,
        }.Ops(d).First();
        var page = new MenuPage("root", [op], [btn]);
        var (bg, act) = MenuRenderer.Sta(() => (MenuRenderer.Background(d, page), MenuRenderer.Activated(d, page, btn)));
        Assert.True(bg.Length > 500);
        Assert.True(act.Length > 200);
    }

    [Fact]
    public void Templates_FolderAutonomousLifecycle_SaveRenameDuplicateDelete()
    {
        var uniq = "unit-" + Guid.NewGuid().ToString("N")[..8];
        var copyName = uniq + "-copy";
        var renamedName = uniq + "-renamed";
        var tmpImg = Path.Combine(Path.GetTempPath(), uniq + ".png");
        // 1x1 PNG
        File.WriteAllBytes(tmpImg, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        try
        {
            var d = BuiltInTemplates.Blank(uniq);
            d.Name = uniq;
            d.BackgroundImage = tmpImg;
            Templates.Save(d);
            var folder = Path.Combine(Templates.Dir, uniq);
            Assert.True(File.Exists(Path.Combine(folder, Templates.FileName)));
            Assert.True(File.Exists(Path.Combine(folder, Templates.ThumbName)));
            Assert.True(Directory.EnumerateFiles(Path.Combine(folder, "media")).Any());

            var dup = Templates.Duplicate(uniq, copyName);
            Assert.Equal(copyName, dup.Name);
            Templates.Rename(copyName, renamedName);
            Assert.DoesNotContain(Templates.Entries(), e => e.Name == copyName);
            Assert.Contains(Templates.Entries(), e => e.Name == renamedName);
        }
        finally
        {
            Templates.Delete(uniq);
            Templates.Delete(copyName);
            Templates.Delete(renamedName);
            try { if (File.Exists(tmpImg)) File.Delete(tmpImg); } catch (IOException) { }
        }
    }
}
