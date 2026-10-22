// Emulator end-to-end tests against a local signaling server (see mobile/README.md for the setup).
//
//   flutter test integration_test/e2e_test.dart -d emulator-5554 \
//     --dart-define=SIGNALING=ws://10.0.2.2:5171/signal \
//     --dart-define=CREDENTIALS_URL=http://10.0.2.2:5199/      # viewer test only
//
// The viewer test needs a DeskShare sender on the host; CREDENTIALS_URL must return "<server-id> <passkey>".
// The sender test is self-contained but needs the MediaProjection consent dialog to be accepted
// (by hand, or `adb shell input tap` / UiAutomator).
import 'dart:io';

import 'package:deskshare/app.dart';
import 'package:deskshare/core/ids.dart';
import 'package:deskshare/core/settings.dart';
import 'package:deskshare/sender/device_control.dart';
import 'package:deskshare/sender/gesture_mapper.dart';
import 'package:deskshare/sender/sender_session.dart';
import 'package:deskshare/viewer/viewer_session.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';
import 'package:integration_test/integration_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

const signaling = String.fromEnvironment('SIGNALING', defaultValue: 'ws://10.0.2.2:5171/signal');
const credentialsUrl = String.fromEnvironment('CREDENTIALS_URL', defaultValue: 'http://10.0.2.2:5199/');

Future<void> until(WidgetTester tester, bool Function() done, {Duration timeout = const Duration(seconds: 60), String? what}) async {
  final end = DateTime.now().add(timeout);
  while (!done()) {
    if (DateTime.now().isAfter(end)) fail('Timed out waiting for ${what ?? 'condition'}');
    await tester.pump(const Duration(milliseconds: 250));
  }
}

Future<AppServices> servicesFor({void Function(ViewerSession)? onViewer, void Function(SenderSession)? onSender}) async {
  SharedPreferences.setMockInitialValues({'signalingUrl': signaling});
  final settings = AppSettings(await SharedPreferences.getInstance());
  final device = MethodChannelDeviceControl();
  return AppServices(
    settings: settings,
    device: device,
    canShare: true,
    createViewer: () {
      final v = WebRtcViewerSession(signalingUrl: settings.signalingUrl);
      onViewer?.call(v);
      return v;
    },
    createSender: () {
      final s = WebRtcSenderSession(settings: settings, device: device);
      onSender?.call(s);
      return s;
    },
  );
}

bool hasFrames(ViewerSession v) => v.renderer != null && v.renderer!.videoWidth > 0 && v.renderer!.videoHeight > 0;

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('viewer: connects to the host sender and renders video frames', (tester) async {
    ViewerSession? viewer;
    await tester.pumpWidget(DeskShareApp(services: await servicesFor(onViewer: (v) => viewer = v)));
    await tester.tap(find.byKey(const Key('mode-connect')));
    await tester.pumpAndSettle();

    // The sender rotates its passkey every 45 s, so fetch it right before typing it.
    final parts = (await (() async {
      final c = HttpClient();
      final r = await (await c.getUrl(Uri.parse(credentialsUrl))).close();
      return r.transform(const SystemEncoding().decoder).join();
    })())
        .trim()
        .split(' ');
    expect(parts.length, 2, reason: 'credentials endpoint must return "<server-id> <passkey>"');

    await tester.enterText(find.byKey(const Key('server-id-field')), formatServerId(parts[0]));
    await tester.enterText(find.byKey(const Key('passkey-field')), parts[1]);
    await tester.tap(find.byKey(const Key('connect-button')));

    await until(tester, () => viewer!.state == ViewerState.connected || viewer!.state == ViewerState.failed, what: 'connection');
    expect(viewer!.state, ViewerState.connected, reason: viewer!.status);
    await until(tester, () => hasFrames(viewer!), what: 'decoded video frames');
    expect(find.byType(RTCVideoView), findsOneWidget);
    // ignore: avoid_print
    print('viewer received ${viewer!.renderer!.videoWidth}x${viewer!.renderer!.videoHeight} video');

    await tester.tap(find.byKey(const Key('disconnect-button')));
    await tester.pumpAndSettle();
  });

  testWidgets('sender: shares the emulator screen and a viewer receives frames', (tester) async {
    SenderSession? sender;
    ViewerSession? viewer;
    final services = await servicesFor(onSender: (s) => sender = s, onViewer: (v) => viewer = v);
    await tester.pumpWidget(DeskShareApp(services: services));

    await tester.tap(find.byKey(const Key('mode-share')));
    await tester.pumpAndSettle();
    expect(find.text('Start sharing'), findsOneWidget);
    await tester.tap(find.byKey(const Key('start-stop')));

    // The system MediaProjection consent dialog appears here and must be accepted.
    await until(tester, () => sender!.state == SenderState.sharing || sender!.state == SenderState.failed,
        timeout: const Duration(seconds: 120), what: 'sharing (accept the screen capture dialog)');
    expect(sender!.state, SenderState.sharing, reason: sender!.status);
    expect(sender!.passkey.length, 9);
    final serverId = sender!.serverId;
    final passkey = sender!.passkey;
    await tester.pump();
    expect(find.text('Stop sharing'), findsOneWidget);

    // A second role in the same process: connect as viewer through the real signaling server.
    final viewerSession = services.createViewer();
    addTearDown(viewerSession.dispose);
    viewer = viewerSession;
    await viewerSession.connect(serverId: serverId, passkey: passkey);
    await until(tester, () => viewerSession.state == ViewerState.connected || viewerSession.state == ViewerState.failed,
        what: 'viewer connection');
    expect(viewerSession.state, ViewerState.connected, reason: viewerSession.status);
    await until(tester, () => hasFrames(viewerSession), what: 'frames of the shared screen');
    expect(viewerSession.controlAvailable, isFalse, reason: 'remote control was not requested');
    // ignore: avoid_print
    print('sender streamed ${viewerSession.renderer!.videoWidth}x${viewerSession.renderer!.videoHeight}');

    await viewerSession.disconnect();
    await tester.tap(find.byKey(const Key('start-stop')));
    await tester.pump();
    await until(tester, () => sender!.state == SenderState.idle, what: 'stop');
    expect(viewer, isNotNull);
  });

  testWidgets('sender with remote control: accessibility service performs gestures', (tester) async {
    // Needs: adb shell settings put secure enabled_accessibility_services
    //   cz.zcloud.deskshare/cz.zcloud.deskshare.DeskShareAccessibilityService
    final device = MethodChannelDeviceControl();
    if (!await device.isAccessibilityEnabled()) {
      markTestSkipped('Accessibility service is not enabled on this device');
      return;
    }
    final size = await device.displaySize();
    expect(size.width, greaterThan(0));
    // A swipe and a Back press are dispatched by the real service (the channel call fails when it is not running).
    await device.perform(StrokeAction([Offset(size.width / 2, size.height * 0.7), Offset(size.width / 2, size.height * 0.3)], 200));
    await device.perform(const GlobalAction(GlobalActionKind.back));
  });
}
