using System.Runtime.Versioning;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// What <see cref="WaylandPortalInputController"/> needs from a RemoteDesktop portal session. Implemented by
/// <see cref="WaylandPortalSession"/>; an interface so the controller's authorization logic can be tested
/// without D-Bus.
/// </summary>
internal interface IRemoteDesktopPortal
{
    bool CanInjectInput { get; }
    bool IsClosed { get; }
    int StreamWidth { get; }
    int StreamHeight { get; }

    /// <summary>Absolute pointer position in the stream's logical coordinates.</summary>
    bool NotifyPointerMotionAbsolute(double x, double y);
    /// <summary>evdev button code (BTN_*), pressed or released.</summary>
    bool NotifyPointerButton(int button, bool pressed);
    /// <summary>Vertical wheel steps, positive = down.</summary>
    bool NotifyPointerAxisDiscrete(int steps);
    /// <summary>evdev key code (KEY_*), pressed or released.</summary>
    bool NotifyKeyboardKeycode(int keycode, bool pressed);
}

/// <summary>
/// Wayland input controller: injects pointer and keyboard events through the RemoteDesktop desktop portal,
/// using the session shared with <see cref="PipeWireScreenCapturer"/> (see <see cref="WaylandPortalSession"/>).
/// </summary>
/// <remarks>
/// Authorization works like <see cref="XTestInputController"/>: the host's consent callback first, then the
/// portal session. Portals without RemoteDesktop (xdg-desktop-portal-wlr) cannot inject input; authorization is
/// then denied with a log line instead of silently dropping every event.
///
/// Opening the portal session can block for as long as its consent dialog is open. Each authorization attempt
/// therefore gets a number; revoke, cancellation and dispose start a new number, and an attempt that finishes
/// after that publishes nothing and gives its session reference back.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class WaylandPortalInputController : IInputController
{
    // evdev button codes (BTN_LEFT, BTN_RIGHT, BTN_MIDDLE, BTN_SIDE, BTN_EXTRA).
    private const int BtnLeft = 0x110, BtnRight = 0x111, BtnMiddle = 0x112, BtnSide = 0x113, BtnExtra = 0x114;
    private const int MaxEventsPerSecond = 120;

    private readonly ILogger _logger;
    private readonly Func<IRemoteDesktopPortal> _acquire;
    private readonly Action<IRemoteDesktopPortal> _release;
    private readonly Queue<DateTime> _eventTimestamps = new();
    private readonly object _gate = new();
    private volatile IRemoteDesktopPortal? _session; // written under _gate
    private int _attempt;                            // guarded by _gate
    private volatile InputAuthorizationState _authorizationState = InputAuthorizationState.NotAuthorized;
    private DateTime _authorizationGrantedAt;
    private int _keyboardEventsProcessed;
    private int _mouseEventsProcessed;
    private int _eventsRejected;
    private bool _disposed; // guarded by _gate

    public event EventHandler<InputAuthorizationState>? AuthorizationStateChanged;

    /// <inheritdoc />
    public Func<string, bool>? AuthorizationRequested { get; set; }

    public bool IsEnabled => _authorizationState == InputAuthorizationState.Authorized;

    public InputAuthorizationState AuthorizationState => _authorizationState;

    public WaylandPortalInputController(ILogger logger)
        : this(logger, WaylandPortalSession.Acquire, s => WaylandPortalSession.Release((WaylandPortalSession)s))
    {
    }

    /// <param name="acquire">Takes a reference on the shared portal session (may block on the consent dialog).</param>
    /// <param name="release">Gives a reference taken by <paramref name="acquire"/> back.</param>
    internal WaylandPortalInputController(ILogger logger, Func<IRemoteDesktopPortal> acquire, Action<IRemoteDesktopPortal> release)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _acquire = acquire;
        _release = release;
    }

    public async Task<bool> RequestAuthorizationAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentNullException(nameof(clientId));

        int attempt;
        IRemoteDesktopPortal? held;
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (_authorizationState == InputAuthorizationState.Authorized)
                return true;
            attempt = ++_attempt;
            held = _session;
        }

        ChangeAuthorizationState(InputAuthorizationState.Pending, attempt);
        if (!(AuthorizationRequested?.Invoke(clientId) ?? false))
        {
            _logger.Information("[WaylandPortalInput] Remote control denied for client: {ClientId}", clientId);
            ChangeAuthorizationState(InputAuthorizationState.Denied, attempt);
            return false;
        }

        IRemoteDesktopPortal session;
        if (held != null && !held.IsClosed)
        {
            session = held;
        }
        else
        {
            // Off the caller's thread so the token can abandon the wait; the acquire itself cannot be interrupted,
            // so an abandoned one hands its reference back when it finishes.
            var acquiring = Task.Run(_acquire);
            try
            {
                session = await acquiring.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _ = acquiring.ContinueWith(t => _release(t.Result), TaskContinuationOptions.OnlyOnRanToCompletion);
                ChangeAuthorizationState(InputAuthorizationState.Denied, attempt); // unless a revoke came first
                Invalidate(attempt);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[WaylandPortalInput] Could not open the desktop portal session");
                ChangeAuthorizationState(InputAuthorizationState.Denied, attempt);
                return false;
            }

            IRemoteDesktopPortal? toRelease = null;
            lock (_gate)
            {
                if (attempt != _attempt || _disposed)
                {
                    toRelease = session; // revoked, cancelled or disposed while the dialog was open
                }
                else
                {
                    toRelease = _session; // a closed session we held before
                    _session = session;
                }
            }
            if (toRelease != null) _release(toRelease);
            if (ReferenceEquals(toRelease, session))
            {
                _logger.Information("[WaylandPortalInput] Authorization was revoked while the portal was opening; ignoring its result");
                return false;
            }
        }

        if (!session.CanInjectInput)
        {
            _logger.Warning("[WaylandPortalInput] This desktop's portal offers no RemoteDesktop (pointer/keyboard) access; remote control is unavailable (screen sharing still works)");
            ChangeAuthorizationState(InputAuthorizationState.Denied, attempt);
            return false;
        }

        if (!ChangeAuthorizationState(InputAuthorizationState.Authorized, attempt))
            return false;
        _authorizationGrantedAt = DateTime.UtcNow;
        _logger.Information("[WaylandPortalInput] Authorization granted to client: {ClientId}", clientId);
        return true;
    }

    public void RevokeAuthorization()
    {
        Invalidate(attempt: null);
        if (_authorizationState is InputAuthorizationState.Authorized or InputAuthorizationState.Pending)
        {
            ChangeAuthorizationState(InputAuthorizationState.Revoked, attempt: null);
            _logger.Information("[WaylandPortalInput] Authorization revoked");
        }
    }

    /// <summary>Makes every running attempt stale (only <paramref name="attempt"/> when given).</summary>
    private void Invalidate(int? attempt)
    {
        lock (_gate)
        {
            if (attempt == null || attempt == _attempt) _attempt++;
        }
    }

    public bool ApplyInput(InputMessage input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));
        if (_disposed)
            throw new ObjectDisposedException(nameof(WaylandPortalInputController));

        var session = _session;
        if (!IsEnabled || session == null || !CheckRateLimit())
        {
            _eventsRejected++;
            return false;
        }

        try
        {
            bool success = input.Type switch
            {
                InputMessageType.MouseMove => SendMove(session, input),
                InputMessageType.MouseDown => SendButton(session, input, pressed: true),
                InputMessageType.MouseUp => SendButton(session, input, pressed: false),
                InputMessageType.MouseWheel => SendWheel(session, input),
                InputMessageType.KeyDown => SendKey(session, input, pressed: true),
                InputMessageType.KeyUp => SendKey(session, input, pressed: false),
                _ => false
            };

            if (!success) _eventsRejected++;
            else if (input.Type is InputMessageType.KeyDown or InputMessageType.KeyUp) _keyboardEventsProcessed++;
            else _mouseEventsProcessed++;
            return success;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[WaylandPortalInput] Failed to apply input: {Type}", input.Type);
            _eventsRejected++;
            return false;
        }
    }

    /// <summary>
    /// Normalized viewer coordinates (0..1) to the stream's logical coordinate space that
    /// NotifyPointerMotionAbsolute uses; 1.0 is clamped onto the last pixel so it stays inside the stream.
    /// </summary>
    internal static (double X, double Y) ToStreamCoordinates(double x, double y, int width, int height) =>
        (Math.Min(x * width, width - 1), Math.Min(y * height, height - 1));

    /// <summary>Wheel delta (+120 per notch up, Windows convention) to discrete portal steps (positive = down).</summary>
    internal static int ToScrollSteps(int wheelDelta) => -wheelDelta / 120;

    internal static int? ToEvdevButton(MouseButton button) => button switch
    {
        MouseButton.Left => BtnLeft,
        MouseButton.Right => BtnRight,
        MouseButton.Middle => BtnMiddle,
        MouseButton.Extra1 => BtnSide,
        MouseButton.Extra2 => BtnExtra,
        _ => null
    };

    private bool SendMove(IRemoteDesktopPortal session, InputMessage input)
    {
        if (input.X is not { } x || input.Y is not { } y || x < 0 || x > 1 || y < 0 || y > 1)
            return false;
        if (session.StreamWidth <= 0 || session.StreamHeight <= 0)
        {
            _logger.Warning("[WaylandPortalInput] The portal reported no stream size; cannot place the pointer");
            return false;
        }

        var (sx, sy) = ToStreamCoordinates(x, y, session.StreamWidth, session.StreamHeight);
        return session.NotifyPointerMotionAbsolute(sx, sy);
    }

    private static bool SendButton(IRemoteDesktopPortal session, InputMessage input, bool pressed) =>
        input.Button is { } button && ToEvdevButton(button) is { } code && session.NotifyPointerButton(code, pressed);

    private static bool SendWheel(IRemoteDesktopPortal session, InputMessage input)
    {
        if (input.WheelDelta is not { } delta || Math.Abs(delta) > 1000)
            return false;
        var steps = ToScrollSteps(delta);
        // Less than one notch: nothing to scroll, like the XTest controller.
        return steps == 0 || session.NotifyPointerAxisDiscrete(steps);
    }

    private bool SendKey(IRemoteDesktopPortal session, InputMessage input, bool pressed)
    {
        if (input.KeyCode is not { } vk || EvdevKeyMap.FromVirtualKey(vk) is not { } code)
        {
            _logger.Debug("[WaylandPortalInput] No evdev mapping for key {KeyCode}", input.KeyCode);
            return false;
        }
        return session.NotifyKeyboardKeycode(code, pressed);
    }

    private bool CheckRateLimit()
    {
        var now = DateTime.UtcNow;
        while (_eventTimestamps.Count > 0 && _eventTimestamps.Peek() < now.AddSeconds(-1))
            _eventTimestamps.Dequeue();
        if (_eventTimestamps.Count >= MaxEventsPerSecond)
            return false;
        _eventTimestamps.Enqueue(now);
        return true;
    }

    /// <summary>
    /// Sets the state unless <paramref name="attempt"/> is given and no longer current (a stale attempt must not
    /// overwrite a revoke). Returns whether the state was applied.
    /// </summary>
    private bool ChangeAuthorizationState(InputAuthorizationState newState, int? attempt)
    {
        bool changed;
        lock (_gate)
        {
            if (attempt != null && (attempt != _attempt || _disposed))
                return false;
            changed = _authorizationState != newState;
            _authorizationState = newState;
        }
        if (changed)
            AuthorizationStateChanged?.Invoke(this, newState);
        return true;
    }

    public InputStatistics GetStatistics() => new()
    {
        KeyboardEventsProcessed = _keyboardEventsProcessed,
        MouseEventsProcessed = _mouseEventsProcessed,
        EventsRejected = _eventsRejected,
        StartTime = _authorizationGrantedAt
    };

    public void Dispose()
    {
        RevokeAuthorization();
        IRemoteDesktopPortal? session;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _attempt++;
            session = _session;
            _session = null;
        }
        if (session != null) _release(session);
    }
}
