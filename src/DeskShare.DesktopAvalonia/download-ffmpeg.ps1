# Downloads the FFmpeg 8.1 shared (LGPL) Windows x64 DLLs required by SIPSorceryMedia.FFmpeg
# (FFmpeg.AutoGen 8.1 → avcodec-62 etc.) into ./ffmpeg. Used by developers and by the release CI.
# Source: BtbN/FFmpeg-Builds (https://github.com/BtbN/FFmpeg-Builds), LGPL build so it can be redistributed.
param(
    [string]$Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip",
    [string]$Destination = (Join-Path $PSScriptRoot "ffmpeg")
)

$ErrorActionPreference = "Stop"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "deskshare-ffmpeg"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $tmp, $Destination | Out-Null

Write-Host "Downloading $Url"
Invoke-WebRequest -Uri $Url -OutFile "$tmp\ffmpeg.zip"
Expand-Archive -Path "$tmp\ffmpeg.zip" -DestinationPath $tmp -Force

# Only the runtime DLLs are needed (no ffmpeg.exe, headers or import libs).
$bin = Get-ChildItem -Path $tmp -Recurse -Directory -Filter bin | Select-Object -First 1
Get-ChildItem -Path $Destination -Filter *.dll | Remove-Item -Force
Copy-Item -Path (Join-Path $bin.FullName "*.dll") -Destination $Destination -Force
Remove-Item $tmp -Recurse -Force

Get-ChildItem $Destination -Filter *.dll | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host "FFmpeg DLLs ready in $Destination"
