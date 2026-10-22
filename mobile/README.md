# DeskShare mobile (Flutter)

Android + iOS app, application id / bundle id `cz.zcloud.deskshare`. Speaks the same protocol as the web and
desktop clients (see `src/WebClient/client.js` and `src/SignalingServer/Program.cs`).

- **Connect to computer** (Android, iOS): viewer. Pinch-zoom/pan; "Enable control" maps touch to mouse
  (tap = left click, two-finger tap = right click, drag = drag, pinch = wheel) and has an on-screen keyboard.
- **Share this phone** (Android): sender. Screen capture through MediaProjection (foreground service
  `CaptureService`), rotating passkey every 45 s. "Allow remote control" (default off) uses the
  `DeskShareAccessibilityService` (taps, swipes, scroll, Back/Home/Recents).
- iOS cannot share yet (needs a ReplayKit Broadcast Upload Extension, see Follow-ups) and iOS can never be
  remote controlled (Apple does not allow it).

Default signaling server `wss://app.deskshare.zcloud.cz/signal`, changeable in Settings. Release builds allow
only HTTPS/WSS; debug builds also allow `ws://` (emulator reaches the host at `10.0.2.2`).

## Layout

| Path | What |
|---|---|
| `lib/core` | protocol: Server ID/passkey rules, `PasskeyCredential`, HMAC `signRequest`, signaling API + socket, messages |
| `lib/viewer` | viewer session (WebRTC), touch/keyboard mapping, screen |
| `lib/sender` | sender session, `GestureMapper` (InputMessage to Android gestures), `DeviceControl` channel, screen |
| `android/app/src/main/kotlin` | `MainActivity` (method channel), `DeskShareAccessibilityService`, `CaptureService` |

`PasskeyCredential.wireValue(serverId)` is the single place that turns the shown passkey into the wire value
(`Passkey` field and HMAC key). The planned T3 change (HKDF auth key + `e2e_bind` handshake) goes there.

## Tests

```
flutter analyze
flutter test                      # unit + widget tests
flutter build apk --debug
```

Emulator end-to-end (`integration_test/e2e_test.dart`). Local signaling server on port 5171 (the emulator
sees the host as 10.0.2.2):

```
$env:Kestrel__Endpoints__Http__Url="http://0.0.0.0:5171"; $env:Kestrel__Endpoints__Https__Url="http://0.0.0.0:5172"
$env:Stun__Port="3578"; $env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --project src/SignalingServer
```

- **Viewer test** needs a DeskShare sender registered at that server plus a tiny HTTP endpoint that returns
  `"<server-id> <passkey>"` (the passkey rotates every 45 s) at `CREDENTIALS_URL`. The Windows desktop sender
  cannot be used from an agent session (DXGI access denied); the Linux E2E image (`docker/e2e`) works, but its
  container IP is unreachable from the emulator, so give the server a TURN entry reachable by both
  (`IceServers__0__Urls=turn:<host LAN IP>:3479?transport=udp`, user `testuser`/`testpass`, Development only).
  `flutter test integration_test/e2e_test.dart -d emulator-5554 --plain-name viewer:`
- **Sender test** is self-contained (sender and viewer both run in the app, through the real server) but the
  system MediaProjection dialog must be accepted: choose "Share entire screen" in the drop-down, then
  "Share screen" (`adb shell input tap` works).
  `flutter test integration_test/e2e_test.dart -d emulator-5554 --plain-name sender:`
- **Remote control on the phone** (`--plain-name remote`): sender with control on plus an in-app viewer; a viewer
  tap and a swipe must land at the same normalized position in a full-screen touch recorder. Needs the
  accessibility service enabled *after* the install (`flutter test` reinstalls the app and that resets it):
  `adb shell settings put secure enabled_accessibility_services cz.zcloud.deskshare/cz.zcloud.deskshare.DeskShareAccessibilityService`
  and `adb shell settings put secure accessibility_enabled 1`, plus the MediaProjection dialog.
- **Viewer control against a Linux sender** (`integration_test/control_e2e_test.dart`): same docker sender as above
  started with `--Security:EnableRemoteControl=true`; while the test prints `STEP ...` lines the harness polls
  `docker exec <container> xdotool getmouselocation` (display :99, 1280x720). Verified positions: tap
  (320,360), tap (960,540), drag ending (480,180).

## Vendored flutter_webrtc

`third_party/flutter_webrtc` is flutter_webrtc 1.6.2+hotfix.4 with one patch: it forwards
`MediaProjection.onStop` (upstream ignores it) so the app can revoke remote control the moment the user ends
capture from the system UI. See `third_party/flutter_webrtc/DESKSHARE_PATCH.md`. The owner secret is stored with
`flutter_secure_storage` (Keystore/Keychain) and backups are disabled (`android:allowBackup="false"`).

## Follow-ups

- Google Play: the Accessibility API needs the Permissions Declaration Form (core functionality: remote
  control requested by the user), a prominent in-app disclosure (the dialog before opening settings) and a
  MediaProjection foreground-service justification. Expect manual review.
- iOS sender: ReplayKit Broadcast Upload Extension (new Xcode target + App Group + flutter_webrtc broadcast
  handler); not built, cannot be done without Xcode.
- Viewer does not auto-reconnect via `/resume` after a lost session (web viewer does).
- Remote keyboard sends US-layout ASCII only (shifted characters use a real Shift key; the Linux sender's VK table
  is basic); no clipboard sync, no file transfer.
- Stopping capture from the system UI is covered by unit tests and the native hook, not yet exercised on a device.
