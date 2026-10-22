using System.Diagnostics;
using System.Text.Json;

namespace DeskShare.Core.Displays;

/// <summary>What happened to a "switch_display" request.</summary>
public enum DisplaySwitchResult
{
    /// <summary>The new display is being shared.</summary>
    Switched,
    /// <summary>The requested display is already the shared one.</summary>
    Unchanged,
    /// <summary>The index is not in the display list.</summary>
    InvalidIndex,
    /// <summary>Too soon after the previous switch, or one is still in progress.</summary>
    RateLimited,
    /// <summary>The capturer for the new display could not start; the old one keeps running.</summary>
    Failed
}

/// <summary>
/// Which display the host shares and whether a viewer may change it right now. Knows nothing about capturing:
/// the actual switch is the <c>apply</c> callback (the host wires it to the capture pipeline). Capability checks
/// (Control) are done before a request gets here, see <see cref="WebRTC.DataChannelPolicy"/>.
/// </summary>
public sealed class DisplaySwitchService
{
    /// <summary>A viewer can switch at most once per this interval. Re-creating a capturer is heavy and flickers.</summary>
    public const int MinSwitchIntervalMs = 1000;

    private readonly Func<DisplayInfo, Task<bool>> _apply;
    private readonly Func<long> _nowMs;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private long _lastAttemptMs;
    private bool _hasAttempted;
    private volatile int _current;

    /// <summary>The displays that can be shared; indexes into this list are what the viewer sends.</summary>
    public IReadOnlyList<DisplayInfo> Displays { get; }

    /// <summary>Index of the display being shared now.</summary>
    public int Current => _current;

    /// <param name="displays">All displays, in list order (<see cref="DisplayInfo.Index"/> equals the position).</param>
    /// <param name="current">The display shared at the start.</param>
    /// <param name="apply">Starts sharing the given display; true when it worked.</param>
    /// <param name="nowMs">Monotonic clock in milliseconds; tests inject their own.</param>
    public DisplaySwitchService(IReadOnlyList<DisplayInfo> displays, int current, Func<DisplayInfo, Task<bool>> apply, Func<long>? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(displays);
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        Displays = displays;
        _current = current >= 0 && current < displays.Count ? current : 0;
    }

    /// <summary>The "displays" message for the viewer. It is also the answer to every switch request, so the viewer's list stays right even when a switch is refused.</summary>
    public string BuildMessage() => JsonSerializer.Serialize(new
    {
        type = "displays",
        items = Displays.Select(d => new { index = d.Index, name = d.Name, width = d.Width, height = d.Height, primary = d.Primary }),
        current = _current
    });

    /// <summary>Reads <c>index</c> from a "switch_display" message; false when it is missing or not an integer.</summary>
    public static bool TryReadIndex(JsonElement message, out int index)
    {
        index = -1;
        return message.TryGetProperty("index", out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt32(out index);
    }

    /// <summary>Validates, rate-limits and performs a switch.</summary>
    public async Task<DisplaySwitchResult> SwitchAsync(int index)
    {
        if (index < 0 || index >= Displays.Count)
            return DisplaySwitchResult.InvalidIndex;

        // A second request during a running switch is dropped, not queued: the viewer gets the state afterwards anyway.
        if (!await _busy.WaitAsync(0))
            return DisplaySwitchResult.RateLimited;

        try
        {
            if (index == _current)
                return DisplaySwitchResult.Unchanged;

            var now = _nowMs();
            if (_hasAttempted && now - _lastAttemptMs < MinSwitchIntervalMs)
                return DisplaySwitchResult.RateLimited;

            // Failed attempts count too, otherwise a viewer could hammer a broken display.
            (_hasAttempted, _lastAttemptMs) = (true, now);

            if (!await _apply(Displays[index]))
                return DisplaySwitchResult.Failed;

            _current = index;
            return DisplaySwitchResult.Switched;
        }
        finally
        {
            _busy.Release();
        }
    }
}
