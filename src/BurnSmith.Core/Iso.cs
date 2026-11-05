namespace BurnSmith.Core;

/// <summary>ISO images and burning: Windows' own IMAPI2 (see <see cref="Imapi"/>), UDF 2.50 for Blu-ray, UDF 1.02 for DVD-Video.</summary>
public static class Iso
{
    public static void Build(string discFolder, string isoFile, string label, bool dvd, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        try { Imapi.BuildImage(discFolder, Path.GetFullPath(isoFile), label, dvd, log, progress, cancel); }
        catch (OperationCanceledException) { File.Delete(isoFile); throw; } // half an image must never be burnt by mistake
    }

    /// <summary>Burns an image to the blank disc in <paramref name="drive"/> ("F:") and verifies it.</summary>
    public static void Burn(string isoFile, string drive, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken cancel = default) =>
        Imapi.Burn(Path.GetFullPath(isoFile), drive, log, progress, verify: true, cancel);
}
