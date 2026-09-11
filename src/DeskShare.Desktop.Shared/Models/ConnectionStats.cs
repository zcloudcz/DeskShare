namespace DeskShare.Desktop.Shared.Models;

/// <summary>
/// Holds real-time connection statistics displayed in the ProjectionWindow toolbar.
/// Shared between WPF and Avalonia desktop clients.
/// </summary>
public class ConnectionStats
{
    /// <summary>
    /// Current frames per second being received.
    /// </summary>
    public int Fps { get; set; }

    /// <summary>
    /// Round-trip latency in milliseconds.
    /// </summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// Current bitrate in kilobits per second.
    /// </summary>
    public int BitrateKbps { get; set; }

    /// <summary>
    /// Percentage of packets lost during transmission.
    /// </summary>
    public double PacketLoss { get; set; }

    /// <summary>
    /// Current video resolution string (e.g., "1920x1080").
    /// </summary>
    public string Resolution { get; set; } = string.Empty;
}
