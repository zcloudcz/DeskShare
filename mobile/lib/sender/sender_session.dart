import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../core/credential.dart';
import '../core/e2e_binding.dart';
import '../core/ids.dart';
import '../core/input_message.dart';
import '../core/settings.dart';
import '../core/signaling_api.dart';
import '../core/signaling_message.dart';
import '../core/signaling_socket.dart';
import 'device_control.dart';
import 'gesture_mapper.dart';
import 'rotation.dart';

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
  int _rotationFailures = 0;
  Duration? _retryAfter;
  StreamSubscription<void>? _projectionSub;
  StreamSubscription<void>? _displaySub;
  bool _reconnecting = false;
  bool _stopping = false;
  String? _viewerId;
  GestureMapper? _mapper;
  bool _controlGrantedForSession = false;
  List<IceServer> _iceServers = const [];

  /// e2e keys of the recent passkeys: a viewer that typed the previous passkey must still bind (memory only).
  final E2EKeyRing _e2eKeys = E2EKeyRing();

  /// Verification state of the CURRENT peer connection only (a new viewer gets a new object, the old one is retired).
  PeerBinding? _peer;
  Timer? _bindTimer;

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

  // Bumped by every start and every shutdown: an await that resumes after the session was stopped (e.g. the
  // user ended the capture from the system UI during startup) sees a different value and must not continue.
  int _startGen = 0;

  @override
  Future<void> start() async {
    if (_state == SenderState.starting || _state == SenderState.sharing) return;
    final gen = ++_startGen;
    bool stale() => gen != _startGen;
    _stopping = false;
    _rotationFailures = 0;
    _retryAfter = null;
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
      final stream = await navigator.mediaDevices.getDisplayMedia({'video': true, 'audio': false});
      if (stale()) {
        for (final t in stream.getTracks()) {
          await t.stop();
        }
        await stream.dispose();
        return;
      }
      _stream = stream;

      final size = await device.displaySize();
      if (stale()) return;
      _mapper = GestureMapper(width: size.width, height: size.height);
      _listenToDevice();

      _api = SignalingApi(settings.signalingUrl);
      _serverId = settings.serverId;
      await _newPasskeyAndRegister(firstRegistration: true);
      if (stale()) return;
      await _connectSocket();
      if (stale()) return;
      _scheduleRotation();
      _set(SenderState.sharing, 'Waiting for a viewer...');
    } on SignalingException catch (e) {
      if (stale()) return;
      await _shutdown();
      _set(SenderState.failed, e.message);
    } catch (e) {
      if (stale()) return;
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
        // The server gets the auth key (via wireValue); the e2e key stays here for the viewer check. Derived per
        // attempt because the Server ID is part of the salt and can change on a 403.
        _e2eKeys.add(PasskeyCredential(passkey).e2eKey(_serverId), validTo);
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
    final wait = rotationDelay(
      validTo: _validTo,
      now: DateTime.now().toUtc(),
      failures: _rotationFailures,
      retryAfter: _retryAfter,
    );
    _rotationTimer = Timer(wait, () async {
      if (_stopping) return;
      try {
        await _newPasskeyAndRegister();
        // A previous failure replaced the status with "Cannot reach..."; registration works again.
        if (_rotationFailures > 0 && _state == SenderState.sharing) {
          _set(_state, _viewer ? 'Viewer connected' : 'Waiting for a viewer...');
        }
        _rotationFailures = 0;
        _retryAfter = null;
      } on SignalingException catch (e) {
        _rotationFailures++;
        _retryAfter = e.retryAfter;
        _dropExpiredPasskey();
      } catch (_) {
        _rotationFailures++;
        _retryAfter = null;
        _dropExpiredPasskey();
      }
      if (!_stopping) _scheduleRotation();
    });
  }

  /// A passkey the server no longer accepts must not stay on screen.
  void _dropExpiredPasskey() {
    final validTo = _validTo;
    if (validTo == null || validTo.isAfter(DateTime.now().toUtc())) return;
    _passkey = '';
    _set(_state, 'Cannot reach the signaling server, retrying...');
  }

  Future<void> _connectSocket() async {
    final gen = _startGen;
    final socket = await SignalingSocket.connect(_api!.signalUrl(_wsToken!));
    if (gen != _startGen || _stopping) {
      await socket.close(); // the session was stopped while the handshake was running
      return;
    }
    _socket = socket;
    _msgSub = socket.messages.listen(_enqueueSignal);
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

  /// Signaling handlers are async and `Stream.listen` does not wait for them. Peer replacement must never
  /// interleave (request A suspended in _closePeer while request B builds a new peer), so every message goes
  /// through one chain and is handled only after the previous one finished.
  Future<void> _signalQueue = Future.value();
  void _enqueueSignal(SignalingMessage m) => _signalQueue = _signalQueue.then((_) => _onSignal(m));

  /// Test hook: replaces the WebRTC part of a connection request.
  @visibleForTesting
  Future<void> Function(String viewerId)? offerOverride;

  @visibleForTesting
  void debugSignal(SignalingMessage m) => _enqueueSignal(m);

  /// Bumped by every peer teardown/replacement; an _offerTo that finds it changed after an await is stale.
  int _peerGen = 0;

  /// Peer connections whose close() has not finished yet. Their senders still hold the shared capture track,
  /// so the track stays disabled until this is 0.
  int _pendingCloses = 0;

  Future<void> _onSignal(SignalingMessage m) async {
    if (_stopping) return;
    try {
      switch (m.type) {
        case SignalingType.connectionRequest:
          if (m.senderId != null) await (offerOverride ?? _offerTo)(m.senderId!);
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
    final closing = _closePeer(); // one viewer at a time, like the desktop sender
    final gen = _peerGen; // _closePeer bumped it synchronously
    bool stale() => gen != _peerGen || _stopping;
    await closing; // the old connection is closed completely before the new one gets the track
    if (stale()) return;
    _viewerId = viewerId;
    final peer = _peer = PeerBinding();
    _syncVideo(); // no pixels until this viewer's binding verified

    final pc = await createPeerConnection({
      'iceServers': _iceServers.isEmpty
          ? [
              {'urls': 'stun:stun.l.google.com:19302'}
            ]
          : _iceServers.map((s) => s.toRtc()).toList(),
      'sdpSemantics': 'unified-plan',
    });
    if (stale()) {
      await pc.close();
      return;
    }
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
        if (!peer.isVerified) {
          _set(SenderState.sharing, 'Verifying viewer...');
          _startBindTimer(peer);
        }
      } else if (s == RTCPeerConnectionState.RTCPeerConnectionStateFailed ||
          s == RTCPeerConnectionState.RTCPeerConnectionStateDisconnected ||
          s == RTCPeerConnectionState.RTCPeerConnectionStateClosed) {
        _viewer = false;
        if (_state == SenderState.sharing) _set(SenderState.sharing, 'Waiting for a viewer...');
      }
    };

    final stream = _stream;
    if (stream == null) return;
    for (final track in stream.getTracks()) {
      await pc.addTrack(track, stream);
      if (stale()) return; // whoever replaced us closes _pc; this pc is no longer _pc
    }

    // Always created, even for a view-only host: the viewer's e2e_bind arrives over it (like ScreenSenderService).
    final dc = await pc.createDataChannel('input', RTCDataChannelInit()..ordered = true);
    if (stale() || !identical(peer, _peer)) {
      await dc.close(); // replaced while the channel was being created
      return;
    }
    _dc = dc;
    dc.onMessage = (msg) {
      if (msg.isBinary || !identical(peer, _peer)) return;
      unawaited(_onText(peer, dc, msg.text, () async => (
            sender: getFingerprint((await pc.getLocalDescription())?.sdp),
            viewer: getFingerprint((await pc.getRemoteDescription())?.sdp),
          )));
    };

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
    mid ??= midOfMLine((await pc.getRemoteDescription())?.sdp, index ?? 0); // null mid aborts Android WebRTC
    await pc.addCandidate(RTCIceCandidate(text, mid, index));
  }

  /// Screen video is sent only while the current viewer is verified. A disabled track makes WebRTC send black
  /// frames instead of the screen; flutter_webrtc supports this reliably on Android (adding the track after
  /// verification would need renegotiation). The tracks are shared by all connections, hence the reset in
  /// [_closePeer] and for every new offer.
  ///
  /// The track is enabled only while the CURRENT peer is verified and no other peer connection is still closing
  /// (the sender is single-viewer, so the current verified peer is then the only one that can see the screen).
  bool get videoEnabled => (_peer?.isVerified ?? false) && _pendingCloses == 0;
  bool _videoApplied = false;

  void _syncVideo() {
    final enabled = videoEnabled;
    _videoApplied = enabled;
    for (final t in _stream?.getVideoTracks() ?? const <MediaStreamTrack>[]) {
      t.enabled = enabled;
    }
  }

  @visibleForTesting
  bool get videoEnabledApplied => _videoApplied;

  void _startBindTimer(PeerBinding peer) {
    _bindTimer?.cancel();
    _bindTimer = Timer(bindTimeout, () {
      // Judges only the peer that was waiting; a verified or already replaced one is left alone.
      if (!peer.isVerified && !peer.isRetired && identical(peer, _peer)) unawaited(_dropUnverified());
    });
  }

  Future<void> _dropUnverified() async {
    await _closePeer();
    if (_state == SenderState.sharing) _set(_state, 'Viewer rejected: secure connection check failed');
  }

  /// Same policy as src/Core/WebRTC/DataChannelPolicy.cs: oversize and unknown messages are dropped.
  static const maxDataChannelBytes = 64 * 1024;

  /// Test hook: a text from the current peer (fingerprints are not needed once it is verified).
  @visibleForTesting
  void handleDataChannelText(String text) {
    final peer = _peer;
    if (peer == null) return;
    unawaited(_onText(peer, _dc, text, () async => (sender: null, viewer: null)));
  }

  /// One data channel message of [peer]. Until the viewer's e2e_bind verified, nothing else is processed
  /// (DataChannelManager.GrantedCapabilities).
  Future<void> _onText(
    PeerBinding peer,
    RTCDataChannel? dc,
    String text,
    Future<({String? sender, String? viewer})> Function() fingerprints, {
    Future<void> Function(String)? send,
  }) async {
    if (text.length > maxDataChannelBytes || peer.isRetired) return;
    final Object? j;
    try {
      j = jsonDecode(text);
    } on FormatException {
      return;
    }
    if (j is! Map<String, dynamic>) return;
    Future<void> reply(String m) async {
      if (send != null) {
        await send(m);
      } else {
        await dc?.send(RTCDataChannelMessage(m));
      }
    }

    if (j['type'] == 'e2e_bind') {
      if (!peer.isVerified) await _verifyViewer(peer, j, fingerprints, reply);
      return;
    }
    if (!peer.isVerified) return;

    if (j['type'] == 'authorization_request') {
      // Remote control was decided by the toggle before sharing started; the viewer only learns the result.
      await reply(jsonEncode({
        'type': 'authorization_response',
        'authorized': _controlGrantedForSession,
        'requestable': false, // no interactive approval on the phone: the toggle before sharing decides
        'clipboard': false,
        'timestamp': DateTime.now().toUtc().toIso8601String(),
      }));
      return;
    }
    if (!_controlGrantedForSession || j.containsKey('type')) return;

    final input = InputMessage.fromJson(j);
    final mapper = _mapper;
    if (input == null || mapper == null) return;
    for (final action in mapper.handle(input)) {
      unawaited(device.perform(action).catchError((Object e) => debugPrint('Gesture failed: $e')));
    }
  }

  /// Verifies the viewer's bind, answers with our own, and only then opens video and input for that peer.
  /// A wrong bind sends nothing back (our MAC must not go to an unverified peer) and drops the viewer.
  Future<void> _verifyViewer(
    PeerBinding peer,
    Map<String, dynamic> json,
    Future<({String? sender, String? viewer})> Function() fingerprints,
    Future<void> Function(String) reply,
  ) async {
    final bind = tryReadBind(json);
    final fp = await fingerprints();
    if (peer.isRetired) return; // replaced while the descriptions were read
    final key = (bind == null || bind.role != viewerRole || fp.sender == null || fp.viewer == null)
        ? null
        : _e2eKeys.tryVerify(viewerRole, fp.sender!, fp.viewer!, bind.mac);
    if (key == null) {
      debugPrint('Viewer binding did not verify');
      if (identical(peer, _peer)) await _dropUnverified();
      return;
    }
    await reply(buildBindMessage(senderRole, computeMac(key, senderRole, fp.sender!, fp.viewer!)));
    // The viewer has our proof before any frame leaves; tryVerify is false if the peer was replaced meanwhile.
    if (!peer.tryVerify()) return;
    _bindTimer?.cancel();
    if (identical(peer, _peer)) {
      _syncVideo();
      _set(SenderState.sharing, 'Viewer connected');
    }
  }

  void _listenToDevice() {
    _projectionSub ??= device.projectionStopped.listen((_) => onProjectionStopped());
    _displaySub ??= device.displayChanged.listen((_) async {
      final size = await device.displaySize();
      _mapper?.resize(size.width, size.height); // also drops a press that is in progress
    });
  }

  /// The user ended screen capture from the system UI. Input must stop at once: the flag and the mapper go
  /// first (synchronously), everything else is torn down after.
  @visibleForTesting
  Future<void> onProjectionStopped() async {
    _controlGrantedForSession = false;
    _mapper = null;
    await _shutdown();
    _set(SenderState.idle, 'Sharing ended: screen capture was stopped');
  }

  /// Test hook: behave as if sharing with remote control had started (no WebRTC involved).
  @visibleForTesting
  void debugArmControl(double width, double height) {
    _controlGrantedForSession = true;
    _mapper = GestureMapper(width: width, height: height);
    _listenToDevice();
    _peer = PeerBinding()..tryVerify();
  }

  /// Test hook: feed one data channel text of [peer] through the real handler; [send] replaces the channel.
  @visibleForTesting
  Future<void> debugOnText(
          PeerBinding peer, String text, Future<({String? sender, String? viewer})> Function() fingerprints,
          {Future<void> Function(String)? send}) =>
      _onText(peer, null, text, fingerprints, send: send);

  /// Test hook: a fresh, unverified peer connection becomes the current one (the previous one is retired).
  @visibleForTesting
  PeerBinding beginDebugPeer() {
    _peer?.retire();
    final peer = _peer = PeerBinding();
    _syncVideo();
    return peer;
  }

  @visibleForTesting
  void addDebugKey(List<int> e2eKey) => _e2eKeys.add(e2eKey, DateTime.now().toUtc().add(const Duration(seconds: 45)));

  @visibleForTesting
  bool get controlGranted => _controlGrantedForSession;

  Future<void> _closePeer() {
    final dc = _dc;
    final pc = _pc;
    _dc = null;
    _pc = null;
    _viewerId = null;
    _viewer = false;
    return _retirePeer(() async {
      await dc?.close();
      await pc?.close();
    });
  }

  /// Everything synchronous (retire, bump generation, disable the track) happens before the first await, so
  /// nothing can slip in between "replace" and "close"; [close] is the slow part.
  Future<void> _retirePeer(Future<void> Function() close) async {
    _peerGen++;
    _peer?.retire(); // a late verification of this connection can no longer succeed
    _peer = null;
    _bindTimer?.cancel();
    _pendingCloses++;
    _syncVideo(); // disabled at once, before anything is awaited
    try {
      await close();
    } finally {
      _pendingCloses--;
      _syncVideo(); // normally still disabled: the new peer is not verified yet
    }
  }

  /// Test hook: retire the current peer with a close() the test controls.
  @visibleForTesting
  Future<void> debugRetirePeer(Future<void> Function() close) => _retirePeer(close);

  Future<void> _shutdown() async {
    _startGen++;
    _stopping = true;
    _rotationTimer?.cancel();
    await _projectionSub?.cancel();
    await _displaySub?.cancel();
    _projectionSub = _displaySub = null;
    _controlGrantedForSession = false;
    _mapper = null;
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
