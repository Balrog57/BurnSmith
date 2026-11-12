using System.Text.RegularExpressions;

namespace BurnSmith.Core.Menu;

/// <summary>
/// What a template is for (Film, Série, Anniversaire…), read from its name: the gallery groups templates this way, not by the program they came from,
/// since every template makes a DVD, a Blu-ray and a 4K menu alike. A name that says nothing goes to <see cref="Original"/>.
/// </summary>
public static class TemplateUsage
{
    public const string Original = "Original";

    // first match wins; words are searched in the lower-case name (shortcut: names only, a template whose name says nothing lands in Original; tag templates if that grows)
    static readonly (string Usage, string[] Words)[] Map =
    [
        ("Série", ["saison", "série", "series", "streaming", "originals", "vamp"]),
        ("Film", ["movie", "cinéma", "cinema", "salle obscure", "rideau", "camera", "tube cathodique"]),
        ("Anniversaire", ["birthday", "cake", "balloon", "fête", "mothersday"]),
        ("Mariage", ["wedding", "mariage", "heart"]),
        ("Bébé et enfance", ["baby", "naissance", "childhood"]),
        ("Noël et Nouvel An", ["christmas", "noël", "noel", "newyear", "fireworks"]),
        ("Voyage et nature", ["travel", "voyage", "holiday", "landscape", "natur","ocean", "sea of", "mountain", "montagne", "autumn", "tree", "forest",
                              "sun", "sky", "cloudy", "winter", "hiver", "snow", "frosty", "penguin", "bois"]),
        ("Sport", ["sport", "stade"]),
        ("Musique et spectacle", ["concert", "synthwave"]),
        ("École et travail", ["business", "education", "tableau noir"]),
    ];

    /// <summary>The usages in the gallery's order, <see cref="Original"/> last.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Map.Select(m => m.Usage), Original];

    public static string Of(string name)
    {
        var n = name.ToLowerInvariant();
        foreach (var (usage, words) in Map)
            if (words.Any(n.Contains)) return usage;
        return Original;
    }
}

/// <summary>
/// The screen shape a template is drawn for. Several templates ship a 16:9 and a 4:3 version under one name with the shape at the end
/// (« Autumn 16-9 », « business (4-3) »): the gallery shows them as one template and the « 16:9 / 4:3 » choice picks the version.
/// </summary>
public static partial class TemplateShape
{
    [GeneratedRegex(@"\s*(\((16-9|16-9-2|4-3)\)|\b(16-9|4-3))$")]
    private static partial Regex Suffix();

    /// <summary>Whether the name is that of a 4:3 version.</summary>
    public static bool IsNarrow(string name) => Suffix().Match(name) is { Success: true } m && m.Value.Contains("4-3");

    /// <summary>The name without its shape: « Autumn 4-3 » → « Autumn »; a second 16:9 version keeps a number (« business (16-9-2) » → « business 2 »).</summary>
    public static string Base(string name) => Suffix().Match(name) is { Success: true } m ? name[..m.Index] + (m.Value.Contains("16-9-2") ? " 2" : "") : name;

    /// <summary>
    /// The version of <paramref name="design"/> for the chosen shape among <paramref name="all"/> (same family, same base name), else
    /// <paramref name="design"/> itself: a template made for one shape only stays as it is.
    /// </summary>
    public static MenuDesign Pick(MenuDesign design, bool narrow, IEnumerable<MenuDesign> all)
    {
        if (IsNarrow(design.Name) == narrow) return design;
        var key = Base(design.Name);
        return all.FirstOrDefault(d => d.Category == design.Category && IsNarrow(d.Name) == narrow && Base(d.Name).Equals(key, StringComparison.OrdinalIgnoreCase)) ?? design;
    }
}
