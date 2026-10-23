import 'dart:async';
import 'dart:convert';

import 'package:deskshare/core/e2e_binding.dart';
import 'package:deskshare/sender/sender_session.dart';
import 'package:deskshare/core/settings.dart';
import 'package:deskshare/core/signaling_message.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'fakes.dart';

// Same constants as tests/js/e2e-vector.mjs and tests/UnitTests/Auth/E2EBindingTests.cs.
const _passkey = 'A3F7K9M2P';
const _serverId = 'server-123456789';
const _authKey = '4-ne--Ifkq6P2q0hI9jVHJ-pxGxoY8wXA0YCydFSHvE';
const _e2eKeyHex = '059dbe1a8b432ce2897c184963de77db35752ae20d7b7074042482ef8160d4d5';
const _senderMac = '1898840887343059f95c5934f3194eaebe5aae504bdfb3f7dfe53cefefdaf1a6';
const _viewerMac = '8f2c9fb1dda05ed5f38edbf5d3508486cc51cc89f03cce4aff94a2b0ea1659af';

String _fp(int first) => [for (var i = 0; i < 32; i++) (first + i).toRadixString(16).padLeft(2, '0').toUpperCase()].join(':');
final _senderFp = _fp(0x00);
final _viewerFp = _fp(0xe0);

String _hex(List<int> b) => [for (final x in b) x.toRadixString(16).padLeft(2, '0')].join();

void main() {
  group('key derivation', () {
    test('matches the shared vector (C# and JS)', () {
      final k = deriveKeys(_passkey, _serverId);
      expect(k.authKey, _authKey);
      expect(k.authKey.length, 43);
      expect(_hex(k.e2eKey), _e2eKeyHex);
    });

    test('normalizes what the user typed', () {
      expect(deriveKeys('a3f-7k9-m2p', _serverId).authKey, _authKey);
      expect(deriveKeys(' a3f 7k9 m2p ', _serverId).authKey, _authKey);
    });

    test('the server id is the salt', () {
      expect(deriveKeys(_passkey, 'server-987654321').authKey, isNot(_authKey));
    });
  });

  group('permanent password', () {
    // Same constants as tests/js/perm-vector.mjs and PermanentPasswordTests.cs (non-ASCII on purpose).
    const password = 'Žluťoučký kůň 2026';
    const permAuth = 'JHEeLJCuuOig-SzfCVF1yovJDLH-yQas8TlWd0gJ5y8';
    const permE2e = '66325114af86ffca1b9bfe6d612c7d763d10c7ed509909362993ca2d29d295ec';

    test('matches the shared vector, also when typed decomposed (NFD)', () async {
      final k = await deriveFromUserInput(password, _serverId);
      expect(k.authKey, permAuth);
      expect(_hex(k.e2eKey), permE2e);
      // The macOS keyboard form: base letters plus combining marks.
      const nfd = 'Žluťoučký kůň 2026';
      expect(nfd, isNot(password));
      final k2 = await deriveFromUserInput(nfd, _serverId);
      expect(k2.authKey, permAuth);
    }, timeout: const Timeout(Duration(minutes: 2)));

    test('input classification', () {
      expect(looksLikePasskey('a3f-7k9-m2p'), isTrue);
      expect(looksLikePasskey('A3F7K9M2'), isFalse);
      expect(isPermanentPassword('correct horse battery'), isTrue);
      expect(isPermanentPassword('short pw'), isFalse);
      expect(isPermanentPassword('a3f-7k9-m2p'), isFalse);
      expect(deriveFromUserInput('short', _serverId), throwsArgumentError);
    });
  });

  group('bind MAC', () {
    final key = deriveKeys(_passkey, _serverId).e2eKey;

    test('matches the shared vector', () {
      expect(computeMac(key, senderRole, _senderFp, _viewerFp), _senderMac);
      expect(computeMac(key, viewerRole, _senderFp, _viewerFp), _viewerMac);
    });

    test('verifies only the right role, fingerprints and key', () {
      expect(verifyMac(key, viewerRole, _senderFp, _viewerFp, _viewerMac), isTrue);
      expect(verifyMac(key, viewerRole, _senderFp, _viewerFp, _viewerMac.toUpperCase()), isTrue);
      expect(verifyMac(key, senderRole, _senderFp, _viewerFp, _viewerMac), isFalse); // echoed back
      expect(verifyMac(key, viewerRole, _senderFp, _senderFp, _viewerMac), isFalse); // swapped fingerprint
      expect(verifyMac(deriveKeys('AAAAAAAAA', _serverId).e2eKey, viewerRole, _senderFp, _viewerFp, _viewerMac), isFalse);
      expect(verifyMac(key, viewerRole, _senderFp, _viewerFp, null), isFalse);
      expect(verifyMac(key, viewerRole, _senderFp, _viewerFp, '00'), isFalse);
    });

    test('bind message round trips and rejects malformed ones', () {
      final json = jsonDecode(buildBindMessage(viewerRole, _viewerMac));
      expect(json['type'], 'e2e_bind');
      expect(json['v'], 1);
      expect(tryReadBind(json)?.role, viewerRole);
      expect(tryReadBind(json)?.mac, _viewerMac);
      expect(tryReadBind({'type': 'e2e_bind', 'role': 'viewer', 'mac': '00'}), isNull); // no version
      expect(tryReadBind({'type': 'e2e_bind', 'v': 2, 'role': 'viewer', 'mac': '00'}), isNull);
      expect(tryReadBind({'type': 'e2e_bind', 'v': 1, 'mac': '00'}), isNull);
      expect(tryReadBind({'type': 'e2e_bind', 'v': 1, 'role': 'viewer', 'mac': 5}), isNull);
    });
  });

  group('getFingerprint', () {
    test('reads a session level line', () {
      expect(getFingerprint('v=0\r\ns=-\r\na=fingerprint:sha-256 $_senderFp\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\n'), _senderFp);
    });

    test('reads a media level line and normalizes case', () {
      expect(getFingerprint('v=0\ns=-\nm=video 9 UDP/TLS/RTP/SAVPF 96\na=fingerprint:sha-256 ${_senderFp.toLowerCase()}\n'), _senderFp);
    });

    test('accepts repeated identical lines but not conflicting ones', () {
      final same = 'a=fingerprint:sha-256 $_senderFp\nm=audio 9 X\na=fingerprint:sha-256 $_senderFp\nm=video 9 X\na=fingerprint:sha-256 $_senderFp\n';
      final conflicting = 'a=fingerprint:sha-256 $_senderFp\nm=video 9 X\na=fingerprint:sha-256 $_viewerFp\n';
      expect(getFingerprint(same), _senderFp);
      expect(getFingerprint(conflicting), isNull);
    });

    test('null when there is no usable line', () {
      for (final sdp in [null, '', 'v=0\r\ns=-\r\n', 'a=fingerprint:sha-1 AA:BB:CC\r\n', 'a=fingerprint:sha-256 AA:BB\r\n']) {
        expect(getFingerprint(sdp), isNull, reason: '$sdp');
      }
    });
  });

  group('E2EKeyRing', () {
    final now = DateTime.utc(2026, 1, 1);
    final k1 = deriveKeys('AAAAAAAAA', _serverId).e2eKey;
    final k2 = deriveKeys('BBBBBBBBB', _serverId).e2eKey;

    test('verifies a viewer that used the previous passkey while it is in the grace period', () {
      var t = now;
      final ring = E2EKeyRing(utcNow: () => t);
      ring.add(k1, now.add(const Duration(seconds: 45)));
      ring.add(k2, now.add(const Duration(seconds: 90)));
      final mac = computeMac(k1, viewerRole, _senderFp, _viewerFp);
      expect(ring.tryVerify(viewerRole, _senderFp, _viewerFp, mac), k1);

      t = now.add(const Duration(minutes: 5)); // k2 passed its grace, k1 was kept by the verification
      expect(ring.count, 1);
      expect(ring.tryVerify(viewerRole, _senderFp, _viewerFp, computeMac(k2, viewerRole, _senderFp, _viewerFp)), isNull);
      expect(ring.tryVerify(viewerRole, _senderFp, _viewerFp, mac), k1);
    });

    test('same key added twice is refreshed, not duplicated; expired keys are pruned', () {
      var t = now;
      final ring = E2EKeyRing(utcNow: () => t);
      ring.add(k1, now.add(const Duration(seconds: 45)));
      ring.add(List.of(k1), now.add(const Duration(seconds: 45)));
      expect(ring.count, 1);
      t = now.add(const Duration(minutes: 3));
      expect(ring.count, 0);
    });

    test('rejects a MAC of the wrong role or fingerprints', () {
      final ring = E2EKeyRing(utcNow: () => now)..add(k1, now.add(const Duration(seconds: 45)));
      final senderRoleMac = computeMac(k1, senderRole, _senderFp, _viewerFp);
      expect(ring.tryVerify(viewerRole, _senderFp, _viewerFp, senderRoleMac), isNull);
      expect(ring.tryVerify(senderRole, _senderFp, _senderFp, senderRoleMac), isNull);
    });
  });

  group('PeerBinding gate', () {
    test('a retired connection never becomes verified', () {
      final a = PeerBinding();
      final b = PeerBinding();
      a.retire(); // replaced by b
      expect(a.tryVerify(), isFalse);
      expect(a.isVerified, isFalse);
      expect(b.isVerified, isFalse); // a new connection does not inherit anything
      expect(b.tryVerify(), isTrue);
      expect(b.isVerified, isTrue);
      b.retire();
      expect(b.isVerified, isFalse);
    });
  });

  group('sender gating', () {
    final key = deriveKeys(_passkey, _serverId).e2eKey;
    Future<({String? sender, String? viewer})> fps() async => (sender: _senderFp, viewer: _viewerFp);
    final bindJson = buildBindMessage(viewerRole, _viewerMac);
    const taps = [
      '{"Type":"MouseMove","X":0.5,"Y":0.5}',
      '{"Type":"MouseDown","Button":"Left"}',
      '{"Type":"MouseUp","Button":"Left"}',
    ];
    const tap = taps; // a full tap = 1 gesture on the device
    Future<void> doTap(WebRtcSenderSession s, PeerBinding p, Future<({String? sender, String? viewer})> Function() f) async {
      for (final m in tap) {
        await s.debugOnText(p, m, f);
      }
    }

    Future<WebRtcSenderSession> sender(FakeDevice device) async {
      SharedPreferences.setMockInitialValues({});
      final s = WebRtcSenderSession(settings: AppSettings(await SharedPreferences.getInstance()), device: device);
      s.debugArmControl(1000, 2000);
      s.addDebugKey(key);
      return s;
    }

    test('nothing but e2e_bind is processed before the viewer verified; then the sender replies and opens up', () async {
      final device = FakeDevice();
      final s = await sender(device);
      final peer = s.beginDebugPeer();
      final sent = <String>[];

      await doTap(s, peer, fps);
      await s.debugOnText(peer, '{"type":"authorization_request"}', fps, send: (m) async => sent.add(m));
      expect(device.performed, isEmpty);
      expect(sent, isEmpty);

      await s.debugOnText(peer, bindJson, fps, send: (m) async => sent.add(m));
      expect(peer.isVerified, isTrue);
      final reply = jsonDecode(sent.single) as Map<String, dynamic>;
      expect(reply['role'], senderRole);
      expect(reply['mac'], _senderMac);

      await doTap(s, peer, fps);
      expect(device.performed, hasLength(1));
    });

    test('a wrong key sends nothing back and the peer stays unverified', () async {
      final s = await sender(FakeDevice());
      final peer = s.beginDebugPeer();
      final sent = <String>[];
      final wrong = buildBindMessage(viewerRole, computeMac(deriveKeys('AAAAAAAAA', _serverId).e2eKey, viewerRole, _senderFp, _viewerFp));
      await s.debugOnText(peer, wrong, fps, send: (m) async => sent.add(m));
      expect(sent, isEmpty);
      expect(peer.isVerified, isFalse);
    });

    test('a bind with the sender role (echo) or a mismatching fingerprint is rejected', () async {
      final s = await sender(FakeDevice());
      final sent = <String>[];
      var peer = s.beginDebugPeer();
      await s.debugOnText(peer, buildBindMessage(senderRole, _senderMac), fps, send: (m) async => sent.add(m));
      expect(peer.isVerified, isFalse);

      peer = s.beginDebugPeer();
      await s.debugOnText(peer, bindJson, () async => (sender: _senderFp, viewer: _senderFp), send: (m) async => sent.add(m));
      expect(peer.isVerified, isFalse);
      expect(sent, isEmpty);
    });

    test('late verification of a replaced connection is ignored and does not verify the new one', () async {
      final device = FakeDevice();
      final s = await sender(device);
      final old = s.beginDebugPeer();
      final sent = <String>[];

      // The old connection's descriptions are still being read when a new offer replaces it.
      final slow = s.debugOnText(old, bindJson, () async {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        return (sender: _senderFp, viewer: _viewerFp);
      }, send: (m) async => sent.add(m));
      final fresh = s.beginDebugPeer();
      await slow;

      expect(old.isVerified, isFalse);
      expect(fresh.isVerified, isFalse);
      expect(sent, isEmpty);
      await doTap(s, fresh, fps);
      expect(device.performed, isEmpty);
    });

    test('overlapping connection requests are handled one after the other', () async {
      final s = await sender(FakeDevice());
      final events = <String>[];
      final gateA = Completer<void>();
      s.offerOverride = (id) async {
        events.add('start $id');
        if (id == 'A') await gateA.future; // A is suspended, e.g. in pc.close()
        events.add('end $id');
      };
      const a = SignalingMessage(type: SignalingType.connectionRequest, senderId: 'A');
      const b = SignalingMessage(type: SignalingType.connectionRequest, senderId: 'B');
      s.debugSignal(a);
      s.debugSignal(b);
      await Future<void>.delayed(const Duration(milliseconds: 20));
      expect(events, ['start A'], reason: 'B must not start while A is still replacing the peer');

      gateA.complete();
      await Future<void>.delayed(const Duration(milliseconds: 20));
      expect(events, ['start A', 'end A', 'start B', 'end B']);
    });

    test('the shared video track stays off for an unverified peer while the old connection is still closing', () async {
      final s = await sender(FakeDevice());
      final a = s.beginDebugPeer();
      await s.debugOnText(a, bindJson, fps, send: (_) async {});
      expect(s.videoEnabledApplied, isTrue, reason: 'verified current peer sees the screen');

      final closeGate = Completer<void>();
      final closing = s.debugRetirePeer(() => closeGate.future); // old pc.close() is slow
      expect(s.videoEnabledApplied, isFalse, reason: 'disabled before the close is awaited');

      final b = s.beginDebugPeer(); // the next request's peer, not verified
      // A late bind of the replaced peer must not switch the track on for B.
      await s.debugOnText(a, bindJson, fps, send: (_) async {});
      expect(s.videoEnabledApplied, isFalse);
      expect(b.isVerified, isFalse);

      // Even B's own valid bind does not enable the track while A's connection is still closing.
      await s.debugOnText(b, bindJson, fps, send: (_) async {});
      expect(b.isVerified, isTrue);
      expect(s.videoEnabledApplied, isFalse);

      closeGate.complete();
      await closing;
      expect(s.videoEnabledApplied, isTrue, reason: 'B is verified and the only connection left');
    });
  });
}
