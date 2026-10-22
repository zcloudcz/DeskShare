/// Server ID / passkey helpers. Mirrors `ServerIdGenerator` and `AuthenticationService` in src/Core.
library;

import 'dart:math';

const serverIdPrefix = 'server-';

/// Passkey alphabet of `AuthenticationService` (no 0/O/1/I to avoid typos).
const passkeyCharset = '23456789ABCDEFGHJKLMNPQRSTUVWXYZ';
const passkeyLength = 9;

/// Seconds a passkey stays valid before the sender rotates it.
const passkeyValiditySeconds = 45;

final _digitsOnly = RegExp(r'^[1-9]\d{8}$');

/// "123 456 789", "123-456-789", "123456789", "server-123456789" -> "server-123456789".
/// Anything else is only trimmed (legacy IDs still reach the server as typed).
String normalizeServerId(String? input) {
  final trimmed = (input ?? '').trim();
  final rest = trimmed.toLowerCase().startsWith(serverIdPrefix)
      ? trimmed.substring(serverIdPrefix.length)
      : trimmed;
  final digits = rest.replaceAll(RegExp(r'[ -]'), '');
  return _digitsOnly.hasMatch(digits) ? '$serverIdPrefix$digits' : trimmed;
}

/// "server-123456789" -> "123 456 789"; anything else is returned unchanged.
String formatServerId(String serverId) {
  final m = RegExp(r'^server-([1-9]\d{2})(\d{3})(\d{3})$').firstMatch(serverId);
  return m == null ? serverId : '${m[1]} ${m[2]} ${m[3]}';
}

/// New random Server ID: "server-" + 9 digits, first digit 1-9 (like the desktop sender).
String generateServerId([Random? random]) {
  final r = random ?? Random.secure();
  return '$serverIdPrefix${100000000 + r.nextInt(900000000)}';
}

/// What the user typed -> canonical passkey: no spaces/dashes, upper case.
String normalizePasskey(String? input) =>
    (input ?? '').replaceAll(RegExp(r'[\s-]'), '').toUpperCase();

/// "ABCDEFGHJ" -> "ABC-DEF-GHJ"; anything not 9 characters is returned unchanged.
String formatPasskey(String passkey) => passkey.length == passkeyLength
    ? '${passkey.substring(0, 3)}-${passkey.substring(3, 6)}-${passkey.substring(6)}'
    : passkey;

String generatePasskey([Random? random]) {
  final r = random ?? Random.secure();
  return String.fromCharCodes(List.generate(
      passkeyLength, (_) => passkeyCharset.codeUnitAt(r.nextInt(passkeyCharset.length))));
}

/// End of the current 45 s slot of the UTC day (`AuthenticationService.GetPasskeyExpiration`).
DateTime passkeyExpiration(DateTime now) {
  final utc = now.toUtc();
  final day = DateTime.utc(utc.year, utc.month, utc.day);
  final seconds = utc.difference(day).inSeconds;
  final slot = seconds ~/ passkeyValiditySeconds;
  return day.add(Duration(seconds: (slot + 1) * passkeyValiditySeconds));
}

/// Fresh viewer identity per connection ("client-" + 32 hex). The signaling server refuses "server-"
/// prefixes and IDs that are still in use, so a new one per connect avoids a stale-socket clash.
String generateClientId([Random? random]) {
  final r = random ?? Random.secure();
  return 'client-${List.generate(16, (_) => r.nextInt(256).toRadixString(16).padLeft(2, '0')).join()}';
}

/// Random UUID v4 (nonce).
String uuidV4([Random? random]) {
  final r = random ?? Random.secure();
  final b = List<int>.generate(16, (_) => r.nextInt(256));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  String h(int from, int to) =>
      b.sublist(from, to).map((x) => x.toRadixString(16).padLeft(2, '0')).join();
  return '${h(0, 4)}-${h(4, 6)}-${h(6, 8)}-${h(8, 10)}-${h(10, 16)}';
}
