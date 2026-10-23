using System.Runtime.Versioning;
using DeskShare.Core.Safety;

namespace DeskShare.Core.Platforms.Windows;

/// <summary>
/// The bar at the top-centre of the primary monitor that tells the person at the computer that somebody is connected,
/// with [Stop control] and [End session]. Shared by the desktop app and the unattended agent.
/// </summary>
/// <remarks>
/// The window exists only while a viewer is connected. It is not excluded from capture on purpose (the viewer sees it
/// too), never takes focus, keeps itself topmost and hides on the lock screen / UAC desktop (see <see cref="Win32Panel"/>).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionBanner : IDisposable
{
    public const string ViewingText = "Remote session: someone is viewing this screen";
    public const string ControllingText = "Remote session: someone is controlling this computer";
    internal const int StopControlId = 1, EndSessionId = 2;

    private readonly Action _stopControl;
    private readonly Action _endSession;
    private readonly object _lock = new();
    private Win32Panel? _panel;
    private bool _disposed;
    private long _version; // newest model version shown (0 = callers that do not number their updates)

    /// <param name="stopControl">Revokes the viewer's control (the viewer keeps watching).</param>
    /// <param name="endSession">Disconnects the viewer.</param>
    public SessionBanner(Action stopControl, Action endSession)
    {
        _stopControl = stopControl;
        _endSession = endSession;
    }

    /// <summary>"(recording)" is appended when the viewer says it records. Informational: the host cannot detect recording.</summary>
    public static string TextFor(SessionBannerState state, bool recording = false) =>
        (state == SessionBannerState.Controlling ? ControllingText : ViewingText) + (recording ? " (recording)" : "");

    /// <summary>Handle of the banner window, zero while none is shown. For the smoke test.</summary>
    internal IntPtr WindowHandle
    {
        get { lock (_lock) return _panel?.Handle ?? IntPtr.Zero; }
    }

    /// <summary>Shows, updates or removes the banner. Safe from any thread.</summary>
    public void Update(SessionBannerState state, bool recording = false, long version = 0)
    {
        Win32Panel? toClose = null;
        lock (_lock)
        {
            if (_disposed) return;
            // Updates from different threads can arrive out of order; the model numbers its states, so an old one is dropped.
            if (version != 0)
            {
                if (version < _version) return;
                _version = version;
            }
            if (state == SessionBannerState.None)
            {
                toClose = _panel;
                _panel = null;
            }
            else if (_panel == null)
            {
                var panel = new Win32Panel(new PanelOptions(
                    "DeskShare remote session", TextFor(state, recording),
                    new[] { new PanelButton(StopControlId, "Stop control"), new PanelButton(EndSessionId, "End session") },
                    Banner: true, HideOnSecureDesktop: true, Width: 760, Height: 38,
                    Background: 0x0007C1FF /* amber (BGR) */, Foreground: 0x00000000));
                panel.ButtonClicked += OnButton;
                if (!panel.Start())
                {
                    panel.Dispose();
                    return; // no desktop access: the session itself must not fail because of the bar
                }
                panel.SetButtonVisible(StopControlId, state == SessionBannerState.Controlling);
                _panel = panel;
            }
            else
            {
                _panel.SetText(TextFor(state, recording));
                _panel.SetButtonVisible(StopControlId, state == SessionBannerState.Controlling);
            }
        }
        toClose?.Dispose(); // outside the lock: Dispose waits for the window thread
    }

    private void OnButton(int id)
    {
        if (id == StopControlId) _stopControl();
        else if (id == EndSessionId) _endSession();
    }

    public void Dispose()
    {
        Win32Panel? panel;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            panel = _panel;
            _panel = null;
        }
        panel?.Dispose();
    }
}
