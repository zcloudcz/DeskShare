import 'dart:convert';

import 'package:crypto/crypto.dart';

/// UTC timestamp like JavaScript's `toISOString()`: always 3 fractional digits ("2026-01-02T03:04:05.123Z").
/// This is what the web viewer puts in the request body.
String formatRequestTimestamp(DateTime time) {
  final t = time.toUtc();
  String p(int v, int w) => v.toString().padLeft(w, '0');
  return '${p(t.year, 4)}-${p(t.month, 2)}-${p(t.day, 2)}T${p(t.hour, 2)}:${p(t.minute, 2)}:'
      '${p(t.second, 2)}.${p(t.millisecond, 3)}Z';
}

/// `RequestSigningService.SignRequest`: Base64(HMAC-SHA256(key = key, "serverId|timestamp|nonce")).
///
/// The server parses the body timestamp and re-formats it with .NET's round-trip format "O" (7 fractional
/// digits), so the 3-digit [timestamp] from [formatRequestTimestamp] is padded with "0000" before "Z"
/// (same trick as client.js `signAuthRequest`).
String signRequest({
  required String serverId,
  required String key,
  required String timestamp,
  required String nonce,
}) {
  final message = '$serverId|${timestamp.replaceFirst('Z', '0000Z')}|$nonce';
  final mac = Hmac(sha256, utf8.encode(key)).convert(utf8.encode(message));
  return base64.encode(mac.bytes);
}
