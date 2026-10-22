import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../core/credential.dart';
import '../core/ids.dart';
import '../core/input_message.dart';
import '../core/signaling_api.dart';
import '../core/signaling_message.dart';
import '../core/signaling_socket.dart';

enum ViewerState { idle, connecting, connected, failed }

/// What the viewer screen needs. The screen depends on this interface so widget tests can use a fake.
abstract class ViewerSession extends ChangeNotifier {
  ViewerState get state;

  /// Human readable progress / error line.
  String get status;

  /// Host said it allows remote control (authorization_response.authorized).
  bool get controlAvailable;
  bool get controlEnabled;
  RTCVideoRenderer? get renderer;

  Future<void> connect({required String serverId, required String passkey});
  Future<void> disconnect();
  void setControl(bool enabled);
  void sendInput(InputMessage message);
}

/// Viewer role of the signaling protocol (mirror of src/WebClient/client.js).
class WebRtcViewerSession extends ViewerSession {
  WebRtcViewerSession({required this.signalingUrl});

  final String signalingUrl;

  static const _pingInterval = Duration(seconds: 5);
  static const _pingTimeout = Duration(seconds: 15);

  ViewerState _state = ViewerState.idle;
  String _status = '';
  bool _disposed = false;

  /// Bumped by every connect/teardown. An async step that resumes after its generation changed (or after
  /// dispose) must not touch the session: it would store sockets and peers nobody will ever close.
  int _gen = 0;
  bool _stale(int gen) => _disposed || gen != _gen;
  bool _controlAvailable = false;
  bool _controlEnabled = false;
  RTCVideoRenderer? _renderer;

  SignalingApi? _api;
  SignalingSocket? _socket;
  StreamSubscription<SignalingMessage>? _msgSub;
  RTCPeerConnection? _pc;
  RTCDataChannel? _dc;
  Timer? _pingTimer;
  DateTime _lastPong = DateTime.now();
  String _serverId = '';
  String? _senderId;
  final List<SignalingMessage> _pendingIce = [];
  List<IceServer> _iceServers = const [];

  @override
  ViewerState get state => _state;
  @override
  String get status => _status;
  @override
  bool get controlAvailable => _controlAvailable;
  @override
  bool get controlEnabled => _controlEnabled;
  @override
  RTCVideoRenderer? get renderer => _renderer;

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  void _set(ViewerState s, String status) {
    if (_disposed) return;
    _state = s;
    _status = status;
    _notify();
  }

  @override
  Future<void> connect({required String serverId, required String passkey}) async {
    if (_state == ViewerState.connecting || _state == ViewerState.connected) return;
    final id = normalizeServerId(serverId);
    final credential = PasskeyCredential(passkey);
    if (id.isEmpty) return _set(ViewerState.failed, 'Server ID is required');
    if (!credential.isComplete) return _set(ViewerState.failed, 'The passkey has 9 characters (XXX-XXX-XXX)');

    _serverId = id;
    final gen = ++_gen;
    _set(ViewerState.connecting, 'Authenticating...');
    try {
      _api = SignalingApi(signalingUrl);
      final grant = await _api!.authenticate(
        serverId: id,
        credential: credential,
        clientId: generateClientId(),
      );
      if (_stale(gen)) return;
      _iceServers = grant.iceServers;

      _set(ViewerState.connecting, 'Connecting to signaling server...');
      final socket = await SignalingSocket.connect(_api!.signalUrl(grant.webSocketToken));
      if (_stale(gen)) {
        await socket.close(); // disposed or cancelled while connecting: nobody owns this socket
        return;
      }
      _socket = socket;
      _msgSub = socket.messages.listen(_onSignal);
      unawaited(socket.closed.then((_) => _onSocketClosed(socket)));
    } on SignalingException catch (e) {
      if (_stale(gen)) return;
      await _teardown();
      _set(ViewerState.failed, e.message);
    } catch (e) {
      if (_stale(gen)) return;
      await _teardown();
      _set(ViewerState.failed, 'Connection failed: $e');
    }
  }

  void _onSocketClosed(SignalingSocket closed) {
    if (!identical(closed, _socket)) return; // an old socket after disconnect()
    _pingTimer?.cancel();
    _socket = null;
    if (_state == ViewerState.connected) {
      // Media flows peer to peer; a signaling outage must not end a running session.
      _set(ViewerState.connected, 'Signaling offline - video continues');
    } else if (_state == ViewerState.connecting) {
      _teardown();
      _set(ViewerState.failed, 'Disconnected from signaling server');
    }
  }

  void _send(SignalingMessage m) => _socket?.send(m);

  Future<void> _onSignal(SignalingMessage m) async {
    try {
      switch (m.type) {
        case SignalingType.identify:
          _set(ViewerState.connecting, 'Requesting connection to ${formatServerId(_serverId)}...');
          _send(SignalingMessage(type: SignalingType.connectionRequest, targetId: _serverId));
          _startPing();
        case SignalingType.offer:
          _senderId = m.senderId;
          await _handleOffer(m);
        case SignalingType.iceCandidate:
          await _handleIce(m);
        case SignalingType.ping:
          _send(SignalingMessage(type: SignalingType.pong, targetId: m.senderId));
        case SignalingType.pong:
          _lastPong = DateTime.now();
        case SignalingType.error:
          final msg = m.errorMessage ?? 'Unknown error';
          // "Client X disconnected" notifications also arrive as errors; only the sender leaving matters.
          if (msg.contains('disconnected')) {
            if (m.senderId == _senderId && _pc?.connectionState != RTCPeerConnectionState.RTCPeerConnectionStateConnected) {
              await _teardown();
              _set(ViewerState.failed, 'The host went offline');
            }
          } else {
            _set(_state, 'Error: $msg');
          }
        case SignalingType.answer:
        case SignalingType.connectionRequest:
          break; // not for a viewer
      }
    } catch (e) {
      _set(ViewerState.failed, 'Signaling error: $e');
    }
  }

  void _startPing() {
    _lastPong = DateTime.now();
    _pingTimer?.cancel();
    _pingTimer = Timer.periodic(_pingInterval, (_) async {
      if (DateTime.now().difference(_lastPong) > _pingTimeout) {
        _pingTimer?.cancel();
        if (_pc?.connectionState == RTCPeerConnectionState.RTCPeerConnectionStateConnected) {
          await _socket?.close(); // media still up: _onSocketClosed keeps the session
        } else {
          await _teardown();
          _set(ViewerState.failed, 'Server not responding');
        }
        return;
      }
      _send(SignalingMessage(type: SignalingType.ping, targetId: _serverId));
    });
  }

  Future<void> _handleOffer(SignalingMessage offer) async {
    final gen = _gen;
    _set(ViewerState.connecting, 'Received offer, creating peer connection...');
    await _closePeer();

    final renderer = _renderer ??= RTCVideoRenderer();
    await renderer.initialize();
    if (_stale(gen)) {
      if (_disposed) await renderer.dispose();
      return;
    }

    final pc = await createPeerConnection({
      'iceServers': _iceServers.isEmpty
          ? [
              {'urls': 'stun:stun.l.google.com:19302'}
            ]
          : _iceServers.map((s) => s.toRtc()).toList(),
      'sdpSemantics': 'unified-plan',
    });
    if (_stale(gen)) {
      await pc.close();
      return;
    }
    _pc = pc;

    pc.onTrack = (e) {
      if (e.track.kind == 'video' && e.streams.isNotEmpty) {
        _renderer!.srcObject = e.streams.first;
        _notify();
      }
    };
    pc.onDataChannel = _setupDataChannel;
    pc.onIceCandidate = (c) {
      if (c.candidate == null) return;
      _send(SignalingMessage(
        type: SignalingType.iceCandidate,
        targetId: _senderId,
        candidate: jsonEncode({'candidate': c.candidate, 'sdpMid': c.sdpMid, 'sdpMLineIndex': c.sdpMLineIndex}),
        sdpMLineIndex: c.sdpMLineIndex,
        sdpMid: c.sdpMid,
      ));
    };
    pc.onConnectionState = (s) {
      if (!identical(pc, _pc)) return;
      switch (s) {
        case RTCPeerConnectionState.RTCPeerConnectionStateConnected:
          _set(ViewerState.connected, 'Receiving video stream');
        case RTCPeerConnectionState.RTCPeerConnectionStateFailed:
        case RTCPeerConnectionState.RTCPeerConnectionStateClosed:
          if (_state != ViewerState.idle) _set(ViewerState.failed, 'Connection to the host was lost');
        default:
          break;
      }
    };

    await pc.setRemoteDescription(RTCSessionDescription(offer.sdp, 'offer'));
    final answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);
    if (_stale(gen)) return; // _teardown has closed the peer
    _send(SignalingMessage(type: SignalingType.answer, targetId: _senderId, sdp: answer.sdp));

    // ICE candidates that raced ahead of the offer.
    final queued = List.of(_pendingIce);
    _pendingIce.clear();
    for (final c in queued) {
      await _handleIce(c);
    }
  }

  Future<void> _handleIce(SignalingMessage m) async {
    final pc = _pc;
    if (pc == null) {
      _pendingIce.add(m);
      return;
    }
    final raw = m.candidate;
    if (raw == null || raw.isEmpty) return;
    String? text;
    String? mid = m.sdpMid;
    int? index = m.sdpMLineIndex;
    try {
      final j = jsonDecode(raw) as Map<String, dynamic>;
      text = j['candidate'] as String?;
      mid = (j['sdpMid'] as String?) ?? mid;
      index = (j['sdpMLineIndex'] as int?) ?? index;
    } on FormatException {
      text = raw; // plain candidate line
    }
    if (text == null || text.isEmpty) return;
    if (!text.startsWith('candidate:')) text = 'candidate:$text';
    // The .NET sender sends sdpMid:null. The browser accepts that, but the Android WebRTC library
    // aborts the whole process on a null mid, so resolve it from the remote description.
    mid ??= midOfMLine((await pc.getRemoteDescription())?.sdp, index ?? 0);
    await pc.addCandidate(RTCIceCandidate(text, mid, index));
  }

  void _setupDataChannel(RTCDataChannel channel) {
    _dc = channel;
    channel.onDataChannelState = (s) {
      if (s == RTCDataChannelState.RTCDataChannelOpen) {
        // The reply tells us whether the host allows remote control.
        _sendDc({'type': 'authorization_request', 'timestamp': DateTime.now().toUtc().toIso8601String()});
      } else if (s == RTCDataChannelState.RTCDataChannelClosed) {
        _controlAvailable = false;
        _controlEnabled = false;
        _notify();
      }
    };
    channel.onMessage = (msg) {
      if (msg.isBinary) return;
      try {
        final j = jsonDecode(msg.text);
        if (j is! Map<String, dynamic>) return;
        switch (j['type']) {
          case 'authorization_response':
            _controlAvailable = j['authorized'] == true;
            if (!_controlAvailable) _controlEnabled = false;
            _set(_state, _controlAvailable ? 'Remote control is available' : 'View only: the host did not allow control');
          case 'authorization_revoked':
            _controlAvailable = false;
            _controlEnabled = false;
            _set(_state, 'The host revoked remote control');
        }
      } on FormatException {
        // not JSON: ignore
      }
    };
  }

  void _sendDc(Map<String, dynamic> json) {
    final dc = _dc;
    if (dc == null || dc.state != RTCDataChannelState.RTCDataChannelOpen) return;
    dc.send(RTCDataChannelMessage(jsonEncode(json)));
  }

  @override
  void setControl(bool enabled) {
    if (enabled && !_controlAvailable) return;
    _controlEnabled = enabled;
    _notify();
  }

  @override
  void sendInput(InputMessage message) {
    if (_controlEnabled) _sendDc(message.toJson());
  }

  Future<void> _closePeer() async {
    final dc = _dc;
    final pc = _pc;
    _dc = null;
    _pc = null;
    _controlAvailable = false;
    _controlEnabled = false;
    await dc?.close();
    await pc?.close();
    if (_renderer != null) _renderer!.srcObject = null;
  }

  Future<void> _teardown() async {
    _gen++;
    _pingTimer?.cancel();
    await _msgSub?.cancel();
    final socket = _socket;
    _socket = null;
    await socket?.close();
    await _closePeer();
    _pendingIce.clear();
    _api?.close();
    _api = null;
  }

  @override
  Future<void> disconnect() async {
    await _teardown();
    _set(ViewerState.idle, '');
  }

  @override
  void dispose() {
    _disposed = true;
    _teardown();
    _renderer?.dispose();
    super.dispose();
  }
}
