using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// Linux implementation of input controller using XTest extension.
/// Provides keyboard and mouse input injection for X11-based systems.
/// </summary>
/// <remarks>
/// This implementation uses the XTest extension (libXtst.so) to simulate input events.
/// XTest is the standard way to inject keyboard and mouse events on X11 systems.
///
/// For Wayland systems, a different approach would be needed (likely using virtual input devices).
///
/// Dependencies:
/// - libX11.so (X11 library)
/// - libXtst.so (X Test extension)
///
/// XTest functions:
/// - XTestFakeKeyEvent: Simulate keyboard press/release
/// - XTestFakeButtonEvent: Simulate mouse button press/release
/// - XTestFakeMotionEvent: Simulate mouse movement
///
/// Security: Requires X11 access and XTest extension to be enabled (usually is by default).
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class XTestInputController : IInputController
{
    private readonly ILogger _logger;
    private IntPtr _display = IntPtr.Zero;
    private int _screenWidth;
    private int _screenHeight;
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

    // X11 native methods
    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayWidth(IntPtr display, int screen);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayHeight(IntPtr display, int screen);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    // XTest extension methods
    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool is_press, ulong delay);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeButtonEvent(IntPtr display, uint button, bool is_press, ulong delay);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);

    public event EventHandler<InputAuthorizationState>? AuthorizationStateChanged;

    public bool IsEnabled => _authorizationState == InputAuthorizationState.Authorized;

    public InputAuthorizationState AuthorizationState => _authorizationState;

    public XTestInputController(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> RequestAuthorizationAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentNullException(nameof(clientId));

        if (_authorizationState == InputAuthorizationState.Authorized)
        {
            _logger.Information("[XTestInputController] Already authorized");
            return true;
        }

        try
        {
            // Change to pending state
            ChangeAuthorizationState(InputAuthorizationState.Pending);

            // Open X display connection
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
            {
                _logger.Error("[XTestInputController] Failed to open X display");
                ChangeAuthorizationState(InputAuthorizationState.Denied);
                return false;
            }

            // Get screen dimensions
            _screenWidth = XDisplayWidth(_display, 0);
            _screenHeight = XDisplayHeight(_display, 0);

            _logger.Information("[XTestInputController] Screen size: {Width}x{Height}", _screenWidth, _screenHeight);

            // Authorization successful
            _authorizationGrantedAt = DateTime.UtcNow;
            ChangeAuthorizationState(InputAuthorizationState.Authorized);

            _logger.Information("[XTestInputController] Authorization granted to client: {ClientId}", clientId);

            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[XTestInputController] Authorization failed");
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
            _logger.Information("[XTestInputController] Authorization revoked");
        }
    }

    public bool ApplyInput(InputMessage input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        if (_disposed)
            throw new ObjectDisposedException(nameof(XTestInputController));

        // Check authorization
        if (!IsEnabled)
        {
            _eventsRejected++;
            _logger.Warning("[XTestInputController] Input rejected - not authorized");
            return false;
        }

        // Rate limiting
        if (!CheckRateLimit())
        {
            _eventsRejected++;
            _logger.Warning("[XTestInputController] Input rejected - rate limit exceeded");
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
            _logger.Error(ex, "[XTestInputController] Failed to apply input: {Type}", input.Type);
            _eventsRejected++;
            return false;
        }
    }

    private bool HandleMouseMove(InputMessage input)
    {
        if (!input.X.HasValue || !input.Y.HasValue)
        {
            _logger.Warning("[XTestInputController] MouseMove missing coordinates");
            return false;
        }

        // Validate normalized coordinates (0.0 - 1.0)
        if (input.X.Value < 0 || input.X.Value > 1 || input.Y.Value < 0 || input.Y.Value > 1)
        {
            _logger.Warning("[XTestInputController] MouseMove coordinates out of range: ({X}, {Y})", input.X, input.Y);
            return false;
        }

        // Convert normalized coordinates to absolute screen coordinates
        int absoluteX = (int)(input.X.Value * _screenWidth);
        int absoluteY = (int)(input.Y.Value * _screenHeight);

        // Send mouse move event
        int result = XTestFakeMotionEvent(_display, -1, absoluteX, absoluteY, 0);
        XFlush(_display);

        return result != 0;
    }

    private bool HandleMouseButton(InputMessage input, bool isPress)
    {
        if (!input.Button.HasValue)
        {
            _logger.Warning("[XTestInputController] MouseButton missing button value");
            return false;
        }

        // Map MouseButton enum to X11 button codes
        // X11: 1=Left, 2=Middle, 3=Right, 8=Back, 9=Forward
        uint xButton = input.Button.Value switch
        {
            MouseButton.Left => 1,
            MouseButton.Middle => 2,
            MouseButton.Right => 3,
            MouseButton.Extra1 => 8,  // Back
            MouseButton.Extra2 => 9,  // Forward
            _ => 0
        };

        if (xButton == 0)
        {
            _logger.Warning("[XTestInputController] Unknown mouse button: {Button}", input.Button.Value);
            return false;
        }

        int result = XTestFakeButtonEvent(_display, xButton, isPress, 0);
        XFlush(_display);

        return result != 0;
    }

    private bool HandleMouseWheel(InputMessage input)
    {
        if (!input.WheelDelta.HasValue)
        {
            _logger.Warning("[XTestInputController] MouseWheel missing delta value");
            return false;
        }

        // Validate wheel delta (typical range: -120 to +120 per notch)
        if (Math.Abs(input.WheelDelta.Value) > 1000)
        {
            _logger.Warning("[XTestInputController] MouseWheel delta too large: {Delta}", input.WheelDelta.Value);
            return false;
        }

        // X11 wheel events use buttons 4 (up) and 5 (down)
        // Each "click" of these buttons represents one wheel notch
        uint wheelButton = input.WheelDelta.Value > 0 ? (uint)4 : (uint)5;
        int notches = Math.Abs(input.WheelDelta.Value) / 120;

        // Send multiple button press/release events for smooth scrolling
        for (int i = 0; i < notches; i++)
        {
            XTestFakeButtonEvent(_display, wheelButton, true, 0);
            XTestFakeButtonEvent(_display, wheelButton, false, 0);
        }

        XFlush(_display);
        return true;
    }

    private bool HandleKeyboard(InputMessage input, bool isPress)
    {
        if (!input.KeyCode.HasValue)
        {
            _logger.Warning("[XTestInputController] Keyboard event missing keycode");
            return false;
        }

        // Validate keycode range (X11 keycodes are typically 8-255)
        if (input.KeyCode.Value < 0 || input.KeyCode.Value > 255)
        {
            _logger.Warning("[XTestInputController] Invalid keycode: {KeyCode}", input.KeyCode.Value);
            return false;
        }

        // Note: input.KeyCode is a Windows Virtual-Key code
        // We need to convert it to X11 keycode
        // For now, we'll use a direct mapping (needs proper conversion table)
        uint xKeycode = ConvertVirtualKeyToXKeycode(input.KeyCode.Value);

        int result = XTestFakeKeyEvent(_display, xKeycode, isPress, 0);
        XFlush(_display);

        return result != 0;
    }

    /// <summary>
    /// Converts Windows Virtual-Key code to X11 keycode.
    /// This is a simplified mapping - a full implementation would need a complete lookup table.
    /// </summary>
    private uint ConvertVirtualKeyToXKeycode(int virtualKey)
    {
        // TODO: Implement proper Virtual-Key to X11 keycode mapping
        // This is a complex mapping that requires a lookup table

        // For now, use a basic offset for common keys
        // A-Z: VK 0x41-0x5A -> X11 keycode 38-61
        if (virtualKey >= 0x41 && virtualKey <= 0x5A)
            return (uint)(virtualKey - 0x41 + 38);

        // 0-9: VK 0x30-0x39 -> X11 keycode 19-28
        if (virtualKey >= 0x30 && virtualKey <= 0x39)
            return (uint)(virtualKey - 0x30 + 19);

        // Default: add offset of 8 (X11 keycodes start at 8)
        return (uint)(virtualKey + 8);
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

        if (_display != IntPtr.Zero)
        {
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }

        _disposed = true;
        _logger.Information("[XTestInputController] Disposed");
    }
}
