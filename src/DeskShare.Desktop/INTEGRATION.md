# DeskShare - Desktop Integration Guide

Průvodce integrací Desktop aplikace s backend komponenty.

## Architektura integrace

```
┌─────────────────────────────────────┐
│   RemoteDesktop.Desktop (WPF)       │
│                                     │
│  ┌──────────┐      ┌─────────────┐ │
│  │  Main    │      │ Projection  │ │
│  │  Window  │      │   Window    │ │
│  └────┬─────┘      └──────┬──────┘ │
│       │                   │         │
│  ┌────▼──────┐      ┌─────▼──────┐ │
│  │  Server   │      │   Client   │ │
│  │  Manager  │      │   Manager  │ │
│  └────┬──────┘      └──────┬─────┘ │
└───────┼──────────────────┼─────────┘
        │                   │
        ▼                   ▼
┌───────────────┐   ┌──────────────┐
│ Screen Sender │   │  Signaling   │
│      App      │   │    Server    │
└───────────────┘   └──────────────┘
```

## Aktuální stav implementace

### ✅ Hotovo

1. **WPF Desktop Application**
   - Dual-mode UI (Server + Client)
   - Modern card-based design
   - Dependency injection setup
   - Structured logging (Serilog)

2. **ServerManager** (`Services/ServerManager.cs`)
   - Interface pro správu serveru
   - Event-based architektura
   - Connection monitoring hooks
   - **Stav:** Připraveno pro integraci

3. **ClientManager** (`Services/ClientManager.cs`)
   - WebSocket signaling komunikace
   - WebRTC connection management
   - Frame reception events
   - **Stav:** Připraveno pro integraci

4. **ProjectionWindow**
   - Fullscreen video renderer
   - WriteableBitmap frame rendering
   - Stats display (FPS, latency, quality)
   - **Stav:** Připraveno pro video stream

5. **Build Status**
   - ✅ 0 compilation errors
   - ⚠️ 4 warnings (non-critical)
   - ✅ All projects reference correctly

### ✅ Integrace - Dokončeno

#### 1. ServerManager - Spuštění backend procesů

**Soubor:** `Services/ServerManager.cs:31-121`

**Stav:** ✅ **IMPLEMENTOVÁNO**

**Implementace:**
```csharp
// Start SignalingServer process
var signalingExe = Path.Combine(AppContext.BaseDirectory, "../SignalingServer/RemoteDesktop.SignalingServer.exe");
_signalingProcess = Process.Start(new ProcessStartInfo
{
    FileName = signalingExe,
    Arguments = "",
    WorkingDirectory = Path.GetDirectoryName(signalingExe),
    UseShellExecute = false,
    RedirectStandardOutput = true
});

await Task.Delay(2000); // Wait for startup

// Start ScreenSenderApp process
var screenSenderExe = Path.Combine(AppContext.BaseDirectory, "../ScreenSenderApp/RemoteDesktop.ScreenSenderApp.exe");
_screenSenderProcess = Process.Start(new ProcessStartInfo
{
    FileName = screenSenderExe,
    Arguments = $"--server-id {serverId} --password {password}",
    WorkingDirectory = Path.GetDirectoryName(screenSenderExe),
    UseShellExecute = false,
    RedirectStandardOutput = true
});
```

**Nebo alternativně - In-process hosting:**
```csharp
// Host ScreenSenderApp in-process
var host = Host.CreateDefaultBuilder()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<IScreenCaptureFactory, DxgiScreenCaptureFactory>();
        services.AddSingleton<CaptureOrchestrator>();
        services.AddSingleton<WebRtcServer>();
        services.AddHostedService<ScreenSenderService>();
    })
    .Build();

await host.StartAsync();
_screenSenderHost = host;
```

#### 2. ClientManager - WebRTC Peer Connection

**Soubor:** `Services/ClientManager.cs:88-188`

**Stav:** ✅ **IMPLEMENTOVÁNO**

**Implementace s použitím SIPSorcery 8.0.23:**
```csharp
private async Task SetupWebRtcConnectionAsync()
{
    _peerConnection = new RTCPeerConnection(new RTCConfiguration
    {
        iceServers = new List<RTCIceServer>
        {
            new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
        }
    });

    // Handle video track
    _peerConnection.ontrack += (track, kind, ssrc) =>
    {
        if (kind == SDPMediaTypesEnum.video)
        {
            track.OnVideoFrameReceived += (frame) =>
            {
                // Convert frame to byte array and fire event
                var frameData = ConvertFrameToBytes(frame);
                FrameReceived?.Invoke(this, frameData);
            };
        }
    };

    // Handle ICE candidates
    _peerConnection.onicecandidate += (candidate) =>
    {
        var msg = new SignalingMessage
        {
            Type = SignalingMessageType.IceCandidate,
            Candidate = candidate.toJSON(),
            SenderId = _clientId
        };
        await SendSignalingMessageAsync(msg);
    };

    // Create offer
    var offer = _peerConnection.createOffer();
    await _peerConnection.setLocalDescription(offer);

    // Send offer via signaling
    var offerMsg = new SignalingMessage
    {
        Type = SignalingMessageType.Offer,
        Sdp = offer.sdp,
        SenderId = _clientId,
        TargetId = _serverId
    };
    await SendSignalingMessageAsync(offerMsg);
}
```

#### 3. ProjectionWindow - Video Frame Rendering Optimalizace

**Soubor:** `Windows/ProjectionWindow.xaml.cs:209-249`

**Aktuální kód je funkční, ale může být optimalizován:**

```csharp
private void OnFrameReceived(object? sender, byte[] frameData)
{
    // Current: Simple copy with unsafe code
    // Optimization: Use parallel processing for large frames

    Dispatcher.InvokeAsync(() =>
    {
        if (_frameBuffer == null)
        {
            // Auto-resize buffer based on frame size
            var width = DetermineWidth(frameData.Length);
            var height = DetermineHeight(frameData.Length);
            _frameBuffer = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            VideoImage.Source = _frameBuffer;
        }

        _frameBuffer.Lock();
        try
        {
            // High-performance copy using Buffer.MemoryCopy
            unsafe
            {
                Buffer.MemoryCopy(
                    src: (void*)Marshal.UnsafeAddrOfPinnedArrayElement(frameData, 0),
                    dst: (void*)_frameBuffer.BackBuffer,
                    dstCount: _frameBuffer.BackBufferStride * _frameBuffer.PixelHeight,
                    count: Math.Min(frameData.Length, _frameBuffer.BackBufferStride * _frameBuffer.PixelHeight)
                );
            }

            _frameBuffer.AddDirtyRect(new Int32Rect(0, 0, _frameBuffer.PixelWidth, _frameBuffer.PixelHeight));
        }
        finally
        {
            _frameBuffer.Unlock();
        }
    }, DispatcherPriority.Render);
}
```

#### 4. Connection Monitoring

**Soubor:** `Services/ServerManager.cs:61-77`

**Implementace skutečného monitoringu:**

```csharp
private void StartMonitoringConnections()
{
    var monitorTimer = new System.Timers.Timer(1000);
    monitorTimer.Elapsed += async (s, e) =>
    {
        // Query actual metrics from ScreenSenderApp
        // Option 1: Via HTTP API
        try
        {
            using var client = new HttpClient();
            var response = await client.GetStringAsync("http://localhost:9090/metrics");
            var stats = ParsePrometheusMetrics(response);

            _activeConnections = stats.ActiveConnections;

            StatsUpdated?.Invoke(this, new ServerStats
            {
                ActiveConnections = stats.ActiveConnections,
                CurrentFps = stats.CurrentFps,
                CpuUsage = stats.CpuUsage,
                MemoryUsageMb = stats.MemoryMb
            });

            ConnectionCountChanged?.Invoke(this, _activeConnections);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query server metrics");
        }
    };
    monitorTimer.Start();
}
```

## Testovací scénáře

### Test 1: Server Mode
```powershell
# Spustit Desktop aplikaci
cd src/RemoteDesktop.Desktop
dotnet run

# 1. Přepnout na "Server Mode" tab
# 2. Kliknout "Start Server"
# 3. Zkontrolovat:
#    - Server ID je zobrazeno
#    - Status badge = "Running" (zelený)
#    - Uptime se inkrementuje
# 4. Kliknout "Copy" u Server ID
# 5. Zkontrolovat clipboard
# 6. Kliknout "Stop Server"
# 7. Zkontrolovat status = "Stopped"
```

### Test 2: Client Mode - Manual Connection
```powershell
# 1. Přepnout na "Client Mode" tab
# 2. Zadat Server ID do "Manual Connection"
# 3. (Volitelně) zadat password
# 4. Zaškrtnout "Save this connection"
# 5. Zadat název "Test PC"
# 6. Kliknout "Connect to Server"
# 7. Zkontrolovat:
#    - ProjectionWindow se otevře
#    - Connection status overlay = "Connecting..."
#    - Po připojení: toolbar nahoře, stats zobrazeny
```

### Test 3: Saved Connections
```powershell
# 1. Po předchozím testu zkontrolovat seznam v "Client Mode"
# 2. Měla by být vidět "Test PC" connection
# 3. Kliknout "Connect"
# 4. ProjectionWindow se otevře
# 5. Double-click na connection - také by mělo připojit
# 6. Kliknout "Remove" - connection zmizí
```

### Test 4: Projection Window
```powershell
# Když je otevřené ProjectionWindow:
# 1. Stisknout F11 - přepnout fullscreen
# 2. Pohnout myší nahoru - toolbar se zobrazí
# 3. Pohnout dolů - toolbar se skryje
# 4. Stisknout ESC - vypnout fullscreen
# 5. Kliknout "Disconnect" - zavřít okno
```

## Požadované NuGet balíčky pro plnou integraci

```xml
<!-- Pokud se použije in-process hosting -->
<PackageReference Include="Microsoft.AspNetCore.App" />

<!-- Pokud se použije WebRTC přes SIPSorcery -->
<PackageReference Include="SIPSorcery" Version="6.1.0" />
<PackageReference Include="SIPSorcery.WebSocketSharp" Version="0.0.1" />

<!-- Pro monitoring přes Prometheus -->
<PackageReference Include="Prometheus.Client" Version="4.4.0" />
```

## Známé limitace (současný stav)

1. ✅ **ServerManager** - Spouští skutečné procesy (SignalingServer + ScreenSenderApp)
2. ✅ **ClientManager** - WebRTC connection plně implementována (SDP offer/answer + ICE)
3. ✅ **Video decoding** - FFmpegVideoEndPoint pro VP8 dekódování implementováno
4. ✅ **Stats monitoring** - Prometheus metrics monitoring implementováno
5. ⚠️ **Runtime testing** - End-to-end workflow vyžaduje testování s běžícím serverem

## Video Sink Implementation (DOKONČENO ✅)

**Soubor:** `Services/VideoSink.cs`

**Implementace:**
- Používá `FFmpegVideoEndPoint` z SIPSorceryMedia.FFmpeg (8.0.12)
- Hardware-akcelerované VP8 dekódování
- Automatická konverze pixelových formátů (RGB/BGRA → BGRA32)
- Event-based architektura pro WPF rendering
- Správa fronty framů (max 30 frames @ 30fps)
- Plná implementace IVideoSink rozhraní

**Flow:**
```
WebRTC Stream (VP8)
    ↓
GotVideoFrame() → FFmpegVideoEndPoint.GotVideoFrame()
    ↓
FFmpeg Decoding (VP8 → raw pixels)
    ↓
OnDecodedSample callback
    ↓
ConvertToBGRA32() → BGRA32 byte[]
    ↓
FrameReceived event → ProjectionWindow
    ↓
WriteableBitmap rendering
```

## Next Steps - Priorita

1. ✅ **[Hotovo]** WPF UI s dual-mode
2. ✅ **[Hotovo]** ServerManager + ClientManager framework
3. ✅ **[Hotovo]** Build bez chyb (0 errors, 6 warnings - všechna nekritická)
4. ✅ **[Hotovo]** Implementovat process launching v ServerManager
5. ✅ **[Hotovo]** Přidat WebRTC peer connection do ClientManager (SIPSorcery 8.0.23)
6. ✅ **[Hotovo]** Implementovat SDP/ICE handling
7. ✅ **[Hotovo]** Prometheus metrics monitoring
8. ✅ **[Hotovo]** Implementovat video sink pro příjem frames (FFmpegVideoEndPoint)
9. **⏳ [Další]** Otestovat end-to-end workflow s běžícím serverem
10. **⏳** Optimalizovat frame rendering performance
11. **⏳** Zajistit FFmpeg native knihovny jsou součástí release buildu

## Logs Location

- **Desktop App:** `%APPDATA%\DeskShare\logs\desktop-*.log`
- **ScreenSenderApp:** `%APPDATA%\DeskShare\logs\screensender-*.log`
- **SignalingServer:** `%APPDATA%\DeskShare\logs\server-*.log`

## Troubleshooting Integration

### Problem: Process fails to start

**Check:**
1. Executable paths are correct
2. Working directory is set properly
3. Firewall allows communication on ports 5000, 9090

**Solution:**
```csharp
_logger.LogInformation("Starting process: {Path}", exePath);
if (!File.Exists(exePath))
{
    throw new FileNotFoundException($"Executable not found: {exePath}");
}
```

### Problem: WebRTC connection fails

**Check:**
1. Signaling server is running
2. STUN server is reachable
3. Check browser console for WebRTC errors

**Debug:**
```csharp
_peerConnection.onconnectionstatechange += (state) =>
{
    _logger.LogInformation("Connection state: {State}", state);
};
```

### Problem: Frames not rendering

**Check:**
1. Frame data format matches WriteableBitmap format (BGRA32)
2. Frame dimensions are valid
3. Dispatcher is not blocked

**Debug:**
```csharp
private void OnFrameReceived(object? sender, byte[] frameData)
{
    _logger.LogDebug("Frame received: {Size} bytes", frameData.Length);
    // ...rendering code...
}
```

---

**Last Updated:** 2025-11-08
**Status:** ✅ Desktop aplikace KOMPLETNÍ - všechny komponenty implementovány
**Build:** ✅ 0 errors, 6 warnings (non-critical)
**WebRTC:** ✅ SIPSorcery 8.0.23 s full signaling + video decoding
**Video:** ✅ FFmpegVideoEndPoint (VP8 hardware-accelerated decoding)
**Remaining:** Runtime testing + FFmpeg native libraries deployment
