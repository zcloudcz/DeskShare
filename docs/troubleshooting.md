# Troubleshooting Guide

Quick solutions to common issues with DeskShare.

## Table of Contents

1. [Connection Issues](#connection-issues)
2. [Video Stream Issues](#video-stream-issues)
3. [Remote Control Issues](#remote-control-issues)
4. [Performance Issues](#performance-issues)
5. [Build and Runtime Errors](#build-and-runtime-errors)

---

## Connection Issues

### Cannot Connect to Signaling Server

**Symptom:**
```
WebSocket error occurred
Connection failed: Failed to connect to ws://localhost:5000/signal
```

**Causes & Solutions:**

1. **SignalingServer not running**
   ```bash
   # Start the server
   cd src/SignalingServer
   dotnet run
   ```
   Look for: `Now listening on: http://localhost:5000`

2. **Wrong URL**
   - Check signaling URL in WebClient: `ws://localhost:5000/signal`
   - Note: `ws://` not `wss://` for localhost
   - Note: Port must match (default 5000)

3. **Firewall blocking**
   ```bash
   # Windows: Allow port 5000
   netsh advfirewall firewall add rule name="RemoteDesktop" dir=in action=allow protocol=TCP localport=5000
   ```

4. **Port already in use**
   ```bash
   # Check what's using port 5000
   netstat -ano | findstr :5000

   # Change port in appsettings.json
   "Urls": "http://localhost:5001"
   ```

### WebRTC Connection Failed

**Symptom:**
```
WebRTC connection state: failed
ICE connection state: failed
```

**Causes & Solutions:**

1. **Firewall blocking UDP**
   - WebRTC needs UDP ports for media
   - Allow UDP traffic or disable firewall temporarily

2. **No STUN server reachable**
   - Default STUN: stun.l.google.com:19302
   - Try alternative in client.js:
   ```javascript
   { urls: 'stun:stun.stunprotocol.org:3478' }
   ```

3. **Network isolation (VPN, Docker)**
   - Disable VPN temporarily
   - Use host network mode if in Docker

### ICE Candidates Not Exchanging

**Symptom:**
```
No ICE candidates received
Connection stuck in "checking" state
```

**Solution:**
- Check browser console for candidate logs
- Ensure both peers are on same network or have TURN server
- Verify signaling messages are being exchanged

---

## Video Stream Issues

### No Video Stream

**Symptom:**
- Video element remains black
- "No video stream. Click Connect to start."

**Diagnostic Steps:**

1. **Check WebRTC state**
   - Should be "connected" not "new" or "failed"

2. **Check track received**
   - Browser console: Look for `[WebRTC] Received track: video`
   - If missing: Problem with ScreenSenderApp capture

3. **Check ScreenSenderApp**
   ```
   Should see:
   [Information] Desktop duplication initialized: 1920x1080
   [Information] WebRTC peer connection established
   ```

**Solutions:**

1. **ScreenSenderApp not capturing**
   ```bash
   # Restart with verbose logging
   cd src/ScreenSenderApp
   dotnet run

   # Check for errors:
   [Error] Failed to initialize desktop duplication
   ```
   - Cause: No monitor detected or DXGI not available
   - Solution: Ensure you're on Windows 10/11 with active display

2. **Wrong adapter/output**
   - Edit appsettings.json:
   ```json
   "AdapterIndex": 0,  // Try 0, 1, 2...
   "OutputIndex": 0    // Try 0, 1, 2...
   ```

3. **Video codec issue**
   - Browser console errors about VP8 codec
   - Ensure SIPSorceryMedia.Encoders is installed
   - Check vpxmd.dll exists in bin directory

### Video Frozen / Not Updating

**Symptom:**
- Video shows but doesn't update
- FPS = 0

**Solutions:**

1. **Capture pipeline stuck**
   ```bash
   # Restart ScreenSenderApp
   # Check logs for:
   [Warning] Frame acquisition timeout
   ```

2. **Encoder overloaded**
   - Reduce FPS in appsettings.json:
   ```json
   "TargetFps": 15  // Lower from 30
   ```

3. **Network congestion**
   - Check packet loss > 10%
   - Reduce bitrate:
   ```json
   "Bitrate": 1000000  // 1 Mbps instead of 4
   ```

### Video Quality Poor

**Symptom:**
- Blocky, pixelated video
- Low FPS despite good network

**Solutions:**

1. **Increase bitrate**
   ```json
   "Quality": {
     "Preset": "High",
     "Bitrate": 8000000  // 8 Mbps
   }
   ```

2. **Enable SIMD optimizations**
   ```json
   "UseSimdOptimization": true,
   "ConverterType": "Pooled"
   ```

3. **Check CPU usage**
   - Should be < 20% for 1080p@30fps
   - If high: Use "Basic" converter or lower resolution

---

## Remote Control Issues

### "Enable Control" Does Nothing

**Symptom:**
- Click button, control status stays "Disabled"
- No authorization request sent

**Diagnostic:**

Browser console:
```javascript
console.log('DataChannel:', dataChannel?.readyState);
// Should be: "open"

console.log('RemoteControl:', remoteControl);
// Should be: RemoteControl instance
```

**Solutions:**

1. **DataChannel not open**
   - Wait 2-3 seconds after video connects
   - Check "Data Channel" stat = should be "open"
   - If "closed": Problem with DataChannel creation

2. **JavaScript error**
   - Open browser DevTools (F12)
   - Check Console tab for errors
   - Common: `RemoteControl is not defined`
   - Fix: Ensure remote-control.js loads before client.js

3. **Button disabled**
   - Button should be enabled after video connects
   - If still disabled: WebRTC not fully connected

### Mouse Control Not Working

**Symptom:**
- Control status = "Authorized"
- Green border visible
- But mouse doesn't move on remote screen

**Diagnostic:**

Check statistics:
- Mouse Moves: Should increase as you move mouse
- Dropped Messages: Should be 0 or very low

**Solutions:**

1. **Messages being dropped**
   - All inputs lost due to DataChannel issues
   - Check DataChannel state = "open"
   - Reconnect if needed

2. **InputController not attached**
   - Server-side issue
   - Check ScreenSenderApp logs:
   ```
   [Information] DataChannel opened: Label=input
   [Information] Remote control authorized for client: xxx
   ```

3. **SendInput API failing**
   - Windows security or policy blocking
   - Check ScreenSenderApp for errors:
   ```
   [Error] SendInput failed for MouseMove
   ```

### Keyboard Not Working

**Symptom:**
- Mouse works fine
- But typing does nothing on remote

**Solutions:**

1. **Video element not focused**
   - Click on video element
   - Should see focus ring or cursor change
   - Keyboard events need focus

2. **Protected shortcuts captured**
   - Some keys are intentionally not sent (Ctrl+T, etc.)
   - Check keyboard shortcuts in remote-control.js

3. **Key code mapping issue**
   - JavaScript keyCode differs across browsers
   - Try different browser (Chrome recommended)

### High Dropped Messages Count

**Symptom:**
- Dropped Messages: > 100
- Control feels laggy

**Cause:** Sending inputs faster than DataChannel can handle

**Solutions:**

1. **Reduce mouse move frequency**
   - Browser sends MANY mousemove events
   - Current: Every move sent
   - TODO: Implement throttling (e.g., max 60/second)

2. **DataChannel congestion**
   - Network can't keep up
   - Reduce video bitrate to free bandwidth
   - Use wired connection instead of Wi-Fi

3. **Server not processing fast enough**
   - Rate limiter rejecting (> 120/second)
   - This is normal and expected
   - Messages will be dropped to maintain stability

---

## Performance Issues

### High Latency (> 200ms)

**Symptom:**
- Latency stat shows > 200ms
- Noticeable delay in video and control

**Measurement:**
- Check "Latency (RTT)" statistic
- LAN: Should be < 50ms
- Internet: 50-150ms acceptable
- > 200ms: Problem

**Solutions:**

1. **Network issue**
   ```bash
   # Test network latency
   ping [server-ip]
   # Should be < 10ms on LAN
   ```
   - Use wired connection
   - Close bandwidth-heavy apps
   - Check router QoS settings

2. **CPU bottleneck**
   - Check Task Manager CPU usage
   - ScreenSenderApp should use < 20%
   - If > 50%: Reduce FPS or resolution

3. **Encoder latency**
   - VP8 encoding introduces ~10-20ms
   - Use hardware encoder if available (TODO: Phase 5)
   - Reduce resolution to speed up encoding

### High Packet Loss (> 5%)

**Symptom:**
- Packet Loss: > 5%
- Video freezes or artifacts
- Control inputs delayed or lost

**Solutions:**

1. **Network congestion**
   - Close other network applications
   - Use wired connection
   - Check router for congestion

2. **Bitrate too high**
   - Reduce in appsettings.json:
   ```json
   "Bitrate": 2000000  // 2 Mbps
   ```

3. **Adaptive bitrate**
   - System automatically reduces quality
   - Check logs for quality adjustments:
   ```
   [Information] Quality adjusted to Low (packet loss: 8.5%)
   ```

### High CPU Usage

**Symptom:**
- ScreenSenderApp uses > 50% CPU
- System becomes sluggish

**Solutions:**

1. **Reduce capture FPS**
   ```json
   "TargetFps": 15  // Instead of 30 or 60
   ```

2. **Use basic converter**
   ```json
   "ConverterType": "Basic"  // Instead of SIMD or Pooled
   ```

3. **Lower resolution**
   ```json
   "Quality": {
     "Preset": "Low",  // 720p instead of 1080p
     "Width": 1280,
     "Height": 720
   }
   ```

4. **Check monitor count**
   - Capturing multiple monitors is expensive
   - Specify single output:
   ```json
   "OutputIndex": 0  // Primary monitor only
   ```

---

## Build and Runtime Errors

### Build Error: "CS1503 Cannot convert Task<RTCDataChannel> to RTCDataChannel"

**Cause:** Using sync method on async API

**Solution:**
```csharp
// Wrong:
var channel = peerConnection.createDataChannel("input", options);

// Correct:
var channel = await peerConnection.createDataChannel("input", options);
```

### Runtime Error: "Access to the path is denied"

**Symptom:**
```
MSB3061: Cannot delete file ... Access is denied
File locked by: RemoteDesktop.ScreenSenderApp (12345)
```

**Solution:**
```bash
# Stop all running instances
taskkill /F /IM RemoteDesktop.ScreenSenderApp.exe

# Clean and rebuild
dotnet clean
dotnet build
```

### Runtime Error: "Desktop duplication failed"

**Symptom:**
```
[Error] Failed to initialize desktop duplication
DXGI_ERROR_UNSUPPORTED
```

**Causes:**

1. **Running in VM or Remote Desktop**
   - DXGI Desktop Duplication doesn't work in RDP
   - Solution: Use physical machine or WindowCapturer

2. **No displays detected**
   - Check monitor is connected and enabled
   - Try different AdapterIndex/OutputIndex

3. **Driver issue**
   - Update graphics drivers
   - Ensure DirectX 11 support

### Runtime Error: "ObjectDisposedException"

**Symptom:**
```
System.ObjectDisposedException: Cannot access a disposed object
Object name: 'WindowsInputController'
```

**Cause:** Using InputController after Dispose()

**Solution:**
- Don't call methods after disposing
- Check lifecycle management
- Ensure proper cleanup in finally blocks

### Test Failures

**Symptom:**
```
Failed: 1, Passed: 147, Total: 148
```

**Debug Steps:**

1. **Run specific test**
   ```bash
   dotnet test --filter "FullyQualifiedName~TestName"
   ```

2. **Check test output**
   ```bash
   dotnet test --verbosity detailed
   ```

3. **Common issues:**
   - Platform-specific tests on wrong OS
   - Timing issues (use longer delays)
   - Mock setup incorrect

---

## Getting Help

### Collect Diagnostic Information

Before reporting issues, collect:

1. **System Info**
   ```bash
   dotnet --info
   systeminfo | findstr /C:"OS"
   ```

2. **Application Logs**
   - ScreenSenderApp: `src/ScreenSenderApp/logs/`
   - SignalingServer: `src/SignalingServer/logs/`

3. **Browser Console**
   - F12 → Console tab
   - Copy all errors and warnings

4. **WebRTC Internals**
   - Chrome: `chrome://webrtc-internals`
   - Edge: `edge://webrtc-internals`
   - Export data for analysis

### Report Issue

Include in your issue:

✅ **DO include:**
- System information (OS, .NET version)
- Complete error messages
- Steps to reproduce
- Log files
- Browser console output

❌ **DON'T include:**
- Sensitive data (passwords, IPs)
- Unrelated errors
- Vague descriptions ("doesn't work")

### Community Support

- **GitHub Issues:** https://github.com/your-username/DeskShare/issues
- **Discussions:** https://github.com/your-username/DeskShare/discussions

---

## Preventive Measures

### Before Deploying

- [ ] Test on clean machine
- [ ] Check firewall rules
- [ ] Verify all dependencies installed
- [ ] Run full test suite (`dotnet test`)
- [ ] Check logs for warnings
- [ ] Test with different browsers
- [ ] Test on both LAN and Internet
- [ ] Monitor resource usage under load

### Regular Maintenance

- [ ] Review logs weekly
- [ ] Monitor dropped message statistics
- [ ] Check for rate limit violations
- [ ] Update dependencies
- [ ] Rotate log files
- [ ] Test backup/recovery procedures

---

**Last Updated:** November 2025
**Version:** 1.0
**Maintainer:** DeskShare Team
