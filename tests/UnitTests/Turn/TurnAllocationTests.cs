using System.Net;
using System.Net.Sockets;
using DeskShare.Turn;

namespace DeskShare.UnitTests.Turn;

public class TurnAllocationTests
{
    [Fact]
    public void Constructor_CreatesAllocationWithGuid()
    {
        // Arrange
        var clientEndPoint = new IPEndPoint(IPAddress.Loopback, 5000);
        var relayedEndPoint = new IPEndPoint(IPAddress.Loopback, 6000);
        var relaySocket = new UdpClient(0);

        try
        {
            // Act
            var allocation = new TurnAllocation(
                clientEndPoint,
                relayedEndPoint,
                relaySocket,
                "testuser",
                "test.realm");

            // Assert
            Assert.NotNull(allocation.AllocationId);
            Assert.NotEqual(Guid.Empty.ToString(), allocation.AllocationId);
            Assert.Equal(clientEndPoint, allocation.ClientEndPoint);
            Assert.Equal(relayedEndPoint, allocation.RelayedEndPoint);
            Assert.Equal("testuser", allocation.Username);
            Assert.Equal("test.realm", allocation.Realm);
        }
        finally
        {
            relaySocket.Dispose();
        }
    }

    [Fact]
    public void Constructor_SetsCreatedAndExpiresAt()
    {
        // Arrange
        var clientEndPoint = new IPEndPoint(IPAddress.Loopback, 5000);
        var relayedEndPoint = new IPEndPoint(IPAddress.Loopback, 6000);
        var relaySocket = new UdpClient(0);
        var beforeCreation = DateTime.UtcNow;

        try
        {
            // Act
            var allocation = new TurnAllocation(
                clientEndPoint,
                relayedEndPoint,
                relaySocket,
                "testuser",
                "test.realm");

            var afterCreation = DateTime.UtcNow;

            // Assert
            Assert.True(allocation.CreatedAt >= beforeCreation);
            Assert.True(allocation.CreatedAt <= afterCreation);
            Assert.True(allocation.ExpiresAt > allocation.CreatedAt);
            Assert.True(allocation.ExpiresAt <= allocation.CreatedAt.Add(TurnAllocation.DefaultLifetime).AddSeconds(1));
        }
        finally
        {
            relaySocket.Dispose();
        }
    }

    [Fact]
    public void IsExpired_NewAllocation_ReturnsFalse()
    {
        // Arrange
        var allocation = CreateTestAllocation();

        // Act
        var isExpired = allocation.IsExpired();

        // Assert
        Assert.False(isExpired);
    }

    [Fact]
    public void IsExpired_AfterExpiryTime_ReturnsTrue()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        allocation.ExpiresAt = DateTime.UtcNow.AddSeconds(-1); // Set to past

        // Act
        var isExpired = allocation.IsExpired();

        // Assert
        Assert.True(isExpired);
    }

    [Fact]
    public void Refresh_UpdatesExpiresAt()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var originalExpiresAt = allocation.ExpiresAt;

        // Act
        System.Threading.Thread.Sleep(10); // Ensure time difference
        allocation.Refresh();

        // Assert
        Assert.True(allocation.ExpiresAt > originalExpiresAt);
    }

    [Fact]
    public void Refresh_WithCustomLifetime_UsesSpecifiedLifetime()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var customLifetime = TimeSpan.FromMinutes(5);

        // Act
        allocation.Refresh(customLifetime);

        // Assert
        var expectedExpiry = DateTime.UtcNow.Add(customLifetime);
        Assert.True(allocation.ExpiresAt >= expectedExpiry.AddSeconds(-1));
        Assert.True(allocation.ExpiresAt <= expectedExpiry.AddSeconds(1));
    }

    [Fact]
    public void Refresh_ExceedingMaxLifetime_ClampedToMax()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var excessiveLifetime = TimeSpan.FromHours(10);

        // Act
        allocation.Refresh(excessiveLifetime);

        // Assert
        var expectedExpiry = DateTime.UtcNow.Add(TurnAllocation.MaxLifetime);
        Assert.True(allocation.ExpiresAt >= expectedExpiry.AddSeconds(-1));
        Assert.True(allocation.ExpiresAt <= expectedExpiry.AddSeconds(1));
    }

    [Fact]
    public void HasPermission_NoPeerAdded_ReturnsFalse()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peerAddress = IPAddress.Parse("203.0.113.50");

        // Act
        var hasPermission = allocation.HasPermission(peerAddress);

        // Assert
        Assert.False(hasPermission);
    }

    [Fact]
    public void CreatePermission_AddsPeerPermission()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peerAddress = IPAddress.Parse("203.0.113.50");

        // Act
        allocation.CreatePermission(peerAddress);

        // Assert
        Assert.True(allocation.HasPermission(peerAddress));
    }

    [Fact]
    public void HasPermission_ExpiredPermission_ReturnsFalse()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peerAddress = IPAddress.Parse("203.0.113.50");
        allocation.Permissions[peerAddress] = DateTime.UtcNow.AddSeconds(-1); // Expired

        // Act
        var hasPermission = allocation.HasPermission(peerAddress);

        // Assert
        Assert.False(hasPermission);
    }

    [Fact]
    public void CreatePermission_MultiplePeers_AllHavePermission()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peer1 = IPAddress.Parse("203.0.113.1");
        var peer2 = IPAddress.Parse("203.0.113.2");
        var peer3 = IPAddress.Parse("203.0.113.3");

        // Act
        allocation.CreatePermission(peer1);
        allocation.CreatePermission(peer2);
        allocation.CreatePermission(peer3);

        // Assert
        Assert.True(allocation.HasPermission(peer1));
        Assert.True(allocation.HasPermission(peer2));
        Assert.True(allocation.HasPermission(peer3));
    }

    [Fact]
    public void BindChannel_ValidChannelNumber_ReturnsTrue()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peerEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 12345);

        // Act
        var result = allocation.BindChannel(0x4000, peerEndPoint);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void BindChannel_InvalidChannelNumberTooLow_ReturnsFalse()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peerEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 12345);

        // Act
        var result = allocation.BindChannel(0x3FFF, peerEndPoint); // Below 0x4000

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void BindChannel_InvalidChannelNumberTooHigh_ReturnsFalse()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peerEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 12345);

        // Act
        var result = allocation.BindChannel(0x8000, peerEndPoint); // Above 0x7FFF

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void BindChannel_DuplicateChannel_ReturnsFalse()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peer1 = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 12345);
        var peer2 = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 12345);
        allocation.BindChannel(0x4000, peer1);

        // Act
        var result = allocation.BindChannel(0x4000, peer2);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetChannel_ExistingChannel_ReturnsBinding()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peerEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 12345);
        allocation.BindChannel(0x4000, peerEndPoint);

        // Act
        var binding = allocation.GetChannel(0x4000);

        // Assert
        Assert.NotNull(binding);
        Assert.Equal(0x4000, binding.ChannelNumber);
        Assert.Equal(peerEndPoint, binding.PeerEndPoint);
    }

    [Fact]
    public void GetChannel_NonExistentChannel_ReturnsNull()
    {
        // Arrange
        var allocation = CreateTestAllocation();

        // Act
        var binding = allocation.GetChannel(0x4000);

        // Assert
        Assert.Null(binding);
    }

    [Fact]
    public void FindChannelByPeer_ExistingPeer_ReturnsChannelNumber()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peerEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 12345);
        allocation.BindChannel(0x4001, peerEndPoint);

        // Act
        var channelNumber = allocation.FindChannelByPeer(peerEndPoint);

        // Assert
        Assert.NotNull(channelNumber);
        Assert.Equal((ushort)0x4001, channelNumber.Value);
    }

    [Fact]
    public void FindChannelByPeer_NonExistentPeer_ReturnsNull()
    {
        // Arrange
        var allocation = CreateTestAllocation();
        var peerEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 12345);

        // Act
        var channelNumber = allocation.FindChannelByPeer(peerEndPoint);

        // Assert
        Assert.Null(channelNumber);
    }

    [Fact]
    public void Statistics_InitiallyZero()
    {
        // Arrange
        var allocation = CreateTestAllocation();

        // Act & Assert
        Assert.Equal(0, allocation.Statistics.BytesSentToPeer);
        Assert.Equal(0, allocation.Statistics.BytesReceivedFromPeer);
        Assert.Equal(0, allocation.Statistics.PacketsSentToPeer);
        Assert.Equal(0, allocation.Statistics.PacketsReceivedFromPeer);
    }

    private static TurnAllocation CreateTestAllocation()
    {
        var clientEndPoint = new IPEndPoint(IPAddress.Loopback, 5000);
        var relayedEndPoint = new IPEndPoint(IPAddress.Loopback, 6000);
        var relaySocket = new UdpClient(0);

        return new TurnAllocation(
            clientEndPoint,
            relayedEndPoint,
            relaySocket,
            "testuser",
            "test.realm");
    }
}
