# DeskShare patch

Copy of `flutter_webrtc` 1.6.2+hotfix.4 (MIT, see LICENSE) without example, docs and tests.

Only change: `android/src/main/java/com/cloudwebrtc/webrtc/GetUserMediaImpl.java` gets a static
`projectionStoppedListener` that is called from `MediaProjection.Callback.onStop()`. Upstream leaves that
callback empty, so an app cannot learn that the user ended screen capture from the system UI.
DeskShare uses it to revoke remote control immediately (`MainActivity` forwards it to Dart).

Drop this copy and return to the pub.dev package once upstream exposes the event.
