# DeskShare - Quick Start Guide

## 🚀 Portable Version - No Installation Required!

This is a portable (standalone) version of DeskShare. Simply extract and run!

## Requirements

- **Operating System:** Windows 10 or Windows 11 (64-bit)
- **No .NET Runtime required** - everything is included!

## Quick Start

### Option 1: One-Click Start (Recommended)

1. Double-click **Start-All.ps1**
2. Wait for services to start (~5 seconds)
3. Open **WebClient\index-control.html** in your browser
4. Click "Connect" and enjoy!

### Option 2: Manual Start

1. Start SignalingServer:
   - Double-click **Start-SignalingServer.ps1**
   - Wait for "Now listening on: http://localhost:5000"

2. Start ScreenSenderApp:
   - Double-click **Start-ScreenSender.ps1**
   - Copy the Server ID from console output

3. Open WebClient:
   - Open **WebClient\index-control.html** in browser
   - Paste Server ID (or leave empty for auto-discovery)
   - Click "Connect"

## URLs

- **SignalingServer:** http://localhost:5000
- **WebClient:** Open WebClient\index-control.html
- **Demo Interface:** http://localhost:5000 (after starting SignalingServer)
- **Metrics (Prometheus):** http://localhost:9090/metrics

## Features

✅ Screen sharing via WebRTC (low latency)
✅ Remote mouse & keyboard control
✅ Multi-client support (broadcast to multiple viewers)
✅ Real-time statistics (FPS, bitrate, latency)
✅ Adaptive quality based on network conditions
✅ Comprehensive monitoring (OpenTelemetry/Prometheus)

## Configuration

Edit **ScreenSenderApp\appsettings.json** to customize:

- Video quality (resolution, FPS, bitrate)
- Capture settings (SIMD optimizations)
- Remote control settings
- OpenTelemetry metrics

Edit **SignalingServer\appsettings.json** to change:

- Server port (default: 5000)
- Logging levels

## Documentation

See **docs/** folder for comprehensive guides:

- **run-guide.md** - Detailed setup and configuration
- **remote-control-guide.md** - Remote control usage
- **troubleshooting.md** - Common issues and solutions
- **security-best-practices.md** - Production deployment
- **telemetry-guide.md** - Monitoring with Grafana/Prometheus

## Troubleshooting

### Cannot Connect

1. Check firewall - allow port 5000
2. Verify SignalingServer is running (green text in console)
3. Try different browser (Chrome/Edge recommended)

### Poor Video Quality

1. Edit ScreenSenderApp\appsettings.json
2. Change "Preset" to "High" or "Ultra"
3. Restart ScreenSenderApp

### High CPU Usage

1. Edit ScreenSenderApp\appsettings.json
2. Reduce "TargetFps" (e.g., 15 or 30 instead of 60)
3. Change "ConverterType" to "Basic"

## Support

- GitHub Issues: https://github.com/your-username/DeskShare/issues
- Documentation: See docs/ folder

## Version

DeskShare v{{VERSION}}
Build Date: {{BUILD_DATE}}

---

**Enjoy DeskShare!** 🚀
