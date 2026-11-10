namespace BurnSmith.Core.Menu;

/// <summary>
/// What a menu template is: this decides how BurnSmith reads it and how it is drawn, never a product name.
/// The gallery groups templates by <see cref="TemplateLabel"/>.
/// </summary>
public enum TemplateKind
{
    None,
    /// <summary>A Blu-ray template tree (<c>template.bd.xml</c>).</summary>
    BluRay,
    /// <summary>A DVD template description (<c>template.xml</c> with flat values).</summary>
    Dvd,
    /// <summary>Pictures only: the layout is measured from their transparent openings.</summary>
    Pictures,
    /// <summary>A theme whose layout is described by its own configuration file.</summary>
    Theme,
    /// <summary>The open-source library that travels with the exe (<c>.dvdt</c> files).</summary>
    Library,
    /// <summary>A design BurnSmith draws from its own parameters.</summary>
    BuiltIn,
}

/// <summary>The neutral name of each kind, both in the template gallery and in the element library.</summary>
public static class TemplateLabel
{
    public const string BluRay = "Blu-ray", Dvd = "DVD", Themes = "Thèmes", Open = "Libres", Own = "BurnSmith";

    /// <summary>
    /// <paramref name="category"/> when it is one of the neutral labels, else "": a design saved by an earlier version kept the name of
    /// where its template came from, and that name is not a group any more.
    /// </summary>
    public static string Clean(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "";
        var trimmed = category.Trim();
        return trimmed is BluRay or Dvd or Themes or Open or Own ? trimmed : "";
    }

    public static string Of(TemplateKind kind) => kind switch
    {
        TemplateKind.BluRay => BluRay,
        TemplateKind.Dvd => Dvd,
        TemplateKind.Pictures or TemplateKind.Theme => Themes,
        TemplateKind.Library => Open,
        TemplateKind.BuiltIn => Own, // BurnSmith's own designs (Métal…) have their group, first in the gallery: among the open library's fifty they were not found
        _ => "",
    };
}
