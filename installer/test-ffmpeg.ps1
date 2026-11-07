# Proves installer\ffmpeg.iss without UAC, through a real setup on test registry keys (test-ffmpeg.iss). It downloads ffmpeg once, about 115 MB.
# Not covered: the real machine PATH, the running elevated, the wizard's message box. Exit code 1 when something failed. Leaves nothing in the registry.
param([string]$GoodFfmpeg = (Split-Path (Get-Command ffmpeg -ErrorAction Stop).Source))
$ErrorActionPreference = 'Stop'
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$out = [IO.Path]::GetFullPath("$PSScriptRoot\..\obj\ffmpeg-test") # Inno writes {app} without the ..
$key = 'HKCU:\Software\BurnSmithFfmpegTest'
$real = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
$fails = 0
function Check($name, $ok) { if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fails++ } }
function Build($name, $defines) { & $iscc /Q "/DOutName=$name" @defines "$PSScriptRoot\test-ffmpeg.iss"; if ($LASTEXITCODE -ne 0) { throw "ISCC failed: $name" } }
function SetPath($sub, $value) { $k = (Get-Item -LiteralPath $key).OpenSubKey($sub, $true); $k.SetValue('Path', $value, 'ExpandString'); $k.Close() }
function GetPath($sub) { $k = (Get-Item -LiteralPath $key).OpenSubKey($sub); $r = [pscustomobject]@{ Value = $k.GetValue('Path', '', 'DoNotExpandEnvironmentNames'); Kind = $k.GetValueKind('Path') }; $k.Close(); $r }
function Run($exe, $dir, $log) { $p = Start-Process "$out\$exe.exe" -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$dir`"", "/LOG=`"$log`"" -PassThru -Wait; $p.ExitCode }

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $out | Out-Null
Build 'good' @()
Build 'nosite' @('/DFfmpegHashUrl=https://www.gyan.dev/ffmpeg/builds/no-such-file.sha256')
Build 'badhash' @("/DFfmpegHashUrl=$real.sha256", "/DFfmpegZipUrl=$real.sha256") # the hash of another file, quickly

try {
    New-Item -Path "$key\Machine" -Force | Out-Null; New-Item -Path "$key\User" -Force | Out-Null
    $fake = "$out\fake"; New-Item -ItemType Directory -Force $fake | Out-Null
    'ffmpeg.exe', 'ffprobe.exe' | ForEach-Object { Set-Content "$fake\$_" 'not a program' }
    $other = '%SystemRoot%\no-such-folder'
    SetPath 'Machine' "$other;$fake"; SetPath 'User' ''
    $app = "$out\app"; $own = "$app\tools\ffmpeg"

    'A file named ffmpeg on the PATH is no ffmpeg: download, install, PATH entry'
    $code = Run 'good' $app "$out\1.log"; $log = Get-Content "$out\1.log" -Raw; $p = GetPath 'Machine'
    Check 'exit code 0' ($code -eq 0)
    Check 'it downloaded' ($log -match 'no usable ffmpeg: downloading')
    Check 'ffmpeg.exe, ffprobe.exe and LICENSE, nothing else' (((Get-ChildItem $own -File).Name | Sort-Object) -join ',' -eq 'ffmpeg.exe,ffprobe.exe,LICENSE')
    Check 'the copy runs' ((& "$own\ffmpeg.exe" -version | Select-Object -First 1) -match '^ffmpeg version')
    Check 'added to the PATH' ($p.Value -eq "$other;$fake;$own")
    Check 'the value stays REG_EXPAND_SZ, %VAR% unexpanded' ($p.Kind -eq 'ExpandString')
    Check '[Run] entries see it: the relaunch finds ffmpeg' (Test-Path "$app\run-saw.txt")

    'An update: a good copy is kept, no download, no second PATH entry'
    $before = (Get-Item "$own\ffmpeg.exe").LastWriteTimeUtc
    $code = Run 'good' $app "$out\2.log"; $log = Get-Content "$out\2.log" -Raw
    Check 'exit code 0' ($code -eq 0)
    Check 'no download' ($log -notmatch 'downloading')
    Check 'the copy is untouched' ((Get-Item "$own\ffmpeg.exe").LastWriteTimeUtc -eq $before)
    Check 'one PATH entry' ((GetPath 'Machine').Value -eq "$other;$fake;$own")

    'Uninstall of a running app: nothing moves, and it says so by its exit code'
    Check 'the app and the uninstaller use one name' ((Get-Content "$PSScriptRoot\running.iss" -Raw) -match "'BurnSmithRunning'" -and (Get-Content "$PSScriptRoot\..\src\BurnSmith.App\App.xaml.cs" -Raw) -match '"BurnSmithRunning"')
    $held = [Threading.Mutex]::new($false, 'BurnSmithRunning')
    $before = (Get-ChildItem $app -Recurse -File).Count
    $u = Start-Process "$app\unins000.exe" -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
    $held.Dispose()
    Start-Sleep 3
    Check 'it fails, without hanging' ($u.ExitCode -ne 0)
    Check 'every file is still there' ((Get-ChildItem $app -Recurse -File).Count -eq $before -and (Test-Path "$app\unins000.exe") -and (Test-Path "$own\ffmpeg.exe"))
    Check 'the PATH entry is still there' ((GetPath 'Machine').Value -eq "$other;$fake;$own")

    'Uninstall: the PATH entry and the folder only'
    Set-Content "$app\tools\mine.txt" 'kept'
    Start-Process "$app\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Start-Sleep 3 # the uninstaller's copy of itself ends after it
    $p = GetPath 'Machine'
    Check 'the entry is gone, the others stay' ($p.Value -eq "$other;$fake")
    Check 'still REG_EXPAND_SZ' ($p.Kind -eq 'ExpandString')
    Check 'the folder is gone' (-not (Test-Path $own))
    Check "someone else's file in tools stays" (Test-Path "$app\tools\mine.txt")
    Remove-Item $app -Recurse -Force

    'A usable ffmpeg on the user PATH: no download, no folder, no PATH entry'
    $good = "$out\good-copy"; New-Item -ItemType Directory -Force $good | Out-Null
    Copy-Item "$GoodFfmpeg\ffmpeg.exe", "$GoodFfmpeg\ffprobe.exe" $good
    SetPath 'User' "$good\"
    $code = Run 'nosite' $app "$out\3.log"; $log = Get-Content "$out\3.log" -Raw
    Check 'exit code 0' ($code -eq 0)
    Check 'no download' ($log -notmatch 'downloading')
    Check 'no folder' (-not (Test-Path $own))
    Check 'no PATH entry' ((GetPath 'Machine').Value -eq "$other;$fake")
    Remove-Item $app -Recurse -Force
    SetPath 'User' ''

    'The site cannot be reached: BurnSmith is still installed, and the setup succeeds'
    $code = Run 'nosite' $app "$out\4.log"; $log = Get-Content "$out\4.log" -Raw
    Check 'exit code 0' ($code -eq 0)
    Check 'the failure is logged' ($log -match 'ffmpeg not installed')
    Check 'the app files are there' (Test-Path "$app\LICENSE")
    Check 'no folder, no PATH entry' (-not (Test-Path $own) -and (GetPath 'Machine').Value -eq "$other;$fake")

    'A broken copy of ours and a wrong hash: it goes, and the setup succeeds'
    New-Item -ItemType Directory -Force $own | Out-Null
    Copy-Item "$fake\*" $own
    SetPath 'Machine' "$other;$fake;$own"
    $code = Run 'badhash' $app "$out\5.log"; $log = Get-Content "$out\5.log" -Raw
    Check 'exit code 0' ($code -eq 0)
    Check 'the hash failure is logged' ($log -match 'ffmpeg not installed')
    Check 'the broken copy is gone' (-not (Test-Path $own))
    Check 'its PATH entry is gone' ((GetPath 'Machine').Value -eq "$other;$fake")
}
finally {
    Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fails) { "$fails failed"; exit 1 }
'all ok'
