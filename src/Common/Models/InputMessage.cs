namespace DeskShare.Common.Models;

/// <summary>
/// Type of input message.
/// </summary>
public enum InputMessageType
{
    /// <summary>
    /// Mouse movement message.
    /// </summary>
    MouseMove,

    /// <summary>
    /// Mouse button press message.
    /// </summary>
    MouseDown,

    /// <summary>
    /// Mouse button release message.
    /// </summary>
    MouseUp,

    /// <summary>
    /// Mouse wheel scroll message.
    /// </summary>
    MouseWheel,

    /// <summary>
    /// Keyboard key press message.
    /// </summary>
    KeyDown,

    /// <summary>
    /// Keyboard key release message.
    /// </summary>
    KeyUp
}

/// <summary>
/// Mouse button identifier.
/// </summary>
public enum MouseButton
{
    /// <summary>
    /// Left mouse button.
    /// </summary>
    Left,

    /// <summary>
    /// Right mouse button.
    /// </summary>
    Right,

    /// <summary>
    /// Middle mouse button (wheel click).
    /// </summary>
    Middle,

    /// <summary>
    /// Extra button 1 (typically back button).
    /// </summary>
    Extra1,

    /// <summary>
    /// Extra button 2 (typically forward button).
    /// </summary>
    Extra2
}

/// <summary>
/// Represents a remote input message (mouse or keyboard).
/// </summary>
public sealed class InputMessage
{
    /// <summary>
    /// Type of input message.
    /// </summary>
    public InputMessageType Type { get; set; }

    /// <summary>
    /// X coordinate for mouse events (normalized 0.0 to 1.0).
    /// </summary>
    public double? X { get; set; }

    /// <summary>
    /// Y coordinate for mouse events (normalized 0.0 to 1.0).
    /// </summary>
    public double? Y { get; set; }

    /// <summary>
    /// Mouse button identifier for mouse button events.
    /// </summary>
    public MouseButton? Button { get; set; }

    /// <summary>
    /// Wheel delta for mouse wheel events (positive = up, negative = down).
    /// </summary>
    public int? WheelDelta { get; set; }

    /// <summary>
    /// Virtual key code for keyboard events (Windows VK_* codes).
    /// </summary>
    public int? KeyCode { get; set; }

    /// <summary>
    /// Scan code for keyboard events.
    /// </summary>
    public int? ScanCode { get; set; }

    /// <summary>
    /// Indicates if Shift key is pressed.
    /// </summary>
    public bool Shift { get; set; }

    /// <summary>
    /// Indicates if Control key is pressed.
    /// </summary>
    public bool Control { get; set; }

    /// <summary>
    /// Indicates if Alt key is pressed.
    /// </summary>
    public bool Alt { get; set; }

    /// <summary>
    /// Timestamp when the input was generated.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
