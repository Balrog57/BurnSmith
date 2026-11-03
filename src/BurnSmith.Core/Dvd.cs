using System.Globalization;
using System.Xml.Linq;
using BurnSmith.Core.Menu;
using static BurnSmith.Core.Menu.IndexedPng;

namespace BurnSmith.Core;

/// <summary>DVD-Video job. <see cref="Menu"/> null = the disc starts straight on the movie.</summary>
public sealed record DvdJob(string Video, string OutDir, MenuDesign? Menu = null, int ChapterMinutes = 5, double CapacityGB = 4.2);

/// <summary>ffmpeg (MPEG-2 + AC-3) -> spumux (button highlights) -> dvdauthor (VIDEO_TS with menus).</summary>
public static class Dvd
{
    const int Title = 1;

    /// <summary>Video/audio bit rates that fill <paramref name="capacityGB"/> (usable GB), video capped at 8 Mb/s.</summary>
    public static (int Video, int Audio) Bitrates(SourceInfo src, double capacityGB)
    {
        var audio = src.AudioChannels > 2 ? 448_000 : 192_000;
        return ((int)Math.Clamp(capacityGB * 1e9 * 8 / src.Duration * 0.96 - audio, 1_000_000, 8_000_000), audio);
    }

    public static double EstimateGB(SourceInfo src, double capacityGB)
    {
        var (v, a) = Bitrates(src, capacityGB);
        return (v + a) * src.Duration / 8 / 1e9;
    }

    public static void Build(DvdJob job, Action<string>? log = null)
    {
        log ??= _ => { };
        var src = BluRay.Probe(job.Video);
        var pal = Math.Abs(src.Fps - 25) < 0.5 || Math.Abs(src.Fps - 50) < 0.5;
        var (w, h) = pal ? (720, 576) : (720, 480);
        var sar = pal ? "64/45" : "32/27";
        var work = job.OutDir + "_work";
        Tools.PrepareOutput(job.OutDir);
        Directory.CreateDirectory(work);
        var ffmpeg = Tools.Find("ffmpeg.exe");

        // movie: square pixels -> 16:9 box with bars -> DVD frame size and aspect
        var movie = Path.Combine(work, "movie.mpg");
        var ntscFilm = !pal && src.Fps < 25;
        var rate = pal ? "fps=25" : ntscFilm ? "fps=24000/1001,telecine=pattern=23" : "fps=30000/1001"; // film: real 3:2 pulldown
        var (videoBps, audioBps) = Bitrates(src, job.CapacityGB);
        log($"movie: {(pal ? "PAL" : "NTSC")} {videoBps / 1000} kb/s");
        var args = new List<string> { "-y", "-v", "warning", "-i", job.Video, "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", $"scale=trunc(iw*sar/2)*2:ih,setsar=1,scale=1024:576:force_original_aspect_ratio=decrease,pad=1024:576:(ow-iw)/2:(oh-ih)/2,scale={w}:{h},setsar={sar},{rate}",
            "-target", pal ? "pal-dvd" : "ntsc-dvd", "-aspect", "16:9", "-b:v", $"{videoBps / 1000}k", "-b:a", $"{audioBps / 1000}k" };
        if (ntscFilm) args.AddRange(["-flags", "+ildct+ilme", "-top", "1"]);
        args.Add(movie);
        Tools.Run(ffmpeg, args, log);

        var chapterTimes = BluRay.Chapters(src.Duration, job.ChapterMinutes);
        var menuPages = new List<(MenuPage Page, string Mpg)>();
        if (job.Menu != null) menuPages = BuildMenus(job, src, chapterTimes, work, pal, log);

        log("author: dvdauthor");
        var xml = Path.Combine(work, "dvd.xml");
        File.WriteAllText(xml, AuthorXml(job.OutDir, pal, movie, chapterTimes, menuPages).ToString());
        // this dvdauthor build wants exactly "-o dest -x script -h N" plus one more argument (it reads argv[7] unchecked and crashes without)
        Tools.Run(Tools.Find("dvdauthor.exe"), ["-o", job.OutDir, "-x", xml, "-h", "0", "-q"], log, env: new Dictionary<string, string> { ["VIDEO_FORMAT"] = pal ? "PAL" : "NTSC" });
        Directory.CreateDirectory(Path.Combine(job.OutDir, "AUDIO_TS")); // empty, but players and tools expect it
        log("done: " + job.OutDir);
    }

    static List<(MenuPage, string)> BuildMenus(DvdJob job, SourceInfo src, List<double> chapterTimes, string work, bool pal, Action<string> log)
    {
        log("menu: render");
        var ffmpeg = Tools.Find("ffmpeg.exe");
        var (w, h) = pal ? (720, 576) : (720, 480);
        var thumbs = Path.Combine(work, "thumbs");
        Directory.CreateDirectory(thumbs);
        string? Thumb(string name, double t, int tw, int th)
        {
            var file = Path.Combine(thumbs, name + ".jpg");
            Tools.Run(ffmpeg, ["-y", "-v", "error", "-ss", Math.Max(0, Math.Min(t, src.Duration - 0.5)).ToString("0.###", CultureInfo.InvariantCulture),
                "-i", Path.Combine(work, "movie.mpg"), "-frames:v", "1", "-q:v", "2", "-vf", $"scale=trunc(iw*sar/2)*2:ih,setsar=1,scale={tw}:{th}:force_original_aspect_ratio=increase,crop={tw}:{th}", file]);
            return File.Exists(file) ? file : null;
        }
        var content = new MenuContent(Title, Thumb("preview", src.Duration * 0.1, 800, 450),
            chapterTimes.Select((t, i) => Thumb($"c{i + 1}", t + 3, 480, 270)).ToList());
        var pages = MenuBuilder.Build(job.Menu!, content);
        var backgrounds = MenuRenderer.Sta(() => pages.Select(p => MenuRenderer.Background(job.Menu!, p)).ToList());

        var result = new List<(MenuPage, string)>();
        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var bg = Path.Combine(work, $"menu_{p.Id}.jpg");
            File.WriteAllBytes(bg, backgrounds[i]);
            var plain = Path.Combine(work, $"menu_{p.Id}_plain.mpg");
            // still image + silent AC-3 so the menu VOB carries the same streams as a normal title
            Tools.Run(ffmpeg, ["-y", "-v", "warning", "-loop", "1", "-framerate", pal ? "25" : "30000/1001", "-i", bg,
                "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000", "-t", "8",
                "-vf", $"scale={w}:{h},setsar={(pal ? "64/45" : "32/27")},format=yuv420p", "-target", pal ? "pal-dvd" : "ntsc-dvd", "-aspect", "16:9",
                "-b:v", "3000k", Path.Combine(work, $"menu_{p.Id}_plain.mpg")]);

            // button highlights: flat frames in 4 colours, scaled from the 1920x1080 layout
            var sx = w / (double)MenuBuilder.W;
            var sy = h / (double)MenuBuilder.H;
            var hl = new Canvas(w, h);
            var sel = new Canvas(w, h);
            var sub = new XElement("spu", new XAttribute("start", "00:00:00.00"), new XAttribute("highlight", $"hl_{p.Id}.png"),
                new XAttribute("select", $"sel_{p.Id}.png"), new XAttribute("force", "yes"));
            foreach (var b in p.Buttons)
            {
                var r = b.Rect.Inflate(MenuBuilder.FocusMargin / 2);
                int x0 = (int)(r.X * sx) & ~1, y0 = (int)(r.Y * sy) & ~1;
                int x1 = (int)((r.X + r.W) * sx), y1 = (int)((r.Y + r.H) * sy) & ~1;
                hl.Frame(x0, y0, x1, y1, 3, 1);
                sel.Frame(x0, y0, x1, y1, 4, 2);
                sub.Add(new XElement("button", new XAttribute("name", b.Id),
                    new XAttribute("x0", x0), new XAttribute("y0", y0), new XAttribute("x1", x1), new XAttribute("y1", y1),
                    Nav("up", b.Up), Nav("down", b.Down), Nav("left", b.Left), Nav("right", b.Right)));
            }
            var accent = Hex(job.Menu!.Accent);
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

    static XDocument AuthorXml(string dest, bool pal, string movie, List<double> chapterTimes, List<(MenuPage Page, string Mpg)> menus)
    {
        var fmt = pal ? "pal" : "ntsc";
        XElement Video() => new("video", new XAttribute("format", fmt), new XAttribute("aspect", "16:9"), new XAttribute("widescreen", "nopanscan"));
        string Chapters() => string.Join(',', chapterTimes.Select(t => TimeSpan.FromSeconds(t).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)));

        var menuEl = new XElement("menus", Video(), new XElement("audio", new XAttribute("lang", "en")), new XElement("subpicture", new XAttribute("lang", "en")));
        // menu PGCs are numbered in order: 1 = root, then the scene pages
        int MenuNumber(string pageId) => menus.FindIndex(m => m.Page.Id == pageId) + 1;
        foreach (var (page, mpg) in menus)
        {
            var pgc = new XElement("pgc");
            if (page.Id == "root") pgc.Add(new XAttribute("entry", "root"));
            pgc.Add(new XElement("vob", new XAttribute("file", mpg), new XAttribute("pause", "inf")));
            foreach (var b in page.Buttons)
            {
                var cmd = b.Action.Kind switch
                {
                    "play" => $"jump title {Title};",
                    "chapter" => $"jump title {Title} chapter {b.Action.Mark + 1};",
                    _ => $"jump menu {MenuNumber(b.Action.Page!)};",
                };
                pgc.Add(new XElement("button", new XAttribute("name", b.Id), cmd));
            }
            menuEl.Add(pgc);
        }

        var vob = new XElement("vob", new XAttribute("file", movie), new XAttribute("chapters", Chapters()));
        var title = new XElement("pgc", vob);
        if (menus.Count > 0) title.Add(new XElement("post", "call menu;"));
        var titleset = new XElement("titleset");
        if (menus.Count > 0) titleset.Add(menuEl);
        titleset.Add(new XElement("titles", Video(), new XElement("audio", new XAttribute("lang", "en")), title));

        var vmgm = new XElement("vmgm");
        if (menus.Count > 0) vmgm.Add(new XElement("fpc", "jump titleset 1 menu;"));
        return new XDocument(new XElement("dvdauthor", new XAttribute("dest", dest), vmgm, titleset));
    }
}
