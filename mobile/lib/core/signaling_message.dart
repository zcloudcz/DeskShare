/// Numeric values of `SignalingMessageType` (System.Text.Json writes enums as numbers).
enum SignalingType {
  offer, // 0
  answer, // 1
  iceCandidate, // 2
  identify, // 3
  error, // 4
  connectionRequest, // 5
  ping, // 6
  pong, // 7
}

/// One WebSocket signaling message. Property names are PascalCase on the wire (default .NET serializer).
class SignalingMessage {
  const SignalingMessage({
    required this.type,
    this.senderId,
    this.targetId,
    this.sdp,
    this.candidate,
    this.sdpMLineIndex,
    this.sdpMid,
    this.errorMessage,
  });

  final SignalingType type;
  final String? senderId;
  final String? targetId;
  final String? sdp;

  /// JSON string `{"candidate":..,"sdpMid":..,"sdpMLineIndex":..}` (what browsers' `candidate.toJSON()` gives).
  final String? candidate;
  final int? sdpMLineIndex;
  final String? sdpMid;
  final String? errorMessage;

  Map<String, dynamic> toJson() => {
        'Type': type.index,
        if (targetId != null) 'TargetId': targetId,
        if (sdp != null) 'Sdp': sdp,
        if (candidate != null) 'Candidate': candidate,
        if (sdpMLineIndex != null) 'SdpMLineIndex': sdpMLineIndex,
        if (sdpMid != null) 'SdpMid': sdpMid,
      };

  /// Returns null for unknown message types so a newer server cannot crash an older app.
  static SignalingMessage? fromJson(Map<String, dynamic> json) {
    final t = json['Type'];
    if (t is! int || t < 0 || t >= SignalingType.values.length) return null;
    return SignalingMessage(
      type: SignalingType.values[t],
      senderId: json['SenderId'] as String?,
      targetId: json['TargetId'] as String?,
      sdp: json['Sdp'] as String?,
      candidate: json['Candidate'] as String?,
      sdpMLineIndex: json['SdpMLineIndex'] as int?,
      sdpMid: json['SdpMid'] as String?,
      errorMessage: json['ErrorMessage'] as String?,
    );
  }
}
