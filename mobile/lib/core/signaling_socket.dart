import 'dart:async';
import 'dart:convert';

import 'package:web_socket_channel/web_socket_channel.dart';

import 'signaling_message.dart';

/// The `/signal` WebSocket: JSON text frames <-> [SignalingMessage].
class SignalingSocket {
  SignalingSocket._(this._channel) {
    _sub = _channel.stream.listen(
      _onData,
      onError: (_) => _finish(),
      onDone: _finish,
    );
  }

  final WebSocketChannel _channel;
  late final StreamSubscription<dynamic> _sub;
  final _messages = StreamController<SignalingMessage>.broadcast();
  final _closed = Completer<void>();

  static Future<SignalingSocket> connect(Uri url) async {
    final channel = WebSocketChannel.connect(url);
    await channel.ready.timeout(const Duration(seconds: 10));
    return SignalingSocket._(channel);
  }

  Stream<SignalingMessage> get messages => _messages.stream;

  /// Completes when the socket is gone (server closed it, network lost or [close] was called).
  Future<void> get closed => _closed.future;

  void send(SignalingMessage message) {
    if (_closed.isCompleted) return;
    _channel.sink.add(jsonEncode(message.toJson()));
  }

  void _onData(dynamic data) {
    if (data is! String) return;
    try {
      final decoded = jsonDecode(data);
      if (decoded is! Map<String, dynamic>) return;
      final m = SignalingMessage.fromJson(decoded);
      if (m != null) _messages.add(m);
    } on FormatException {
      // Ignore a malformed frame instead of dropping the whole session.
    }
  }

  void _finish() {
    if (!_closed.isCompleted) _closed.complete();
    if (!_messages.isClosed) _messages.close();
  }

  Future<void> close() async {
    await _sub.cancel();
    await _channel.sink.close();
    _finish();
  }
}
