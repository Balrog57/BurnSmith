using System.Text;

namespace BurnSmith.Core.Menu;

public static class MenuFiles
{
    /// <summary>The text drawn on a button, for the highlight styles that recolour it (the text-colour highlight style). Null = no label of its own.</summary>

    /// <summary>
    /// Writes what the BD-J xlet reads (bdj/.../Menu.java): menu.txt plus one JPEG per page and one PNG per button focus frame.
    /// With <paramref name="loopPlaylist"/> the xlet plays that playlist behind the pages; they are then transparent PNG overlays when
    /// <paramref name="overlay"/> (a moving menu), else opaque JPEGs as usual (the playlist only carries the menu music).
    /// </summary>
    public static void WriteBdj(MenuDesign design, IReadOnlyList<MenuPage> pages, string dir, int loopPlaylist = 0, double scale = 1, PlaybackOptions? playback = null, int titles = 0, bool overlay = false, int defaultSubtitle = 0, IReadOnlyList<MenuPage>? popup = null,
        IEnumerable<(int Playlist, IEnumerable<int> Visible, IReadOnlyList<(int Audio, int Sub)> Forced)>? subtitleTracks = null, Action<string>? log = null, int loopReturnMark = -1,
        IEnumerable<(int Playlist, string? Action)>? titleEnds = null, int hdrGraphicsNits = 0,
        IReadOnlyDictionary<string, (int Playlist, bool Overlay, IReadOnlyList<Box>? Cutouts)>? pageLoops = null)
    {
        Directory.CreateDirectory(dir);
        var ext = overlay ? "png" : "jpg";
        var look = MenuBuilder.PopupDesign(design);
        var trackMarker = MenuBuilder.TrackMarker(design);
        var marker = trackMarker?.Bounds ?? new Box(MenuBuilder.MarkerOffset(design), 0, MenuBuilder.MarkerSize, MenuBuilder.MarkerSize);
        int Px(int v) => MenuRenderer.Px(v, scale); // a 720p disc draws its 1920x1080 layout at 2/3
        var txt = new StringBuilder($"size {Px(MenuBuilder.W)} {Px(MenuBuilder.H)}\n");
        if (loopPlaylist > 0) txt.Append($"loop {loopPlaylist}\n");
        if (loopPlaylist > 0 && loopReturnMark >= 0) txt.Append($"loop-return-mark {loopReturnMark}\n");
        var hasSounds = design.Sounds != null && !string.IsNullOrWhiteSpace(design.Sounds.Pack) && !design.Sounds.Pack.Equals("none", StringComparison.OrdinalIgnoreCase);
        if (hasSounds)
        {
            File.WriteAllBytes(Path.Combine(dir, "snd_move.wav"), SoundBank.BuildWav(design.Sounds!, false));
            File.WriteAllBytes(Path.Combine(dir, "snd_select.wav"), SoundBank.BuildWav(design.Sounds!, true));
            txt.Append("sound-move snd_move.wav\n");
            txt.Append("sound-select snd_select.wav\n");
        }
        if (playback != null && titles > 0)
            txt.Append($"playback {(playback.StartAtFirstTitle ? "first" : "menu")} {(playback.PlayAll ? 1 : 0)} {(playback.Rewind ? 1 : 0)} {BluRay.MoviePlaylist} {titles}\n");
        if (playback is { StartAtFirstTitle: false, AutoStartSeconds: > 0 } && titles > 0)
            txt.Append($"auto-start {Math.Min(300, playback.AutoStartSeconds)}\n");
        if (playback?.RememberPosition == true && titles > 0)
            txt.Append($"resume-key {Guid.NewGuid():N}\n");
        if (design.PageFadeMs > 0) txt.Append($"page-fade {Math.Min(500, design.PageFadeMs)}\n");
        foreach (var (playlist, action) in titleEnds ?? [])
            if (action is "menu" or "stop" or "repeat" or "next") txt.Append($"title-end {playlist} {action}\n");
        if (defaultSubtitle > 0) txt.Append($"defaults 1 {defaultSubtitle}\n"); // the audio is always the first (the default one goes first), the subtitle starts as chosen
        foreach (var (playlist, visible, forced) in subtitleTracks ?? [])
        {
            txt.Append($"subtracks {playlist} {string.Join(' ', visible)}\n");
            foreach (var (audio, sub) in forced) txt.Append($"forced-track {playlist} {audio} {sub}\n");
        }
        var images = MenuRenderer.Sta(() =>
        {
            byte[] Calibrate(byte[] data, bool jpeg) => hdrGraphicsNits is > 0 and < 400 ? MenuRenderer.ScaleImageForHdr(data, hdrGraphicsNits, jpeg) : data;
            var list = new List<(string Name, byte[] Data)>();
            foreach (var p in pages)
            {
                var pInfo = pageLoops != null && pageLoops.TryGetValue(p.Id, out var pl) ? pl : (Playlist: loopPlaylist, Overlay: overlay, Cutouts: (IReadOnlyList<Box>?)null);
                var pExt = pInfo.Overlay ? "png" : "jpg";
                var bgBytes = pInfo.Overlay ? MenuRenderer.Overlay(design, p, scale) : MenuRenderer.Background(design, p, scale);
                if (pInfo.Cutouts is { Count: > 0 } && !pInfo.Overlay)
                    bgBytes = MenuRenderer.PunchAlphaCutouts(MenuRenderer.Background(design, p, scale), pInfo.Cutouts, scale);
                else if (pInfo.Cutouts is { Count: > 0 })
                    bgBytes = MenuRenderer.PunchAlphaCutouts(MenuRenderer.Background(design, p, scale), pInfo.Cutouts, scale);
                list.Add(($"bg_{p.Id}.{pExt}", Calibrate(bgBytes, !pInfo.Overlay)));
                foreach (var b in p.Buttons)
                {
                    list.Add(($"hl_{p.Id}_{b.Id}.png", Calibrate(MenuRenderer.Focus(design, p, b, scale), false)));
                    if (b.DvdSelectImage != null)
                        list.Add(($"act_{p.Id}_{b.Id}.png", Calibrate(MenuRenderer.Activated(design, p, b, scale), false)));
                }
            }
            // the pop-up pages go over the film: always transparent overlays, in BurnSmith's own layout
            foreach (var p in popup ?? [])
            {
                list.Add(($"bg_{p.Id}.png", Calibrate(MenuRenderer.Overlay(look, p, scale), false)));
                foreach (var b in p.Buttons) list.Add(($"hl_{p.Id}_{b.Id}.png", Calibrate(MenuRenderer.Focus(look, p, b, scale), false)));
            }
            list.Add(("mark.png", Calibrate(trackMarker is { } art ? MenuRenderer.Marker(art.Path, art.Bounds, scale)
                : MenuRenderer.Marker(design, MenuBuilder.MarkerSize, scale), false)));
            return list;
        });
        foreach (var (name, data) in images) File.WriteAllBytes(Path.Combine(dir, name), data);
        var hasCursorPreset = design.CursorPreset is "pulse" or "ring" or "arrow" or "custom" || pages.Any(p => p.Id == "root" && p.Buttons.Any(b => b.FocusSlot != null));
        var focusFrames = hasCursorPreset
            ? (design.FocusFrames ?? MenuRenderer.BuiltInCursorFrames(design.CursorPreset, design.Accent) ?? [])
                .Select(path => ElementLibrary.Resolve(path) ?? path).ToArray()
            : [];
        var animatedFocus = focusFrames.Length >= 2 && MenuRenderer.Sta(() => focusFrames.All(path =>
            File.Exists(path) && MenuRenderer.Size(path) is { W: > 0 and <= 8192, H: > 0 and <= 8192 }));
        if (focusFrames.Length >= 2 && !animatedFocus)
            log?.Invoke("Menu BD-J : image d'animation du curseur manquante ou illisible, surbrillance fixe utilisée.");
        if (animatedFocus)
        {
            txt.Append("focus-frames");
            for (int i = 0; i < focusFrames.Length; i++)
            {
                var name = $"focus_frame_{i}.png";
                File.Copy(focusFrames[i], Path.Combine(dir, name), true);
                txt.Append(' ').Append(name);
            }
            txt.Append('\n');
        }
        // the active audio / subtitle is marked by the xlet while the menu runs: image, size, offset from the button's left edge (which includes the focus margin)
        txt.Append($"marker mark.png {Px(marker.W)} {Px(marker.H)} {Px(marker.X)}\n");

        foreach (var p in pages.Concat(popup ?? []))
        {
            var isMainPage = pages.Contains(p);
            var pInfo = isMainPage && pageLoops != null && pageLoops.TryGetValue(p.Id, out var pl) ? pl : (Playlist: loopPlaylist, Overlay: overlay, Cutouts: (IReadOnlyList<Box>?)null);
            var pExt = isMainPage ? (pInfo.Overlay ? "png" : "jpg") : "png";
            // a pop-up page is BurnSmith's layout: its own marker offset, whatever the template's settings page uses
            txt.Append(isMainPage ? $"page {p.Id} bg_{p.Id}.{pExt}\n" :
                $"page {p.Id} bg_{p.Id}.png {Px(MenuBuilder.MarkerOffset(look) + MenuBuilder.MarkerSize - marker.W)}\n");
            if (isMainPage && pInfo.Playlist != loopPlaylist && pInfo.Playlist > 0)
                txt.Append($"page-loop {p.Id} {pInfo.Playlist}\n");
            foreach (var b in p.Buttons)
            {
                var r = MenuRenderer.FocusBox(b);
                string N(string? s) => s ?? "-";
                var action = b.Action.Kind switch
                {
                    "play" => $"play {b.Action.Playlist}",
                    "chapter" => $"chapter {b.Action.Playlist} {b.Action.Mark}",
                    "playall" => $"playall {b.Action.Playlist} {b.Action.Mark}",
                    "audio" => $"audio {b.Action.Mark}",
                    "sub" => $"sub {b.Action.Mark}",
                    "resume" or "popup" or "menu" => b.Action.Kind,
                    _ => $"page {b.Action.Page}",
                };
                txt.Append($"button {p.Id} {b.Id} {Px(r.X)} {Px(r.Y)} {Px(r.W)} {Px(r.H)} hl_{p.Id}_{b.Id}.png {N(b.Left)} {N(b.Right)} {N(b.Up)} {N(b.Down)} {action}\n");
                if (b.DvdSelectImage != null)
                    txt.Append($"button-act {p.Id} {b.Id} act_{p.Id}_{b.Id}.png\n");
                if (animatedFocus)
                {
                    var slot = b.FocusSlot ?? new Box(Math.Max(0, b.Rect.X - 54), b.Rect.Y + (b.Rect.H - 44) / 2, 44, 44);
                    if (b.FocusSlot != null || (!p.Id.StartsWith("pop", StringComparison.Ordinal) && b.Id == p.Buttons[0].Id || design.CursorPreset is "pulse" or "ring" or "arrow" or "custom"))
                        txt.Append($"focus-slot {Px(slot.X)} {Px(slot.Y)} {Px(slot.W)} {Px(slot.H)}\n");
                }
            }
        }
        File.WriteAllText(Path.Combine(dir, "menu.txt"), txt.ToString(), new UTF8Encoding(false));
    }
}
