# Download FFmpeg binaries for RemoteDesktop.Desktop
# This script downloads FFmpeg shared libraries (DLLs) for Windows x64

param(
    [string]$Version = "7.0",
    [string]$OutputDir = ".\ffmpeg"
)

$ErrorActionPreference = "Stop"

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " FFmpeg Native Libraries Downloader" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host ""

# Create output directory
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
    Write-Host "Created directory: $OutputDir" -ForegroundColor Green
}

# FFmpeg download URLs
$ffmpegUrl = "https://github.com/Ruslan-B/FFmpeg.AutoGen/raw/master/FFmpeg/bin/x64"

# List of required DLL files
$requiredDlls = @(
    "avcodec-60.dll",
    "avdevice-60.dll",
    "avfilter-9.dll",
    "avformat-60.dll",
    "avutil-58.dll",
    "swresample-4.dll",
    "swscale-7.dll"
)

Write-Host "Downloading FFmpeg $Version binaries from GitHub..." -ForegroundColor Yellow
Write-Host "Source: $ffmpegUrl" -ForegroundColor Gray
Write-Host ""

$downloadedCount = 0
$failedCount = 0

foreach ($dll in $requiredDlls) {
    $url = "$ffmpegUrl/$dll"
    $outputPath = Join-Path $OutputDir $dll

    try {
        Write-Host "Downloading: $dll... " -NoNewline

        # Download with progress
        $webClient = New-Object System.Net.WebClient
        $webClient.DownloadFile($url, $outputPath)

        if (Test-Path $outputPath) {
            $fileSize = (Get-Item $outputPath).Length / 1MB
            Write-Host "OK ($([math]::Round($fileSize, 2)) MB)" -ForegroundColor Green
            $downloadedCount++
        } else {
            Write-Host "FAILED" -ForegroundColor Red
            $failedCount++
        }
    }
    catch {
        Write-Host "FAILED: $_" -ForegroundColor Red
        $failedCount++
    }
}

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " Download Summary" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "Downloaded: $downloadedCount / $($requiredDlls.Count)" -ForegroundColor $(if ($downloadedCount -eq $requiredDlls.Count) { "Green" } else { "Yellow" })
Write-Host "Failed: $failedCount" -ForegroundColor $(if ($failedCount -eq 0) { "Green" } else { "Red" })
Write-Host "Output Directory: $OutputDir" -ForegroundColor Gray
Write-Host ""

if ($downloadedCount -eq $requiredDlls.Count) {
    Write-Host "✅ All FFmpeg libraries downloaded successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Yellow
    Write-Host "  1. Rebuild the project: dotnet build" -ForegroundColor Gray
    Write-Host "  2. FFmpeg DLLs will be automatically copied to output directory" -ForegroundColor Gray
    Write-Host ""
    exit 0
} else {
    Write-Host "⚠️ Some libraries failed to download!" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Alternative installation methods:" -ForegroundColor Yellow
    Write-Host "  1. winget install 'FFmpeg (Shared)' --version 7.0" -ForegroundColor Gray
    Write-Host "  2. Download from: https://www.gyan.dev/ffmpeg/builds/" -ForegroundColor Gray
    Write-Host ""
    exit 1
}
