# Third-party software and code

BurnSmith is free software under the GNU General Public License v3 or later (see `LICENSE`).

## Programs carried by the released `BurnSmith.exe`

They are separate executables, unpacked on first run into `%LOCALAPPDATA%\BurnSmith\tools` and run through their command line.

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

## Menu templates

BurnSmith does not contain, copy or redistribute menu templates of DVDFab or Leawo; it reads the ones already installed on the user's PC.
BurnSmith is independent of, and not affiliated with, DVDFab or Leawo.
