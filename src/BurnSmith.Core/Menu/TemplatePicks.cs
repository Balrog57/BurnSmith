using System.Globalization;

namespace BurnSmith.Core.Menu;

/// <summary>
/// The user's own marks on the template gallery: favourites (a star keeps the template at the top) and the last templates used, kept beside
/// template-folders.txt, one name per line. A name is the template's without its screen shape, so one star covers its 16:9 and its 4:3 version.
/// </summary>
public static class TemplatePicks
{
    /// <summary>How many templates « Récemment utilisés » keeps.</summary>
    public const int RecentCount = 6;

    static string dir = Path.GetDirectoryName(TemplateCatalog.FoldersFile)!;
    static HashSet<string>? stars;

    /// <summary>Where both files are (a check points it at a folder of its own).</summary>
    public static string Dir { get => dir; set { dir = value; stars = null; } }

    static string FavoritesFile => Path.Combine(dir, "template-favorites.txt");
    static string RecentFile => Path.Combine(dir, "template-recent.txt");

    static List<string> Read(string file)
    {
        try { return File.Exists(file) ? [.. File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)] : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static void Write(string file, IEnumerable<string> names)
    {
        try { Directory.CreateDirectory(dir); File.WriteAllLines(file, names); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a mark that cannot be kept is not worth stopping the user for */ }
    }

    static HashSet<string> Stars => stars ??= new(Read(FavoritesFile), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Favorites => Stars;

    public static bool IsFavorite(string name) => Stars.Contains(TemplateShape.Base(name));

    public static void Star(string name, bool on)
    {
        var key = TemplateShape.Base(name);
        if (on ? Stars.Add(key) : Stars.Remove(key)) Write(FavoritesFile, Stars.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The last templates used, the latest first.</summary>
    public static List<string> Recent() => [.. Read(RecentFile).Take(RecentCount)];

    public static void Used(string name)
    {
        var key = TemplateShape.Base(name);
        Write(RecentFile, Recent().Where(n => !n.Equals(key, StringComparison.OrdinalIgnoreCase)).Prepend(key).Take(RecentCount));
    }

    static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Whether <paramref name="text"/> is in <paramref name="value"/>, case and accents aside (« serie » finds « Série »).</summary>
    public static bool Contains(string? value, string text) => value != null && Compare.IndexOf(value, text.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>The search box: the text in the template's name, its use (Mariage, Série…) or its group.</summary>
    public static bool Matches(TemplateCatalog.Found f, string? text) =>
        string.IsNullOrWhiteSpace(text) || Contains(f.Design.Name, text) || Contains(f.Usage, text) || Contains(f.Design.Category, text);

    /// <summary>Favourites first, the rest in the order given.</summary>
    public static IEnumerable<TemplateCatalog.Found> Sorted(IEnumerable<TemplateCatalog.Found> found) => found.OrderBy(f => !IsFavorite(f.Design.Name));
}
