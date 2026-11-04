using System.Text;

namespace BurnSmith.Core.Menu;

public static class MenuFiles
{
    /// <summary>
    /// Writes what the BD-J xlet reads (bdj/.../Menu.java): menu.txt plus one JPEG per page and one PNG per button focus frame.
    /// With <paramref name="loopPlaylist"/> the xlet plays that playlist behind the pages; they are then transparent PNG overlays when
    /// <paramref name="overlay"/> (a moving menu), else opaque JPEGs as usual (the playlist only carries the menu music).
    /// </summary>
    public static void WriteBdj(MenuDesign design, IReadOnlyList<MenuPage> pages, string dir, int loopPlaylist = 0, double scale = 1, PlaybackOptions? playback = null, int titles = 0, bool overlay = false)
    {
        Directory.CreateDirectory(dir);
        var ext = overlay ? "png" : "jpg";
        int Px(int v) => MenuRenderer.Px(v, scale); // a 720p disc draws its 1920x1080 layout at 2/3
        var txt = new StringBuilder($"size {Px(MenuBuilder.W)} {Px(MenuBuilder.H)}\n");
        if (loopPlaylist > 0) txt.Append($"loop {loopPlaylist}\n");
        if (playback != null && titles > 0)
            txt.Append($"playback {(playback.StartAtFirstTitle ? "first" : "menu")} {(playback.PlayAll ? 1 : 0)} {(playback.Rewind ? 1 : 0)} {BluRay.MoviePlaylist} {titles}\n");
        var images = MenuRenderer.Sta(() =>
        {
            var list = new List<(string Name, byte[] Data)>();
            foreach (var p in pages)
            {
                list.Add(($"bg_{p.Id}.{ext}", overlay ? MenuRenderer.Overlay(design, p, scale) : MenuRenderer.Background(design, p, scale)));
                foreach (var b in p.Buttons) list.Add(($"hl_{p.Id}_{b.Id}.png", MenuRenderer.Focus(design, b, scale)));
            }
            list.Add(("mark.png", MenuRenderer.Marker(design, MenuBuilder.MarkerSize, scale)));
            return list;
        });
        foreach (var (name, data) in images) File.WriteAllBytes(Path.Combine(dir, name), data);
        // the active audio / subtitle is marked by the xlet while the menu runs: image, size, offset from the button's left edge (which includes the focus margin)
        txt.Append($"marker mark.png {Px(MenuBuilder.MarkerSize)} {Px(MenuBuilder.MarkerSize)} {Px(MenuBuilder.MarkerOffset(design))}\n");

        foreach (var p in pages)
        {
            txt.Append($"page {p.Id} bg_{p.Id}.{ext}\n");
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
                txt.Append($"button {p.Id} {b.Id} {Px(r.X)} {Px(r.Y)} {Px(r.W)} {Px(r.H)} hl_{p.Id}_{b.Id}.png {N(b.Left)} {N(b.Right)} {N(b.Up)} {N(b.Down)} {action}\n");
            }
        }
        File.WriteAllText(Path.Combine(dir, "menu.txt"), txt.ToString(), new UTF8Encoding(false));
    }
}
