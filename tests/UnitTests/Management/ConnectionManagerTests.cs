using DeskShare.Core.Management;

namespace DeskShare.UnitTests.Management;

/// <summary>
/// Unit tests for ConnectionManager.
/// Tests connection tracking, statistics, and health monitoring.
/// </summary>
public sealed class ConnectionManagerTests : IDisposable
{
    private readonly ConnectionManager _manager = new();

    /// <summary>
    /// Tests that RegisterConnection adds a new connection successfully.
    /// This is the basic happy path for connection registration.
    /// </summary>
    [Fact]
    public void RegisterConnection_NewClient_ReturnsTrue()
    {
        // Arrange
        var clientId = "client-1";
        var remoteAddress = "192.168.1.100:12345";

        // Act
        var result = _manager.RegisterConnection(clientId, remoteAddress);

        // Assert
        Assert.True(result);
        Assert.Equal(1, _manager.ActiveConnectionCount);
        Assert.Equal(1, _manager.TotalConnectionsEver);
    }

    /// <summary>
    /// Tests that RegisterConnection with duplicate client returns false.
    /// Duplicate registrations should be prevented.
    /// </summary>
    [Fact]
    public void RegisterConnection_DuplicateClient_ReturnsFalse()
    {
        // Arrange
        var clientId = "client-1";
        _manager.RegisterConnection(clientId, "192.168.1.100:12345");

        // Act
        var result = _manager.RegisterConnection(clientId, "192.168.1.100:12345");

        // Assert
        Assert.False(result);
        Assert.Equal(1, _manager.ActiveConnectionCount);
    }

    /// <summary>
    /// Tests that RegisterConnection with null/empty ID throws ArgumentException.
    /// Proper input validation is important.
    /// </summary>
    [Fact]
    public void RegisterConnection_NullClientId_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _manager.RegisterConnection(null!, "address"));
        Assert.Throws<ArgumentException>(() => _manager.RegisterConnection("", "address"));
        Assert.Throws<ArgumentException>(() => _manager.RegisterConnection("  ", "address"));
    }

    /// <summary>
    /// Tests that RegisterConnection raises ClientConnected event.
    /// Event subscribers should be notified of new connections.
    /// </summary>
    [Fact]
    public void RegisterConnection_RaisesClientConnectedEvent()
    {
        // Arrange
        ConnectionEventArgs? eventArgs = null;
        _manager.ClientConnected += (sender, args) => eventArgs = args;

        // Act
        _manager.RegisterConnection("client-1", "192.168.1.100:12345");

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal("client-1", eventArgs.ClientId);
        Assert.Equal("192.168.1.100:12345", eventArgs.RemoteAddress);
    }

    /// <summary>
    /// Tests that UnregisterConnection removes an existing connection.
    /// Connection should be removed from active list.
    /// </summary>
    [Fact]
    public void UnregisterConnection_ExistingClient_ReturnsTrue()
    {
        // Arrange
        var clientId = "client-1";
        _manager.RegisterConnection(clientId, "address");

        // Act
        var result = _manager.UnregisterConnection(clientId, "User disconnected");

        // Assert
        Assert.True(result);
        Assert.Equal(0, _manager.ActiveConnectionCount);
        Assert.Equal(1, _manager.TotalDisconnectsEver);
    }

    /// <summary>
    /// Tests that UnregisterConnection with non-existent client returns false.
    /// Attempting to unregister unknown client should be safe.
    /// </summary>
    [Fact]
    public void UnregisterConnection_NonExistentClient_ReturnsFalse()
    {
        // Act
        var result = _manager.UnregisterConnection("non-existent-client");

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Tests that UnregisterConnection raises ClientDisconnected event.
    /// Event should include disconnect reason.
    /// </summary>
    [Fact]
    public void UnregisterConnection_RaisesClientDisconnectedEvent()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "192.168.1.100:12345");
        ConnectionEventArgs? eventArgs = null;
        _manager.ClientDisconnected += (sender, args) => eventArgs = args;

        // Act
        _manager.UnregisterConnection("client-1", "Connection timeout");

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal("client-1", eventArgs.ClientId);
        Assert.Equal("Connection timeout", eventArgs.Reason);
    }

    /// <summary>
    /// Tests updating connection statistics.
    /// Statistics should be tracked and aggregated.
    /// </summary>
    [Fact]
    public void UpdateConnectionStats_UpdatesStatsCorrectly()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "address");
        var stats = new ConnectionStats
        {
            FramesSentDelta = 100,
            FramesDroppedDelta = 5,
            PacketLossPercent = 1.5,
            LatencyMs = 50,
            BandwidthKbps = 5000
        };

        // Act
        _manager.UpdateConnectionStats("client-1", stats);

        // Assert
        var info = _manager.GetConnectionInfo("client-1");
        Assert.NotNull(info);
        Assert.Equal(100, info.TotalFramesSent);
        Assert.Equal(5, info.TotalFramesDropped);
        Assert.Equal(stats, info.CurrentStats);
    }

    /// <summary>
    /// Tests that UpdateConnectionStats with null stats throws ArgumentNullException.
    /// Proper null checking is important.
    /// </summary>
    [Fact]
    public void UpdateConnectionStats_NullStats_ThrowsArgumentNullException()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "address");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            _manager.UpdateConnectionStats("client-1", null!));
    }

    /// <summary>
    /// Tests GetConnectionInfo returns correct information.
    /// Should return full connection details.
    /// </summary>
    [Fact]
    public void GetConnectionInfo_ExistingClient_ReturnsInfo()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "192.168.1.100:12345");

        // Act
        var info = _manager.GetConnectionInfo("client-1");

        // Assert
        Assert.NotNull(info);
        Assert.Equal("client-1", info.ClientId);
        Assert.Equal("192.168.1.100:12345", info.RemoteAddress);
        Assert.Equal(ConnectionState.Connected, info.State);
    }

    /// <summary>
    /// Tests GetConnectionInfo for non-existent client returns null.
    /// This is the expected behavior.
    /// </summary>
    [Fact]
    public void GetConnectionInfo_NonExistentClient_ReturnsNull()
    {
        // Act
        var info = _manager.GetConnectionInfo("non-existent");

        // Assert
        Assert.Null(info);
    }

    /// <summary>
    /// Tests GetAllConnections returns all active connections.
    /// Should return complete list of connections.
    /// </summary>
    [Fact]
    public void GetAllConnections_ReturnsAllActiveConnections()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "address1");
        _manager.RegisterConnection("client-2", "address2");
        _manager.RegisterConnection("client-3", "address3");

        // Act
        var connections = _manager.GetAllConnections();

        // Assert
        Assert.Equal(3, connections.Count);
        Assert.Contains(connections, c => c.ClientId == "client-1");
        Assert.Contains(connections, c => c.ClientId == "client-2");
        Assert.Contains(connections, c => c.ClientId == "client-3");
    }

    /// <summary>
    /// Tests GetAggregateStats returns correct aggregate statistics.
    /// Should summarize all connections.
    /// </summary>
    [Fact]
    public void GetAggregateStats_ReturnsCorrectAggregates()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "address1");
        _manager.RegisterConnection("client-2", "address2");

        var stats1 = new ConnectionStats
        {
            FramesSentDelta = 100,
            FramesDroppedDelta = 5,
            PacketLossPercent = 1.0,
            LatencyMs = 50
        };

        var stats2 = new ConnectionStats
        {
            FramesSentDelta = 200,
            FramesDroppedDelta = 10,
            PacketLossPercent = 2.0,
            LatencyMs = 100
        };

        _manager.UpdateConnectionStats("client-1", stats1);
        _manager.UpdateConnectionStats("client-2", stats2);

        // Act
        var aggregates = _manager.GetAggregateStats();

        // Assert
        Assert.Equal(2, aggregates.ActiveConnections);
        Assert.Equal(2, aggregates.TotalConnectionsEver);
        Assert.Equal(300, aggregates.TotalFramesSent);
        Assert.Equal(15, aggregates.TotalFramesDropped);
        Assert.Equal(1.5, aggregates.AveragePacketLoss); // (1.0 + 2.0) / 2
        Assert.Equal(75, aggregates.AverageLatency);     // (50 + 100) / 2
    }

    /// <summary>
    /// Tests that health status changes trigger HealthChanged event.
    /// Event should fire when connection degrades.
    /// </summary>
    [Fact]
    public void UpdateConnectionStats_HealthDegrades_RaisesHealthChangedEvent()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "address");
        ConnectionHealthEventArgs? eventArgs = null;
        _manager.HealthChanged += (sender, args) => eventArgs = args;

        // Start with good stats
        _manager.UpdateConnectionStats("client-1", new ConnectionStats
        {
            PacketLossPercent = 0.5,
            LatencyMs = 30
        });

        // Act - Update with bad stats
        _manager.UpdateConnectionStats("client-1", new ConnectionStats
        {
            PacketLossPercent = 10.0, // High packet loss
            LatencyMs = 500            // High latency
        });

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal("client-1", eventArgs.ClientId);
        Assert.Equal(ConnectionHealth.Good, eventArgs.OldHealth);
        Assert.Equal(ConnectionHealth.Poor, eventArgs.NewHealth);
    }

    /// <summary>
    /// Tests that good stats result in Good health status.
    /// Low packet loss and latency = Good health.
    /// </summary>
    [Fact]
    public void UpdateConnectionStats_GoodMetrics_SetsGoodHealth()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "address");

        // Act
        _manager.UpdateConnectionStats("client-1", new ConnectionStats
        {
            PacketLossPercent = 0.5,
            LatencyMs = 30
        });

        // Assert
        var info = _manager.GetConnectionInfo("client-1");
        Assert.NotNull(info);
        Assert.Equal(ConnectionHealth.Good, info.HealthStatus);
    }

    /// <summary>
    /// Tests that moderate stats result in Degraded health status.
    /// Moderate packet loss or latency = Degraded health.
    /// </summary>
    [Fact]
    public void UpdateConnectionStats_ModerateMetrics_SetsDegradedHealth()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "address");

        // Act
        _manager.UpdateConnectionStats("client-1", new ConnectionStats
        {
            PacketLossPercent = 3.0, // Moderate packet loss (2-5%)
            LatencyMs = 50
        });

        // Assert
        var info = _manager.GetConnectionInfo("client-1");
        Assert.NotNull(info);
        Assert.Equal(ConnectionHealth.Degraded, info.HealthStatus);
    }

    /// <summary>
    /// Tests that bad stats result in Poor health status.
    /// High packet loss or latency = Poor health.
    /// </summary>
    [Fact]
    public void UpdateConnectionStats_BadMetrics_SetsPoorHealth()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "address");

        // Act
        _manager.UpdateConnectionStats("client-1", new ConnectionStats
        {
            PacketLossPercent = 10.0, // High packet loss (>5%)
            LatencyMs = 500            // High latency (>200ms)
        });

        // Assert
        var info = _manager.GetConnectionInfo("client-1");
        Assert.NotNull(info);
        Assert.Equal(ConnectionHealth.Poor, info.HealthStatus);
    }

    /// <summary>
    /// Tests ClearAll removes all connections.
    /// Useful for reset/testing scenarios.
    /// </summary>
    [Fact]
    public void ClearAll_RemovesAllConnections()
    {
        // Arrange
        _manager.RegisterConnection("client-1", "address1");
        _manager.RegisterConnection("client-2", "address2");
        Assert.Equal(2, _manager.ActiveConnectionCount);

        // Act
        _manager.ClearAll();

        // Assert
        Assert.Equal(0, _manager.ActiveConnectionCount);
        Assert.Empty(_manager.GetAllConnections());
    }

    /// <summary>
    /// Tests that ServerUptime is tracking correctly.
    /// Uptime should increase over time.
    /// </summary>
    [Fact]
    public void ServerUptime_IncreasesOverTime()
    {
        // Arrange
        var initialUptime = _manager.ServerUptime;

        // Act
        Thread.Sleep(100); // Wait 100ms
        var laterUptime = _manager.ServerUptime;

        // Assert
        Assert.True(laterUptime > initialUptime);
    }

    /// <summary>
    /// Tests that Dispose can be called multiple times safely.
    /// Multiple dispose should be idempotent.
    /// </summary>
    [Fact]
    public void Dispose_MultipleTimes_NoException()
    {
        // Arrange
        var manager = new ConnectionManager();

        // Act & Assert
        manager.Dispose();
        manager.Dispose(); // Second dispose should be safe
    }

    /// <summary>
    /// Cleanup after each test.
    /// </summary>
    public void Dispose()
    {
        _manager.Dispose();
    }
}
