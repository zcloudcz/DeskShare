namespace DeskShare.Core.Safety;

/// <summary>What the on-screen session banner has to say.</summary>
public enum SessionBannerState
{
    /// <summary>Nobody is connected: no banner.</summary>
    None,
    /// <summary>A verified viewer sees the screen.</summary>
    Viewing,
    /// <summary>A verified viewer sees the screen and may use the mouse and keyboard.</summary>
    Controlling,
}

/// <summary>
/// Two facts decide the banner: is a (verified) viewer connected, and is control authorized. Kept separate from the
/// Win32 window so the rule is testable without a display.
/// </summary>
public sealed class SessionStateModel
{
    private readonly object _lock = new();
    private bool _viewer;
    private bool _control;
    private SessionBannerState _state;

    public SessionBannerState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>Raised (outside the lock) every time <see cref="State"/> changes.</summary>
    public event Action<SessionBannerState>? Changed;

    public void ViewerStarted() => Update(() => _viewer = true);

    public void ViewerEnded() => Update(() => _viewer = false);

    /// <summary>The input controller's authorization changed. Without a viewer this alone shows nothing (control is "armed", not in use).</summary>
    public void ControlChanged(bool authorized) => Update(() => _control = authorized);

    private void Update(Action change)
    {
        SessionBannerState next;
        lock (_lock)
        {
            change();
            next = !_viewer ? SessionBannerState.None : _control ? SessionBannerState.Controlling : SessionBannerState.Viewing;
            if (next == _state) return;
            _state = next;
        }
        Changed?.Invoke(next);
    }
}
