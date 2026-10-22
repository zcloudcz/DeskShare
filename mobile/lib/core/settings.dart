import 'dart:convert';
import 'dart:math';

import 'package:shared_preferences/shared_preferences.dart';

import 'ids.dart';
import 'signaling_api.dart';

/// Persistent app state: signaling URL (configurable for self-hosting/tests) and this phone's sender identity.
class AppSettings {
  AppSettings(this._prefs);

  final SharedPreferences _prefs;

  static Future<AppSettings> load() async => AppSettings(await SharedPreferences.getInstance());

  String get signalingUrl => _prefs.getString('signalingUrl') ?? defaultSignalingUrl;

  Future<void> setSignalingUrl(String url) => url.trim().isEmpty || url.trim() == defaultSignalingUrl
      ? _prefs.remove('signalingUrl')
      : _prefs.setString('signalingUrl', url.trim());

  /// Stable Server ID of this phone, created on first use. A changing ID would break the server-side
  /// ownership claim (see `ServerIdGenerator`).
  String get serverId {
    final stored = _prefs.getString('serverId');
    if (stored != null && stored.startsWith(serverIdPrefix)) return stored;
    final id = generateServerId();
    _prefs.setString('serverId', id);
    return id;
  }

  /// Used after a 403 from /register (another installation owns the ID).
  String regenerateServerId() {
    final id = generateServerId();
    _prefs.setString('serverId', id);
    return id;
  }

  /// Proves to the server that we own [serverId] (trust on first use). 32 random bytes, Base64.
  String get ownerSecret {
    final stored = _prefs.getString('ownerSecret');
    if (stored != null && stored.isNotEmpty) return stored;
    final r = Random.secure();
    final secret = base64.encode(List<int>.generate(32, (_) => r.nextInt(256)));
    _prefs.setString('ownerSecret', secret);
    return secret;
  }
}
