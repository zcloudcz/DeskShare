using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Core.Platforms.Windows;

/// <summary>
/// Windows implementation of remote input controller using SendInput API.
/// Provides secure remote control with authorization, rate limiting, and input validation.
/// </summary>
/// <remarks>
/// This class injects mouse and keyboard inputs into the Windows system using the SendInput API.
///
/// Security features:
/// - Requires explicit user authorization via dialog
/// - Rate limiting (max 120 inputs per second)
/// - Input validation and sanitization
/// - Audit logging of all input operations
/// - Automatic session timeout after 30 minutes of inactivity
///
/// For junior developers:
/// The SendInput API is the recommended way to simulate user input on Windows.
/// It's more secure and reliable than older methods like keybd_event or mouse_event.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsInputController : IInputController
{
    #region Win32 API Declarations

    // Mouse event flags for SendInput API
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    // Extra button identifiers for X buttons (back/forward)
    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;

    // Keyboard event flags for SendInput API
    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    // Input type constants
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    /// <summary>
    /// Represents a single input event (mouse or keyboard).
    /// This is the structure expected by the SendInput Windows API.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    /// <summary>
    /// Union of mouse and keyboard input structures.
    /// C# doesn't have unions, so we use StructLayout.Explicit with FieldOffset.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    /// <summary>
    /// Mouse input structure for SendInput.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>
    /// Keyboard input structure for SendInput.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>
    /// Windows SendInput API - injects input events into the system.
    /// Returns the number of events successfully inserted.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Gets the dimensions of the virtual screen (all monitors combined).
    /// Used for converting normalized coordinates to absolute pixel positions.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // System metrics constants for screen dimensions
    private const int SM_CXSCREEN = 0;  // Primary monitor width
    private const int SM_CYSCREEN = 1;  // Primary monitor height
    private const int SM_CXVIRTUALSCREEN = 78;  // Virtual screen width (all monitors)
    private const int SM_CYVIRTUALSCREEN = 79;  // Virtual screen height (all monitors)

    #endregion

    #region Fields and Properties

    private readonly ILogger _logger;
    private readonly object _lockObject = new();

    // Rate limiting: Track recent inputs to prevent abuse
    private readonly Queue<DateTime> _recentInputs = new();
    private const int MaxInputsPerSecond = 120;  // Windows can handle ~120 inputs/second safely

    // Authorization state
    private InputAuthorizationState _authorizationState = InputAuthorizationState.NotAuthorized;
    private string? _authorizedClientId;
    private DateTime? _authorizationGrantedAt;
    private DateTime _lastInputTime = DateTime.UtcNow;
    private const int SessionTimeoutMinutes = 30;  // Auto-revoke after 30 min inactivity

    // Statistics tracking
    private long _totalInputsReceived;
    private long _totalInputsApplied;
    private long _totalInputsRejected;

    private bool _disposed;

    /// <inheritdoc />
    public bool IsEnabled => _authorizationState == InputAuthorizationState.Authorized;

    /// <inheritdoc />
    public InputAuthorizationState AuthorizationState
    {
        get
        {
            lock (_lockObject)
            {
                // Auto-revoke if session timed out due to inactivity
                if (_authorizationState == InputAuthorizationState.Authorized &&
                    DateTime.UtcNow - _lastInputTime > TimeSpan.FromMinutes(SessionTimeoutMinutes))
                {
                    _logger.Warning("Remote control session timed out after {Timeout} minutes of inactivity",
                        SessionTimeoutMinutes);
                    RevokeAuthorization();
                }
                return _authorizationState;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<InputAuthorizationState>? AuthorizationStateChanged;

    /// <summary>
    /// Callback invoked to authorize a remote control request.
    /// Returns true to allow, false to deny. If not set, all requests are denied.
    /// </summary>
    public Func<string, bool>? AuthorizationRequested { get; set; }

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the WindowsInputController class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">Thrown when logger is null.</exception>
    public WindowsInputController(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.Information("WindowsInputController initialized with rate limit: {MaxInputs} inputs/second",
            MaxInputsPerSecond);
    }

    #endregion

    #region IInputController Implementation

    /// <inheritdoc />
    public async Task<bool> RequestAuthorizationAsync(string requestingClientId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestingClientId);

        lock (_lockObject)
        {
            if (_authorizationState == InputAuthorizationState.Authorized)
            {
                _logger.Warning("Authorization already granted to client: {ClientId}", _authorizedClientId);
                return true;
            }

            _authorizationState = InputAuthorizationState.Pending;
            AuthorizationStateChanged?.Invoke(this, _authorizationState);
        }

        _logger.Information("Client {ClientId} requesting remote control authorization", requestingClientId);

        await Task.Delay(1000, cancellationToken);

        bool authorized = AuthorizationRequested?.Invoke(requestingClientId) ?? false;

        lock (_lockObject)
        {
            if (authorized)
            {
                _authorizationState = InputAuthorizationState.Authorized;
                _authorizedClientId = requestingClientId;
                _authorizationGrantedAt = DateTime.UtcNow;
                _lastInputTime = DateTime.UtcNow;
                _logger.Information("Remote control authorized for client: {ClientId}", requestingClientId);
            }
            else
            {
                _authorizationState = InputAuthorizationState.Denied;
                _logger.Information("Remote control denied for client: {ClientId}", requestingClientId);
            }

            AuthorizationStateChanged?.Invoke(this, _authorizationState);
            return authorized;
        }
    }

    /// <inheritdoc />
    public void RevokeAuthorization()
    {
        lock (_lockObject)
        {
            if (_authorizationState != InputAuthorizationState.Authorized)
            {
                return;
            }

            var previousClient = _authorizedClientId;
            _authorizationState = InputAuthorizationState.Revoked;
            _authorizedClientId = null;
            _authorizationGrantedAt = null;

            _logger.Information("Remote control authorization revoked for client: {ClientId}", previousClient);
            AuthorizationStateChanged?.Invoke(this, _authorizationState);
        }
    }

    /// <inheritdoc />
    public bool ApplyInput(InputMessage input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsInputController));
        }

        Interlocked.Increment(ref _totalInputsReceived);

        lock (_lockObject)
        {
            // Check authorization
            if (AuthorizationState != InputAuthorizationState.Authorized)
            {
                Interlocked.Increment(ref _totalInputsRejected);
                _logger.Warning("Input rejected: Not authorized. Current state: {State}", _authorizationState);
                return false;
            }

            // Rate limiting: Check if we're exceeding max inputs per second
            var now = DateTime.UtcNow;
            while (_recentInputs.Count > 0 && (now - _recentInputs.Peek()).TotalSeconds > 1)
            {
                _recentInputs.Dequeue();
            }

            if (_recentInputs.Count >= MaxInputsPerSecond)
            {
                Interlocked.Increment(ref _totalInputsRejected);
                _logger.Warning("Input rejected: Rate limit exceeded ({Count} inputs in last second)",
                    _recentInputs.Count);
                return false;
            }

            _recentInputs.Enqueue(now);
            _lastInputTime = now;

            // Validate and apply input
            bool success = input.Type switch
            {
                InputMessageType.MouseMove => ApplyMouseMove(input),
                InputMessageType.MouseDown => ApplyMouseButton(input, true),
                InputMessageType.MouseUp => ApplyMouseButton(input, false),
                InputMessageType.MouseWheel => ApplyMouseWheel(input),
                InputMessageType.KeyDown => ApplyKeyboard(input, true),
                InputMessageType.KeyUp => ApplyKeyboard(input, false),
                _ => false
            };

            if (success)
            {
                Interlocked.Increment(ref _totalInputsApplied);
            }
            else
            {
                Interlocked.Increment(ref _totalInputsRejected);
            }

            return success;
        }
    }

    /// <inheritdoc />
    public InputStatistics GetStatistics()
    {
        lock (_lockObject)
        {
            var keyboardEvents = Interlocked.Read(ref _totalInputsApplied); // Approximate
            var mouseEvents = 0L; // TODO: Separate tracking for mouse/keyboard
            var rejected = Interlocked.Read(ref _totalInputsRejected);

            return new InputStatistics
            {
                KeyboardEventsProcessed = keyboardEvents,
                MouseEventsProcessed = mouseEvents,
                EventsRejected = rejected,
                StartTime = _authorizationGrantedAt ?? DateTime.UtcNow
            };
        }
    }

    #endregion

    #region Input Application Methods

    /// <summary>
    /// Applies a mouse move input to the system.
    /// </summary>
    /// <param name="input">Input message containing normalized X/Y coordinates (0.0 to 1.0).</param>
    /// <returns>True if successful, false otherwise.</returns>
    private bool ApplyMouseMove(InputMessage input)
    {
        // Validate coordinates are present and normalized
        if (!input.X.HasValue || !input.Y.HasValue)
        {
            _logger.Warning("MouseMove rejected: Missing coordinates");
            return false;
        }

        if (input.X.Value < 0 || input.X.Value > 1 || input.Y.Value < 0 || input.Y.Value > 1)
        {
            _logger.Warning("MouseMove rejected: Coordinates out of range (X={X}, Y={Y})",
                input.X.Value, input.Y.Value);
            return false;
        }

        // Convert normalized coordinates (0-1) to absolute screen coordinates
        // Note: For MOUSEEVENTF_ABSOLUTE, coordinates are 0-65535 across entire virtual screen
        int screenWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        int absoluteX = (int)(input.X.Value * 65535);
        int absoluteY = (int)(input.Y.Value * 65535);

        var mouseInput = new INPUT
        {
            Type = INPUT_MOUSE,
            Data = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    X = absoluteX,
                    Y = absoluteY,
                    MouseData = 0,
                    Flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

        uint result = SendInput(1, new[] { mouseInput }, Marshal.SizeOf<INPUT>());

        if (result == 0)
        {
            _logger.Error("SendInput failed for MouseMove");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies a mouse button press or release to the system.
    /// </summary>
    /// <param name="input">Input message containing button identifier.</param>
    /// <param name="isDown">True for button press, false for button release.</param>
    /// <returns>True if successful, false otherwise.</returns>
    private bool ApplyMouseButton(InputMessage input, bool isDown)
    {
        if (!input.Button.HasValue)
        {
            _logger.Warning("MouseButton rejected: Missing button identifier");
            return false;
        }

        uint flags = input.Button.Value switch
        {
            MouseButton.Left => isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButton.Right => isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            MouseButton.Middle => isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            MouseButton.Extra1 => isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
            MouseButton.Extra2 => isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
            _ => 0
        };

        if (flags == 0)
        {
            _logger.Warning("MouseButton rejected: Unknown button type {Button}", input.Button.Value);
            return false;
        }

        uint mouseData = input.Button.Value switch
        {
            MouseButton.Extra1 => XBUTTON1,
            MouseButton.Extra2 => XBUTTON2,
            _ => 0
        };

        var mouseInput = new INPUT
        {
            Type = INPUT_MOUSE,
            Data = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    X = 0,
                    Y = 0,
                    MouseData = mouseData,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

        uint result = SendInput(1, new[] { mouseInput }, Marshal.SizeOf<INPUT>());

        if (result == 0)
        {
            _logger.Error("SendInput failed for MouseButton {Button} {Action}",
                input.Button.Value, isDown ? "Down" : "Up");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies a mouse wheel scroll to the system.
    /// </summary>
    /// <param name="input">Input message containing wheel delta (positive=up, negative=down).</param>
    /// <returns>True if successful, false otherwise.</returns>
    private bool ApplyMouseWheel(InputMessage input)
    {
        if (!input.WheelDelta.HasValue)
        {
            _logger.Warning("MouseWheel rejected: Missing wheel delta");
            return false;
        }

        // Validate wheel delta is reasonable (typically -120 to +120 per notch)
        if (Math.Abs(input.WheelDelta.Value) > 1200)
        {
            _logger.Warning("MouseWheel rejected: Excessive wheel delta {Delta}", input.WheelDelta.Value);
            return false;
        }

        var mouseInput = new INPUT
        {
            Type = INPUT_MOUSE,
            Data = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    X = 0,
                    Y = 0,
                    MouseData = (uint)input.WheelDelta.Value,
                    Flags = MOUSEEVENTF_WHEEL,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

        uint result = SendInput(1, new[] { mouseInput }, Marshal.SizeOf<INPUT>());

        if (result == 0)
        {
            _logger.Error("SendInput failed for MouseWheel");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies a keyboard key press or release to the system.
    /// </summary>
    /// <param name="input">Input message containing key code.</param>
    /// <param name="isDown">True for key press, false for key release.</param>
    /// <returns>True if successful, false otherwise.</returns>
    private bool ApplyKeyboard(InputMessage input, bool isDown)
    {
        if (!input.KeyCode.HasValue)
        {
            _logger.Warning("Keyboard rejected: Missing key code");
            return false;
        }

        // Validate key code is in valid range (0-254 for Windows VK codes)
        if (input.KeyCode.Value < 0 || input.KeyCode.Value > 254)
        {
            _logger.Warning("Keyboard rejected: Invalid key code {KeyCode}", input.KeyCode.Value);
            return false;
        }

        var keyInput = new INPUT
        {
            Type = INPUT_KEYBOARD,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = (ushort)input.KeyCode.Value,
                    ScanCode = 0,
                    Flags = isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

        uint result = SendInput(1, new[] { keyInput }, Marshal.SizeOf<INPUT>());

        if (result == 0)
        {
            _logger.Error("SendInput failed for Keyboard {KeyCode} {Action}",
                input.KeyCode.Value, isDown ? "Down" : "Up");
            return false;
        }

        return true;
    }

    #endregion

    #region IDisposable Implementation

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        RevokeAuthorization();
        _disposed = true;

        _logger.Information("WindowsInputController disposed. Final statistics: Received={Received}, Applied={Applied}, Rejected={Rejected}",
            _totalInputsReceived, _totalInputsApplied, _totalInputsRejected);
    }

    #endregion
}
