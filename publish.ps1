# Builds BurnSmith.exe (self-contained, one file, portable) into .\dist, with ffmpeg, ffprobe and tsMuxeR inside, then, when Inno Setup 6 is
# installed, the installer .\dist\BurnSmith-Setup-<version>.exe (Program Files; this is the file the in-app update downloads from a GitHub release).
# The tools are not in this repository: they are taken from this PC (a folder with ffmpeg.exe + ffprobe.exe, and tsMuxeR.exe) and zipped
# into obj\payload\tools.zip, which the BurnSmith.Payload assembly embeds. Licences: see THIRD-PARTY-NOTICES.md (shipped in the zip).
# ffmpeg and ffprobe must be the build that notice promises (the hashes below): another one, or none of the templates below, stops the script.
# Likewise the pictures and layout files of the DVDFab / Leawo / Yuhan menu templates found on this PC (no fonts, no programs) go into
# obj\payload\templates.zip. The first time a build runs, the exe unpacks the tools into BurnSmith-data next to itself (or %LOCALAPPDATA%\BurnSmith\files
# when installed) and the template pictures into Documents\BurnSmith\Templates.
param(
    [string]$FfmpegDir = (Split-Path (Get-Command ffmpeg -ErrorAction Stop).Source),
    [string]$TsMuxer = "$PSScriptRoot\tools\tsmuxer\bin\tsMuxeR.exe"
)
$ErrorActionPreference = 'Stop' # a missing licence file must fail the build, not ship without it
$root = $PSScriptRoot
$pins = @{ # FFmpeg 8.1.1 "full" by gyan.dev. A new build: change these and docs\THIRD-PARTY-NOTICES.md together
    'ffmpeg.exe'  = '09948D4CDD0650DA6FF5A87577469F2A218DC2615AE379F8F734D24C49DE0F73'
    'ffprobe.exe' = 'A6618E99BB58869DED3C6F37B53AA1A8D701C3591DBB7B5B317D47369C112BE2'
}
foreach ($tool in $pins.Keys) {
    $f = "$FfmpegDir\$tool"
    if (-not (Test-Path $f)) { throw "missing: $f" }
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash
    if ($hash -ne $pins[$tool]) { throw "$f is not the pinned build: sha256 $hash, expected $($pins[$tool]) (FFmpeg 8.1.1 full by gyan.dev, see docs\THIRD-PARTY-NOTICES.md)" }
    $version = & $f -version | Select-Object -First 1
    if ($version -notmatch ' version 8\.1\.1-full_build-www\.gyan\.dev ') { throw "$f says '$version', not 8.1.1-full_build-www.gyan.dev" }
}
if (-not (Test-Path $TsMuxer)) { throw "missing: $TsMuxer" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = "$root\obj\payload"
New-Item -ItemType Directory -Force $payload | Out-Null

$tz = "$payload\templates.zip" # before the tools, whose zip takes minutes
dotnet run --project "$root\src\BurnSmith.Cli" -c Release -- pack-templates $tz
if ($LASTEXITCODE -ne 0) { if (Test-Path $tz) { [IO.File]::Delete($tz) }; throw "pack-templates failed: no release without the templates" }
"templates: {0:N0} MB" -f ((Get-Item $tz).Length / 1MB)

$zip = "$payload\tools.zip"
if (Test-Path $zip) { [IO.File]::Delete($zip) }
$z = [IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($e in @(@("$FfmpegDir\ffmpeg.exe", 'ffmpeg.exe'), @("$FfmpegDir\ffprobe.exe", 'ffprobe.exe'), @($TsMuxer, 'tsMuxeR.exe'), @("$root\docs\THIRD-PARTY-NOTICES.md", 'THIRD-PARTY-NOTICES.md'))) {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $e[0], $e[1], 'Optimal')
}
$z.Dispose()
"payload: {0:N0} MB" -f ((Get-Item $zip).Length / 1MB)

dotnet publish "$root\src\BurnSmith.App" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-Item "$root\dist\BurnSmith.exe" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }

# the installer: its version is the exe's (<Version> in BurnSmith.App.csproj), and the GitHub release tag must be v<that version>
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Warning "Inno Setup 6 not found (winget install JRSoftware.InnoSetup): only the portable exe was built"; return }
$version = ((Get-Item "$root\dist\BurnSmith.exe").VersionInfo.ProductVersion -split '\+')[0]
& $iscc "/DAppVersion=$version" "$root\installer\BurnSmith.iss"
if ($LASTEXITCODE -ne 0) { throw "installer failed" }
Get-Item "$root\dist\BurnSmith-Setup-$version.exe" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
