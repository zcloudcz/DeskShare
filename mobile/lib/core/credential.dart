import 'e2e_binding.dart' as e2e;
import 'ids.dart';

/// The single place that turns what the user typed (or the generated passkey) into what goes on the wire.
///
/// The raw passkey/password never leaves the device (the signaling server sees everything we send it): the server
/// gets `authKey` and the peers prove to each other over the data channel with `e2eKey`, both HKDF-derived from
/// the secret and the Server ID (see e2e_binding.dart). The viewer field accepts a 9-character passkey or a
/// permanent password (12+ characters).
class PasskeyCredential {
  PasskeyCredential(this.typed) : passkey = normalizePasskey(typed);

  /// Exactly what was typed (a permanent password is case sensitive and keeps dashes and spaces).
  final String typed;

  /// Canonical 9-character passkey (upper case, no separators).
  final String passkey;

  bool get isPasskey => e2e.looksLikePasskey(typed);
  bool get isPermanentPassword => e2e.isPermanentPassword(typed);

  bool get isComplete => isPasskey || isPermanentPassword;

  /// Both keys for [serverId]; the password path is slow (PBKDF2) and runs in a background isolate.
  Future<e2e.DerivedKeys> deriveFor(String serverId) => e2e.deriveFromUserInput(typed, serverId);

  /// Auth key of a generated passkey (sender side; synchronous, no PBKDF2). Sent as `Passkey` and used as the
  /// request-signing HMAC key.
  String wireValue(String serverId) => e2e.deriveKeys(passkey, serverId).authKey;

  /// e2e key of a generated passkey (sender side). Stays on the device.
  List<int> e2eKey(String serverId) => e2e.deriveKeys(passkey, serverId).e2eKey;
}
