using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BurnSmith.Core;

/// <summary>
/// Updates an installed BurnSmith: finds the newest GitHub release, downloads its <c>BurnSmith-Setup-X.exe</c> and runs it (the installer
/// asks Windows for administrator rights, closes this exe, replaces the files in Program Files and starts the new one).
/// </summary>
public static class Updater
{
    public const string LatestUrl = "https://api.github.com/repos/Balrog57/BurnSmith/releases/latest";

    public sealed record Release(Version Version, string Url, string? Sha256);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30), DefaultRequestHeaders = { { "User-Agent", "BurnSmith-updater" } } };

    /// <summary>"0.1.0" and "0.1.0.0" are the same version (<see cref="Version"/> would call the first one older).</summary>
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>The version a release tag such as "v0.2.0" names, or null when it is not a version.</summary>
    public static Version? FromTag(string tag) => Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? Normalize(v) : null;

    public static bool IsNewer(string tag, Version current) => FromTag(tag) is { } v && v > Normalize(current);

    /// <summary>Only an installed copy updates itself: a development build, or an exe run from anywhere else, must not install anything in Program Files.</summary>
    public static bool IsInstalled() =>
        Environment.ProcessPath is { } p && Path.GetDirectoryName(p) is { } dir
        && dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);

    /// <summary>The newest release when it is newer than <paramref name="current"/> and carries an installer, else null.</summary>
    public static async Task<Release?> LatestAsync(Version current, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestUrl, ct));
        var release = doc.RootElement;
        if (release.TryGetProperty("tag_name", out var tag) && tag.GetString() is { } t && IsNewer(t, current))
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
                if (asset.GetProperty("name").GetString() is { } name && name.StartsWith("BurnSmith-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null; // "sha256:<hex>", given by GitHub for the files it stores
                    return new Release(FromTag(t)!, asset.GetProperty("browser_download_url").GetString()!, digest?.StartsWith("sha256:") == true ? digest[7..] : null);
                }
        return null;
    }

    /// <summary>Downloads the installer to a temporary folder and checks it against the hash GitHub gave, refusing a release without one since the installer runs as administrator; returns its path.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(release.Sha256)) throw new IOException("GitHub ne donne pas de hash sha256 pour cet installateur : il n'a pas été téléchargé");
        var dir = Path.Combine(Path.GetTempPath(), "BurnSmith-update");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "BurnSmith-Setup-*.exe"))
            try { File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // still running or being scanned: the next update removes it
        var path = Path.Combine(dir, $"BurnSmith-Setup-{release.Version}-{Environment.ProcessId}.exe"); // the process id: a locked older download of this version cannot stop this one
        using (var response = await Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var from = await response.Content.ReadAsStreamAsync(ct);
            await using var to = File.Create(path);
            var buffer = new byte[1 << 20];
            long done = 0;
            for (int n; (n = await from.ReadAsync(buffer, ct)) > 0;)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        bool same;
        await using (var check = File.OpenRead(path))
            same = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!same)
        {
            File.Delete(path);
            throw new IOException("the downloaded installer does not match the hash GitHub gave: it was not run");
        }
        return path;
    }

    /// <summary>
    /// Runs the installer and waits for it, so that a refused or missed Windows prompt can be told. No "runas" here: the setup elevates itself, and must start as the user for its
    /// <c>runasoriginaluser</c> relaunch to come back unelevated. A good install closes this process through Restart Manager (CloseApplications in the script) before this returns.
    /// </summary>
    /// <exception cref="IOException">The installer ended with an error code: nothing was installed.</exception>
    public static async Task Install(string setup)
    {
        using var p = Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1") { UseShellExecute = true }) ?? throw new IOException("l'installateur n'a pas démarré");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new IOException($"l'installateur s'est arrêté sans rien installer (code {p.ExitCode}), par exemple parce que la demande d'administrateur de Windows a été refusée ou est restée sans réponse");
    }
}
