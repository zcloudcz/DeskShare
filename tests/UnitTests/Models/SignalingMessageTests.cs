using System.Text.Json;
using DeskShare.Core.Models;

namespace DeskShare.UnitTests.Models;

/// <summary>
/// Unit tests for SignalingMessage serialization and validation.
/// </summary>
public sealed class SignalingMessageTests
{
    [Fact]
    public void Constructor_CreatesDefaultMessage()
    {
        // Act
        var message = new SignalingMessage();

        // Assert
        Assert.Equal(SignalingMessageType.Offer, message.Type); // Default enum value
        Assert.Null(message.SenderId);
        Assert.Null(message.TargetId);
        Assert.Null(message.Sdp);
        Assert.Null(message.Candidate);
        Assert.NotEqual(DateTime.MinValue, message.Timestamp);
    }

    [Fact]
    public void Serialization_OfferMessage_RoundTrip()
    {
        // Arrange
        var original = new SignalingMessage
        {
            Type = SignalingMessageType.Offer,
            SenderId = "sender-123",
            TargetId = "target-456",
            Sdp = "v=0\r\no=- 123456 0 IN IP4 127.0.0.1\r\n",
            Timestamp = new DateTime(2025, 10, 29, 12, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SignalingMessage>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Type, deserialized.Type);
        Assert.Equal(original.SenderId, deserialized.SenderId);
        Assert.Equal(original.TargetId, deserialized.TargetId);
        Assert.Equal(original.Sdp, deserialized.Sdp);
        Assert.Equal(original.Timestamp, deserialized.Timestamp);
    }

    [Fact]
    public void Serialization_IceCandidateMessage_RoundTrip()
    {
        // Arrange
        var original = new SignalingMessage
        {
            Type = SignalingMessageType.IceCandidate,
            SenderId = "sender-123",
            TargetId = "target-456",
            Candidate = "{\"candidate\":\"...\",\"sdpMid\":\"0\",\"sdpMLineIndex\":0}",
            SdpMLineIndex = 0,
            SdpMid = "0",
            Timestamp = DateTime.UtcNow
        };

        // Act
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SignalingMessage>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(SignalingMessageType.IceCandidate, deserialized.Type);
        Assert.Equal(original.Candidate, deserialized.Candidate);
        Assert.Equal(original.SdpMLineIndex, deserialized.SdpMLineIndex);
        Assert.Equal(original.SdpMid, deserialized.SdpMid);
    }

    [Fact]
    public void Serialization_ErrorMessage_RoundTrip()
    {
        // Arrange
        var original = new SignalingMessage
        {
            Type = SignalingMessageType.Error,
            SenderId = "server",
            TargetId = "client-123",
            ErrorMessage = "Connection failed: Timeout",
            Timestamp = DateTime.UtcNow
        };

        // Act
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SignalingMessage>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(SignalingMessageType.Error, deserialized.Type);
        Assert.Equal(original.ErrorMessage, deserialized.ErrorMessage);
    }

    [Fact]
    public void Serialization_IdentifyMessage_RoundTrip()
    {
        // Arrange
        var original = new SignalingMessage
        {
            Type = SignalingMessageType.Identify,
            SenderId = "server",
            TargetId = "new-client-789",
            Timestamp = DateTime.UtcNow
        };

        // Act
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SignalingMessage>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(SignalingMessageType.Identify, deserialized.Type);
        Assert.Equal(original.SenderId, deserialized.SenderId);
        Assert.Equal(original.TargetId, deserialized.TargetId);
    }

    [Fact]
    public void Serialization_AllMessageTypes_AreSerializable()
    {
        // Arrange & Act & Assert
        foreach (SignalingMessageType type in Enum.GetValues(typeof(SignalingMessageType)))
        {
            var message = new SignalingMessage { Type = type };
            var json = JsonSerializer.Serialize(message);
            var deserialized = JsonSerializer.Deserialize<SignalingMessage>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(type, deserialized.Type);
        }
    }

    [Fact]
    public void Serialization_EmptyJson_CanDeserialize()
    {
        // Arrange
        var json = "{}";

        // Act
        var message = JsonSerializer.Deserialize<SignalingMessage>(json);

        // Assert
        Assert.NotNull(message);
        Assert.Equal(SignalingMessageType.Offer, message.Type); // Default
        Assert.Null(message.SenderId);
    }

    [Fact]
    public void Serialization_WithNullValues_HandlesGracefully()
    {
        // Arrange
        var original = new SignalingMessage
        {
            Type = SignalingMessageType.Answer,
            SenderId = null,
            TargetId = null,
            Sdp = null,
            Candidate = null
        };

        // Act
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SignalingMessage>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(SignalingMessageType.Answer, deserialized.Type);
        Assert.Null(deserialized.SenderId);
        Assert.Null(deserialized.Sdp);
    }
}
