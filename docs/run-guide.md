# Run Guide - DeskShare

Kompletní návod na spuštění, konfiguraci a debugging DeskShare aplikace.

## 📋 Obsah

1. [Požadavky](#1-požadavky)
2. [Instalace a Build](#2-instalace-a-build)
3. [Spuštění komponent](#3-spuštění-komponent)
4. [Konfigurace](#4-konfigurace)
5. [Debugging](#5-debugging)
6. [Troubleshooting](#6-troubleshooting)
7. [Performance tuning](#7-performance-tuning)

## 1. Požadavky

### Systémové požadavky

#### ScreenSenderApp (Sender)

- **OS**: Windows 10 (1903+) nebo Windows 11
- **CPU**: Intel/AMD 4+ cores, 2.5+ GHz
- **RAM**: 4GB+ (8GB doporučeno)
- **GPU**: DirectX 11 compatible (Desktop Duplication API)
- **.NET**: .NET 8 SDK nebo novější

#### SignalingServer

- **OS**: Windows, Linux, macOS (cross-platform)
- **RAM**: 512MB+
- **.NET**: .NET 8 Runtime

#### WebClient

- **Browser**: Chrome 90+, Edge 90+, Firefox 88+, Safari 14.1+
- **WebRTC**: Musí být podporováno (všechny moderní browsery)

### Software požadavky

```bash
# Ověření .NET SDK
dotnet --version
# Očekáváno: 8.0.x nebo novější

# Ověření Git
git --version

# Volitelné: Docker pro coturn
docker --version
```

## 2. Instalace a Build

### 2.1 Clone repository

```bash
git clone https://github.com/your-username/DeskShare.git
cd DeskShare
```

### 2.2 Restore dependencies

```bash
dotnet restore
```

### 2.3 Build celého solution

```bash
# Debug build
dotnet build

# Release build (optimalizované)
dotnet build -c Release
```

### 2.4 Ověření buildu

```bash
dotnet build --no-incremental
```

Očekávaný výstup:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## 3. Spuštění komponent

### 3.1 Spuštění SignalingServer

#### Vývoj (Development)

```bash
cd src/SignalingServer
dotnet run
```

Výstup:
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

#### Produkce (Production)

```bash
cd src/SignalingServer
dotnet run -c Release --urls "https://0.0.0.0:443"
```

**Poznámka**: Pro HTTPS v produkci je potřeba certifikát:

```bash
# Generovat dev certifikát
dotnet dev-certs https --trust

# Nebo použít vlastní certifikát v appsettings.json
```

### 3.2 Spuštění WebClient

WebClient je statická HTML/JS aplikace. Lze spustit několika způsoby:

#### Způsob 1: Jednoduchý HTTP server (Python)

```bash
cd src/WebClient
python -m http.server 8080
```

Otevřete: `http://localhost:8080`

#### Způsob 2: VS Code Live Server

1. Nainstalujte "Live Server" extension
2. Otevřete `src/WebClient/index.html`
3. Klikněte "Go Live" v status baru

#### Způsob 3: Přímo v browseru

Otevřete `file:///C:/GIT/DeskShare/src/WebClient/index.html` v Chrome/Edge.

⚠️ **WebSocket může vyžadovat HTTP server (ne file://).**

### 3.3 Spuštění ScreenSenderApp

⚠️ **DŮLEŽITÉ**: WebRTC integrace zatím není implementována. Následující je plánovaný postup:

```bash
cd src/ScreenSenderApp
dotnet run

# Nebo s parametry
dotnet run -- --adapter 0 --output 0 --fps 30
```

**Očekávané parametry** (budoucí):

- `--adapter <N>` - Index grafického adaptéru (0 = primární)
- `--output <N>` - Index monitoru (0 = primární)
- `--fps <N>` - Cílový framerate (default: 30)
- `--signaling <URL>` - URL signaling serveru

### 3.4 Docker setup pro coturn (TURN server)

```bash
cd docker/coturn

# Vytvořit docker-compose.yml (viz níže)
docker-compose up -d
```

**docker-compose.yml** (vytvořit):

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

**turnserver.conf** (vytvořit):

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

## 4. Konfigurace

### 4.1 SignalingServer konfigurace

**appsettings.json** (volitelné):

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

**Environment proměnné**:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://localhost:5000"
```

### 4.2 WebClient konfigurace

Upravte `src/WebClient/client.js`:

```javascript
const config = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun1.l.google.com:19302' },
        // Pro produkci přidat TURN:
        {
            urls: 'turn:your-turn-server.com:3478',
            username: 'testuser',
            credential: 'testpassword'
        }
    ]
};
```

Upravte default signaling URL v `index.html`:

```html
<input type="text" id="signalingUrl" value="wss://your-server.com/signal" />
```

### 4.3 ScreenSenderApp konfigurace (budoucí)

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

1. Otevřete `DeskShare.sln`
2. Nastavte startup projekty:
   - Right-click Solution -> Properties
   - Multiple startup projects
   - SignalingServer: Start
   - ScreenSenderApp: Start
3. F5 pro debug

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

Defaultně loguje do konzole. Pro file logging přidat do `appsettings.json`:

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

1. Otevřete DevTools (F12)
2. Console tab
3. Sledujte WebSocket messages a WebRTC events

```javascript
// Enable verbose logging
localStorage.debug = '*';
```

### 5.4 Network debugging

#### Wireshark pro WebRTC

1. Spusťte Wireshark
2. Filtr: `udp.port == 49152:65535` (RTP packets)
3. Analyze -> Decode As -> RTP

#### Chrome WebRTC internals

1. Otevřete `chrome://webrtc-internals`
2. Sledujte:
   - PeerConnection stats
   - ICE candidates
   - Bitrate graphs
   - Packet loss

## 6. Troubleshooting

### 6.1 DesktopDuplicator selhává

**Symptom**: `Initialize()` vrací `false`

**Možné příčiny**:

1. **Žádná GPU/adaptér**
   ```
   Solution: Zkontrolujte Device Manager -> Display adapters
   ```

2. **Desktop Duplication není podporováno**
   ```
   Solution: Aktualizujte grafické ovladače
   Minimum: Windows 10 1903+
   ```

3. **Aplikace běží jako service (Session 0)**
   ```
   Solution: Desktop Duplication vyžaduje interaktivní session (Session 1+)
   ```

### 6.2 WebSocket connection failed

**Symptom**: WebClient hlásí "WebSocket error"

**Řešení**:

1. Ověřte, že SignalingServer běží:
   ```bash
   curl http://localhost:5000/health
   ```

2. Zkontrolujte CORS policy
   ```
   SignalingServer musí mít AllowAnyOrigin v CORS policy
   ```

3. Firewall
   ```bash
   # Windows
   netsh advfirewall firewall add rule name="SignalingServer" dir=in action=allow protocol=TCP localport=5000
   ```

### 6.3 WebRTC connection stuck on "Connecting"

**Možné příčiny**:

1. **Žádné ICE candidates**
   - Zkontrolujte chrome://webrtc-internals
   - Měly by být viditelné `host`, `srflx` nebo `relay` candidates

2. **NAT/Firewall blokuje UDP**
   - Potřebujete TURN server pro fallback
   - Nebo nastavte port forwarding

3. **ICE candidates se nevyměňují**
   - Zkontrolujte signaling logs
   - Měly se vyměnit IceCandidate messages

### 6.4 Performance issues

**Symptom**: Nízký FPS, vysoká latence

**Diagnostika**:

```csharp
// Přidat do capture loop
var sw = Stopwatch.StartNew();
capturer.TryAcquireFrame(out var frame);
Console.WriteLine($"Capture: {sw.ElapsedMilliseconds}ms");

converter.Convert(frame);
Console.WriteLine($"Convert: {sw.ElapsedMilliseconds}ms");
```

**Optimalizace**:

1. Snížit rozlišení (720p místo 1080p)
2. Snížit FPS (20 místo 30)
3. Build v Release mode
4. Zavřít ostatní GPU-intensive aplikace

## 7. Performance tuning

### 7.1 Capture optimalizace

```csharp
// Použít nižší timeout pro vyšší responsiveness
const int AcquireTimeoutMs = 50; // Default: 100

// Nebo vyšší pro nižší CPU usage
const int AcquireTimeoutMs = 200;
```

### 7.2 WebRTC bitrate control (budoucí)

```javascript
// V WebClient
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

### 7.3 Conversion optimalizace

**Build s aggressive inlining**:

```xml
<PropertyGroup>
  <Optimize>true</Optimize>
  <AggressiveInlining>true</AggressiveInlining>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

**CPU affinity** (omezit na specifické cores):

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

**Další kroky**:
- [Architecture Guide](architecture.md)
- [API Documentation](api.md) (TODO)
- [README](../README.md)
