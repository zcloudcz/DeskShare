import 'dart:ui';

import 'package:deskshare/core/input_message.dart';
import 'package:deskshare/sender/gesture_mapper.dart';
import 'package:deskshare/viewer/touch_mapper.dart';
import 'package:flutter_test/flutter_test.dart';

String _summary(List<InputMessage> ms) => ms.map((m) {
      switch (m.type) {
        case InputType.mouseMove:
          return 'move(${m.x!.toStringAsFixed(2)},${m.y!.toStringAsFixed(2)})';
        case InputType.mouseDown:
          return 'down(${m.button!.name})';
        case InputType.mouseUp:
          return 'up(${m.button!.name})';
        case InputType.mouseWheel:
          return 'wheel(${m.wheelDelta})';
        default:
          return m.type.name;
      }
    }).join(' ');

void main() {
  group('GestureMapper (InputMessage -> Android gesture)', () {
    var now = 0;
    GestureMapper make() => GestureMapper(width: 1000, height: 2000, nowMs: () => now);
    setUp(() => now = 0);

    test('normalized coordinates become pixels; click without movement is a tap', () {
      final g = make();
      expect(g.handle(const InputMessage.move(0.5, 0.25)), isEmpty);
      expect(g.handle(const InputMessage.down(MouseButton.left)), isEmpty);
      now = 80;
      expect(g.handle(const InputMessage.up(MouseButton.left)), [
        const StrokeAction([Offset(500, 500)], 50),
      ]);
    });

    test('holding still for 500 ms is a long press with the real duration', () {
      final g = make();
      g.handle(const InputMessage.move(0.1, 0.1));
      g.handle(const InputMessage.down(MouseButton.left));
      now = 800;
      expect(g.handle(const InputMessage.up(MouseButton.left)), [
        const StrokeAction([Offset(100, 200)], 800),
      ]);
    });

    test('moving while the button is down becomes a swipe along the path', () {
      final g = make();
      g.handle(const InputMessage.move(0.5, 0.8));
      g.handle(const InputMessage.down(MouseButton.left));
      now = 100;
      g.handle(const InputMessage.move(0.5, 0.5));
      now = 250;
      g.handle(const InputMessage.move(0.5, 0.2));
      final actions = g.handle(const InputMessage.up(MouseButton.left));
      expect(actions, [
        const StrokeAction([Offset(500, 1600), Offset(500, 1000), Offset(500, 400)], 250),
      ]);
    });

    test('moves without the button do not produce gestures', () {
      final g = make();
      expect(g.handle(const InputMessage.move(0.3, 0.3)), isEmpty);
      expect(g.handle(const InputMessage.move(0.4, 0.4)), isEmpty);
      expect(g.handle(const InputMessage.up(MouseButton.left)), isEmpty);
    });

    test('long paths are thinned to a bounded number of points', () {
      final g = make();
      g.handle(const InputMessage.move(0.0, 0.0));
      g.handle(const InputMessage.down(MouseButton.left));
      for (var i = 1; i <= 200; i++) {
        g.handle(InputMessage.move(i / 200, i / 200));
      }
      now = 1000;
      final stroke = g.handle(const InputMessage.up(MouseButton.left)).single as StrokeAction;
      expect(stroke.points.length, GestureMapper.maxPathPoints);
      expect(stroke.points.first, Offset.zero);
      expect(stroke.points.last, const Offset(1000, 2000));
    });

    test('resize rescales the pointer and drops a press in progress', () {
      final g = make();
      g.handle(const InputMessage.move(0.5, 0.5));
      g.handle(const InputMessage.down(MouseButton.left));
      g.resize(2000, 1000); // rotated
      expect(g.handle(const InputMessage.up(MouseButton.left)), isEmpty, reason: 'old-space press is dropped');
      g.handle(const InputMessage.down(MouseButton.left));
      final tap = g.handle(const InputMessage.up(MouseButton.left)).single as StrokeAction;
      expect(tap.points.single, const Offset(1000, 500), reason: 'pointer keeps its normalized position');
      g.handle(const InputMessage.move(0.1, 0.9));
      g.handle(const InputMessage.down(MouseButton.left));
      expect((g.handle(const InputMessage.up(MouseButton.left)).single as StrokeAction).points.single, const Offset(200, 900));
    });

    test('wheel scrolls with a vertical swipe (wheel up moves the finger down)', () {
      final g = make();
      g.handle(const InputMessage.move(0.5, 0.5));
      final up = g.handle(const InputMessage.wheel(120)).single as StrokeAction;
      expect(up.points.first.dy, lessThan(up.points.last.dy));
      final down = g.handle(const InputMessage.wheel(-120)).single as StrokeAction;
      expect(down.points.first.dy, greaterThan(down.points.last.dy));
    });

    test('right, middle, extra1 buttons and Esc map to global actions', () {
      final g = make();
      expect(g.handle(const InputMessage.down(MouseButton.right)), [const GlobalAction(GlobalActionKind.back)]);
      expect(g.handle(const InputMessage.down(MouseButton.middle)), [const GlobalAction(GlobalActionKind.home)]);
      expect(g.handle(const InputMessage.down(MouseButton.extra1)), [const GlobalAction(GlobalActionKind.recents)]);
      expect(g.handle(const InputMessage(type: InputType.keyDown, keyCode: 27)), [const GlobalAction(GlobalActionKind.back)]);
      expect(g.handle(const InputMessage(type: InputType.keyDown, keyCode: 65)), isEmpty);
    });

    test('out of range coordinates are clamped to the screen', () {
      final g = make();
      g.handle(const InputMessage.move(1.5, -2));
      g.handle(const InputMessage.down(MouseButton.left));
      final tap = g.handle(const InputMessage.up(MouseButton.left)).single as StrokeAction;
      expect(tap.points.single, const Offset(1000, 0));
    });
  });

  group('TouchMapper (touch -> mouse, like mobile.css / remote-control.js)', () {
    const area = Size(400, 200);
    late TouchMapper t;
    setUp(() => t = TouchMapper());

    test('tap = move + left click', () {
      expect(_summary(t.down(1, const Offset(100, 50), area)), 'move(0.25,0.25)');
      expect(_summary(t.up(1, const Offset(100, 50), area)), 'down(left) up(left)');
    });

    test('small jitter below the slop is still a tap', () {
      t.down(1, const Offset(100, 50), area);
      expect(t.move(1, const Offset(104, 53), area), isEmpty);
      expect(_summary(t.up(1, const Offset(104, 53), area)), 'down(left) up(left)');
    });

    test('one-finger drag = left button held while moving', () {
      t.down(1, const Offset(100, 100), area);
      expect(_summary(t.move(1, const Offset(140, 100), area)), 'down(left) move(0.35,0.50)');
      expect(_summary(t.move(1, const Offset(200, 100), area)), 'move(0.50,0.50)');
      expect(_summary(t.up(1, const Offset(200, 100), area)), 'move(0.50,0.50) up(left)');
    });

    test('two-finger tap = right click at the first finger, no left click', () {
      expect(_summary(t.down(1, const Offset(100, 100), area)), 'move(0.25,0.50)');
      expect(t.down(2, const Offset(200, 100), area), isEmpty);
      expect(_summary(t.up(2, const Offset(200, 100), area)), 'move(0.25,0.50) down(right) up(right)');
      expect(t.up(1, const Offset(100, 100), area), isEmpty, reason: 'only one right click per gesture');
    });

    test('two-finger spread/pinch = mouse wheel', () {
      t.down(1, const Offset(100, 100), area);
      t.down(2, const Offset(150, 100), area);
      expect(_summary(t.move(2, const Offset(200, 100), area)), 'wheel(120)');
      expect(_summary(t.move(2, const Offset(120, 100), area)), 'wheel(-120)');
      expect(t.up(2, const Offset(120, 100), area), isEmpty, reason: 'a pinch is not a right click');
      expect(t.up(1, const Offset(100, 100), area), isEmpty);
    });

    test('second finger during a drag releases the left button first (no stuck button on the host)', () {
      t.down(1, const Offset(100, 100), area);
      expect(_summary(t.move(1, const Offset(160, 100), area)), 'down(left) move(0.40,0.50)');
      expect(_summary(t.down(2, const Offset(300, 100), area)), 'up(left)');
      // The gesture continues as a pinch; no second release and no right click when the fingers lift.
      expect(_summary(t.move(2, const Offset(380, 100), area)), 'wheel(120)');
      expect(t.up(2, const Offset(380, 100), area), isEmpty);
      expect(t.up(1, const Offset(160, 100), area), isEmpty);
    });

    test('coordinates outside the video are clamped to 0..1', () {
      expect(_summary(t.down(1, const Offset(-20, 500), area)), 'move(0.00,1.00)');
    });

    test('state resets between gestures', () {
      t.down(1, const Offset(10, 10), area);
      t.move(1, const Offset(100, 10), area);
      t.up(1, const Offset(100, 10), area);
      t.down(1, const Offset(10, 10), area);
      expect(_summary(t.up(1, const Offset(10, 10), area)), 'down(left) up(left)');
    });

    test('cancel releases a held button', () {
      t.down(1, const Offset(10, 10), area);
      t.move(1, const Offset(100, 10), area);
      expect(_summary(t.cancel()), 'up(left)');
      expect(t.cancel(), isEmpty);
    });
  });

  group('On-screen keyboard', () {
    test('letters, digits, space and shifted symbols map to Windows VK codes', () {
      expect(keyForChar('a'), (vk: 65, shift: false));
      expect(keyForChar('A'), (vk: 65, shift: true));
      expect(keyForChar('7'), (vk: 55, shift: false));
      expect(keyForChar('!'), (vk: 49, shift: true));
      expect(keyForChar(' '), (vk: 32, shift: false));
      expect(keyForChar('.'), (vk: 190, shift: false));
      expect(keyForChar('?'), (vk: 191, shift: true));
      expect(keyForChar('é'), isNull);
    });

    test('unshifted character is KeyDown then KeyUp, nothing for unsupported characters', () {
      final m = typeChar('b');
      expect(m.map((e) => e.type), [InputType.keyDown, InputType.keyUp]);
      expect(m.first.keyCode, 66);
      expect(m.first.shift, isFalse);
      expect(typeChar('é'), isEmpty);
    });

    test('shifted characters press a real Shift key around them (the host ignores the Shift flag)', () {
      String s(List<InputMessage> ms) => ms.map((e) => '${e.type.name}:${e.keyCode}').join(' ');
      expect(s(typeChar('A')), 'keyDown:16 keyDown:65 keyUp:65 keyUp:16');
      expect(s(typeChar('!')), 'keyDown:16 keyDown:49 keyUp:49 keyUp:16');
      expect(s(typeChar('?')), 'keyDown:16 keyDown:191 keyUp:191 keyUp:16');
    });
  });
}
