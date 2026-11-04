// Self-check of BurnSmith.Core.Hdmv. No test framework (nothing to download): plain asserts, exit code = number of failures.
//   unit                         pure checks, no external tool
//   bdmvkit <run-dir> <work>     the IG stream and the navigation files against what bdmvkit wrote (<run-dir> = bdmvkit-run with its out\BDMV)
//   disc <run-dir> <out>         builds a complete disc from the bdmvkit inputs, compares its files with bdmvkit's
//   addmenu <bdmv> <run-dir> [default|zero|reference] [none|menu]   adds the menu to a BDMV folder that holds a movie (BurnSmith.Cli ... --no-menu)
//   synth <dir> / synthcmp <dir> writes the synthetic gradient buttons as raw RGBA / compares our .sup with the ref.sup that bdmvkit made from the same files
//   palette <menu-dir>           bdmvkit's quantizer against the median cut on the hl_*.png BurnSmith renders (menu-dir = unzipped BD-J menu jar)
//   realmenu <menu-dir> <bdmv> [palette]   BurnSmith's own rendered menu (menu.txt, bg_*.jpg, hl_*.png) as HDMV pages on a BDMV folder that holds the movie
//   pes <m2ts> [pid]             prints the PTS / DTS of the PES packets of a PID (default 0x1400)
using System.Globalization;
using BurnSmith.Core;
using BurnSmith.Core.Hdmv;
using BurnSmith.HdmvCheck;

static class Program
{
    static int failures;

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) failures++;
        Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}{(ok || detail.Length == 0 ? "" : "\n       " + detail.Replace("\n", "\n       "))}");
    }

    static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "unit";
        try
        {
            switch (mode)
            {
                case "unit": Unit(); break;
                case "bdmvkit": Unit(); AgainstBdmvkit(args[1], args[2]); break;
                case "disc": BuildDisc(args[1], args[2]); break;
                case "addmenu": AddMenu(args[1], args[2], args.Length > 3 ? args[3] : "zero", args.Length > 4 ? args[4] : "none"); break;
                                case "synth": Directory.CreateDirectory(args[1]); foreach (var b in SyntheticButtons()) { File.WriteAllBytes(Path.Combine(args[1], b.Id + "-nor.rgba"), b.Normal!); File.WriteAllBytes(Path.Combine(args[1], b.Id + "-sel.rgba"), b.Selected); File.WriteAllBytes(Path.Combine(args[1], b.Id + "-act.rgba"), b.Activated!); } break;
                case "synthcmp": SynthCompare(args[1]); break;
                case "palette": PaletteReport(args[1]); break;
                case "realmenu": RealMenu(args[1], args[2], args.Length > 3 ? args[3] : "bdmvkit"); break;
                case "pes": Pes(args[1], args.Length > 2 ? Convert.ToInt32(args[2], 16) : 0x1400); break;
                default: Console.WriteLine("modes: unit | bdmvkit <run-dir> <work> | disc <run-dir> <out> | addmenu <bdmv> <run-dir> [default|zero|reference] [none|menu] | pes <m2ts> [pid]"); return 2;
            }
        }
        catch (Exception e) { Console.WriteLine("ERROR " + e); return 99; }
        Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} check(s) FAILED");
        return failures;
    }

    // ---------------------------------------------------------------- pure checks

    static byte[] Pic(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> f)
    {
        var p = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) { var (r, g, b, a) = f(x, y); var i = (y * w + x) * 4; p[i] = r; p[i + 1] = g; p[i + 2] = b; p[i + 3] = a; }
        return p;
    }

    static void Unit()
    {
        Check("CRC-32/MPEG-2 of '123456789'", TransportStream.Crc32("123456789"u8) == 0x0376E6E7u);
        Check("YCbCr white / black (BT.709 studio range)", Pgs.YCbCr(255, 255, 255) == (235, 128, 128) && Pgs.YCbCr(0, 0, 0) == (16, 128, 128));
        Check("HDMV commands: JUMP_TITLE 1 and JUMP_OBJECT 4 as bdmvkit writes them",
              Convert.ToHexString(HdmvCommand.JumpTitle(1)) == "218100000000000100000000" && Convert.ToHexString(HdmvCommand.JumpObject(4)) == "218000000000000400000000");
        Check("UO mask: menu key = bits 0 and 24; the reference page mask is 3cb805ff40000000",
              UoMask.Mask(["menu"]) == (1UL << 63 | 1UL << 39) && UoMask.MenuReference == 0x3cb805ff40000000UL);

        // RLE round trip on pictures with long runs (>63, >16383), short runs (1, 2, 3) and zeros
        var rnd = new Random(7);
        foreach (var (w, h) in new[] { (1, 1), (7, 5), (64, 3), (65, 2), (20000, 2), (300, 40) })
        {
            var idx = new byte[w * h];
            for (var y = 0; y < h; y++)
            {
                var x = 0;
                while (x < w)
                {
                    var run = rnd.Next(5) switch { 0 => 1, 1 => 2, 2 => 3, 3 => rnd.Next(60, 70), _ => rnd.Next(1, 20000) };
                    var c = (byte)(rnd.Next(3) == 0 ? 0 : rnd.Next(1, 256));
                    for (var k = 0; k < run && x < w; k++) idx[y * w + x++] = c;
                }
            }
            var back = Pgs.Unrle(Pgs.Rle(idx, w, h), w, h);
            Check($"RLE round trip {w}x{h}", back.AsSpan().SequenceEqual(idx));
        }

        // quantizer: few colours stay exact, many are reduced to at most 255, transparent pixels are index 0
        var flat = Pic(20, 10, (x, y) => x < 10 ? ((byte)200, (byte)10, (byte)10, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        var (fp, fi) = IgStream.Quantize([flat]);
        Check("quantize: two-region picture = 1 colour, exact, transparent = 0", fp.Count == 1 && fp[0] == (200, 10, 10, 255) && fi[0][0] == 1 && fi[0][19] == 0);
        var grad = Gradient(300, 60);
        var (gp, gi) = IgStream.Quantize([grad, flat.Length == 0 ? grad : grad]);
        Check($"quantize: gradient with thousands of colours -> {gp.Count} palette entries (<= 255), indexes in range",
              gp.Count is > 0 and <= 255 && gi[0].All(i => i <= gp.Count));
        long err = 0; var seen = 0;
        for (var i = 0; i < grad.Length; i += 4)
            if (grad[i + 3] != 0) { var e = gi[0][i / 4]; Check2(e != 0); var c = gp[e - 1]; err += Math.Abs(c.R - grad[i]) + Math.Abs(c.G - grad[i + 1]); seen++; }
        Check($"quantize: mean R+G error of the gradient {(double)err / seen:0.0} (informative; bdmvkit's quantizer is kept as it is)", seen > 0);
        Check("quantize: a pixel that is transparent in the source stays index 0", Enumerable.Range(0, 300 * 60).All(i => grad[i * 4 + 3] != 0 || gi[0][i] == 0));

        // a whole IG stream, decoded back
        var buttons = SyntheticButtons();
        var sup = IgStream.Build(buttons, 1);
        var dec = IgDecoded.Decode(IgDecoded.ReadSup(sup));
        Check("IG: ICS fields (1920x1080, frame rate code 1, epoch start, one page, 3 buttons, default 0, page UO mask)",
              dec is { W: 1920, H: 1080, FrameRate: 1, State: 2, Sequence: 0xC0, Pages: 1, DefaultSelected: 0, DefaultActivated: 0xFFFF, Uo: "3cb805ff40000000" } && dec.Buttons.Count == 3 && !dec.UiModel && !dec.StreamModel,
              dec.Dump());
        Check("IG: neighbours computed (column of three: up/down/left/right)",
              dec.Buttons[0].Contains("nav(U,D,L,R)=[0,1,0,0]") && dec.Buttons[1].Contains("nav(U,D,L,R)=[0,2,1,1]") && dec.Buttons[2].Contains("nav(U,D,L,R)=[1,2,2,2]"));
        Check("IG: one command per button, kept as given", dec.Buttons[0].EndsWith("cmds=1 218100000000000100000000") && dec.Buttons[1].EndsWith("218000000000000400000000"));
        Check("IG: 9 objects (normal / selected / activated per button), sizes as the buttons", dec.Objects.Count == 9 && dec.Objects.Select(o => (o.W, o.H)).SequenceEqual(buttons.SelectMany(b => new[] { (b.W, b.H), (b.W, b.H), (b.W, b.H) })));
        var all = buttons.SelectMany(b => new[] { b.Normal!, b.Selected, b.Activated! }).ToList();
        var (pal, indexed) = IgStream.QuantizeMedianCut(all);
        Check("IG (defaults): objects decode (RLE) to the median-cut index pictures", dec.Objects.Select((o, i) => o.Pixels.AsSpan().SequenceEqual(indexed[i])).All(x => x));
        Check($"IG (defaults): palette = {pal.Count} colours + transparent entry 0", dec.Palette.Count == pal.Count + 1 && dec.Palette[0] == (16, 128, 128, 0));
        var bk = IgDecoded.Decode(IgDecoded.ReadSup(IgStream.Build(buttons, 1, IgOptions.BdmvkitCompatible)));
        var (bpal, bindexed) = IgStream.Quantize(all);
        Check($"IG (bdmvkit options): objects decode to bdmvkit-quantizer pictures, palette = {bpal.Count} colours + 1",
              bk.Palette.Count == bpal.Count + 1 && bk.Objects.Select((o, i) => o.Pixels.AsSpan().SequenceEqual(bindexed[i])).All(x => x));
        var withoutNormal = IgDecoded.Decode(IgDecoded.ReadSup(IgStream.Build(buttons.Select(b => new IgButton { Id = b.Id, X = b.X, Y = b.Y, W = b.W, H = b.H, Selected = b.Selected }).ToList(), 1)));
        Check("IG: without normal pictures the normal state is 'no object' (0xFFFF) and there are 2 objects per button", withoutNormal.Objects.Count == 6 && withoutNormal.Buttons[0].Contains("normal=65535-65535"));
        var pop = IgDecoded.Decode(IgDecoded.ReadSup(IgStream.Build(buttons, 1, new IgOptions { Popup = true, Default = "b", FollowSelection = false })));
        Check("IG: pop-up option sets ui_model, Default selects that button", pop.UiModel && pop.DefaultSelected == 1);
        var follow = IgDecoded.Decode(IgDecoded.ReadSup(IgStream.Build(buttons, 1, new IgOptions { FollowSelection = true })));
        Check("IG: follow-selection writes default button 0xFFFF", follow.DefaultSelected == 0xFFFF);
        Check("IG: a button outside the frame is refused", Throws(() => IgStream.Build([new IgButton { Id = "x", X = 1900, Y = 0, W = 100, H = 10, Selected = new byte[4000] }], 1)));

        // reference timeline: numbers measured on the decoded reference disc
        var t = IgStream.Timeline(1920, 1080, [(901, 92)], 150000);
        Check("timeline: ICS PTS - DTS = 23328 ticks (plane), first object 933 ticks, palette dated at the ICS DTS",
              t.Ics.Pts - t.Ics.Dts == 23328 && t.Ods[0].Pts - t.Ods[0].Dts == 933 && t.Pds == t.Ics.Dts && t.Ods[0].Dts == t.Ics.Dts);
        var timed = IgStream.Build(buttons, 1, new IgOptions { Timeline = IgTimeline.Reference, PresentationTicks = 150000 });
        var segs = Headers(timed);
        Check("timeline: Reference writes growing object dates, END at the last object, ICS last in time",
              segs[0].Dts < segs[0].Pts && segs[^1].Pts == segs[^2].Pts && segs.Skip(2).Take(8).Zip(segs.Skip(3).Take(8)).All(p => p.Second.Dts == p.First.Pts) && segs[0].Pts >= segs[^1].Pts);
        var zeroed = Headers(IgStream.Build(buttons, 1, IgOptions.BdmvkitCompatible));
        Check("options: BdmvkitCompatible = every segment dated 0 / 0 and a zero page UO mask", zeroed.All(s => s.Pts == 0 && s.Dts == 0) && IgDecoded.Decode(IgDecoded.ReadSup(IgStream.Build(buttons, 1, IgOptions.BdmvkitCompatible))).Uo == "0000000000000000");
        var (mp, mi) = IgStream.QuantizeMedianCut([grad, grad]);
        Check($"median cut: gradient -> {mp.Count} entries (<= 255), indexes in range, transparent = 0, mean error {Error(grad, mp, mi[0]):0.00} vs bdmvkit {Error(grad, gp, gi[0]):0.00}",
              mp.Count is > 0 and <= 255 && mi[0].All(i => i <= mp.Count) && Enumerable.Range(0, 300 * 60).All(i => grad[i * 4 + 3] != 0 || mi[0][i] == 0) && Error(grad, mp, mi[0]) < Error(grad, gp, gi[0]));
        var (fp2, fi2) = IgStream.QuantizeMedianCut([flat]);
        Check("median cut: a picture with few colours is kept exactly", fp2.Count == 1 && fp2[0] == (200, 10, 10, 255) && fi2[0][0] == 1 && fi2[0][19] == 0);
        var mdec = IgDecoded.Decode(IgDecoded.ReadSup(IgStream.Build(buttons, 1, new IgOptions { Palette = IgPalette.MedianCut })));
        Check("median cut: a whole IG stream with the option decodes (9 objects, palette <= 256 entries)", mdec.Objects.Count == 9 && mdec.Palette.Count <= 256);
        Check("options: the defaults are the reference disc's (dated segments, page UO mask 3cb805ff40000000)", Headers(sup).Any(s => s.Dts != 0) && dec.Uo == "3cb805ff40000000");
    }

    /// <summary>Mean absolute error per channel (r, g, b, a) over the non-transparent pixels.</summary>
    static double Error(byte[] rgba, List<(byte R, byte G, byte B, byte A)> palette, byte[] index)
    {
        double sum = 0; long n = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0) continue;
            var c = index[i / 4] == 0 ? default : palette[index[i / 4] - 1]; // (index 0: a nearly transparent pixel the quantizer dropped)
            sum += (Math.Abs(c.R - rgba[i]) + Math.Abs(c.G - rgba[i + 1]) + Math.Abs(c.B - rgba[i + 2]) + Math.Abs(c.A - rgba[i + 3])) / 4.0;
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    static void PaletteReport(string menuDir)
    {
        var files = Directory.GetFiles(menuDir, "hl_*.png").Order().ToList();
        var all = files.Select(f => MenuClip.ReadRgba(f).Rgba).ToList();
        var (bp, bi) = IgStream.Quantize(all);
        var (mp, mi) = IgStream.QuantizeMedianCut(all);
        Console.WriteLine($"{files.Count} pictures of BurnSmith's menu, quantized together (one palette, as in an IG stream):");
        Console.WriteLine($"  bdmvkit    : {bp.Count} palette entries, mean error per channel {all.Select((im, i) => Error(im, bp, bi[i])).Average():0.000}, worst picture {all.Select((im, i) => Error(im, bp, bi[i])).Max():0.000}");
        Console.WriteLine($"  median cut : {mp.Count} palette entries, mean error per channel {all.Select((im, i) => Error(im, mp, mi[i])).Average():0.000}, worst picture {all.Select((im, i) => Error(im, mp, mi[i])).Max():0.000}");
        Check("palette: median cut never exceeds 255 entries", mp.Count <= 255 && bp.Count <= 255);
    }

    /// <summary>BurnSmith's BD-J menu description (menu.txt) as HDMV pages: same pages, same buttons, same positions, same neighbours.</summary>
    static void RealMenu(string menuDir, string bdmv, string palette)
    {
        var pages = new List<(string Id, string Bg, List<HdmvButtonSpec> Buttons)>();
        foreach (var line in File.ReadAllLines(Path.Combine(menuDir, "menu.txt")))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 0) continue;
            if (f[0] == "page") pages.Add((f[1], Path.Combine(menuDir, f[2]), []));
            else if (f[0] == "button")
            {
                // button <page> <id> x y w h <picture> <left> <right> <up> <down> <action> <argument...>
                string? N(string v) => v == "-" ? null : v;
                var nav = new Dictionary<string, string>();
                foreach (var (dir, v) in new[] { ("left", f[8]), ("right", f[9]), ("up", f[10]), ("down", f[11]) }) if (N(v) is { } to) nav[dir] = to;
                HdmvAction action = f[12] switch
                {
                    "play" => HdmvAction.PlayTitle(1),
                    "chapter" => HdmvAction.PlayChapter(int.Parse(f[13]), int.Parse(f[14]) + 1),
                    "page" => HdmvAction.OpenPage(f[13]),
                    _ => throw new NotSupportedException(line),
                };
                pages.Single(p => p.Id == f[1]).Buttons.Add(new HdmvButtonSpec { Id = f[2], X = int.Parse(f[3]), Y = int.Parse(f[4]), Selected = Path.Combine(menuDir, f[7]), Action = action, Nav = nav });
            }
        }
        var set = new HdmvMenuSet
        {
            Movies = [new HdmvMovie(1, 2)],
            Pages = [.. pages.Select(p => new HdmvPage { Id = p.Id, Background = p.Bg, Buttons = p.Buttons })],
            Ig = new IgOptions { Palette = palette == "mediancut" ? IgPalette.MedianCut : IgPalette.Bdmvkit },
        };
        var r = HdmvAuthor.AddMenus(bdmv, set);
        Console.WriteLine($"pages {string.Join(',', pages.Select(p => p.Id))}: clips {string.Join(',', r.PageClips)}, titles {string.Join(',', r.PageTitles)}, {r.ObjectCount} movie objects, palette {palette}");
    }

    /// <summary>The gradient buttons (thousands of colours, partial alpha: the quantizer's reduction and nearest-colour paths) against bdmvkit's own output for the same pictures.</summary>
    static void SynthCompare(string dir)
    {
        var theirs = File.ReadAllBytes(Path.Combine(dir, "ref.sup"));
        var ours = IgStream.Build(SyntheticButtons(), 1, IgOptions.BdmvkitCompatible);
        Check($"synthetic gradient buttons: .sup byte-identical to bdmvkit's ({ours.Length} bytes)", ours.AsSpan().SequenceEqual(theirs),
              $"ours {ours.Length} bytes, bdmvkit {theirs.Length} bytes");
        var (p, i) = IgStream.Quantize(SyntheticButtons().SelectMany(b => new[] { b.Normal!, b.Selected, b.Activated! }).ToList());
        Console.WriteLine($"  (palette of {p.Count} entries after reducing the colours; the gradient has thousands)");
    }

    static bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
    static void Check2(bool ok) { if (!ok) throw new InvalidOperationException("opaque pixel mapped to transparent"); }

    static List<(uint Pts, uint Dts)> Headers(byte[] sup)
    {
        var l = new List<(uint, uint)>();
        for (var i = 0; i < sup.Length; i += 13 + (sup[i + 11] << 8 | sup[i + 12]))
            l.Add((System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(sup.AsSpan(i + 2)), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(sup.AsSpan(i + 6))));
        return l;
    }

    /// <summary>Soft-edged rounded buttons with a vertical and horizontal gradient: thousands of colours, partial alpha.</summary>
    static byte[] Gradient(int w, int h) => Pic(w, h, (x, y) =>
    {
        var edge = Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y));
        var a = edge >= 4 ? 255 : edge * 64;
        return ((byte)(x * 255 / (w - 1)), (byte)(y * 255 / (h - 1)), (byte)((x + y) % 256), (byte)a);
    });

    static List<IgButton> SyntheticButtons()
    {
        var w = 300; var h = 60;
        var g = Gradient(w, h);
        byte[] Tint(byte[] src, int r) { var o = (byte[])src.Clone(); for (var i = 0; i < o.Length; i += 4) o[i] = (byte)Math.Min(255, o[i] + r); return o; }
        return
        [
            new IgButton { Id = "a", X = 100, Y = 600, W = w, H = h, Normal = g, Selected = Tint(g, 60), Activated = Tint(g, 120), Commands = [HdmvCommand.JumpTitle(1)] },
            new IgButton { Id = "b", X = 100, Y = 700, W = w, H = h, Normal = g, Selected = Tint(g, 60), Activated = Tint(g, 120), Commands = [HdmvCommand.JumpObject(4)] },
            new IgButton { Id = "c", X = 100, Y = 800, W = w, H = h, Normal = g, Selected = Tint(g, 60), Activated = Tint(g, 120), Commands = [HdmvCommand.JumpTitle(1)] },
        ];
    }

    // ---------------------------------------------------------------- against bdmvkit's output

    static string Hex(byte[] b, int max = 80) => Convert.ToHexString(b.AsSpan(0, Math.Min(max, b.Length)));

    static List<IgButton> BdmvkitButtons(string run) =>
        [.. new[] { ("play", 600, "title"), ("scenes", 740, "chapter"), ("setup", 880, "title") }.Select(t =>
        {
            var (w, h, sel) = MenuClip.ReadRgba(Path.Combine(run, t.Item1 + "-lit.png"));
            return new IgButton
            {
                Id = t.Item1, X = 160, Y = t.Item2, W = w, H = h, Selected = sel,
                Activated = MenuClip.ReadRgba(Path.Combine(run, t.Item1 + "-on.png"), w, h).Rgba,
                Normal = MenuClip.ReadRgba(Path.Combine(run, t.Item1 + "-off.png"), w, h).Rgba,
                Commands = [t.Item3 == "chapter" ? HdmvCommand.JumpObject(4) : HdmvCommand.JumpTitle(1)],
            };
        })];

    static void AgainstBdmvkit(string run, string work)
    {
        Directory.CreateDirectory(work);
        var theirs = Path.Combine(run, "out", "BDMV");
        // the IG segments libbluray actually played: the PES payloads of PID 0x1400 in bdmvkit's menu clip
        var pes = TransportStream.ReadPes(Path.Combine(theirs, "STREAM", "00002.m2ts"), 0x1400);
        var theirSegs = pes.SelectMany(p => TransportStream.Segments(p.Payload)).ToList();
        var buttons = BdmvkitButtons(run);
        var sup = IgStream.Build(buttons, 1, IgOptions.BdmvkitCompatible);
        File.WriteAllBytes(Path.Combine(work, "menu-cs.sup"), sup);
        var ourSegs = IgDecoded.ReadSup(sup);
        var a = IgDecoded.Decode(theirSegs);
        var b = IgDecoded.Decode(ourSegs);
        Check("vs bdmvkit: decoded ICS / palette / objects identical", a.Dump() == b.Dump(), "bdmvkit:\n" + a.Dump() + "\nC#:\n" + b.Dump());
        Check($"vs bdmvkit: {theirSegs.Count} segments, same types, lengths and bodies (byte-identical, timestamps aside)",
              theirSegs.Count == ourSegs.Count && theirSegs.Zip(ourSegs).All(p => p.First.Type == p.Second.Type && p.First.Body.AsSpan().SequenceEqual(p.Second.Body)),
              string.Join("\n", theirSegs.Zip(ourSegs).Select((p, i) => $"{i}: {p.First.Type:x2}/{p.First.Body.Length} vs {p.Second.Type:x2}/{p.Second.Body.Length} {(p.First.Body.AsSpan().SequenceEqual(p.Second.Body) ? "=" : "DIFF")}")));
    }

    // ---------------------------------------------------------------- disc from the bdmvkit inputs

    static HdmvMenuSet BdmvkitSet(string run, IgOptions? ig = null) => new()
    {
        Movies = [new HdmvMovie(1, 2)],
        Fast = true, // the bdmvkit reference disc was built with --fast (ultrafast x264): same settings, same clip
        Ig = ig ?? IgOptions.BdmvkitCompatible,
        PlaylistUoMask = ig == null ? UoMask.None : UoMask.MenuReference,
        Pages =
        [
            new HdmvPage
            {
                Id = "main", Background = Path.Combine(run, "menu.png"),
                Buttons =
                [
                    new HdmvButtonSpec { Id = "play", X = 160, Y = 600, Selected = Path.Combine(run, "play-lit.png"), Activated = Path.Combine(run, "play-on.png"), Normal = Path.Combine(run, "play-off.png"), Action = HdmvAction.PlayTitle(1) },
                    new HdmvButtonSpec { Id = "scenes", X = 160, Y = 740, Selected = Path.Combine(run, "scenes-lit.png"), Activated = Path.Combine(run, "scenes-on.png"), Normal = Path.Combine(run, "scenes-off.png"), Action = HdmvAction.PlayChapter(1, 2) },
                    new HdmvButtonSpec { Id = "setup", X = 160, Y = 880, Selected = Path.Combine(run, "setup-lit.png"), Activated = Path.Combine(run, "setup-on.png"), Normal = Path.Combine(run, "setup-off.png"), Action = HdmvAction.PlayTitle(1) },
                ],
            },
        ],
    };

    /// <summary>The marks of a movie clip as bdmvkit places them: first picture, then the key frame nearest to each chapter time.</summary>
    static List<long> ChapterMarks(ClipScan scan, params double[] chapters)
    {
        var marks = new List<long> { scan.FirstPts / 2 };
        foreach (var t in chapters.Where(c => c > 0).Order())
        {
            var want = scan.FirstPts + (long)Math.Round(t * 90000);
            var near = scan.Entries.Select(e => e.Pts).MinBy(k => Math.Abs(k - want));
            if (near / 2 > marks[^1]) marks.Add(near / 2);
        }
        return marks;
    }

    static void BuildDisc(string run, string outDir)
    {
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        var bdmv = Path.Combine(outDir, "BDMV");
        foreach (var d in new[] { "PLAYLIST", "CLIPINF", "STREAM", "AUXDATA", "BDJO", "JAR", "META", "BACKUP\\PLAYLIST", "BACKUP\\CLIPINF", "BACKUP\\BDJO" }) Directory.CreateDirectory(Path.Combine(bdmv, d));
        Directory.CreateDirectory(Path.Combine(outDir, "CERTIFICATE", "BACKUP"));
        // movie: bdmvkit's encoded clip, its clip information and playlist written by our port too (tsMuxeR does this in BurnSmith)
        var clip = Path.Combine(bdmv, "STREAM", "00001.m2ts");
        File.Copy(Path.Combine(run, "feat.m2ts"), clip);
        var scan = TransportStream.Scan(clip);
        var codes = HdmvFormats.VideoCodes(24000, 1001)!.Value;
        var audio = new[] { new HdmvAudio(HdmvFormats.CodingAc3, 2, "fre") };
        var marks = ChapterMarks(scan, 0, 4);
        var item = new PlayItem("00001", marks[0], scan.EndPts / 2, codes.Format, codes.Rate, audio, [], false);
        foreach (var root in new[] { bdmv, Path.Combine(bdmv, "BACKUP") })
        {
            File.WriteAllBytes(Path.Combine(root, "CLIPINF", "00001.clpi"), ClipFiles.Clpi(scan, codes.Format, codes.Rate, audio));
            File.WriteAllBytes(Path.Combine(root, "PLAYLIST", "00001.mpls"), ClipFiles.Mpls([item], marks.Select(m => (0, m)).ToList()));
        }
        var result = HdmvAuthor.AddMenus(bdmv, BdmvkitSet(run));
        Console.WriteLine($"menus: clips {string.Join(',', result.PageClips)}, titles {string.Join(',', result.PageTitles)}, first play object {result.FirstPlayObject}, top menu object {result.TopMenuObject}, {result.ObjectCount} movie objects");

        var theirs = Path.Combine(run, "out", "BDMV");
        foreach (var rel in new[] { "index.bdmv", "MovieObject.bdmv", "CLIPINF\\00001.clpi", "CLIPINF\\00002.clpi", "PLAYLIST\\00001.mpls", "PLAYLIST\\00002.mpls",
                                    "BACKUP\\index.bdmv", "BACKUP\\MovieObject.bdmv", "BACKUP\\CLIPINF\\00002.clpi", "BACKUP\\PLAYLIST\\00002.mpls" })
        {
            var ours = File.ReadAllBytes(Path.Combine(bdmv, rel));
            var their = File.ReadAllBytes(Path.Combine(theirs, rel));
            Check($"vs bdmvkit: {rel} byte-identical ({ours.Length} bytes)", ours.AsSpan().SequenceEqual(their), $"ours  {Hex(ours, 200)}\ntheirs {Hex(their, 200)}");
        }
        var menuOurs = TransportStream.ReadPes(Path.Combine(bdmv, "STREAM", "00002.m2ts"), 0x1400);
        var menuTheirs = TransportStream.ReadPes(Path.Combine(theirs, "STREAM", "00002.m2ts"), 0x1400);
        Check("menu clip: IG PES payloads identical to bdmvkit's", menuOurs.Count == menuTheirs.Count && menuOurs.Zip(menuTheirs).All(p => p.First.Payload.AsSpan().SequenceEqual(p.Second.Payload)));
        Check("menu clip: PMT marks PID 0x1400 as stream type 0x91", TransportStream.StreamPids(Path.Combine(bdmv, "STREAM", "00002.m2ts"))[0x1400] == 0x91);
        Console.WriteLine("disc written to " + outDir);
    }

    // ---------------------------------------------------------------- menu added to a tsMuxeR movie

    static void AddMenu(string bdmv, string run, string timeline, string uo)
    {
        // "default" = the writer's defaults; otherwise a combination of the two switches
        var ig = timeline == "default" ? new IgOptions() : new IgOptions
        {
            Timeline = timeline == "reference" ? IgTimeline.Reference : IgTimeline.Zero,
            PageUoMask = uo == "menu" ? UoMask.MenuReference : UoMask.None,
        };
        var set = BdmvkitSet(run, ig);
        var result = HdmvAuthor.AddMenus(bdmv, set);
        Console.WriteLine($"menus: clips {string.Join(',', result.PageClips)}, {result.ObjectCount} movie objects, timeline {timeline}, UO {uo}");
    }

    static void Pes(string m2ts, int pid)
    {
        var all = TransportStream.ReadPes(m2ts, pid);
        Console.WriteLine($"{all.Count} PES packets on PID {pid:X4}, stream types: " + string.Join(", ", TransportStream.StreamPids(m2ts).Select(p => $"{p.Key:X4}={p.Value:X2}")));
        for (var i = 0; i < all.Count && i < 40; i++)
        {
            var p = all[i];
            var segs = pid == 0x1400 ? string.Join('+', TransportStream.Segments(p.Payload).Select(s => s.Type.ToString("x2"))) : "";
            Console.WriteLine($"  {i,3} pts={p.Pts / 90000.0:0.000000} dts={(p.Dts == null ? "-" : (p.Dts / 90000.0).Value.ToString("0.000000", CultureInfo.InvariantCulture))} len={p.Payload.Length} {segs}");
        }
    }
}
