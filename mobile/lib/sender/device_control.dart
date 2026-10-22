import 'dart:io' show Platform;

import 'package:flutter/services.dart';

import 'gesture_mapper.dart';

/// Android-only native features (Kotlin in android/app): accessibility gestures and the
/// foreground service Android requires while the screen is captured.
abstract class DeviceControl {
  Future<bool> isAccessibilityEnabled();
  Future<void> openAccessibilitySettings();

  /// Real display size in pixels (the coordinate space of gestures).
  Future<Size> displaySize();
  Future<void> perform(DeviceAction action);

  /// Must run after the capture permission was granted and before the capture starts (Android 14+).
  Future<void> startCaptureService();
  Future<void> stopCaptureService();
}

class MethodChannelDeviceControl implements DeviceControl {
  static const _channel = MethodChannel('cz.zcloud.deskshare/device');

  /// Remote control and screen sharing exist on Android only.
  static bool get supported => Platform.isAndroid;

  @override
  Future<bool> isAccessibilityEnabled() async =>
      supported && (await _channel.invokeMethod<bool>('isAccessibilityEnabled') ?? false);

  @override
  Future<void> openAccessibilitySettings() => _channel.invokeMethod('openAccessibilitySettings');

  @override
  Future<Size> displaySize() async {
    final m = await _channel.invokeMapMethod<String, num>('displaySize');
    return Size(m!['width']!.toDouble(), m['height']!.toDouble());
  }

  @override
  Future<void> perform(DeviceAction action) async {
    switch (action) {
      case StrokeAction():
        await _channel.invokeMethod('stroke', {
          'points': [
            for (final p in action.points) [p.dx, p.dy]
          ],
          'durationMs': action.durationMs,
        });
      case GlobalAction():
        await _channel.invokeMethod('globalAction', {'name': action.kind.name});
    }
  }

  @override
  Future<void> startCaptureService() => _channel.invokeMethod('startCaptureService');

  @override
  Future<void> stopCaptureService() => _channel.invokeMethod('stopCaptureService');
}
