# Architektura DeskShare

## 1. Přehled architektury

DeskShare je postavena na **čisté architektuře** s důrazem na:

- **Separation of Concerns** - každá komponenta má jasně definovanou zodpovědnost
- **Dependency Injection** - všechny závislosti jsou explicitní a testovatelné
- **Interface-based design** - možnost pluginů a mockování pro testy
- **Performance-first** - kritické sekce optimalizovány (unsafe code, SIMD-ready)

## 2. Komponenty systému

```
┌─────────────────────────────────────────────────────────────┐
│                        User Interface                        │
│  (ScreenSenderApp Desktop UI / WebClient Browser)           │
└───────────────────┬─────────────────────────────────────────┘
                    │
┌───────────────────┴─────────────────────────────────────────┐
│                    Application Layer                         │
│  - Capture Pipeline Management                               │
│  - WebRTC Session Management                                 │
│  - Input Control Authorization                               │
└───────────────────┬─────────────────────────────────────────┘
                    │
┌───────────────────┴─────────────────────────────────────────┐
│                    Core Interfaces                           │
│  ICapturer | IFrameConverter | IVideoSource | ISignaler     │
└─────┬─────────┬──────────┬────────────┬────────────────────┘
      │         │          │            │
┌─────▼───┐ ┌──▼──────┐ ┌─▼──────┐  ┌─▼────────────┐
│Desktop  │ │Pixel    │ │WebRTC  │  │WebSocket     │
│Duplicator│ │Converter│ │Source  │  │Signaler      │
└─────────┘ └─────────┘ └────────┘  └──────────────┘
      │                       │              │
┌─────▼───────────────────────▼──────────────▼─────────┐
│              Platform Services                        │
│  DXGI | D3D11 | WebRTC Native | Network Stack        │
└───────────────────────────────────────────────────────┘
```

## 3. Capture Pipeline

### 3.1 DesktopDuplicator (ICapturer)

**Zodpovědnost**: Efektivní zachytávání obrazovky pomocí DXGI Desktop Duplication API.

**Implementační detaily**:

```
1. Initialize()
   ├─ Enumerate DXGI adapters & outputs
   ├─ Create D3D11 device
   ├─ Create IDXGIOutputDuplication
   └─ Create staging texture (CPU-accessible)

2. TryAcquireFrame()
   ├─ AcquireNextFrame() with timeout (100ms)
   ├─ Copy to staging texture
   ├─ Map staging texture to CPU memory
   └─ Return Frame (BGRA format)

3. ReleaseFrame()
   └─ Release DXGI frame for next capture
```

**Performance charakteristiky**:
- ⚡ GPU-based copy (near zero CPU overhead)
- 🔄 Only captures on screen changes (event-driven)
- 📦 Direct memory mapping (no extra allocations)

**Optimalizace**:
- Staging texture reuse (no per-frame allocation)
- Timeout handling pro throttling
- Fallback při chybách (reinitializace)

### 3.2 PixelConverter (IFrameConverter)

**Zodpovědnost**: Konverze BGRA (32bpp) -> I420 (YUV 4:2:0 planar).

**Algoritmus**: ITU-R BT.601 standard

```
Y = ( 66R + 129G +  25B + 128) >> 8 + 16
U = (-38R -  74G + 112B + 128) >> 8 + 128
V = (112R -  94G -  18B + 128) >> 8 + 128
```

**Implementace**:
- ✅ Unsafe pointer arithmetic pro přímý memory access
- ✅ Fixed-point matematika místo floating-point
- ✅ 2x2 subsampling pro U/V plány (4:2:0)
- 🔜 SIMD vectorization (AVX2) - Fáze 2

**Performance profil**:
- 1080p frame (~8MB BGRA): ~5-10ms conversion time
- Cíl: <3ms s SIMD optimalizací

## 4. Signaling architektura

### 4.1 SignalingServer (ASP.NET Core)

**Zodpovědnost**: WebSocket relay pro SDP/ICE zprávy mezi peers.

**Endpoints**:
- `GET /` - Info endpoint
- `GET /health` - Health check s počtem spojení
- `WS /signal` - WebSocket signaling endpoint

**Message routing**:

```
Client A                SignalingServer              Client B
   │                           │                         │
   ├─── Connect (WS) ─────────>│                         │
   │<── Identify {clientId} ────┤                         │
   │                           │<─── Connect (WS) ───────┤
   │                           ├─── Identify {clientId} ─>│
   │                           │                         │
   ├─── Offer {targetId: B} ──>│                         │
   │                           ├─── Offer {from: A} ─────>│
   │                           │<─── Answer {targetId: A}─┤
   │<── Answer {from: B} ───────┤                         │
   │                           │                         │
   ├─── IceCandidate ─────────>│─── IceCandidate ───────>│
   │<── IceCandidate ───────────┤<─── IceCandidate ───────┤
```

**ConnectionManager**:
- `ConcurrentDictionary<string, WebSocket>` pro thread-safe routing
- Automatic cleanup při disconnect
- Broadcast support (pro multi-viewer budoucnost)

## 5. WebRTC pipeline (Plánováno)

### 5.1 VideoSource (IVideoSource)

**Zodpovědnost**: Push I420 frames do WebRTC peer connection.

**Komponenty**:

```
IVideoSource
    ├─ ExternalVideoTrackSource (WebRTC native)
    ├─ PeerConnection management
    ├─ Backpressure handling
    └─ Statistics tracking
```

**Backpressure strategie**:
1. Token bucket algorithm pro frame rate limiting
2. Drop frames když WebRTC buffer je plný
3. Emit statistics pro adaptive bitrate

### 5.2 PeerConnection lifecycle

```
1. Create RTCPeerConnection
   ├─ Add video track (from ExternalVideoTrackSource)
   ├─ Setup ICE candidate callback
   └─ Setup connection state callbacks

2. Signaling phase
   ├─ Create offer
   ├─ Set local description
   ├─ Send offer via signaling
   ├─ Receive answer
   └─ Set remote description

3. ICE gathering
   ├─ Gather ICE candidates
   ├─ Exchange candidates via signaling
   └─ Establish connection

4. Media streaming
   ├─ Push frames via PushFrame()
   └─ Monitor RTCP stats for congestion

5. Teardown
   ├─ Stop frame pushing
   └─ Close peer connection
```

## 6. Threading model

### 6.1 ScreenSenderApp threads

```
Main Thread (UI)
    │
    ├─ Capture Thread (Producer)
    │  └─ Loop: TryAcquireFrame -> Queue
    │
    ├─ Conversion Thread (Transform)
    │  └─ Loop: Dequeue BGRA -> Convert I420 -> Queue
    │
    └─ WebRTC Thread (Consumer)
       └─ Loop: Dequeue I420 -> PushFrame
```

**Synchronizace**:
- `BlockingCollection<T>` pro producer/consumer queues
- Bounded capacity pro backpressure
- CancellationToken pro graceful shutdown

### 6.2 SignalingServer threads

- **Kestrel thread pool** pro HTTP/WebSocket handling
- **ConnectionManager** je thread-safe (ConcurrentDictionary)
- Async/await pattern všude

## 7. Error handling strategie

### 7.1 DesktopDuplicator

```csharp
try
{
    // Acquire frame
}
catch (SharpGenException ex) when (ex.Result == DXGI_ERROR_ACCESS_LOST)
{
    // Display mode changed, reinitialize
    Reinitialize();
}
catch (SharpGenException ex) when (ex.Result == DXGI_ERROR_WAIT_TIMEOUT)
{
    // No screen updates, continue
    return false;
}
```

### 7.2 SignalingServer

- **Graceful degradation** - jeden broken connection neovlivní ostatní
- **Structured logging** (Serilog) pro diagnostiku
- **Health endpoint** pro monitoring

### 7.3 WebRTC

- **ICE restart** při connection failure
- **Automatic reconnection** s exponential backoff
- **RTCP feedback** pro congestion control

## 8. Bezpečnostní architektura

### 8.1 Signaling security

```
Development: ws://localhost:5000
Production:  wss://server.com:443 (WSS only)
```

**Authentication** (Fáze 3):
- JWT tokens pro client identification
- Certificate pinning pro WSS
- Rate limiting per IP

### 8.2 Media security

- **DTLS** - TLS pro DataChannel
- **SRTP** - Encrypted RTP pro video/audio
- **Automatic** - WebRTC handles natively

### 8.3 Input control security

```
User clicks "Allow Remote Control"
    ├─ Show explicit consent dialog
    ├─ Log authorization event (timestamp, IP, user)
    ├─ Enable input processing
    │
    ├─ For each input command:
    │  ├─ Validate command (whitelist, range checks)
    │  ├─ Rate limit (max 100 inputs/second)
    │  └─ Apply input
    │
    └─ User revokes OR timeout (5min)
       └─ Disable input processing
```

## 9. Testovací strategie

### 9.1 Unit testy

- **PixelConverter**: Validate známé RGB->YUV konverze
- **ConnectionManager**: Mock WebSocket, verify routing
- **SignalingMessage**: Serialization/deserialization

### 9.2 Integrační testy

- **E2E capture pipeline**: DesktopDuplicator -> PixelConverter
- **Signaling flow**: Two mock clients exchange messages
- **Performance benchmarks**: Conversion speed, memory usage

### 9.3 Smoke testy

- **DesktopDuplicator.Initialize()** nehodí výjimku
- **SignalingServer** odpovídá na /health
- **WebClient** načte bez errors

## 10. Deployment architektura

```
┌────────────────────────────────────────────────────┐
│                   Load Balancer                    │
│              (HTTPS/WSS termination)               │
└─────────────┬──────────────────────────────────────┘
              │
    ┌─────────┴─────────┐
    │                   │
┌───▼────────┐   ┌──────▼──────┐
│ Signaling  │   │  Signaling  │
│ Server 1   │   │  Server 2   │
└────────────┘   └─────────────┘
                       │
              ┌────────┴─────────┐
              │                  │
         ┌────▼────┐      ┌──────▼──────┐
         │  TURN   │      │    TURN     │
         │Server 1 │      │  Server 2   │
         └─────────┘      └─────────────┘
```

**Komponenty**:
- **Nginx/Traefik**: Reverse proxy + TLS
- **SignalingServer**: Stateless, horizontálně škálovatelný
- **coturn**: TURN/STUN servers pro NAT traversal
- **Redis** (budoucí): Shared state pro multi-instance signaling

## 11. Monitoring & Observability

### 11.1 Metriky (OpenTelemetry - Fáze 4)

- Capture FPS, latence, dropped frames
- WebRTC bitrate, packet loss, RTT
- Signaling connections, messages/sec

### 11.2 Logging (Serilog)

```csharp
_logger.LogInformation("Client {ClientId} connected from {IP}",
    clientId, remoteIp);

_logger.LogWarning("Frame dropped, queue full. Backpressure active.");

_logger.LogError(ex, "Failed to acquire frame, reinitializing capturer");
```

### 11.3 Health checks

```
GET /health
{
    "status": "Healthy",
    "connections": 42,
    "timestamp": "2025-10-29T19:00:00Z"
}
```

---

**Next**: [Run Guide](run-guide.md) | [README](../README.md)
