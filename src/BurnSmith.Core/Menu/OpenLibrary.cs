namespace BurnSmith.Core.Menu;

/// <summary>
/// Open-source menu files that travel inside the exe (DVDStyler's templates and backgrounds, GPL, with their notices).
/// They are unpacked, when they differ from what is there, to Documents\BurnSmith\Templates\open (see <see cref="AppFiles"/>) so that image readers and the template reader get plain files.
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
            using var all = new MemoryStream();
            foreach (var name in names)
            {
                all.Write(System.Text.Encoding.UTF8.GetBytes(name));
                using var from = asm.GetManifestResourceStream(name)!;
                from.CopyTo(all);
            }
            all.Position = 0;
            return dir = AppFiles.Unpack("open", AppFiles.Stamp(all), target =>
            {
                foreach (var name in names)
                {
                    var file = Path.Combine(target, name["library/".Length..].Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    using var from = asm.GetManifestResourceStream(name)!;
                    using var to = File.Create(file);
                    from.CopyTo(to);
                }
            }, inLibrary: true);
        }
    }
}
