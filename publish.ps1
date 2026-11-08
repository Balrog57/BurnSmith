# Builds the installer .\dist\BurnSmith-Setup-<version>.exe, the only file of a GitHub release and the one the in-app update downloads (Program Files; Inno Setup 6 is required).
# It wraps .\dist\BurnSmith.exe, an intermediate step: self-contained, one file, with tsMuxeR inside. The installer itself carries ffmpeg and ffprobe
# (installer\ffmpeg.iss) and ImgBurn's own setup (installer\imgburn.iss), taken from this PC once their SHA-256 matches: nothing is downloaded at install.
# tsMuxeR is not in this repository: it is taken from this PC and zipped
# into obj\payload\tools.zip, which the BurnSmith.Payload assembly embeds, with tsMuxeR's Apache-2.0 licence.
# None of the templates below stops the script.
# Likewise the pictures and layout files of the menu templates found on this PC (no fonts, no programs) go into
# obj\payload\templates.zip. The first time it runs, the installed exe unpacks the tools into %LOCALAPPDATA%\BurnSmith\files
# and the template pictures into Documents\BurnSmith\Templates.
param(
    [string]$TsMuxer = "$PSScriptRoot\tools\tsmuxer\bin\tsMuxeR.exe",
    # the folder of ffmpeg.exe and ffprobe.exe: tools\ffmpeg as CI fills it, else the one on the PATH (winget install Gyan.FFmpeg --version 8.1.1)
    [string]$Ffmpeg = $(if (Test-Path "$PSScriptRoot\tools\ffmpeg\ffmpeg.exe") { "$PSScriptRoot\tools\ffmpeg" } elseif (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue) { Split-Path (Get-Command ffmpeg.exe).Source } else { "$PSScriptRoot\tools\ffmpeg" }),
    # from https://download.imgburn.com/SetupImgBurn_2.5.8.0.exe
    [string]$ImgBurnSetup = "$PSScriptRoot\tools\imgburn-setup\SetupImgBurn_2.5.8.0.exe"
)
$ErrorActionPreference = 'Stop' # a missing licence file must fail the build, not ship without it
$root = $PSScriptRoot
if (-not (Test-Path $TsMuxer)) { throw "missing: $TsMuxer" }
# the setup carries exactly what was tested: the ffmpeg build CI pins (.github\workflows\ci.yml), ImgBurn's setup as Chocolatey's moderated package checks it
$pins = @{
    "$Ffmpeg\ffmpeg.exe" = '09948D4CDD0650DA6FF5A87577469F2A218DC2615AE379F8F734D24C49DE0F73'
    "$Ffmpeg\ffprobe.exe" = 'A6618E99BB58869DED3C6F37B53AA1A8D701C3591DBB7B5B317D47369C112BE2'
    $ImgBurnSetup = '49AA06EAFFE431F05687109FEE25F66781ABBE1108F3F8CA78C79BDEC8753420'
}
foreach ($f in $pins.Keys) {
    if (-not (Test-Path -LiteralPath $f)) { throw "missing: $f" }
    if ((Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash -ne $pins[$f]) { throw "$f is not the pinned build" }
}

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
foreach ($e in @(@($TsMuxer, 'tsMuxeR.exe'), @("$root\installer\tsMuxeR-LICENSE.txt", 'tsMuxeR-LICENSE.txt'))) {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $e[0], $e[1], 'Optimal')
}
$z.Dispose()
"payload: {0:N0} MB" -f ((Get-Item $zip).Length / 1MB)

dotnet publish "$root\src\BurnSmith.App" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-Item "$root\dist\BurnSmith.exe" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }

# the licences of the .NET runtime inside the exe, from the very packs this publish used (its restore records them, and honours NUGET_PACKAGES); not in git, the installer installs them
$assets = Get-Content "$root\src\BurnSmith.App\obj\project.assets.json" -Raw | ConvertFrom-Json
$packs = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies
foreach ($l in @(
        @('Microsoft.NETCore.App.Runtime.win-x64', 'LICENSE.TXT', 'dotnet-runtime-LICENSE.txt'),
        @('Microsoft.NETCore.App.Runtime.win-x64', 'THIRD-PARTY-NOTICES.TXT', 'dotnet-runtime-THIRD-PARTY-NOTICES.txt'),
        @('Microsoft.WindowsDesktop.App.Runtime.win-x64', 'LICENSE', 'windowsdesktop-runtime-LICENSE.txt'))) {
    $pack = $packs | Where-Object { $_.name -eq $l[0] }
    if (-not $pack) { throw "the restore does not name the pack $($l[0])" }
    $file = Join-Path $assets.project.restore.packagesPath "$($l[0].ToLowerInvariant())\$($pack.version.Trim('[', ']', ' ').Split(',')[0])\$($l[1])"
    if (-not (Test-Path -LiteralPath $file)) { throw "missing: $file" }
    Copy-Item -LiteralPath $file "$root\dist\$($l[2])" -Force
}

# the installer: its version is the exe's (<Version> in BurnSmith.App.csproj), and the GitHub release tag must be v<that version>
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup): a release is its installer" }
$version = ((Get-Item "$root\dist\BurnSmith.exe").VersionInfo.ProductVersion -split '\+')[0]
& $iscc "/DAppVersion=$version" "/DFfmpegDir=$Ffmpeg" "/DImgBurnSetup=$ImgBurnSetup" "$root\installer\BurnSmith.iss"
if ($LASTEXITCODE -ne 0) { throw "installer failed" }
Get-Item "$root\dist\BurnSmith-Setup-$version.exe" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
