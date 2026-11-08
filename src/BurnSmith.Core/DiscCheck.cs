using System.Text;
using System.Text.RegularExpressions;

namespace BurnSmith.Core;

/// <summary>
/// "Test before burning": what can be told from the files of a finished disc folder (and its ISO) without a player. Each problem is one line; an empty list means
/// the structure looks right. It catches a build that stopped half way, a file that went missing, a stream that is not cut in whole packets, a DVD whose IFO
/// no longer matches its VOB: not whether a player will like the disc.
/// </summary>
public static class DiscCheck
{
    /// <summary>The problems of a disc folder (a BDMV or a VIDEO_TS folder inside it); with <paramref name="mediaGb"/> (the nominal size of the disc: 25 for a BD25) files that do not fit it.</summary>
    public static List<string> Folder(string dir, double mediaGb = 0)
    {
        var problems = new List<string>();
        if (Directory.Exists(Path.Combine(dir, "BDMV"))) CheckBluRay(Path.Combine(dir, "BDMV"), problems);
        else if (Directory.Exists(Path.Combine(dir, "VIDEO_TS"))) CheckDvd(Path.Combine(dir, "VIDEO_TS"), problems);
        else problems.Add("ni BDMV ni VIDEO_TS dans le dossier");
        if (mediaGb > 0 && Directory.Exists(dir)) Fits(new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length), "les fichiers du disque", mediaGb, problems);
        return problems;
    }

    /// <summary>
    /// Bytes over what the disc holds. The nominal size is a floor of the real one (BD25 25.03 GB, DVD+R 4.70 GB, DVD-R DL 8.54 GB, BD50 50.05 GB), so what passes here fits
    /// any disc of that name.
    /// </summary>
    static void Fits(long bytes, string what, double mediaGb, List<string> problems)
    {
        if (bytes > mediaGb * 1e9) problems.Add($"{what} ({bytes / 1e9:0.00} Go) dépassent la capacité du disque ({mediaGb:0.##} Go)");
    }

    /// <summary>The problems of an image: whole sectors, a volume descriptor (ISO 9660 or UDF), and, when the folder it was made from is given, room for all its files; with <paramref name="mediaGb"/> an image the disc cannot hold.</summary>
    public static List<string> Image(string file, string? folder = null, double mediaGb = 0)
    {
        var problems = new List<string>();
        if (!File.Exists(file)) return ["l'image ISO n'existe pas"];
        using var f = File.OpenRead(file);
        if (f.Length % 2048 != 0) problems.Add("la taille de l'image n'est pas un nombre entier de secteurs de 2048 octets");
        if (mediaGb > 0) Fits(f.Length, "l'image ISO", mediaGb, problems);
        var head = new byte[8];
        f.Position = 16 * 2048 + 1;
        if (f.Length < 17 * 2048 || f.Read(head) < 5 || Encoding.ASCII.GetString(head, 0, 5) is not ("CD001" or "BEA01")) problems.Add("pas de descripteur de volume (ISO 9660 ou UDF) au secteur 16");
        if (f.Length > 257 * 2048 + 2)
        {
            f.Position = 256 * 2048;
            var anchor = new byte[2];
            if (f.Read(anchor) < 2 || anchor[0] != 2 || anchor[1] != 0) problems.Add("pas de point d'ancrage UDF au secteur 256");
            else
            {
                f.Position = f.Length - 2048; // the second anchor is in the last sector: an image cut short has none
                if (f.Read(anchor) < 2 || anchor[0] != 2 || anchor[1] != 0) problems.Add("pas de second point d'ancrage UDF en fin d'image : l'image est tronquée");
            }
        }
        if (folder != null && Directory.Exists(folder))
        {
            var content = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length);
            if (f.Length < content) problems.Add($"l'image ({f.Length:N0} octets) est plus petite que les fichiers du disque ({content:N0} octets)");
        }
        return problems;
    }

    static bool Starts(string file, string magic, string shown, List<string> problems)
    {
        if (!File.Exists(file)) { problems.Add($"{shown} manque"); return false; }
        using var f = File.OpenRead(file);
        var head = new byte[magic.Length];
        if (f.Read(head) < head.Length || Encoding.ASCII.GetString(head) != magic) { problems.Add($"{shown} n'est pas un fichier {magic}"); return false; }
        return true;
    }

    static void Same(string a, string b, string shown, List<string> problems)
    {
        if (!File.Exists(b)) problems.Add($"la copie de secours {shown} manque");
        else if (File.Exists(a) && !File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b))) problems.Add($"la copie de secours {shown} diffère de l'original");
    }

    static void CheckBluRay(string bdmv, List<string> problems)
    {
        Starts(Path.Combine(bdmv, "index.bdmv"), "INDX", "BDMV\\index.bdmv", problems);
        Starts(Path.Combine(bdmv, "MovieObject.bdmv"), "MOBJ", "BDMV\\MovieObject.bdmv", problems);
        foreach (var name in new[] { "index.bdmv", "MovieObject.bdmv" }) Same(Path.Combine(bdmv, name), Path.Combine(bdmv, "BACKUP", name), "BACKUP\\" + name, problems);
        var lists = Directory.Exists(Path.Combine(bdmv, "PLAYLIST")) ? Directory.GetFiles(Path.Combine(bdmv, "PLAYLIST"), "*.mpls") : [];
        if (lists.Length == 0) problems.Add("aucune liste de lecture (PLAYLIST\\*.mpls)");
        var clips = new SortedSet<string>();
        foreach (var list in lists)
        {
            if (!Starts(list, "MPLS", "PLAYLIST\\" + Path.GetFileName(list), problems)) continue;
            foreach (Match m in Regex.Matches(Encoding.Latin1.GetString(File.ReadAllBytes(list)), @"(\d{5})M2TS")) clips.Add(m.Groups[1].Value);
        }
        foreach (var clip in clips)
        {
            Starts(Path.Combine(bdmv, "CLIPINF", clip + ".clpi"), "HDMV", $"CLIPINF\\{clip}.clpi", problems);
            var stream = Path.Combine(bdmv, "STREAM", clip + ".m2ts");
            if (!File.Exists(stream)) { problems.Add($"STREAM\\{clip}.m2ts manque (une liste de lecture l'utilise)"); continue; }
            Packets(stream, 192, 4, $"STREAM\\{clip}.m2ts", problems);
        }
        if (Directory.Exists(Path.Combine(bdmv, "JAR")) && Directory.GetFiles(Path.Combine(bdmv, "JAR")).Length > 0 && !(Directory.Exists(Path.Combine(bdmv, "BDJO")) && Directory.GetFiles(Path.Combine(bdmv, "BDJO"), "*.bdjo").Length > 0))
            problems.Add("le menu BD-J (JAR) n'a pas de fichier BDJO");

        // a player starts a signed BD-J application only with the disc's certificates and a signature they vouch for
        var jars = Directory.Exists(Path.Combine(bdmv, "JAR")) ? Directory.GetFiles(Path.Combine(bdmv, "JAR"), "*.jar") : [];
        var cert = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bdmv))!, "CERTIFICATE");
        if (jars.Length == 0) return;
        foreach (var name in new[] { "app.discroot.crt", "bu.discroot.crt", "id.bdmv" })
            if (!File.Exists(Path.Combine(cert, name))) problems.Add($"CERTIFICATE\\{name} manque : une platine refusera le menu BD-J");
            else Same(Path.Combine(cert, name), Path.Combine(cert, "BACKUP", name), "CERTIFICATE\\BACKUP\\" + name, problems);
        if (File.Exists(Path.Combine(cert, "app.discroot.crt")) && File.Exists(Path.Combine(cert, "id.bdmv")))
            foreach (var jar in jars) problems.AddRange(BdjSign.Verify(jar, File.ReadAllBytes(Path.Combine(cert, "app.discroot.crt")), File.ReadAllBytes(Path.Combine(cert, "id.bdmv"))));
    }

    /// <summary>A transport stream is a run of whole packets (192 bytes with the time stamp), each starting with the sync byte 0x47.</summary>
    static void Packets(string file, int size, int sync, string shown, List<string> problems)
    {
        var info = new FileInfo(file);
        if (info.Length == 0 || info.Length % size != 0) { problems.Add($"{shown} n'est pas fait de paquets entiers de {size} octets ({info.Length:N0} octets)"); return; }
        using var f = File.OpenRead(file);
        var count = info.Length / size;
        var buffer = new byte[1];
        foreach (var k in Enumerable.Range(0, (int)Math.Min(count, 40)).Select(i => (long)i).Concat([count / 2, count - 1]))
        {
            f.Position = k * size + sync;
            if (f.Read(buffer) < 1 || buffer[0] != 0x47) { problems.Add($"{shown} : le paquet {k} ne commence pas par l'octet de synchronisation"); return; }
        }
    }

    static void CheckDvd(string video, List<string> problems)
    {
        var all = Directory.GetFiles(video);
        long Sectors(string pattern) => all.Where(f => Regex.IsMatch(Path.GetFileName(f), pattern, RegexOptions.IgnoreCase)).Sum(f => new FileInfo(f).Length) / 2048 - 1;
        long LastSector(string ifo) { using var f = File.OpenRead(ifo); if (f.Length < 16) return -1; f.Position = 12; var b = new byte[4]; f.ReadExactly(b); return b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]; }
        if (Starts(Path.Combine(video, "VIDEO_TS.IFO"), "DVDVIDEO-VMG", "VIDEO_TS.IFO", problems))
        {
            Same(Path.Combine(video, "VIDEO_TS.IFO"), Path.Combine(video, "VIDEO_TS.BUP"), "VIDEO_TS.BUP", problems);
            if (LastSector(Path.Combine(video, "VIDEO_TS.IFO")) != Sectors(@"^VIDEO_TS\.(IFO|BUP|VOB)$")) problems.Add("VIDEO_TS.IFO ne donne pas la taille des fichiers VIDEO_TS");
        }
        var sets = all.Select(f => Regex.Match(Path.GetFileName(f), @"^VTS_(\d\d)_0\.IFO$", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        if (sets.Count == 0) problems.Add("aucun titre (VTS_nn_0.IFO)");
        foreach (var n in sets)
        {
            var ifo = Path.Combine(video, $"VTS_{n}_0.IFO");
            if (!Starts(ifo, "DVDVIDEO-VTS", $"VTS_{n}_0.IFO", problems)) continue;
            Same(ifo, Path.Combine(video, $"VTS_{n}_0.BUP"), $"VTS_{n}_0.BUP", problems);
            if (LastSector(ifo) != Sectors($@"^VTS_{n}_\d\.(IFO|BUP|VOB)$")) problems.Add($"VTS_{n}_0.IFO ne donne pas la taille des fichiers du titre {n}");
            if (!File.Exists(Path.Combine(video, $"VTS_{n}_1.VOB"))) problems.Add($"VTS_{n}_1.VOB manque");
            foreach (var vob in all.Where(f => Regex.IsMatch(Path.GetFileName(f), $@"^VTS_{n}_\d\.VOB$", RegexOptions.IgnoreCase)))
            {
                var info = new FileInfo(vob);
                if (info.Length == 0 || info.Length % 2048 != 0) { problems.Add($"{info.Name} n'est pas fait de secteurs entiers de 2048 octets"); continue; }
                using var f = File.OpenRead(vob);
                var head = new byte[4];
                if (f.Read(head) < 4 || !head.AsSpan().SequenceEqual(new byte[] { 0, 0, 1, 0xBA })) problems.Add($"{info.Name} ne commence pas par un en-tête de paquet MPEG");
            }
        }
    }
}
