using System.Text.Json;

namespace BurnSmith.Core.Menu;

/// <summary>
/// User templates saved from the menu workshop in Documents\BurnSmith\Templates\mine.
/// Each saved template gets its own autonomous folder (<c>&lt;name&gt;\template.burnsmith.json</c>, <c>thumb.jpg</c>, and <c>media\</c> local copies),
/// while still reading legacy flat <c>*.json</c> files seamlessly.
/// </summary>
public static class Templates
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public const string FileName = "template.burnsmith.json";
    public const string ThumbName = "thumb.jpg";

    public sealed record UserTemplate(string Name, MenuDesign Design, string? Thumbnail, string FolderOrFile)
    {
        public override string ToString() => Name;
    }

    public static string Dir { get; } = AppFiles.UserFolder("mine", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BurnSmith", "templates"));

    static string SafeName(string name)
    {
        var clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return clean.Length == 0 ? throw new ArgumentException("template name is empty") : clean;
    }

    static string? CopyMediaToFolder(string? reference, string mediaDir)
    {
        if (string.IsNullOrWhiteSpace(reference)) return reference;
        if (reference.StartsWith("lib:", StringComparison.Ordinal) || reference.StartsWith("carried:", StringComparison.Ordinal))
            return reference;
        if (MenuRenderer.IsSvgText(reference)) return reference;
        var resolved = ElementLibrary.Resolve(reference) ?? reference;
        var libRef = ElementLibrary.Reference(resolved);
        if (libRef.StartsWith("lib:", StringComparison.Ordinal) || libRef.StartsWith("carried:", StringComparison.Ordinal))
            return reference;
        if (!File.Exists(resolved)) return reference;
        // Already inside this template's media folder? Keep as is
        if (resolved.StartsWith(mediaDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return resolved;
        Directory.CreateDirectory(mediaDir);
        var fileName = Path.GetFileName(resolved);
        var dest = Path.Combine(mediaDir, fileName);
        if (File.Exists(dest) && new FileInfo(dest).Length != new FileInfo(resolved).Length)
            dest = Path.Combine(mediaDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid().ToString("N")[..6]}{Path.GetExtension(fileName)}");
        File.Copy(resolved, dest, true);
        return dest;
    }

    /// <summary>Copies linked local media (images, loops, music, sounds) into <paramref name="mediaDir"/> and rewrites paths on a cloned design.</summary>
    static MenuDesign LocalizeMedia(MenuDesign source, string mediaDir)
    {
        var d = source.Clone();
        d.BackgroundImage = CopyMediaToFolder(d.BackgroundImage, mediaDir);
        d.Poster = CopyMediaToFolder(d.Poster, mediaDir);
        d.LoopFile = CopyMediaToFolder(d.LoopFile, mediaDir);
        d.Music = CopyMediaToFolder(d.Music, mediaDir);
        if (d.FocusFrames != null)
            d.FocusFrames = [.. d.FocusFrames.Select(f => CopyMediaToFolder(f, mediaDir) ?? f)];
        foreach (var k in d.PageBackgrounds.Keys.ToList())
            d.PageBackgrounds[k] = CopyMediaToFolder(d.PageBackgrounds[k], mediaDir) ?? d.PageBackgrounds[k];
        foreach (var m in d.PageMedias)
        {
            m.LoopFile = CopyMediaToFolder(m.LoopFile, mediaDir);
            m.Music = CopyMediaToFolder(m.Music, mediaDir);
        }
        foreach (var e in d.Elements)
        {
            e.Image = CopyMediaToFolder(e.Image, mediaDir);
            e.Focus = CopyMediaToFolder(e.Focus, mediaDir);
            e.ActiveImage = CopyMediaToFolder(e.ActiveImage, mediaDir);
        }
        foreach (var ov in d.NativeOverrides)
        {
            ov.Image = CopyMediaToFolder(ov.Image, mediaDir);
            ov.Focus = CopyMediaToFolder(ov.Focus, mediaDir);
            ov.ActiveImage = CopyMediaToFolder(ov.ActiveImage, mediaDir);
        }
        d.Sounds.MoveWav = CopyMediaToFolder(d.Sounds.MoveWav, mediaDir);
        d.Sounds.SelectWav = CopyMediaToFolder(d.Sounds.SelectWav, mediaDir);
        return d;
    }

    public static List<UserTemplate> Entries()
    {
        if (!Directory.Exists(Dir)) return [];
        var list = new List<UserTemplate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1. Autonomous folders (<name>\template.burnsmith.json)
        foreach (var sub in Directory.EnumerateDirectories(Dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            var jsonFile = Path.Combine(sub, FileName);
            if (!File.Exists(jsonFile)) continue;
            try
            {
                if (JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(jsonFile)) is not { } d) continue;
                var thumb = Path.Combine(sub, ThumbName);
                seen.Add(d.Name);
                list.Add(new UserTemplate(d.Name, d, File.Exists(thumb) ? thumb : null, sub));
            }
            catch (JsonException) { }
        }
        // 2. Legacy flat *.json files
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(f)) is not { } d) continue;
                if (!seen.Add(d.Name)) continue;
                list.Add(new UserTemplate(d.Name, d, null, f));
            }
            catch (JsonException) { }
        }
        return [.. list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static List<MenuDesign> List() => [.. Entries().Select(e => e.Design)];

    public static void Save(MenuDesign d, byte[]? thumbnailJpeg = null)
    {
        var safe = SafeName(d.Name);
        var folder = Path.Combine(Dir, safe);
        Directory.CreateDirectory(folder);
        var localized = LocalizeMedia(d, Path.Combine(folder, "media"));
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(localized, Json));
        // Remove legacy flat file of same stem if present so folder copy is canonical
        var flat = Path.Combine(Dir, safe + ".json");
        if (File.Exists(flat)) try { File.Delete(flat); } catch (IOException) { }
        var thumbPath = Path.Combine(folder, ThumbName);
        if (thumbnailJpeg is { Length: > 0 })
        {
            File.WriteAllBytes(thumbPath, thumbnailJpeg);
        }
        else if (!File.Exists(thumbPath))
        {
            try
            {
                var sample = new MenuContent(1, null, [null, null, null, null, null, null]);
                var bytes = MenuRenderer.Sta(() => MenuRenderer.Background(localized, MenuBuilder.Build(localized, sample)[0]));
                File.WriteAllBytes(thumbPath, bytes);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException) { }
        }
    }

    public static void Rename(string oldName, string newName)
    {
        var cleanNew = newName.Trim();
        if (cleanNew.Length == 0) throw new ArgumentException("template name is empty");
        var entry = Entries().FirstOrDefault(e => e.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Modèle introuvable : {oldName}");
        var design = entry.Design.Clone();
        design.Name = cleanNew;
        byte[]? thumb = entry.Thumbnail != null && File.Exists(entry.Thumbnail) ? File.ReadAllBytes(entry.Thumbnail) : null;
        Save(design, thumb);
        if (!oldName.Equals(cleanNew, StringComparison.OrdinalIgnoreCase))
            Delete(oldName);
    }

    public static MenuDesign Duplicate(string name, string copyName)
    {
        var entry = Entries().FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Modèle introuvable : {name}");
        var copy = entry.Design.Clone();
        copy.Name = copyName.Trim();
        byte[]? thumb = entry.Thumbnail != null && File.Exists(entry.Thumbnail) ? File.ReadAllBytes(entry.Thumbnail) : null;
        Save(copy, thumb);
        return copy;
    }

    public static void Delete(string name)
    {
        var entry = Entries().FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        if (Directory.Exists(entry.FolderOrFile)) Directory.Delete(entry.FolderOrFile, true);
        else if (File.Exists(entry.FolderOrFile)) File.Delete(entry.FolderOrFile);
        var flat = Path.Combine(Dir, SafeName(name) + ".json");
        if (File.Exists(flat)) File.Delete(flat);
    }
}
