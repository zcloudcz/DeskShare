# Deployment Guide

Complete guide for deploying DeskShare as a portable application.

## Table of Contents

1. [Overview](#overview)
2. [Building Release](#building-release)
3. [Portable Deployment](#portable-deployment)
4. [Distribution](#distribution)
5. [Production Deployment](#production-deployment)
6. [Updates and Maintenance](#updates-and-maintenance)

---

## Overview

DeskShare is designed as a **portable application** - no installation required!

**Deployment Strategy:**
- Self-contained executable (includes .NET runtime)
- Extract and run - zero dependencies
- No registry modifications
- No admin rights required (except for first-time firewall)
- Easy updates - just replace files

**Package Contents:**
```
DeskShare-1.0.0/
├── ScreenSenderApp/          # Screen capture & streaming
│   ├── RemoteDesktop.ScreenSenderApp.exe
│   ├── appsettings.json
│   └── [dependencies]
├── SignalingServer/          # WebSocket coordination
│   ├── RemoteDesktop.SignalingServer.exe
│   ├── appsettings.json
│   └── [dependencies]
├── WebClient/                # Browser-based viewer
│   ├── index-control.html
│   ├── client-with-control.js
│   ├── remote-control.js
│   └── [web assets]
├── docs/                     # Documentation
├── Start-All.ps1             # One-click launcher
├── Start-SignalingServer.ps1
├── Start-ScreenSender.ps1
├── QUICKSTART.md
├── README.md
└── VERSION.txt
```

---

## Building Release

> **Superseded:** releases are built by `.github/workflows/release.yml` (Velopack). `build-release.ps1` was removed; the portable-ZIP steps below are historical.

### Prerequisites

- .NET 8 SDK
- PowerShell 5.1 or PowerShell Core 7+
- Windows 10/11 (for build)

### Build Process

**1. Simple Build (current version):**

```powershell
.\build-release.ps1
```

This creates: `publish/DeskShare-1.0.0/`

**2. Custom Version:**

```powershell
.\build-release.ps1 -Version "1.2.0"
```

**3. Create ZIP for Distribution:**

```powershell
.\build-release.ps1 -Version "1.0.0" -CreateZip
```

Creates: `publish/DeskShare-1.0.0-win-x64.zip`

**4. Skip Tests (faster build):**

```powershell
.\build-release.ps1 -SkipTests -CreateZip
```

### Build Script Parameters

| Parameter | Description | Default |
|-----------|-------------|---------|
| `-Version` | Version number (semver) | `1.0.0` |
| `-Configuration` | Build configuration | `Release` |
| `-SkipTests` | Skip running tests | `false` |
| `-CreateZip` | Create ZIP archive | `false` |

### Build Output

**Console Output:**
```
========================================
DeskShare Release Builder
Version: 1.0.0
========================================

[1/7] Checking .NET SDK...
  ✓ .NET SDK found: 8.0.100

[2/7] Cleaning previous builds...
  ✓ Clean completed

[3/7] Running tests...
  ✓ All tests passed (148/148)

[4/7] Building solution...
  ✓ Build completed

[5/7] Publishing ScreenSenderApp (portable)...
  ✓ ScreenSenderApp published

[6/7] Publishing SignalingServer (portable)...
  ✓ SignalingServer published

[7/7] Creating ZIP archive...
  ✓ ZIP created: 45.3 MB

========================================
✓ Release build completed!
========================================
```

---

## Portable Deployment

### For End Users

**Download and Extract:**

1. Download `DeskShare-1.0.0-win-x64.zip`
2. Extract to any location (e.g., `C:\RemoteDesktop\`)
3. No installation required!

**First Run:**

1. **Windows Firewall Prompt:**
   - Click "Allow access" when prompted
   - Required for network communication

2. **Start Application:**
   ```powershell
   .\Start-All.ps1
   ```

3. **Open WebClient:**
   - Open `WebClient\index-control.html` in browser
   - Click "Connect"

**That's it!** No setup, no configuration needed for basic use.

### Manual Configuration (Optional)

**ScreenSenderApp Settings** (`ScreenSenderApp\appsettings.json`):

```json
{
  "Capture": {
    "TargetFps": 30,          // Frame rate (15, 30, 60)
    "UseSimdOptimization": true
  },
  "WebRTC": {
    "MaxBitrate": 5000        // Video bitrate (Kbps)
  },
  "VideoQualityPresets": {
    "Medium": {               // Change to "High" or "Ultra"
      "Width": 1920,
      "Height": 1080,
      "Fps": 30
    }
  }
}
```

**SignalingServer Settings** (`SignalingServer\appsettings.json`):

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5000"  // Change port if needed
      }
    }
  }
}
```

### Running Individual Components

**SignalingServer Only:**
```powershell
.\Start-SignalingServer.ps1
```

**ScreenSenderApp Only:**
```powershell
.\Start-ScreenSender.ps1
```

**Custom Configuration:**
```powershell
cd ScreenSenderApp
.\RemoteDesktop.ScreenSenderApp.exe --urls "http://localhost:9090"
```

---

## Distribution

### Hosting on GitHub Releases

**1. Create Release on GitHub:**

```bash
# Tag the release
git tag -a v1.0.0 -m "Release version 1.0.0"
git push origin v1.0.0

# Create release on GitHub
# Upload: DeskShare-1.0.0-win-x64.zip
```

**2. Release Notes Template:**

```markdown
# DeskShare v1.0.0

## 🚀 Portable Windows Release

**Download:** [DeskShare-1.0.0-win-x64.zip](link)

### Features

- ✅ Screen sharing via WebRTC (low latency <150ms)
- ✅ Remote mouse & keyboard control
- ✅ Multi-client support (broadcast to multiple viewers)
- ✅ Real-time statistics (FPS, bitrate, latency, packet loss)
- ✅ Adaptive quality based on network conditions
- ✅ OpenTelemetry metrics for monitoring

### Requirements

- Windows 10 or Windows 11 (64-bit)
- No .NET Runtime required (self-contained)

### Quick Start

1. Download and extract ZIP
2. Run `Start-All.ps1`
3. Open `WebClient\index-control.html` in browser
4. Click "Connect"

### Documentation

- [Quick Start](QUICKSTART.md)
- [User Guide](docs/run-guide.md)
- [Remote Control](docs/remote-control-guide.md)
- [Troubleshooting](docs/troubleshooting.md)

### Changelog

- Initial release
- Full WebRTC screen sharing
- Remote control implementation
- Multi-client support
- OpenTelemetry monitoring

### Known Issues

- Authorization dialog not yet implemented (auto-approve in current version)
- UAC prompts cannot be controlled remotely (Windows security limitation)

### Support

Report issues: https://github.com/your-username/DeskShare/issues
```

### Hosting on Website

**Static File Hosting:**

```html
<!-- Download page -->
<div class="download">
  <h2>Download DeskShare</h2>
  <a href="DeskShare-1.0.0-win-x64.zip" class="download-btn">
    Download for Windows (64-bit)
    <small>Version 1.0.0 • 45 MB • No installation required</small>
  </a>

  <h3>System Requirements</h3>
  <ul>
    <li>Windows 10 or 11 (64-bit)</li>
    <li>No additional software needed</li>
  </ul>

  <h3>Quick Start</h3>
  <ol>
    <li>Extract ZIP to any folder</li>
    <li>Run Start-All.ps1</li>
    <li>Open WebClient in browser</li>
  </ol>
</div>
```

### Automatic Updates (Future Enhancement)

**Version Check Mechanism:**

```json
// version.json (hosted on web)
{
  "version": "1.0.0",
  "downloadUrl": "https://example.com/DeskShare-1.0.0-win-x64.zip",
  "releaseDate": "2025-11-07",
  "changelog": "..."
}
```

**Client-Side Check:**

```csharp
// Future: Add to ScreenSenderApp startup
var currentVersion = "1.0.0";
var latestVersion = await CheckForUpdates("https://example.com/version.json");

if (latestVersion > currentVersion)
{
    Console.WriteLine($"New version available: {latestVersion}");
    Console.WriteLine("Download: https://example.com/downloads");
}
```

---

## Production Deployment

### Single Machine (Testing/Demo)

**Use Case:** Personal use, testing, demos

**Setup:**
1. Extract to `C:\RemoteDesktop\`
2. Run `Start-All.ps1`
3. Access via `http://localhost:5000`

**No special configuration needed.**

### LAN Deployment (Office/Home Network)

**Use Case:** Share screen within local network

**Server Setup:**

1. Extract on server machine
2. Configure firewall:
   ```powershell
   New-NetFirewallRule -DisplayName "DeskShare" `
     -Direction Inbound -Protocol TCP -LocalPort 5000,9090 -Action Allow
   ```

3. Edit `SignalingServer\appsettings.json`:
   ```json
   {
     "Kestrel": {
       "Endpoints": {
         "Http": {
           "Url": "http://0.0.0.0:5000"  // Listen on all interfaces
         }
       }
     }
   }
   ```

4. Start services: `.\Start-All.ps1`

**Client Access:**

- WebClient: `http://<server-ip>:5000`
- Or distribute WebClient files and edit signaling URL

### Internet Deployment (Public Access)

**Use Case:** Remote access over internet

**⚠️ IMPORTANT SECURITY STEPS:**

1. **Use HTTPS/WSS:**
   - Configure reverse proxy (nginx/IIS)
   - Use valid SSL certificate (Let's Encrypt)
   - Change SignalingServer to wss:// in WebClient

2. **Authentication:**
   - Add authentication to SignalingServer
   - Implement password protection
   - Use VPN for additional security

3. **TURN Server:**
   - Deploy coturn for NAT traversal
   - Configure in appsettings.json

**Example nginx reverse proxy:**

```nginx
server {
    listen 443 ssl;
    server_name remote.example.com;

    ssl_certificate /etc/letsencrypt/live/remote.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/remote.example.com/privkey.pem;

    location / {
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
    }
}
```

### Docker Deployment (Advanced)

**Dockerfile (SignalingServer):**

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY SignalingServer/ .
EXPOSE 5000
ENTRYPOINT ["dotnet", "RemoteDesktop.SignalingServer.dll"]
```

**Note:** ScreenSenderApp requires Windows for DXGI Desktop Duplication - cannot run in Linux Docker.

---

## Updates and Maintenance

### Updating to New Version

**Process:**

1. **Backup Configuration:**
   ```powershell
   Copy-Item ScreenSenderApp\appsettings.json appsettings.backup.json
   Copy-Item SignalingServer\appsettings.json appsettings-server.backup.json
   ```

2. **Stop Services:**
   - Close all DeskShare windows
   - Or: Kill processes via Task Manager

3. **Replace Files:**
   - Delete old folder contents
   - Extract new version
   - Restore configuration files

4. **Restart Services:**
   ```powershell
   .\Start-All.ps1
   ```

**Zero-Downtime Update (Advanced):**

1. Run new version on different port
2. Redirect clients to new instance
3. Shut down old version

### Monitoring in Production

**1. Check Prometheus Metrics:**

Access: `http://localhost:9090/metrics`

Key metrics:
- `capture_fps` - Should be ~30 (or configured FPS)
- `webrtc_connections_active` - Current viewers
- `webrtc_packet_loss_percent` - Should be <5%
- `system_cpu_usage` - Should be <50%

**2. Check Logs:**

```
ScreenSenderApp/logs/screensender-*.log
SignalingServer/logs/server-*.log
```

**3. Health Checks:**

```powershell
# SignalingServer health
Invoke-WebRequest http://localhost:5000/health

# Metrics endpoint
Invoke-WebRequest http://localhost:9090/metrics
```

### Backup and Recovery

**What to Backup:**

```
✅ Configuration files (appsettings.json)
✅ Custom WebClient modifications
✅ Logs (if needed for debugging)
❌ Executables (can be re-downloaded)
❌ Dependencies (included in package)
```

**Backup Script:**

```powershell
$backupDir = "backup-$(Get-Date -Format 'yyyy-MM-dd')"
New-Item -ItemType Directory -Path $backupDir

Copy-Item ScreenSenderApp\appsettings.json "$backupDir\screensender-config.json"
Copy-Item SignalingServer\appsettings.json "$backupDir\signaling-config.json"
Copy-Item ScreenSenderApp\logs "$backupDir\logs" -Recurse -ErrorAction SilentlyContinue

Compress-Archive -Path $backupDir -DestinationPath "$backupDir.zip"
```

---

## Troubleshooting Deployment

### Port Already in Use

**Error:** "Address already in use: http://0.0.0.0:5000"

**Solution:**

1. Find process using port:
   ```powershell
   netstat -ano | findstr :5000
   ```

2. Kill process or change port in appsettings.json:
   ```json
   {
     "Kestrel": {
       "Endpoints": {
         "Http": {
           "Url": "http://0.0.0.0:5001"  // Use different port
         }
       }
     }
   }
   ```

### Firewall Blocking

**Symptom:** Clients cannot connect from other machines

**Solution:**

```powershell
# Allow SignalingServer
New-NetFirewallRule -DisplayName "RemoteDesktop SignalingServer" `
  -Direction Inbound -Protocol TCP -LocalPort 5000 -Action Allow

# Allow Metrics
New-NetFirewallRule -DisplayName "RemoteDesktop Metrics" `
  -Direction Inbound -Protocol TCP -LocalPort 9090 -Action Allow
```

### Missing Dependencies

**Error:** "DLL not found" or "Native library not loaded"

**Cause:** Incomplete extraction or corrupt download

**Solution:**

1. Re-download ZIP
2. Verify ZIP integrity (check file size)
3. Extract to new location
4. Ensure `vpxmd.dll` is in ScreenSenderApp folder

### Permission Issues

**Error:** "Access denied" when starting

**Solution:**

1. Run as Administrator (first time only for firewall)
2. Or: Move to user directory (no admin needed):
   ```
   C:\Users\<username>\RemoteDesktop\
   ```

---

## Best Practices

### Development Environment

✅ **DO:**
- Use `demo.ps1` for quick testing
- Keep configuration in version control (example files)
- Test portable package before distribution

❌ **DON'T:**
- Commit build outputs to git
- Include absolute paths in configuration
- Hardcode credentials

### Production Environment

✅ **DO:**
- Use HTTPS/WSS for public deployments
- Monitor metrics regularly
- Keep logs for 30+ days
- Document configuration changes
- Test updates in staging first

❌ **DON'T:**
- Expose SignalingServer directly to internet without auth
- Use default ports in production
- Skip firewall configuration
- Ignore log warnings

---

## Appendix

### File Size Reference

| Component | Approximate Size |
|-----------|-----------------|
| ScreenSenderApp | ~25 MB |
| SignalingServer | ~15 MB |
| WebClient | <1 MB |
| Documentation | ~2 MB |
| **Total ZIP** | **~45 MB** |

### System Requirements

**Minimum:**
- Windows 10 (64-bit)
- 2 GB RAM
- Dual-core processor
- 100 MB disk space

**Recommended:**
- Windows 11 (64-bit)
- 4 GB RAM
- Quad-core processor
- 500 MB disk space
- Dedicated GPU (for better capture performance)

### Performance Benchmarks

| Resolution | FPS | CPU Usage | Memory | Bandwidth |
|------------|-----|-----------|---------|-----------|
| 720p | 15 | ~5% | ~150 MB | 1-2 Mbps |
| 1080p | 30 | ~10% | ~180 MB | 3-5 Mbps |
| 1080p | 60 | ~20% | ~220 MB | 6-8 Mbps |
| 1440p | 60 | ~35% | ~280 MB | 10-12 Mbps |

*(Measured on Intel i5-8250U with Intel UHD 620)*

---

**Last Updated:** November 2025
**Version:** 1.0
**Maintainer:** DeskShare Team
