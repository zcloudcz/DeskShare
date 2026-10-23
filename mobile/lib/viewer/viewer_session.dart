import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../core/credential.dart';
import '../core/e2e_binding.dart';
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

  /// An interactive control request waits for the person at the computer.
  bool get controlPending => false;
  RTCVideoRenderer? get renderer;

  Future<void> connect({required String serverId, required String passkey});
  Future<void> disconnect();
  void setControl(bool enabled);
  void sendInput(InputMessage message);
}

/// Viewer role of the signaling protocol (mirror of src/WebClient/client.js).
class WebRtcViewerSession extends ViewerSession {
  WebRtcViewerSession({required this.signalingUrl, @visibleForTesting List<int>? e2eKeyOverride})
      : _e2eKeyOverride = e2eKeyOverride;

  final String signalingUrl;

  /// Test seam: the key the viewer binds with instead of the one derived from the passkey (wrong-key tests).
  final List<int>? _e2eKeyOverride;

  static const _pingInterval = Duration(seconds: 5);
  static const _pingTimeout = Duration(seconds: 15);

  ViewerState _state = ViewerState.idle;
  String _status = '';
  bool _disposed = false;

  /// Bumped by every connect/teardown. An async step that resumes after its generation changed (or after
  /// dispose) must not touch the session: it would store sockets and peers nobody will ever close.
  int _gen = 0;
  bool _stale(int gen) => _disposed || gen != _gen;
  bool _controlAvailable = false; // the host allows control right now
  bool _controlRequestable = false; // the host would ask its user (approval)
  bool _controlPending = false; // our interactive request waits for the answer
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

  /// Key of the e2e_bind handshake (derived from the typed passkey, never sent anywhere).
  List<int>? _e2eKey;

  /// Verification state of the CURRENT peer connection only. A new offer gets a new object and retires the old
  /// one, so a late result of an old connection can never mark the new one verified.
  PeerBinding? _binding;

  /// Video of the current connection, held back until its binding verified.
  MediaStream? _pendingStream;
  Timer? _bindTimer;

  @override
  ViewerState get state => _state;
  @override
  String get status => _status;
  @override
  bool get controlAvailable => _controlAvailable || _controlRequestable;
  @override
  bool get controlPending => _controlPending;
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
    if (!credential.isComplete) {
      return _set(ViewerState.failed,
          'Enter the passkey (9 characters, XXX-XXX-XXX) or the permanent password (at least $permanentPasswordMinLength characters)');
    }

    _serverId = id;
    final gen = ++_gen;
    _set(ViewerState.connecting, credential.isPermanentPassword ? 'Deriving the key from the password...' : 'Authenticating...');
    try {
      // The permanent password path is PBKDF2 with 600 000 rounds: it runs in a background isolate.
      final keys = await credential.deriveFor(id);
      if (_stale(gen)) return;
      _e2eKey = _e2eKeyOverride ?? keys.e2eKey;
      _set(ViewerState.connecting, 'Authenticating...');
      _api = SignalingApi(signalingUrl);
      final grant = await _api!.authenticate(
        serverId: id,
        authKey: keys.authKey,
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
    } else if (_state == ViewerState.connecting &&
        _pc?.connectionState != RTCPeerConnectionState.RTCPeerConnectionStateConnected) {
      // (A peer that is already connected is waiting for its binding check; its own 10 s deadline decides.)
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
    final binding = _binding = PeerBinding();

    pc.onTrack = (e) {
      if (!identical(binding, _binding)) return;
      if (e.track.kind == 'video' && e.streams.isNotEmpty) {
        _pendingStream = e.streams.first;
        _showVideo(binding);
      }
    };
    pc.onDataChannel = (channel) => _setupDataChannel(pc, binding, channel);
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
          if (!binding.isVerified) {
            _set(ViewerState.connecting, 'Verifying secure connection...');
            _startBindTimer(binding);
          }
        case RTCPeerConnectionState.RTCPeerConnectionStateFailed:
        case RTCPeerConnectionState.RTCPeerConnectionStateClosed:
        case RTCPeerConnectionState.RTCPeerConnectionStateDisconnected:
          // The host drops a viewer whose binding does not verify. Losing the connection after we sent ours but
          // before the host's arrived is a failed check, not a network glitch.
          if (binding.bindSent && !binding.isVerified && _state != ViewerState.idle) {
            _failBinding();
          } else if (s != RTCPeerConnectionState.RTCPeerConnectionStateDisconnected && _state != ViewerState.idle) {
            _set(ViewerState.failed, 'Connection to the host was lost');
          }
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

  void _setupDataChannel(RTCPeerConnection pc, PeerBinding binding, RTCDataChannel channel) {
    if (!identical(binding, _binding) || binding.isRetired) {
      channel.close(); // a channel of a replaced connection
      return;
    }
    _dc = channel;
    bool current() => identical(binding, _binding) && identical(channel, _dc);
    channel.onDataChannelState = (s) {
      if (!current()) return;
      if (s == RTCDataChannelState.RTCDataChannelOpen) {
        // First message on the channel is our binding; authorization_request follows only after the host's verified.
        unawaited(_sendBind(pc, binding, channel).then((ok) {
          if (!ok && current()) _failBinding();
        }, onError: (Object _) {
          if (current()) _failBinding();
        }));
      } else if (s == RTCDataChannelState.RTCDataChannelClosed) {
        _controlAvailable = false;
        _controlRequestable = false;
        _controlPending = false;
        _controlEnabled = false;
        _notify();
      }
    };
    channel.onMessage = (msg) {
      if (msg.isBinary || !current()) return;
      final Object? j;
      try {
        j = jsonDecode(msg.text);
      } on FormatException {
        return; // not JSON: ignore
      }
      if (j is! Map<String, dynamic>) return;
      if (j['type'] == 'e2e_bind') {
        if (binding.isVerified) return;
        unawaited(_verifySenderBind(pc, j).then((ok) {
          // Success AND failure only count for the connection the check started on.
          if (!current()) return;
          if (ok) {
            _onVerified(binding);
          } else {
            _failBinding();
          }
        }, onError: (Object _) {
          if (current()) _failBinding();
        }));
        return;
      }
      // Until the host's binding verified, whatever else arrives is ignored (it could come from a man in the middle).
      if (!binding.isVerified) return;
      switch (j['type']) {
        case 'authorization_response':
          _onAuthorizationResponse(j);
        case 'approval_pending':
          // The person at the computer is being asked: about the connection (unattended host) or about control.
          if (j['what'] == 'connection') {
            _set(_state, 'Waiting for the person at the computer to allow the connection...');
          } else if (_controlPending) {
            _set(_state, 'Waiting for the person at the computer to allow control...');
          }
        case 'approval_denied':
          if (j['what'] == 'connection') {
            unawaited(_teardown().then((_) => _set(ViewerState.failed, 'The person at the computer did not allow the connection')));
          }
        case 'authorization_revoked':
          _controlAvailable = false;
          _controlRequestable = false;
          _controlPending = false;
          _controlEnabled = false;
          _set(_state, 'The host revoked remote control');
      }
    };
  }

  /// Fingerprints as this side sees them: the sender's is in the remote description (its offer), ours in the local one.
  Future<({String? sender, String? viewer})> _fingerprints(RTCPeerConnection pc) async => (
        sender: getFingerprint((await pc.getRemoteDescription())?.sdp),
        viewer: getFingerprint((await pc.getLocalDescription())?.sdp),
      );

  Future<bool> _sendBind(RTCPeerConnection pc, PeerBinding binding, RTCDataChannel channel) async {
    final key = _e2eKey;
    final fp = await _fingerprints(pc);
    if (binding.isRetired || !identical(channel, _dc)) return true; // replaced meanwhile: not a failure
    if (key == null || fp.sender == null || fp.viewer == null) return false;
    final mac = computeMac(key, viewerRole, fp.sender!, fp.viewer!);
    // Set before the send completes: the host may drop us (and the state callback fire) before send() returns.
    binding.bindSent = true;
    await channel.send(RTCDataChannelMessage(buildBindMessage(viewerRole, mac)));
    return true;
  }

  Future<bool> _verifySenderBind(RTCPeerConnection pc, Map<String, dynamic> json) async {
    final bind = tryReadBind(json);
    final key = _e2eKey;
    final fp = await _fingerprints(pc);
    if (bind == null || key == null || fp.sender == null || fp.viewer == null || bind.role != senderRole) return false;
    return verifyMac(key, senderRole, fp.sender!, fp.viewer!, bind.mac);
  }

  void _onVerified(PeerBinding binding) {
    if (!binding.tryVerify()) return;
    _bindTimer?.cancel();
    _showVideo(binding);
    _set(ViewerState.connected, 'Receiving video stream');
    // The reply tells us whether the host allows remote control.
    // "interactive" must always be present: a host that asks its user answers requests without it with
    // client_update_required. The probe never prompts anybody (interactive: false).
    _sendDc({'type': 'authorization_request', 'interactive': false, 'timestamp': DateTime.now().toUtc().toIso8601String()});
  }

  void _showVideo(PeerBinding binding) {
    final stream = _pendingStream;
    if (stream == null || !binding.isVerified || !identical(binding, _binding)) return;
    _renderer?.srcObject = stream;
    _notify();
  }

  void _startBindTimer(PeerBinding binding) {
    _bindTimer?.cancel();
    if (binding.isVerified) return;
    _bindTimer = Timer(bindTimeout, () {
      // Only the connection that was waiting counts, not a newer one created by a reconnect.
      if (!binding.isVerified && identical(binding, _binding)) _failBinding();
    });
  }

  Future<void> _failBinding() async {
    debugPrint('End-to-end check failed'); // no keys or fingerprints in the log
    await _teardown();
    _set(ViewerState.failed, bindFailedMessage);
  }

  /// Every outgoing data channel message passes here: nothing but the binding goes out before the host verified.
  void _sendDc(Map<String, dynamic> json) {
    final dc = _dc;
    if (dc == null || dc.state != RTCDataChannelState.RTCDataChannelOpen) return;
    if (!(_binding?.isVerified ?? false)) return;
    dc.send(RTCDataChannelMessage(jsonEncode(json)));
  }

  /// `authorization_response {authorized, requestable?, reason?, ...}`; unknown fields are ignored. Before the user
  /// asked for control it only says what the host allows; afterwards it is the answer to the interactive request.
  void _onAuthorizationResponse(Map<String, dynamic> j) {
    final authorized = j['authorized'] == true;
    if (_controlPending) {
      _controlPending = false;
      _controlAvailable = authorized;
      _controlEnabled = authorized;
      _set(_state, authorized ? 'Remote control active' : _denialText(j['reason']));
      return;
    }
    _controlAvailable = authorized;
    _controlRequestable = !authorized && j['requestable'] == true;
    if (!authorized) _controlEnabled = false;
    _set(
        _state,
        authorized
            ? 'Remote control is available'
            : _controlRequestable
                ? 'Control can be requested from the person at the computer'
                : 'View only: the host did not allow control');
  }

  static String _denialText(Object? reason) => switch (reason) {
        'denied' => 'The person at the computer denied remote control',
        'timeout' => 'Nobody at the computer answered the control request',
        'client_update_required' => 'The host requires a newer app version to allow remote control',
        _ => 'The host did not allow remote control',
      };

  @override
  void setControl(bool enabled) {
    if (!enabled) {
      _controlEnabled = false;
      _notify();
    } else if (_controlAvailable) {
      _controlEnabled = true;
      _notify();
    } else if (_controlRequestable && !_controlPending) {
      // Ask the host; the probe on channel open is interactive:false.
      _controlPending = true;
      _set(_state, 'Requesting remote control...');
      _sendDc({'type': 'authorization_request', 'interactive': true, 'timestamp': DateTime.now().toUtc().toIso8601String()});
    }
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
    _binding?.retire();
    _binding = null;
    _pendingStream = null;
    _bindTimer?.cancel();
    _controlAvailable = false;
    _controlRequestable = false;
    _controlPending = false;
    _controlEnabled = false;
    await dc?.close();
    await pc?.close();
    if (_renderer != null && !_disposed) _renderer!.srcObject = null; // dispose() disposes the renderer first
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
