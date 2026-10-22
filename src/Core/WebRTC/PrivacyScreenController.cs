using DeskShare.Core.Interfaces;

namespace DeskShare.Core.WebRTC;

/// <summary>
/// On/off state of the privacy screen for one host. The viewer asks for it ("privacy_screen"); everything that ends
/// the viewer's session (channel closed or replaced, authorization revoked, service stop) switches it off again,
/// so a black monitor never outlives the person who asked for it.
/// </summary>
public sealed class PrivacyScreenController : IDisposable
{
    private readonly IPrivacyOverlay _overlay;
    private readonly bool _blockInput;
    private readonly object _gate = new();

    public PrivacyScreenController(IPrivacyOverlay overlay, bool blockInput)
    {
        _overlay = overlay;
        _blockInput = blockInput;
    }

    public bool Enabled { get; private set; }

    /// <summary>Raised after every change (and after a refused request) with the state the viewer should show.</summary>
    public event Action<bool, string?>? StateChanged;

    /// <summary>Handles the viewer's request. A failed show leaves the state off and reports why.</summary>
    public void Set(bool enable)
    {
        bool enabled;
        string? reason = null;
        lock (_gate)
        {
            if (enable)
            {
                var result = _overlay.TryShow(_blockInput);
                Enabled = result.Shown;
                reason = result.Reason;
            }
            else
            {
                _overlay.Hide();
                Enabled = false;
            }
            enabled = Enabled;
        }
        StateChanged?.Invoke(enabled, reason);
    }

    /// <summary>Automatic off. Does nothing (and notifies nobody) when already off.</summary>
    public void Disable(string reason)
    {
        lock (_gate)
        {
            if (!Enabled)
                return;
            _overlay.Hide();
            Enabled = false;
        }
        StateChanged?.Invoke(false, reason);
    }

    public void Dispose()
    {
        Disable("stopped");
        _overlay.Dispose();
    }
}
