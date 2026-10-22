// Emulator end-to-end tests against a local signaling server (see mobile/README.md for the setup).
//
//   flutter test integration_test/e2e_test.dart -d emulator-5554 \
//     --dart-define=SIGNALING=ws://10.0.2.2:5171/signal \
//     --dart-define=CREDENTIALS_URL=http://10.0.2.2:5199/      # viewer test only
//
// The viewer test needs a DeskShare sender on the host; CREDENTIALS_URL must return "<server-id> <passkey>".
// The sender test is self-contained but needs the MediaProjection consent dialog to be accepted
// (by hand, or `adb shell input tap` / UiAutomator).
import 'dart:async';
import 'dart:io';

import 'package:deskshare/app.dart';
import 'package:deskshare/core/ids.dart';
import 'package:deskshare/core/input_message.dart';
import 'package:deskshare/core/settings.dart';
import 'package:deskshare/sender/device_control.dart';
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
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();

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

  testWidgets('remote control: a viewer tap and swipe become real gestures on the phone', (tester) async {
    // Needs the accessibility service enabled beforehand:
    //   adb shell settings put secure enabled_accessibility_services     //     cz.zcloud.deskshare/cz.zcloud.deskshare.DeskShareAccessibilityService
    //   adb shell settings put secure accessibility_enabled 1
    // and the MediaProjection dialog accepted (see mobile/README.md).
    final services = await servicesFor();
    final device = services.device;
    var accessibilityOn = false;
    unawaited((() async {
      while (!accessibilityOn) {
        accessibilityOn = await device.isAccessibilityEnabled();
        await Future<void>.delayed(const Duration(seconds: 1));
      }
    })());
    // flutter test reinstalls the app, which resets the setting: wait for the harness to enable it again.
    await until(tester, () => accessibilityOn, timeout: const Duration(seconds: 90), what: 'accessibility service (enable it via adb)');

    final sender = services.createSender();
    addTearDown(sender.dispose);
    sender.controlRequested = true;
    await sender.start();
    await until(tester, () => sender.state == SenderState.sharing || sender.state == SenderState.failed,
        timeout: const Duration(seconds: 120), what: 'sharing (accept the screen capture dialog)');
    expect(sender.state, SenderState.sharing, reason: sender.status);

    final viewer = services.createViewer();
    addTearDown(viewer.dispose);
    await viewer.connect(serverId: sender.serverId, passkey: sender.passkey);
    await until(tester, () => viewer.state == ViewerState.connected || viewer.state == ViewerState.failed, what: 'viewer');
    expect(viewer.state, ViewerState.connected, reason: viewer.status);
    await until(tester, () => viewer.controlAvailable, what: 'host to authorize control');
    viewer.setControl(true);

    // Real touches are dropped by the test binding unless told otherwise.
    binding.shouldPropagateDevicePointerEvents = true;
    // A full-screen touch recorder: real touches injected by the accessibility service arrive here.
    final downs = <Offset>[];
    final moves = <Offset>[];
    await tester.pumpWidget(MaterialApp(
      home: Listener(
        behavior: HitTestBehavior.opaque, // an empty SizedBox is not hit-testable by default
        onPointerDown: (e) => downs.add(e.position),
        onPointerMove: (e) => moves.add(e.position),
        child: const SizedBox.expand(),
      ),
    ));
    final view = tester.view;
    final size = view.physicalSize / view.devicePixelRatio;
    final display = await device.displaySize();
    // ignore: avoid_print
    print('flutter view ${size.width}x${size.height} dp, display ${display.width}x${display.height} px, dpr ${view.devicePixelRatio}');

    // Tap at (25%, 40%).
    viewer.sendInput(const InputMessage.move(0.25, 0.4));
    viewer.sendInput(const InputMessage.down(MouseButton.left));
    viewer.sendInput(const InputMessage.up(MouseButton.left));
    await until(tester, () => downs.isNotEmpty, timeout: const Duration(seconds: 20), what: 'the injected tap');
    final tap = downs.first;
    // ignore: avoid_print
    print('tap landed at ${tap.dx / size.width}, ${tap.dy / size.height} (normalized)');
    expect(tap.dx / size.width, closeTo(0.25, 0.03));
    expect(tap.dy / size.height, closeTo(0.4, 0.05));

    // Swipe up: (50%, 80%) -> (50%, 30%).
    downs.clear();
    moves.clear();
    viewer.sendInput(const InputMessage.move(0.5, 0.8));
    viewer.sendInput(const InputMessage.down(MouseButton.left));
    viewer.sendInput(const InputMessage.move(0.5, 0.55));
    viewer.sendInput(const InputMessage.move(0.5, 0.3));
    viewer.sendInput(const InputMessage.up(MouseButton.left));
    await until(tester, () => moves.length > 3, timeout: const Duration(seconds: 20), what: 'the injected swipe');
    expect(downs.first.dy / size.height, closeTo(0.8, 0.05));
    expect(moves.last.dy / size.height, closeTo(0.3, 0.06));
    expect(moves.last.dy, lessThan(downs.first.dy), reason: 'finger moved up');
    binding.shouldPropagateDevicePointerEvents = false; // the binding insists the test restores it
  });
}
