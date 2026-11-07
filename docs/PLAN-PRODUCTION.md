# BurnSmith: verified facts and production plan (2026-11-05)

Sources: five reports written on 2026-11-05, checked against the repository, GitHub and test runs. Only the code review is kept: `docs/RAPPORT-SESSION-2026-11-05.md` (finding numbers below refer to it). This document replaces the other four.

Only two labels are used. **Checked** means seen today in the code, git, GitHub or a test run. **Reported** means a report says so and it could not be re-checked; treat it as a lead, not as proof.

## 1. Facts

### Project state (checked)

| Fact | Value |
|---|---|
| Published release | v0.5.1 is Latest, tag at `d5d619b`, with `BurnSmith-Setup-0.5.1.exe` (356,216,337 bytes) and `BurnSmith.exe`, both with a GitHub SHA-256 digest |
| `main` | `a567dd3`, 5 commits ahead of `origin/main` (`d5d619b`), not pushed |
| Version in the code | 0.5.1, the same number as the release although the code is newer |
| Installed on Marc's PC | 0.5.0 in `C:\Program Files\BurnSmith` |
| Local build | `dist\BurnSmith-Setup-0.5.1.exe` from `627e3cd`, not the same file as the GitHub one, unsigned, not installed |
| Code size | about 10,900 lines of C#, about 7,500 of them in `BurnSmith.Core` |
| Tests | No test project, no CI (`.github/` absent). Checks are CLI commands. |
| Signing | No signing step in `publish.ps1`, `installer/BurnSmith.iss` or `bdj/build.ps1` |
| `shortcut:` comments | 7 |

### Branches not merged in `main` (checked)

| Branch | Content | Files it changes compared to `main` |
|---|---|---|
| `langue-des-pistes` | Track language: 2 commits | `MainWindow.xaml.cs`, `Program.cs`, `Tracks.cs` |
| `fond-par-page` | Background per page: 2 commits, base older than `main` | `MenuEditorWindow.xaml(.cs)`, `Program.cs`, `MenuDesign.cs`, `MenuRenderer.cs` |
| menu editor branch | menu editor: **no commit, all uncommitted** in its worktree, about 800 added lines | `App.xaml.cs`, `MenuEditorWindow.xaml(.cs)`, `Program.cs`, `ElementLibrary.cs`, `MenuElement.cs`, `MenuLayout.cs` |
| `claude/hdmv-menu-csharp-c5f4bb` | Native HDMV menu worktree | On hold (see phase 6); contents not reviewed |

### Check results at `627e3cd` (checked; all exit 0)

`check-update` 5, `check-edit` 51 (50 without `dvdauthor`), `check-elements` 15, `check-highlights` 11, `check-templates` 144: all passed, build with 0 warnings and 0 errors.

These prove that discs and menus build with the right structure. They do not prove that a disc plays on hardware or looks like the original template.

### Defects (finding numbers from the review)

| # | Defect | Evidence |
|---|---|---|
| 1 | `Tools.PrepareOutput` deletes everything in a folder that contains one `VIDEO_TS` or `BDMV` entry | Run: other files were deleted |
| 2 | Blu-ray: 44.1 kHz sources become 44.1 kHz AC-3 | Run: `ffprobe` on the m2ts |
| 3 | Blu-ray: 10-bit H.264 is copied unchanged and `check-disc` passes | Run: `ffprobe` shows `High 10` |
| 4 | Blu-ray: a re-encoded 24.000 fps source drifts, video ended 1.76 s before the audio in 30 min | Run: packet times |
| 5 | Cancel only acts between steps (no token in `Build` or `Tools.Run`), no `Closing` handler, update can shut down mid-burn | Code |
| 6 | Parent `tools` folders are searched before the bundled tools | Run: fake `ffprobe.exe` was used. `C:\tools` exists here and users can create it (`icacls`) |
| 7 | DVD without `dvdauthor`: encodes first, then fails. Without `spumux`: logs "subtitles dropped" and continues | Run |
| 8 | No global exception handler and no log file; `Update()` reads the size of each source on the UI thread | Code |
| 9 | Updater runs the installer unchecked if GitHub gives no digest; digest and file come from the same source; installer unsigned | Code |
| 10 | Blu-ray "fit to disc" does not count the intro; the DVD path does | Code |
| 11 | `<output>_work` is deleted only on success | Code |
| 12 | Truncated PGS data throws `IndexOutOfRangeException`, only `InvalidDataException` is caught | Code |
| 13 | `check-disc` passes findings 2 and 3; no CI | Run |
| 14 | `MainWindow` runs the template scan on the UI thread, which waits for the same locks as the unpacking | Code; duration not measured on a release |
| 15 | `publish.ps1` ships the first ffmpeg on `PATH`; templates can be left out silently | Code |
| 16 | Small list in the review | Code |

### Not done or never tried

- **Checked as absent from the repo:** a test for the Settings page after a DVD intro, or for an NTSC intro on a PAL film.
- **Reported as never tried:** real burning, a living-room player, uninstall, a real update from an installed version, the mouse in the new editor, the WPF window in the `fond-par-page` and `langue-des-pistes` work.
- **Reported, not re-checked:**
  - DVD Settings page after an intro and NTSC intro on PAL film were checked by hand in VLC.
  - Installed 0.5.0 starts and works.
  - `check-elements` reaches 31 on the menu editor branch and 19 on `fond-par-page`; `check-edit` reaches 54 on `langue-des-pistes`.
  - `check-templates` lists overlapping buttons in some carried templates as "to review".
  - Unsigned BD-J jars are refused by some players.
- **Features reported as missing:** video and music per page, cut slider, built-in preview, button sounds, configurable Bonus page, pop-up menu, undo in the editor, SVG buttons, template opened as editable elements, UHD re-encoding, native HDMV menu.

### Claims removed because they are false or out of date

| Claim in a report | Reality |
|---|---|
| A wrong output folder choice destroys nothing | False, finding 1 |
| Cancel gets out of a stuck ffmpeg | False, it acts only after the step ends |
| Tool versions are fixed by the payload | Only inside one build; `publish.ps1` takes what is on the PC (finding 15) |
| Temporary folders are cleaned up | Only on success (finding 11) |
| DVD fails cleanly when tools are missing | It fails after encoding (finding 7) |
| Last release is 0.5.0 | It is 0.5.1 |
| `check-templates` is 114/114 | It is 144 |
| `main` has 4 CA2013 warnings | The build at `627e3cd` has 0 warnings |
| "46 discs without a false positive" | Cannot be checked: removed |

## 2. Plan

### Rules for every phase

- **Writers:** one worktree and branch per phase. Two sessions never change the same files at the same time.
- **Done means:** build with 0 warnings; all checks in section 1 still pass; the defect is reproduced before the fix and gone after it, with a check that fails if it returns.
- **Release builds:** always from `git archive <sha>`, never from a working tree with other sessions' edits.
- **Never without Marc's explicit OK:** push, tag, GitHub release, installing on his PC, committing another session's uncommitted work, deleting a branch or worktree.
- **Not in this plan:** the vendor artwork decision (recorded and final) and the features listed above.

### Phases

| Phase | Work | Findings | Main files | Depends on |
|---|---|---|---|---|
| 1. Merge | Merge `langue-des-pistes`, then `fond-par-page`, into `main`. Rebuild and rerun all checks. Marc decides on the menu editor branch. | none | `Program.cs`, `MainWindow.xaml.cs`, `Tracks.cs`, menu files | Marc's OK to merge |
| 2. Discs and data | Safe `PrepareOutput`, 48 kHz, copy only compatible video, one frame-rate helper, DVD preflight, intro in fit-to-disc, work folder cleanup, PGS decoding. New `check-bd`. | 1, 2, 3, 4, 7, 10, 11, 12, 13 (part) | `Tools.cs` (`PrepareOutput`), `BluRay.cs`, `Dvd.cs`, `Tracks.cs`, `Disc/PgsStream.cs`, `Cli/` | Phase 1 |
| 3. App reliability | Real Cancel with process kill, `Closing` guard, no update during work, global handler with log file, UI start not blocked. | 5, 8, 14 | `Tools.Run`, `BluRay.Build`, `Dvd.Build`, `MainWindow`, `App.xaml.cs` | Phase 2 (same files) |
| 4. Release safety | Bundled tools first in a release, refuse a missing digest, clean old installers, pin ffmpeg in `publish.ps1`, fail on empty templates. | 6, 9 (digest), 15, 16 (part) | `Tools.Find`, `Updater.cs`, `publish.ps1` | Phase 1; can run beside phase 3 if `Tools.cs` edits are merged in turn |
| 5. CI | GitHub Action on Windows: build and `check-*`. Add the hand checks to `check-edit`. | 13 | `.github/`, `Cli/` | Phase 2 |
| 6. Release and hardware | Version 0.5.2, build from `git archive`, install, real update 0.5.0 → 0.5.2, uninstall. Burn a DVD-RW and play it on a player. | 9 (signing is Marc's choice) | none | Phases 2 to 5 and Marc for each step |

The native HDMV menu stays on hold until Marc has the DVD-RW result from phase 6.

### Decisions only Marc can make

1. Phase 1: is the menu editor branch committed and merged now, or left out of 0.5.2? It has not been tried with the mouse and is expected to conflict with `fond-par-page`.
2. Phase 6: go-ahead to push and publish, and whether to buy a code-signing certificate.
3. Phase 6: he runs the UAC prompts, the burn and the player test.
