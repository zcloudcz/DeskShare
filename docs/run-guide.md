# Run Guide - DeskShare

Complete guide to running, configuring and debugging the DeskShare application.

## 📋 Contents

1. [Requirements](#1-requirements)
2. [Installation and Build](#2-installation-and-build)
3. [Running the components](#3-running-the-components)
4. [Configuration](#4-configuration)
5. [Debugging](#5-debugging)
6. [Troubleshooting](#6-troubleshooting)
7. [Performance tuning](#7-performance-tuning)

## 1. Requirements

### System requirements

#### ScreenSenderApp (Sender)

- **OS**: Windows 10 (1903+) or Windows 11
- **CPU**: Intel/AMD 4+ cores, 2.5+ GHz
- **RAM**: 4GB+ (8GB recommended)
- **GPU**: DirectX 11 compatible (Desktop Duplication API)
- **.NET**: .NET 8 SDK or newer

#### SignalingServer

- **OS**: Windows, Linux, macOS (cross-platform)
- **RAM**: 512MB+
- **.NET**: .NET 8 Runtime

#### WebClient

- **Browser**: Chrome 90+, Edge 90+, Firefox 88+, Safari 14.1+
- **WebRTC**: Must be supported (all modern browsers)

### Software requirements

```bash
# Verify .NET SDK
dotnet --version
# Expected: 8.0.x or newer

# Verify Git
git --version

# Optional: Docker for coturn
docker --version
```

## 2. Installation and Build

### 2.1 Clone repository

```bash
git clone https://github.com/your-username/DeskShare.git
cd DeskShare
```

### 2.2 Restore dependencies

```bash
dotnet restore
```

### 2.3 Build the whole solution

```bash
# Debug build
dotnet build

# Release build (optimized)
dotnet build -c Release
```

### 2.4 Verifying the build

```bash
dotnet build --no-incremental
```

Expected output:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## 3. Running the components

### 3.1 Running SignalingServer

#### Development

```bash
cd src/SignalingServer
dotnet run
```

Output:
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

#### Production

```bash
cd src/SignalingServer
dotnet run -c Release --urls "https://0.0.0.0:443"
```

**Note**: HTTPS in production requires a certificate:

```bash
# Generate a dev certificate
dotnet dev-certs https --trust

# Or use your own certificate in appsettings.json
```

### 3.2 Running WebClient

WebClient is a static HTML/JS application. It can be run in several ways:

#### Option 1: Simple HTTP server (Python)

```bash
cd src/WebClient
python -m http.server 8080
```

Open: `http://localhost:8080`

#### Option 2: VS Code Live Server

1. Install the "Live Server" extension
2. Open `src/WebClient/index.html`
3. Click "Go Live" in the status bar

#### Option 3: Directly in the browser

Open `file:///C:/GIT/DeskShare/src/WebClient/index.html` in Chrome/Edge.

⚠️ **WebSocket may require an HTTP server (not file://).**

### 3.3 Running ScreenSenderApp

⚠️ **IMPORTANT**: WebRTC integration is not implemented yet. The following is the planned procedure:

```bash
cd src/ScreenSenderApp
dotnet run

# Or with parameters
dotnet run -- --adapter 0 --output 0 --fps 30
```

**Expected parameters** (future):

- `--adapter <N>` - Graphics adapter index (0 = primary)
- `--output <N>` - Monitor index (0 = primary)
- `--fps <N>` - Target framerate (default: 30)
- `--signaling <URL>` - Signaling server URL

### 3.4 Docker setup for coturn (TURN server)

```bash
cd docker/coturn

# Create docker-compose.yml (see below)
docker-compose up -d
```

**docker-compose.yml** (create):

```yaml
version: '3.8'
services:
  coturn:
    image: coturn/coturn:latest
    container_name: coturn
    network_mode: host
    volumes:
      - ./turnserver.conf:/etc/turnserver.conf:ro
    restart: unless-stopped
```

**turnserver.conf** (create):

```ini
# TURN server config
listening-port=3478
tls-listening-port=5349
external-ip=YOUR_PUBLIC_IP
realm=DeskShare
server-name=DeskShare

# Authentication
user=testuser:testpassword

# Relay settings
min-port=49152
max-port=65535

# Logging
verbose
log-file=/var/log/turnserver.log
```

## 4. Configuration

### 4.1 SignalingServer configuration

**appsettings.json** (optional):

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5000"
      }
    }
  }
}
```

**Environment variables**:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://localhost:5000"
```

### 4.2 WebClient configuration

Edit `src/WebClient/client.js`:

```javascript
const config = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun1.l.google.com:19302' },
        // For production add TURN:
        {
            urls: 'turn:your-turn-server.com:3478',
            username: 'testuser',
            credential: 'testpassword'
        }
    ]
};
```

Edit the default signaling URL in `index.html`:

```html
<input type="text" id="signalingUrl" value="wss://your-server.com/signal" />
```

### 4.3 ScreenSenderApp configuration (future)

**appsettings.json**:

```json
{
  "Capture": {
    "AdapterIndex": 0,
    "OutputIndex": 0,
    "TargetFps": 30,
    "TargetWidth": 1920,
    "TargetHeight": 1080
  },
  "Signaling": {
    "ServerUrl": "ws://localhost:5000/signal"
  },
  "WebRTC": {
    "IceServers": [
      { "Urls": ["stun:stun.l.google.com:19302"] }
    ]
  }
}
```

## 5. Debugging

### 5.1 Visual Studio 2022

1. Open `DeskShare.sln`
2. Set the startup projects:
   - Right-click Solution -> Properties
   - Multiple startup projects
   - SignalingServer: Start
   - ScreenSenderApp: Start
3. F5 to debug

### 5.2 VS Code

**.vscode/launch.json**:

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "SignalingServer",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/src/SignalingServer/bin/Debug/net8.0/RemoteDesktop.SignalingServer.dll",
      "cwd": "${workspaceFolder}/src/SignalingServer",
      "stopAtEntry": false
    },
    {
      "name": "ScreenSenderApp",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/src/ScreenSenderApp/bin/Debug/net8.0/RemoteDesktop.ScreenSenderApp.dll",
      "cwd": "${workspaceFolder}/src/ScreenSenderApp",
      "stopAtEntry": false
    }
  ]
}
```

### 5.3 Logging

#### SignalingServer logs

Logs to the console by default. For file logging add to `appsettings.json`:

```json
{
  "Serilog": {
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": { "path": "logs/signaling-.log", "rollingInterval": "Day" }
      }
    ]
  }
}
```

#### Browser console (WebClient)

1. Open DevTools (F12)
2. Console tab
3. Watch the WebSocket messages and WebRTC events

```javascript
// Enable verbose logging
localStorage.debug = '*';
```

### 5.4 Network debugging

#### Wireshark for WebRTC

1. Start Wireshark
2. Filter: `udp.port == 49152:65535` (RTP packets)
3. Analyze -> Decode As -> RTP

#### Chrome WebRTC internals

1. Open `chrome://webrtc-internals`
2. Watch:
   - PeerConnection stats
   - ICE candidates
   - Bitrate graphs
   - Packet loss

## 6. Troubleshooting

### 6.1 DesktopDuplicator fails

**Symptom**: `Initialize()` returns `false`

**Possible causes**:

1. **No GPU/adapter**
   ```
   Solution: Check Device Manager -> Display adapters
   ```

2. **Desktop Duplication is not supported**
   ```
   Solution: Update the graphics drivers
   Minimum: Windows 10 1903+
   ```

3. **The application runs as a service (Session 0)**
   ```
   Solution: Desktop Duplication requires an interactive session (Session 1+)
   ```

### 6.2 WebSocket connection failed

**Symptom**: WebClient reports "WebSocket error"

**Solution**:

1. Verify that SignalingServer is running:
   ```bash
   curl http://localhost:5000/health
   ```

2. Check the CORS policy
   ```
   SignalingServer must have AllowAnyOrigin in the CORS policy
   ```

3. Firewall
   ```bash
   # Windows
   netsh advfirewall firewall add rule name="SignalingServer" dir=in action=allow protocol=TCP localport=5000
   ```

### 6.3 WebRTC connection stuck on "Connecting"

**Possible causes**:

1. **No ICE candidates**
   - Check chrome://webrtc-internals
   - `host`, `srflx` or `relay` candidates should be visible

2. **NAT/Firewall blocks UDP**
   - You need a TURN server as a fallback
   - Or set up port forwarding

3. **ICE candidates are not being exchanged**
   - Check the signaling logs
   - IceCandidate messages should have been exchanged

### 6.4 Performance issues

**Symptom**: Low FPS, high latency

**Diagnostics**:

```csharp
// Add to the capture loop
var sw = Stopwatch.StartNew();
capturer.TryAcquireFrame(out var frame);
Console.WriteLine($"Capture: {sw.ElapsedMilliseconds}ms");

converter.Convert(frame);
Console.WriteLine($"Convert: {sw.ElapsedMilliseconds}ms");
```

**Optimizations**:

1. Reduce the resolution (720p instead of 1080p)
2. Reduce FPS (20 instead of 30)
3. Build in Release mode
4. Close other GPU-intensive applications

## 7. Performance tuning

### 7.1 Capture optimization

```csharp
// Use a lower timeout for higher responsiveness
const int AcquireTimeoutMs = 50; // Default: 100

// Or a higher one for lower CPU usage
const int AcquireTimeoutMs = 200;
```

### 7.2 WebRTC bitrate control (future)

```javascript
// In WebClient
const offerOptions = {
    offerToReceiveVideo: true,
    offerToReceiveAudio: false
};

// Sender constraints
const constraints = {
    video: {
        width: { ideal: 1920 },
        height: { ideal: 1080 },
        frameRate: { ideal: 30 },
        // Bitrate control
        advanced: [{ maxBitrate: 2500000 }] // 2.5 Mbps
    }
};
```

### 7.3 Conversion optimization

**Build with aggressive inlining**:

```xml
<PropertyGroup>
  <Optimize>true</Optimize>
  <AggressiveInlining>true</AggressiveInlining>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

**CPU affinity** (restrict to specific cores):

```csharp
Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(0x0F); // Cores 0-3
```

### 7.4 Memory profiling

```bash
# Install dotnet-counters
dotnet tool install --global dotnet-counters

# Monitor memory
dotnet-counters monitor -p <PID> --counters System.Runtime

# Heap snapshot
dotnet-gcdump collect -p <PID>
```

---

**Next steps**:
- [Architecture Guide](architecture.md)
- [API Documentation](api.md) (TODO)
- [README](../README.md)
