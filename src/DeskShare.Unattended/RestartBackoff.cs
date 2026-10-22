namespace DeskShare.Unattended;

/// <summary>Exponential delay between agent restarts; resets once the agent stayed up long enough to be considered healthy.</summary>
public sealed class RestartBackoff
{
    private readonly TimeSpan _initial, _max, _healthyAfter;
    private int _attempt;

    public RestartBackoff(TimeSpan? initial = null, TimeSpan? max = null, TimeSpan? healthyAfter = null)
    {
        _initial = initial ?? TimeSpan.FromSeconds(1);
        _max = max ?? TimeSpan.FromSeconds(60);
        _healthyAfter = healthyAfter ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Delay before the next start after an agent that ran for <paramref name="ranFor"/> exited.</summary>
    public TimeSpan NextDelay(TimeSpan ranFor)
    {
        if (ranFor >= _healthyAfter)
            _attempt = 0;
        var factor = Math.Pow(2, Math.Min(_attempt, 20)); // capped exponent, far above any sane max
        _attempt++;
        return TimeSpan.FromTicks((long)Math.Min(_initial.Ticks * factor, _max.Ticks));
    }

    /// <summary>Deliberate restart (session switch): forget past failures.</summary>
    public void Reset() => _attempt = 0;
}
