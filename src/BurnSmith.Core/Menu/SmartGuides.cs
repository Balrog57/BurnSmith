namespace BurnSmith.Core.Menu;

/// <summary>
/// The alignment marks of the menu editor, as a picture editor shows them. While an element is moved or resized, its edges and its middle are
/// compared with those of the page and of the other things on it: within a few pixels they meet exactly, and a line says with what. A moved
/// element also finds the place that keeps a row or a column evenly spaced (the third button as far from the second as the second from the first).
/// The pull is short on purpose: it lands an alignment the eye was already aiming at, it does not drag the element about.
/// </summary>
public static class SmartGuides
{
    /// <summary>A line to draw on the page. Vertical: at x = <paramref name="At"/> from y = From to To; else at y = At from x = From to To.
    /// <paramref name="Spacing"/>: it measures a gap that equals another, instead of joining aligned edges.</summary>
    public readonly record struct Mark(bool Vertical, int At, int From, int To, bool Spacing = false);

    /// <summary>How near, in pixels of the 1920x1080 page (about two on the editor's screen), an edge has to come to be taken.</summary>
    public const int Reach = 6;

    static readonly Box Page = new(0, 0, MenuBuilder.W, MenuBuilder.H);

    // left / middle / right (or top / middle / bottom) in half pixels, so that the middle of an odd width is a whole number
    static int[] Lines(int start, int size) => [2 * start, 2 * start + size, 2 * (start + size)];

    /// <summary>The shift (in pixels) that brings one of <paramref name="mine"/> onto the nearest line of the others, or null when none is within reach.</summary>
    static int? Pull(int[] mine, IEnumerable<int[]> theirs, int reach)
    {
        var best = theirs.SelectMany(t => t).SelectMany(t => mine.Select(m => t - m)).Where(d => Math.Abs(d) <= 2 * reach).OrderBy(Math.Abs).Select(d => (int?)d).FirstOrDefault();
        return best is { } d2 ? (int)Math.Round(d2 / 2.0, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>The element where it lands and the marks to show. <paramref name="others"/>: every other rectangle of the page (elements, buttons).</summary>
    public static (Box Box, List<Mark> Marks) Move(Box moving, IReadOnlyList<Box> others, int reach = Reach)
    {
        var all = others.Append(Page).ToList();
        var marks = new List<Mark>();
        var dx = Pull(Lines(moving.X, moving.W), all.Select(o => Lines(o.X, o.W)), reach);
        var dy = Pull(Lines(moving.Y, moving.H), all.Select(o => Lines(o.Y, o.H)), reach);
        var b = new Box(moving.X + (dx ?? 0), moving.Y + (dy ?? 0), moving.W, moving.H);
        // no edge to meet on an axis: the place that keeps the spacing of the others even, if it is near
        if (dx == null && Even(b.X, b.W, others.Where(o => o.Y < b.Y + b.H && b.Y < o.Y + o.H).Select(o => (o.X, o.W)).ToList(), reach) is { } ex)
        {
            b = b with { X = ex.At };
            marks.AddRange(ex.Gaps.Select(g => new Mark(false, b.Y + b.H / 2, g.From, g.To, true)));
        }
        if (dy == null && Even(b.Y, b.H, others.Where(o => o.X < b.X + b.W && b.X < o.X + o.W).Select(o => (o.Y, o.H)).ToList(), reach) is { } ey)
        {
            b = b with { Y = ey.At };
            marks.AddRange(ey.Gaps.Select(g => new Mark(true, b.X + b.W / 2, g.From, g.To, true)));
        }
        marks.AddRange(Aligned(b, all));
        return (b, marks);
    }

    /// <summary>The same for a corner being dragged: only the edges that move are compared (<paramref name="left"/>: the left one, else the right; <paramref name="top"/> likewise).</summary>
    public static (Box Box, List<Mark> Marks) Resize(Box box, bool left, bool top, IReadOnlyList<Box> others, int reach = Reach, int minW = 1, int minH = 1)
    {
        var all = others.Append(Page).ToList();
        int x0 = box.X, x1 = box.X + box.W, y0 = box.Y, y1 = box.Y + box.H;
        if (Pull([2 * (left ? x0 : x1)], all.Select(o => Lines(o.X, o.W)), reach) is { } dx) { if (left) x0 += dx; else x1 += dx; }
        if (Pull([2 * (top ? y0 : y1)], all.Select(o => Lines(o.Y, o.H)), reach) is { } dy) { if (top) y0 += dy; else y1 += dy; }
        if (x1 - x0 < minW || y1 - y0 < minH) return (box, []);
        var b = new Box(x0, y0, x1 - x0, y1 - y0);
        return (b, Aligned(b, all, left ? 0 : 2, top ? 0 : 2));
    }

    /// <summary>A line for every edge or middle of <paramref name="b"/> that is exactly one of another rectangle's, drawn across both.</summary>
    static List<Mark> Aligned(Box b, List<Box> all, int? onlyX = null, int? onlyY = null)
    {
        var marks = new List<Mark>();
        int[] mx = Lines(b.X, b.W), my = Lines(b.Y, b.H);
        foreach (var o in all)
        {
            for (var i = 0; i < 3; i++)
            {
                if ((onlyX == null || onlyX == i) && Lines(o.X, o.W).Contains(mx[i])) marks.Add(new Mark(true, mx[i] / 2, Math.Min(b.Y, o.Y), Math.Max(b.Y + b.H, o.Y + o.H)));
                if ((onlyY == null || onlyY == i) && Lines(o.Y, o.H).Contains(my[i])) marks.Add(new Mark(false, my[i] / 2, Math.Min(b.X, o.X), Math.Max(b.X + b.W, o.X + o.W)));
            }
        }
        // one line per place, as long as the longest that goes there
        return marks.GroupBy(m => (m.Vertical, m.At)).Select(g => new Mark(g.Key.Vertical, g.Key.At, g.Min(m => m.From), g.Max(m => m.To))).ToList();
    }

    /// <summary>
    /// Along one axis: where an element of <paramref name="size"/> near <paramref name="start"/> makes equal gaps with its neighbours — after two
    /// of them (the same gap again), before two of them, or half-way between two. Returns that place and the gaps it makes equal.
    /// </summary>
    static (int At, List<(int From, int To)> Gaps)? Even(int start, int size, List<(int Start, int Size)> row, int reach)
    {
        var sorted = row.OrderBy(r => r.Start).ToList();
        (int At, List<(int, int)> Gaps)? best = null;
        void Try(int at, params (int, int)[] gaps)
        {
            if (Math.Abs(at - start) <= reach && (best == null || Math.Abs(at - start) < Math.Abs(best.Value.At - start))) best = (at, gaps.ToList());
        }
        for (var i = 0; i + 1 < sorted.Count; i++)
        {
            var (a, z) = (sorted[i], sorted[i + 1]);
            var gap = z.Start - (a.Start + a.Size);
            if (gap < 0) continue;
            Try(z.Start + z.Size + gap, (a.Start + a.Size, z.Start), (z.Start + z.Size, z.Start + z.Size + gap));       // after the pair
            Try(a.Start - gap - size, (a.Start - gap, a.Start), (a.Start + a.Size, z.Start));                            // before it
            if (gap >= size) Try(a.Start + a.Size + (gap - size) / 2, (a.Start + a.Size, a.Start + a.Size + (gap - size) / 2), (z.Start - (gap - size) / 2, z.Start)); // between
        }
        return best;
    }
}
