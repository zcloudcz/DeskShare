# Fetches the FFmpeg 8.1 shared (LGPL) Windows x64 DLLs required by SIPSorceryMedia.FFmpeg
# (FFmpeg.AutoGen 8.1 → avcodec-62 etc.) into ./ffmpeg. Used by developers and by the release CI.
#
# Default: the MINIMAL build (~9 MB instead of 159 MB) made by .github/workflows/ffmpeg-windows.yml from
# scripts/ffmpeg-win/build.sh and published as the artifact "ffmpeg-win64-min" of that workflow (needs `gh` logged in,
# in CI GH_TOKEN). To rebuild it: run that workflow (Actions > ffmpeg-windows > Run workflow) or see scripts/ffmpeg-win/README.md.
# -Full: the generic BtbN build (https://github.com/BtbN/FFmpeg-Builds, LGPL), e.g. when the minimal build lacks something.
# -Url:  any zip with the DLLs either in a bin folder or at the zip root.
param(
    [string]$Url,
    [switch]$Full,
    [string]$Repo = "zcloudcz/DeskShare",
    [string]$RunId,   # a specific ffmpeg-windows run; default is the latest successful one
    [string]$Destination = (Join-Path $PSScriptRoot "ffmpeg")
)

$ErrorActionPreference = "Stop"
if ($Full) { $Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip" }
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "deskshare-ffmpeg"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $tmp, $Destination | Out-Null

if ($Url) {
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile "$tmp\ffmpeg.zip"
} else {
    if (-not $RunId) {
        $RunId = gh run list -R $Repo --workflow ffmpeg-windows.yml --status success --limit 1 --json databaseId --jq ".[0].databaseId"
        if (-not $RunId) { throw "No successful ffmpeg-windows run found in $Repo. Run that workflow first, or use -Full." }
    }
    Write-Host "Downloading artifact ffmpeg-win64-min from $Repo run $RunId"
    # Artifacts expire after 90 days; the workflow also runs monthly to keep a fresh one.
    gh run download $RunId -R $Repo -n ffmpeg-win64-min -D "$tmp\art"
    if ($LASTEXITCODE -ne 0) { throw "gh run download failed (expired artifact? use -Full)" }
    Move-Item (Get-ChildItem "$tmp\art" -Filter *.zip | Select-Object -First 1).FullName "$tmp\ffmpeg.zip"
}
Expand-Archive -Path "$tmp\ffmpeg.zip" -DestinationPath $tmp -Force

# Only the runtime DLLs are needed (no ffmpeg.exe, headers or import libs); license texts come along when present.
$bin = Get-ChildItem -Path $tmp -Recurse -Directory -Filter bin | Select-Object -First 1
$src = if ($bin) { $bin.FullName } else { $tmp }
Get-ChildItem -Path $Destination -Include *.dll, *.txt -Recurse | Remove-Item -Force
Copy-Item -Path (Join-Path $src "*.dll") -Destination $Destination -Force
Copy-Item -Path (Join-Path $src "*.txt") -Destination $Destination -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $src "licenses")) { Copy-Item (Join-Path $src "licenses") $Destination -Recurse -Force }
Remove-Item $tmp -Recurse -Force

Get-ChildItem $Destination -Filter *.dll | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host "FFmpeg DLLs ready in $Destination"
