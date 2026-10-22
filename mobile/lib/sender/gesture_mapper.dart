import 'dart:ui';

import '../core/input_message.dart';

/// Something the Android accessibility service can do.
sealed class DeviceAction {
  const DeviceAction();
}

/// One finger stroke along [points] (screen pixels) taking [durationMs]. A single point is a tap
/// (or a long press when the duration is long).
class StrokeAction extends DeviceAction {
  const StrokeAction(this.points, this.durationMs);
  final List<Offset> points;
  final int durationMs;

  @override
  bool operator ==(Object other) =>
      other is StrokeAction && other.durationMs == durationMs && _sameOffsets(other.points, points);
  @override
  int get hashCode => Object.hash(durationMs, Object.hashAll(points));
  @override
  String toString() => 'Stroke($points, ${durationMs}ms)';
}

bool _sameOffsets(List<Offset> a, List<Offset> b) {
  if (a.length != b.length) return false;
  for (var i = 0; i < a.length; i++) {
    if (a[i] != b[i]) return false;
  }
  return true;
}

enum GlobalActionKind { back, home, recents }

class GlobalAction extends DeviceAction {
  const GlobalAction(this.kind);
  final GlobalActionKind kind;

  @override
  bool operator ==(Object other) => other is GlobalAction && other.kind == kind;
  @override
  int get hashCode => kind.hashCode;
  @override
  String toString() => 'Global(${kind.name})';
}

/// Translates mouse-style [InputMessage]s (normalized 0..1) into Android gestures.
///
/// Android can only inject a whole stroke at once (`dispatchGesture`), so a drag is buffered between
/// MouseDown and MouseUp and replayed with its real duration when the button is released.
///  - left click (down+up without moving) -> tap; held >= [longPressMs] -> long press
///  - left drag -> swipe along the recorded path
///  - wheel -> short swipe (wheel up = finger moves down = content scrolls up, like a mouse wheel)
///  - right button -> Back, middle -> Home, extra1 -> Recents, Esc key -> Back
class GestureMapper {
  GestureMapper({required this.width, required this.height, int Function()? nowMs})
      : _now = nowMs ?? (() => DateTime.now().millisecondsSinceEpoch),
        _pos = Offset(width / 2, height / 2);

  final double width;
  final double height;
  final int Function() _now;

  /// Movement (px) below which a press/release counts as a tap.
  static const slop = 12.0;
  static const longPressMs = 500;
  static const maxPathPoints = 40;
  static const _escapeVk = 27;

  Offset _pos;
  List<Offset>? _path;
  int _downAt = 0;

  List<DeviceAction> handle(InputMessage m) {
    switch (m.type) {
      case InputType.mouseMove:
        if (m.x == null || m.y == null) return const [];
        _pos = Offset(m.x!.clamp(0.0, 1.0) * width, m.y!.clamp(0.0, 1.0) * height);
        _path?.add(_pos);
        return const [];
      case InputType.mouseDown:
        switch (m.button ?? MouseButton.left) {
          case MouseButton.left:
            _path = [_pos];
            _downAt = _now();
            return const [];
          case MouseButton.right:
            return const [GlobalAction(GlobalActionKind.back)];
          case MouseButton.middle:
            return const [GlobalAction(GlobalActionKind.home)];
          case MouseButton.extra1:
            return const [GlobalAction(GlobalActionKind.recents)];
          case MouseButton.extra2:
            return const [];
        }
      case InputType.mouseUp:
        if ((m.button ?? MouseButton.left) != MouseButton.left || _path == null) return const [];
        return [_finishLeft()];
      case InputType.mouseWheel:
        final delta = m.wheelDelta ?? 0;
        if (delta == 0) return const [];
        final half = height * 0.15;
        final dy = delta > 0 ? half : -half;
        final top = (_pos.dy - dy).clamp(0.0, height);
        final bottom = (_pos.dy + dy).clamp(0.0, height);
        return [StrokeAction([Offset(_pos.dx, top), Offset(_pos.dx, bottom)], 250)];
      case InputType.keyDown:
        return m.keyCode == _escapeVk ? const [GlobalAction(GlobalActionKind.back)] : const [];
      case InputType.keyUp:
        return const [];
    }
  }

  StrokeAction _finishLeft() {
    final path = _path!;
    _path = null;
    final elapsed = _now() - _downAt;
    final moved = path.any((p) => (p - path.first).distance > slop);
    if (!moved) {
      return StrokeAction([path.first], elapsed >= longPressMs ? elapsed.clamp(longPressMs, 3000) : 50);
    }
    return StrokeAction(_thin(path), elapsed.clamp(100, 3000));
  }

  /// Keeps at most [maxPathPoints] points (Android has a stroke length limit; extra points add nothing).
  static List<Offset> _thin(List<Offset> path) {
    if (path.length <= maxPathPoints) return List.of(path);
    final step = (path.length - 1) / (maxPathPoints - 1);
    return [for (var i = 0; i < maxPathPoints; i++) path[(i * step).round()]];
  }
}
