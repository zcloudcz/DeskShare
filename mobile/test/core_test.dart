import 'dart:convert';
import 'dart:math';

import 'package:deskshare/core/credential.dart';
import 'package:deskshare/core/ids.dart';
import 'package:deskshare/core/input_message.dart';
import 'package:deskshare/core/request_signer.dart';
import 'package:deskshare/core/signaling_api.dart';
import 'package:deskshare/core/signaling_message.dart';
import 'package:deskshare/core/signaling_socket.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('Server ID', () {
    test('normalizes every accepted spelling', () {
      for (final input in [
        '123 456 789',
        '123-456-789',
        '123456789',
        'server-123456789',
        'SERVER-123456789',
        '  123 456 789  ',
        'server-123 456 789',
      ]) {
        expect(normalizeServerId(input), 'server-123456789', reason: input);
      }
    });

    test('leaves anything else trimmed but unchanged (legacy IDs)', () {
      expect(normalizeServerId(' AABBCCDDEEFF '), 'AABBCCDDEEFF');
      expect(normalizeServerId('023456789'), '023456789', reason: 'first digit 0 is not a valid ID');
      expect(normalizeServerId('12345678'), '12345678');
      expect(normalizeServerId(null), '');
    });

    test('formats for display', () {
      expect(formatServerId('server-123456789'), '123 456 789');
      expect(formatServerId('legacy'), 'legacy');
    });

    test('generated IDs are valid and normalize to themselves', () {
      final r = Random(1);
      for (var i = 0; i < 200; i++) {
        final id = generateServerId(r);
        expect(RegExp(r'^server-[1-9]\d{8}$').hasMatch(id), isTrue, reason: id);
        expect(normalizeServerId(id), id);
      }
    });
  });

  group('Passkey', () {
    test('normalizes typed input', () {
      expect(normalizePasskey('abc-def-ghj'), 'ABCDEFGHJ');
      expect(normalizePasskey(' abc def ghj '), 'ABCDEFGHJ');
      expect(normalizePasskey(null), '');
    });

    test('formats XXX-XXX-XXX only for 9 characters', () {
      expect(formatPasskey('ABCDEFGHJ'), 'ABC-DEF-GHJ');
      expect(formatPasskey('ABC'), 'ABC');
    });

    test('generated passkeys use the sender alphabet and length', () {
      final r = Random(2);
      for (var i = 0; i < 200; i++) {
        final p = generatePasskey(r);
        expect(p.length, passkeyLength);
        expect(p.split('').every(passkeyCharset.contains), isTrue, reason: p);
      }
    });

    test('expiration is the end of the 45 s slot of the UTC day', () {
      expect(passkeyExpiration(DateTime.utc(2026, 1, 1, 0, 0, 0)), DateTime.utc(2026, 1, 1, 0, 0, 45));
      expect(passkeyExpiration(DateTime.utc(2026, 1, 1, 0, 0, 44, 999)), DateTime.utc(2026, 1, 1, 0, 0, 45));
      expect(passkeyExpiration(DateTime.utc(2026, 1, 1, 0, 0, 45)), DateTime.utc(2026, 1, 1, 0, 1, 30));
    });

    test('credential wire value is the HKDF auth key, never the raw passkey', () {
      final c = PasskeyCredential('abc-def-ghj');
      expect(c.isComplete, isTrue);
      // Independent value from Node's crypto.hkdfSync.
      expect(c.wireValue('server-123456789'), '8DafN8Ubr2W_rLEAy-YwIWFK_Ccy2mIQY9U4FC1bvuc');
      expect(c.wireValue('server-123456789'), isNot(contains('ABCDEFGHJ')));
      expect(PasskeyCredential('ABC').isComplete, isFalse);
    });
  });

  group('Request signing (RequestSigningService.SignRequest)', () {
    // Expected values computed with Node's crypto.createHmac over the exact string the C# code builds:
    // $"{serverId}|{timestamp:O}|{nonce}" with a UTC DateTime ("O" = 7 fractional digits, trailing Z).
    test('matches the C# signature for a timestamp with milliseconds', () {
      expect(
        signRequest(
          serverId: 'server-123456789',
          key: 'ABCDEFGHJ',
          timestamp: '2026-01-02T03:04:05.123Z',
          nonce: '11111111-2222-3333-4444-555555555555',
        ),
        'rkC13GYZpPHAnZ9rOeTV4as/12QacJIj7ETqRwwxFS4=',
      );
    });

    test('matches the C# signature for a whole-second timestamp', () {
      expect(
        signRequest(
          serverId: 'server-987654321',
          key: '23456789K',
          timestamp: '2026-10-22T14:48:41.000Z',
          nonce: 'nonce-1',
        ),
        '+f+GX2TCvDNTrSw2wpjj+FNs4EM1GgFGaO8PbD2z9R0=',
      );
    });

    test('timestamp has exactly 3 fractional digits like toISOString()', () {
      expect(formatRequestTimestamp(DateTime.utc(2026, 1, 2, 3, 4, 5, 7)), '2026-01-02T03:04:05.007Z');
      expect(formatRequestTimestamp(DateTime.utc(2026, 1, 2, 3, 4, 5, 0, 999)), '2026-01-02T03:04:05.000Z');
    });
  });

  group('Signaling messages', () {
    test('serializes with PascalCase keys and numeric type', () {
      final json = const SignalingMessage(
        type: SignalingType.connectionRequest,
        targetId: 'server-123456789',
      ).toJson();
      expect(json, {'Type': 5, 'TargetId': 'server-123456789'});
    });

    test('enum numbers match SignalingMessageType', () {
      expect(SignalingType.values.map((e) => e.index).toList(), [0, 1, 2, 3, 4, 5, 6, 7]);
      expect(SignalingType.offer.index, 0);
      expect(SignalingType.identify.index, 3);
      expect(SignalingType.pong.index, 7);
    });

    test('parses a server message, ignoring unknown fields', () {
      final m = SignalingMessage.fromJson(jsonDecode(
        '{"Type":0,"SenderId":"server-123456789","TargetId":"client-1","Sdp":"v=0","Candidate":null,'
        '"Timestamp":"2026-01-02T03:04:05Z","Extra":1}',
      ) as Map<String, dynamic>)!;
      expect(m.type, SignalingType.offer);
      expect(m.senderId, 'server-123456789');
      expect(m.sdp, 'v=0');
      expect(m.candidate, isNull);
    });

    test('resolves a null sdpMid from the SDP (Android WebRTC aborts on null)', () {
      const sdp = 'v=0\r\nm=audio 9 RTP/AVP 0\r\na=mid:audio0\r\nm=video 9 RTP/AVP 96\r\na=mid:1\r\n';
      expect(midOfMLine(sdp, 0), 'audio0');
      expect(midOfMLine(sdp, 1), '1');
      expect(midOfMLine(sdp, 5), '5');
      expect(midOfMLine(null, 0), '0');
    });

    test('unknown message type is ignored, not fatal', () {
      expect(SignalingMessage.fromJson({'Type': 99}), isNull);
      expect(SignalingMessage.fromJson({'Type': 'Offer'}), isNull);
    });
  });

  group('InputMessage wire format', () {
    test('writes PascalCase Type name like remote-control.js', () {
      final j = const InputMessage.down(MouseButton.right).toJson();
      expect(j['Type'], 'MouseDown');
      expect(j['Button'], 'Right');
      expect(j.containsKey('X'), isFalse);
      final move = const InputMessage.move(0.25, 0.75).toJson();
      expect(move['Type'], 'MouseMove');
      expect(move['X'], 0.25);
      expect(move['Y'], 0.75);
      expect(const InputMessage.wheel(-120).toJson()['WheelDelta'], -120);
    });

    test('round trips and accepts .NET numeric enums', () {
      final m = InputMessage.fromJson({'Type': 'MouseDown', 'Button': 'Middle'})!;
      expect(m.type, InputType.mouseDown);
      expect(m.button, MouseButton.middle);
      expect(InputMessage.fromJson({'Type': 2, 'Button': 0})!.type, InputType.mouseUp);
      expect(InputMessage.fromJson({'Type': 'Nope'}), isNull);
      expect(InputMessage.fromJson({'type': 'authorization_request'}), isNull);
    });
  });

  group('SignalingApi', () {
    test('authenticate posts the signed body the server validates', () async {
      late http.Request seen;
      final api = SignalingApi('wss://example.test/signal', client: MockClient((req) async {
        seen = req;
        return http.Response(
          jsonEncode({
            'success': true,
            'remoteControlEnabled': true,
            'webSocketToken': 'tok',
            'resumeToken': 'res',
            'iceServers': [
              {'urls': ['stun:a'], 'username': 'u', 'credential': 'c'}
            ],
          }),
          200,
        );
      }));

      final grant = await api.authenticate(
        serverId: 'server-123456789',
        credential: PasskeyCredential('abc-def-ghj'),
        clientId: 'client-x',
        now: DateTime.utc(2026, 1, 2, 3, 4, 5, 123),
        nonce: '11111111-2222-3333-4444-555555555555',
      );

      expect(seen.url.toString(), 'https://example.test/authenticate');
      final body = jsonDecode(seen.body) as Map<String, dynamic>;
      expect(body, {
        'ServerId': 'server-123456789',
        'Passkey': '8DafN8Ubr2W_rLEAy-YwIWFK_Ccy2mIQY9U4FC1bvuc',
        'ClientId': 'client-x',
        'Timestamp': '2026-01-02T03:04:05.123Z',
        'Nonce': '11111111-2222-3333-4444-555555555555',
        'Signature': 'jWWHYEH/plKy7MVlDlRsMxI/L++oXjX1uY34KxiH830=',
      });
      expect(grant.webSocketToken, 'tok');
      expect(grant.iceServers.single.toRtc(), {'urls': ['stun:a'], 'username': 'u', 'credential': 'c'});
      expect(api.signalUrl('tok').toString(), 'wss://example.test/signal?token=tok');
    });

    test('release builds only accept wss:// (API and socket)', () async {
      expect(signalingUrlError('wss://example.test/signal', allowInsecure: false), isNull);
      expect(signalingUrlError('ws://example.test/signal', allowInsecure: false), contains('wss://'));
      expect(signalingUrlError('ws://10.0.2.2:5171/signal', allowInsecure: true), isNull);
      expect(signalingUrlError('http://example.test/signal', allowInsecure: true), isNotNull);
      expect(signalingUrlError('wss:///signal'), isNotNull);
      expect(() => SignalingApi('ws://example.test/signal', allowInsecure: false), throwsA(isA<SignalingException>()));
      await expectLater(
        SignalingSocket.connect(Uri.parse('ws://example.test/signal'), allowInsecure: false),
        throwsA(isA<SignalingException>()),
      );
    });

    test('keeps a non-default port and maps ws to http', () {
      final api = SignalingApi('ws://10.0.2.2:5171/signal');
      expect(api.baseUrl.toString(), 'http://10.0.2.2:5171');
    });

    test('401 becomes a readable error', () async {
      final api = SignalingApi('wss://example.test/signal', client: MockClient((_) async => http.Response('', 401)));
      expect(
        api.authenticate(serverId: 'server-123456789', credential: PasskeyCredential('ABCDEFGHJ'), clientId: 'c'),
        throwsA(isA<SignalingException>().having((e) => e.message, 'message', 'Invalid passkey or server ID')),
      );
    });

    test('429 carries Retry-After', () async {
      final api = SignalingApi('wss://example.test/signal',
          client: MockClient((_) async => http.Response('', 429, headers: {'retry-after': '7'})));
      await expectLater(
        api.register(
          serverId: 'server-123456789',
          credential: PasskeyCredential('ABCDEFGHJ'),
          validTo: DateTime.utc(2026),
          remoteControlEnabled: false,
          ownerSecret: 's',
        ),
        throwsA(isA<SignalingException>().having((e) => e.retryAfter, 'retryAfter', const Duration(seconds: 7))),
      );
    });

    test('register sends the registration message and reports 403', () async {
      late Map<String, dynamic> body;
      var status = 200;
      final api = SignalingApi('wss://example.test/signal', client: MockClient((req) async {
        body = jsonDecode(req.body) as Map<String, dynamic>;
        return http.Response(jsonEncode({'success': true, 'serverId': 'x', 'webSocketToken': 'tok'}), status);
      }));

      await api.register(
        serverId: 'server-123456789',
        credential: PasskeyCredential('ABCDEFGHJ'),
        validTo: DateTime.utc(2026, 1, 2, 3, 4, 45),
        remoteControlEnabled: true,
        ownerSecret: 'secret',
      );
      expect(body['ServerId'], 'server-123456789');
      expect(body['Passkey'], '8DafN8Ubr2W_rLEAy-YwIWFK_Ccy2mIQY9U4FC1bvuc');
      expect(body['RemoteControlEnabled'], true);
      expect(body['TrustClientPermanent'], false);
      expect(body['OwnerSecret'], 'secret');
      expect(body['ValidTo'], '2026-01-02T03:04:45.000Z');

      status = 403;
      expect(
        api.register(
          serverId: 'server-123456789',
          credential: PasskeyCredential('ABCDEFGHJ'),
          validTo: DateTime.utc(2026),
          remoteControlEnabled: false,
          ownerSecret: 's',
        ),
        throwsA(isA<SignalingException>().having((e) => e.statusCode, 'statusCode', 403)),
      );
    });
  });
}
