# Third-party software

The released `BurnSmith.exe` carries the programs below as separate executables (unpacked on first run into
`%LOCALAPPDATA%\BurnSmith\tools`). They are independent programs run through their command line, under their own licences.

| Program | Licence | Source |
|---|---|---|
| FFmpeg (`ffmpeg.exe`, `ffprobe.exe`), 8.1.1 "full" build by gyan.dev, built `--enable-gpl --enable-version3` (includes x264, x265 and other GPL libraries) | GNU GPL v3 or later (the libraries it links carry their own licences) | https://ffmpeg.org/ - build and its configuration: https://www.gyan.dev/ffmpeg/builds/ |
| tsMuxeR 2.7.0 | Apache License 2.0 | https://github.com/justdan96/tsMuxer |

The corresponding source code of the GPL programs is available from the addresses above; a copy can also be requested from the
BurnSmith repository (https://github.com/Balrog57/BurnSmith, "Issues").

Disc images and burning use the Windows components IMAPI2 / IMAPI2FS (part of Windows, not shipped).

Ideas and small pieces of approach were taken from open projects, read on GitHub: futureburn (MIT, sp00nznet) for the IMAPI2 / UDF handling,
and bdmvauthor (Apache-2.0, blenheimears) and DVD Forge (GPL-3.0, Nicolasalucin) as references for disc authoring.

BurnSmith does not contain, copy or redistribute menu templates of DVDFab or Leawo; it reads the ones already installed on the user's PC.
BurnSmith is independent of, and not affiliated with, DVDFab or Leawo.
