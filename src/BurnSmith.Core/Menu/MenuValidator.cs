namespace BurnSmith.Core.Menu;

/// <summary>Live normative check on a menu page for DVD, HDMV, or BD-J authoring.</summary>
public static class MenuValidator
{
    public sealed record Issue(string Severity, string Kind, string PageId, string Message, Box? Rect = null, string? ButtonId = null, string? TargetId = null);

    public static List<Issue> Validate(MenuDesign d, MenuPage page, string format = "bdj")
    {
        var issues = new List<Issue>();
        var isDvd = format.Equals("dvd", StringComparison.OrdinalIgnoreCase);

        // 1. Overlapping buttons
        for (var i = 0; i < page.Buttons.Count; i++)
        {
            var a = page.Buttons[i];
            for (var j = i + 1; j < page.Buttons.Count; j++)
            {
                var b = page.Buttons[j];
                if (Intersects(a.Rect, b.Rect))
                {
                    issues.Add(new Issue("error", "overlap", page.Id,
                        $"Boutons superposés sur « {page.Id} » : {a.Id} et {b.Id}.", a.Rect, a.Id, b.Id));
                    issues.Add(new Issue("error", "overlap", page.Id,
                        $"Boutons superposés sur « {page.Id} » : {b.Id} et {a.Id}.", b.Rect, b.Id, a.Id));
                }
            }
        }

        // 2. DVD subpicture button limit: 18 in 16:9 widescreen, 36 in 4:3
        if (isDvd)
        {
            var limit = TemplateShape.IsNarrow(d.Name) ? 36 : 18;
            if (page.Buttons.Count > limit)
            {
                for (var k = limit; k < page.Buttons.Count; k++)
                {
                    var extra = page.Buttons[k];
                    issues.Add(new Issue("error", "limit", page.Id,
                        $"Norme DVD : maximum {limit} boutons par page dépassé ({page.Buttons.Count}).", extra.Rect, extra.Id, limit.ToString()));
                }
            }
        }

        // 3. Unreachable (orphan) buttons from default button [0] via remote D-pad links
        if (page.Buttons.Count > 1)
        {
            var byId = page.Buttons.ToDictionary(b => b.Id, StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            visited.Add(page.Buttons[0].Id);
            queue.Enqueue(page.Buttons[0].Id);
            while (queue.Count > 0)
            {
                if (!byId.TryGetValue(queue.Dequeue(), out var cur)) continue;
                foreach (var next in new[] { cur.Left, cur.Right, cur.Up, cur.Down })
                    if (!string.IsNullOrEmpty(next) && byId.ContainsKey(next) && visited.Add(next))
                        queue.Enqueue(next);
            }
            foreach (var b in page.Buttons)
                if (!visited.Contains(b.Id))
                    issues.Add(new Issue("warning", "orphan", page.Id,
                        $"Bouton inatteignable à la télécommande sur « {page.Id} » : {b.Id}.", b.Rect, b.Id));
        }

        // 4. TV title-safe area (90 %)
        foreach (var (what, rect) in SafeArea.Outside(page, d))
            issues.Add(new Issue("warning", "safe", page.Id,
                $"Hors zone sûre TV (90 %) sur « {page.Id} » : {what}.", rect, what));

        return issues;
    }

    static bool Intersects(Box a, Box b) =>
        a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;
}
