import 'dart:convert';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:pointycastle/export.dart' show HMac, PBKDF2KeyDerivator, Pbkdf2Parameters, SHA256Digest;
import 'package:unorm_dart/unorm_dart.dart' as unorm;

import 'ids.dart';

/// Mirror of src/Core/Auth/E2EBinding.cs and E2EKeyRing.cs (tests/js/e2e-vector.mjs and the Dart tests share
/// one fixed vector, so a drift in any of the three implementations is caught).
///
/// Why: the signaling server relays the SDP (and its DTLS fingerprints) and sees the passkey. So the server only
/// gets `authKey`, derived from the passkey; `e2eKey` (also derived) stays on the two peers and proves over the
/// data channel that both see the same fingerprints (no man in the middle).

const e2eBindVersion = 1;
const bindTimeout = Duration(seconds: 10);
const senderRole = 'sender';
const viewerRole = 'viewer';

/// Shown on the viewer when the check fails (same text as the web viewer).
const bindFailedMessage = 'Secure connection check failed. The connection may be intercepted. Do not continue.';

const _saltPrefix = 'DeskShare/v1/';
const _macContext = 'DeskShare-bind-v1|';

class DerivedKeys {
  const DerivedKeys(this.authKey, this.e2eKey);

  /// base64url without padding, 43 characters. Goes to the server instead of the passkey.
  final String authKey;

  /// 32 bytes, never sent anywhere.
  final List<int> e2eKey;
}

/// RFC 5869 HKDF-SHA256 for one output block (32 bytes = the hash length, so expand is a single HMAC).
List<int> _hkdf32(List<int> ikm, List<int> salt, String info) {
  final prk = Hmac(sha256, salt).convert(ikm).bytes;
  return Hmac(sha256, prk).convert([...utf8.encode(info), 1]).bytes;
}

/// The shared HKDF step: passkey and permanent password differ only in how the input secret is made.
DerivedKeys _deriveFromSecret(List<int> ikm, String serverId) {
  final salt = utf8.encode('$_saltPrefix$serverId');
  return DerivedKeys(
    base64Url.encode(_hkdf32(ikm, salt, 'auth')).replaceAll('=', ''),
    _hkdf32(ikm, salt, 'e2e'),
  );
}

DerivedKeys deriveKeys(String passkey, String serverId) =>
    _deriveFromSecret(utf8.encode(normalizePasskey(passkey)), serverId);

/// Shortest permanent password accepted (longer than the 9-character passkey, so the two never look alike).
const permanentPasswordMinLength = 12;
const permanentPasswordIterations = 600000;
const _permanentSaltPrefix = 'DeskShare/v1/perm/';

/// Keys from a long-lived password (E2EBinding.FromPermanentPassword): PBKDF2-HMAC-SHA256 over the NFC-normalized
/// password with the public salt "DeskShare/v1/perm/" + serverId, then the usual HKDF step. Slow on purpose
/// (600 000 iterations): call it off the UI isolate, see [deriveFromUserInput].
///
/// pointycastle (pure Dart) because package:crypto has no PBKDF2, and 600 000 `Hmac.convert` calls would re-hash
/// the key pads every time; pointycastle's HMac keeps the padded-key state. unorm_dart supplies the NFC step
/// (Dart has no Unicode normalization), needed so a macOS-typed (decomposed) password gives the same keys.
DerivedKeys fromPermanentPassword(String password, String serverId, {int iterations = permanentPasswordIterations}) {
  final derivator = PBKDF2KeyDerivator(HMac(SHA256Digest(), 64))
    ..init(Pbkdf2Parameters(Uint8List.fromList(utf8.encode('$_permanentSaltPrefix$serverId')), iterations, 32));
  final master = derivator.process(Uint8List.fromList(utf8.encode(unorm.nfc(password))));
  return _deriveFromSecret(master, serverId);
}

/// True when the input is a passkey: 9 ASCII letters/digits once dashes and spaces are removed.
bool looksLikePasskey(String? input) {
  final p = normalizePasskey(input);
  return p.length == passkeyLength && RegExp(r'^[A-Z0-9]+$').hasMatch(p);
}

bool isPermanentPassword(String? input) =>
    !looksLikePasskey(input) && (input ?? '').length >= permanentPasswordMinLength;

/// What the viewer does with its one "passkey or password" field. The password path runs in a background isolate.
Future<DerivedKeys> deriveFromUserInput(String input, String serverId) async {
  if (looksLikePasskey(input)) return deriveKeys(input, serverId);
  if (!isPermanentPassword(input)) {
    throw ArgumentError(
        'Enter the 9-character passkey or the permanent password (at least $permanentPasswordMinLength characters).');
  }
  return Isolate.run(() => fromPermanentPassword(input, serverId));
}

final _fingerprintLine = RegExp(
  r'^a=fingerprint:sha-256[ \t]+([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){31})[ \t]*$',
  multiLine: true,
);

/// The sha-256 DTLS fingerprint of an SDP (upper case, colons), or null when there is none or the SDP names two
/// different ones (a second certificate would not be bound).
String? getFingerprint(String? sdp) {
  if (sdp == null || sdp.isEmpty) return null;
  String? found;
  for (final m in _fingerprintLine.allMatches(sdp.replaceAll('\r', ''))) {
    final fp = m.group(1)!.toUpperCase();
    if (found != null && found != fp) return null;
    found = fp;
  }
  return found;
}

String _hex(List<int> bytes) => [for (final b in bytes) b.toRadixString(16).padLeft(2, '0')].join();

/// Lower case hex HMAC-SHA256 over role and both fingerprints (fixed order: sender, viewer).
String computeMac(List<int> e2eKey, String role, String senderFp, String viewerFp) =>
    _hex(Hmac(sha256, e2eKey).convert(utf8.encode('$_macContext$role|$senderFp|$viewerFp')).bytes);

/// Constant-time compare, so timing does not reveal how many leading characters of a guess were right.
bool verifyMac(List<int> e2eKey, String role, String senderFp, String viewerFp, String? mac) {
  if (mac == null || mac.length != 64) return false;
  final expected = computeMac(e2eKey, role, senderFp, viewerFp);
  final given = mac.toLowerCase();
  var diff = 0;
  for (var i = 0; i < 64; i++) {
    diff |= expected.codeUnitAt(i) ^ given.codeUnitAt(i);
  }
  return diff == 0;
}

String buildBindMessage(String role, String mac) =>
    jsonEncode({'type': 'e2e_bind', 'v': e2eBindVersion, 'role': role, 'mac': mac});

/// `role` and `mac` of an `e2e_bind` message, or null when the shape or version is wrong.
({String role, String mac})? tryReadBind(Object? json) {
  if (json is! Map) return null;
  final v = json['v'], role = json['role'], mac = json['mac'];
  if (v != e2eBindVersion || role is! String || mac is! String) return null;
  return (role: role, mac: mac);
}

/// The sender's e2e keys, one per recent passkey (E2EKeyRing.cs): a viewer that typed the previous passkey is
/// still connecting when the passkey rotates, and a key that verified a viewer is kept for its reconnects.
class E2EKeyRing {
  E2EKeyRing({DateTime Function()? utcNow}) : _utcNow = utcNow ?? (() => DateTime.now().toUtc());

  static const grace = Duration(minutes: 2);
  static const verifiedRetention = Duration(hours: 8);

  final DateTime Function() _utcNow;
  final List<({List<int> key, DateTime expires})> _keys = [];

  int get count {
    _prune();
    return _keys.length;
  }

  void add(List<int> e2eKey, DateTime passkeyValidTo) {
    _prune();
    final expires = passkeyValidTo.add(grace);
    final i = _keys.indexWhere((k) => _same(k.key, e2eKey));
    if (i < 0) {
      _keys.add((key: e2eKey, expires: expires));
    } else if (expires.isAfter(_keys[i].expires)) {
      _keys[i] = (key: e2eKey, expires: expires); // re-registration of the same passkey: refresh, don't duplicate
    }
  }

  /// The key that makes [mac] a valid bind of [role] over the fingerprints, or null. All keys are tried.
  List<int>? tryVerify(String role, String senderFp, String viewerFp, String mac) {
    _prune();
    int? matched;
    for (var i = 0; i < _keys.length; i++) {
      if (verifyMac(_keys[i].key, role, senderFp, viewerFp, mac)) matched ??= i;
    }
    if (matched == null) return null;
    final keep = _utcNow().add(verifiedRetention);
    final k = _keys[matched];
    if (keep.isAfter(k.expires)) _keys[matched] = (key: k.key, expires: keep);
    return k.key;
  }

  void clear() => _keys.clear();

  void _prune() {
    final now = _utcNow();
    _keys.removeWhere((k) => !k.expires.isAfter(now));
  }

  static bool _same(List<int> a, List<int> b) {
    if (a.length != b.length) return false;
    for (var i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }
}

/// Verification state of ONE peer connection (PeerContext.cs / bind-gate.js). Verifying needs awaits (the SDPs are
/// read asynchronously), and a new offer can replace the connection meanwhile. With one object per connection a
/// late result reaches only its own object, and [retire] (called when the connection is replaced) makes that
/// object refuse to become verified. Never keep a "verified" flag on the long-lived session.
class PeerBinding {
  bool _verified = false;
  bool _dead = false;

  /// Set by the viewer once its own bind went out (a lost connection after that counts as a failed check).
  bool bindSent = false;

  bool get isVerified => _verified && !_dead;
  bool get isRetired => _dead;

  /// Marks verified unless the connection was replaced meanwhile; false = caller must do nothing for this peer.
  bool tryVerify() {
    if (_dead) return false;
    _verified = true;
    return true;
  }

  void retire() {
    _dead = true;
    _verified = false;
  }
}
