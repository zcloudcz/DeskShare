import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../core/credential.dart';
import '../core/ids.dart';
import '../core/input_message.dart';
import '../core/settings.dart';
import '../core/signaling_api.dart';
import '../core/signaling_message.dart';
import '../core/signaling_socket.dart';
import 'device_control.dart';
import 'gesture_mapper.dart';

enum SenderState { idle, starting, sharing, failed }

/// What the sender screen needs (interface so widget tests can use a fake).
abstract class SenderSession extends ChangeNotifier {
  SenderState get state;
  String get status;

  /// "server-123456789"
  String get serverId;

  /// Canonical passkey of the current 45 s slot, empty while not sharing.
  String get passkey;
  DateTime? get passkeyValidTo;
  bool get viewerConnected;

  /// "Allow remote control" toggle. It only takes effect when the accessibility service is enabled
  /// in system settings (checked when sharing starts) and is fixed for the running session.

  bool get controlRequested;
  set controlRequested(bool value);

  Future<void> start();
  Future<void> stop();
}

/// Sender role: capture the screen, register with the signaling server, answer viewers
/// (mirror of src/Core/ScreenSenderService.cs + WebRTCSession).
class WebRtcSenderSession extends SenderSession {
  WebRtcSenderSession({required this.settings, required this.device});

  final AppSettings settings;
  final DeviceControl device;

  static const _maxServerIdAttempts = 5;

  SenderState _state = SenderState.idle;
  String _status = '';
  String _serverId = '';
  String _passkey = '';
  DateTime? _validTo;
  bool _control = false;
  bool _viewer = false;

  SignalingApi? _api;
  SignalingSocket? _socket;
  StreamSubscription<SignalingMessage>? _msgSub;
  MediaStream? _stream;
  RTCPeerConnection? _pc;
  RTCDataChannel? _dc;
  Timer? _rotationTimer;
  bool _reconnecting = false;
  bool _stopping = false;
  String? _viewerId;
  GestureMapper? _mapper;
  bool _controlGrantedForSession = false;
  List<IceServer> _iceServers = const [];

  @override
  SenderState get state => _state;
  @override
  String get status => _status;
  @override
  String get serverId => _serverId.isEmpty ? settings.serverId : _serverId;
  @override
  String get passkey => _passkey;
  @override
  DateTime? get passkeyValidTo => _validTo;
  @override
  bool get viewerConnected => _viewer;
  @override
  bool get controlRequested => _control;
  @override
  set controlRequested(bool value) {
    if (_state == SenderState.sharing || _state == SenderState.starting) return; // fixed for the session
    _control = value;
    notifyListeners();
  }

  void _set(SenderState s, String status) {
    _state = s;
    _status = status;
    notifyListeners();
  }

  @override
  Future<void> start() async {
    if (_state == SenderState.starting || _state == SenderState.sharing) return;
    _stopping = false;
    _set(SenderState.starting, 'Asking for screen capture permission...');
    try {
      _controlGrantedForSession = _control && await device.isAccessibilityEnabled();

      // Android 14+: the user must consent BEFORE the mediaProjection foreground service starts,
      // and the service must run BEFORE the capture is created. flutter_webrtc keeps the consent
      // and reuses it in the following getDisplayMedia call.
      if (!await Helper.requestCapturePermission()) {
        return _set(SenderState.failed, 'Screen capture permission was denied');
      }
      await device.startCaptureService();
      _stream = await navigator.mediaDevices.getDisplayMedia({'video': true, 'audio': false});

      final size = await device.displaySize();
      _mapper = GestureMapper(width: size.width, height: size.height);

      _api = SignalingApi(settings.signalingUrl);
      _serverId = settings.serverId;
      await _newPasskeyAndRegister(firstRegistration: true);
      await _connectSocket();
      _scheduleRotation();
      _set(SenderState.sharing, 'Waiting for a viewer...');
    } on SignalingException catch (e) {
      await _shutdown();
      _set(SenderState.failed, e.message);
    } catch (e) {
      await _shutdown();
      _set(SenderState.failed, 'Could not start sharing: $e');
    }
  }

  String? _wsToken;

  /// Generates a passkey and registers it. Only the first registration may change the Server ID
  /// (HTTP 403 = another installation owns it); later ones keep it because viewers already know it.
  Future<void> _newPasskeyAndRegister({bool firstRegistration = false}) async {
    final now = DateTime.now().toUtc();
    final passkey = generatePasskey();
    final validTo = passkeyExpiration(now);

    for (var attempt = 1;; attempt++) {
      try {
        final grant = await _api!.register(
          serverId: _serverId,
          credential: PasskeyCredential(passkey),
          validTo: validTo,
          remoteControlEnabled: _controlGrantedForSession,
          ownerSecret: settings.ownerSecret,
        );
        _wsToken = grant.webSocketToken;
        if (grant.iceServers.isNotEmpty) _iceServers = grant.iceServers;
        break;
      } on SignalingException catch (e) {
        if (e.statusCode == 403 && firstRegistration && attempt < _maxServerIdAttempts) {
          _serverId = settings.regenerateServerId();
          continue;
        }
        rethrow;
      }
    }
    _passkey = passkey;
    _validTo = validTo;
    notifyListeners();
  }

  void _scheduleRotation() {
    _rotationTimer?.cancel();
    final wait = _validTo!.difference(DateTime.now().toUtc());
    _rotationTimer = Timer(wait.isNegative ? Duration.zero : wait, () async {
      if (_stopping) return;
      try {
        await _newPasskeyAndRegister();
      } catch (_) {
        // Server unreachable: keep the old passkey on screen, try again at the next slot.
      }
      if (!_stopping) _scheduleRotation();
    });
  }

  Future<void> _connectSocket() async {
    final socket = await SignalingSocket.connect(_api!.signalUrl(_wsToken!));
    _socket = socket;
    _msgSub = socket.messages.listen(_onSignal);
    unawaited(socket.closed.then((_) => _onSocketClosed(socket)));
  }

  Future<void> _onSocketClosed(SignalingSocket closed) async {
    if (_stopping || !identical(closed, _socket) || _reconnecting) return;
    _reconnecting = true;
    _socket = null;
    await _msgSub?.cancel();
    _set(_state, 'Signaling connection lost, reconnecting...');
    try {
      // The one-time token is spent: register again for a fresh one, with growing delays.
      for (var attempt = 0; !_stopping; attempt++) {
        await Future<void>.delayed(Duration(seconds: [1, 2, 4, 8, 16, 30][attempt.clamp(0, 5)]));
        if (_stopping) return;
        try {
          await _newPasskeyAndRegister();
          await _connectSocket();
          _set(SenderState.sharing, _viewer ? 'Viewer connected' : 'Waiting for a viewer...');
          return;
        } catch (_) {
          // try again
        }
      }
    } finally {
      _reconnecting = false;
    }
  }

  void _send(SignalingMessage m) => _socket?.send(m);

  Future<void> _onSignal(SignalingMessage m) async {
    try {
      switch (m.type) {
        case SignalingType.connectionRequest:
          if (m.senderId != null) await _offerTo(m.senderId!);
        case SignalingType.answer:
          if (m.sdp != null && m.senderId == _viewerId) {
            await _pc?.setRemoteDescription(RTCSessionDescription(m.sdp, 'answer'));
          }
        case SignalingType.iceCandidate:
          await _addIce(m);
        case SignalingType.ping:
          _send(SignalingMessage(type: SignalingType.pong, targetId: m.senderId));
        case SignalingType.error:
          if (m.senderId == _viewerId && (m.errorMessage ?? '').contains('disconnected')) {
            await _closePeer();
            _set(_state, 'Waiting for a viewer...');
          }
        case SignalingType.offer:
        case SignalingType.identify:
        case SignalingType.pong:
          break;
      }
    } catch (e) {
      _set(_state, 'Signaling error: $e');
    }
  }

  Future<void> _offerTo(String viewerId) async {
    await _closePeer(); // one viewer at a time, like the desktop sender
    _viewerId = viewerId;

    final pc = await createPeerConnection({
      'iceServers': _iceServers.isEmpty
          ? [
              {'urls': 'stun:stun.l.google.com:19302'}
            ]
          : _iceServers.map((s) => s.toRtc()).toList(),
      'sdpSemantics': 'unified-plan',
    });
    _pc = pc;

    pc.onIceCandidate = (c) {
      if (c.candidate == null) return;
      _send(SignalingMessage(
        type: SignalingType.iceCandidate,
        targetId: viewerId,
        candidate: jsonEncode({'candidate': c.candidate, 'sdpMid': c.sdpMid, 'sdpMLineIndex': c.sdpMLineIndex}),
        sdpMLineIndex: c.sdpMLineIndex,
        sdpMid: c.sdpMid,
      ));
    };
    pc.onConnectionState = (s) {
      if (!identical(pc, _pc)) return;
      if (s == RTCPeerConnectionState.RTCPeerConnectionStateConnected) {
        _viewer = true;
        _set(SenderState.sharing, 'Viewer connected');
      } else if (s == RTCPeerConnectionState.RTCPeerConnectionStateFailed ||
          s == RTCPeerConnectionState.RTCPeerConnectionStateDisconnected ||
          s == RTCPeerConnectionState.RTCPeerConnectionStateClosed) {
        _viewer = false;
        if (_state == SenderState.sharing) _set(SenderState.sharing, 'Waiting for a viewer...');
      }
    };

    for (final track in _stream!.getTracks()) {
      await pc.addTrack(track, _stream!);
    }

    // Like the desktop sender: the "input" channel exists only when the host allowed remote control.
    if (_controlGrantedForSession) {
      final dc = await pc.createDataChannel('input', RTCDataChannelInit()..ordered = true);
      _dc = dc;
      dc.onMessage = _onDataChannelMessage;
    }

    final offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    _send(SignalingMessage(type: SignalingType.offer, targetId: viewerId, sdp: offer.sdp));
  }

  Future<void> _addIce(SignalingMessage m) async {
    final pc = _pc;
    final raw = m.candidate;
    if (pc == null || raw == null || raw.isEmpty || m.senderId != _viewerId) return;
    String? text;
    var mid = m.sdpMid;
    var index = m.sdpMLineIndex;
    try {
      final j = jsonDecode(raw) as Map<String, dynamic>;
      text = j['candidate'] as String?;
      mid = (j['sdpMid'] as String?) ?? mid;
      index = (j['sdpMLineIndex'] as int?) ?? index;
    } on FormatException {
      text = raw;
    }
    if (text == null || text.isEmpty) return;
    if (!text.startsWith('candidate:')) text = 'candidate:$text';
    await pc.addCandidate(RTCIceCandidate(text, mid, index));
  }

  /// Same policy as src/Core/WebRTC/DataChannelPolicy.cs: oversize and unknown messages are dropped.
  static const maxDataChannelBytes = 64 * 1024;

  void _onDataChannelMessage(RTCDataChannelMessage msg) {
    if (msg.isBinary || msg.text.length > maxDataChannelBytes) return;
    final Object? j;
    try {
      j = jsonDecode(msg.text);
    } on FormatException {
      return;
    }
    if (j is! Map<String, dynamic>) return;

    if (j['type'] == 'authorization_request') {
      // Remote control was decided by the toggle before sharing started; the viewer only learns the result.
      _dc?.send(RTCDataChannelMessage(jsonEncode({
        'type': 'authorization_response',
        'authorized': _controlGrantedForSession,
        'clipboard': false,
        'timestamp': DateTime.now().toUtc().toIso8601String(),
      })));
      return;
    }
    if (!_controlGrantedForSession || j.containsKey('type')) return;

    final input = InputMessage.fromJson(j);
    final mapper = _mapper;
    if (input == null || mapper == null) return;
    for (final action in mapper.handle(input)) {
      unawaited(device.perform(action).catchError((Object _) {}));
    }
  }

  Future<void> _closePeer() async {
    final dc = _dc;
    final pc = _pc;
    _dc = null;
    _pc = null;
    _viewerId = null;
    _viewer = false;
    await dc?.close();
    await pc?.close();
  }

  Future<void> _shutdown() async {
    _stopping = true;
    _rotationTimer?.cancel();
    await _msgSub?.cancel();
    await _socket?.close();
    _socket = null;
    await _closePeer();
    final stream = _stream;
    _stream = null;
    if (stream != null) {
      for (final t in stream.getTracks()) {
        await t.stop();
      }
      await stream.dispose();
    }
    _api?.close();
    _api = null;
    _passkey = '';
    _validTo = null;
    try {
      await device.stopCaptureService();
    } catch (_) {}
  }

  @override
  Future<void> stop() async {
    await _shutdown();
    _set(SenderState.idle, '');
  }

  @override
  void dispose() {
    _shutdown();
    super.dispose();
  }
}
