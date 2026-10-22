using System.Runtime.Versioning;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// Wayland input controller: injects pointer and keyboard events through the RemoteDesktop desktop portal,
/// using the session shared with <see cref="PipeWireScreenCapturer"/> (see <see cref="WaylandPortalSession"/>).
/// </summary>
/// <remarks>
/// Authorization works like <see cref="XTestInputController"/>: the host's consent callback first, then the
/// portal session. Portals without RemoteDesktop (xdg-desktop-portal-wlr) cannot inject input; authorization is
/// then denied with a log line instead of silently dropping every event.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class WaylandPortalInputController : IInputController
{
    // evdev button codes (BTN_LEFT, BTN_RIGHT, BTN_MIDDLE, BTN_SIDE, BTN_EXTRA).
    private const int BtnLeft = 0x110, BtnRight = 0x111, BtnMiddle = 0x112, BtnSide = 0x113, BtnExtra = 0x114;
    private const int MaxEventsPerSecond = 120;

    private readonly ILogger _logger;
    private readonly Queue<DateTime> _eventTimestamps = new();
    private WaylandPortalSession? _session;
    private InputAuthorizationState _authorizationState = InputAuthorizationState.NotAuthorized;
    private DateTime _authorizationGrantedAt;
    private int _keyboardEventsProcessed;
    private int _mouseEventsProcessed;
    private int _eventsRejected;
    private bool _disposed;

    public event EventHandler<InputAuthorizationState>? AuthorizationStateChanged;

    /// <inheritdoc />
    public Func<string, bool>? AuthorizationRequested { get; set; }

    public bool IsEnabled => _authorizationState == InputAuthorizationState.Authorized;

    public InputAuthorizationState AuthorizationState => _authorizationState;

    public WaylandPortalInputController(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<bool> RequestAuthorizationAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentNullException(nameof(clientId));

        if (_authorizationState == InputAuthorizationState.Authorized)
            return Task.FromResult(true);

        ChangeAuthorizationState(InputAuthorizationState.Pending);
        if (!(AuthorizationRequested?.Invoke(clientId) ?? false))
        {
            _logger.Information("[WaylandPortalInput] Remote control denied for client: {ClientId}", clientId);
            ChangeAuthorizationState(InputAuthorizationState.Denied);
            return Task.FromResult(false);
        }

        try
        {
            _session ??= WaylandPortalSession.Acquire();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[WaylandPortalInput] Could not open the desktop portal session");
            ChangeAuthorizationState(InputAuthorizationState.Denied);
            return Task.FromResult(false);
        }

        if (!_session.CanInjectInput)
        {
            _logger.Warning("[WaylandPortalInput] This desktop's portal offers no RemoteDesktop (pointer/keyboard) access; remote control is unavailable (screen sharing still works)");
            ChangeAuthorizationState(InputAuthorizationState.Denied);
            return Task.FromResult(false);
        }

        _authorizationGrantedAt = DateTime.UtcNow;
        ChangeAuthorizationState(InputAuthorizationState.Authorized);
        _logger.Information("[WaylandPortalInput] Authorization granted to client: {ClientId}", clientId);
        return Task.FromResult(true);
    }

    public void RevokeAuthorization()
    {
        if (_authorizationState is InputAuthorizationState.Authorized or InputAuthorizationState.Pending)
        {
            ChangeAuthorizationState(InputAuthorizationState.Revoked);
            _logger.Information("[WaylandPortalInput] Authorization revoked");
        }
    }

    public bool ApplyInput(InputMessage input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));
        if (_disposed)
            throw new ObjectDisposedException(nameof(WaylandPortalInputController));

        if (!IsEnabled || !CheckRateLimit())
        {
            _eventsRejected++;
            return false;
        }

        try
        {
            bool success = input.Type switch
            {
                InputMessageType.MouseMove => SendMove(input),
                InputMessageType.MouseDown => SendButton(input, pressed: true),
                InputMessageType.MouseUp => SendButton(input, pressed: false),
                InputMessageType.MouseWheel => SendWheel(input),
                InputMessageType.KeyDown => SendKey(input, pressed: true),
                InputMessageType.KeyUp => SendKey(input, pressed: false),
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

    private bool SendMove(InputMessage input)
    {
        if (input.X is not { } x || input.Y is not { } y || x < 0 || x > 1 || y < 0 || y > 1)
            return false;
        if (_session!.StreamWidth <= 0 || _session.StreamHeight <= 0)
        {
            _logger.Warning("[WaylandPortalInput] The portal reported no stream size; cannot place the pointer");
            return false;
        }

        var (sx, sy) = ToStreamCoordinates(x, y, _session.StreamWidth, _session.StreamHeight);
        using var writer = _session.BeginNotify("NotifyPointerMotionAbsolute", "udd");
        writer.WriteUInt32(_session.NodeId);
        writer.WriteDouble(sx);
        writer.WriteDouble(sy);
        return _session.Send(writer.CreateMessage());
    }

    private bool SendButton(InputMessage input, bool pressed)
    {
        if (input.Button is not { } button || ToEvdevButton(button) is not { } code)
            return false;

        using var writer = _session!.BeginNotify("NotifyPointerButton", "iu");
        writer.WriteInt32(code);
        writer.WriteUInt32(pressed ? 1u : 0u);
        return _session.Send(writer.CreateMessage());
    }

    private bool SendWheel(InputMessage input)
    {
        if (input.WheelDelta is not { } delta || Math.Abs(delta) > 1000)
            return false;
        var steps = ToScrollSteps(delta);
        if (steps == 0)
            return true; // less than one notch: nothing to scroll, like the XTest controller

        using var writer = _session!.BeginNotify("NotifyPointerAxisDiscrete", "ui");
        writer.WriteUInt32(0); // vertical axis
        writer.WriteInt32(steps);
        return _session.Send(writer.CreateMessage());
    }

    private bool SendKey(InputMessage input, bool pressed)
    {
        if (input.KeyCode is not { } vk || EvdevKeyMap.FromVirtualKey(vk) is not { } code)
        {
            _logger.Debug("[WaylandPortalInput] No evdev mapping for key {KeyCode}", input.KeyCode);
            return false;
        }

        using var writer = _session!.BeginNotify("NotifyKeyboardKeycode", "iu");
        writer.WriteInt32(code);
        writer.WriteUInt32(pressed ? 1u : 0u);
        return _session.Send(writer.CreateMessage());
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

    private void ChangeAuthorizationState(InputAuthorizationState newState)
    {
        if (_authorizationState != newState)
        {
            _authorizationState = newState;
            AuthorizationStateChanged?.Invoke(this, newState);
        }
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
        if (_disposed)
            return;
        RevokeAuthorization();
        if (_session != null)
        {
            _session = null;
            WaylandPortalSession.Release();
        }
        _disposed = true;
    }
}
