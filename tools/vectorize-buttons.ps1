# Traces the button pictures of the vendor templates into SVG with VectorCraft's Image Trace, one name.svg beside each name.png.
# BurnSmith draws the .svg in place of the picture it stands beside (InstalledTemplate.Asset), and a converted pack carries both.
#
#   pwsh tools\vectorize-buttons.ps1 [-Root <templates folder>] [-Force]
#
# -Root : the folder template-folders.txt names (default: Documents\BurnSmith\Templates\sources). -Force traces again what already has its .svg.
# What a trace loses: translucent glows around a button and fine textures inside it (stripes); shapes, gradients and lettering are kept.
param(
    [string]$Root = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'BurnSmith\Templates\sources'),
    [string]$VectorCraft = $(if ($env:VECTORCRAFT_CLI) { $env:VECTORCRAFT_CLI } else { 'C:\Program Files\ArtCraft\VectorCraft\vectorcraft-cli.exe' }),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $VectorCraft)) { throw "VectorCraft introuvable : $VectorCraft" }
# "High Fidelity Photo" keeps up to 64 colours; with gradients an evenly shaded area is one path instead of bands
$trace = '{"preset":"High Fidelity Photo","params":{"gradients":true}}'
# the buttons: DVDFab's play / scenes / setup / home / left / right (normal and highlighted), the arrows of the picture themes
$buttons = Get-ChildItem $Root -Recurse -File -Filter *.png | Where-Object {
    $_.FullName -notmatch '\\LANG_[A-Z]+\\' -and $_.Name -notmatch '\.mask\.' -and (
        $_.Directory.Name -in 'play', 'scenes', 'setup', 'home', 'left', 'right' -or
        ($_.Directory.Name -in 'Scene', '1920x1080' -and $_.BaseName -in 'back', 'next', 'previous'))
}
$done = 0; $failed = 0
foreach ($png in $buttons) {
    $svg = [IO.Path]::ChangeExtension($png.FullName, '.svg')
    if (-not $Force -and (Test-Path $svg) -and (Get-Item $svg).LastWriteTimeUtc -ge $png.LastWriteTimeUtc) { continue }
    $out = & $VectorCraft run --in $png.FullName --cmd select.all --cmd imageTrace.makeAndExpand --params $trace --export $svg 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $svg) -or $out -notmatch '"format":"svg"') { $failed++; Write-Warning "non tracé : $($png.FullName)"; if (Test-Path $svg) { Remove-Item $svg }; continue }
    $done++
}
"{0} boutons, {1} tracés, {2} échecs" -f $buttons.Count, $done, $failed
if ($failed -gt 0) { exit 1 }
