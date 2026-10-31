# DeskShare Architecture

## 1. Architecture overview

DeskShare is built on a **clean architecture** with an emphasis on:

- **Separation of Concerns** - each component has a clearly defined responsibility
- **Dependency Injection** - all dependencies are explicit and testable
- **Interface-based design** - support for plugins and mocking in tests
- **Performance-first** - critical sections are optimized (unsafe code, SIMD-ready)

## 2. System components

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

**Responsibility**: Efficient screen capture using the DXGI Desktop Duplication API.

**Implementation details**:

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

**Performance characteristics**:
- ⚡ GPU-based copy (near zero CPU overhead)
- 🔄 Only captures on screen changes (event-driven)
- 📦 Direct memory mapping (no extra allocations)

**Optimizations**:
- Staging texture reuse (no per-frame allocation)
- Timeout handling for throttling
- Fallback on errors (reinitialization)

### 3.2 PixelConverter (IFrameConverter)

**Responsibility**: Conversion of BGRA (32bpp) -> I420 (YUV 4:2:0 planar).

**Algorithm**: ITU-R BT.601 standard

```
Y = ( 66R + 129G +  25B + 128) >> 8 + 16
U = (-38R -  74G + 112B + 128) >> 8 + 128
V = (112R -  94G -  18B + 128) >> 8 + 128
```

**Implementation**:
- ✅ Unsafe pointer arithmetic for direct memory access
- ✅ Fixed-point math instead of floating-point
- ✅ 2x2 subsampling for the U/V planes (4:2:0)
- 🔜 SIMD vectorization (AVX2) - Phase 2

**Performance profile**:
- 1080p frame (~8MB BGRA): ~5-10ms conversion time
- Target: <3ms with SIMD optimization

## 4. Signaling architecture

### 4.1 SignalingServer (ASP.NET Core)

**Responsibility**: WebSocket relay for SDP/ICE messages between peers.

**Endpoints**:
- `GET /` - Info endpoint
- `GET /health` - Health check with the connection count
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
- `ConcurrentDictionary<string, WebSocket>` for thread-safe routing
- Automatic cleanup on disconnect
- Broadcast support (for a multi-viewer future)

## 5. WebRTC pipeline (Planned)

### 5.1 VideoSource (IVideoSource)

**Responsibility**: Push I420 frames into the WebRTC peer connection.

**Components**:

```
IVideoSource
    ├─ ExternalVideoTrackSource (WebRTC native)
    ├─ PeerConnection management
    ├─ Backpressure handling
    └─ Statistics tracking
```

**Backpressure strategy**:
1. Token bucket algorithm for frame rate limiting
2. Drop frames when the WebRTC buffer is full
3. Emit statistics for adaptive bitrate

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

**Synchronization**:
- `BlockingCollection<T>` for producer/consumer queues
- Bounded capacity for backpressure
- CancellationToken for graceful shutdown

### 6.2 SignalingServer threads

- **Kestrel thread pool** for HTTP/WebSocket handling
- **ConnectionManager** is thread-safe (ConcurrentDictionary)
- Async/await pattern everywhere

## 7. Error handling strategy

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

- **Graceful degradation** - one broken connection does not affect the others
- **Structured logging** (Serilog) for diagnostics
- **Health endpoint** for monitoring

### 7.3 WebRTC

- **ICE restart** on connection failure
- **Automatic reconnection** with exponential backoff
- **RTCP feedback** for congestion control

## 8. Security architecture

### 8.1 Signaling security

```
Development: ws://localhost:5000
Production:  wss://server.com:443 (WSS only)
```

**Authentication** (Phase 3):
- JWT tokens for client identification
- Certificate pinning for WSS
- Rate limiting per IP

### 8.2 Media security

- **DTLS** - TLS for DataChannel
- **SRTP** - Encrypted RTP for video/audio
- **Automatic** - WebRTC handles it natively

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

## 9. Testing strategy

### 9.1 Unit tests

- **PixelConverter**: Validate known RGB->YUV conversions
- **ConnectionManager**: Mock WebSocket, verify routing
- **SignalingMessage**: Serialization/deserialization

### 9.2 Integration tests

- **E2E capture pipeline**: DesktopDuplicator -> PixelConverter
- **Signaling flow**: Two mock clients exchange messages
- **Performance benchmarks**: Conversion speed, memory usage

### 9.3 Smoke tests

- **DesktopDuplicator.Initialize()** does not throw an exception
- **SignalingServer** responds to /health
- **WebClient** loads without errors

## 10. Deployment architecture

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

**Components**:
- **Nginx/Traefik**: Reverse proxy + TLS
- **SignalingServer**: Stateless, horizontally scalable
- **coturn**: TURN/STUN servers for NAT traversal
- **Redis** (future): Shared state for multi-instance signaling

## 11. Monitoring & Observability

### 11.1 Metrics (OpenTelemetry - Phase 4)

- Capture FPS, latency, dropped frames
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
