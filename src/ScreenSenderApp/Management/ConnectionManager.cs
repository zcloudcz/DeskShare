using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeskShare.ScreenSenderApp.Management;

/// <summary>
/// Centralized connection manager for tracking and monitoring all client connections.
/// Provides statistics, health monitoring, and event notifications.
/// </summary>
/// <remarks>
/// This manager keeps track of:
/// - All active client connections
/// - Connection health metrics (uptime, packet loss, latency)
/// - Connection events (connect, disconnect, quality changes)
/// - Aggregate statistics across all clients
///
/// Use this for monitoring dashboards and diagnostics.
/// </remarks>
public sealed class ConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
    private readonly Stopwatch _serverUptime = Stopwatch.StartNew();
    private bool _disposed;

    // Aggregate statistics
    private long _totalConnectionsEver;
    private long _totalDisconnectsEver;
    private long _totalFramesSent;
    private long _totalFramesDropped;

    /// <summary>
    /// Gets the total number of currently active connections.
    /// </summary>
    public int ActiveConnectionCount => _connections.Count;

    /// <summary>
    /// Gets the server uptime duration.
    /// </summary>
    public TimeSpan ServerUptime => _serverUptime.Elapsed;

    /// <summary>
    /// Gets total connections ever made since server start.
    /// </summary>
    public long TotalConnectionsEver => _totalConnectionsEver;

    /// <summary>
    /// Gets total disconnects ever since server start.
    /// </summary>
    public long TotalDisconnectsEver => _totalDisconnectsEver;

    /// <summary>
    /// Event raised when a new client connects.
    /// </summary>
    public event EventHandler<ConnectionEventArgs>? ClientConnected;

    /// <summary>
    /// Event raised when a client disconnects.
    /// </summary>
    public event EventHandler<ConnectionEventArgs>? ClientDisconnected;

    /// <summary>
    /// Event raised when connection health changes (quality degradation, recovery, etc.).
    /// </summary>
    public event EventHandler<ConnectionHealthEventArgs>? HealthChanged;

    /// <summary>
    /// Registers a new client connection.
    /// Should be called when a client successfully establishes connection.
    /// </summary>
    /// <param name="clientId">Unique client identifier.</param>
    /// <param name="remoteAddress">Client's remote address (IP:port).</param>
    /// <returns>True if connection was registered, false if already exists.</returns>
    public bool RegisterConnection(string clientId, string remoteAddress = "unknown")
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("Client ID cannot be null or whitespace.", nameof(clientId));

        var connectionInfo = new ConnectionInfo
        {
            ClientId = clientId,
            RemoteAddress = remoteAddress,
            ConnectedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            State = ConnectionState.Connected
        };

        if (_connections.TryAdd(clientId, connectionInfo))
        {
            Interlocked.Increment(ref _totalConnectionsEver);
            ClientConnected?.Invoke(this, new ConnectionEventArgs(clientId, remoteAddress));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Unregisters a client connection.
    /// Should be called when a client disconnects or connection fails.
    /// </summary>
    /// <param name="clientId">Client identifier to unregister.</param>
    /// <param name="reason">Reason for disconnection (optional).</param>
    /// <returns>True if connection was unregistered, false if not found.</returns>
    public bool UnregisterConnection(string clientId, string reason = "")
    {
        if (_connections.TryRemove(clientId, out var connectionInfo))
        {
            connectionInfo.DisconnectedAt = DateTime.UtcNow;
            connectionInfo.DisconnectReason = reason;
            connectionInfo.State = ConnectionState.Disconnected;

            Interlocked.Increment(ref _totalDisconnectsEver);
            ClientDisconnected?.Invoke(this, new ConnectionEventArgs(clientId, connectionInfo.RemoteAddress, reason));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Updates connection statistics for a specific client.
    /// Should be called periodically with latest metrics.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="stats">Current connection statistics.</param>
    public void UpdateConnectionStats(string clientId, ConnectionStats stats)
    {
        if (stats == null)
            throw new ArgumentNullException(nameof(stats));

        if (_connections.TryGetValue(clientId, out var connectionInfo))
        {
            connectionInfo.LastActivityAt = DateTime.UtcNow;
            connectionInfo.CurrentStats = stats;
            connectionInfo.TotalFramesSent += stats.FramesSentDelta;
            connectionInfo.TotalFramesDropped += stats.FramesDroppedDelta;

            // Update aggregate statistics
            Interlocked.Add(ref _totalFramesSent, stats.FramesSentDelta);
            Interlocked.Add(ref _totalFramesDropped, stats.FramesDroppedDelta);

            // Check for health issues
            CheckConnectionHealth(connectionInfo);
        }
    }

    /// <summary>
    /// Gets connection information for a specific client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <returns>Connection info or null if not found.</returns>
    public ConnectionInfo? GetConnectionInfo(string clientId)
    {
        return _connections.TryGetValue(clientId, out var info) ? info : null;
    }

    /// <summary>
    /// Gets all active connections.
    /// </summary>
    /// <returns>List of all active connection infos.</returns>
    public IReadOnlyList<ConnectionInfo> GetAllConnections()
    {
        return _connections.Values.ToList();
    }

    /// <summary>
    /// Gets aggregate statistics across all connections.
    /// </summary>
    /// <returns>Aggregate statistics summary.</returns>
    public AggregateStats GetAggregateStats()
    {
        var connections = _connections.Values.ToList();

        return new AggregateStats
        {
            ActiveConnections = connections.Count,
            TotalConnectionsEver = _totalConnectionsEver,
            TotalDisconnectsEver = _totalDisconnectsEver,
            TotalFramesSent = _totalFramesSent,
            TotalFramesDropped = _totalFramesDropped,
            AverageUptime = connections.Any()
                ? TimeSpan.FromSeconds(connections.Average(c => (DateTime.UtcNow - c.ConnectedAt).TotalSeconds))
                : TimeSpan.Zero,
            AveragePacketLoss = connections.Any()
                ? connections.Average(c => c.CurrentStats?.PacketLossPercent ?? 0)
                : 0,
            AverageLatency = connections.Any()
                ? connections.Average(c => c.CurrentStats?.LatencyMs ?? 0)
                : 0,
            ServerUptime = _serverUptime.Elapsed
        };
    }

    /// <summary>
    /// Checks connection health and raises events if issues detected.
    /// </summary>
    private void CheckConnectionHealth(ConnectionInfo connectionInfo)
    {
        if (connectionInfo.CurrentStats == null)
            return;

        var stats = connectionInfo.CurrentStats;
        var previousHealth = connectionInfo.HealthStatus;
        var newHealth = DetermineHealthStatus(stats);

        if (newHealth != previousHealth)
        {
            connectionInfo.HealthStatus = newHealth;
            HealthChanged?.Invoke(this, new ConnectionHealthEventArgs(
                connectionInfo.ClientId,
                previousHealth,
                newHealth,
                stats.PacketLossPercent,
                stats.LatencyMs));
        }
    }

    /// <summary>
    /// Determines health status based on connection statistics.
    /// </summary>
    private ConnectionHealth DetermineHealthStatus(ConnectionStats stats)
    {
        // Poor health: high packet loss OR high latency
        if (stats.PacketLossPercent > 5.0 || stats.LatencyMs > 200)
        {
            return ConnectionHealth.Poor;
        }

        // Degraded health: moderate packet loss OR moderate latency
        if (stats.PacketLossPercent > 2.0 || stats.LatencyMs > 100)
        {
            return ConnectionHealth.Degraded;
        }

        // Good health
        return ConnectionHealth.Good;
    }

    /// <summary>
    /// Clears all connections (useful for reset/testing).
    /// </summary>
    public void ClearAll()
    {
        _connections.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _connections.Clear();
        _serverUptime.Stop();
        _disposed = true;
    }
}

/// <summary>
/// Information about a single client connection.
/// Tracks connection metadata, statistics, and health.
/// </summary>
public sealed class ConnectionInfo
{
    /// <summary>
    /// Unique client identifier.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Remote address (IP:port) of the client.
    /// </summary>
    public string RemoteAddress { get; set; } = string.Empty;

    /// <summary>
    /// When the connection was established.
    /// </summary>
    public DateTime ConnectedAt { get; set; }

    /// <summary>
    /// When the connection was terminated (null if still active).
    /// </summary>
    public DateTime? DisconnectedAt { get; set; }

    /// <summary>
    /// Reason for disconnection (if applicable).
    /// </summary>
    public string DisconnectReason { get; set; } = string.Empty;

    /// <summary>
    /// Last activity timestamp (updated on each stat update).
    /// </summary>
    public DateTime LastActivityAt { get; set; }

    /// <summary>
    /// Current connection state.
    /// </summary>
    public ConnectionState State { get; set; }

    /// <summary>
    /// Current connection health status.
    /// </summary>
    public ConnectionHealth HealthStatus { get; set; } = ConnectionHealth.Good;

    /// <summary>
    /// Current connection statistics (latest update).
    /// </summary>
    public ConnectionStats? CurrentStats { get; set; }

    /// <summary>
    /// Total frames sent to this client since connection.
    /// </summary>
    public long TotalFramesSent { get; set; }

    /// <summary>
    /// Total frames dropped for this client since connection.
    /// </summary>
    public long TotalFramesDropped { get; set; }

    /// <summary>
    /// Gets connection uptime duration.
    /// </summary>
    public TimeSpan Uptime => DisconnectedAt.HasValue
        ? DisconnectedAt.Value - ConnectedAt
        : DateTime.UtcNow - ConnectedAt;
}

/// <summary>
/// Connection statistics for a specific time period.
/// These are delta values (changes since last update).
/// </summary>
public sealed class ConnectionStats
{
    /// <summary>
    /// Frames sent since last update (delta).
    /// </summary>
    public long FramesSentDelta { get; set; }

    /// <summary>
    /// Frames dropped since last update (delta).
    /// </summary>
    public long FramesDroppedDelta { get; set; }

    /// <summary>
    /// Current packet loss percentage (0-100).
    /// </summary>
    public double PacketLossPercent { get; set; }

    /// <summary>
    /// Current latency in milliseconds (round-trip time).
    /// </summary>
    public double LatencyMs { get; set; }

    /// <summary>
    /// Current bandwidth utilization in kilobits per second.
    /// </summary>
    public long BandwidthKbps { get; set; }
}

/// <summary>
/// Aggregate statistics across all connections.
/// </summary>
public sealed class AggregateStats
{
    /// <summary>Number of currently active connections.</summary>
    public int ActiveConnections { get; set; }

    /// <summary>Total connections ever established.</summary>
    public long TotalConnectionsEver { get; set; }

    /// <summary>Total disconnects ever occurred.</summary>
    public long TotalDisconnectsEver { get; set; }

    /// <summary>Total frames sent across all connections.</summary>
    public long TotalFramesSent { get; set; }

    /// <summary>Total frames dropped across all connections.</summary>
    public long TotalFramesDropped { get; set; }

    /// <summary>Average connection uptime across all clients.</summary>
    public TimeSpan AverageUptime { get; set; }

    /// <summary>Average packet loss percentage across all clients.</summary>
    public double AveragePacketLoss { get; set; }

    /// <summary>Average latency in milliseconds across all clients.</summary>
    public double AverageLatency { get; set; }

    /// <summary>Total server uptime since start.</summary>
    public TimeSpan ServerUptime { get; set; }
}

/// <summary>
/// Connection state enumeration.
/// </summary>
public enum ConnectionState
{
    /// <summary>Connection is being established.</summary>
    Connecting,

    /// <summary>Connection is active and healthy.</summary>
    Connected,

    /// <summary>Connection is being torn down.</summary>
    Disconnecting,

    /// <summary>Connection has been terminated.</summary>
    Disconnected
}

/// <summary>
/// Connection health status.
/// </summary>
public enum ConnectionHealth
{
    /// <summary>Good health - low packet loss, low latency.</summary>
    Good,

    /// <summary>Degraded health - moderate packet loss or latency.</summary>
    Degraded,

    /// <summary>Poor health - high packet loss or latency.</summary>
    Poor
}

/// <summary>
/// Event args for connection events (connect/disconnect).
/// </summary>
public sealed class ConnectionEventArgs : EventArgs
{
    /// <summary>Gets the client identifier.</summary>
    public string ClientId { get; }

    /// <summary>Gets the remote address of the client.</summary>
    public string RemoteAddress { get; }

    /// <summary>Gets the reason for the event (empty for connect, disconnect reason for disconnect).</summary>
    public string Reason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionEventArgs"/> class.
    /// </summary>
    public ConnectionEventArgs(string clientId, string remoteAddress, string reason = "")
    {
        ClientId = clientId;
        RemoteAddress = remoteAddress;
        Reason = reason;
    }
}

/// <summary>
/// Event args for connection health changes.
/// </summary>
public sealed class ConnectionHealthEventArgs : EventArgs
{
    /// <summary>Gets the client identifier.</summary>
    public string ClientId { get; }

    /// <summary>Gets the previous health status.</summary>
    public ConnectionHealth OldHealth { get; }

    /// <summary>Gets the new health status.</summary>
    public ConnectionHealth NewHealth { get; }

    /// <summary>Gets the current packet loss percentage.</summary>
    public double PacketLoss { get; }

    /// <summary>Gets the current latency in milliseconds.</summary>
    public double Latency { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionHealthEventArgs"/> class.
    /// </summary>
    public ConnectionHealthEventArgs(
        string clientId,
        ConnectionHealth oldHealth,
        ConnectionHealth newHealth,
        double packetLoss,
        double latency)
    {
        ClientId = clientId;
        OldHealth = oldHealth;
        NewHealth = newHealth;
        PacketLoss = packetLoss;
        Latency = latency;
    }
}
