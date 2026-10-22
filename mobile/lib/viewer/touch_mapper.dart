import 'dart:ui';

import '../core/input_message.dart';

/// Touch -> mouse mapping, same rules as the web viewer (remote-control.js / mobile.css):
///  - tap = left click
///  - drag with one finger = left button held while the pointer moves
///  - two-finger tap = right click
///  - two-finger pinch/spread = mouse wheel (+-120 per step)
///
/// Pure logic (no Flutter widgets) so it is unit-testable. Unlike the web version the left button is
/// pressed only once the finger moved past [slop], so a two-finger tap does not first click left.
class TouchMapper {
  TouchMapper({this.slop = 8, this.pinchThreshold = 5});

  /// Pixels a finger may move before a touch becomes a drag.
  final double slop;

  /// Pixels the finger distance must change to emit one wheel step.
  final double pinchThreshold;

  final Map<int, Offset> _pointers = {};
  int? _primary;
  Offset _start = Offset.zero;
  bool _dragging = false;
  bool _multi = false;
  bool _pinched = false;
  bool _rightClickDone = false;
  double _lastDistance = 0;

  InputMessage _moveTo(Offset p, Size area) => InputMessage.move(
        (p.dx / area.width).clamp(0.0, 1.0),
        (p.dy / area.height).clamp(0.0, 1.0),
      );

  double _distance() {
    final pts = _pointers.values.take(2).toList();
    return pts.length < 2 ? 0 : (pts[0] - pts[1]).distance;
  }

  List<InputMessage> down(int id, Offset pos, Size area) {
    _pointers[id] = pos;
    if (_pointers.length == 1) {
      _primary = id;
      _start = pos;
      _dragging = _multi = _pinched = _rightClickDone = false;
      return [_moveTo(pos, area)];
    }
    _multi = true;
    _lastDistance = _distance();
    return const [];
  }

  List<InputMessage> move(int id, Offset pos, Size area) {
    if (!_pointers.containsKey(id)) return const [];
    _pointers[id] = pos;

    if (_multi) {
      final d = _distance();
      final delta = d - _lastDistance;
      if (delta.abs() <= pinchThreshold) return const [];
      _lastDistance = d;
      _pinched = true;
      return [InputMessage.wheel(delta > 0 ? 120 : -120)];
    }

    if (id != _primary) return const [];
    if (!_dragging) {
      if ((pos - _start).distance <= slop) return const [];
      _dragging = true;
      return [const InputMessage.down(MouseButton.left), _moveTo(pos, area)];
    }
    return [_moveTo(pos, area)];
  }

  List<InputMessage> up(int id, Offset pos, Size area) {
    if (!_pointers.containsKey(id)) return const [];
    _pointers.remove(id);
    final out = <InputMessage>[];

    if (_multi) {
      if (!_pinched && !_rightClickDone) {
        _rightClickDone = true;
        out.addAll([
          _moveTo(_start, area),
          const InputMessage.down(MouseButton.right),
          const InputMessage.up(MouseButton.right),
        ]);
      }
    } else if (_dragging) {
      out.addAll([_moveTo(pos, area), const InputMessage.up(MouseButton.left)]);
    } else {
      out.addAll([const InputMessage.down(MouseButton.left), const InputMessage.up(MouseButton.left)]);
    }

    if (_pointers.isEmpty) {
      _primary = null;
      _dragging = _multi = _pinched = false;
    }
    return out;
  }

  /// A cancelled gesture (e.g. the route was closed): release anything held.
  List<InputMessage> cancel() {
    final out = _dragging ? [const InputMessage.up(MouseButton.left)] : <InputMessage>[];
    _pointers.clear();
    _primary = null;
    _dragging = _multi = _pinched = false;
    return out;
  }
}

/// Windows virtual-key codes + shift flag for printable ASCII (US layout), used by the on-screen keyboard.
/// Returns null for characters we cannot express (e.g. accents).
({int vk, bool shift})? keyForChar(String ch) {
  if (ch.length != 1) return null;
  final c = ch.codeUnitAt(0);
  if (c >= 0x61 && c <= 0x7a) return (vk: c - 0x20, shift: false); // a-z -> VK_A..Z
  if (c >= 0x41 && c <= 0x5a) return (vk: c, shift: true); // A-Z
  if (c >= 0x30 && c <= 0x39) return (vk: c, shift: false); // 0-9
  const shiftedDigits = ')!@#\$%^&*(';
  final sd = shiftedDigits.indexOf(ch);
  if (sd >= 0) return (vk: 0x30 + sd, shift: true);
  const plain = {
    ' ': 32, '\n': 13, '\t': 9,
    ';': 186, '=': 187, ',': 188, '-': 189, '.': 190, '/': 191, '`': 192,
    '[': 219, '\\': 220, ']': 221, "'": 222,
  };
  const shifted = {
    ':': 186, '+': 187, '<': 188, '_': 189, '>': 190, '?': 191, '~': 192,
    '{': 219, '|': 220, '}': 221, '"': 222,
  };
  if (plain.containsKey(ch)) return (vk: plain[ch]!, shift: false);
  if (shifted.containsKey(ch)) return (vk: shifted[ch]!, shift: true);
  return null;
}

/// KeyDown+KeyUp pair for one character, or empty when it cannot be typed remotely.
List<InputMessage> typeChar(String ch) {
  final k = keyForChar(ch);
  if (k == null) return const [];
  return [
    InputMessage(type: InputType.keyDown, keyCode: k.vk, shift: k.shift),
    InputMessage(type: InputType.keyUp, keyCode: k.vk, shift: k.shift),
  ];
}

/// Press+release of a special key (VK code), e.g. 8 = Backspace, 27 = Esc, 37..40 = arrows.
List<InputMessage> pressKey(int vk) => [
      InputMessage(type: InputType.keyDown, keyCode: vk),
      InputMessage(type: InputType.keyUp, keyCode: vk),
    ];
