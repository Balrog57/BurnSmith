# Compiles the BD-J menu xlet to Java 1.3 bytecode (class major 47) and packs it as assets\bdj\menu.jar.
# The BD-J API classes come from the libbluray jar shipped with VLC (compile-time only, not redistributed).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$jdk  = (Get-ChildItem "$root\tools\jdk8\jdk" -Directory | Select-Object -First 1).FullName
$api  = (Get-ChildItem "$root\tools\vlc" -Recurse -Filter 'libbluray-j2se-*.jar' | Select-Object -First 1).FullName
$out  = "$root\bdj\build"
Remove-Item "$out\*" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, "$root\assets\bdj" | Out-Null

$src = Get-ChildItem "$root\bdj\src" -Recurse -Filter *.java | ForEach-Object FullName
& "$jdk\bin\javac.exe" -source 1.3 -target 1.3 -Xlint:-options -cp $api -d $out $src
if ($LASTEXITCODE) { throw 'javac failed' }
& "$jdk\bin\jar.exe" cf "$root\assets\bdj\menu.jar" -C $out .
if ($LASTEXITCODE) { throw 'jar failed' }
"built assets\bdj\menu.jar ({0} bytes)" -f (Get-Item "$root\assets\bdj\menu.jar").Length
