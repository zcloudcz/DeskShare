import 'ids.dart';

/// The single place that turns the passkey shown on screen into what goes on the wire
/// (the `Passkey` field and the HMAC key).
///
/// Today the wire value IS the raw passkey. The planned "T3" change (HKDF-derived auth key + e2e_bind
/// data channel handshake) only has to change [wireValue]; no other file knows how the key is made.
class PasskeyCredential {
  PasskeyCredential(String typedOrDisplayed) : passkey = normalizePasskey(typedOrDisplayed);

  /// Canonical 9-character passkey (upper case, no separators).
  final String passkey;

  bool get isComplete => passkey.length == passkeyLength;

  /// Value sent as `Passkey` and used as HMAC key for [serverId].
  String wireValue(String serverId) => passkey;
}
