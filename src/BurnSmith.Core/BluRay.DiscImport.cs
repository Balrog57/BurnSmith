using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BurnSmith.Core;

public static partial class BluRay
{
    /// <summary>One selectable title found inside a Blu-ray (BDMV) or DVD-Video (VIDEO_TS) folder or ISO.</summary>
    public sealed record DiscSourceTitle(string Kind, string Name, double Duration, int Chapters, string Summary, string PathOrId);

    static string MountDiscRoot(string path)
    {
        if (File.Exists(path) && Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            var quoted = Path.GetFullPath(path).Replace("'", "''");
            var letter = Tools.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
                $"$i = Get-DiskImage -ImagePath '{quoted}'; if (-not $i.Attached) {{ $i = Mount-DiskImage -ImagePath '{quoted}' -Access ReadOnly -PassThru }}; ($i | Get-Volume).DriveLetter"]).Trim();
            return letter.Length == 1 ? letter + @":\" : throw new InvalidDataException($"{Path.GetFileName(path)}: Windows could not open the image");
        }
        return Directory.Exists(path) ? path : "";
    }

    /// <summary>Lists playable titles inside an unencrypted Blu-ray (BDMV/PLAYLIST) or DVD-Video (VIDEO_TS) folder or ISO, longest first.</summary>
    public static List<DiscSourceTitle> ListDiscTitles(string path)
    {
        var root = MountDiscRoot(path);
        if (root.Length == 0) return [];
        var discName = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(path));
        var bdmv = new[] { Path.Combine(root, "BDMV"), Path.Combine(root, "AVCHD", "BDMV"), root }
            .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "PLAYLIST")) && Directory.Exists(Path.Combine(d, "STREAM")));
        if (bdmv != null)
        {
            var results = new List<(DiscSourceTitle Title, string Sig)>();
            foreach (var file in Directory.GetFiles(Path.Combine(bdmv, "PLAYLIST"), "*.mpls").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    if (Mpls.StructuralProblem(bytes) != null) continue;
                    var pl = Mpls.Read(bytes);
                    if (pl.Items.Count == 0 || pl.Seconds < 5) continue;
                    if (!pl.Items.All(i => File.Exists(Path.Combine(bdmv, "STREAM", i.Clip + ".m2ts")))) continue;
                    var sig = string.Join("+", pl.Items.Select(i => i.Clip)) + $"|{Math.Round(pl.Seconds)}";
                    var audioCount = pl.Items[0].Audio.Count;
                    var subCount = pl.Items[0].Pg.Count;
                    var summary = $"{Timecode.Format(Math.Floor(pl.Seconds))} · {pl.Chapters.Count} chap. · {audioCount} audio · {subCount} sous-titres · {Path.GetFileName(file)}";
                    var titleName = $"{discName} ({Path.GetFileNameWithoutExtension(file)})";
                    results.Add((new DiscSourceTitle("bd", titleName, pl.Seconds, pl.Chapters.Count, summary, file), sig));
                }
                catch { }
            }
            var deduped = results.DistinctBy(x => x.Sig).Select(x => x.Title).OrderByDescending(t => t.Duration).ToList();
            if (deduped.Any(t => t.Duration >= 60)) deduped = deduped.Where(t => t.Duration >= 30).ToList();
            if (deduped.Count == 1) deduped[0] = deduped[0] with { Name = discName };
            return deduped;
        }

        var videoTs = new[] { Path.Combine(root, "VIDEO_TS"), root }
            .FirstOrDefault(d => Directory.Exists(d) && Directory.GetFiles(d, "VTS_*_1.VOB").Length > 0);
        if (videoTs != null)
        {
            var dvdTitles = new List<DiscSourceTitle>();
            var vtsGroups = Directory.GetFiles(videoTs, "VTS_*_*.VOB")
                .Select(f => (File: f, Match: Regex.Match(Path.GetFileName(f), @"^VTS_(\d\d)_([1-9])\.VOB$", RegexOptions.IgnoreCase)))
                .Where(x => x.Match.Success)
                .GroupBy(x => x.Match.Groups[1].Value)
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in vtsGroups)
            {
                var vobs = g.OrderBy(x => int.Parse(x.Match.Groups[2].Value, Inv)).Select(x => x.File).ToList();
                var totalBytes = vobs.Sum(Tools.SizeOf);
                if (totalBytes < 500_000) continue;
                var firstProbe = TryProbeDuration(vobs[0]);
                var estDuration = firstProbe > 0 ? firstProbe * totalBytes / Math.Max(1, Tools.SizeOf(vobs[0])) : 0;
                var summary = $"{(estDuration > 0 ? Timecode.Format(Math.Floor(estDuration)) + " · " : "")}{totalBytes / 1e6:0} Mo · VTS_{g.Key} ({vobs.Count} VOB)";
                dvdTitles.Add(new DiscSourceTitle("dvd", $"{discName} · Titre {int.Parse(g.Key, Inv)}", estDuration, 0, summary, string.Join('|', vobs)));
            }
            if (dvdTitles.Count == 1) dvdTitles[0] = dvdTitles[0] with { Name = discName };
            return dvdTitles.OrderByDescending(t => t.Duration).ToList();
        }
        return [];
    }

    static double TryProbeDuration(string file)
    {
        try
        {
            var s = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file]).Trim();
            return double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : 0;
        }
        catch { return 0; }
    }

    /// <summary>Extracts or returns the video file path for a <see cref="DiscSourceTitle"/> chosen from <see cref="ListDiscTitles"/>.</summary>
    public static string ExtractDiscTitle(DiscSourceTitle chosen, Action<string>? log = null)
    {
        if (chosen.Kind == "dvd")
        {
            var vobs = chosen.PathOrId.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var dir = Path.Combine(Path.GetTempPath(), "BurnSmith-joined");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(chosen.Name.Split(Path.GetInvalidFileNameChars()));
            var outMkv = Path.Combine(dir, safe + ".mkv");
            var concat = "concat:" + string.Join('|', vobs);
            log?.Invoke($"Import DVD : {chosen.Name} ({vobs.Length} VOB)…");
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "warning", "-fflags", "+genpts", "-i", concat, "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-c", "copy", outMkv], log);
            return outMkv;
        }
        var mpls = chosen.PathOrId;
        var bdmv = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(mpls))!)!;
        var pl = Mpls.Read(File.ReadAllBytes(mpls));
        if (pl.Items.Count == 1)
            return Path.Combine(bdmv, "STREAM", pl.Items[0].Clip + ".m2ts");
        var firstClip = Path.Combine(bdmv, "STREAM", pl.Items[0].Clip + ".m2ts");
        return JoinedPlaylist(mpls, firstClip, chosen.Name) is { } joined ? MainClip(joined) : firstClip;
    }

    /// <summary>
    /// The video to add for a Blu-ray or DVD given whole: an .iso (opened read-only by Windows) or a folder holding BDMV/VIDEO_TS gives its main
    /// title (multi-title discs offer interactive selection via <see cref="ListDiscTitles"/>); any other path is returned unchanged.
    /// </summary>
    public static string MainClip(string path)
    {
        var root = MountDiscRoot(path);
        if (root.Length == 0) return path;
        var stream = new[] { Path.Combine(root, "BDMV", "STREAM"), Path.Combine(root, "AVCHD", "BDMV", "STREAM"), Path.Combine(root, "STREAM") }.FirstOrDefault(Directory.Exists);
        var name = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(path));
        if (stream == null)
        {
            var dvdTitles = ListDiscTitles(path);
            if (dvdTitles.Count > 0) return ExtractDiscTitle(dvdTitles[0]);
            throw new InvalidDataException($"{name}: aucun film Blu-ray (BDMV\\STREAM) ni DVD (VIDEO_TS) trouvé");
        }
        var clip = Directory.GetFiles(stream, "*.m2ts").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                   ?? throw new InvalidDataException($"{name}: no Blu-ray film (BDMV\\STREAM) in it");
        return Joined(clip, name) is { } joined ? MainClip(joined) : clip;
    }

    /// <summary>The .ssif of a clip of a 3D Blu-ray (BDMV\STREAM\SSIF\N.ssif beside BDMV\STREAM\N.m2ts): both eyes, which tsMuxeR reads; null for any other video.</summary>
    public static string? Ssif(string video) =>
        Path.GetExtension(video).Equals(".m2ts", StringComparison.OrdinalIgnoreCase)
        && Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video))!, "SSIF", Path.GetFileNameWithoutExtension(video) + ".ssif") is var ssif && File.Exists(ssif) ? ssif : null;

    /// <summary>
    /// What the playlist of a clip taken from a Blu-ray says of its tracks, which the clip does not carry: by PID, the language and, for the subtitles of a
    /// 3D film, the depth they are shown at (3d-plane, -1 = none). Read by tsMuxeR from the biggest playlist that plays the clip; empty when there is none.
    /// </summary>
    public static Dictionary<int, (string Lang, int Plane)> PlaylistTracks(string video)
    {
        var found = new Dictionary<int, (string Lang, int Plane)>();
        if (MainPlaylist(video) is not { } mpls) return found;
        string listing;
        try { listing = Tools.Run(Tools.Find("tsMuxeR.exe"), [mpls]); }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException) { return found; }
        foreach (var (id, _, lang, plane) in ListedTracks(listing)) found[id] = (lang, plane);
        return found;
    }

    /// <summary>The biggest playlist of the disc that plays a clip taken from it; null when the clip is not in a disc's BDMV\STREAM.</summary>
    static string? MainPlaylist(string video)
    {
        var stream = Path.GetDirectoryName(Path.GetFullPath(video))!;
        var playlists = Path.Combine(Path.GetDirectoryName(stream) ?? "", "PLAYLIST");
        if (!Path.GetFileName(stream).Equals("STREAM", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(playlists)) return null;
        var clip = Encoding.ASCII.GetBytes(Path.GetFileNameWithoutExtension(video) + "M2TS");
        return Directory.GetFiles(playlists, "*.mpls").Where(f => File.ReadAllBytes(f).AsSpan().IndexOf(clip) >= 0).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    /// <summary>The tracks of a tsMuxeR listing, in order: id, tsMuxeR's codec name (empty when it cannot take the track), language, 3D depth (-1 = none).</summary>
    public static List<(int Id, string Codec, string Lang, int Plane)> ListedTracks(string listing)
    {
        var tracks = new List<(int Id, string Codec, string Lang, int Plane)>();
        var at = -1;
        foreach (var line in listing.Split('\n'))
        {
            if (line.Split(':', 2) is not [var key, var value]) continue;
            (key, value) = (key.Trim(), value.Trim());
            if (key == "Track ID" && int.TryParse(value, out var id))
            {
                tracks.RemoveAll(t => t.Id == id);
                tracks.Add((id, "", "", -1));
                at = tracks.Count - 1;
            }
            else if (at < 0) continue;
            else if (key == "Stream ID") tracks[at] = tracks[at] with { Codec = value };
            else if (key == "Stream lang") tracks[at] = tracks[at] with { Lang = value };
            else if (key == "Stream info" && Regex.Match(value, @"3d-plane: (\d+)") is { Success: true } m) tracks[at] = tracks[at] with { Plane = int.Parse(m.Groups[1].Value, Inv) };
        }
        return tracks;
    }

    /// <summary>
    /// A film the disc plays as several clips in turn (seamless branching): tsMuxeR copies them, every track as it is (both eyes of a 3D film), into a
    /// Blu-ray image of one clip in %TEMP%\BurnSmith-joined, which then stands for the film. Null when its playlist has a single clip.
    /// </summary>
    static string? Joined(string clip, string name) => MainPlaylist(clip) is { } mpls ? JoinedPlaylist(mpls, clip, name) : null;

    static string? JoinedPlaylist(string mpls, string clip, string name)
    {
        var tsMuxer = Tools.Find("tsMuxeR.exe");
        var listing = Tools.Run(tsMuxer, [mpls]);
        if (Regex.Matches(listing, @"^File #\d+", RegexOptions.Multiline).Count < 2) return null;
        var dir = Path.Combine(Path.GetTempPath(), "BurnSmith-joined");
        Directory.CreateDirectory(dir);
        var iso = Path.Combine(dir, string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".iso");
        var key = $"{mpls} {new FileInfo(clip).Length}";
        if (File.Exists(iso + ".done") && File.ReadAllText(iso + ".done") == key) return iso;
        foreach (var old in Directory.GetFiles(dir))
        {
            if (old.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                try { Tools.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", $"Dismount-DiskImage -ImagePath '{old.Replace("'", "''")}' | Out-Null"]); } catch (InvalidOperationException) { }
            try { File.Delete(old); } catch (IOException) { }
        }
        var meta = Path.Combine(dir, "join.meta");
        File.WriteAllLines(meta, ListedTracks(listing).Where(t => t.Codec.Length > 0).Select(t => $"{t.Codec}, \"{mpls}\", track={t.Id}"
                + (t.Lang.Length > 0 ? $", lang={t.Lang}" : "") + (t.Plane >= 0 ? $", 3d-plane={t.Plane}" : ""))
            .Prepend("MUXOPT --blu-ray --vbr --no-pcr-on-video-pid --label=JOINED"));
        Tools.Run(tsMuxer, [meta, iso]);
        File.WriteAllText(iso + ".done", key);
        return iso;
    }
}
