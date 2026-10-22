/// When the sender should try to register a new passkey.
///
/// Normally at the end of the current 45 s slot. After a failed registration the old slot is already over,
/// so a delay computed from it would be zero and the loop would hammer /register; failures therefore use
/// a bounded exponential backoff (1, 2, 4 ... 60 s) and honour the server's Retry-After when it is longer.
Duration rotationDelay({
  required DateTime? validTo,
  required DateTime now,
  required int failures,
  Duration? retryAfter,
}) {
  if (failures <= 0) {
    final wait = validTo?.difference(now) ?? Duration.zero;
    return wait.isNegative ? Duration.zero : wait;
  }
  final seconds = 1 << (failures - 1).clamp(0, 6); // 1, 2, 4, ... 64
  final backoff = Duration(seconds: seconds > 60 ? 60 : seconds);
  return retryAfter != null && retryAfter > backoff ? retryAfter : backoff;
}
