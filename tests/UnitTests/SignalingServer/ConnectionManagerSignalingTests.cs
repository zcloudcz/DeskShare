using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using NSubstitute;
using DeskShare.SignalingServer.Services;

namespace DeskShare.UnitTests.SignalingServer;

public class ConnectionManagerSignalingTests
{
    [Fact]
    public void WebSocketToken_IsSingleUse_AndMapsBackToClientId()
    {
        var manager = CreateConnectionManager();

        var token = manager.IssueWebSocketToken("server-1");

        Assert.True(manager.ValidateAndConsumeToken(token, out var clientId));
        Assert.Equal("server-1", clientId);
        Assert.False(manager.ValidateAndConsumeToken(token, out _)); // second use must fail
    }

    private ConnectionManager CreateConnectionManager()
    {
        var logger = Substitute.For<ILogger<ConnectionManager>>();
        return new ConnectionManager(logger);
    }

    [Fact]
    public void RegisterClient_NewClient_ReturnsTrue()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket = Substitute.For<WebSocket>();
        var clientId = "client-1";

        // Act
        var result = manager.RegisterClient(clientId, mockWebSocket, "127.0.0.1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void RegisterClient_DuplicateClient_ReturnsFalse()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket1 = Substitute.For<WebSocket>();
        var mockWebSocket2 = Substitute.For<WebSocket>();
        var clientId = "client-1";

        manager.RegisterClient(clientId, mockWebSocket1, "127.0.0.1");

        // Act
        var result = manager.RegisterClient(clientId, mockWebSocket2, "127.0.0.2");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void RegisterClient_NullClientId_ThrowsArgumentException()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket = Substitute.For<WebSocket>();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.RegisterClient(null!, mockWebSocket, "127.0.0.1"));
    }

    [Fact]
    public void RegisterClient_EmptyClientId_ThrowsArgumentException()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket = Substitute.For<WebSocket>();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            manager.RegisterClient("", mockWebSocket, "127.0.0.1"));
    }

    [Fact]
    public void RegisterClient_NullWebSocket_ThrowsArgumentNullException()
    {
        // Arrange
        var manager = CreateConnectionManager();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            manager.RegisterClient("client-1", null!, "127.0.0.1"));
    }

    [Fact]
    public void UnregisterClient_ExistingClient_RemovesClient()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket = Substitute.For<WebSocket>();
        var clientId = "client-1";

        manager.RegisterClient(clientId, mockWebSocket, "127.0.0.1");
        Assert.Equal(1, manager.ConnectionCount);

        // Act
        manager.UnregisterClient(clientId, "Test disconnect");

        // Assert
        Assert.Equal(0, manager.ConnectionCount);
    }

    [Fact]
    public void UnregisterClient_NonExistentClient_NoError()
    {
        // Arrange
        var manager = CreateConnectionManager();

        // Act & Assert - Should not throw
        manager.UnregisterClient("nonexistent", "Test");
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket = Substitute.For<WebSocket>();

        manager.RegisterClient("client-1", mockWebSocket, "127.0.0.1");
        manager.RecordMessageReceived("client-1");
        manager.RecordMessageReceived("client-1");

        // Act
        var stats = manager.GetStatistics();

        // Assert
        Assert.Equal(1, stats.ActiveConnections);
        Assert.Equal(1, stats.TotalConnectionsEver);
        Assert.Equal(2, stats.TotalMessagesReceived);
    }

    [Fact]
    public void RecordMessageReceived_UpdatesLastActivity()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket = Substitute.For<WebSocket>();
        var clientId = "client-1";

        manager.RegisterClient(clientId, mockWebSocket, "127.0.0.1");
        var initialStats = manager.GetStatistics();
        var initialMessages = initialStats.TotalMessagesReceived;

        // Act
        System.Threading.Thread.Sleep(10); // Ensure time difference
        manager.RecordMessageReceived(clientId);

        var finalStats = manager.GetStatistics();

        // Assert
        Assert.Equal(initialMessages + 1, finalStats.TotalMessagesReceived);
    }

    [Fact]
    public void GetStatistics_IncludesClientInfo()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket = Substitute.For<WebSocket>();
        var clientId = "client-1";
        var remoteAddress = "192.168.1.100";

        manager.RegisterClient(clientId, mockWebSocket, remoteAddress);

        // Act
        var stats = manager.GetStatistics();

        // Assert
        Assert.NotNull(stats);
        Assert.NotEmpty(stats.Connections);
        var clientInfo = stats.Connections.First();
        Assert.Equal(clientId, clientInfo.ClientId);
        Assert.Equal(remoteAddress, clientInfo.RemoteAddress);
    }

    [Fact]
    public void ConnectionCount_UpdatesCorrectly()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket1 = Substitute.For<WebSocket>();
        var mockWebSocket2 = Substitute.For<WebSocket>();

        // Act & Assert
        Assert.Equal(0, manager.ConnectionCount);

        manager.RegisterClient("client-1", mockWebSocket1, "127.0.0.1");
        Assert.Equal(1, manager.ConnectionCount);

        manager.RegisterClient("client-2", mockWebSocket2, "127.0.0.2");
        Assert.Equal(2, manager.ConnectionCount);

        manager.UnregisterClient("client-1", "Test");
        Assert.Equal(1, manager.ConnectionCount);

        manager.UnregisterClient("client-2", "Test");
        Assert.Equal(0, manager.ConnectionCount);
    }

    [Fact]
    public void GetStatistics_AfterUnregister_UpdatesActiveConnections()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket = Substitute.For<WebSocket>();

        manager.RegisterClient("client-1", mockWebSocket, "127.0.0.1");
        var statsWithConnection = manager.GetStatistics();

        // Act
        manager.UnregisterClient("client-1", "Test");
        var statsAfterDisconnect = manager.GetStatistics();

        // Assert
        Assert.Equal(1, statsWithConnection.ActiveConnections);
        Assert.Equal(1, statsWithConnection.TotalConnectionsEver);

        Assert.Equal(0, statsAfterDisconnect.ActiveConnections);
        Assert.Equal(1, statsAfterDisconnect.TotalConnectionsEver);
        Assert.Equal(1, statsAfterDisconnect.TotalDisconnectsEver);
    }

    [Fact]
    public void RecordMessageReceived_NonExistentClient_NoException()
    {
        // Arrange
        var manager = CreateConnectionManager();

        // Act & Assert - Should not throw
        manager.RecordMessageReceived("nonexistent");
    }

    [Fact]
    public void GetStatistics_TracksServerUptime()
    {
        // Arrange
        var manager = CreateConnectionManager();

        // Act
        System.Threading.Thread.Sleep(50); // Wait a bit
        var stats = manager.GetStatistics();

        // Assert
        Assert.True(stats.ServerUptime.TotalMilliseconds >= 50);
    }

    [Fact]
    public void RegisterClient_MultipleClients_AllTracked()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket1 = Substitute.For<WebSocket>();
        var mockWebSocket2 = Substitute.For<WebSocket>();
        var mockWebSocket3 = Substitute.For<WebSocket>();

        // Act
        manager.RegisterClient("client-1", mockWebSocket1, "127.0.0.1");
        manager.RegisterClient("client-2", mockWebSocket2, "127.0.0.2");
        manager.RegisterClient("client-3", mockWebSocket3, "127.0.0.3");

        var stats = manager.GetStatistics();

        // Assert
        Assert.Equal(3, stats.ActiveConnections);
        Assert.Equal(3, stats.TotalConnectionsEver);
        Assert.Equal(3, stats.Connections.Count);
    }

    [Fact]
    public void GetStatistics_CalculatesAverageSessionDuration()
    {
        // Arrange
        var manager = CreateConnectionManager();
        var mockWebSocket = Substitute.For<WebSocket>();

        manager.RegisterClient("client-1", mockWebSocket, "127.0.0.1");
        System.Threading.Thread.Sleep(100); // Let some time pass

        // Act
        var stats = manager.GetStatistics();

        // Assert
        Assert.True(stats.AverageSessionDuration.TotalMilliseconds >= 0);
        Assert.NotEqual(TimeSpan.Zero, stats.AverageSessionDuration);
    }

    [Fact]
    public void GetStatistics_NoConnections_AverageIsZero()
    {
        // Arrange
        var manager = CreateConnectionManager();

        // Act
        var stats = manager.GetStatistics();

        // Assert
        Assert.Equal(0, stats.ActiveConnections);
        Assert.Equal(TimeSpan.Zero, stats.AverageSessionDuration);
    }
}
