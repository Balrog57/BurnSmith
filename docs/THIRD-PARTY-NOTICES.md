# Third-party software and code

BurnSmith is free software under the GNU General Public License v3 or later (the `LICENSE` file, in the installation folder and at the root of the repository).

## Programs carried by the released `BurnSmith.exe`

They are separate executables, unpacked on first run into `%LOCALAPPDATA%\BurnSmith\files\tools` and run through their command line.

| Program | Licence | Source |
|---|---|---|
| FFmpeg (`ffmpeg.exe`, `ffprobe.exe`), 8.1.1 "full" build by gyan.dev, built `--enable-gpl --enable-version3` (includes x264, x265 and other GPL libraries) | GNU GPL v3 or later (the libraries it links carry their own licences) | https://ffmpeg.org/ - the build and its configuration: https://www.gyan.dev/ffmpeg/builds/ |
| tsMuxeR 2.7.0 | Apache License 2.0 | https://github.com/justdan96/tsMuxer |

The corresponding source code of the GPL programs is available from the addresses above.

The exe is a self-contained .NET program, so it also includes the **.NET runtime** and **Windows Desktop runtime (WPF)**: MIT License, Copyright (c) .NET Foundation and Contributors, https://github.com/dotnet/runtime and https://github.com/dotnet/wpf. The licence and notices files of those runtime packages are in the installation folder: `dotnet-runtime-LICENSE.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt` and `windowsdesktop-runtime-LICENSE.txt`. The installer `BurnSmith-Setup-<version>.exe` is built with **Inno Setup**: Inno Setup License, Copyright (C) 1997-2026 Jordan Russell, portions Copyright (C) 2000-2026 Martijn Laan, https://jrsoftware.org/isinfo.php.

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

`src/BurnSmith.Core/Hdmv/` (`Pgs.cs`, `IgStream.cs`, `TransportStream.cs`, `ClipFiles.cs`, `HdmvNav.cs`, `MenuClip.cs`, `HdmvAuthor.cs`) is a C# port of
**bdmvkit** by kajisho5 (Shogo Kajiyama), https://github.com/kajisho5/bdmvkit, commit 5789076 (v0.7.0): the Interactive Graphics (HDMV menu) stream
writer, the HDMV navigation commands, CLPI / MPLS / MovieObject.bdmv / index.bdmv writers and the PMT retyping. bdmvkit is licensed under the
**GNU Lesser General Public License v3.0 or later**; the port is redistributed under the GNU GPL v3 or later, by removing the LGPL's additional permissions, as section 7 of the GPL v3 (which the LGPL v3 incorporates) allows.
Every ported file keeps bdmvkit's authorship and licence in its header, and the options added on top of the port (dated segments, UO mask,
median-cut palette) are documented in `src/BurnSmith.Core/Hdmv/README.md`. `src/BurnSmith.HdmvCheck/` is BurnSmith's own check program and is not
part of the released `BurnSmith.exe`.

## Menu templates and pictures carried by the released `BurnSmith.exe`

Stored as data resources, unpacked on first use into `Documents\BurnSmith\Templates\open`; they are read, not linked, by BurnSmith.

| Material | Licence | Source |
|---|---|---|
| **DVDStyler** menu templates (`.dvdt`), their preview pictures and the background pictures, unchanged (`assets/library/dvdstyler`, with upstream `COPYING`, `AUTHORS` and `SOURCE.txt`) | GNU General Public License as published with DVDStyler (`COPYING` is the version 2 text; DVDStyler's README says "GNU General Public License (GPL)") | https://github.com/ntxalex/DVDStyler (mirror of https://www.dvdstyler.org), author Alex Thuering |
| **DVD Forge** template parameters (grid, colours, font, margins of Cinema Dark, Multi-Page TV Series Grid, Classic Criterion, Retro CRT, Minimalist), re-typed in `DvdForgeTemplates.cs`; no code, no picture | GNU GPL v3 | https://github.com/Nicolasalucin/dvd-forge (`crates/dvdforge-menu/src/templates.rs`) |

The upstream project does not itemise who made each DVDStyler picture; they travel under the project's licence as published. These files are separate data
aggregated in the same executable, not linked into BurnSmith's code, so BurnSmith's own code stays GPL v3 or later. If you redistribute the exe, keep this file and the
`COPYING` / `AUTHORS` / `SOURCE.txt` of `assets/library/dvdstyler`, which are the corresponding source of the DVDStyler data. DVDStyler's README and source
headers (for example `src/MainWin.cpp`) say only "GPL", without a version number; its `COPYING` is the version 2 text, whose section 9 lets the recipient choose any version
published by the Free Software Foundation, so v3 is available. This is our reading, not legal advice.

## Menu templates of other software (pictures and layout files only)

The released `BurnSmith.exe` carries the **pictures and layout files** (PNG / JPEG / XML) of the menu templates of **DVDFab**, **Leawo Prof. Media** and **Yuhan Blu-ray DVD Creator**, copied
from the PC that built the exe (`publish.ps1` runs `BurnSmith.Cli pack-templates`) and unpacked to `Documents\BurnSmith\Templates\carried`. They are **not part of
BurnSmith's GPL-licensed code**, **they are not in this repository** and they belong to their owners; the exe includes them because the project owner decided
to reproduce the template artwork. **No program, code, interface or font of any of these products is included.** A template installed on the user's PC is read from
there and wins over the carried copy of the same name. If an owner objects to the artwork travelling in the exe, it can be left out by changing `publish.ps1`,
which otherwise refuses to build without them, and replacing the released file; the exe then simply offers the free templates.
Yuhan keeps the layout of each theme in an encrypted `config.dat` (the key is in its own program file): BurnSmith reads it with the key of the user's installation at run time, and the exe
carries the layout as plain `config.xml` next to the 1920x1080 pictures (no other resolution, no program, no code).
BurnSmith is independent of, and not affiliated with, DVDFab, Leawo or Yuhan.
