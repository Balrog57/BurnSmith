using System.Text;

namespace BurnSmith.Core.Menu;

public static class MenuFiles
{
    /// <summary>Writes what the BD-J xlet reads (bdj/.../Menu.java): menu.txt plus one JPEG per page and one PNG per button focus frame.</summary>
    public static void WriteBdj(MenuDesign design, IReadOnlyList<MenuPage> pages, string dir)
    {
        Directory.CreateDirectory(dir);
        var txt = new StringBuilder($"size {MenuBuilder.W} {MenuBuilder.H}\n");
        var images = MenuRenderer.Sta(() =>
        {
            var list = new List<(string Name, byte[] Data)>();
            foreach (var p in pages)
            {
                list.Add(($"bg_{p.Id}.jpg", MenuRenderer.Background(design, p)));
                foreach (var b in p.Buttons) list.Add(($"hl_{p.Id}_{b.Id}.png", MenuRenderer.Focus(design, b)));
            }
            list.Add(("mark.png", MenuRenderer.Marker(design, MenuBuilder.MarkerSize)));
            return list;
        });
        foreach (var (name, data) in images) File.WriteAllBytes(Path.Combine(dir, name), data);
        // the active audio / subtitle is marked by the xlet while the menu runs: image, size, offset from the button's left edge (which includes the focus margin)
        txt.Append($"marker mark.png {MenuBuilder.MarkerSize} {MenuBuilder.MarkerSize} {MenuBuilder.FocusMargin - MenuBuilder.MarkerSize - MenuBuilder.MarkerGap}\n");

        foreach (var p in pages)
        {
            txt.Append($"page {p.Id} bg_{p.Id}.jpg\n");
            foreach (var b in p.Buttons)
            {
                var r = b.Rect.Inflate(MenuBuilder.FocusMargin);
                string N(string? s) => s ?? "-";
                var action = b.Action.Kind switch
                {
                    "play" => $"play {b.Action.Playlist}",
                    "chapter" => $"chapter {b.Action.Playlist} {b.Action.Mark}",
                    "playall" => $"playall {b.Action.Playlist} {b.Action.Mark}",
                    "audio" => $"audio {b.Action.Mark}",
                    "sub" => $"sub {b.Action.Mark}",
                    _ => $"page {b.Action.Page}",
                };
                txt.Append($"button {p.Id} {b.Id} {r.X} {r.Y} {r.W} {r.H} hl_{p.Id}_{b.Id}.png {N(b.Left)} {N(b.Right)} {N(b.Up)} {N(b.Down)} {action}\n");
            }
        }
        File.WriteAllText(Path.Combine(dir, "menu.txt"), txt.ToString(), new UTF8Encoding(false));
    }
}
