# Remote Control Usage Guide

## Overview

DeskShare provides secure, low-latency remote control capability over WebRTC. This guide covers setup, usage, security, and troubleshooting.

## Table of Contents

1. [Quick Start](#quick-start)
2. [Architecture](#architecture)
3. [Security Model](#security-model)
4. [Usage Instructions](#usage-instructions)
5. [Configuration](#configuration)
6. [Troubleshooting](#troubleshooting)
7. [Performance Tuning](#performance-tuning)
8. [API Reference](#api-reference)

---

## Quick Start

### 1. Start the Server

```bash
cd src/ScreenSenderApp
dotnet run
```

The server will output its Server ID GUID - save this for connecting clients.

### 2. Open WebClient

Open `src/WebClient/index-control.html` in a modern browser (Chrome, Edge, Firefox recommended).

### 3. Connect

1. Enter signaling server URL: `ws://localhost:5000/signal`
2. (Optional) Paste Server ID or leave empty for auto-discovery
3. Click **Connect**
4. Wait for video stream to appear

### 4. Enable Remote Control

1. Click **Enable Control** button
2. Wait for authorization (currently auto-approved in development)
3. Green border appears around video = control is active
4. Move mouse over video to control remote cursor
5. Type to send keystrokes to remote desktop

### 5. Disable When Done

Click **Disable Control** to stop sending inputs.

---

## Architecture

### Components

```
┌─────────────────┐          ┌──────────────────┐
│   WebClient     │          │  SignalingServer │
│  (JavaScript)   │◄────────►│   (ASP.NET)      │
│                 │  WSS     │                  │
│ - RemoteControl │          └──────────────────┘
│ - DataChannel   │                    ▲
└────────┬────────┘                    │
         │                             │
         │ WebRTC                      │
         │ (Video + Data)              │
         │                             │
         ▼                             │
┌─────────────────────────────────────┴┐
│        ScreenSenderApp                │
│                                       │
│  ┌──────────────┐  ┌───────────────┐│
│  │ VideoSource  │  │ InputController││
│  │ (Capture)    │  │ (SendInput)    ││
│  └──────────────┘  └───────────────┘│
│                                       │
│  ┌──────────────────────────────────┐│
│  │   DataChannelManager              ││
│  │   (Receive input messages)        ││
│  └──────────────────────────────────┘│
└───────────────────────────────────────┘
```

### Data Flow

1. **Mouse Move**: WebClient → DataChannel → InputController → Windows SendInput
2. **Keyboard**: WebClient → DataChannel → InputController → Windows SendInput
3. **Authorization**: WebClient → DataChannel → InputController → Response
4. **Video**: Capture → Encoder → WebRTC → WebClient

---

## Security Model

### Authorization

Remote control requires **explicit authorization** before any inputs are processed.

**Authorization Flow:**
1. Client clicks "Enable Control"
2. `authorization_request` sent via DataChannel
3. Server calls `InputController.RequestAuthorizationAsync()`
4. User sees dialog (TODO: currently auto-approved)
5. `authorization_response` sent back to client
6. If authorized: inputs are processed
7. If denied: control disabled, must request again

### Rate Limiting

**Limit:** 120 inputs per second (per client)

**Why:** Prevents abuse and ensures system stability.

**Behavior:**
- Tracks last 1 second of inputs
- Rejects inputs exceeding limit
- Logs rejected count for monitoring
- Automatically recovers after 1 second

### Input Validation

All inputs are validated before execution:

**Mouse Coordinates:**
- Must be in range 0.0 - 1.0 (normalized)
- Out of range = rejected

**Mouse Wheel:**
- Delta must be ≤ 1200 (reasonable scroll amount)
- Excessive values = rejected

**Keyboard:**
- Key codes must be 0-254 (valid Windows VK codes)
- Invalid codes = rejected

### Session Timeout

**Timeout:** 30 minutes of inactivity

**Behavior:**
- Tracks last input time
- Auto-revokes authorization after 30 minutes
- Client must request authorization again
- Prevents "forgotten" sessions

### Audit Logging

All input operations are logged via Serilog:

```
[Information] Remote control authorized for client: abc-123
[Warning] Input rejected: Rate limit exceeded (125 inputs in last second)
[Warning] Input rejected: Coordinates out of range (X=1.5, Y=-0.1)
[Information] Remote control session timed out after 30 minutes of inactivity
```

---

## Usage Instructions

### Mouse Control

**Move:**
- Move mouse over video element
- Coordinates automatically normalized to remote screen size
- Works across different resolutions

**Click:**
- Left/Right/Middle mouse buttons supported
- Extra buttons (back/forward) supported
- Context menu disabled when control active

**Scroll:**
- Mouse wheel scrolls on remote desktop
- Direction preserved (up/down)
- Standard delta: 120 units per notch

**Tips:**
- Keep mouse inside video bounds for accurate control
- If video is smaller than original resolution, movements are scaled
- Full-screen mode recommended for best experience

### Keyboard Control

**Typing:**
- Click video to ensure focus
- Type normally - all keys captured
- Modifier keys (Shift, Ctrl, Alt) tracked automatically

**Special Keys:**
- Arrow keys, Page Up/Down, Home/End work
- Function keys (F1-F12) work
- Escape, Tab, Backspace, Enter work

**Protected Shortcuts:**
These shortcuts are NOT captured (remain local):
- `Ctrl+T` - New browser tab
- `Ctrl+W` - Close browser tab
- `Ctrl+R` - Refresh page
- `Alt+F4` - Close browser window
- `F5` - Refresh (unless Ctrl held)

**All Other Shortcuts Captured:**
- `Ctrl+C/V/X` - Copy/Paste/Cut (sent to remote)
- `Ctrl+Z` - Undo (sent to remote)
- `Alt+Tab` - Switch apps (sent to remote)
- `Windows Key` - Start menu (sent to remote)

### Control Indicators

**Green Border:**
- Appears when control is authorized
- Glowing effect indicates active session

**Control Status Badge:**
- Top-right corner of video
- Shows "🎮 Remote Control ACTIVE"
- Only visible when authorized

**Button States:**
- "Enable Control" (Orange) = Not controlling
- "Disable Control" (Green) = Currently controlling

### Statistics

Real-time statistics are displayed:

**Remote Control Panel:**
- Control Status: Disabled/Requesting/Authorized/Denied
- Mouse Moves: Total mouse move commands sent
- Mouse Clicks: Total click commands sent
- Keys Sent: Total keyboard commands sent
- Dropped Messages: Messages lost (DataChannel not ready)

**Monitoring Dropped Messages:**
- Normal: 0 dropped
- If > 0: DataChannel may be congested or not open
- Check "Data Channel" status in Connection Stats panel

---

## Configuration

### Server Configuration

Edit `src/ScreenSenderApp/appsettings.json`:

```json
{
  "RemoteControl": {
    "Enabled": true,
    "MaxInputsPerSecond": 120,
    "SessionTimeoutMinutes": 30,
    "RequireAuthorization": true,
    "AutoApprove": false  // TODO: Implement dialog
  }
}
```

**Note:** Current implementation uses hard-coded values. Configuration support coming in Phase 4.

### Client Configuration

No configuration needed - all settings in UI.

**Recommended Browser Settings:**
- Hardware acceleration: Enabled
- WebRTC: Enabled (check chrome://webrtc-internals)
- Permissions: Camera/Microphone not needed

---

## Troubleshooting

### Control Not Working

**Symptom:** Click "Enable Control" but nothing happens

**Possible Causes:**

1. **DataChannel not open**
   - Check "Data Channel" status = should be "open"
   - Wait a few seconds after video connects
   - DataChannel opens shortly after WebRTC connection

2. **Authorization denied**
   - Check Control Status = should be "Authorized"
   - If "Denied", server rejected control request
   - Try disconnecting and reconnecting

3. **Video element not focused**
   - Click on video once to ensure focus
   - Keyboard events require focus

**Fix:**
```javascript
// Check in browser console:
console.log('DataChannel state:', dataChannel?.readyState);
console.log('Remote control enabled:', remoteControl?.enabled);
console.log('Remote control authorized:', remoteControl?.authorized);
```

### High Latency / Lag

**Symptom:** Mouse movements or keystrokes delayed

**Possible Causes:**

1. **Network latency**
   - Check "Latency (RTT)" stat
   - Should be < 50ms on LAN
   - > 100ms = noticeable lag

2. **Packet loss**
   - Check "Packet Loss" stat
   - Should be < 1%
   - > 5% = video quality/latency issues

3. **CPU overload on sender**
   - Check ScreenSenderApp CPU usage
   - Should be < 20% for 1080p@30fps
   - Reduce resolution or FPS if needed

**Fix:**
- Use wired connection (not Wi-Fi)
- Close other network applications
- Reduce video quality in appsettings.json
- Use "Low" or "Medium" quality preset

### Dropped Messages

**Symptom:** "Dropped Messages" count increasing

**Cause:** DataChannel not ready when trying to send

**This is normal during:**
- Connection establishment (first few seconds)
- WebRTC reconnection
- Network interruptions

**Fix:**
- Wait for DataChannel to open (status = "open")
- If persistent, check network stability
- Messages will resume once DataChannel recovers

### Keys Not Working

**Symptom:** Some keys don't work on remote desktop

**Possible Issues:**

1. **Virtual key code mismatch**
   - JavaScript keyCode may differ from Windows VK
   - Some keys have different codes across browsers

2. **Browser captured the key**
   - Protected shortcuts (Ctrl+T, etc.) not sent
   - This is intentional for browser usability

3. **Remote app not accepting input**
   - Some full-screen games may not work
   - UAC prompts require admin (security limitation)

**Limitations:**
- Cannot control UAC prompts (Windows security)
- Some DirectX games may not receive input
- Windows key may be disabled by policy

---

## Performance Tuning

### Low Latency Settings

For gaming or real-time control:

```json
{
  "Capture": {
    "TargetFps": 60,
    "UseSimdOptimization": true,
    "ConverterType": "Pooled"
  },
  "Quality": {
    "Preset": "High",
    "Bitrate": 8000000,
    "TargetFps": 60
  }
}
```

**Expected:** < 100ms end-to-end latency on LAN

### Bandwidth Optimization

For limited bandwidth:

```json
{
  "Quality": {
    "Preset": "Low",
    "Bitrate": 1000000,
    "TargetFps": 15
  }
}
```

**Bandwidth:** ~1 Mbps for video + minimal for control

### CPU Optimization

For lower CPU usage:

```json
{
  "Capture": {
    "TargetFps": 30,
    "ConverterType": "Basic"
  }
}
```

**Trade-off:** Higher CPU = lower latency

---

## API Reference

### RemoteControl Class (JavaScript)

```javascript
// Create instance
const remoteControl = new RemoteControl(videoElement, dataChannel);

// Enable control (requests authorization)
remoteControl.enable();

// Disable control
remoteControl.disable();

// Check status
console.log(remoteControl.enabled);     // boolean
console.log(remoteControl.authorized);  // boolean

// Get statistics
const stats = remoteControl.getStatistics();
console.log(stats.mouseMoveSent);       // number
console.log(stats.mouseClicksSent);     // number
console.log(stats.keysSent);            // number
console.log(stats.messagesDropped);     // number
```

### WindowsInputController (C#)

```csharp
// Create with dependency injection
var controller = new WindowsInputController(logger);

// Request authorization
bool authorized = await controller.RequestAuthorizationAsync(
    "client-id-123",
    cancellationToken
);

// Apply input
var input = new InputMessage
{
    Type = InputMessageType.MouseMove,
    X = 0.5,
    Y = 0.5
};
bool success = controller.ApplyInput(input);

// Revoke authorization
controller.RevokeAuthorization();

// Get statistics
var stats = controller.GetStatistics();
Console.WriteLine($"Inputs: {stats.TotalInputsApplied}");
```

### DataChannelManager (C#)

```csharp
// Create with logger and input controller
var dcManager = new DataChannelManager(logger, inputController);

// Create data channel on peer connection
var channel = await dcManager.CreateDataChannelAsync(
    peerConnection,
    label: "input"
);

// Send message
dcManager.SendMessage("Hello from server!");

// Send JSON object
dcManager.SendJson(new { type = "status", value = "ready" });

// Get statistics
var stats = dcManager.GetStatistics();
Console.WriteLine($"Messages: {stats.MessagesReceived}/{stats.MessagesSent}");
```

---

## Best Practices

### Security

✅ **DO:**
- Request authorization for each session
- Implement user confirmation dialog
- Log all input operations
- Monitor rate limit violations
- Use session timeouts

❌ **DON'T:**
- Auto-approve authorization in production
- Disable rate limiting
- Allow unlimited session duration
- Ignore validation failures

### Performance

✅ **DO:**
- Use wired network for best latency
- Enable SIMD optimizations
- Use pooled converters for 60+ FPS
- Monitor packet loss and adjust quality

❌ **DON'T:**
- Stream at higher resolution than needed
- Disable frame rate limiting
- Ignore dropped messages warnings

### User Experience

✅ **DO:**
- Show clear visual indicators
- Display real-time statistics
- Provide easy enable/disable toggle
- Explain keyboard shortcuts

❌ **DON'T:**
- Capture browser shortcuts
- Hide authorization status
- Leave control enabled after disconnect

---

## Changelog

### Version 1.0 (November 2025)

**Features:**
- ✅ Full mouse control (move, buttons, wheel)
- ✅ Full keyboard control with modifiers
- ✅ WebRTC DataChannel integration
- ✅ Authorization system
- ✅ Rate limiting (120 inputs/s)
- ✅ Input validation
- ✅ Session timeout (30 minutes)
- ✅ Audit logging
- ✅ Real-time statistics

**Known Limitations:**
- Authorization dialog not implemented (auto-approve)
- UAC prompts cannot be controlled
- Some full-screen games may not work
- Configuration via JSON not yet supported

**Coming in Phase 4:**
- GUI authorization dialog
- Configurable settings via appsettings.json
- OpenTelemetry metrics
- Enhanced security audit

---

## Support

**Issues:** https://github.com/your-username/DeskShare/issues
**Documentation:** https://github.com/your-username/DeskShare/docs
**License:** MIT

**Author:** Generated with focus on clean code, performance, and security.
