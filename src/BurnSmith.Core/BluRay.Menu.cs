using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BurnSmith.Core.Menu;

namespace BurnSmith.Core;

public static partial class BluRay
{
    static List<(string Name, byte[] Data)> JarFiles(string jar)
    {
        var entries = new List<(string, byte[])>();
        using var z = ZipFile.OpenRead(jar);
        foreach (var e in z.Entries.Where(e => e.Length > 0 && !e.FullName.StartsWith("META-INF/", StringComparison.Ordinal)))
        {
            using var from = e.Open();
            using var bytes = new MemoryStream();
            from.CopyTo(bytes);
            entries.Add((e.FullName, bytes.ToArray()));
        }
        return entries;
    }

    /// <summary>GPR the disc's start sets to the number of the title that holds the menu again, after the videos' titles: a video's title goes back there at its end.</summary>
    public const int BdjMenuTitleRegister = 114;

    /// <summary>Application ID of the xlet in the videos' titles: not the menu's (0x4000), so a player stops one and starts the other at each change of title.</summary>
    public const ushort BdjPlayerAppId = 0x4001;

    /// <summary>Adds the BD-J menu on top of the tsMuxeR disc: jar, BDJOs, and the index.bdmv and movie object that start it (BACKUP copies included).</summary>
    static void AddMenu(BluRayJob job, string menuDir, bool intro, Action<string> log)
    {
        log("menu: BD-J");
        var bdmv = Path.Combine(job.OutDir, "BDMV");
        foreach (var d in new[] { "JAR", "BDJO", "AUXDATA", @"BACKUP\BDJO" }) Directory.CreateDirectory(Path.Combine(bdmv, d));

        var entries = JarFiles(Assets.Find(@"bdj\menu.jar"));
        foreach (var f in Directory.EnumerateFiles(menuDir)) entries.Add((Path.GetFileName(f), File.ReadAllBytes(f)));
        BdjSign.Write(Path.Combine(bdmv, "JAR", "00000.jar"), entries, job.OutDir, job.Uhd);
        TitleLayout(bdmv, job.Titles.Count, job.Uhd ? Bdmv.HaviUhd1080 : job.Encode?.Hd720 == true ? Bdmv.HaviHd720 : Bdmv.HaviHd1080, intro);
    }

    /// <summary>
    /// The titles of a BD-J menu disc, as the studios' discs lay them out: the Top Menu is the menu (BDJO 00000), title N plays video N (playlist N, BDJO 00001,
    /// the same xlet under another application ID), and title <paramref name="videos"/> + 1 is the menu again, where a video's title goes back at its end. The Top Menu
    /// key is then never pressed inside the top menu title while a video plays, which a player refuses ("opération interdite"). First Playback is an HDMV object
    /// that says the disc has just started (GPR109) and where the menu title is (GPR114), plays the intro, and jumps to the menu title.
    /// </summary>
    static void TitleLayout(string bdmv, int videos, int havi, bool intro)
    {
        foreach (var (name, appId) in new[] { ("00000", (ushort)0x4000), ("00001", BdjPlayerAppId) })
        {
            var bdjo = Bdmv.Bdjo("org.burnsmith.menu.Menu", havi, appId: appId);
            File.WriteAllBytes(Path.Combine(bdmv, "BDJO", name + ".bdjo"), bdjo);
            File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", "BDJO", name + ".bdjo"), bdjo);
        }
        var menuTitle = videos + 1;
        var firstPlay = -1;
        foreach (var file in new[] { Path.Combine(bdmv, "MovieObject.bdmv"), Path.Combine(bdmv, "BACKUP", "MovieObject.bdmv") }.Where(File.Exists))
            File.WriteAllBytes(file, Bdmv.AppendObject(File.ReadAllBytes(file), out firstPlay,
                [Disc.Nav.Set(BdjStartRegister, 1), Disc.Nav.Set(BdjMenuTitleRegister, menuTitle), .. intro ? [Disc.Nav.PlayPlaylist(IntroPlaylist)] : Array.Empty<byte[]>(), Disc.Nav.JumpTitle(menuTitle)]));
        foreach (var index in new[] { Path.Combine(bdmv, "index.bdmv"), Path.Combine(bdmv, "BACKUP", "index.bdmv") })
            if (File.Exists(index)) File.WriteAllBytes(index, Bdmv.PatchIndex(File.ReadAllBytes(index), firstPlay, videos));
    }

    /// <summary>
    /// A menu.txt with no "playback" line (a disc made with the old default: back to the menu after the picked title) gets today's default, the studios' series discs:
    /// a picked episode goes on to the next ones, back to the menu after the last. The episodes are those of the menu's "play all" button, so bonuses stay alone; without one it is kept.
    /// </summary>
    public static byte[] ChainEpisodes(byte[] menuTxt)
    {
        var text = Encoding.UTF8.GetString(menuTxt);
        var lines = text.Split('\n');
        if (lines.Any(l => l.StartsWith("playback ", StringComparison.Ordinal))) return menuTxt;
        var all = lines.Select(l => l.Trim().Split(' ')).Where(w => w.Length >= 3 && w[^3] == "playall").Select(w => (First: int.Parse(w[^2], Inv), Count: int.Parse(w[^1], Inv))).OrderByDescending(t => t.Count).FirstOrDefault(); // none: (0, 0)
        return all.Count > 1 ? Encoding.UTF8.GetBytes($"playback menu 1 1 {all.First} {all.Count}\n" + text) : menuTxt;
    }

    /// <summary>
    /// A BD-J menu disc made before 1.0.33 (the menu and every video in one title, where a player refuses the Top Menu key during a video) put on today's titles and
    /// xlet, in its folder <paramref name="discRoot"/>: the menu it carries (menu.txt and its pictures) is kept, with today's default play mode when it had none (see ChainEpisodes),
    /// and the jar is signed again. A disc already on today's titles only gets that play mode. Returns false for a disc that does not need anything.
    /// </summary>
    public static bool RepairTopMenu(string discRoot)
    {
        var bdmv = Path.Combine(discRoot, "BDMV");
        var jar = Path.Combine(bdmv, "JAR", "00000.jar");
        var menuBdjo = Path.Combine(bdmv, "BDJO", "00000.bdjo");
        if (!File.Exists(jar) || !File.Exists(menuBdjo)) return false; // no BD-J menu
        var layout = !File.Exists(Path.Combine(bdmv, "BDJO", "00001.bdjo")); // not yet one title per video
        var files = JarFiles(jar);
        var menu = files.Where(e => !e.Name.EndsWith(".class", StringComparison.Ordinal)).Select(e => e.Name == "menu.txt" ? (e.Name, ChainEpisodes(e.Data)) : e).ToList();
        var xlet = JarFiles(Assets.Find(@"bdj\menu.jar"));
        static string Classes(IEnumerable<(string Name, byte[] Data)> f) => string.Join("|", f.Where(e => e.Name.EndsWith(".class", StringComparison.Ordinal)).OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => e.Name + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(e.Data))));
        var oldXlet = Classes(files) != Classes(xlet); // an older xlet (before 1.0.33 a chapter sent a real player back to the menu)
        if (!layout && !oldXlet && menu.SequenceEqual(files.Where(e => !e.Name.EndsWith(".class", StringComparison.Ordinal)))) return false;
        foreach (var f in Directory.EnumerateFiles(discRoot, "*", SearchOption.AllDirectories)) // files copied out of an image are read-only
            File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.ReadOnly);
        var old = File.ReadAllBytes(menuBdjo);
        var havi = old[(int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(old.AsSpan(8)) + 4 + 5] >> 4; // terminal info: length, font name, then the HAVi configuration
        var videos = Enumerable.Range(MoviePlaylist, IntroPlaylist - MoviePlaylist).TakeWhile(n => File.Exists(Path.Combine(bdmv, "PLAYLIST", $"{n:00000}.mpls"))).Count();
        File.Delete(jar); // read above; the signer writes a new file
        BdjSign.Write(jar, [.. xlet, .. menu], discRoot, uhd: havi == Bdmv.HaviUhd1080);
        // the objects an earlier start left in MovieObject.bdmv stay there, unused
        if (layout) TitleLayout(bdmv, videos, havi, File.Exists(Path.Combine(bdmv, "PLAYLIST", $"{IntroPlaylist:00000}.mpls")));
        return true;
    }
}

/// <summary>Finds files shipped in the assets folder next to the app (in a development build, or any parent: what goes on a disc must not come from a folder a normal user can create, such as C:\assets), else the copy embedded in the assembly.</summary>
public static class Assets
{
    public static string Find(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = Tools.BundledDir() == null ? d.Parent : null)
        {
            var p = Path.Combine(d.FullName, "assets", relative);
            if (File.Exists(p)) return p;
        }
        var name = "assets/" + relative.Replace('\\', '/');
        using var s = typeof(Assets).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException("asset not found: " + relative);
        var file = Path.Combine(Path.GetTempPath(), "BurnSmith", name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var o = File.Create(file)) s.CopyTo(o);
        return file;
    }
}
