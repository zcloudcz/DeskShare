import 'dart:convert';

import 'package:flutter/foundation.dart' show kDebugMode;
import 'package:http/http.dart' as http;

import 'credential.dart';
import 'ids.dart';
import 'request_signer.dart';

const defaultSignalingUrl = 'wss://app.deskshare.zcloud.cz/signal';

/// STUN/TURN server as handed out by the signaling server (same shape as the browser's RTCIceServer).
class IceServer {
  const IceServer({required this.urls, this.username, this.credential});

  final List<String> urls;
  final String? username;
  final String? credential;

  static IceServer fromJson(Map<String, dynamic> j) {
    final u = j['urls'] ?? j['Urls'];
    return IceServer(
      urls: u is List ? u.cast<String>() : [u as String],
      username: (j['username'] ?? j['Username']) as String?,
      credential: (j['credential'] ?? j['Credential']) as String?,
    );
  }

  /// Map for flutter_webrtc's `iceServers` configuration.
  Map<String, dynamic> toRtc() => {
        'urls': urls,
        if (username != null) 'username': username,
        if (credential != null) 'credential': credential,
      };
}

/// Why [url] cannot be used as signaling address, or null when it is fine.
/// The passkey and the owner secret travel over this connection, so release builds accept `wss://` only;
/// plain `ws://` (and the `http://` REST calls derived from it) is a debug-build convenience for a local server.
String? signalingUrlError(String url, {bool allowInsecure = kDebugMode}) {
  final uri = Uri.tryParse(url.trim());
  if (uri == null || uri.host.isEmpty) return 'Use a wss:// address, e.g. $defaultSignalingUrl';
  if (uri.scheme == 'wss') return null;
  if (uri.scheme == 'ws') {
    return allowInsecure ? null : 'Only secure wss:// servers are allowed (ws:// would send your passkey unencrypted)';
  }
  return 'Use a wss:// address, e.g. $defaultSignalingUrl';
}

class SignalingException implements Exception {
  SignalingException(this.message, {this.statusCode, this.retryAfter});
  final String message;
  final int? statusCode;

  /// Server's Retry-After (HTTP 429/503), when it sent one.
  final Duration? retryAfter;
  @override
  String toString() => message;
}

class SessionGrant {
  const SessionGrant({
    required this.webSocketToken,
    this.resumeToken,
    this.iceServers = const [],
    this.remoteControlEnabled = false,
  });

  final String webSocketToken;
  final String? resumeToken;
  final List<IceServer> iceServers;
  final bool remoteControlEnabled;

  static SessionGrant fromJson(Map<String, dynamic> j) {
    final token = j['webSocketToken'] as String?;
    if (token == null || token.isEmpty) {
      throw SignalingException('Server did not return a WebSocket token');
    }
    return SessionGrant(
      webSocketToken: token,
      resumeToken: j['resumeToken'] as String?,
      iceServers: [
        for (final s in (j['iceServers'] as List? ?? const [])) IceServer.fromJson(s as Map<String, dynamic>)
      ],
      remoteControlEnabled: j['remoteControlEnabled'] == true,
    );
  }
}

/// HTTP side of the signaling server: POST /register (sender) and /authenticate (viewer).
class SignalingApi {
  SignalingApi(String signalingUrl, {http.Client? client, bool allowInsecure = kDebugMode})
      : wsUrl = _checked(signalingUrl, allowInsecure),
        _client = client ?? http.Client();

  static Uri _checked(String url, bool allowInsecure) {
    final error = signalingUrlError(url, allowInsecure: allowInsecure);
    if (error != null) throw SignalingException(error);
    return Uri.parse(url.trim());
  }

  /// The `/signal` WebSocket URL.
  final Uri wsUrl;
  final http.Client _client;

  /// ws://host:port/signal -> http://host:port (same rule as client.js `getBaseUrl`).
  Uri get baseUrl => Uri(
        scheme: wsUrl.scheme == 'wss' ? 'https' : 'http',
        host: wsUrl.host,
        port: wsUrl.hasPort ? wsUrl.port : null,
      );

  Uri signalUrl(String token) =>
      wsUrl.replace(queryParameters: {...wsUrl.queryParameters, 'token': token});

  Future<Map<String, dynamic>> _post(String path, Map<String, dynamic> body) async {
    final http.Response r;
    try {
      r = await _client
          .post(baseUrl.resolve(path),
              headers: {'Content-Type': 'application/json'}, body: jsonEncode(body))
          .timeout(const Duration(seconds: 10));
    } catch (e) {
      throw SignalingException('Cannot reach the signaling server ($e)');
    }
    if (r.statusCode == 401) {
      throw SignalingException('Invalid passkey or server ID', statusCode: 401);
    }
    if (r.statusCode == 403) {
      throw SignalingException('Server ID already in use', statusCode: 403);
    }
    if (r.statusCode < 200 || r.statusCode >= 300) {
      final seconds = int.tryParse(r.headers['retry-after'] ?? '');
      throw SignalingException(r.statusCode == 429 ? 'Too many attempts, try again later' : 'Server error (${r.statusCode})',
          statusCode: r.statusCode, retryAfter: seconds == null ? null : Duration(seconds: seconds));
    }
    return jsonDecode(r.body) as Map<String, dynamic>;
  }

  /// Viewer: POST /authenticate with an HMAC-signed body (`ClientAuthenticationMessage`).
  Future<SessionGrant> authenticate({
    required String serverId,
    required String authKey,
    required String clientId,
    DateTime? now,
    String? nonce,
  }) async {
    final timestamp = formatRequestTimestamp(now ?? DateTime.now());
    final n = nonce ?? uuidV4();
    final key = authKey;
    final json = await _post('/authenticate', {
      'ServerId': serverId,
      'Passkey': key,
      'ClientId': clientId,
      'Timestamp': timestamp,
      'Nonce': n,
      'Signature': signRequest(serverId: serverId, key: key, timestamp: timestamp, nonce: n),
    });
    return SessionGrant.fromJson(json);
  }

  /// Sender: POST /register (`ServerRegistrationMessage`); repeated for every passkey rotation.
  /// Throws [SignalingException] with statusCode 403 when another installation owns the Server ID.
  Future<SessionGrant> register({
    required String serverId,
    required PasskeyCredential credential,
    required DateTime validTo,
    required bool remoteControlEnabled,
    required String ownerSecret,
  }) async {
    final json = await _post('/register', {
      'ServerId': serverId,
      'Passkey': credential.wireValue(serverId),
      'ValidTo': validTo.toUtc().toIso8601String(),
      'RemoteControlEnabled': remoteControlEnabled,
      'TrustClientPermanent': false,
      'OwnerSecret': ownerSecret,
    });
    if (json['success'] != true) {
      throw SignalingException((json['errorMessage'] as String?) ?? 'Registration failed');
    }
    return SessionGrant.fromJson(json);
  }

  void close() => _client.close();
}
