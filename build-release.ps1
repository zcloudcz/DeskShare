# DeskShare Release Builder
# Creates portable deployment packages for Windows

param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$CreateZip
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "DeskShare Release Builder" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check for .NET SDK
Write-Host "[1/7] Checking .NET SDK..." -ForegroundColor Yellow
try {
    $dotnetVersion = dotnet --version
    Write-Host "  ✓ .NET SDK found: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Host "  ✗ .NET SDK not found. Please install .NET 8 SDK." -ForegroundColor Red
    exit 1
}

# Clean previous builds
Write-Host ""
Write-Host "[2/7] Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean DeskShare.sln --configuration $Configuration --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ Clean failed" -ForegroundColor Red
    exit 1
}
Write-Host "  ✓ Clean completed" -ForegroundColor Green

# Run tests (unless skipped)
if (-not $SkipTests) {
    Write-Host ""
    Write-Host "[3/7] Running tests..." -ForegroundColor Yellow
    dotnet test DeskShare.sln --configuration $Configuration --verbosity quiet --no-build
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ✗ Tests failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "  ✓ All tests passed" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[3/7] Skipping tests (-SkipTests flag)" -ForegroundColor Yellow
}

# Build solution
Write-Host ""
Write-Host "[4/7] Building solution..." -ForegroundColor Yellow
dotnet build DeskShare.sln --configuration $Configuration --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "  ✓ Build completed" -ForegroundColor Green

# Publish ScreenSenderApp (self-contained)
Write-Host ""
Write-Host "[5/7] Publishing ScreenSenderApp (portable)..." -ForegroundColor Yellow

$publishDir = "publish/DeskShare-$Version"
$screenSenderPublish = "$publishDir/ScreenSenderApp"

# Publish as self-contained (includes .NET runtime)
dotnet publish src/ScreenSenderApp/DeskShare.ScreenSenderApp.csproj `
    --configuration $Configuration `
    --output $screenSenderPublish `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ ScreenSenderApp publish failed" -ForegroundColor Red
    exit 1
}
Write-Host "  ✓ ScreenSenderApp published to $screenSenderPublish" -ForegroundColor Green

# Publish SignalingServer (self-contained)
Write-Host ""
Write-Host "[6/7] Publishing SignalingServer (portable)..." -ForegroundColor Yellow

$signalingPublish = "$publishDir/SignalingServer"

dotnet publish src/SignalingServer/DeskShare.SignalingServer.csproj `
    --configuration $Configuration `
    --output $signalingPublish `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ SignalingServer publish failed" -ForegroundColor Red
    exit 1
}
Write-Host "  ✓ SignalingServer published to $signalingPublish" -ForegroundColor Green

# Copy WebClient files
Write-Host ""
Write-Host "Copying WebClient files..." -ForegroundColor Yellow
$webClientDest = "$publishDir/WebClient"
New-Item -ItemType Directory -Force -Path $webClientDest | Out-Null
Copy-Item -Path "src/WebClient/*" -Destination $webClientDest -Recurse -Force
Write-Host "  ✓ WebClient copied to $webClientDest" -ForegroundColor Green

# Copy documentation
Write-Host "Copying documentation..." -ForegroundColor Yellow
$docsDest = "$publishDir/docs"
New-Item -ItemType Directory -Force -Path $docsDest | Out-Null
Copy-Item -Path "docs/*" -Destination $docsDest -Recurse -Force
Copy-Item -Path "README.md" -Destination "$publishDir/README.md" -Force
Copy-Item -Path "LICENSE" -Destination "$publishDir/LICENSE" -Force -ErrorAction SilentlyContinue
Write-Host "  ✓ Documentation copied" -ForegroundColor Green

# Create startup scripts
Write-Host "Creating startup scripts..." -ForegroundColor Yellow

# Start-All.ps1 - starts both server and signaling server
$startAllScript = @"
# Start DeskShare
# Starts both SignalingServer and ScreenSenderApp

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "DeskShare Launcher" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Starting SignalingServer..." -ForegroundColor Yellow
Start-Process -FilePath "SignalingServer\DeskShare.SignalingServer.exe" -WorkingDirectory "SignalingServer"
Start-Sleep -Seconds 2

Write-Host "Starting ScreenSenderApp..." -ForegroundColor Yellow
Start-Process -FilePath "ScreenSenderApp\DeskShare.ScreenSenderApp.exe" -WorkingDirectory "ScreenSenderApp"
Start-Sleep -Seconds 2

Write-Host ""
Write-Host "✓ DeskShare is running!" -ForegroundColor Green
Write-Host ""
Write-Host "SignalingServer: http://localhost:5000" -ForegroundColor Cyan
Write-Host "WebClient: Open WebClient\index-control.html in browser" -ForegroundColor Cyan
Write-Host "Metrics: http://localhost:9090/metrics" -ForegroundColor Cyan
Write-Host ""
Write-Host "Press any key to stop all services..." -ForegroundColor Yellow
`$null = `$Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')

Write-Host ""
Write-Host "Stopping services..." -ForegroundColor Yellow
Stop-Process -Name "DeskShare.SignalingServer" -ErrorAction SilentlyContinue
Stop-Process -Name "DeskShare.ScreenSenderApp" -ErrorAction SilentlyContinue
Write-Host "✓ All services stopped" -ForegroundColor Green
"@

Set-Content -Path "$publishDir/Start-All.ps1" -Value $startAllScript -Encoding UTF8
Write-Host "  ✓ Start-All.ps1 created" -ForegroundColor Green

# Start-SignalingServer.ps1
$startSignalingScript = @'
# Start SignalingServer only
Set-Location SignalingServer
.\DeskShare.SignalingServer.exe
'@

Set-Content -Path "$publishDir/Start-SignalingServer.ps1" -Value $startSignalingScript -Encoding UTF8
Write-Host "  ✓ Start-SignalingServer.ps1 created" -ForegroundColor Green

# Start-ScreenSender.ps1
$startScreenSenderScript = @'
# Start ScreenSenderApp only
Set-Location ScreenSenderApp
.\DeskShare.ScreenSenderApp.exe
'@

Set-Content -Path "$publishDir/Start-ScreenSender.ps1" -Value $startScreenSenderScript -Encoding UTF8
Write-Host "  ✓ Start-ScreenSender.ps1 created" -ForegroundColor Green

# Create QUICKSTART.md from template
Write-Host "Creating QUICKSTART.md from template..." -ForegroundColor Yellow
$quickStartTemplate = Get-Content "QUICKSTART.template.md" -Raw -Encoding UTF8
$quickStartContent = $quickStartTemplate -replace '{{VERSION}}', $Version -replace '{{BUILD_DATE}}', (Get-Date -Format "yyyy-MM-dd")
Set-Content -Path "$publishDir/QUICKSTART.md" -Value $quickStartContent -Encoding UTF8
Write-Host "  ✓ QUICKSTART.md created" -ForegroundColor Green

# Create VERSION.txt
$versionInfo = @"
DeskShare v$Version
Build Configuration: $Configuration
Build Date: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
.NET Runtime: Included (self-contained)
Platform: Windows x64

Components:
- ScreenSenderApp (screen capture & streaming)
- SignalingServer (WebSocket coordination)
- WebClient (browser-based viewer)

Total Size: $(Get-ChildItem -Path $publishDir -Recurse | Measure-Object -Property Length -Sum | Select-Object -ExpandProperty Sum | ForEach-Object { [math]::Round($_ / 1MB, 2) }) MB
"@

Set-Content -Path "$publishDir/VERSION.txt" -Value $versionInfo -Encoding UTF8
Write-Host "  ✓ VERSION.txt created" -ForegroundColor Green

# Create ZIP archive (if requested)
if ($CreateZip) {
    Write-Host ""
    Write-Host "[7/7] Creating ZIP archive..." -ForegroundColor Yellow

    $zipPath = "publish/DeskShare-$Version-win-x64.zip"

    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path $publishDir -DestinationPath $zipPath -CompressionLevel Optimal

    $zipSize = (Get-Item $zipPath).Length / 1MB
    Write-Host "  ✓ ZIP created: $zipPath ($([math]::Round($zipSize, 2)) MB)" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[7/7] Skipping ZIP creation (use -CreateZip to enable)" -ForegroundColor Yellow
}

# Summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "✓ Release build completed!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output directory: $publishDir" -ForegroundColor Cyan
Write-Host ""
Write-Host "To test the release:" -ForegroundColor Yellow
Write-Host "  1. cd $publishDir" -ForegroundColor White
Write-Host "  2. .\Start-All.ps1" -ForegroundColor White
Write-Host "  3. Open WebClient\index-control.html" -ForegroundColor White
Write-Host ""
Write-Host "To create distributable ZIP:" -ForegroundColor Yellow
Write-Host "  .\build-release.ps1 -Version $Version -CreateZip" -ForegroundColor White
Write-Host ""
