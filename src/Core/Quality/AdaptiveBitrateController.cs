using System.Diagnostics;

namespace DeskShare.Core.Quality;

/// <summary>
/// Adaptive bitrate controller that adjusts video quality based on network conditions.
/// Monitors packet loss, RTT, and bandwidth to optimize streaming quality.
/// </summary>
/// <remarks>
/// This controller implements a simple adaptive algorithm:
/// - Monitor network metrics (packet loss, RTT, bandwidth)
/// - Adjust quality level based on thresholds
/// - Provide smooth transitions between quality levels
/// - Prevent rapid quality switching (hysteresis)
///
/// Quality levels: Low (15fps/720p) → Medium (30fps/1080p) → High (60fps/1080p)
/// </remarks>
public sealed class AdaptiveBitrateController
{
    // Network quality thresholds
    private const double HighPacketLossThreshold = 5.0;      // 5% packet loss = bad
    private const double MediumPacketLossThreshold = 2.0;    // 2% packet loss = medium
    private const int HighRttThreshold = 200;                // 200ms RTT = bad
    private const int MediumRttThreshold = 100;              // 100ms RTT = medium

    // Hysteresis: minimum time between quality changes (prevents rapid switching)
    private readonly TimeSpan _minTimeBetweenChanges = TimeSpan.FromSeconds(5);
    private DateTime _lastQualityChange = DateTime.MinValue;

    // Current quality level
    private QualityLevel _currentQuality = QualityLevel.Medium;

    // Statistics history for smoothing decisions
    private readonly Queue<NetworkMetrics> _metricsHistory = new();
    private const int HistorySize = 10; // Keep last 10 measurements

    /// <summary>
    /// Current quality level.
    /// </summary>
    public QualityLevel CurrentQuality => _currentQuality;

    /// <summary>
    /// Event raised when quality level changes.
    /// </summary>
    public event EventHandler<QualityChangedEventArgs>? QualityChanged;

    /// <summary>
    /// Updates the controller with current network metrics.
    /// May trigger a quality change if conditions warrant it.
    /// </summary>
    /// <param name="metrics">Current network metrics (packet loss, RTT, etc.).</param>
    /// <returns>True if quality level changed.</returns>
    public bool UpdateMetrics(NetworkMetrics metrics)
    {
        if (metrics == null)
            throw new ArgumentNullException(nameof(metrics));

        // Add to history and maintain size limit
        _metricsHistory.Enqueue(metrics);
        if (_metricsHistory.Count > HistorySize)
        {
            _metricsHistory.Dequeue();
        }

        // Don't change quality too frequently (hysteresis)
        var timeSinceLastChange = DateTime.UtcNow - _lastQualityChange;
        if (timeSinceLastChange < _minTimeBetweenChanges)
        {
            return false;
        }

        // Calculate average metrics from history for stable decisions
        var avgPacketLoss = _metricsHistory.Average(m => m.PacketLossPercent);
        var avgRtt = _metricsHistory.Average(m => m.RttMilliseconds);

        // Determine target quality based on network conditions
        var targetQuality = DetermineTargetQuality(avgPacketLoss, avgRtt);

        // Change quality if needed
        if (targetQuality != _currentQuality)
        {
            var oldQuality = _currentQuality;
            _currentQuality = targetQuality;
            _lastQualityChange = DateTime.UtcNow;

            // Raise event
            QualityChanged?.Invoke(this, new QualityChangedEventArgs(oldQuality, targetQuality));

            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines the target quality level based on network metrics.
    /// Uses conservative approach: prefer lower quality for stability.
    /// </summary>
    /// <param name="packetLoss">Average packet loss percentage.</param>
    /// <param name="rtt">Average round-trip time in milliseconds.</param>
    /// <returns>Recommended quality level.</returns>
    private QualityLevel DetermineTargetQuality(double packetLoss, double rtt)
    {
        // Bad conditions: high packet loss OR high RTT → Low quality
        if (packetLoss > HighPacketLossThreshold || rtt > HighRttThreshold)
        {
            return QualityLevel.Low;
        }

        // Medium conditions: moderate packet loss OR moderate RTT → Medium quality
        if (packetLoss > MediumPacketLossThreshold || rtt > MediumRttThreshold)
        {
            return QualityLevel.Medium;
        }

        // Good conditions: low packet loss AND low RTT → High quality
        return QualityLevel.High;
    }

    /// <summary>
    /// Manually sets the quality level (bypasses automatic control).
    /// Useful for user override or testing.
    /// </summary>
    /// <param name="quality">Desired quality level.</param>
    public void SetManualQuality(QualityLevel quality)
    {
        if (_currentQuality == quality)
            return;

        var oldQuality = _currentQuality;
        _currentQuality = quality;
        _lastQualityChange = DateTime.UtcNow;

        QualityChanged?.Invoke(this, new QualityChangedEventArgs(oldQuality, quality));
    }

    /// <summary>
    /// Gets recommended resolution and frame rate for current quality level.
    /// </summary>
    /// <returns>Quality settings with resolution and FPS.</returns>
    public QualitySettings GetCurrentSettings()
    {
        return _currentQuality switch
        {
            QualityLevel.Low => new QualitySettings
            {
                Width = 1280,
                Height = 720,
                FrameRate = 15,
                Description = "Low quality - 720p @ 15fps (poor network)"
            },
            QualityLevel.Medium => new QualitySettings
            {
                Width = 1920,
                Height = 1080,
                FrameRate = 30,
                Description = "Medium quality - 1080p @ 30fps (normal network)"
            },
            QualityLevel.High => new QualitySettings
            {
                Width = 1920,
                Height = 1080,
                FrameRate = 60,
                Description = "High quality - 1080p @ 60fps (excellent network)"
            },
            _ => throw new InvalidOperationException($"Unknown quality level: {_currentQuality}")
        };
    }

    /// <summary>
    /// Resets the controller to default state (Medium quality).
    /// Clears metrics history.
    /// </summary>
    public void Reset()
    {
        _currentQuality = QualityLevel.Medium;
        _metricsHistory.Clear();
        _lastQualityChange = DateTime.MinValue;
    }
}

/// <summary>
/// Network metrics used for quality decisions.
/// These should be collected from WebRTC statistics.
/// </summary>
public sealed class NetworkMetrics
{
    /// <summary>
    /// Packet loss percentage (0-100).
    /// Higher values indicate poor network conditions.
    /// </summary>
    public double PacketLossPercent { get; set; }

    /// <summary>
    /// Round-trip time in milliseconds.
    /// Higher values indicate higher latency.
    /// </summary>
    public double RttMilliseconds { get; set; }

    /// <summary>
    /// Current bandwidth in kilobits per second.
    /// Optional - can be used for more advanced decisions.
    /// </summary>
    public long BandwidthKbps { get; set; }

    /// <summary>
    /// Timestamp when metrics were collected.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Quality level enumeration.
/// Defines the three quality tiers available.
/// </summary>
public enum QualityLevel
{
    /// <summary>
    /// Low quality: 720p @ 15fps.
    /// Used when network conditions are poor.
    /// </summary>
    Low,

    /// <summary>
    /// Medium quality: 1080p @ 30fps.
    /// Default quality for normal network conditions.
    /// </summary>
    Medium,

    /// <summary>
    /// High quality: 1080p @ 60fps.
    /// Used when network conditions are excellent.
    /// </summary>
    High
}

/// <summary>
/// Quality settings with resolution and frame rate.
/// Returned by GetCurrentSettings() to apply to video pipeline.
/// </summary>
public sealed class QualitySettings
{
    /// <summary>
    /// Video width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Video height in pixels.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Target frame rate (frames per second).
    /// </summary>
    public int FrameRate { get; set; }

    /// <summary>
    /// Human-readable description of this quality level.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Event args for quality change events.
/// Contains both old and new quality levels.
/// </summary>
public sealed class QualityChangedEventArgs : EventArgs
{
    /// <summary>
    /// Previous quality level before the change.
    /// </summary>
    public QualityLevel OldQuality { get; }

    /// <summary>
    /// New quality level after the change.
    /// </summary>
    public QualityLevel NewQuality { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityChangedEventArgs"/> class.
    /// </summary>
    public QualityChangedEventArgs(QualityLevel oldQuality, QualityLevel newQuality)
    {
        OldQuality = oldQuality;
        NewQuality = newQuality;
    }
}
