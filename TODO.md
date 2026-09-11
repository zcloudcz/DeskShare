# DeskShare — Multiplatform TODO

> Current state analysis and task breakdown for macOS, iOS, and Linux support.
>
> **Baseline (2026-04-16):** Windows fully working, PWA covers all browsers,
> 335/335 tests passing, production hardening complete.

---

## Platform Support Matrix

| Component | Windows | Linux (X11) | macOS | iOS |
|-----------|:-------:|:-----------:|:-----:|:---:|
| Screen Capture | DXGI | X11 XGetImage | stub | — |
| Input Control | SendInput | XTest | CGEvent | — |
| Clipboard | Win32 API | X11 Selection | pbcopy/pbpaste | — |
| Window Manager | Win32 | X11 XQueryTree | stub | — |
| Desktop Viewer | WPF + Avalonia | Avalonia (untested) | Avalonia (untested) | — |
| Mobile Viewer | — | — | — | PWA only |

**Legend:** "stub" = class exists, throws `PlatformNotSupportedException`

---

## Phase A — macOS Server-Side (Screen Sharing FROM Mac)

**Goal:** A Mac can share its screen to any viewer (web, Windows, another Mac).

### A.1 Implement ScreenCaptureKitCapturer

**File:** `src/Core/Platforms/macOS/ScreenCaptureKitCapturer.cs`
**Status:** Stub — both `ScreenCaptureKitCapturer` (macOS 12.3+) and
`CGDisplayStreamCapturer` (fallback) throw `PlatformNotSupportedException`.

**What needs to happen:**

1. **Choose implementation approach:**
   - **Option A (recommended):** Use FFmpeg's `avfoundation` input device via
     `SIPSorceryMedia.FFmpeg`. FFmpeg already handles ScreenCaptureKit
     internally on macOS 12.3+ and falls back to AVFoundation on older versions.
     This avoids Objective-C bridging entirely.
   - **Option B:** P/Invoke to Objective-C runtime (`objc_msgSend`) to call
     ScreenCaptureKit API directly. More control but significantly more complex.
   - **Option C:** Create a small native Swift/Obj-C library (.dylib) that
     exposes a C API for screen capture, then P/Invoke into it.

2. **Implement `ICapturer` interface:**
   - `Initialize()` — request Screen Recording permission, create capture stream
   - `TryAcquireFrame(out Frame frame)` — get latest frame as BGRA byte array
   - `ReleaseFrame()` — release frame resources
   - `Dispose()` — stop capture stream

3. **Handle macOS permissions:**
   - Screen Recording permission is required (System Preferences > Privacy).
   - Check `CGPreflightScreenCaptureAccess()` (macOS 13+) or attempt capture
     and handle denial gracefully.
   - Show user-friendly error if permission is not granted.

4. **Pixel format:**
   - ScreenCaptureKit outputs `CVPixelBuffer` in BGRA format.
   - FFmpeg avfoundation outputs frames that need conversion to BGRA/I420.
   - Ensure the output matches the existing `Frame` model (BGRA byte array).

**Acceptance criteria:**
- Mac can capture its own screen and stream via WebRTC.
- Works on macOS 12.3+ (Apple Silicon and Intel).
- Graceful error if Screen Recording permission is denied.
- ScreenSenderApp runs on macOS and produces video frames.

---

### A.2 Implement CGWindowManager

**File:** `src/Core/Platforms/macOS/CGWindowManager.cs`
**Status:** Stub — all methods return empty/null.

**What needs to happen:**

1. **Window enumeration via P/Invoke:**
   - Call `CGWindowListCopyWindowInfo` with `kCGWindowListOptionOnScreenOnly`
   - Parse the returned CFArray of CFDictionary entries
   - Extract: window ID (`kCGWindowNumber`), owner name (`kCGWindowOwnerName`),
     window title (`kCGWindowName`), bounds (`kCGWindowBounds`)
   - Filter out system UI windows (menu bar, dock, status items)

2. **Implement `IWindowManager` interface:**
   - `GetWindows()` — return list of capturable windows
   - `GetWindowTitle(IntPtr handle)` — get window name
   - `IsWindowValid(IntPtr handle)` — check if window still exists
   - `GetWindowBounds(IntPtr handle)` — get window position and size

3. **Alternative approach:** Use `osascript` (AppleScript) for simpler but
   slower window enumeration: `tell application "System Events" to get
   every process whose visible is true`.

**Acceptance criteria:**
- Can enumerate all visible windows on macOS.
- Window titles and bounds are correct.
- Can select a specific window for capture (not just full screen).

---

### A.3 Complete keyboard mapping in CGEventInputController

**File:** `src/Core/Platforms/macOS/CGEventInputController.cs`
**Status:** Implemented but keyboard mapping is incomplete (has TODO).

**What needs to happen:**

1. Complete the Windows virtual key code → macOS keycode mapping table.
   - The current implementation has partial mapping.
   - macOS uses hardware keycodes (not virtual key codes like Windows).
   - Reference: `Events.h` in Carbon framework or
     `HIToolbox/Events.h` for keycode definitions.

2. Handle macOS-specific modifier keys:
   - Command (⌘) key — maps to Windows key (VK_LWIN/VK_RWIN)
   - Option (⌥) key — maps to Alt
   - Control (⌃) key — maps to Ctrl
   - Fn key — no direct Windows equivalent

**Acceptance criteria:**
- All standard keyboard keys work via remote control on macOS.
- Modifier keys (Cmd, Option, Control, Shift) work correctly.
- Special keys (F1-F12, arrows, Home/End/PgUp/PgDn) work.

---

### A.4 macOS permissions and entitlements

**What needs to happen:**

1. **Screen Recording permission:**
   - Required for screen capture.
   - Cannot be granted programmatically — user must approve in
     System Settings > Privacy & Security > Screen Recording.
   - App must detect denial and show instructions.

2. **Accessibility permission:**
   - Required for `CGEventInputController` (simulating mouse/keyboard).
   - Same manual approval flow as Screen Recording.
   - Check with `AXIsProcessTrusted()` (already in CGEventInputController).

3. **Network permission:**
   - Needed for WebSocket/WebRTC connections.
   - Entitlement: `com.apple.security.network.client`
   - If using App Sandbox, also need `com.apple.security.network.server`

4. **Create `Info.plist`** with privacy usage descriptions:
   ```xml
   <key>NSScreenCaptureUsageDescription</key>
   <string>DeskShare needs screen recording access to share your screen.</string>
   ```

---

## Phase B — macOS/Linux Desktop Viewer (Avalonia)

**Goal:** The Avalonia viewer runs natively on macOS and Linux,
providing the same experience as the WPF viewer on Windows.

### B.1 Test and fix Avalonia viewer on macOS

**Project:** `src/DeskShare.DesktopAvalonia` (targets `net8.0`, builds on all platforms)

**What needs to happen:**

1. **Build on macOS:**
   ```bash
   dotnet build src/DeskShare.DesktopAvalonia/DeskShare.DesktopAvalonia.csproj
   ```
   - Fix any macOS-specific build errors.
   - The FFmpeg deployment section in .csproj currently only handles
     `win-x64` natives — add `osx-arm64` and `osx-x64` FFmpeg binaries.

2. **Fix FFmpeg dependency for macOS:**
   - Current .csproj references `runtimes\win-x64\native` for FFmpeg.
   - Need to add macOS FFmpeg libraries (`.dylib` files).
   - Options: Homebrew (`brew install ffmpeg`), static build, or
     NuGet runtime package if available.

3. **Test video rendering:**
   - `VideoSink` in Desktop.Shared uses `WriteableBitmap` — verify
     Avalonia's `WriteableBitmap` works on macOS (it should).
   - Test frame decoding (SIPSorceryMedia.FFmpeg) on macOS.

4. **macOS-specific UI adjustments:**
   - Menu bar integration (macOS apps use a global menu bar, not in-window).
   - Dock icon configuration.
   - Native file dialogs (Avalonia handles this, but verify).

**Acceptance criteria:**
- Avalonia viewer builds and runs on macOS (ARM64 + x64).
- Can connect to a Windows/Mac ScreenSenderApp and view the stream.
- Remote control (mouse/keyboard) works from macOS viewer.

---

### B.2 Test and fix Avalonia viewer on Linux

**What needs to happen:**

1. **Build and run on Linux (X11):**
   - Install FFmpeg dev packages: `sudo apt install libavcodec-dev libavformat-dev`
   - Fix any Linux-specific build issues.
   - Test with Wayland compositor (may need `GDK_BACKEND=x11` fallback).

2. **FFmpeg libraries for Linux:**
   - Add Linux FFmpeg library deployment to .csproj.
   - Or document system dependency: `sudo apt install ffmpeg`.

**Acceptance criteria:**
- Avalonia viewer runs on Ubuntu 22.04+ with X11.
- Video stream renders correctly.
- Remote control works.

---

### B.3 macOS distribution

**What needs to happen:**

1. **Create `.app` bundle:**
   - Use `dotnet publish -r osx-arm64` to create self-contained app.
   - Wrap in `.app` bundle structure with `Info.plist`.
   - Include `Info.plist` with privacy descriptions (A.4).

2. **Code signing:**
   - Obtain Apple Developer ID Application certificate.
   - Sign with `codesign --deep --force --verify --verbose
     --sign "Developer ID Application: ..." DeskShare.app`
   - Sign all embedded frameworks and dylibs.

3. **Notarization:**
   - Submit to Apple for notarization:
     `xcrun notarytool submit DeskShare.zip --apple-id ... --team-id ...`
   - Staple the notarization ticket:
     `xcrun stapler staple DeskShare.app`

4. **Create DMG installer:**
   - Use `create-dmg` or `hdiutil` to create a DMG with drag-to-Applications.
   - Or create a `.pkg` installer for enterprise deployment.

5. **CI/CD:**
   - Add `macos-latest` runner to GitHub Actions workflow.
   - Build, sign, notarize, and publish in release pipeline.
   - Store signing credentials in GitHub Secrets.

**Acceptance criteria:**
- DMG installer works on macOS 12+ (Monterey and later).
- App passes Gatekeeper checks (no "unidentified developer" warning).
- Notarization succeeds.

---

## Phase C — iOS / Mobile (Future)

**Goal:** Native mobile viewer for iOS (and Android).
Currently covered by PWA, which works in Safari/Chrome.

### C.1 Evaluate whether PWA is sufficient

**Before building a native app, consider:**

- PWA already provides: WebRTC streaming, touch controls, fullscreen,
  offline app shell, add-to-home-screen.
- PWA limitations on iOS:
  - Safari-only WebRTC engine (no Chrome/Firefox engine)
  - No background execution
  - Limited push notifications (iOS 16.4+)
  - No haptic feedback API
  - 50 MB storage limit for PWA data

**Decision criteria:**
- If PWA covers 80%+ of use cases → skip native app, invest in PWA improvements.
- If native features are critical (background, haptics, App Store presence)
  → proceed with C.2.

---

### C.2 .NET MAUI mobile client (if needed)

**What would be needed:**

1. **New project:** `DeskShare.Mobile` targeting iOS and Android.
2. **Shared code:** `DeskShare.Common` models + `DeskShare.Core` signaling
   work directly in MAUI (same .NET 8 runtime).
3. **WebRTC on mobile:**
   - SIPSorcery has limited mobile support.
   - Would need a wrapper around Google's native WebRTC SDK:
     - iOS: `WebRTC.framework` (CocoaPod or SPM)
     - Android: `webrtc-android` (Maven)
   - Bridge via MAUI Platform Invoke or binding libraries.
4. **Touch-to-mouse mapping:**
   - Already implemented in `remote-control.js` (PWA) — port logic to C#.
   - Single tap = left click, long press = right click, pinch = zoom.
5. **Virtual keyboard:**
   - Map software keyboard input to `InputMessage` key events.

**Estimated effort:** 4-6 weeks for a basic viewer (no server-side capture).

---

## Phase D — Linux Server-Side

**Goal:** A Linux machine can share its screen.

**Status:** Already mostly implemented in `src/Core/Platforms/Linux/`:

| Component | Status |
|-----------|--------|
| X11ScreenCapturer | Implemented |
| XTestInputController | Implemented |
| X11ClipboardManager | Implemented |
| X11WindowManager | Implemented |

**Remaining work:**

### D.1 Wayland support

- X11 capturer won't work on Wayland-only desktops (GNOME 42+, KDE 6).
- Options:
  - **PipeWire/xdg-desktop-portal:** The standard Wayland screen capture API.
    Requires D-Bus calls to `org.freedesktop.portal.ScreenCast`.
  - **wlroots screencopy:** For wlroots-based compositors (Sway, etc.).
  - **Fallback:** Set `GDK_BACKEND=x11` or `QT_QPA_PLATFORM=xcb` to force
    X11 mode via XWayland.

### D.2 Test on major distributions

- Ubuntu 22.04+ (X11 and Wayland)
- Fedora 39+ (Wayland default)
- Debian 12+
- Arch Linux

---

## Priority Recommendation

| Phase | Effort | Impact | Recommendation |
|-------|--------|--------|----------------|
| **A (macOS server)** | Medium (2-3 weeks) | High — unlocks Mac screen sharing | Do first if Mac users need to share |
| **B (Avalonia viewer)** | Low-Medium (1-2 weeks) | High — native macOS/Linux viewers | Do second, most code exists |
| **C (iOS native)** | High (4-6 weeks) | Medium — PWA already covers this | Defer unless App Store presence needed |
| **D (Linux server)** | Low (3-5 days) | Medium — X11 already works | Do if Wayland users complain |

**Quick win:** Phase B.1 (test Avalonia on macOS) can likely be done in 1-2 days
since the project already targets `net8.0` and builds on Windows.

---

*Generated: 2026-04-16 | Project: DeskShare (RemoteDesktopNet) | Tests: 335/335 passing*
