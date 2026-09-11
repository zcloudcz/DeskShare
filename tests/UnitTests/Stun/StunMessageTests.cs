using System.Buffers.Binary;
using System.Net;
using DeskShare.Stun;

namespace DeskShare.UnitTests.Stun;

public class StunMessageTests
{
    [Fact]
    public void Constructor_CreatesMessageWithTransactionId()
    {
        // Act
        var message = new StunMessage
        {
            MessageType = StunMessageType.BindingRequest,
            TransactionId = new byte[12]
        };

        // Assert
        Assert.NotNull(message);
        Assert.Equal(StunMessageType.BindingRequest, message.MessageType);
        Assert.Equal(12, message.TransactionId.Length);
    }

    [Fact]
    public void ToBytes_MinimalMessage_ReturnsValidBytes()
    {
        // Arrange
        var message = new StunMessage
        {
            MessageType = StunMessageType.BindingRequest,
            TransactionId = new byte[12]
        };

        // Act
        var bytes = message.ToBytes();

        // Assert
        Assert.NotNull(bytes);
        Assert.True(bytes.Length >= StunMessage.HeaderSize);

        // Verify magic cookie at offset 4
        var magicCookie = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
        Assert.Equal(StunMessage.MagicCookie, magicCookie);
    }

    [Fact]
    public void Parse_ValidBindingRequest_Success()
    {
        // Arrange
        var originalMessage = new StunMessage
        {
            MessageType = StunMessageType.BindingRequest,
            TransactionId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }
        };
        var bytes = originalMessage.ToBytes();

        // Act
        var parsedMessage = StunMessage.Parse(bytes);

        // Assert
        Assert.NotNull(parsedMessage);
        Assert.Equal(StunMessageType.BindingRequest, parsedMessage.MessageType);
        Assert.Equal(originalMessage.TransactionId, parsedMessage.TransactionId);
    }

    [Fact]
    public void Parse_TooShort_ThrowsArgumentException()
    {
        // Arrange
        var shortBytes = new byte[10]; // Less than minimum header size

        // Act & Assert
        Assert.Throws<ArgumentException>(() => StunMessage.Parse(shortBytes));
    }

    [Fact]
    public void Parse_InvalidMagicCookie_ThrowsArgumentException()
    {
        // Arrange
        var bytes = new byte[StunMessage.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 0xFFFFFFFF); // Wrong magic cookie

        // Act & Assert
        Assert.Throws<ArgumentException>(() => StunMessage.Parse(bytes));
    }

    [Fact]
    public void CreateBindingResponse_ValidInput_CreatesResponse()
    {
        // Arrange
        var transactionId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var clientEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.100"), 12345);

        // Act
        var response = StunMessage.CreateBindingResponse(transactionId, clientEndPoint);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(StunMessageType.BindingResponse, response.MessageType);
        Assert.Equal(transactionId, response.TransactionId);
        Assert.Contains(response.Attributes, a => a.Type == StunAttributeType.XorMappedAddress);
    }

    [Fact]
    public void CreateBindingResponse_WithIPv4_ContainsXorMappedAddress()
    {
        // Arrange
        var transactionId = new byte[12];
        var clientEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 54321);

        // Act
        var response = StunMessage.CreateBindingResponse(transactionId, clientEndPoint);
        var bytes = response.ToBytes();
        var parsed = StunMessage.Parse(bytes);

        // Assert
        var xorMappedAddress = parsed.Attributes.FirstOrDefault(a => a.Type == StunAttributeType.XorMappedAddress);
        Assert.NotNull(xorMappedAddress);
        Assert.NotNull(xorMappedAddress.Value);
        Assert.True(xorMappedAddress.Value.Length >= 8); // IPv4 XOR-MAPPED-ADDRESS is 8 bytes
    }

    [Fact]
    public void ToBytes_WithAttributes_IncludesAttributesInOutput()
    {
        // Arrange
        var message = new StunMessage
        {
            MessageType = StunMessageType.BindingResponse,
            TransactionId = new byte[12]
        };

        var attribute = new StunAttribute
        {
            Type = StunAttributeType.Software,
            Value = System.Text.Encoding.UTF8.GetBytes("TestServer")
        };
        message.Attributes.Add(attribute);

        // Act
        var bytes = message.ToBytes();

        // Assert
        Assert.True(bytes.Length > StunMessage.HeaderSize);

        // Parse and verify attribute is present
        var parsed = StunMessage.Parse(bytes);
        Assert.Single(parsed.Attributes);
        Assert.Equal(StunAttributeType.Software, parsed.Attributes[0].Type);
    }

    [Fact]
    public void RoundTrip_ComplexMessage_PreservesData()
    {
        // Arrange
        var original = new StunMessage
        {
            MessageType = StunMessageType.BindingResponse,
            TransactionId = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }
        };
        original.Attributes.Add(new StunAttribute
        {
            Type = StunAttributeType.Software,
            Value = System.Text.Encoding.UTF8.GetBytes("Test")
        });

        // Act
        var bytes = original.ToBytes();
        var parsed = StunMessage.Parse(bytes);

        // Assert
        Assert.Equal(original.MessageType, parsed.MessageType);
        Assert.Equal(original.TransactionId, parsed.TransactionId);
        Assert.Equal(original.Attributes.Count, parsed.Attributes.Count);
    }
}
