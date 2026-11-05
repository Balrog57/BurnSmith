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

    /// <summary>Only an installed copy updates itself: a portable exe or a development build must not install anything in Program Files.</summary>
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

    /// <summary>Downloads the installer to a temporary folder and checks it against the hash GitHub gave; returns its path.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "BurnSmith-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"BurnSmith-Setup-{release.Version}.exe");
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
        if (release.Sha256 is { } expected)
        {
            await using var check = File.OpenRead(path);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new IOException("the downloaded installer does not match the hash GitHub gave: it was not run");
            }
        }
        return path;
    }

    /// <summary>Starts the installer (Windows asks for administrator rights); the caller then exits so the installer can replace the exe.</summary>
    public static void Install(string setup) =>
        Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1") { UseShellExecute = true });
}
