# Builds the installer .\dist\BurnSmith-Setup-<version>.exe, the only file of a GitHub release and the one the in-app update downloads (Program Files; Inno Setup 6 is required).
# It wraps .\dist\BurnSmith.exe, an intermediate step: self-contained, one file, with tsMuxeR inside. ffmpeg and ffprobe are not carried: installer\ffmpeg.iss finds or downloads them.
# tsMuxeR is not in this repository: it is taken from this PC and zipped
# into obj\payload\tools.zip, which the BurnSmith.Payload assembly embeds, with tsMuxeR's Apache-2.0 licence.
# None of the templates below stops the script.
# Likewise the pictures and layout files of the menu templates found on this PC (no fonts, no programs) go into
# obj\payload\templates.zip. The first time it runs, the installed exe unpacks the tools into %LOCALAPPDATA%\BurnSmith\files
# and the template pictures into Documents\BurnSmith\Templates.
param(
    [string]$TsMuxer = "$PSScriptRoot\tools\tsmuxer\bin\tsMuxeR.exe"
)
$ErrorActionPreference = 'Stop' # a missing licence file must fail the build, not ship without it
$root = $PSScriptRoot
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
& $iscc "/DAppVersion=$version" "$root\installer\BurnSmith.iss"
if ($LASTEXITCODE -ne 0) { throw "installer failed" }
Get-Item "$root\dist\BurnSmith-Setup-$version.exe" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
