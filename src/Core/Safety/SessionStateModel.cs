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
    private bool _recording;
    private SessionBannerState _state;

    public SessionBannerState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>
    /// True while the connected viewer says it records the session. Informational only (the host cannot detect or prevent
    /// recording); it makes the banner honest for viewers that tell the truth. Never true without a viewer.
    /// </summary>
    public bool Recording
    {
        get { lock (_lock) return _shownRecording; }
    }

    private bool _shownRecording;

    /// <summary>Raised (outside the lock) every time <see cref="State"/> or <see cref="Recording"/> changes.</summary>
    public event Action<SessionBannerState>? Changed;

    public void ViewerStarted() => Update(() => _viewer = true);

    public void ViewerEnded() => Update(() => _viewer = false);

    /// <summary>The input controller's authorization changed. Without a viewer this alone shows nothing (control is "armed", not in use).</summary>
    public void ControlChanged(bool authorized) => Update(() => _control = authorized);

    /// <summary>The viewer started or stopped recording. Without a viewer this shows nothing.</summary>
    public void RecordingChanged(bool recording) => Update(() => _recording = recording);

    private void Update(Action change)
    {
        SessionBannerState next;
        lock (_lock)
        {
            change();
            if (!_viewer) _recording = false; // a viewer that left no longer records
            next = !_viewer ? SessionBannerState.None : _control ? SessionBannerState.Controlling : SessionBannerState.Viewing;
            if (next == _state && _recording == _shownRecording) return;
            _state = next;
            _shownRecording = _recording;
        }
        Changed?.Invoke(next);
    }
}
