namespace BurnSmith.Core.Menu;

/// <summary>
/// Unified navigation graph analyzer for BD-J, HDMV and DVD menus: inspects the exact pages and buttons built by
/// <see cref="MenuBuilder"/> to report transitions, unreachable pages and dead-end pages before building the disc.
/// </summary>
public static class MenuNavigationMap
{
    public sealed record Report(List<string> Lines, List<string> Warnings);

    public static Report Analyze(IReadOnlyList<MenuPage> pages, PlaybackOptions? playback = null, string mode = "BD-J")
    {
        var lines = new List<string>();
        var warnings = new List<string>();
        if (pages.Count == 0)
        {
            warnings.Add("Le menu ne contient aucune page.");
            return new(lines, warnings);
        }

        var byId = pages.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var start = pages[0].Id;
        lines.Add($"Mode : {mode} · {pages.Count} page{(pages.Count > 1 ? "s" : "")}");
        if (playback != null)
        {
            var entry = playback.StartAtFirstTitle ? "Démarrage direct au 1er titre" : $"Démarrage sur « {start} »";
            var auto = !playback.StartAtFirstTitle && playback.AutoStartSeconds > 0 ? $" (auto-démarrage après {playback.AutoStartSeconds} s)" : "";
            var resume = playback.RememberPosition ? " · reprise entre sessions activée" : "";
            lines.Add($"{entry}{auto}{resume}");
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!byId.TryGetValue(id, out var page)) continue;
            foreach (var btn in page.Buttons)
            {
                if (btn.Action.Kind == "page" && !string.IsNullOrEmpty(btn.Action.Page))
                {
                    if (!byId.ContainsKey(btn.Action.Page))
                        warnings.Add($"Page « {id} », bouton « {btn.Id} » : cible « {btn.Action.Page} » introuvable.");
                    else if (reachable.Add(btn.Action.Page))
                        queue.Enqueue(btn.Action.Page);
                }
            }
        }

        foreach (var page in pages)
        {
            if (!reachable.Contains(page.Id))
                warnings.Add($"Page « {page.Id} » inaccessible depuis le menu principal.");
            var exits = page.Buttons.Any(b => b.Action.Kind is "page" or "play" or "playall" or "menu" or "resume");
            if (page.Id != start && !exits)
                warnings.Add($"Page « {page.Id} » sans issue (aucun bouton de retour ou de lecture).");

            lines.Add("");
            lines.Add($"[{page.Id}] ({page.Buttons.Count} bouton{(page.Buttons.Count > 1 ? "s" : "")})");
            foreach (var btn in page.Buttons)
            {
                var target = btn.Action.Kind switch
                {
                    "page" => $"→ page {btn.Action.Page}",
                    "play" => btn.Action.Mark > 0 ? $"→ titre {btn.Action.Playlist}, chap. {btn.Action.Mark}" : $"→ titre {btn.Action.Playlist}",
                    "playall" => "→ tout lire",
                    "audio" => $"→ audio {btn.Action.Playlist}",
                    "sub" => btn.Action.Playlist <= 0 ? "→ sous-titres désactivés" : $"→ sous-titre {btn.Action.Playlist}",
                    "resume" => "→ reprendre la lecture",
                    "menu" => "→ menu principal",
                    "popup" => "→ fermer le pop-up",
                    _ => $"→ {btn.Action.Kind}"
                };
                lines.Add($"  • {btn.Id} {target}");
            }
        }
        return new(lines, warnings);
    }
}
