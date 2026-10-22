import 'package:deskshare/app.dart';
import 'package:deskshare/core/signaling_api.dart';
import 'package:deskshare/settings_screen.dart';
import 'package:deskshare/viewer/viewer_session.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'fakes.dart';

Future<void> _pumpApp(WidgetTester tester, AppServices services) async {
  tester.view.physicalSize = const Size(1080, 2200);
  tester.view.devicePixelRatio = 2.75;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(DeskShareApp(services: services));
}

void main() {
  group('Home', () {
    testWidgets('offers both modes', (tester) async {
      await _pumpApp(tester, await fakeServices());
      expect(find.text('Share this phone'), findsOneWidget);
      expect(find.text('Connect to computer'), findsOneWidget);
    });

    testWidgets('sharing is disabled with an explanation where it is not supported (iOS)', (tester) async {
      await _pumpApp(tester, await fakeServices(canShare: false));
      expect(find.textContaining('Not available on iPhone'), findsOneWidget);
      await tester.tap(find.byKey(const Key('mode-share')));
      await tester.pumpAndSettle();
      expect(find.text('Server ID'), findsNothing, reason: 'the sender screen must not open');
    });

    testWidgets('settings screen validates and saves the signaling URL', (tester) async {
      final services = await fakeServices();
      await _pumpApp(tester, services);
      await tester.tap(find.byTooltip('Settings'));
      await tester.pumpAndSettle();
      expect(find.text(defaultSignalingUrl), findsWidgets);

      await tester.enterText(find.byKey(const Key('signaling-url')), 'http://wrong');
      await tester.tap(find.text('Save'));
      await tester.pump();
      expect(find.textContaining('Use a wss:// address'), findsOneWidget);

      await tester.enterText(find.byKey(const Key('signaling-url')), 'ws://10.0.2.2:5171/signal');
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();
      expect(services.settings.signalingUrl, 'ws://10.0.2.2:5171/signal');
    });
  });

  testWidgets('release settings refuse cleartext ws:// (passkey would travel unencrypted)', (tester) async {
    final services = await fakeServices();
    await tester.pumpWidget(MaterialApp(home: SettingsScreen(settings: services.settings, allowInsecure: false)));
    await tester.enterText(find.byKey(const Key('signaling-url')), 'ws://10.0.2.2:5171/signal');
    await tester.tap(find.text('Save'));
    await tester.pump();
    expect(find.textContaining('Only secure wss://'), findsOneWidget);
    expect(services.settings.signalingUrl, defaultSignalingUrl);

    await tester.enterText(find.byKey(const Key('signaling-url')), 'wss://example.test/signal');
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();
    expect(services.settings.signalingUrl, 'wss://example.test/signal');
  });

  group('Share this phone', () {
    testWidgets('shows the Server ID, starts and stops sharing', (tester) async {
      final sender = FakeSender();
      await _pumpApp(tester, await fakeServices(sender: sender));
      await tester.tap(find.byKey(const Key('mode-share')));
      await tester.pumpAndSettle();

      expect(tester.widget<Text>(find.byKey(const Key('server-id'))).data, '123 456 789');
      expect(tester.widget<Text>(find.byKey(const Key('passkey'))).data, '---');
      expect(find.text('Start sharing'), findsOneWidget);

      await tester.tap(find.byKey(const Key('start-stop')));
      await tester.pump();
      expect(sender.starts, 1);
      expect(tester.widget<Text>(find.byKey(const Key('passkey'))).data, 'ABC-DEF-GHJ');
      expect(find.text('Stop sharing'), findsOneWidget);
      expect(find.textContaining('New passkey in'), findsOneWidget);
      // The toggle is fixed while sharing.
      expect(tester.widget<SwitchListTile>(find.byKey(const Key('control-switch'))).onChanged, isNull);

      await tester.tap(find.byKey(const Key('start-stop')));
      await tester.pump();
      expect(sender.stops, 1);
      expect(find.text('Start sharing'), findsOneWidget);
    });

    testWidgets('remote control defaults off; turning it on explains the accessibility permission', (tester) async {
      final device = FakeDevice();
      final sender = FakeSender();
      await _pumpApp(tester, await fakeServices(device: device, sender: sender));
      await tester.tap(find.byKey(const Key('mode-share')));
      await tester.pumpAndSettle();

      expect(sender.controlRequested, isFalse);
      expect(find.byKey(const Key('accessibility-warning')), findsNothing);

      await tester.tap(find.byKey(const Key('control-switch')));
      await tester.pumpAndSettle();
      expect(sender.controlRequested, isTrue);
      expect(find.text('Accessibility permission needed'), findsOneWidget);
      expect(find.textContaining('does not read your screen content'), findsOneWidget);

      await tester.tap(find.text('Open settings'));
      await tester.pumpAndSettle();
      expect(device.settingsOpened, 1);
      expect(find.byKey(const Key('accessibility-warning')), findsOneWidget);
    });

    testWidgets('no warning when the accessibility service is already on', (tester) async {
      final device = FakeDevice()..accessibility = true;
      final sender = FakeSender();
      await _pumpApp(tester, await fakeServices(device: device, sender: sender));
      await tester.tap(find.byKey(const Key('mode-share')));
      await tester.pumpAndSettle();
      sender.controlRequested = true;
      await tester.pump();
      expect(find.byKey(const Key('accessibility-warning')), findsNothing);
    });

    testWidgets('a failure is shown, not swallowed', (tester) async {
      final sender = FakeSender()..failure = 'Screen capture permission was denied';
      await _pumpApp(tester, await fakeServices(sender: sender));
      await tester.tap(find.byKey(const Key('mode-share')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('start-stop')));
      await tester.pump();
      expect(find.text('Screen capture permission was denied'), findsOneWidget);
      expect(find.text('Start sharing'), findsOneWidget, reason: 'the user can retry');
    });
  });

  group('Connect to computer', () {
    testWidgets('passes the typed Server ID and passkey to the session', (tester) async {
      final viewer = FakeViewer();
      await _pumpApp(tester, await fakeServices(viewer: viewer));
      await tester.tap(find.byKey(const Key('mode-connect')));
      await tester.pumpAndSettle();

      await tester.enterText(find.byKey(const Key('server-id-field')), '123 456 789');
      await tester.enterText(find.byKey(const Key('passkey-field')), 'abc-def-ghj');
      await tester.tap(find.byKey(const Key('connect-button')));
      await tester.pump();

      expect(viewer.lastServerId, '123 456 789');
      expect(viewer.lastPasskey, 'abc-def-ghj');
      expect(find.text('Connecting...'), findsOneWidget);
    });

    testWidgets('shows the error and lets the user try again', (tester) async {
      final viewer = FakeViewer();
      await _pumpApp(tester, await fakeServices(viewer: viewer));
      await tester.tap(find.byKey(const Key('mode-connect')));
      await tester.pumpAndSettle();

      viewer.emit(ViewerState.failed, 'Invalid passkey or server ID');
      await tester.pump();
      expect(find.text('Invalid passkey or server ID'), findsOneWidget);
      expect(tester.widget<FilledButton>(find.byKey(const Key('connect-button'))).onPressed, isNotNull);
    });
  });
}
