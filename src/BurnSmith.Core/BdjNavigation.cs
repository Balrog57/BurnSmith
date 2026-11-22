namespace BurnSmith.Core;

internal static class BdjNavigation
{
    static int Number(string[] row, int index, int fallback = -1) =>
        index < row.Length && int.TryParse(row[index], out var value) ? value : fallback;

    internal static void Trace(List<string[]> rows, Func<int, Mpls?> playlist, List<string> trace,
                               Action<string> problem, int audioChoice, int subtitleChoice)
    {
        var pages = rows.Where(row => row is ["page", _, _, ..]).Select(row => row[1]).ToList();
        if (pages.Count == 0) { problem("aucune page dans le menu"); return; }
        if (pages.Distinct().Count() != pages.Count) problem("identifiants de pages en double");
        var root = pages[0];
        var buttons = rows.Where(row => row is ["button", _, _, ..] && row.Length >= 13).ToLookup(row => row[1]);
        var playback = rows.FirstOrDefault(row => row[0] == "playback") ?? [];
        var first = Math.Clamp(Number(playback, 4, BluRay.MoviePlaylist), 1, 99000);
        var count = Math.Clamp(Number(playback, 5, 0), 0, 999);
        var startsFirst = playback is [_, "first", ..];
        var chain = Number(playback, 2) == 1;
        var rewind = Number(playback, 3) == 1;
        var defaults = rows.FirstOrDefault(row => row[0] == "defaults") ?? [];
        var audio = audioChoice > 0 ? audioChoice : Number(defaults, 1, 1);
        var subtitle = subtitleChoice >= 0 ? subtitleChoice : Number(defaults, 2, 0);
        var pending = new Queue<string>();
        var seen = new HashSet<string>();
        var titles = new HashSet<int>();
        var subtitleTracks = rows.Where(row => row[0] == "subtracks").ToLookup(row => Number(row, 1));
        var forced = rows.Where(row => row[0] == "forced-track").ToLookup(row => Number(row, 1));
        var endings = rows.Where(row => row is ["title-end", _, _, ..]).ToLookup(row => Number(row, 1));

        void Open(string page)
        {
            if (!pages.Contains(page)) problem($"page cible {page} inexistante");
            else if (seen.Add(page)) pending.Enqueue(page);
        }
        void Play(int title, int end, int mark = -1)
        {
            if (title is <= 0 or > 99999 || end <= title || end > 100000 || end - title > 999)
            {
                problem($"séquence de lecture invalide ({title}, {end})");
                return;
            }
            for (var n = title; n < end; n++)
            {
                if (playlist(n) is not { } list) { problem($"liste de lecture {n:00000} inexistante"); continue; }
                if (!titles.Add(n)) continue;
                foreach (var item in list.Items)
                {
                    var played = item.Audio.Count >= audio ? audio : item.Audio.Count > 0 ? 1 : 0;
                    var visible = subtitleTracks[n].FirstOrDefault();
                    var selected = subtitle > 0 && (visible == null || visible.Skip(2).Any(s => int.TryParse(s, out var slot) && slot == subtitle))
                        ? subtitle : 0;
                    var own = forced[n].FirstOrDefault(row => Number(row, 2) == played);
                    if (visible == null) own = rows.FirstOrDefault(row => row[0] == "forced" && Number(row, 1) == played);
                    var shown = selected > 0 ? selected : own == null ? 0 : Number(own, visible == null ? 2 : 3, 0);
                    var sound = played > 0 ? $"{played} ({item.Audio[played - 1].Lang})" : "aucun";
                    var text = shown > 0 && shown <= item.Pg.Count
                        ? $"{shown} ({item.Pg[shown - 1].Lang}{(selected == 0 ? ", forcé" : "")})" : "aucun";
                    trace.Add($"Liste {n:00000}, clip {item.Clip} | audio {sound} | sous-titres {text}");
                }
                if (pages.Contains($"pop{n}"))
                {
                    trace.Add($"Liste {n:00000} → touche Pop-up → pop{n}");
                    Open($"pop{n}");
                }
                if (endings[n].FirstOrDefault() is { } ending)
                {
                    var target = ending[2] switch
                    {
                        "repeat" => $"liste {n:00000}",
                        "stop" => "arrêt",
                        "next" when n >= first && n + 1 < first + count => $"liste {n + 1:00000}",
                        _ => root
                    };
                    trace.Add($"Liste {n:00000} → fin configurée → {target}");
                    if (ending[2] != "next" || target == root) return;
                    end = Math.Max(end, n + 2);
                }
            }
            var finish = !chain || rewind ? chain && startsFirst ? $"liste {first:00000}" : root : "arrêt";
            trace.Add($"Lecture {title:00000}{(mark >= 0 ? $" chapitre {mark + 1}" : "")} à {end - 1:00000} → fin → {finish}");
        }

        trace.Add(startsFirst ? $"Entrée BD-J → liste {first:00000}" : $"Entrée BD-J → {root}");
        trace.Add($"Top Menu → {root}");
        Open(root);
        if (startsFirst) Play(first, first + Math.Max(count, 1));
        else if (rows.FirstOrDefault(row => row[0] == "auto-start") is { } timer && Number(timer, 1) > 0 && count > 0)
        {
            trace.Add($"{root} → {Number(timer, 1)} s sans action → liste {first:00000}");
            Play(first, first + (chain ? count : 1));
        }
        while (pending.TryDequeue(out var page))
        {
            var byId = new Dictionary<string, string[]>();
            foreach (var row in buttons[page])
                if (!byId.TryAdd(row[2], row)) problem($"bouton {page}/{row[2]} en double");
            var focus = new Queue<string>();
            var focused = new HashSet<string>();
            if (byId.Keys.FirstOrDefault() is { } initial) { focused.Add(initial); focus.Enqueue(initial); }
            else problem($"page {page} sans bouton");
            while (focus.TryDequeue(out var id))
            {
                var row = byId[id];
                string[] directions = ["gauche", "droite", "haut", "bas"];
                for (var i = 0; i < directions.Length; i++)
                {
                    var direction = row[8 + i];
                    if (direction == "-") continue;
                    if (!byId.ContainsKey(direction)) problem($"bouton {page}/{id} : direction vers {direction} inexistante");
                    else
                    {
                        trace.Add($"{page}/{id} → {directions[i]} → {page}/{direction}");
                        if (focused.Add(direction)) focus.Enqueue(direction);
                    }
                }
                trace.Add($"{page}/{id} → OK → {string.Join(' ', row[12..])}");
                switch (row[12])
                {
                    case "page":
                        Open(row.Length > 13 ? row[13] : "(vide)");
                        break;
                    case "play":
                    case "chapter":
                    case "playall":
                        var title = Number(row, 13);
                        var end = row[12] == "playall" ? (long)title + Number(row, 14)
                            : chain && title >= first && title < first + (long)count ? first + (long)count : title + 1L;
                        Play(title, end is > 0 and <= 100000 ? (int)end : 0, row[12] == "chapter" ? Number(row, 14) : -1);
                        break;
                }
            }
            foreach (var id in byId.Keys.Except(focused)) problem($"bouton {page}/{id} inaccessible avec les flèches");
        }
        foreach (var page in pages.Except(seen)) problem($"page {page} inaccessible");
    }
}
