# Third-party software and code

BurnSmith is free software under the GNU General Public License v3 or later (see `LICENSE`).

## Programs carried by the released `BurnSmith.exe`

They are separate executables, unpacked on first run into the `BurnSmith-data\tools` folder next to the exe (or `%LOCALAPPDATA%\BurnSmith\files\tools` when that folder cannot be written) and run through their command line.

| Program | Licence | Source |
|---|---|---|
| FFmpeg (`ffmpeg.exe`, `ffprobe.exe`), 8.1.1 "full" build by gyan.dev, built `--enable-gpl --enable-version3` (includes x264, x265 and other GPL libraries) | GNU GPL v3 or later (the libraries it links carry their own licences) | https://ffmpeg.org/ - the build and its configuration: https://www.gyan.dev/ffmpeg/builds/ |
| tsMuxeR 2.7.0 | Apache License 2.0 | https://github.com/justdan96/tsMuxer |

The corresponding source code of the GPL programs is available from the addresses above.

Disc images and burning use the Windows components IMAPI2 / IMAPI2FS (part of Windows, not shipped).

## Code adapted from other projects

`src/BurnSmith.Core/Imapi.cs` follows the approach of **futureburn** (https://github.com/sp00nznet/futureburn) for the IMAPI2 / IMAPI2FS
calls (late-bound COM, ISO to a drive, UDF image from a folder). Its licence:

> MIT License
>
> Copyright (c) 2026 sp00nz
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
> "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish,
> distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to
> the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
> MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR
> ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
> SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Menu templates and pictures carried by the released `BurnSmith.exe`

Stored as data resources, unpacked on first use into `BurnSmith-data\open` next to the exe; they are read, not linked, by BurnSmith.

| Material | Licence | Source |
|---|---|---|
| **DVDStyler** menu templates (`.dvdt`), their preview pictures and the background pictures, unchanged (`assets/library/dvdstyler`, with upstream `COPYING`, `AUTHORS` and `SOURCE.txt`) | GNU General Public License as published with DVDStyler (`COPYING` is the version 2 text; DVDStyler's README says "GNU General Public License (GPL)") | https://github.com/ntxalex/DVDStyler (mirror of https://www.dvdstyler.org), author Alex Thuering |
| **DVD Forge** template parameters (grid, colours, font, margins of Cinema Dark, Multi-Page TV Grid, Classic Criterion, Retro CRT, Minimalist), re-typed in `DvdForgeTemplates.cs`; no code, no picture | GNU GPL v3 | https://github.com/Nicolasalucin/dvd-forge (`crates/dvdforge-menu/src/templates.rs`) |

The upstream project does not itemise who made each DVDStyler picture; they travel under the project's licence as published. These files are separate data
aggregated in the same executable, not linked into BurnSmith's code, so BurnSmith's own code stays GPL v3 or later. If you redistribute the exe, keep this file and the
`COPYING` / `AUTHORS` / `SOURCE.txt` of `assets/library/dvdstyler`, which are the corresponding source of the DVDStyler data. DVDStyler's README and source
headers (for example `src/MainWin.cpp`) say only "GPL", without a version number; its `COPYING` is the version 2 text, whose section 9 lets the recipient choose any version
published by the Free Software Foundation, so v3 is available. This is our reading, not legal advice.

## Menu templates of other software (pictures and layout files only)

The released `BurnSmith.exe` carries the **pictures and layout files** (PNG / JPEG / XML) of the menu templates of **DVDFab** and **Leawo Prof. Media**, copied
from the PC that built the exe (`publish.ps1` runs `BurnSmith.Cli pack-templates`) and unpacked to `BurnSmith-data\templates` next to it. They are **not part of
BurnSmith's GPL-licensed code**, **they are not in this repository** and they belong to their owners; the exe includes them because the project owner decided
to reproduce the template artwork. **No program, code, interface or font of either product is included.** A template installed on the user's PC is read from
there and wins over the carried copy of the same name. If an owner objects to the artwork travelling in the exe, it can be left out by publishing from a PC
without those programs (the exe then simply offers the free templates), and the released file replaced.
BurnSmith is independent of, and not affiliated with, DVDFab or Leawo.
