# Builds the portable BurnSmith.exe (self-contained, one file, no install) into .\dist, with ffmpeg, ffprobe and tsMuxeR inside.
# The tools are not in this repository: they are taken from this PC (a folder with ffmpeg.exe + ffprobe.exe, and tsMuxeR.exe) and zipped
# into obj\payload\tools.zip, which the BurnSmith.Payload assembly embeds. Licences: see THIRD-PARTY-NOTICES.md (shipped in the zip).
param(
    [string]$FfmpegDir = (Split-Path (Get-Command ffmpeg -ErrorAction Stop).Source),
    [string]$TsMuxer = "$PSScriptRoot\tools\tsmuxer\bin\tsMuxeR.exe"
)
$root = $PSScriptRoot
foreach ($f in "$FfmpegDir\ffmpeg.exe", "$FfmpegDir\ffprobe.exe", $TsMuxer) { if (-not (Test-Path $f)) { throw "missing: $f" } }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = "$root\obj\payload"
New-Item -ItemType Directory -Force $payload | Out-Null
$zip = "$payload\tools.zip"
if (Test-Path $zip) { [IO.File]::Delete($zip) }
$z = [IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($e in @(@("$FfmpegDir\ffmpeg.exe", 'ffmpeg.exe'), @("$FfmpegDir\ffprobe.exe", 'ffprobe.exe'), @($TsMuxer, 'tsMuxeR.exe'), @("$root\THIRD-PARTY-NOTICES.md", 'THIRD-PARTY-NOTICES.md'))) {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $e[0], $e[1], 'Optimal')
}
$z.Dispose()
"payload: {0:N0} MB" -f ((Get-Item $zip).Length / 1MB)

dotnet publish "$root\src\BurnSmith.App" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-Item "$root\dist\BurnSmith.exe" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
