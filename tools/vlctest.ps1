# Opens a BDMV/DVD folder in the portable VLC (BD-J enabled through JDK 8), drives it through VLC's own RC interface
# (no OS key events, so no other window can receive them) and saves VLC snapshots + logs.
# usage: vlctest.ps1 -Disc <folder> -OutDir <folder> [-Keys key-nav-down,key-nav-activate,sleep:25] [-Scheme bluray|dvd] [-Wait 15]
# An arrow key does reach the xlet (see "Key posted" in libbluray.log), but moves the focus only where menu.txt gives a neighbour: first read the button's line. key-snapshot has only the video, never the BD-J page, and fails when no video plays.
param([string]$Disc, [string]$OutDir, [string[]]$Keys = @(), [string]$Scheme = 'bluray', [int]$Wait = 15, [int]$Port = 4212)
$root = $PSScriptRoot # the tools (not in git) sit beside the main checkout: a worktree finds them higher up, as a development build does
while ($root -and -not (Test-Path "$root\tools\vlc")) { $root = Split-Path $root -Parent }
$jdk = (Get-ChildItem "$root\tools\jdk8\jdk" -Directory | Select-Object -First 1).FullName
$vlc = (Get-ChildItem "$root\tools\vlc\vlc" -Recurse -Filter vlc.exe | Select-Object -First 1).FullName
$env:JAVA_HOME = $jdk
$env:BD_DEBUG_MASK = '0xFFFFFFFF'
$env:BD_DEBUG_FILE = "$OutDir\libbluray.log"
foreach ($f in Get-ChildItem $OutDir -File -Include 'libbluray.log', 'vlc.log', 'snap*.png' -ErrorAction SilentlyContinue) { [IO.File]::Delete($f.FullName) }
Get-Process vlc -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $vlc } | Stop-Process -Force # only our portable copy
$mrl = "${Scheme}:///" + $Disc.Replace('\', '/')
$p = Start-Process $vlc -PassThru -ArgumentList @('--no-one-instance', '--no-qt-privacy-ask', '--no-video-title-show', '--no-qt-error-dialogs',
    '--extraintf=rc', "--rc-host=127.0.0.1:$Port", '--rc-quiet', "--snapshot-path=$OutDir", '--snapshot-format=png', '--snapshot-prefix=snap',
    '-vv', '--file-logging', "--logfile=$OutDir\vlc.log", $mrl)
Start-Sleep $Wait

$tcp = New-Object Net.Sockets.TcpClient('127.0.0.1', $Port)
$w = New-Object IO.StreamWriter($tcp.GetStream()); $w.AutoFlush = $true
foreach ($k in $Keys) {
    if ($k -like 'sleep:*') { Start-Sleep ([int]$k.Substring(6)); continue }
    $w.WriteLine("key $k"); Start-Sleep 3
}
$w.WriteLine('quit'); $tcp.Close()
if (-not $p.WaitForExit(8000)) { $p.Kill() }
Start-Sleep 1
"snapshots: " + ((Get-ChildItem $OutDir -Filter 'snap*.png' | Sort-Object LastWriteTime | ForEach-Object Name) -join ', ')
if ($Scheme -eq 'dvd') { # DVD menus are plain video, so snapshots show them
    "== vlc.log: dvdnav lines =="
    Get-Content "$OutDir\vlc.log" | Select-String -Pattern 'dvdnav|dvdread|libdvd|error' | Select-Object -First 40 | ForEach-Object { $_.Line }
}
elseif (Test-Path "$OutDir\libbluray.log") {
    "== libbluray.log: BD-J lines =="
    Get-Content "$OutDir\libbluray.log" | Select-String -Pattern 'burnsmith:|Exception|Caused|selectPlaylistN|PRINTSTREAM|ERROR: (?!Invalid Disc)' |
        Select-Object -First 50 | ForEach-Object { $_.Line }
}
