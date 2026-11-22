using BurnSmith.Core;
using BurnSmith.Core.Menu;

namespace BurnSmith.Cli;

static class UpdateCheck
{
    public static async Task<int> Run(bool live)
    {
        var upFailed = 0;
        void Up(string what, bool ok) { if (!ok) upFailed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        Up("0.1.0 is not newer than 0.1.0.0", !Updater.IsNewer("v0.1.0", new Version(0, 1, 0, 0)));
        Up("0.1.1 is newer than 0.1.0.0", Updater.IsNewer("v0.1.1", new Version(0, 1, 0, 0)));
        Up("0.2 is newer than 0.1.9", Updater.IsNewer("0.2", new Version(0, 1, 9)));
        Up("a tag that is no version is ignored", !Updater.IsNewer("nightly", new Version(0, 1, 0)));
        Up("a dev build is not installed", !Updater.IsInstalled());
        if (live)
        {
            var r = await Updater.LatestAsync(new Version(0, 0, 0));
            Console.WriteLine(r != null ? $"newest release: {r.Version} {string.Join(" | ", r.Urls)} sha256={r.Sha256 ?? "(none given)"}" : "no release with a BurnSmith-Setup asset yet");
            Up("the newest release carries a digest, so it will not be refused", r?.Sha256 != null);
            if (r != null)
                foreach (var address in r.Urls) // each address on its own: the update needs only one, but a failing one is worth knowing
                {
                    using var http = new System.Net.Http.HttpClient();
                    using var ask = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, address);
                    ask.Headers.Accept.ParseAdd("application/octet-stream");
                    ask.Headers.UserAgent.ParseAdd("BurnSmith-check");
                    ask.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
                    string answer;
                    try
                    {
                        using var got = await http.SendAsync(ask, System.Net.Http.HttpCompletionOption.ResponseHeadersRead); // 2 bytes read, even from a server that ignores the range
                        var head = new byte[2];
                        if (got.IsSuccessStatusCode) await (await got.Content.ReadAsStreamAsync()).ReadExactlyAsync(head);
                        answer = got.IsSuccessStatusCode ? System.Text.Encoding.ASCII.GetString(head) : $"{(int)got.StatusCode}";
                    }
                    catch (System.Net.Http.HttpRequestException e) { answer = e.Message; }
                    Console.WriteLine($"  {address}: {(answer == "MZ" ? "the installer's first bytes" : answer)}");
                    if (address == r.Urls[0]) Up("the first address the update tries serves the installer", answer == "MZ");
                }
        }
    
        var work = Path.Combine(Path.GetTempPath(), $"burnsmith-check-update-{Environment.ProcessId}");
        var realTmp = Environment.GetEnvironmentVariable("TMP");
        var icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        var locked = Path.Combine(work, "tools", "locked");
        Directory.CreateDirectory(locked);
        try
        {
            // the update: a server on this PC, and %TEMP% moved here so that a real download is not touched
            Environment.SetEnvironmentVariable("TMP", work);
            var updates = Path.Combine(work, "BurnSmith-update");
            Up("the update folder of this check is its own", Path.GetTempPath().TrimEnd('\\').Equals(work, StringComparison.OrdinalIgnoreCase));
            var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            port.Start();
            var url = $"http://localhost:{((System.Net.IPEndPoint)port.LocalEndpoint).Port}/";
            port.Stop();
            using var server = new System.Net.HttpListener();
            server.Prefixes.Add(url);
            server.Start();
            var body = System.Text.Encoding.UTF8.GetBytes("not an installer");
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
            var requests = 0;
            var gatewayErrors = 0;
            _ = Task.Run(async () =>
            {
                while (server.IsListening)
                {
                    var call = await server.GetContextAsync();
                    Interlocked.Increment(ref requests);
                    var path = call.Request.Url!.AbsolutePath;
                    if (path == "/down") { Interlocked.Increment(ref gatewayErrors); call.Response.StatusCode = 504; } // what github.com/…/releases/download answered on 2026-11-08
                    else if (path == "/api" && !(call.Request.Headers["Accept"] ?? "").Contains("application/octet-stream"))
                        await call.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"name\":\"BurnSmith-Setup.exe\"}")); // the API describes the asset unless asked for the file
                    else await call.Response.OutputStream.WriteAsync(body);
                    call.Response.Close();
                }
            });
            var v = new Version(9, 9, 9, 9);
            async Task<Exception?> Download(string? sha, params string[] urls) { try { await Updater.DownloadAsync(new Updater.Release(v, urls.Length > 0 ? urls : [url], sha)); return null; } catch (Exception e) { return e; } }
            Directory.CreateDirectory(updates);
            var older = Path.Combine(updates, "BurnSmith-Setup-0.0.1.exe");
            File.WriteAllText(older, "old");
            Up("no digest: refused", await Download(null) is IOException);
            Up("no digest: nothing asked of the server, nothing written, nothing removed", requests == 0 && Directory.GetFiles(updates).SequenceEqual([older]));
            var lockedOlder = Path.Combine(updates, "BurnSmith-Setup-0.0.2.exe");
            var sameVersion = Path.Combine(updates, $"BurnSmith-Setup-{v}.exe");
            File.WriteAllText(lockedOlder, "old");
            File.WriteAllText(sameVersion, "old");
            using (File.Open(lockedOlder, FileMode.Open, FileAccess.Read, FileShare.None))
            using (File.Open(sameVersion, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failure = await Download(digest);
                Up("right digest: downloaded, locked older installers did not stop it", failure == null);
                if (failure != null) Console.WriteLine(failure.Message);
                Up("an older installer is removed when a download starts", !File.Exists(older));
                Up("a locked one is left alone", File.Exists(lockedOlder) && File.Exists(sameVersion));
            }
            Up("the download is what the server sent", Directory.GetFiles(updates, $"BurnSmith-Setup-{v}-*.exe").SingleOrDefault() is { } got && File.ReadAllBytes(got).SequenceEqual(body));
            static bool Refused(Action act) { try { act(); return false; } catch (IOException) { return true; } }
            Up("the checked download cannot be changed before it runs as administrator", Directory.GetFiles(updates, $"BurnSmith-Setup-{v}-*.exe").Single() is var checkedSetup
                && Refused(() => File.OpenWrite(checkedSetup).Dispose()) && Refused(() => File.Delete(checkedSetup)));
            Up("wrong digest: refused, the download does not stay", await Download(new string('0', 64)) is IOException && Directory.GetFiles(updates, $"BurnSmith-Setup-{v}-*.exe").Length == 0);
            Up("the API's address gives the file: it is asked for the file, not its description", await Download(digest, url + "api") == null);
            var failure504 = await Download(digest, url + "down", url);
            Up("the first address answers 504: the next one gives the file", failure504 == null && gatewayErrors == 1);
            var start504 = System.Diagnostics.Stopwatch.StartNew();
            var allDown = await Download(digest, url + "down", url + "down");
            Up("every address fails: three rounds of both, then a refusal that says what GitHub answered", allDown is IOException && gatewayErrors == 7 && allDown.Message.Contains("504")
                && start504.Elapsed < TimeSpan.FromSeconds(30));
            server.Stop();
            Environment.SetEnvironmentVariable("TMP", realTmp);
    
            // the installer's end decides: an error code comes back as a refusal, and the wait lasts until it is over
            async Task<Exception?> Install(string script) { var cmd = Path.Combine(work, "fake-setup.cmd"); File.WriteAllText(cmd, script); try { await Updater.Install(cmd); return null; } catch (Exception e) { return e; } }
            var refusal = await Install("@exit /b 1");
            Up("an installer ending on an error code (a refused prompt) is told, with its code", refusal is IOException && refusal.Message.Contains("code 1"));
            var seen = Path.Combine(work, "fake-setup.txt");
            Up("an installer ending well: no error, waited for to its end, given the silent arguments and the relaunch",
               await Install($"@ping -n 2 127.0.0.1 >nul\r\n@echo %* > \"{seen}\"\r\n@exit /b 0") == null && File.Exists(seen) && File.ReadAllText(seen) is var given && given.Contains("/SILENT") && given.Contains("/relaunch=1"));
    
            var noTemplates = Path.Combine(work, "none.zip");
            Up("packing no template is an error", Throws(() => TemplateCatalog.Pack(noTemplates, roots: [work])));
            File.Delete(noTemplates);
    
            // publish.ps1: no tsMuxeR must stop it first of all, before anything is built
            var script = "publish.ps1";
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, script))) { script = Path.Combine(d.FullName, script); break; }
            var refused = "";
            try { Tools.Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-TsMuxer", Path.Combine(work, "none.exe")], env: new Dictionary<string, string> { ["PSModulePath"] = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\Modules") }); } // not the modules of a PowerShell 7 this was started from
            catch (InvalidOperationException e) { refused = e.Message.Replace("\r", "").Replace("\n", ""); }
            Up("publish.ps1 refuses a missing tsMuxeR", refused.Contains("none.exe"));
    
            // where tools are looked for. Development first: Payload null, as here, and every checkout has a tools folder (vlctest.ps1 is committed)
            Environment.SetEnvironmentVariable("BURNSMITH_TOOLS", null);
            var beside = Path.Combine(AppContext.BaseDirectory, "tools");
            Up("a development build looks in the tools folder of the parents", Tools.SearchRoots().Any(r => r != beside));
            var devJar = Assets.Find(@"bdj\menu.jar");
            Up("a development build takes the jar of a parent's assets folder", AppContext.BaseDirectory.StartsWith(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(devJar)!, "..", "..")), StringComparison.OrdinalIgnoreCase));
            Environment.SetEnvironmentVariable("BURNSMITH_TOOLS", Path.Combine(work, "does-not-exist"));
            Up("a BURNSMITH_TOOLS that does not exist is passed over", !Tools.SearchRoots().Any(r => r.StartsWith(work)));
            Up("the cache can be used from several threads", typeof(Tools).GetField("cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.FieldType.Name.StartsWith("Concurrent") == true);
    
            var tools = Path.GetDirectoryName(locked)!;
            var onPath = Path.Combine(work, "on-path");
            Directory.CreateDirectory(onPath);
            foreach (var name in new[] { "in-both.exe", "only-path.exe", "spaced.exe" }) File.WriteAllText(Path.Combine(onPath, name), "");
            File.WriteAllText(Path.Combine(tools, "in-env.exe"), "");
            Environment.SetEnvironmentVariable("PATH", onPath);
            Environment.SetEnvironmentVariable("BURNSMITH_TOOLS", tools);
            if (!AppFiles.Root().StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) // through dotnet <dll> the fake tools below would replace those an installed copy unpacked
            {
                Up("where a release looks for tools: run BurnSmith.Cli.exe, not dotnet <dll>", false);
                return 1;
            }
            var carried = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(carried, System.IO.Compression.ZipArchiveMode.Create, true))
                foreach (var name in new[] { "in-env.exe", "in-both.exe", "only-bundled.exe" }) zip.CreateEntry(name);
            // the unpack: the same content is left alone whatever exe asks, other content replaces it, a swap's leftovers go and nothing else does
            var unpackings = 0;
            void Fill(string d) { unpackings++; File.WriteAllText(Path.Combine(d, "file"), $"{unpackings}"); }
            var unpacked = Path.Combine(AppFiles.Root(), "check-unpack");
            using (var dead = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit") { CreateNoWindow = true })!)
            {
                dead.WaitForExit();
                string[] gone = [$"{unpacked}.{dead.Id}.old", $"{unpacked}.{dead.Id}.tmp"];
                string[] kept = [$"{unpacked}.4.tmp", $"{unpacked}.mine", $"{unpacked}.{dead.Id}.old.2", $"{unpacked}2.{dead.Id}.old"]; // 4 is Windows' System process: running, so maybe swapping
                foreach (var folder in gone.Concat(kept)) { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "x"), ""); }
                AppFiles.Unpack("check-unpack", "A", Fill);
                Up("an unpack leaves the same stamp alone and redoes another one",
                   unpackings == 1 && AppFiles.Unpack("check-unpack", "A", Fill) == unpacked && unpackings == 1 && AppFiles.Unpack("check-unpack", "B", Fill) == unpacked && unpackings == 2 && File.ReadAllText(Path.Combine(unpacked, "file")) == "2");
                Up("what a dead process's swap left is removed; a running process's, and folders of other names, stay", !gone.Any(Directory.Exists) && kept.All(Directory.Exists));
                foreach (var folder in kept) Directory.Delete(folder, true);
                Directory.Delete(unpacked, true);
            }
            byte[] twoBytes = [1, 2];
            var stamped = new MemoryStream(twoBytes);
            Up("a stamp comes from the content, and the stream can then be read from its start", AppFiles.Stamp(stamped) == AppFiles.Stamp(new MemoryStream(twoBytes)) && stamped.Position == 0 && AppFiles.Stamp(stamped) != AppFiles.Stamp(new MemoryStream(new byte[] { 1, 3 })));
    
            Tools.Payload = () => new MemoryStream(carried.ToArray()); // from here on this process is a release, with these tools "carried"
            Up("a release looks in BURNSMITH_TOOLS and no parent", Tools.SearchRoots().Where(r => r != beside).SequenceEqual([tools]));
            Tools.Run(icacls, [locked, "/deny", "*S-1-1-0:(RD)"]);
            string? Found(string exe) { try { return Tools.Find(exe); } catch (Exception e) { Console.WriteLine($"{exe}: {e.GetType().Name}: {e.Message}"); return null; } }
            Up("a locked sub-folder does not stop the search", Found("only-bundled.exe")?.StartsWith(Tools.BundledDir()!) == true);
            Up("BURNSMITH_TOOLS comes before the bundled copy", Found("in-env.exe")?.StartsWith(tools) == true);
            Up("the bundled copy comes before PATH", Found("in-both.exe")?.StartsWith(Tools.BundledDir()!) == true);
            Up("PATH comes last", Found("only-path.exe")?.StartsWith(onPath) == true);
            Environment.SetEnvironmentVariable("TMP", work); // the embedded jar is written under %TEMP%: not over a real one
            var jar = Assets.Find(@"bdj\menu.jar");
            Environment.SetEnvironmentVariable("TMP", realTmp);
            Up("a release takes the embedded jar, not the one of a parent's assets folder", jar.StartsWith(work, StringComparison.OrdinalIgnoreCase));
            using (var opened = System.IO.Compression.ZipFile.OpenRead(jar))
                Up("the jar can be used", opened.Entries.Any(e => e.Name.EndsWith(".class")));
    
            var cwd = Path.Combine(work, "cwd");
            Directory.CreateDirectory(cwd);
            File.WriteAllText(Path.Combine(cwd, "in-cwd.exe"), "");
            Environment.SetEnvironmentVariable("PATH", $";  ; {onPath} ;");
            var home = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(cwd);
            try
            {
                string? fromCwd = null;
                try { fromCwd = Tools.Find("in-cwd.exe"); } catch (FileNotFoundException) { }
                Up("an empty PATH entry is not the current folder", fromCwd == null);
            }
            finally { Directory.SetCurrentDirectory(home); }
            Up("spaces around a PATH entry do not matter", Found("spaced.exe")?.StartsWith(onPath) == true);
            var absent = "";
            try { Tools.Find("not-anywhere.exe"); } catch (Exception e) { absent = e.Message; }
            Up("a missing tool says how to provide it", absent.Contains("BURNSMITH_TOOLS") && absent.Contains("PATH"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", realTmp);
            Tools.Run(icacls, [locked, "/remove:d", "*S-1-1-0"]);
            Tools.TryDelete(work);
        }
        return upFailed == 0 ? 0 : 1;
    
        static bool Throws(Action act) { try { act(); return false; } catch (InvalidOperationException) { return true; } }
    }
}
