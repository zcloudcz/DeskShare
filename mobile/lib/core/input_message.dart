/// `InputMessageType` names. The web viewer sends the NAME in a PascalCase "Type" property;
/// the host's DataChannelPolicy accepts only exact names (or numbers).
enum InputType { mouseMove, mouseDown, mouseUp, mouseWheel, keyDown, keyUp }

enum MouseButton { left, right, middle, extra1, extra2 }

String _pascal(String s) => s[0].toUpperCase() + s.substring(1);

/// Remote input event. X/Y are normalized 0..1 over the shared screen.
class InputMessage {
  const InputMessage({
    required this.type,
    this.x,
    this.y,
    this.button,
    this.wheelDelta,
    this.keyCode,
    this.shift = false,
    this.control = false,
    this.alt = false,
  });

  const InputMessage.move(double x, double y) : this(type: InputType.mouseMove, x: x, y: y);
  const InputMessage.down(MouseButton b) : this(type: InputType.mouseDown, button: b);
  const InputMessage.up(MouseButton b) : this(type: InputType.mouseUp, button: b);
  const InputMessage.wheel(int delta) : this(type: InputType.mouseWheel, wheelDelta: delta);

  final InputType type;
  final double? x;
  final double? y;
  final MouseButton? button;
  final int? wheelDelta;

  /// Windows virtual key code (VK_*).
  final int? keyCode;
  final bool shift;
  final bool control;
  final bool alt;

  Map<String, dynamic> toJson() => {
        'Type': _pascal(type.name),
        if (x != null) 'X': x,
        if (y != null) 'Y': y,
        if (button != null) 'Button': _pascal(button!.name),
        if (wheelDelta != null) 'WheelDelta': wheelDelta,
        if (keyCode != null) ...{'KeyCode': keyCode, 'ScanCode': 0},
        'Shift': shift,
        'Control': control,
        'Alt': alt,
        'Timestamp': DateTime.now().toUtc().toIso8601String(),
      };

  /// Parses a data channel message; null when it is not an input message.
  /// Accepts the type as name or as the .NET enum number.
  static InputMessage? fromJson(Map<String, dynamic> json) {
    final t = json['Type'];
    InputType? type;
    if (t is String) {
      type = InputType.values.where((e) => _pascal(e.name) == t).firstOrNull;
    } else if (t is int && t >= 0 && t < InputType.values.length) {
      type = InputType.values[t];
    }
    if (type == null) return null;

    MouseButton? button;
    final b = json['Button'];
    if (b is String) {
      button = MouseButton.values.where((e) => _pascal(e.name) == b).firstOrNull;
    } else if (b is int && b >= 0 && b < MouseButton.values.length) {
      button = MouseButton.values[b];
    }

    return InputMessage(
      type: type,
      x: (json['X'] as num?)?.toDouble(),
      y: (json['Y'] as num?)?.toDouble(),
      button: button,
      wheelDelta: (json['WheelDelta'] as num?)?.toInt(),
      keyCode: (json['KeyCode'] as num?)?.toInt(),
      shift: json['Shift'] == true,
      control: json['Control'] == true,
      alt: json['Alt'] == true,
    );
  }
}
