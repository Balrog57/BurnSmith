using BurnSmith.Core;

namespace BurnSmith.App;

/// <summary>The output: a disc size (nominal, and the part a build may fill: the rest is file system and menu overhead) and, for a Blu-ray, a 720p or 1080p picture.</summary>
public sealed record Output(string Name, double Gb, double Usable, bool Hd720 = false)
{
    public override string ToString() => $"{Name} ({Gb:0.#} Go)";

    public static readonly Output[] Dvds = [new("DVD-5", 4.7, 4.2), new("DVD-9", 8.5, 7.6)];
    public static readonly Output[] Uhds = [new("BD-66", 66, 61), new("BD-100", 100, 93)];
    public static readonly Output[] Bds = BdProfile.All.Select(p => new Output(p.Name, p.Gb, p.UsableGb, p.Hd720)).ToArray();

    /// <summary>BD25 1080p/i, what a new project starts with.</summary>
    public static Output DefaultBd => Bds[2];

    public static Output[] For(string kind) => kind == "dvd" ? Dvds : kind == "uhd" ? Uhds : Bds;
}
