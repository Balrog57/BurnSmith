using BurnSmith.Core;

namespace BurnSmith.App;

/// <summary>
/// The output: a disc size (nominal, and the part a build may fill: the rest is file system and menu overhead) and, for a Blu-ray, a 720p or 1080p picture.
/// « Sans limite » (<see cref="Unlimited"/>: a folder or an image for a hard disk, not for a disc) has no size: <see cref="Gb"/> 0 and <see cref="Usable"/> below 0,
/// which a project file keeps as it is and the builds read as "nothing to fit".
/// </summary>
public sealed record Output(string Name, double Gb, double Usable, bool Hd720 = false)
{
    public bool Unlimited => Usable <= 0;
    public override string ToString() => Unlimited ? Name : $"{Name} ({Gb:0.#} Go)";

    public static readonly Output[] Dvds = [new("DVD-5", 4.7, 4.2), new("DVD-9", 8.5, 7.6), new("Mini DVD 8 cm", 1.46, 1.3), new("Mini DVD 8 cm double couche", 2.66, 2.38), new("Sans limite", 0, -1)];
    public static readonly Output[] Uhds = [new("BD100 2160p", 100, 93), new("BD66 2160p", 66, 61), new("BD50 2160p", 50, 46.5), new("BD25 2160p", 25, 23.3), new("Sans limite 2160p", 0, -1)];
    public static readonly Output[] Bds = [.. BdProfile.All.Select(p => new Output(p.Name.Replace("MiniBD", "Mini BD 8 cm"), p.Gb, p.UsableGb, p.Hd720)), new("Sans limite 1080p/i", 0, -1), new("Sans limite 720p", 0, -1, true)];

    /// <summary>BD25 1080p/i, what a new project starts with.</summary>
    public static Output DefaultBd => Bds[2];

    public static Output[] For(string kind) => kind == "dvd" ? Dvds : kind == "uhd" ? Uhds : Bds;
}
