import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:ui';

import 'package:deskshare/core/settings.dart';
import 'package:deskshare/sender/gesture_mapper.dart';
import 'package:deskshare/sender/rotation.dart';
import 'package:deskshare/sender/sender_session.dart';
import 'package:deskshare/viewer/viewer_session.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'fakes.dart';

Future<WebRtcSenderSession> _armedSender(FakeDevice device) async {
  SharedPreferences.setMockInitialValues({});
  final s = WebRtcSenderSession(settings: AppSettings(await SharedPreferences.getInstance()), device: device);
  s.debugArmControl(1000, 2000);
  return s;
}

void _send(WebRtcSenderSession s, List<Map<String, Object?>> messages) {
  for (final m in messages) {
    s.handleDataChannelText(jsonEncode(m));
  }
}

const _tap = [
  {'Type': 'MouseMove', 'X': 0.5, 'Y': 0.5},
  {'Type': 'MouseDown', 'Button': 'Left'},
  {'Type': 'MouseUp', 'Button': 'Left'},
];

void main() {
  group('passkey rotation delay', () {
    final now = DateTime.utc(2026, 1, 1, 0, 0, 10);

    test('normally waits for the end of the slot', () {
      expect(rotationDelay(validTo: DateTime.utc(2026, 1, 1, 0, 0, 45), now: now, failures: 0), const Duration(seconds: 35));
    });

    test('failures back off exponentially up to 60 s, not from the stale expired slot', () {
      final expired = DateTime.utc(2026, 1, 1, 0, 0, 0);
      final delays = [for (var f = 1; f <= 9; f++) rotationDelay(validTo: expired, now: now, failures: f).inSeconds];
      expect(delays, [1, 2, 4, 8, 16, 32, 60, 60, 60]);
      expect(rotationDelay(validTo: expired, now: now, failures: 1), isNot(Duration.zero));
    });

    test('Retry-After longer than the backoff wins', () {
      expect(rotationDelay(validTo: null, now: now, failures: 1, retryAfter: const Duration(seconds: 90)), const Duration(seconds: 90));
      expect(rotationDelay(validTo: null, now: now, failures: 3, retryAfter: const Duration(seconds: 1)), const Duration(seconds: 4));
    });
  });

  group('sender revokes control when capture stops (MediaProjection.onStop)', () {
    test('input is executed while armed', () async {
      final device = FakeDevice();
      final s = await _armedSender(device);
      _send(s, _tap);
      await Future<void>.delayed(Duration.zero);
      expect(device.performed.length, 1);
      expect(device.performed.single, isA<StrokeAction>());
    });

    test('after the system stops projection, control is off at once and gestures are ignored', () async {
      final device = FakeDevice();
      final s = await _armedSender(device);
      device.stopped.add(null);
      await Future<void>.delayed(Duration.zero);
      expect(s.controlGranted, isFalse);
      expect(s.state, SenderState.idle);
      _send(s, _tap);
      await Future<void>.delayed(Duration.zero);
      expect(device.performed, isEmpty);
    });

    test('rotation updates gesture coordinates', () async {
      final device = FakeDevice();
      final s = await _armedSender(device);
      device.size = const Size(2000, 1000);
      device.changed.add(null);
      await Future<void>.delayed(Duration.zero);
      _send(s, _tap);
      await Future<void>.delayed(Duration.zero);
      expect((device.performed.single as StrokeAction).points.single, const Offset(1000, 500));
    });
  });

  group('owner secret storage', () {
    test('a secret from an older version moves from SharedPreferences into the secure store', () async {
      SharedPreferences.setMockInitialValues({'ownerSecret': 'legacy-secret'});
      final prefs = await SharedPreferences.getInstance();
      final store = MemorySecretStore();
      final settings = await AppSettings.load(store: store, prefs: prefs);
      expect(settings.ownerSecret, 'legacy-secret');
      expect(store.value, 'legacy-secret');
      expect(prefs.containsKey('ownerSecret'), isFalse, reason: 'must not stay in plain prefs');
    });

    test('a new secret is generated once and reused', () async {
      SharedPreferences.setMockInitialValues({});
      final prefs = await SharedPreferences.getInstance();
      final store = MemorySecretStore();
      final first = (await AppSettings.load(store: store, prefs: prefs)).ownerSecret;
      expect(base64.decode(first).length, 32);
      expect((await AppSettings.load(store: store, prefs: prefs)).ownerSecret, first);
      expect(prefs.getKeys().any((k) => prefs.get(k) == first), isFalse);
    });
  });

  group('viewer session lifecycle', () {
    test('dispose while the WebSocket is still connecting closes the late socket', () async {
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      addTearDown(() => server.close(force: true));
      final wsClosed = Completer<void>();
      final wsAccepted = Completer<void>();
      server.listen((req) async {
        if (req.uri.path == '/authenticate') {
          req.response
            ..headers.contentType = ContentType.json
            ..write(jsonEncode({'success': true, 'webSocketToken': 'tok', 'iceServers': []}));
          await req.response.close();
        } else if (req.uri.path == '/signal') {
          await Future<void>.delayed(const Duration(milliseconds: 400)); // slow handshake
          final ws = await WebSocketTransformer.upgrade(req);
          wsAccepted.complete();
          ws.listen((_) {}, onDone: wsClosed.complete);
        }
      });

      final session = WebRtcViewerSession(signalingUrl: 'ws://127.0.0.1:${server.port}/signal');
      final connecting = session.connect(serverId: '123456789', passkey: 'ABCDEFGHJ');
      await Future<void>.delayed(const Duration(milliseconds: 150)); // authenticated, socket handshake in flight
      session.dispose();
      await connecting;

      await wsAccepted.future.timeout(const Duration(seconds: 3));
      await wsClosed.future.timeout(const Duration(seconds: 3), onTimeout: () => fail('late socket was left open'));
    });
  });
}
