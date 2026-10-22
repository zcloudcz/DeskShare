using DeskShare.Core.Interfaces;

namespace DeskShare.Core.WebRTC;

/// <summary>
/// On/off state of the privacy screen for one host. The viewer asks for it ("privacy_screen"); everything that ends
/// the viewer's session (channel closed or replaced, authorization revoked, service stop) switches it off again,
/// so a black monitor never outlives the person who asked for it.
/// </summary>
/// <remarks>
/// Epoch: a request is checked against the session policy first and applied later. If the session ends in between
/// (revoke, close), the request must not switch the overlay on afterwards. So the request captures <see cref="Epoch"/>
/// when it arrives, every <see cref="Disable"/> bumps it, and <see cref="Set"/> ignores a request whose epoch is stale.
/// </remarks>
public sealed class PrivacyScreenController : IDisposable
{
    private readonly IPrivacyOverlay _overlay;
    private readonly bool _blockInput;
    private readonly object _gate = new();
    private long _epoch;

    public PrivacyScreenController(IPrivacyOverlay overlay, bool blockInput)
    {
        _overlay = overlay;
        _blockInput = blockInput;
        _overlay.Failed += OnOverlayFailed;
    }

    public bool Enabled { get; private set; }

    /// <summary>Session generation; capture it when a request arrives and pass it to <see cref="Set"/>.</summary>
    public long Epoch => Volatile.Read(ref _epoch);

    /// <summary>Raised after every change (and after a refused request) with the state the viewer should show.</summary>
    public event Action<bool, string?>? StateChanged;

    /// <summary>
    /// Handles the viewer's request. A failed show leaves the state off and reports why. Ignored when the session
    /// ended since <paramref name="epoch"/> was captured, or when <paramref name="stillAllowed"/> (the capability
    /// re-check, run under the lock) says no.
    /// </summary>
    public void Set(bool enable, long epoch, Func<bool>? stillAllowed = null)
    {
        bool enabled;
        string? reason = null;
        lock (_gate)
        {
            if (epoch != _epoch || (stillAllowed != null && !stillAllowed()))
                return;

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

    /// <summary>Automatic off. Ends the session generation, so pending requests become stale.</summary>
    public void Disable(string reason)
    {
        lock (_gate)
        {
            _epoch++;
            if (!Enabled)
                return;
            _overlay.Hide();
            Enabled = false;
        }
        StateChanged?.Invoke(false, reason);
    }

    // The overlay removed itself (and released input). Mirror that and tell the viewer.
    private void OnOverlayFailed(string reason)
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
        _overlay.Failed -= OnOverlayFailed;
        _overlay.Dispose();
    }
}
