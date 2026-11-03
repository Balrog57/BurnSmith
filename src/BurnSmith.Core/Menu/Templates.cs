using System.Text.Json;

namespace BurnSmith.Core.Menu;

/// <summary>User templates: one JSON file per <see cref="MenuDesign"/> in %APPDATA%\BurnSmith\templates.</summary>
public static class Templates
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BurnSmith", "templates");

    public static List<MenuDesign> List() =>
        !Directory.Exists(Dir) ? [] : Directory.EnumerateFiles(Dir, "*.json").Order()
            .Select(f => { try { return JsonSerializer.Deserialize<MenuDesign>(File.ReadAllText(f)); } catch (JsonException) { return null; } })
            .OfType<MenuDesign>().ToList();

    // shortcut: the template stores the background image / music paths, not copies; save a copy if the original may move
    public static void Save(MenuDesign d)
    {
        var name = string.Concat(d.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (name.Length == 0) throw new ArgumentException("template name is empty");
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, name + ".json"), JsonSerializer.Serialize(d, Json));
    }
}
