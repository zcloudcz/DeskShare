import 'dart:ui';

import 'package:deskshare/app.dart';
import 'package:deskshare/core/input_message.dart';
import 'package:deskshare/core/settings.dart';
import 'package:deskshare/sender/device_control.dart';
import 'package:deskshare/sender/gesture_mapper.dart';
import 'package:deskshare/sender/sender_session.dart';
import 'package:deskshare/viewer/viewer_session.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';
import 'package:shared_preferences/shared_preferences.dart';

class FakeDevice implements DeviceControl {
  bool accessibility = false;
  int settingsOpened = 0;
  final performed = <DeviceAction>[];

  @override
  Future<bool> isAccessibilityEnabled() async => accessibility;
  @override
  Future<void> openAccessibilitySettings() async => settingsOpened++;
  @override
  Future<Size> displaySize() async => const Size(1080, 2400);
  @override
  Future<void> perform(DeviceAction action) async => performed.add(action);
  @override
  Future<void> startCaptureService() async {}
  @override
  Future<void> stopCaptureService() async {}
}

class FakeViewer extends ViewerSession {
  ViewerState _state = ViewerState.idle;
  String _status = '';
  bool available = false;
  bool _control = false;
  String? lastServerId;
  String? lastPasskey;
  final sent = <InputMessage>[];

  @override
  ViewerState get state => _state;
  @override
  String get status => _status;
  @override
  bool get controlAvailable => available;
  @override
  bool get controlEnabled => _control;
  @override
  RTCVideoRenderer? get renderer => null;

  void emit(ViewerState s, String status) {
    _state = s;
    _status = status;
    notifyListeners();
  }

  @override
  Future<void> connect({required String serverId, required String passkey}) async {
    lastServerId = serverId;
    lastPasskey = passkey;
    emit(ViewerState.connecting, 'Authenticating...');
  }

  @override
  Future<void> disconnect() async => emit(ViewerState.idle, '');
  @override
  void setControl(bool enabled) {
    _control = enabled && available;
    notifyListeners();
  }

  @override
  void sendInput(InputMessage message) => sent.add(message);
}

class FakeSender extends SenderSession {
  SenderState _state = SenderState.idle;
  String _status = '';
  bool _control = false;
  int starts = 0;
  int stops = 0;

  @override
  SenderState get state => _state;
  @override
  String get status => _status;
  @override
  String get serverId => 'server-123456789';
  @override
  String get passkey => _state == SenderState.sharing ? 'ABCDEFGHJ' : '';
  @override
  DateTime? get passkeyValidTo => _state == SenderState.sharing ? DateTime.now().toUtc().add(const Duration(seconds: 30)) : null;
  @override
  bool get viewerConnected => false;
  @override
  bool get controlRequested => _control;
  @override
  set controlRequested(bool value) {
    _control = value;
    notifyListeners();
  }

  /// When set, start() fails with this message (like a denied capture permission).
  String? failure;

  @override
  Future<void> start() async {
    starts++;
    _state = failure == null ? SenderState.sharing : SenderState.failed;
    _status = failure ?? 'Waiting for a viewer...';
    notifyListeners();
  }

  @override
  Future<void> stop() async {
    stops++;
    _state = SenderState.idle;
    _status = '';
    notifyListeners();
  }
}

Future<AppServices> fakeServices({
  FakeDevice? device,
  FakeViewer? viewer,
  FakeSender? sender,
  bool canShare = true,
}) async {
  SharedPreferences.setMockInitialValues({});
  final d = device ?? FakeDevice();
  return AppServices(
    settings: AppSettings(await SharedPreferences.getInstance()),
    device: d,
    canShare: canShare,
    createViewer: () => viewer ?? FakeViewer(),
    createSender: () => sender ?? FakeSender(),
  );
}
