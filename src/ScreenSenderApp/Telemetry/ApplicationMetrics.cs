using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DeskShare.ScreenSenderApp.Telemetry;

/// <summary>
/// Centralized metrics collection for RemoteDesktop.NET application.
/// Exposes OpenTelemetry metrics for monitoring capture pipeline, WebRTC connections,
/// and remote control operations.
/// </summary>
/// <remarks>
/// <para>
/// This class provides metrics instrumentation using System.Diagnostics.Metrics API,
/// which integrates with OpenTelemetry for export to Prometheus, Grafana, or other
/// monitoring systems.
/// </para>
/// <para>
/// Metrics are organized into three categories:
/// - Capture Pipeline: Frame rate, frame drops, encoding time
/// - WebRTC Connections: Active clients, bitrate, packet loss, latency
/// - Remote Control: Input events (mouse, keyboard), authorization, rate limiting
/// </para>
/// </remarks>
public sealed class ApplicationMetrics : IDisposable
{
    /// <summary>
    /// Application name for OpenTelemetry metrics namespace.
    /// </summary>
    public const string MeterName = "RemoteDesktop.ScreenSenderApp";

    /// <summary>
    /// Application version for metrics metadata.
    /// </summary>
    public const string Version = "1.0.0";

    private readonly Meter _meter;
    private bool _disposed;

    // Capture Pipeline Metrics
    private readonly Counter<long> _framesCapture;
    private readonly Counter<long> _framesDropped;
    private readonly Histogram<double> _captureTime;
    private readonly Histogram<double> _encodingTime;
    private readonly ObservableGauge<double> _captureFps;
    private double _currentFps;

    // WebRTC Connection Metrics
    private readonly ObservableGauge<int> _activeConnections;
    private readonly Counter<long> _connectionsTotal;
    private readonly Counter<long> _disconnectionsTotal;
    private readonly Histogram<long> _connectionDuration;
    private readonly Histogram<long> _bitrate;
    private readonly Histogram<double> _packetLoss;
    private readonly Histogram<long> _latencyMs;
    private int _currentActiveConnections;

    // Remote Control Metrics
    private readonly Counter<long> _mouseMovesTotal;
    private readonly Counter<long> _mouseClicksTotal;
    private readonly Counter<long> _keysTotal;
    private readonly Counter<long> _inputsRejected;
    private readonly Counter<long> _authorizationsRequested;
    private readonly Counter<long> _authorizationsGranted;
    private readonly Counter<long> _authorizationsDenied;
    private readonly ObservableGauge<int> _authorizedSessions;
    private int _currentAuthorizedSessions;

    // System Resource Metrics
    private readonly ObservableGauge<long> _memoryUsageBytes;
    private readonly ObservableGauge<double> _cpuUsagePercent;
    private readonly Process _currentProcess;
    private DateTime _lastCpuCheckTime;
    private TimeSpan _lastCpuTime;

    /// <summary>
    /// Initializes a new instance of the ApplicationMetrics class.
    /// Creates all metrics instruments for the application.
    /// </summary>
    public ApplicationMetrics()
    {
        _meter = new Meter(MeterName, Version);
        _currentProcess = Process.GetCurrentProcess();
        _lastCpuCheckTime = DateTime.UtcNow;
        _lastCpuTime = _currentProcess.TotalProcessorTime;

        // === Capture Pipeline Metrics ===

        _framesCapture = _meter.CreateCounter<long>(
            name: "capture.frames.total",
            unit: "frames",
            description: "Total number of frames captured from screen");

        _framesDropped = _meter.CreateCounter<long>(
            name: "capture.frames.dropped",
            unit: "frames",
            description: "Total number of frames dropped due to encoding backlog");

        _captureTime = _meter.CreateHistogram<double>(
            name: "capture.time.ms",
            unit: "ms",
            description: "Time taken to capture a single frame from screen");

        _encodingTime = _meter.CreateHistogram<double>(
            name: "encoding.time.ms",
            unit: "ms",
            description: "Time taken to encode a single frame to VP8");

        _captureFps = _meter.CreateObservableGauge<double>(
            name: "capture.fps",
            observeValue: () => _currentFps,
            unit: "fps",
            description: "Current frames per second being captured");

        // === WebRTC Connection Metrics ===

        _activeConnections = _meter.CreateObservableGauge<int>(
            name: "webrtc.connections.active",
            observeValue: () => _currentActiveConnections,
            unit: "connections",
            description: "Number of currently active WebRTC connections");

        _connectionsTotal = _meter.CreateCounter<long>(
            name: "webrtc.connections.total",
            unit: "connections",
            description: "Total number of WebRTC connections established");

        _disconnectionsTotal = _meter.CreateCounter<long>(
            name: "webrtc.disconnections.total",
            unit: "connections",
            description: "Total number of WebRTC disconnections");

        _connectionDuration = _meter.CreateHistogram<long>(
            name: "webrtc.connection.duration.seconds",
            unit: "s",
            description: "Duration of WebRTC connections in seconds");

        _bitrate = _meter.CreateHistogram<long>(
            name: "webrtc.bitrate.kbps",
            unit: "kbps",
            description: "WebRTC video bitrate in kilobits per second");

        _packetLoss = _meter.CreateHistogram<double>(
            name: "webrtc.packet_loss.percent",
            unit: "%",
            description: "WebRTC packet loss percentage");

        _latencyMs = _meter.CreateHistogram<long>(
            name: "webrtc.latency.ms",
            unit: "ms",
            description: "WebRTC round-trip time (RTT) in milliseconds");

        // === Remote Control Metrics ===

        _mouseMovesTotal = _meter.CreateCounter<long>(
            name: "remote_control.mouse.moves",
            unit: "events",
            description: "Total number of mouse move events processed");

        _mouseClicksTotal = _meter.CreateCounter<long>(
            name: "remote_control.mouse.clicks",
            unit: "events",
            description: "Total number of mouse click events processed");

        _keysTotal = _meter.CreateCounter<long>(
            name: "remote_control.keyboard.keys",
            unit: "events",
            description: "Total number of keyboard events processed");

        _inputsRejected = _meter.CreateCounter<long>(
            name: "remote_control.inputs.rejected",
            unit: "events",
            description: "Total number of input events rejected (rate limit or validation)");

        _authorizationsRequested = _meter.CreateCounter<long>(
            name: "remote_control.authorization.requested",
            unit: "requests",
            description: "Total number of authorization requests");

        _authorizationsGranted = _meter.CreateCounter<long>(
            name: "remote_control.authorization.granted",
            unit: "grants",
            description: "Total number of authorizations granted");

        _authorizationsDenied = _meter.CreateCounter<long>(
            name: "remote_control.authorization.denied",
            unit: "denials",
            description: "Total number of authorizations denied");

        _authorizedSessions = _meter.CreateObservableGauge<int>(
            name: "remote_control.sessions.authorized",
            observeValue: () => _currentAuthorizedSessions,
            unit: "sessions",
            description: "Number of currently authorized remote control sessions");

        // === System Resource Metrics ===

        _memoryUsageBytes = _meter.CreateObservableGauge<long>(
            name: "system.memory.usage",
            observeValue: GetMemoryUsage,
            unit: "bytes",
            description: "Process memory usage in bytes");

        _cpuUsagePercent = _meter.CreateObservableGauge<double>(
            name: "system.cpu.usage",
            observeValue: GetCpuUsage,
            unit: "%",
            description: "Process CPU usage percentage");
    }

    #region Capture Pipeline

    /// <summary>
    /// Records that a frame was successfully captured from the screen.
    /// </summary>
    /// <param name="captureTimeMs">Time taken to capture the frame in milliseconds.</param>
    public void RecordFrameCaptured(double captureTimeMs)
    {
        _framesCapture.Add(1);
        _captureTime.Record(captureTimeMs);
    }

    /// <summary>
    /// Records that a frame was dropped due to encoding backlog or other issues.
    /// </summary>
    public void RecordFrameDropped()
    {
        _framesDropped.Add(1);
    }

    /// <summary>
    /// Records the time taken to encode a frame to VP8 format.
    /// </summary>
    /// <param name="encodingTimeMs">Encoding time in milliseconds.</param>
    public void RecordFrameEncoded(double encodingTimeMs)
    {
        _encodingTime.Record(encodingTimeMs);
    }

    /// <summary>
    /// Updates the current frames per second metric.
    /// This should be called periodically (e.g., every second) with the current FPS value.
    /// </summary>
    /// <param name="fps">Current frames per second.</param>
    public void UpdateCurrentFps(double fps)
    {
        _currentFps = fps;
    }

    #endregion

    #region WebRTC Connections

    /// <summary>
    /// Records a new WebRTC connection being established.
    /// </summary>
    public void RecordConnectionEstablished()
    {
        _connectionsTotal.Add(1);
        Interlocked.Increment(ref _currentActiveConnections);
    }

    /// <summary>
    /// Records a WebRTC connection being closed.
    /// </summary>
    /// <param name="durationSeconds">Duration the connection was active in seconds.</param>
    public void RecordConnectionClosed(long durationSeconds)
    {
        _disconnectionsTotal.Add(1);
        _connectionDuration.Record(durationSeconds);
        Interlocked.Decrement(ref _currentActiveConnections);
    }

    /// <summary>
    /// Records WebRTC performance statistics for a connection.
    /// </summary>
    /// <param name="bitrateKbps">Current bitrate in kilobits per second.</param>
    /// <param name="packetLossPercent">Packet loss percentage (0-100).</param>
    /// <param name="latencyMs">Round-trip time in milliseconds.</param>
    public void RecordWebRtcStats(long bitrateKbps, double packetLossPercent, long latencyMs)
    {
        _bitrate.Record(bitrateKbps);
        _packetLoss.Record(packetLossPercent);
        _latencyMs.Record(latencyMs);
    }

    #endregion

    #region Remote Control

    /// <summary>
    /// Records a mouse move event being processed.
    /// </summary>
    public void RecordMouseMove()
    {
        _mouseMovesTotal.Add(1);
    }

    /// <summary>
    /// Records a mouse click event being processed.
    /// </summary>
    public void RecordMouseClick()
    {
        _mouseClicksTotal.Add(1);
    }

    /// <summary>
    /// Records a keyboard event being processed.
    /// </summary>
    public void RecordKeyboardEvent()
    {
        _keysTotal.Add(1);
    }

    /// <summary>
    /// Records an input event being rejected (rate limit or validation failure).
    /// </summary>
    /// <param name="reason">Reason for rejection (e.g., "rate_limit", "invalid_coordinates").</param>
    public void RecordInputRejected(string reason)
    {
        _inputsRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    /// <summary>
    /// Records a remote control authorization being requested.
    /// </summary>
    public void RecordAuthorizationRequested()
    {
        _authorizationsRequested.Add(1);
    }

    /// <summary>
    /// Records a remote control authorization being granted.
    /// </summary>
    public void RecordAuthorizationGranted()
    {
        _authorizationsGranted.Add(1);
        Interlocked.Increment(ref _currentAuthorizedSessions);
    }

    /// <summary>
    /// Records a remote control authorization being denied.
    /// </summary>
    public void RecordAuthorizationDenied()
    {
        _authorizationsDenied.Add(1);
    }

    /// <summary>
    /// Records a remote control authorization being revoked.
    /// </summary>
    public void RecordAuthorizationRevoked()
    {
        Interlocked.Decrement(ref _currentAuthorizedSessions);
    }

    #endregion

    #region System Resources

    /// <summary>
    /// Gets the current process memory usage in bytes.
    /// </summary>
    private long GetMemoryUsage()
    {
        _currentProcess.Refresh();
        return _currentProcess.WorkingSet64;
    }

    /// <summary>
    /// Gets the current process CPU usage percentage.
    /// </summary>
    /// <remarks>
    /// Calculates CPU usage based on the time elapsed since the last measurement.
    /// Returns 0 for the first call or if insufficient time has elapsed.
    /// </remarks>
    private double GetCpuUsage()
    {
        var currentTime = DateTime.UtcNow;
        var currentCpuTime = _currentProcess.TotalProcessorTime;

        var timeDiff = (currentTime - _lastCpuCheckTime).TotalMilliseconds;
        if (timeDiff < 100) // Avoid division by very small numbers
        {
            return 0;
        }

        var cpuDiff = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
        var cpuUsage = (cpuDiff / (timeDiff * Environment.ProcessorCount)) * 100;

        _lastCpuCheckTime = currentTime;
        _lastCpuTime = currentCpuTime;

        return Math.Max(0, Math.Min(100, cpuUsage)); // Clamp to 0-100%
    }

    #endregion

    /// <summary>
    /// Disposes the metrics meter and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _meter.Dispose();
        _currentProcess.Dispose();
        _disposed = true;
    }
}
