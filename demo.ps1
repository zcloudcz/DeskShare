# DeskShare Demo Launcher
# This script starts the SignalingServer and opens a browser for testing

param(
    [switch]$WithScreenSender,
    [switch]$Help
)

if ($Help) {
    Write-Host "DeskShare Demo Launcher" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:"
    Write-Host "  .\demo.ps1                    Start SignalingServer and open browser"
    Write-Host "  .\demo.ps1 -WithScreenSender  Also start ScreenSenderApp automatically"
    Write-Host "  .\demo.ps1 -Help              Show this help"
    Write-Host ""
    Write-Host "After running this script:"
    Write-Host "  1. SignalingServer will start on http://localhost:5000"
    Write-Host "  2. Browser will open to http://localhost:5000"
    Write-Host "  3. Navigate to /statistics to see server stats"
    Write-Host "  4. Open src/WebClient/index.html to test WebRTC streaming"
    Write-Host ""
    Write-Host "Press Ctrl+C to stop all services"
    exit 0
}

Write-Host "===========================================`n" -ForegroundColor Green
Write-Host "  DeskShare Demo Launcher" -ForegroundColor Green
Write-Host "`n===========================================" -ForegroundColor Green
Write-Host ""

# Check if .NET 8 is installed
Write-Host "[1/4] Checking .NET 8 SDK..." -ForegroundColor Yellow
try {
    $dotnetVersion = dotnet --version
    Write-Host "  ✓ Found .NET $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Host "  ✗ .NET 8 SDK not found! Please install from https://dotnet.microsoft.com" -ForegroundColor Red
    exit 1
}

# Build solution
Write-Host "`n[2/4] Building solution..." -ForegroundColor Yellow
$buildOutput = dotnet build DeskShare.sln --configuration Release --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ Build failed!" -ForegroundColor Red
    Write-Host $buildOutput
    exit 1
}
Write-Host "  ✓ Build successful" -ForegroundColor Green

# Start SignalingServer in background
Write-Host "`n[3/4] Starting SignalingServer..." -ForegroundColor Yellow
$signalingJob = Start-Job -ScriptBlock {
    Set-Location $using:PWD
    dotnet run --project src/SignalingServer/RemoteDesktop.SignalingServer.csproj --configuration Release --no-build
}
Write-Host "  ✓ SignalingServer started (Job ID: $($signalingJob.Id))" -ForegroundColor Green

# Wait for server to start
Write-Host "`n  Waiting for server to be ready..." -ForegroundColor Cyan
Start-Sleep -Seconds 3

# Test if server is responding
try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/health" -TimeoutSec 5 -ErrorAction Stop
    Write-Host "  ✓ Server is responding!" -ForegroundColor Green
} catch {
    Write-Host "  ⚠ Server may not be ready yet, continuing anyway..." -ForegroundColor Yellow
}

# Optionally start ScreenSenderApp
if ($WithScreenSender) {
    Write-Host "`n[3.5/4] Starting ScreenSenderApp..." -ForegroundColor Yellow
    $senderJob = Start-Job -ScriptBlock {
        Set-Location $using:PWD
        dotnet run --project src/ScreenSenderApp/RemoteDesktop.ScreenSenderApp.csproj --configuration Release --no-build
    }
    Write-Host "  ✓ ScreenSenderApp started (Job ID: $($senderJob.Id))" -ForegroundColor Green
}

# Open browser
Write-Host "`n[4/4] Opening browser..." -ForegroundColor Yellow
Start-Process "http://localhost:5000"
Write-Host "  ✓ Browser opened to http://localhost:5000" -ForegroundColor Green

# Show instructions
Write-Host "`n===========================================`n" -ForegroundColor Green
Write-Host "  Demo is running!" -ForegroundColor Green
Write-Host "`n===========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Available endpoints:" -ForegroundColor Cyan
Write-Host "  • Home:        http://localhost:5000" -ForegroundColor White
Write-Host "  • Health:      http://localhost:5000/health" -ForegroundColor White
Write-Host "  • Statistics:  http://localhost:5000/statistics" -ForegroundColor White
Write-Host "  • WebClient:   src/WebClient/index.html (open in browser)" -ForegroundColor White
Write-Host ""
Write-Host "Press Ctrl+C to stop all services" -ForegroundColor Yellow
Write-Host ""

# Wait for user to press Ctrl+C
try {
    while ($true) {
        Start-Sleep -Seconds 1

        # Check if jobs are still running
        $runningJobs = Get-Job | Where-Object { $_.State -eq "Running" }
        if ($runningJobs.Count -eq 0) {
            Write-Host "`n⚠ All background jobs stopped unexpectedly" -ForegroundColor Yellow
            break
        }
    }
} finally {
    # Cleanup
    Write-Host "`n`nStopping services..." -ForegroundColor Yellow
    Get-Job | Stop-Job
    Get-Job | Remove-Job
    Write-Host "✓ All services stopped" -ForegroundColor Green
}
