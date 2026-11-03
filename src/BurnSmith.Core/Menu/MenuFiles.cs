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
            return list;
        });
        foreach (var (name, data) in images) File.WriteAllBytes(Path.Combine(dir, name), data);

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
                    _ => $"page {b.Action.Page}",
                };
                txt.Append($"button {p.Id} {b.Id} {r.X} {r.Y} {r.W} {r.H} hl_{p.Id}_{b.Id}.png {N(b.Left)} {N(b.Right)} {N(b.Up)} {N(b.Down)} {action}\n");
            }
        }
        File.WriteAllText(Path.Combine(dir, "menu.txt"), txt.ToString(), new UTF8Encoding(false));
    }
}
