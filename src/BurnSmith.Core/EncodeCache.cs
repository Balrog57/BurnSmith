using System.Security.Cryptography;
using System.Text.Json;

namespace BurnSmith.Core;

internal static class EncodeCache
{
    static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BurnSmith", "encode-cache");
    const long Budget = 100_000_000_000;

    static object Stamp(string path)
    {
        var file = new FileInfo(path);
        return new { file.FullName, Bytes = file.Exists ? file.Length : -1, Modified = file.LastWriteTimeUtc };
    }

    static string Key(object settings, IEnumerable<string?> inputs) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Engine = typeof(EncodeCache).Module.ModuleVersionId,
            Settings = settings,
            Inputs = inputs.OfType<string>().Select(Stamp)
        })));

    internal static IEnumerable<string?> Inputs(DiscTitle title, IEnumerable<AudioTrack> audio) =>
        new[] { title.Video, title.Effects?.Watermark, title.Effects?.BurnSubtitle }
            .Concat(audio.Select(track => track.File));

    sealed record Entry(string Suffix, long Bytes);

    internal static string? OptionalTool(string name)
    {
        try { return Tools.Find(name); }
        catch (FileNotFoundException) { return null; }
    }

    static void Copy(string source, string target)
    {
        using var input = File.OpenRead(source);
        using var output = File.Create(target);
        input.CopyToAsync(output, 1024 * 1024, Tools.Cancellation).GetAwaiter().GetResult();
    }

    internal static void Prepare(string output, object settings, IEnumerable<string?> inputs, Action encode, Action<string> log)
    {
        var stem = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output));
        Tools.Cancellation.ThrowIfCancellationRequested();
        string key;
        try { key = Key(settings, inputs); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log($"Cache encodage : sources non identifiables ({e.Message}).");
            encode();
            return;
        }
        var destination = Path.Combine(Root, key);
        var restored = new List<string>();
        try
        {
            if (Directory.Exists(destination))
            {
                var files = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(Path.Combine(destination, "files.json")));
                if (files is { Length: > 0 } && files.All(f => f?.Suffix is { Length: > 1 } && f.Suffix.StartsWith('.')
                    && f.Suffix.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_') && f.Bytes > 0
                    && Tools.SizeOf(Path.Combine(destination, f.Suffix)) == f.Bytes)
                    && files.Any(f => stem + f.Suffix == output))
                {
                    foreach (var file in files)
                    {
                        var path = stem + file.Suffix;
                        restored.Add(path);
                        Copy(Path.Combine(destination, file.Suffix), path);
                    }
                    log($"Cache encodage : réutilisé ({Path.GetFileName(output)}).");
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            foreach (var path in restored) File.Delete(path);
            log($"Cache encodage : lecture ignorée ({e.Message}).");
        }

        encode();
        Tools.Cancellation.ThrowIfCancellationRequested();
        if (Tools.SizeOf(output) == 0 || Directory.Exists(destination)) return;

        var staged = Path.Combine(Root, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var files = Directory.EnumerateFiles(Path.GetDirectoryName(output)!, Path.GetFileName(stem) + ".*")
                .Where(path => path.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                .Select(path => new Entry(path[stem.Length..], new FileInfo(path).Length)).ToArray();
            if (files.Any(file => file.Bytes == 0)) return;
            Directory.CreateDirectory(Root);
            var bytes = files.Sum(file => file.Bytes);
            if (bytes > new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace
                || bytes + Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(Tools.SizeOf) > Budget)
            {
                log("Cache encodage : espace insuffisant, titre non ajouté.");
                return;
            }
            Directory.CreateDirectory(staged);
            foreach (var file in files)
            {
                Copy(stem + file.Suffix, Path.Combine(staged, file.Suffix));
            }
            File.WriteAllText(Path.Combine(staged, "files.json"), JsonSerializer.Serialize(files));
            Tools.Cancellation.ThrowIfCancellationRequested();
            if (Key(settings, inputs) != key) return;
            Directory.Move(staged, destination);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(destination)) log($"Cache encodage : écriture ignorée ({e.Message}).");
        }
        finally { Tools.TryDelete(staged); }
    }
}
