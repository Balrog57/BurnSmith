# Proves installer\ffmpeg.iss, imgburn.iss and running.iss without UAC, through a real setup (test-ffmpeg.iss) on a test registry key, and, when dist\BurnSmith.exe
# exists (publish.ps1), an update started from the open app as Updater.cs starts it. Not covered: the real machine PATH, the elevation, Program Files.
# The app's window shows for a few seconds. Exit code 1 when something failed. Leaves nothing behind: ImgBurn's remembered folder is put back.
param(
    [string]$Ffmpeg = $(if (Test-Path "$PSScriptRoot\..\tools\ffmpeg\ffmpeg.exe") { "$PSScriptRoot\..\tools\ffmpeg" } else { Split-Path (Get-Command ffmpeg.exe -ErrorAction Stop).Source }),
    [string]$ImgBurnSetup = "$PSScriptRoot\..\tools\imgburn-setup\SetupImgBurn_2.5.8.0.exe",
    [string]$AppExe = "$PSScriptRoot\..\dist\BurnSmith.exe"
)
$ErrorActionPreference = 'Stop'
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$out = [IO.Path]::GetFullPath("$PSScriptRoot\..\obj\ffmpeg-test") # Inno writes {app} without the ..
$key = 'HKCU:\Software\BurnSmithFfmpegTest'
$fails = 0
function Check($name, $ok) { if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fails++ } }
function SetPath($sub, $value) { $k = (Get-Item -LiteralPath $key).OpenSubKey($sub, $true); $k.SetValue('Path', $value, 'ExpandString'); $k.Close() }
function GetPath($sub) { $k = (Get-Item -LiteralPath $key).OpenSubKey($sub); $r = [pscustomobject]@{ Value = $k.GetValue('Path', '', 'DoNotExpandEnvironmentNames'); Kind = $k.GetValueKind('Path') }; $k.Close(); $r }
function Run($dir, $log) { $p = Start-Process "$out\test-ffmpeg.exe" -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$dir`"", "/LOG=`"$log`"" -PassThru -Wait; $p.ExitCode }
function Apps { Get-Process BurnSmith -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$app\BurnSmith.exe" } }

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $out | Out-Null
$app = "$out\app"; $own = "$app\tools\ffmpeg"; $withApp = Test-Path $AppExe
$defines = @("/DFfmpegDir=$([IO.Path]::GetFullPath($Ffmpeg))", "/DImgBurnSetup=$([IO.Path]::GetFullPath($ImgBurnSetup))", "/DAppDir=$app")
if ($withApp) { $defines += "/DAppExe=$([IO.Path]::GetFullPath($AppExe))" } else { "dist\BurnSmith.exe absent (run publish.ps1 first): the update from the open app is not tried" }
& $iscc /Q @defines "$PSScriptRoot\test-ffmpeg.iss"; if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$env:__COMPAT_LAYER = 'RunAsInvoker' # ImgBurn's setup asks for administrator rights: here it runs as the user, into the test's folder
$ib = 'HKCU:\Software\ImgBurn'; $ibHad = Test-Path $ib; $ibDir = if ($ibHad) { (Get-ItemProperty $ib).InstallDirectory }
$icon = "$env:PUBLIC\Desktop\ImgBurn.lnk"; $iconHad = Test-Path $icon
try {
    New-Item -Path "$key\Machine" -Force | Out-Null
    $other = '%SystemRoot%\no-such-folder'
    SetPath 'Machine' "$other;C:\elsewhere"
    New-Item $ib -Force | Out-Null; Set-ItemProperty $ib InstallDirectory "$out\remembered" # where an earlier ImgBurn went: its setup goes back there unless told

    'Install: ffmpeg in its folder and on the PATH, ImgBurn where BurnSmith looks'
    $code = Run $app "$out\1.log"; $log = Get-Content "$out\1.log" -Raw; $p = GetPath 'Machine'
    Check 'exit code 0' ($code -eq 0)
    Check 'ffmpeg.exe, ffprobe.exe, LICENSE and README.txt, nothing else' (((Get-ChildItem $own -File).Name | Sort-Object) -join ',' -eq 'ffmpeg.exe,ffprobe.exe,LICENSE,README.txt')
    Check 'the carried ffmpeg is the pinned 8.1.1' ((& "$own\ffmpeg.exe" -version | Select-Object -First 1) -match '^ffmpeg version 8\.1\.1-')
    Check 'added to the PATH' ($p.Value -eq "$other;C:\elsewhere;$own")
    Check 'the value stays REG_EXPAND_SZ, %VAR% unexpanded' ($p.Kind -eq 'ExpandString')
    Check '[Run] entries see it: the relaunch finds ffmpeg' (Test-Path "$app\run-saw.txt")
    Check 'ImgBurn installed, nothing downloaded' ($log -match 'ImgBurn installed' -and $log -notmatch 'Downloading')
    Check 'in the folder given, not the one its setup remembered' ((Test-Path "$app\ImgBurn\ImgBurn.exe") -and -not (Test-Path "$out\remembered"))
    Check 'no ImgBurn icon left on the desktop' ((Test-Path $icon) -eq $iconHad)

    'Install again: one PATH entry, ImgBurn left as it is'
    $code = Run $app "$out\2.log"; $log = Get-Content "$out\2.log" -Raw
    Check 'exit code 0' ($code -eq 0)
    Check 'one PATH entry' ((GetPath 'Machine').Value -eq "$other;C:\elsewhere;$own")
    Check "ImgBurn's setup not run again" ($log -notmatch 'SetupImgBurn')

    if ($withApp) {
        'An update started from the open app, as Updater.cs does it: Restart Manager closes the app, the setup ends well, the app comes back'
        $since = Get-Date
        $held = [IO.File]::Open("$out\test-ffmpeg.exe", 'Open', 'Read', 'Read') # Updater.DownloadAsync keeps the checked file open like this
        try {
            $first = Start-Process "$app\BurnSmith.exe" -ArgumentList '--try-install', "`"$out\test-ffmpeg.exe`"" -PassThru
            $closed = $first.WaitForExit(180000)
        }
        finally { $held.Dispose() }
        Check 'the open app was closed' $closed
        $setupLog = $null
        for ($i = 0; $i -lt 120 -and -not ($setupLog -and (Get-Content $setupLog.FullName -Raw) -match 'Log closed'); $i++) {
            Start-Sleep 1
            $setupLog = Get-ChildItem $env:TEMP -Filter 'Setup Log*.txt' | Where-Object LastWriteTime -gt $since | Sort-Object LastWriteTime | Select-Object -Last 1
        }
        $log = if ($setupLog) { Get-Content $setupLog.FullName -Raw } else { '' }
        Check 'the setup ran and succeeded' ($log -match 'Installation process succeeded')
        Check 'it closed the app itself (Restart Manager)' ($log -match '(?i)attempting to shut down|shutting down applications|RmShutdown')
        $back = $null
        for ($i = 0; $i -lt 30 -and -not $back; $i++) { Start-Sleep 1; $back = Apps }
        Check 'the app started again' ($null -ne $back)
        Apps | Stop-Process -Force; Start-Sleep 2
        if ($fails) { "  setup log: $($setupLog.FullName)" }
    }

    'Uninstall of a running app: nothing moves, and it says so by its exit code'
    Check 'the app and the uninstaller use one name' ((Get-Content "$PSScriptRoot\running.iss" -Raw) -match "'BurnSmithRunning'" -and (Get-Content "$PSScriptRoot\..\src\BurnSmith.App\App.xaml.cs" -Raw) -match '"BurnSmithRunning"')
    $mutex = [Threading.Mutex]::new($false, 'BurnSmithRunning')
    $before = (Get-ChildItem $app -Recurse -File).Count
    $u = Start-Process "$app\unins000.exe" -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
    $mutex.Dispose()
    Start-Sleep 3
    Check 'it fails, without hanging' ($u.ExitCode -ne 0)
    Check 'every file is still there' ((Get-ChildItem $app -Recurse -File).Count -eq $before -and (Test-Path "$app\unins000.exe") -and (Test-Path "$own\ffmpeg.exe"))
    Check 'the PATH entry is still there' ((GetPath 'Machine').Value -eq "$other;C:\elsewhere;$own")

    "Uninstall: the PATH entry and ffmpeg's folder only; ImgBurn is its own program"
    Set-Content "$app\tools\mine.txt" 'kept'
    Start-Process "$app\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Start-Sleep 3 # the uninstaller's copy of itself ends after it
    $p = GetPath 'Machine'
    Check 'the entry is gone, the others stay' ($p.Value -eq "$other;C:\elsewhere")
    Check 'still REG_EXPAND_SZ' ($p.Kind -eq 'ExpandString')
    Check 'the folder is gone' (-not (Test-Path $own))
    Check "someone else's file in tools stays" (Test-Path "$app\tools\mine.txt")
    Check 'ImgBurn stays' (Test-Path "$app\ImgBurn\ImgBurn.exe")
}
finally {
    Remove-Item Env:\__COMPAT_LAYER -ErrorAction SilentlyContinue
    Apps | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $ibHad) { Remove-Item $ib -Recurse -Force -ErrorAction SilentlyContinue }
    elseif ($null -ne $ibDir) { Set-ItemProperty $ib InstallDirectory $ibDir }
    if (-not $iconHad) { Remove-Item $icon -ErrorAction SilentlyContinue }
}
if ($fails) { "$fails failed"; exit 1 }
'all ok'
