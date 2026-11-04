namespace BurnSmith.Core.Menu;

/// <summary>
/// Open-source menu files that travel inside the exe (DVDStyler's templates and backgrounds, GPL, with their notices).
/// They are unpacked once to %LOCALAPPDATA%\BurnSmith\open\&lt;build id&gt; so that image readers and the template reader get plain files.
/// </summary>
public static class OpenLibrary
{
    static readonly object Gate = new();
    static string? dir;

    /// <summary>The unpacked folder, or null when this build carries no library.</summary>
    public static string? Dir()
    {
        lock (Gate)
        {
            if (dir != null) return dir;
            var asm = typeof(OpenLibrary).Assembly;
            var names = asm.GetManifestResourceNames().Where(n => n.StartsWith("library/", StringComparison.Ordinal)).ToList();
            if (names.Count == 0) return null;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BurnSmith", "open");
            var target = Path.Combine(root, asm.ManifestModule.ModuleVersionId.ToString("N")[..12]);
            if (!File.Exists(Path.Combine(target, ".ready")))
            {
                var tmp = $"{target}.{Environment.ProcessId}.tmp";
                Tools.TryDelete(tmp);
                foreach (var name in names)
                {
                    var file = Path.Combine(tmp, name["library/".Length..].Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    using var from = asm.GetManifestResourceStream(name)!;
                    using var to = File.Create(file);
                    from.CopyTo(to);
                }
                File.WriteAllText(Path.Combine(tmp, ".ready"), "");
                try { Directory.Move(tmp, target); }
                catch (IOException) when (File.Exists(Path.Combine(target, ".ready"))) { Tools.TryDelete(tmp); } // another instance unpacked it first
                foreach (var old in Directory.GetDirectories(root).Where(d => !d.EndsWith(".tmp") && !d.Equals(target, StringComparison.OrdinalIgnoreCase))) Tools.TryDelete(old);
            }
            return dir = target;
        }
    }
}
