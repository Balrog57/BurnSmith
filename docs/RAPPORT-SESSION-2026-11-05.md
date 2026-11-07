# BurnSmith code review (2026-11-05)

**What this repo does:** BurnSmith is a Windows desktop app and command-line tool. It turns videos, photos and songs into DVD or Blu-ray discs with menus, then makes an ISO and burns it. I assumed one person on one PC, with public releases on GitHub, so a failure costs hours of encoding or a wasted disc rather than a server outage.

**How I checked:** I built a clean copy of the code (version 0.5.1) and ran small test builds with `ffprobe` on the results. Each finding says if I only read the code instead of running it. No project code was changed by this review, and none of these findings had been fixed when it was written. Line numbers are those of that version.

**About the other report:** `docs/RAPPORT-DE-SESSION.md` needs correcting on three points:

- It calls the tool search order safe, but see finding 6.
- It says DVD mode "fails cleanly", but see finding 7.
- It lists folder cleanup as a strength, but see finding 11 and finding 1.

## Architecture

```
BurnSmith.App (WPF window) ─┐
BurnSmith.Cli (commands + check-*) ─┴─► BurnSmith.Core ─► ffmpeg, ffprobe, tsMuxeR (carried in the exe)
BurnSmith.Payload (the embedded zips)        │          ─► dvdauthor, spumux (not carried)
                                             └─► Windows IMAPI2 (ISO and burning)
bdj/ → assets/bdj/menu.jar (Java Blu-ray menu)
```

The disc flow is: `Probe` → cut and tracks → encode or copy → render menu → `tsMuxeR` or `dvdauthor` → `DiscCheck` → ISO → burn → read back.

The split is good: all the logic lives in `Core`, and the window and command line stay thin. Keep this structure, because nothing here needs a rewrite.

## Folders

| Folder | Purpose |
|---|---|
| `src/BurnSmith.Core/` | Engine, with `Menu/` for menus and `Disc/` for the BD-ROM structures (navigation commands, `MovieObject.bdmv`, `index.bdmv`, PGS) |
| `src/BurnSmith.App/`, `src/BurnSmith.Cli/` | The window, and the commands plus checks |
| `bdj/`, `assets/` | Menu xlet (the small Java program that runs the disc menu) and open-source artwork |
| `installer/`, `publish.ps1` | Release build |
| `tools/`, `dist/`, `analysis/` | Local only, not in git (except `tools/vlctest.ps1`) |

## Findings

### Must fix

1. **Choosing the wrong folder deletes your files** (`src/BurnSmith.Core/Tools.cs:70`)
   - **What this is:** Before a build, this code empties the output folder if it looks like an old disc.
   - **Problem:** One entry named `VIDEO_TS` or `BDMV` is enough. In my test, a folder holding `VIDEO_TS`, `precieux.txt` and `Films\autre.mkv` lost all of it, with no question asked.
   - **Fix:** Use `kind.All(...)` instead of `kind.Any(...)`, so any other file makes it refuse.
   - **If we skip it:** Someone picks `D:\Films` or a USB stick and loses data for good.

2. **Blu-ray sound stays at 44.1 kHz** (`src/BurnSmith.Core/BluRay.cs:497`)
   - **What this is:** Audio a Blu-ray can't carry is converted to AC-3.
   - **Problem:** The sample rate is never set. My AAC 44.1 kHz file came out as AC-3 44.1 kHz. The code's own comment at line 350 says a disc does not take that rate.
   - **Fix:** Add `$"-ar:a:{j}", "48000"` at line 497. Also store the sample rate in `AudioTrack`, and don't copy tracks that aren't 48 kHz.
   - **If we skip it:** Files with web-style audio may play silent or badly on a real player. I could not test a player.

3. **10-bit video is copied onto the disc** (`src/BurnSmith.Core/BluRay.cs:469`)
   - **What this is:** A video that already looks right for Blu-ray is copied without re-encoding.
   - **Problem:** Only codec, size and frame rate are checked. My 10-bit file reached the disc as `High 10`, and `check-disc` still said OK. From the code, a level 5.1 file would pass too.
   - **Fix:** Read `pix_fmt` in `Probe`, then copy only when it is `yuv420p`, the level is 41 or lower, and the profile is not a 10-bit one.
   - **If we skip it:** The disc looks fine on the PC, but hardware players are not expected to play it. I could not test one.

4. **Picture and sound drift apart on 24 fps sources** (`src/BurnSmith.Core/BluRay.cs:313`, `:483`)
   - **What this is:** One line picks the encoding rate, and another tells `tsMuxeR` the rate.
   - **Problem:** A re-encoded 24.000 fps video is made at 23.976, but declared as 24. Over 30 minutes the video ended 1.76 s before the sound, which is about 7 s on a 2-hour film. From the code, 50 fps also becomes 23.976 on Blu-ray, while the DVD path treats it as PAL.
   - **Fix:** Use one helper for both lines: `fps≈25 or 50 → 25`, `fps≈24 → 24`, otherwise `24000/1001`.
   - **If we skip it:** Voices get steadily further out of sync through the film.

5. **Cancel doesn't stop a build, and closing the window ignores running work** (`src/BurnSmith.App/MainWindow.xaml.cs:873`, `src/BurnSmith.Core/Tools.cs:85`, `src/BurnSmith.App/App.xaml.cs:76`; read, not run)
   - **What this is:** The Cancel button, app closing, and the end of an update.
   - **Problem:**
     - `Build` and `Tools.Run` receive no cancel token, so an encode keeps running until it ends.
     - There is no `Closing` handler, so closing mid-burn can ruin the disc and leave ffmpeg running.
     - An update download that finishes during a burn calls `Shutdown()` anyway.
   - **Fix:**
     - Pass the token through and call `token.Register(() => p.Kill(true))` in `Tools.Run`.
     - Ask for confirmation in `Closing` when `busy`.
     - Don't start the installer while `busy`.
   - **If we skip it:** Cancel looks broken, and discs get wasted.

6. **A `tools` folder in a parent directory beats the bundled programs** (`src/BurnSmith.Core/Tools.cs:15`, `:46`)
   - **What this is:** The search that finds `ffmpeg.exe` and the other tools.
   - **Problem:**
     - It climbs every parent folder before looking at the bundled copy. An installed app therefore checks `C:\tools` first.
     - Normal users can create that folder (checked with `icacls`), and it already exists on the PC used for this review.
     - In my test, a fake `ffprobe.exe` in a parent `tools` folder ran instead of the real one.
     - From the code, a locked subfolder there would also make the search fail.
   - **Fix:** Walk parent folders only in a development build (`BundledDir() == null`). In a release, look only in `BURNSMITH_TOOLS`, the folder next to the exe, and the bundled copy, then `PATH`.
   - **If we skip it:** Anyone who can write to `C:\` can run code as another user on a shared PC. Wrong ffmpeg versions also get used by accident.

7. **DVD mode encodes first, then says a tool is missing** (`src/BurnSmith.Core/Dvd.cs:82`, `:151`)
   - **What this is:** The released exe does not carry `dvdauthor` or `spumux`, but the window still offers DVD.
   - **Problem:** My run wrote the full `movie1.mpg` and only then failed on `dvdauthor.exe`. A missing `spumux` is worse: it logs "subtitles dropped" and carries on.
   - **Fix:** Call `Tools.Find` for both tools at the top of `Dvd.Build`. Also check free disk space there, and disable DVD in the window if the tools are missing.
   - **If we skip it:** Users of the release lose hours on a job that can't finish.

8. **Any unexpected error closes the whole app** (`src/BurnSmith.App/App.xaml.cs`, `src/BurnSmith.App/MainWindow.xaml.cs:248`; read, not run)
   - **What this is:** The app has no global error handler and no log file.
   - **Problem:** `Update()` reads `new FileInfo(t.Path).Length` on the UI thread. Unplug a USB source, change a setting, and the app dies with nothing recorded.
   - **Fix:** Add `DispatcherUnhandledException` to show the error and write it to a log, and check `File.Exists` in `Update()`.
   - **If we skip it:** The project is lost, and you get bug reports you can't diagnose.

9. **The updater can run an unchecked installer as admin** (`src/BurnSmith.Core/Updater.cs:69`; read, not run)
   - **What this is:** The in-app update checks a SHA-256 hash, then runs the installer.
   - **Problem:** If GitHub sends no digest, the check is skipped. The hash also comes from the same place as the file, and the installer is unsigned, so a stolen GitHub account could push malware to every user.
   - **Fix:** Now: throw if the hash is missing. Before wide release: sign with Authenticode and verify the signature before running.
   - **If we skip it:** One stolen account becomes admin-level code on users' PCs, after a click and a UAC prompt.

### Should fix

10. **"Fit to disc" forgets the intro video** (`src/BurnSmith.Core/BluRay.cs:129`, `:143`; read, not run)
    - **What this is:** It works out the bit rate that fills the disc.
    - **Problem:** The intro is removed from the list before that sum. The DVD code counts it (`src/BurnSmith.Core/Dvd.cs:49`).
    - **Fix:** Add the intro's duration and audio to `TargetKbps`.
    - **If we skip it:** A long intro can overflow the disc, and you only find out when burning.

11. **Failed builds leave the work folder behind** (`src/BurnSmith.Core/BluRay.cs:214`, `src/BurnSmith.Core/Dvd.cs:84`; read, not run)
    - **What this is:** `<output>_work` holds the temporary video files.
    - **Problem:** It is deleted only on success, so each failure can leave many GB. An old thumbnail can also be reused later.
    - **Fix:** Wrap the build in `try/finally` and call `TryDelete(work)`.
    - **If we skip it:** The disk fills up without the user knowing why.

12. **A damaged `.sup` subtitle fails the whole DVD build** (`src/BurnSmith.Core/Disc/PgsStream.cs`, `src/BurnSmith.Core/PgsSubtitles.cs:219`; read, not run)
    - **What this is:** `Unpack` decodes PGS subtitle pictures.
    - **Problem:** Cut-off data throws `IndexOutOfRangeException`, but only `InvalidDataException` is caught. The promise to "leave out" bad pictures fails, and after the movie is encoded.
    - **Fix:** Check `i` against the data length and throw `InvalidDataException`.
    - **If we skip it:** One bad subtitle costs the whole encode.

13. **The checks don't catch wrong video or sound, and nothing runs them automatically** (`src/BurnSmith.Cli/Program.cs`, no `.github/`)
    - **What this is:** The tests are `check-*` commands inside the CLI. All pass at commit `627e3cd`: `check-update` 5, `check-edit` 51 (50 without `dvdauthor`), `check-elements` 15, `check-highlights` 11, `check-templates` 144: all pass.
    - **Problem:** `check-disc` passed my disc with 10-bit video. It looks at structure, not at what a player needs. There is no CI (automatic runs on each commit), so nothing runs these on their own.
    - **Fix:** Add one `check-bd` command that builds short clips and asserts 48 kHz audio, 8-bit video, level 41 or lower, and matching video and audio lengths. Run it in a GitHub Action.
    - **If we skip it:** Findings 2 to 4 can come back with nobody noticing.

14. **The window can freeze on the first start** (`src/BurnSmith.App/MainWindow.xaml.cs:84`; read, not run)
    - **What this is:** A field in `MainWindow` calls `TemplateCatalog.Default()` while the window is being built.
    - **Problem:** That scan waits for the same lock the unpacking holds, so the UI thread waits for the tools and templates to unpack. In a dev build, with nothing to unpack, the scan and process start took 1.3 s. I did not time a release.
    - **Fix:** Start with `new MenuDesign()`, and load the default template in the background.
    - **If we skip it:** A new user's first impression is a window that doesn't respond.

15. **Releases depend on whatever is on the PC** (`publish.ps1:9`, `src/BurnSmith.Core/Menu/TemplateCatalog.cs:145`; read, not run)
    - **What this is:** The script that builds a release.
    - **Problem:** It ships the first `ffmpeg` on `PATH`, while the notices promise version 8.1.1. On a PC without the vendor apps, the templates are left out with no error.
    - **Fix:** Fail if the ffmpeg version or hash isn't the expected one, and fail if `pack-templates` returns 0.
    - **If we skip it:** A release can break the licence notice or ship with no templates.

### Nice to have

16. **Small fixes** (all read, not run)
    - DVD video bit rate ignores the audio, so 5 AC-3 tracks can pass the 10.08 Mb/s limit (`src/BurnSmith.Core/Dvd.cs:24`).
    - Settings pages multiply, for example 3 audio × 11 subtitle choices = 33 encodes (`src/BurnSmith.Core/Dvd.cs:216`).
    - Update installers stay in `%TEMP%` forever (`src/BurnSmith.Core/Updater.cs:51`).
    - Non-UTF-8 Russian or Arabic subtitles are read as CP1252 and come out garbled (`src/BurnSmith.Core/BluRay.cs:552`).
    - `Tools.cache` is not thread-safe (`src/BurnSmith.Core/Tools.cs:9`).
    - Verification accepts a disc up to 1 MiB short (`src/BurnSmith.Core/Imapi.cs:245`).
    - Some comments are stale or doubled (`src/BurnSmith.App/MainWindow.xaml.cs:42`, `:220`, `:566`).

A few smaller points were left out.

## Code quality, errors and security

- **Good:** `ArgumentList` with no shell closes off command injection, and `shortcut:` comments are honest. The unpack that renames into place and the ISO and disc checks before burning are also solid.
- **Weak:** the code often catches one narrow exception type and misses the others. There is also no way to save a project, so a crash loses all the setup.
- **Better approaches:** add one preflight step before encoding, covering tools, disk space, disc size and source files. Keep `Program.cs` as commands and move the checks to a test project when you add CI.

## Deployment guide

1. Set `<Version>` in `src/BurnSmith.App/BurnSmith.App.csproj`.
2. Run the checks:
   ```bash
   dotnet build BurnSmith.slnx -c Release
   ```
   ```bash
   dotnet run --project src/BurnSmith.Cli -- check-edit
   ```
   ```bash
   dotnet run --project src/BurnSmith.Cli -- check-templates
   ```
3. Run `.\publish.ps1`. It needs ffmpeg on `PATH`, `tools\tsmuxer\bin\tsMuxeR.exe` and Inno Setup 6.
4. On GitHub, tag the release `vX.Y.Z`, matching the exe version exactly. Upload `BurnSmith-Setup-X.Y.Z.exe`, since the updater looks for that name.
5. Before a public release:
   - Test install, uninstall and a real update on a clean PC.
   - Sign the binaries.
   - Burn a disc and play it on a real player.

## Conclusion

**Verdict:** the design is sound, but it is not ready for wide release. Fix finding 1 first because it destroys data, then 2 to 4, which make bad discs while the checks still pass.

**Not checked:**

- Real burning and read-back, BD-J menus on hardware players, and the installer and updates. There was no burner or player here.
- `Menu.java`, `InstalledTemplate.cs`, the `Disc/` folder and the editor window were skimmed rather than reviewed in full.
- Uncommitted edits another session was making during the review.

**Risk outside the code:** shipping template artwork gathered on a publishing PC in public releases is a legal risk. It was already decided by the project owner, so this review did not reopen it.
