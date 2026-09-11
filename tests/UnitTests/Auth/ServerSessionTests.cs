using DeskShare.Core.Auth;
using Xunit;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// Unit tests for ServerSession lifecycle and validation logic.
/// </summary>
public class ServerSessionTests
{
    [Fact]
    public void IsValid_RegisteredWithValidPasskey_ShouldReturnTrue()
    {
        // Arrange
        var session = new ServerSession
        {
            ServerId = "TEST123",
            Passkey = "A3F7K9M2P",
            ValidTo = DateTime.UtcNow.AddSeconds(30),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            Status = SessionStatus.Registered
        };

        // Act
        var isValid = session.IsValid(DateTime.UtcNow);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void IsValid_RegisteredWithExpiredPasskey_ShouldReturnFalse()
    {
        // Arrange
        var session = new ServerSession
        {
            ServerId = "TEST123",
            Passkey = "A3F7K9M2P",
            ValidTo = DateTime.UtcNow.AddSeconds(-10), // Expired 10 seconds ago
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            Status = SessionStatus.Registered
        };

        // Act
        var isValid = session.IsValid(DateTime.UtcNow);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void IsValid_ConnectedWithRecentActivity_ShouldReturnTrue()
    {
        // Arrange
        var session = new ServerSession
        {
            ServerId = "TEST123",
            Passkey = "A3F7K9M2P",
            ValidTo = DateTime.UtcNow.AddSeconds(30),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            Status = SessionStatus.Connected,
            LastActivity = DateTime.UtcNow.AddSeconds(-30) // 30 seconds ago (within 60s grace period)
        };

        // Act
        var isValid = session.IsValid(DateTime.UtcNow);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void IsValid_ConnectedWithOldActivity_ShouldReturnFalse()
    {
        // Arrange
        var session = new ServerSession
        {
            ServerId = "TEST123",
            Passkey = "A3F7K9M2P",
            ValidTo = DateTime.UtcNow.AddSeconds(30),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            Status = SessionStatus.Connected,
            LastActivity = DateTime.UtcNow.AddSeconds(-90) // 90 seconds ago (exceeds 60s grace period)
        };

        // Act
        var isValid = session.IsValid(DateTime.UtcNow);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void IsValid_ExpiredStatus_ShouldReturnFalse()
    {
        // Arrange
        var session = new ServerSession
        {
            ServerId = "TEST123",
            Passkey = "A3F7K9M2P",
            ValidTo = DateTime.UtcNow.AddSeconds(30),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            Status = SessionStatus.Expired
        };

        // Act
        var isValid = session.IsValid(DateTime.UtcNow);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void MarkAsConnected_ShouldUpdateStatusAndClientId()
    {
        // Arrange
        var session = new ServerSession
        {
            ServerId = "TEST123",
            Passkey = "A3F7K9M2P",
            ValidTo = DateTime.UtcNow.AddSeconds(30),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            Status = SessionStatus.Registered
        };

        // Act
        session.MarkAsConnected("CLIENT456");

        // Assert
        Assert.Equal(SessionStatus.Connected, session.Status);
        Assert.Equal("CLIENT456", session.ConnectedClientId);
        Assert.NotNull(session.ConnectedAt);
    }

    [Fact]
    public void UpdateActivity_ShouldUpdateLastActivityTimestamp()
    {
        // Arrange
        var session = new ServerSession
        {
            ServerId = "TEST123",
            Passkey = "A3F7K9M2P",
            ValidTo = DateTime.UtcNow.AddSeconds(30),
            RemoteControlEnabled = false,
            TrustClientPermanent = false
        };
        var oldActivity = session.LastActivity;

        // Act
        System.Threading.Thread.Sleep(10); // Small delay to ensure timestamp changes
        session.UpdateActivity();

        // Assert
        Assert.True(session.LastActivity > oldActivity);
    }

    [Fact]
    public void MarkAsDisconnected_ShouldUpdateStatus()
    {
        // Arrange
        var session = new ServerSession
        {
            ServerId = "TEST123",
            Passkey = "A3F7K9M2P",
            ValidTo = DateTime.UtcNow.AddSeconds(30),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            Status = SessionStatus.Connected
        };

        // Act
        session.MarkAsDisconnected();

        // Assert
        Assert.Equal(SessionStatus.Disconnected, session.Status);
    }

    [Fact]
    public void MarkAsExpired_ShouldUpdateStatus()
    {
        // Arrange
        var session = new ServerSession
        {
            ServerId = "TEST123",
            Passkey = "A3F7K9M2P",
            ValidTo = DateTime.UtcNow.AddSeconds(30),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            Status = SessionStatus.Registered
        };

        // Act
        session.MarkAsExpired();

        // Assert
        Assert.Equal(SessionStatus.Expired, session.Status);
    }
}
