// Viewer-side remote control end to end, against a headless Linux sender with remote control on
// (docker/e2e image: Xvfb + ScreenSenderApp --Security:EnableRemoteControl=true).
//
// The harness polls `xdotool getmouselocation` inside the container while this test runs; the expected
// pointer positions (screen 1280x720) are printed with the marker "STEP". See mobile/README.md.
import 'dart:io';
import 'dart:ui';

import 'package:deskshare/core/ids.dart';
import 'package:deskshare/core/input_message.dart';
import 'package:deskshare/viewer/touch_mapper.dart';
import 'package:deskshare/viewer/viewer_session.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'e2e_test.dart' show credentialsUrl, servicesFor, until;

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('viewer control: touch gestures move the pointer on the host', (tester) async {
    final services = await servicesFor();
    final viewer = services.createViewer();
    addTearDown(viewer.dispose);

    final c = HttpClient();
    final body = await (await (await c.getUrl(Uri.parse(credentialsUrl))).close()).transform(const SystemEncoding().decoder).join();
    final parts = body.trim().split(' ');
    await viewer.connect(serverId: parts[0], passkey: parts[1]);
    await until(tester, () => viewer.state == ViewerState.connected || viewer.state == ViewerState.failed, what: 'connection');
    expect(viewer.state, ViewerState.connected, reason: viewer.status);
    await until(tester, () => viewer.controlAvailable, what: 'host to allow control (authorization_response)');
    viewer.setControl(true);
    expect(viewer.controlEnabled, isTrue);

    const area = Size(1280, 720); // mapped 1:1 onto the host screen
    final mapper = TouchMapper();
    Future<void> hold(String step, int seconds) async {
      // ignore: avoid_print
      print('STEP $step');
      await tester.runAsync(() => Future<void>.delayed(Duration(seconds: seconds)));
    }

    void send(List<InputMessage> messages) {
      for (final m in messages) {
        viewer.sendInput(m);
      }
    }

    // 1. plain pointer move to (25%, 50%) -> host (320, 360)
    send(mapper.down(1, const Offset(320, 360), area));
    send(mapper.up(1, const Offset(320, 360), area));
    await hold('tap at 320,360', 6);

    // 2. tap at (75%, 75%) -> host (960, 540)
    send(mapper.down(1, const Offset(960, 540), area));
    send(mapper.up(1, const Offset(960, 540), area));
    await hold('tap at 960,540', 6);

    // 3. one-finger drag from (160,180) to (480,180) -> pointer ends at (480, 180)
    send(mapper.down(1, const Offset(160, 180), area));
    send(mapper.move(1, const Offset(300, 180), area));
    send(mapper.move(1, const Offset(480, 180), area));
    send(mapper.up(1, const Offset(480, 180), area));
    await hold('drag ends at 480,180', 6);

    // 4. typed text with a shifted character (Shift press must reach the host as a real key)
    for (final m in typeChar('A')) {
      viewer.sendInput(m);
    }
    await hold('typed A', 3);

    expect(formatServerId(parts[0]), isNotEmpty);
    await viewer.disconnect();
  });
}
