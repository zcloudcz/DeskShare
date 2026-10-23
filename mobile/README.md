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

## End-to-end binding (protocol T3)

Mirror of `src/Core/Auth/E2EBinding.cs` / `E2EKeyRing.cs` in `lib/core/e2e_binding.dart`. The raw passkey never
leaves the phone: `PasskeyCredential.wireValue(serverId)` is the HKDF-SHA256 auth key (sent as `Passkey` and used
as the request-signing HMAC key), the e2e key stays local. Both roles then exchange `e2e_bind` MACs over the data
channel (HMAC over role and both DTLS fingerprints from the SDPs), 10 s deadline, same error text as the web viewer.

- **Viewer**: sends its bind when the channel opens, ignores everything until the host's bind verified, keeps the
  video stream away from the renderer and sends no input/`authorization_request` before that. State is per peer
  connection (`PeerBinding`); a replaced connection is retired, so a late result cannot verify the new one.
- **Android sender**: always creates the `input` data channel now (the bind travels over it, also for view-only
  hosts). A key ring holds the keys of recent passkeys (viewers that typed the previous one still bind). The
  viewer's bind is verified first, then the sender's own bind goes out, then video and input open up.
  Until then the screen track is **disabled** (`track.enabled = false` = black frames, no pixels); this is
  reliable in flutter_webrtc on Android, adding the track after verification would need renegotiation.
- **Permanent password**: the viewer field takes the passkey or a 12+ character permanent password
  (PBKDF2-HMAC-SHA256, NFC, 600 000 rounds in a background isolate via pointycastle + unorm_dart, then the same
  HKDF; vector shared with `tests/js/perm-vector.mjs`). HTTP 429 from `/authenticate` reads "Too many attempts".
- **Control approval**: the probe after binding is `authorization_request {interactive:false}`; "Enable control" sends
  `interactive:true` when the host answered `requestable:true`, shows `approval_pending` and the final
  `authorization_response` (`reason` denied / timeout / client_update_required). `approval_pending` /
  `approval_denied` with `what:"connection"` are handled for unattended hosts. Written against the protocol
  description; not yet exercised against a real approving host.
- Tests: `test/e2e_binding_test.dart` uses the fixed vector shared with `tests/js/e2e-vector.mjs`. Old clients
  (raw passkey) cannot talk to new senders and vice versa.

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
  Run coturn for it: `docker run -d -p 3479:3479/udp -p 50000-50040:50000-50040/udp coturn/coturn -n
  --listening-port=3479 --min-port=50000 --max-port=50040 --lt-cred-mech --user=testuser:testpass --realm=test
  --no-tls --external-ip=<host LAN IP>` and start the server with `Turn__Enabled=false` (the server's built-in
  TURN logs relay address 0.0.0.0; not verified whether it works). A passkey is single use: a second viewer test
  needs the next rotation.
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
