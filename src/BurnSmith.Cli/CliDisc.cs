using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using BurnSmith.Core;

namespace BurnSmith.Cli;

/// <summary>Disc inspection, ISO, burning, playback and repair CLI commands.</summary>
internal static class CliDisc
{
    public static int RunCheckDisc(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        var found = Directory.Exists(a[1]) ? DiscCheck.Folder(a[1]) : DiscCheck.Image(a[1], a.Count > 2 ? a[2] : null);
        Console.WriteLine(found.Count == 0 ? "ok   disc structure" : string.Join(Environment.NewLine, found.Select(p => "FAIL " + p)));
        return found.Count == 0 ? 0 : 1;
    }

    public static int RunCheckNavigation(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        try
        {
            var audio = CliBuild.Opt(a, "--audio") is { } audioText ? int.Parse(audioText) : 0;
            var sub = CliBuild.Opt(a, "--sub") is { } subText ? int.Parse(subText) : -1;
            var (trace, problems) = DiscCheck.Navigation(a[1], audio, sub);
            foreach (var line in trace) Console.WriteLine(line);
            foreach (var problem in problems) Console.Error.WriteLine("FAIL " + problem);
            return problems.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static int RunScanDamage(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        var (report, damaged) = DamageScan.Run(a[1], a.Contains("--decode"), line => Console.Error.WriteLine(line), !a.Contains("--no-decode"));
        foreach (var line in report) Console.WriteLine(line);
        return damaged ? 1 : 0;
    }

    public static int RunCheckScanDamage()
    {
        var wrong = DamageScan.SelfCheck();
        Console.WriteLine(wrong.Count == 0 ? "ok   scan-damage" : string.Join(Environment.NewLine, wrong.Select(p => "FAIL " + p)));
        return wrong.Count == 0 ? 0 : 1;
    }

    public static int RunCheckDrives()
    {
        var bad = 0;
        void DriveCheck(string what, bool ok) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) bad++; }
        Imapi.Media bdr = new("BD-R", true, 25_025_314_816, 25_025_314_816), bdre = new("BD-RE", false, 0, 25_025_314_816),
                    closed = new("BD-R", false, 0, 20_000_000_000), dvd = new("DVD+R", true, 4_700_372_992, 4_700_372_992);
        DriveCheck("a blank BD-R reads « BD-R vierge, 25 Go »", Iso.DiscState(bdr, true) == "BD-R vierge, 25 Go");
        DriveCheck("a written BD-RE reads « BD-RE déjà écrit »", Iso.DiscState(bdre, true) == "BD-RE déjà écrit");
        DriveCheck("an empty drive reads « pas de disque », a pressed disc « disque non inscriptible »", Iso.DiscState(null, false) == "pas de disque" && Iso.DiscState(null, true) == "disque non inscriptible");
        DriveCheck("a BD-50 project on a blank BD-25 is too big, a BD-25 one fits", Iso.TooSmall(bdr, 46.5) && !Iso.TooSmall(bdr, 23.3));
        DriveCheck("a written BD-RE is judged on its size once erased", Iso.TooSmall(bdre, 46.5) && !Iso.TooSmall(bdre, 23.3));
        DriveCheck("a closed BD-R is not judged (it says only what it holds)", !Iso.TooSmall(closed, 46.5));
        DriveCheck("a DVD-9 project on a DVD+R is too big, a DVD-5 one fits", Iso.TooSmall(dvd, 7.6) && !Iso.TooSmall(dvd, 4.2));
        var listed = Iso.Drives();
        foreach (var d in listed) Console.WriteLine($"     {d.Letter} {d.State} - {d.Name}");
        DriveCheck("this PC: only drives that write are listed (no « Msft Virtual DVD-ROM »)", listed.All(d => d.Writes && !d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)));
        DriveCheck("this PC: each burner IMAPI2 reaches says what is in it", listed.All(d => d.Id == d.Letter || d.State.Length > 0));
        Console.WriteLine(bad == 0 ? "check-drives: all ok" : $"check-drives: {bad} failed");
        return bad == 0 ? 0 : 1;
    }

    public static int RunDrives(List<string> a)
    {
        Console.WriteLine($"ImgBurn: {Iso.ImgBurn() ?? "absent (burning through IMAPI2)"}");
        var drives = Iso.Drives();
        foreach (var d in drives) Console.WriteLine($"{d.Letter,-3} {(d.State.Length > 0 ? d.State : "disque inconnu (ImgBurn seul)")} - {d.Name}");
        if (drives.Count == 0) Console.WriteLine("no disc burner visible to Windows");
        if (CliBuild.Opt(a, "--capacity") is { } need && double.TryParse(need, CultureInfo.InvariantCulture, out var needGb))
            foreach (var d in drives)
                if (Iso.Disc(d.Letter) is { } disc && Iso.TooSmall(disc, needGb)) Console.WriteLine($"warning: the {disc.Kind} in {d.Letter} holds {disc.TotalBytes / 1e9:0.0} GB, less than the {needGb:0.0} GB asked");
        Imapi.SelfTest();
        Console.WriteLine("IMAPI2: typed interfaces and progress events OK");
        return 0;
    }

    public static int RunErase(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        Imapi.Erase(a[1], Console.WriteLine);
        return 0;
    }

    public static int RunVerify(List<string> a)
    {
        if (a.Count < 3) return CliBuild.Usage();
        try { Imapi.Verify(a[1], a[2], Console.WriteLine, new Progress<double>(p => Console.Write($"\r{p:P0}  "))); return 0; }
        catch (IOException e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    public static int RunIso(List<string> a)
    {
        if (a.Count < 3) return CliBuild.Usage();
        Iso.Build(a[1], a[2], CliBuild.Opt(a, "--label") ?? "BURNSMITH", a.Contains("--dvd"), Console.WriteLine);
        return 0;
    }

    public static int RunBurn(List<string> a)
    {
        if (a.Count < 3) return CliBuild.Usage();
        if (File.Exists(a[1]) && Iso.Disc(a[2]) is { } inDrive && Iso.TooSmall(inDrive, new FileInfo(a[1]).Length / 1e9))
            Console.WriteLine($"warning: the image ({new FileInfo(a[1]).Length / 1e9:0.0} GB) is larger than the {inDrive.Kind} in {a[2]} ({inDrive.TotalBytes / 1e9:0.0} GB)");
        Iso.Burn(a[1], a[2], Console.WriteLine, new Progress<double>(p => Console.Write($"\r{p:P0}  ")),
            speed: int.TryParse(CliBuild.Opt(a, "--speed"), out var sp) ? sp : a.Contains("--max-compat") ? Iso.Speeds[^1] : 0,
            verify: !a.Contains("--no-verify"), eject: a.Contains("--eject"), erase: a.Contains("--erase"));
        return 0;
    }

    public static int RunPlay(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        Console.WriteLine($"vlc {Vlc.Arguments(a[1])}");
        if (!Vlc.Play(a[1])) { Console.WriteLine($"VLC n'est pas installé sur cet ordinateur : {Vlc.Site}"); return 1; }
        if (!Vlc.Arguments(a[1]).StartsWith("\"dvd:")) Console.WriteLine(Vlc.JavaNote);
        return 0;
    }

    public static int RunCheckPlay()
    {
        var playFailed = 0;
        void Pl(string what, bool ok, string shown) { if (!ok) playFailed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what} : {shown}"); }
        var playRoot = Path.Combine(Path.GetTempPath(), "bs_play " + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var bd = Directory.CreateDirectory(Path.Combine(playRoot, "JUDGE DREDD", "BDMV")).Parent!.FullName;
            var dvdDir = Directory.CreateDirectory(Path.Combine(playRoot, "Épisodes #1 à 100%", "VIDEO_TS")).Parent!.FullName;
            var image = Path.Combine(playRoot, "JUDGE DREDD.iso");
            File.WriteAllBytes(image, []);
            var url = "bluray:///" + bd.Replace('\\', '/');
            static bool One(string s) => s.Length > 2 && s[0] == '"' && s[^1] == '"' && s.Count(c => c == '"') == 2 && s[^2] != '\\';
            var b = Vlc.Arguments(bd);
            Pl("a Blu-ray folder with a space is bluray:/// and one argument", b == $"\"{url}\"" && One(b), b);
            Pl("its BDMV folder, or a trailing \\, give the same", Vlc.Arguments(Path.Combine(bd, "BDMV")) == b && Vlc.Arguments(bd + "\\") == b, Vlc.Arguments(Path.Combine(bd, "BDMV")));
            var d = Vlc.Arguments(dvdDir);
            Pl("a DVD folder is dvd:///, # and % escaped (VLC reads # as title:chapter)", d == $"\"dvd:///{dvdDir.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23")}\"" && One(d) && !d.Contains('#'), d);
            var i = Vlc.Arguments(image);
            Pl("an ISO is passed as it is, quoted", i == $"\"{image}\"" && One(i), i);
            var refused = false;
            try { Vlc.Arguments(playRoot); } catch (InvalidOperationException) { refused = true; }
            Pl("a folder with no disc is refused before VLC starts", refused, playRoot);
            var exe = Vlc.Find();
            Pl("VLC found is the installed one, never the test copy under tools\\vlc", exe == null || !exe.Contains(@"\tools\vlc\", StringComparison.OrdinalIgnoreCase), exe ?? "VLC not installed here (the window then offers videolan.org)");
        }
        finally { Directory.Delete(playRoot, true); }
        Console.WriteLine(playFailed == 0 ? "all ok" : $"{playFailed} failed");
        return playFailed == 0 ? 0 : 1;
    }

    public static int RunProbe(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        Console.WriteLine(JsonSerializer.Serialize(BluRay.Probe(a[1]), new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }

    public static int RunRepairTopMenu(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        var fixedIt = BluRay.RepairTopMenu(a[1]);
        Console.WriteLine(fixedIt ? "repaired: the Top Menu is the menu, each video plays in its own title" : "nothing to repair (not a BD-J menu disc of the old kind)");
        return 0;
    }

    public static int RunReplaceFilm(List<string> a)
    {
        if (a.Count < 2) return CliBuild.Usage();
        var map = CliBuild.OptAll(a, "--map").SelectMany(m => m.Split(',', StringSplitOptions.RemoveEmptyEntries)).Select(m => m.Split('='))
            .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p.Length == 2 && int.TryParse(p[1].Trim().Replace("mkv:", ""), out var n) ? n : throw new ArgumentException($"--map {string.Join('=', p)} : A1=mkv:N attendu"));
        if (CliBuild.Opt(a, "--mkv") is not { } mkv || CliBuild.Opt(a, "--out") is not { } outFolder) { Console.Error.WriteLine("replace-film : --mkv et --out sont nécessaires"); return 2; }
        return ReplaceFilm.Run(new ReplaceFilmJob(Path.GetFullPath(a[1]), Path.GetFullPath(mkv), Path.GetFullPath(outFolder),
            CliBuild.Opt(a, "--iso") is { } isoOut ? Path.GetFullPath(isoOut) : null,
            int.TryParse(CliBuild.Opt(a, "--playlist"), out var plNumber) ? plNumber : null,
            double.TryParse(CliBuild.Opt(a, "--cut-start"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cutAt) ? cutAt : null,
            map, a.Contains("--no-align"), a.Contains("--measure-only"), CliBuild.Opt(a, "--label")), Console.WriteLine);
    }

    public static int RunCheckFilmSync()
    {
        var problems = FilmSync.SelfCheck();
        Console.WriteLine(problems.Count == 0 ? "ok   film sync" : string.Join(Environment.NewLine, problems.Select(p => "FAIL " + p)));
        return problems.Count == 0 ? 0 : 1;
    }
}
