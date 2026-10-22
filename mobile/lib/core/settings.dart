import 'dart:convert';
import 'dart:math';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'ids.dart';
import 'signaling_api.dart';

/// Where the owner secret lives. The real one is the Android Keystore / iOS Keychain; whoever reads the
/// secret can register under this phone's Server ID (take over the identity), so it must not sit in
/// plain SharedPreferences.
abstract class SecretStore {
  Future<String?> read();
  Future<void> write(String value);
}

class KeystoreSecretStore implements SecretStore {
  const KeystoreSecretStore();
  static const _key = 'deskshare.ownerSecret';
  static const _storage = FlutterSecureStorage();

  @override
  Future<String?> read() async {
    try {
      return await _storage.read(key: _key);
    } catch (_) {
      return null; // corrupted keystore entry (e.g. restored on another device): a new secret is made
    }
  }

  @override
  Future<void> write(String value) => _storage.write(key: _key, value: value);
}

class MemorySecretStore implements SecretStore {
  MemorySecretStore([this.value]);
  String? value;
  @override
  Future<String?> read() async => value;
  @override
  Future<void> write(String v) async => value = v;
}

/// Persistent app state: signaling URL (configurable for self-hosting/tests) and this phone's sender identity.
class AppSettings {
  AppSettings(this._prefs, {String? ownerSecret}) : _ownerSecret = ownerSecret;

  final SharedPreferences _prefs;
  String? _ownerSecret;

  static const _legacySecretKey = 'ownerSecret';

  /// Loads the owner secret from [store]; a secret an earlier version kept in SharedPreferences is moved
  /// into the store and removed from the preferences.
  static Future<AppSettings> load({SecretStore store = const KeystoreSecretStore(), SharedPreferences? prefs}) async {
    final p = prefs ?? await SharedPreferences.getInstance();
    var secret = await store.read();
    if (secret == null || secret.isEmpty) {
      final legacy = p.getString(_legacySecretKey);
      secret = (legacy != null && legacy.isNotEmpty) ? legacy : _newSecret();
      await store.write(secret);
    }
    await p.remove(_legacySecretKey);
    return AppSettings(p, ownerSecret: secret);
  }

  static String _newSecret() {
    final r = Random.secure();
    return base64.encode(List<int>.generate(32, (_) => r.nextInt(256)));
  }

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
  /// Kept in memory only after [load]; tests that skip [load] get a throwaway secret.
  String get ownerSecret => _ownerSecret ??= _newSecret();
}
