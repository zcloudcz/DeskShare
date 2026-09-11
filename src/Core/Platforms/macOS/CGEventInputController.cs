using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Core.Platforms.macOS;

/// <summary>
/// macOS implementation of input controller using CGEvent API.
/// Provides keyboard and mouse input injection for macOS systems.
/// </summary>
/// <remarks>
/// This implementation uses Core Graphics (Quartz) Event Services to simulate input.
/// CGEvent is the standard way to create and post input events on macOS.
///
/// CGEvent functions:
/// - CGEventCreateMouseEvent: Create mouse events
/// - CGEventCreateKeyboardEvent: Create keyboard events
/// - CGEventPost: Post events to the event stream
/// - CGEventCreateScrollWheelEvent: Create scroll events
///
/// Security: macOS requires Accessibility permissions for input injection.
/// Apps must be granted permission in System Preferences > Security & Privacy > Accessibility.
///
/// Framework: CoreGraphics.framework
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class CGEventInputController : IInputController
{
    private readonly ILogger _logger;
    private bool _disposed;

    // Authorization state
    private InputAuthorizationState _authorizationState = InputAuthorizationState.NotAuthorized;
    private DateTime _authorizationGrantedAt;

    // Rate limiting (120 events per second max)
    private const int MaxEventsPerSecond = 120;
    private readonly Queue<DateTime> _eventTimestamps = new();

    // Statistics tracking
    private int _keyboardEventsProcessed;
    private int _mouseEventsProcessed;
    private int _eventsRejected;

    // Screen dimensions
    private int _screenWidth;
    private int _screenHeight;

    // CoreGraphics native methods
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, CGEventType mouseType,
        CGPoint mouseCursorPosition, CGMouseButton mouseButton);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, bool keyDown);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, CGScrollEventUnit units,
        uint wheelCount, int wheel1);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGEventPost(CGEventTapLocation tap, IntPtr @event);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGMainDisplayID();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGDisplayPixelsWide(IntPtr display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGDisplayPixelsHigh(IntPtr display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern bool AXIsProcessTrusted();

    // CoreGraphics structures and enums
    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double x;
        public double y;

        public CGPoint(double x, double y)
        {
            this.x = x;
            this.y = y;
        }
    }

    private enum CGEventType : uint
    {
        Null = 0,
        LeftMouseDown = 1,
        LeftMouseUp = 2,
        RightMouseDown = 3,
        RightMouseUp = 4,
        MouseMoved = 5,
        LeftMouseDragged = 6,
        RightMouseDragged = 7,
        KeyDown = 10,
        KeyUp = 11,
        FlagsChanged = 12,
        ScrollWheel = 22,
        OtherMouseDown = 25,
        OtherMouseUp = 26,
        OtherMouseDragged = 27
    }

    private enum CGMouseButton : uint
    {
        Left = 0,
        Right = 1,
        Center = 2
    }

    private enum CGEventTapLocation : uint
    {
        HID = 0,
        Session = 1,
        AnnotatedSession = 2
    }

    private enum CGScrollEventUnit : uint
    {
        Pixel = 0,
        Line = 1
    }

    public event EventHandler<InputAuthorizationState>? AuthorizationStateChanged;

    public bool IsEnabled => _authorizationState == InputAuthorizationState.Authorized;

    public InputAuthorizationState AuthorizationState => _authorizationState;

    public CGEventInputController(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> RequestAuthorizationAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentNullException(nameof(clientId));

        if (_authorizationState == InputAuthorizationState.Authorized)
        {
            _logger.Information("[CGEventInputController] Already authorized");
            return true;
        }

        try
        {
            // Change to pending state
            ChangeAuthorizationState(InputAuthorizationState.Pending);

            // Check for Accessibility permissions
            bool isTrusted = AXIsProcessTrusted();
            if (!isTrusted)
            {
                _logger.Error("[CGEventInputController] Accessibility permissions not granted. " +
                    "Please enable in System Preferences > Security & Privacy > Accessibility");
                ChangeAuthorizationState(InputAuthorizationState.Denied);
                return false;
            }

            // Get screen dimensions
            IntPtr mainDisplay = CGMainDisplayID();
            _screenWidth = CGDisplayPixelsWide(mainDisplay);
            _screenHeight = CGDisplayPixelsHigh(mainDisplay);

            _logger.Information("[CGEventInputController] Screen size: {Width}x{Height}", _screenWidth, _screenHeight);

            // Authorization successful
            _authorizationGrantedAt = DateTime.UtcNow;
            ChangeAuthorizationState(InputAuthorizationState.Authorized);

            _logger.Information("[CGEventInputController] Authorization granted to client: {ClientId}", clientId);

            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CGEventInputController] Authorization failed");
            ChangeAuthorizationState(InputAuthorizationState.Denied);
            return false;
        }
    }

    public void RevokeAuthorization()
    {
        if (_authorizationState == InputAuthorizationState.Authorized ||
            _authorizationState == InputAuthorizationState.Pending)
        {
            ChangeAuthorizationState(InputAuthorizationState.Revoked);
            _logger.Information("[CGEventInputController] Authorization revoked");
        }
    }

    public bool ApplyInput(InputMessage input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        if (_disposed)
            throw new ObjectDisposedException(nameof(CGEventInputController));

        // Check authorization
        if (!IsEnabled)
        {
            _eventsRejected++;
            _logger.Warning("[CGEventInputController] Input rejected - not authorized");
            return false;
        }

        // Rate limiting
        if (!CheckRateLimit())
        {
            _eventsRejected++;
            _logger.Warning("[CGEventInputController] Input rejected - rate limit exceeded");
            return false;
        }

        try
        {
            bool success = input.Type switch
            {
                InputMessageType.MouseMove => HandleMouseMove(input),
                InputMessageType.MouseDown => HandleMouseButton(input, true),
                InputMessageType.MouseUp => HandleMouseButton(input, false),
                InputMessageType.MouseWheel => HandleMouseWheel(input),
                InputMessageType.KeyDown => HandleKeyboard(input, true),
                InputMessageType.KeyUp => HandleKeyboard(input, false),
                _ => false
            };

            if (success)
            {
                if (input.Type is InputMessageType.KeyDown or InputMessageType.KeyUp)
                    _keyboardEventsProcessed++;
                else
                    _mouseEventsProcessed++;
            }
            else
            {
                _eventsRejected++;
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CGEventInputController] Failed to apply input: {Type}", input.Type);
            _eventsRejected++;
            return false;
        }
    }

    private bool HandleMouseMove(InputMessage input)
    {
        if (!input.X.HasValue || !input.Y.HasValue)
        {
            _logger.Warning("[CGEventInputController] MouseMove missing coordinates");
            return false;
        }

        // Validate normalized coordinates (0.0 - 1.0)
        if (input.X.Value < 0 || input.X.Value > 1 || input.Y.Value < 0 || input.Y.Value > 1)
        {
            _logger.Warning("[CGEventInputController] MouseMove coordinates out of range: ({X}, {Y})", input.X, input.Y);
            return false;
        }

        // Convert normalized coordinates to absolute screen coordinates
        var point = new CGPoint(
            input.X.Value * _screenWidth,
            input.Y.Value * _screenHeight);

        // Create and post mouse move event
        IntPtr moveEvent = CGEventCreateMouseEvent(IntPtr.Zero, CGEventType.MouseMoved, point, CGMouseButton.Left);
        if (moveEvent != IntPtr.Zero)
        {
            try
            {
                CGEventPost(CGEventTapLocation.HID, moveEvent);
                return true;
            }
            finally
            {
                CFRelease(moveEvent);
            }
        }

        return false;
    }

    private bool HandleMouseButton(InputMessage input, bool isPress)
    {
        if (!input.Button.HasValue)
        {
            _logger.Warning("[CGEventInputController] MouseButton missing button value");
            return false;
        }

        // Get current mouse position (we need it for the event)
        // For simplicity, using (0,0) - in production should query actual position
        var point = new CGPoint(0, 0);

        // Map MouseButton enum to CGEventType and CGMouseButton
        (CGEventType eventType, CGMouseButton cgButton) = input.Button.Value switch
        {
            MouseButton.Left => (isPress ? CGEventType.LeftMouseDown : CGEventType.LeftMouseUp, CGMouseButton.Left),
            MouseButton.Right => (isPress ? CGEventType.RightMouseDown : CGEventType.RightMouseUp, CGMouseButton.Right),
            MouseButton.Middle => (isPress ? CGEventType.OtherMouseDown : CGEventType.OtherMouseUp, CGMouseButton.Center),
            // macOS doesn't have standard CGEvent types for extra buttons
            // Would need to use CGEventSetIntegerValueField with kCGMouseEventButtonNumber
            _ => (CGEventType.Null, CGMouseButton.Left)
        };

        if (eventType == CGEventType.Null)
        {
            _logger.Warning("[CGEventInputController] Unsupported mouse button: {Button}", input.Button.Value);
            return false;
        }

        IntPtr buttonEvent = CGEventCreateMouseEvent(IntPtr.Zero, eventType, point, cgButton);
        if (buttonEvent != IntPtr.Zero)
        {
            try
            {
                CGEventPost(CGEventTapLocation.HID, buttonEvent);
                return true;
            }
            finally
            {
                CFRelease(buttonEvent);
            }
        }

        return false;
    }

    private bool HandleMouseWheel(InputMessage input)
    {
        if (!input.WheelDelta.HasValue)
        {
            _logger.Warning("[CGEventInputController] MouseWheel missing delta value");
            return false;
        }

        // Validate wheel delta (typical range: -120 to +120 per notch)
        if (Math.Abs(input.WheelDelta.Value) > 1000)
        {
            _logger.Warning("[CGEventInputController] MouseWheel delta too large: {Delta}", input.WheelDelta.Value);
            return false;
        }

        // Convert Windows-style delta (120 units per notch) to macOS scroll lines
        int scrollLines = input.WheelDelta.Value / 120;

        IntPtr scrollEvent = CGEventCreateScrollWheelEvent(IntPtr.Zero, CGScrollEventUnit.Line, 1, scrollLines);
        if (scrollEvent != IntPtr.Zero)
        {
            try
            {
                CGEventPost(CGEventTapLocation.HID, scrollEvent);
                return true;
            }
            finally
            {
                CFRelease(scrollEvent);
            }
        }

        return false;
    }

    private bool HandleKeyboard(InputMessage input, bool isPress)
    {
        if (!input.KeyCode.HasValue)
        {
            _logger.Warning("[CGEventInputController] Keyboard event missing keycode");
            return false;
        }

        // Validate keycode range
        if (input.KeyCode.Value < 0 || input.KeyCode.Value > 255)
        {
            _logger.Warning("[CGEventInputController] Invalid keycode: {KeyCode}", input.KeyCode.Value);
            return false;
        }

        // Convert Windows Virtual-Key code to macOS keycode
        ushort macKeycode = ConvertVirtualKeyToMacKeycode(input.KeyCode.Value);

        IntPtr keyEvent = CGEventCreateKeyboardEvent(IntPtr.Zero, macKeycode, isPress);
        if (keyEvent != IntPtr.Zero)
        {
            try
            {
                CGEventPost(CGEventTapLocation.HID, keyEvent);
                return true;
            }
            finally
            {
                CFRelease(keyEvent);
            }
        }

        return false;
    }

    /// <summary>
    /// Converts Windows Virtual-Key code to macOS keycode.
    /// This is a simplified mapping - a full implementation would need a complete lookup table.
    /// </summary>
    private ushort ConvertVirtualKeyToMacKeycode(int virtualKey)
    {
        // TODO: Implement proper Virtual-Key to macOS keycode mapping
        // macOS keycodes are different from both Windows VK codes and X11 keycodes

        // Common key mappings (partial):
        // A-Z: VK 0x41-0x5A -> macOS 0x00-0x19
        if (virtualKey >= 0x41 && virtualKey <= 0x5A)
            return (ushort)(virtualKey - 0x41);

        // 0-9: VK 0x30-0x39 -> macOS varies
        if (virtualKey >= 0x30 && virtualKey <= 0x39)
        {
            // macOS number keys: 1=18, 2=19, 3=20, 4=21, 5=23, 6=22, 7=26, 8=28, 9=25, 0=29
            int[] numberKeycodes = { 29, 18, 19, 20, 21, 23, 22, 26, 28, 25 };
            return (ushort)numberKeycodes[virtualKey - 0x30];
        }

        // Common special keys
        return virtualKey switch
        {
            0x08 => 51,  // Backspace
            0x09 => 48,  // Tab
            0x0D => 36,  // Return
            0x1B => 53,  // Escape
            0x20 => 49,  // Space
            0x25 => 123, // Left Arrow
            0x26 => 126, // Up Arrow
            0x27 => 124, // Right Arrow
            0x28 => 125, // Down Arrow
            _ => (ushort)virtualKey // Fallback (will likely be wrong)
        };
    }

    private bool CheckRateLimit()
    {
        var now = DateTime.UtcNow;
        var oneSecondAgo = now.AddSeconds(-1);

        // Remove old timestamps
        while (_eventTimestamps.Count > 0 && _eventTimestamps.Peek() < oneSecondAgo)
        {
            _eventTimestamps.Dequeue();
        }

        // Check if we're at the limit
        if (_eventTimestamps.Count >= MaxEventsPerSecond)
        {
            return false;
        }

        // Add current timestamp
        _eventTimestamps.Enqueue(now);
        return true;
    }

    private void ChangeAuthorizationState(InputAuthorizationState newState)
    {
        if (_authorizationState != newState)
        {
            _authorizationState = newState;
            AuthorizationStateChanged?.Invoke(this, newState);
        }
    }

    public InputStatistics GetStatistics()
    {
        return new InputStatistics
        {
            KeyboardEventsProcessed = _keyboardEventsProcessed,
            MouseEventsProcessed = _mouseEventsProcessed,
            EventsRejected = _eventsRejected,
            StartTime = _authorizationGrantedAt
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        RevokeAuthorization();

        _disposed = true;
        _logger.Information("[CGEventInputController] Disposed");
    }
}
