namespace DeskShare.Core.Signaling;

/// <summary>
/// Wait times between signaling reconnect attempts: short at first (a blip), longer later
/// (a server deployment takes about 75 s), and capped so a long outage does not hammer the server.
/// </summary>
public static class ReconnectBackoff
{
    private static readonly TimeSpan[] Schedule =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    };

    /// <summary>Delay before attempt number <paramref name="attempt"/> (0-based); the last step repeats forever.</summary>
    public static TimeSpan GetDelay(int attempt) =>
        Schedule[Math.Clamp(attempt, 0, Schedule.Length - 1)];
}
