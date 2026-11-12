using System.Globalization;
using BurnSmith.Core.Disc;
using BurnSmith.Core.Menu;

namespace BurnSmith.Core;

/// <summary>
/// The menu as native Blu-ray navigation (HDMV) instead of the BD-J program: what every player runs without Java, with less than the BD-J menu can do.
/// Each page is a playlist of its own: the page's picture as a short video (with the menu music when there is some), and over it an Interactive
/// Graphics stream (<see cref="IgStream"/>) that only draws the selected button's highlight and the dot of the audio / subtitle in use. Buttons set
/// registers and jump; three kinds of movie objects do the rest: First Playback, the Top Menu (plays the page the register names) and one per title.
/// Not here, by design of this first version: a moving background (the pages are still pictures), the pop-up menu over the film, and a choice of
/// audio or subtitle that changes a film already playing (it applies when a title starts).
/// </summary>
public static class HdmvMenu
{
    /// <summary>Playlist of the first page, the others follow; each page takes two clips from <see cref="FirstClip"/> (its video, its graphics).</summary>
    public const int FirstPlaylist = 100, FirstClip = 1000;

    /// <summary>Seconds of video behind a page without music: the player then holds the last picture for as long as the page stays.</summary>
    const int StillSeconds = 5;

    // registers: the playlist of the page to show, the button to select on it (0 = its first), the audio and subtitle streams chosen (subtitle 0 = off),
    // the chapter mark to start the title at, whether the next title follows this one, and a scratch one
    const int Page = 10, Selected = 11, Audio = 12, Subtitle = 13, Chapter = 14, Chain = 15, Scratch = 16;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>What the BD-J menu would do with this design and a native menu does not: said in the log, and shown by the app before the build.</summary>
    public static List<string> LeftOut(MenuDesign menu)
    {
        var list = new List<string>();
        if (menu.Loop) list.Add("fond animé (les pages sont des images fixes)");
        if (menu.Popup) list.Add("menu pop-up pendant le film");
        return list;
    }

    internal static void Add(BluRayJob job, IReadOnlyList<MenuPage> pages, int mainCount, int defaultSubtitle, IReadOnlyList<(int Audio, int Sub)> forced,
                             bool intro, string work, Action<string> log)
    {
        var menu = job.Menu!;
        log("menu: HDMV");
        foreach (var missing in LeftOut(menu)) log($"menu HDMV : pas de {missing}");
        var (videoW, videoH) = BluRay.VideoSize(job);
        var scale = Math.Min(1, videoH / 1080.0); // the graphics plane is 1920x1080 on a 4K disc too
        int Px(int v) => MenuRenderer.Px(v, scale);
        var (planeW, planeH) = (Px(MenuBuilder.W), Px(MenuBuilder.H));
        var (fps, declared) = BluRay.DiscRate(0);
        var musicFile = menu.ExistingMusic();
        var seconds = musicFile == null ? StillSeconds : BluRay.LoopLength(menu, BluRay.MaxLoopSeconds);
        var music = musicFile == null ? null : BluRay.PrepareMusic(menu, musicFile, seconds, work, log);
        if (music != null) log($"menu: music from {Path.GetFileName(musicFile)}, {seconds}s on every page");

        var art = MenuRenderer.Sta(() => (
            Backgrounds: pages.Select(p => MenuRenderer.Background(menu, p, scale)).ToList(),
            Focus: pages.Select(p => p.Buttons.Select(b => MenuRenderer.Pixels(MenuRenderer.Focus(menu, p, b, scale))).ToList()).ToList(),
            Marker: MenuRenderer.Pixels(MenuRenderer.Marker(menu, MenuBuilder.MarkerSize, scale))));
        var playlists = pages.Select((p, k) => (p.Id, Playlist: FirstPlaylist + k)).ToDictionary(x => x.Id, x => x.Playlist);
        var play = job.Playback ?? new PlaybackOptions();

        for (var k = 0; k < pages.Count; k++)
        {
            var page = pages[k];
            var picture = Path.Combine(work, $"hdmv_{page.Id}.jpg");
            File.WriteAllBytes(picture, art.Backgrounds[k]);
            var ts = Path.Combine(work, $"hdmv_{page.Id}.ts");
            var args = new List<string> { "-y", "-v", "warning", "-loop", "1", "-framerate", fps, "-i", picture };
            if (music != null) args.AddRange(["-i", music, "-map", "0:v:0", "-map", "1:a:0"]);
            // a JPEG is full-range RGB: BT.709 like every HD picture, or the page's colours shift
            args.AddRange(["-t", seconds.ToString(Inv), "-vf", $"scale={videoW}:{videoH}:out_color_matrix=bt709", "-r", fps, "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"]);
            args.AddRange(BluRay.MenuCodec(job.Uhd, "SDR", fps, videoH));
            if (music != null) args.AddRange(["-c:a", "ac3", "-b:a", "448k", "-ac", "2", "-ar", "48000"]);
            args.AddRange(["-sn", "-dn", ts]);
            Tools.Run(Tools.Find("ffmpeg.exe"), args, log);

            var graphics = Path.Combine(work, $"hdmv_{page.Id}.igs");
            File.WriteAllBytes(graphics, Graphics(page, art.Focus[k], art.Marker, MenuBuilder.MarkerOffset(menu), scale, planeW, planeH, IgStream.RateCode(declared),
                                                  playlists, play.PlayAll, mainCount, log));
            var meta = Path.Combine(work, $"hdmv_{page.Id}.meta");
            File.WriteAllLines(meta,
            [
                // the options a native menu is known to play with in libbluray: a floor on the bit rate, or a still page of a few hundred KB is read whole at
                // once and a pressed button waits for the clip's end; without music the last picture is held (--final-still) and nothing restarts
                $"MUXOPT {(job.Uhd ? "--blu-ray-v3" : "--blu-ray")} --label={job.Label} --new-audio-pes --no-pcr-on-video-pid --start-time=27000000 --maxbitrate={(job.Uhd ? 86000 : 48000)} --vbv-len=950"
                    + $"{(music == null ? " --minbitrate=4000" : "")} --mplsOffset={FirstPlaylist + k} --m2tsOffset={FirstClip + 2 * k}{(music == null ? " --final-still" : "")}",
                job.Uhd ? $"V_MPEGH/ISO/HEVC, \"{ts}\", fps={declared}, track=256" : $"V_MPEG4/ISO/AVC, \"{ts}\", fps={declared}, track=256",
                .. music != null ? new[] { $"A_AC3, \"{ts}\", track=257" } : [],
                // subClip: the graphics in a clip of their own, loaded before the page shows, so they stay while the page's video starts again
                $"S_HDMV/IGS, \"{graphics}\", lang=eng, fps={declared}, video-width={planeW}, video-height={planeH}, subClip",
            ]);
            var dest = Path.Combine(work, "mux" + (FirstPlaylist + k));
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Tools.Run(Tools.Find("tsMuxeR.exe"), [meta, dest], log);
            BluRay.Merge(dest, job.OutDir);
        }
        log($"menu: {pages.Count} pages");

        // First Playback: the choices a disc starts with, the start video, then the menu or the first title
        List<byte[]> firstPlay =
        [
            Nav.Set(Audio, 1), Nav.Set(Subtitle, defaultSubtitle), Nav.Set(Page, FirstPlaylist), Nav.Set(Selected, 0), Nav.Set(Chapter, 0), Nav.Set(Chain, play.StartAtFirstTitle ? 1 : 0),
            .. intro ? new[] { Nav.PlayPlaylist(BluRay.IntroPlaylist) } : [],
            Nav.JumpTitle(play.StartAtFirstTitle ? 1 : 0),
        ];
        // Top Menu: the page of the register, again when its music ends, on the button that was selected
        List<byte[]> topMenu = [Nav.PlayPlaylistIn(Page), Nav.CopyPlayer(Selected, 10), Nav.GoTo(0)];
        var objects = new List<IReadOnlyList<byte[]>> { firstPlay, topMenu };
        for (var t = 1; t <= job.Titles.Count; t++) objects.Add(TitleObject(t, mainCount, forced, play));

        var bdmv = Path.Combine(job.OutDir, "BDMV");
        var movieObject = MovieObjectFile.Write(objects, job.Uhd ? "0300" : "0200");
        var index = Bdmv.HdmvIndex(File.ReadAllBytes(Path.Combine(bdmv, "index.bdmv")), job.Titles.Count);
        foreach (var dir in new[] { bdmv, Path.Combine(bdmv, "BACKUP") })
        {
            File.WriteAllBytes(Path.Combine(dir, "MovieObject.bdmv"), movieObject);
            File.WriteAllBytes(Path.Combine(dir, "index.bdmv"), index);
        }
    }

    /// <summary>
    /// What title <paramref name="title"/> (1 = the first) runs: the audio and subtitle chosen in the menu (the forced track of the audio's language while
    /// subtitles are off), the film from the chapter asked for, then what the play mode says, as the BD-J menu does: the next main title when they follow
    /// each other, and after the last one the first again, the menu, or nothing (the disc stops).
    /// </summary>
    public static List<byte[]> TitleObject(int title, int mainCount, IReadOnlyList<(int Audio, int Sub)> forced, PlaybackOptions play)
    {
        var playlist = BluRay.MoviePlaylist + title - 1;
        // subtitles off: the audio alone, never "subtitle number 0"
        var c = new List<byte[]> { Nav.SelectStreamsIn(Audio), Nav.IfEqual(Subtitle, 0), Nav.GoTo(4), Nav.SelectStreamsIn(Audio, Subtitle) };
        if (forced.Count > 0)
        {
            c.AddRange([Nav.Set(Scratch, 0), Nav.IfEqual(Subtitle, 0), Nav.Copy(Scratch, Audio)]); // the audio stream when subtitles are off, else 0
            foreach (var (audio, sub) in forced) c.AddRange([Nav.IfEqual(Scratch, audio), Nav.SelectStreams(audio, sub)]);
        }
        var at = c.Count;
        c.AddRange([Nav.IfEqual(Chapter, 0), Nav.GoTo(at + 4), Nav.PlayPlaylistAtMarkIn(playlist, Chapter), Nav.GoTo(at + 5), Nav.PlayPlaylist(playlist), Nav.Set(Chapter, 0)]);
        if (title < mainCount) c.AddRange([Nav.IfEqual(Chain, 1), Nav.JumpTitle(title + 1)]);
        if (play.PlayAll && play.StartAtFirstTitle && play.Rewind) c.AddRange([Nav.Set(Chain, 1), Nav.JumpTitle(1)]);
        else if (!play.PlayAll || play.Rewind) c.Add(Nav.JumpTitle(0));
        return c;
    }

    /// <summary>
    /// The graphics of one page. Buttons 1..n are the page's; after them one hidden button per audio / subtitle line, the dot beside it, shown when that
    /// stream is the one in use; last, an invisible button the page opens on, which runs by itself: it shows the right dots and selects the button of the
    /// <see cref="Selected"/> register (the one a title was started from, or the one selected when the music started again), else the first.
    /// </summary>
    static byte[] Graphics(MenuPage page, List<(uint[] Argb, int W, int H)> focus, (uint[] Argb, int W, int H) marker, int markerOffset, double scale, int planeW, int planeH,
                           int rateCode, Dictionary<string, int> playlists, bool playAll, int mainCount, Action<string> log)
    {
        int Px(int v) => MenuRenderer.Px(v, scale);
        int Number(string? id, int own) => id != null && page.Buttons.FindIndex(b => b.Id == id) is >= 0 and var i ? i + 1 : own;
        var count = page.Buttons.Count;
        var first = count + 1; // numbers of the dots
        var dots = page.Buttons.Select((b, i) => (Button: b, Index: i)).Where(x => x.Button.Action.Kind is "audio" or "sub").Select((x, n) => (x.Button, x.Index, Id: first + n)).ToList();
        var opener = first + dots.Count;
        // choosing a stream moves the dot of its kind at once
        IEnumerable<byte[]> Dot(MenuButton b) => dots.Where(d => d.Button.Action.Kind == b.Action.Kind).Select(d => Nav.EnableButton(d.Id, d.Button == b));

        // Where each picture goes: the highlights, then the dots. No two may share a pixel: a living-room player erases or hides buttons that overlap
        // (a software player draws them anyway). So a dot stands just left of its button's highlight, not on it as in the BD-J menu, and what still
        // overlaps is trimmed: two highlights at the middle of what they share, a highlight alone where it meets a dot.
        var boxes = new List<(uint[] Argb, int X, int Y, int W, int H)>();
        for (var i = 0; i < count; i++)
        {
            var box = MenuRenderer.FocusBox(page.Buttons[i]);
            boxes.Add(Crop(focus[i].Argb, focus[i].W, focus[i].H, Px(box.X), Px(box.Y), planeW, planeH));
        }
        foreach (var (b, _, _) in dots)
        {
            var box = MenuRenderer.FocusBox(b);
            var left = Px(box.X) - marker.W - Px(4);
            boxes.Add(Crop(marker.Argb, marker.W, marker.H, left >= 0 ? left : Px(box.X) + Px(markerOffset), Px(box.Y) + (Px(box.H) - marker.H) / 2, planeW, planeH));
        }
        Separate(boxes, count);

        var buttons = new List<IgButton>();
        for (var i = 0; i < count; i++)
        {
            var b = page.Buttons[i];
            var a = b.Action;
            var title = a.Playlist - BluRay.MoviePlaylist + 1;
            var chain = playAll && title <= mainCount ? 1 : 0;
            List<byte[]> commands = a.Kind switch
            {
                "play" => [Nav.Set(Selected, i + 1), Nav.Set(Chapter, 0), Nav.Set(Chain, chain), Nav.JumpTitle(title)],
                "chapter" => [Nav.Set(Selected, i + 1), Nav.Set(Chapter, a.Mark), Nav.Set(Chain, chain), Nav.JumpTitle(title)],
                "playall" => [Nav.Set(Selected, i + 1), Nav.Set(Chapter, 0), Nav.Set(Chain, 1), Nav.JumpTitle(title)],
                "audio" => [Nav.Set(Audio, a.Mark), .. Dot(b)],
                "sub" => [Nav.Set(Subtitle, a.Mark), .. Dot(b)],
                // a page this disc does not have: the first one
                _ => [Nav.Set(Selected, 0), Nav.Set(Page, playlists.GetValueOrDefault(a.Page ?? "", FirstPlaylist)), Nav.JumpObject(1)],
            };
            var (argb, x, y, w, h) = boxes[i];
            buttons.Add(new IgButton(i + 1, x, y, w, h, argb, null, Number(b.Up, i + 1), Number(b.Down, i + 1), Number(b.Left, i + 1), Number(b.Right, i + 1), commands));
        }
        foreach (var (_, _, id) in dots)
        {
            var (argb, x, y, w, h) = boxes[id - 1];
            buttons.Add(new IgButton(id, x, y, w, h, null, argb, id, id, id, id, [], Enabled: false));
        }
        var start = Math.Min(1, count); // a page without button keeps the opener selected
        var blank = new uint[16 * 12];
        buttons.Add(new IgButton(opener, 0, 0, 16, 12, blank, blank, start == 0 ? opener : 1, start == 0 ? opener : 1, start == 0 ? opener : 1, start == 0 ? opener : 1,
        [
            .. dots.Select(d => Nav.EnableButton(d.Id, false)),
            .. dots.SelectMany(d => new[] { Nav.IfEqual(d.Button.Action.Kind == "audio" ? Audio : Subtitle, d.Button.Action.Mark), Nav.EnableButton(d.Id) }),
            .. start == 0 ? Array.Empty<byte[]>() : [Nav.IfEqual(Selected, 0), Nav.Set(Selected, 1), Nav.SelectButtonIn(Selected)],
        ], Auto: true));

        // a player decodes a page's pictures into 16 MB: said, not refused (a page of large highlights still plays in software players)
        var pixels = buttons.Where(b => b.Selected != null).Sum(b => (long)b.W * b.H);
        if (pixels > 16 << 20) log($"warning: page {page.Id}: its highlights are {pixels >> 20} MB of pictures, more than the 16 MB a player decodes at once");
        return IgStream.Write(planeW, planeH, rateCode, buttons, opener);
    }

    /// <summary>
    /// Trims the pictures until no two overlap. The first <paramref name="highlights"/> give way to each other half and half, on the side where they share
    /// the least; the others (dots) are kept whole and the highlight under one loses the smallest side that frees it.
    /// </summary>
    public static void Separate(List<(uint[] Argb, int X, int Y, int W, int H)> boxes, int highlights)
    {
        void Keep(int i, int x0, int y0, int x1, int y1)
        {
            var (argb, x, y, w, h) = boxes[i];
            (x0, y0, x1, y1) = (Math.Max(x0, x), Math.Max(y0, y), Math.Min(x1, x + w), Math.Min(y1, y + h));
            if (x1 - x0 < 8 || y1 - y0 < 8) return; // shortcut: a highlight that would vanish stays whole and overlaps, never seen on a real template
            var cut = new uint[(x1 - x0) * (y1 - y0)];
            for (var row = y0; row < y1; row++) Array.Copy(argb, (row - y) * w + x0 - x, cut, (row - y0) * (x1 - x0), x1 - x0);
            boxes[i] = (cut, x0, y0, x1 - x0, y1 - y0);
        }
        const int All = int.MaxValue;
        for (var i = 0; i < highlights; i++)
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var (a, b) = (boxes[i], boxes[j]);
                int x0 = Math.Max(a.X, b.X), x1 = Math.Min(a.X + a.W, b.X + b.W), y0 = Math.Max(a.Y, b.Y), y1 = Math.Min(a.Y + a.H, b.Y + b.H);
                if (x0 >= x1 || y0 >= y1) continue;
                if (j >= highlights) // a dot: the side of the highlight that costs it the least
                {
                    int left = (b.X + b.W - a.X) * a.H, right = (a.X + a.W - b.X) * a.H, top = (b.Y + b.H - a.Y) * a.W, bottom = (a.Y + a.H - b.Y) * a.W;
                    var least = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
                    if (least == left) Keep(i, b.X + b.W, 0, All, All);
                    else if (least == right) Keep(i, 0, 0, b.X, All);
                    else if (least == top) Keep(i, 0, b.Y + b.H, All, All);
                    else Keep(i, 0, 0, All, b.Y);
                }
                else if (x1 - x0 < y1 - y0) // side by side
                {
                    var middle = (x0 + x1) / 2;
                    if (a.X <= b.X) { Keep(i, 0, 0, middle, All); Keep(j, middle, 0, All, All); } else { Keep(j, 0, 0, middle, All); Keep(i, middle, 0, All, All); }
                }
                else // one above the other
                {
                    var middle = (y0 + y1) / 2;
                    if (a.Y <= b.Y) { Keep(i, 0, 0, All, middle); Keep(j, 0, middle, All, All); } else { Keep(j, 0, 0, All, middle); Keep(i, 0, middle, All, All); }
                }
            }
    }

    /// <summary>The part of a picture placed at <paramref name="x"/>, <paramref name="y"/> that lies on the screen (a highlight's margin can pass the edge), at least 8 pixels a side.</summary>
    internal static (uint[] Argb, int X, int Y, int W, int H) Crop(uint[] argb, int w, int h, int x, int y, int planeW, int planeH)
    {
        int x0 = Math.Clamp(x, 0, planeW - 8), y0 = Math.Clamp(y, 0, planeH - 8);
        int x1 = Math.Clamp(Math.Max(x + w, x0 + 8), 8, planeW), y1 = Math.Clamp(Math.Max(y + h, y0 + 8), 8, planeH);
        if (x0 == x && y0 == y && x1 == x + w && y1 == y + h) return (argb, x, y, w, h);
        var cut = new uint[(x1 - x0) * (y1 - y0)];
        for (var row = Math.Max(y0, y); row < Math.Min(y1, y + h); row++)
            for (var col = Math.Max(x0, x); col < Math.Min(x1, x + w); col++)
                cut[(row - y0) * (x1 - x0) + col - x0] = argb[(row - y) * w + col - x];
        return (cut, x0, y0, x1 - x0, y1 - y0);
    }
}
