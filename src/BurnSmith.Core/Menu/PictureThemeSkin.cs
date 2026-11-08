using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BurnSmith.Core.Menu;

/// <summary>
/// Older picture-only themes (business, childhood, education, holiday, nature, sports, standard, wedding, in three screen shapes)
/// exist on disk as pictures only: their layout lives elsewhere. Every one carries the same five pictures, whose transparent
/// openings are the real apertures (640x360 preview, 320x180 thumbnails in 1920x1080 terms), so BurnSmith measures each opening and lays
/// the pictures out itself: preview frame centred, Play / Scenes under it, a 3 x 2 grid of thumbnail frames, previous / back / next below.
/// The artwork is read from the user's installation; the positions are BurnSmith's own.
/// </summary>
static class PictureThemeSkin
{
    const int W = MenuBuilder.W, H = MenuBuilder.H;

    /// <summary>A frame picture: its size, the transparent opening, and whether it hangs from the top edge (strings, chains).</summary>
    sealed record Frame(int Width, int Height, Box Hole, bool Hangs);

    /// <summary>The picture of <paramref name="stem"/> in <paramref name="sub"/> whatever its case or extension, as a path relative to the template folder.</summary>
    static string? Find(string dir, string sub, string stem)
    {
        var folder = Path.Combine(dir, sub);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(f => Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase))
                .Select(f => $"{sub}/{Path.GetFileName(f)}").FirstOrDefault()
            : null;
    }

    static (int W, int H)? Size(string file) => MenuRenderer.Size(file);

    /// <summary>
    /// Flood-fills the transparent area around the picture's centre (the opening of the frame) and takes the largest rectangle in it.
    /// A picture without an opening (or whose centre is solid) gets its inner 70% instead.
    /// </summary>
    static Frame? Measure(string file)
    {
        try
        {
            MenuRenderer.CheckSize(file);
            var decoded = new FormatConvertedBitmap(BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
            int w = decoded.PixelWidth, h = decoded.PixelHeight;
            var px = new byte[w * h * 4];
            decoded.CopyPixels(px, w * 4, 0);
            bool Clear(int i) => px[i * 4 + 3] < 40;

            var hangs = Enumerable.Range(0, w).Any(x => px[x * 4 + 3] > 128);
            var seed = -1;
            foreach (var y in new[] { h / 2, h / 2 - h / 8, h / 2 + h / 8 }) // the centre row first, then a little above and below
            {
                for (var d = 0; d < w / 2 && seed < 0; d++)
                    foreach (var x in new[] { w / 2 - d, w / 2 + d })
                        if (x >= 0 && x < w && Clear(y * w + x)) { seed = y * w + x; break; }
                if (seed >= 0) break;
            }

            if (seed >= 0)
            {
                var inside = new bool[w * h];
                var queue = new int[w * h];
                int head = 0, tail = 0;
                var touchesEdge = false;
                queue[tail++] = seed;
                inside[seed] = true;
                while (head < tail)
                {
                    var i = queue[head++];
                    int x = i % w, y = i / w;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1) touchesEdge = true;
                    void Visit(int j) { if (!inside[j] && Clear(j)) { inside[j] = true; queue[tail++] = j; } }
                    if (x > 0) Visit(i - 1);
                    if (x < w - 1) Visit(i + 1);
                    if (y > 0) Visit(i - w);
                    if (y < h - 1) Visit(i + w);
                }
                if (!touchesEdge && tail > w * h / 50) return new Frame(w, h, LargestRectangle(inside, w, h), hangs);
            }
            return new Frame(w, h, new Box(w * 15 / 100, h * 15 / 100, w * 70 / 100, h * 70 / 100), hangs);
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException) { return null; }
    }

    /// <summary>Largest axis-aligned rectangle of true cells (stack of bar heights, row by row).</summary>
    static Box LargestRectangle(bool[] cell, int w, int h)
    {
        var heights = new int[w + 1];
        var stack = new Stack<(int Start, int Height)>();
        (int Area, int X, int Y, int W, int H) best = default;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) heights[x] = cell[y * w + x] ? heights[x] + 1 : 0;
            stack.Clear();
            for (var x = 0; x <= w; x++)
            {
                var start = x;
                while (stack.Count > 0 && stack.Peek().Height >= heights[x])
                {
                    var (sx, sh) = stack.Pop();
                    if (sh * (x - sx) > best.Area) best = (sh * (x - sx), sx, y - sh + 1, x - sx, sh);
                    start = sx;
                }
                stack.Push((start, heights[x]));
            }
        }
        return new Box(best.X, best.Y, best.W, best.H);
    }

    static XNode El(string name, params (string Key, string Value)[] attrs) => new(name, attrs.ToDictionary(a => a.Key, a => a.Value));
    static XNode Leaf(string name, string text) { var n = El(name); n.Content.Append(text); return n; }

    /// <summary>White text with a dark outline: readable on any of the pictures, light or dark.</summary>
    static XNode Text(string name, Box r, int size, string align, params (string, string)[] more) =>
        El(name, [("rect", $"{r.X},{r.Y},{r.W},{r.H}"), ("font", "Tahoma"), ("font_size", size.ToString()), ("color", "#FFFFFF"), ("bold", "1"), ("italic", "0"),
            ("align", align), ("stroke_color", "#000000"), ("stroke_width", "3"), .. more]);

    /// <summary>The template tree <see cref="InstalledTemplate"/> draws; an empty one (reported as having no main menu) when the pictures are missing.</summary>
    public static XNode Build(string dir)
    {
        var template = El("template");
        var bg1 = Find(dir, "Menu", "background01");
        var bg2 = Find(dir, "Scene", "background02") ?? bg1;
        var frameFile = Find(dir, "Menu", "scrbackground");
        var thumbFile = Find(dir, "Scene", "videobackground");
        if (bg1 == null || frameFile == null || thumbFile == null || Size(Path.Combine(dir, bg1)) is not var (bw, bh)
            || Measure(Path.Combine(dir, frameFile)) is not { } frame || Measure(Path.Combine(dir, thumbFile)) is not { } thumb) return template;

        // every picture of a skin is drawn for its own screen size (1920x1080, 3840x2160 or 768x576): one scale puts them on the 1920x1080 canvas
        var s = Math.Min(W / (double)bw, H / (double)bh);
        int Px(double v) => (int)Math.Round(v * s);
        var full = $"{(W - Px(bw)) / 2},{(H - Px(bh)) / 2},{Px(bw)},{Px(bh)}";
        XNode Bg(string image) { var n = El("bg", ("rect", full), ("fit", "xmidymid")); n.Kids.Add(Leaf("image", image)); return n; }
        static string Rect(int x, int y, int w, int h) => $"{x},{y},{w},{h}";

        var common = El("common");
        common.Kids.AddRange([Leaf("accent", "#FEE453"), Leaf("templatename", Path.GetFileName(dir))]);
        foreach (var (key, stem) in new[] { ("leftbuttontimage", "previous"), ("rightbuttontimage", "next"), ("homebuttontimage", "back") })
            if (Find(dir, "Scene", stem) is { } picture) common.Kids.Add(Leaf(key, picture));

        // ---- main menu: preview in its frame, Play and Scenes under it, the title above (below when the frame hangs from the top)
        int fw = Px(frame.Width), fh = Px(frame.Height);
        var hole = new Box(Px(frame.Hole.X), Px(frame.Hole.Y), Px(frame.Hole.W), Px(frame.Hole.H));
        int fx = W / 2 - (hole.X + hole.W / 2), fy = frame.Hangs ? 0 : 400 - (hole.Y + hole.H / 2);
        var root = El("rootmenu");
        root.Kids.AddRange([Bg(bg1),
            El("video", ("rect", Rect(fx + hole.X, fy + hole.Y, hole.W, hole.H)), ("frame_under", frameFile), ("under_rect", Rect(fx, fy, fw, fh)))]);
        var below = fy + fh + 25;
        var titleBox = frame.Hangs ? new Box(260, Math.Min(H - 130, 930), 1400, 110) : new Box(260, 40, 1400, 110);
        root.Kids.AddRange([Text("label", titleBox, 72, "center"), Text("play", new Box(W / 2 - 250, below, 500, 70), 52, "center", ("textbutton", "1")),
            Text("scenes", new Box(W / 2 - 250, below + 85, 500, 70), 52, "center", ("textbutton", "1"))]);

        // ---- scene page: 3 x 2 thumbnail frames with their names, previous / back / next under them
        int tfw = Px(thumb.Width), tfh = Px(thumb.Height);
        var thumbHole = new Box(Px(thumb.Hole.X), Px(thumb.Hole.Y), Px(thumb.Hole.W), Px(thumb.Hole.H));
        int pitchX = Math.Max(520, tfw + 50), pitchY = tfh + 95, gridHeight = pitchY + tfh + 54;
        int top = 100 + (800 - gridHeight) / 2;
        var scene = El("scene");
        scene.Kids.Add(Bg(bg2 ?? bg1));
        var videos = El("videoes");
        for (var i = 0; i < 6; i++)
        {
            int x = W / 2 + (i % 3 - 1) * pitchX - (thumbHole.X + thumbHole.W / 2), y = top + i / 3 * pitchY;
            var video = El("video", ("rect", Rect(x + thumbHole.X, y + thumbHole.Y, thumbHole.W, thumbHole.H)), ("frame_under", thumbFile), ("under_rect", Rect(x, y, tfw, tfh)));
            video.Kids.Add(Text("name", new Box(x - 30, y + tfh + 4, tfw + 60, 50), 26, "center"));
            videos.Kids.Add(video);
        }
        foreach (var (role, stem, cx) in new[] { ("left", "previous", W / 2 - 230), ("home", "back", W / 2), ("right", "next", W / 2 + 230) })
            if (Find(dir, "Scene", stem) is { } picture && Size(Path.Combine(dir, picture)) is var (pw, ph))
                scene.Kids.Add(El(role, ("rect", Rect(cx - Px(pw) / 2, 960 - Px(ph) / 2, Px(pw), Px(ph)))));
        scene.Kids.AddRange([Text("label", new Box(60, 24, 800, 80), 48, "left"), videos]);
        var tscenes = El("tscenes");
        tscenes.Kids.Add(scene);
        var scenes = El("scenes");
        scenes.Kids.Add(tscenes);
        template.Kids.AddRange([common, root, scenes]);
        return template;
    }
}
