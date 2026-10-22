import 'e2e_binding.dart';
import 'ids.dart';

/// The single place that turns the passkey shown on screen into what goes on the wire.
///
/// The raw passkey never leaves the device (the signaling server sees everything we send it): the server gets
/// `authKey` and the peers prove to each other over the data channel with `e2eKey`, both HKDF-derived from the
/// passkey and the Server ID (see e2e_binding.dart).
class PasskeyCredential {
  PasskeyCredential(String typedOrDisplayed) : passkey = normalizePasskey(typedOrDisplayed);

  /// Canonical 9-character passkey (upper case, no separators).
  final String passkey;

  bool get isComplete => passkey.length == passkeyLength;

  /// Value sent as `Passkey` and used as HMAC key for request signing: the auth key for [serverId].
  String wireValue(String serverId) => deriveKeys(passkey, serverId).authKey;

  /// Key for the e2e_bind handshake. Stays on the device.
  List<int> e2eKey(String serverId) => deriveKeys(passkey, serverId).e2eKey;
}
