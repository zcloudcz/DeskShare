using System.Buffers.Binary;
using System.Net;

namespace DeskShare.Stun;

/// <summary>
/// Represents a STUN message according to RFC 5389.
///
/// STUN Message Structure:
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |0 0|     STUN Message Type     |         Message Length        |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                         Magic Cookie                          |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                                                               |
/// |                     Transaction ID (96 bits)                  |
/// |                                                               |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </summary>
public class StunMessage
{
    public const uint MagicCookie = 0x2112A442;
    public const int HeaderSize = 20;

    public StunMessageType MessageType { get; set; }
    public byte[] TransactionId { get; set; } = new byte[12];
    public List<StunAttribute> Attributes { get; set; } = new();

    /// <summary>
    /// Parses a STUN message from raw bytes.
    /// </summary>
    public static StunMessage Parse(byte[] data)
    {
        if (data.Length < HeaderSize)
            throw new ArgumentException("STUN message too short");

        var message = new StunMessage();

        // Read message type (bytes 0-1)
        var messageType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
        message.MessageType = (StunMessageType)messageType;

        // Read message length (bytes 2-3)
        var messageLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));

        // Read magic cookie (bytes 4-7)
        var magicCookie = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
        if (magicCookie != MagicCookie)
            throw new ArgumentException("Invalid STUN magic cookie");

        // Read transaction ID (bytes 8-19)
        Array.Copy(data, 8, message.TransactionId, 0, 12);

        // Parse attributes
        int offset = HeaderSize;
        while (offset < HeaderSize + messageLength)
        {
            if (offset + 4 > data.Length)
                break;

            var attrType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            var attrLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2, 2));

            offset += 4;

            if (offset + attrLength > data.Length)
                break;

            var attrValue = new byte[attrLength];
            Array.Copy(data, offset, attrValue, 0, attrLength);

            message.Attributes.Add(new StunAttribute
            {
                Type = (StunAttributeType)attrType,
                Value = attrValue
            });

            // Attributes are padded to 4-byte boundary
            offset += attrLength;
            int padding = (4 - (attrLength % 4)) % 4;
            offset += padding;
        }

        return message;
    }

    /// <summary>
    /// Serializes the STUN message to bytes.
    /// </summary>
    public byte[] ToBytes()
    {
        // Calculate total message length
        int attributesLength = 0;
        foreach (var attr in Attributes)
        {
            attributesLength += 4; // Type + Length
            attributesLength += attr.Value.Length;
            int padding = (4 - (attr.Value.Length % 4)) % 4;
            attributesLength += padding;
        }

        var buffer = new byte[HeaderSize + attributesLength];

        // Write message type (bytes 0-1)
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), (ushort)MessageType);

        // Write message length (bytes 2-3)
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)attributesLength);

        // Write magic cookie (bytes 4-7)
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), MagicCookie);

        // Write transaction ID (bytes 8-19)
        Array.Copy(TransactionId, 0, buffer, 8, 12);

        // Write attributes
        int offset = HeaderSize;
        foreach (var attr in Attributes)
        {
            // Write attribute type
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)attr.Type);
            offset += 2;

            // Write attribute length
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)attr.Value.Length);
            offset += 2;

            // Write attribute value
            Array.Copy(attr.Value, 0, buffer, offset, attr.Value.Length);
            offset += attr.Value.Length;

            // Add padding
            int padding = (4 - (attr.Value.Length % 4)) % 4;
            offset += padding;
        }

        return buffer;
    }

    /// <summary>
    /// Creates a Binding Response message with XOR-MAPPED-ADDRESS attribute.
    /// </summary>
    public static StunMessage CreateBindingResponse(byte[] transactionId, IPEndPoint clientEndPoint)
    {
        var response = new StunMessage
        {
            MessageType = StunMessageType.BindingResponse,
            TransactionId = transactionId
        };

        // Add XOR-MAPPED-ADDRESS attribute
        var xorMappedAddress = CreateXorMappedAddressAttribute(clientEndPoint, transactionId);
        response.Attributes.Add(xorMappedAddress);

        // Add SOFTWARE attribute
        var software = CreateSoftwareAttribute("RemoteDesktop.NET STUN Server 1.0");
        response.Attributes.Add(software);

        return response;
    }

    /// <summary>
    /// Creates XOR-MAPPED-ADDRESS attribute (RFC 5389 Section 15.2).
    /// </summary>
    private static StunAttribute CreateXorMappedAddressAttribute(IPEndPoint endPoint, byte[] transactionId)
    {
        var addressBytes = endPoint.Address.GetAddressBytes();
        var isIPv6 = endPoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

        var attrValue = new byte[isIPv6 ? 20 : 8];

        // Reserved (1 byte) + Family (1 byte)
        attrValue[0] = 0x00;
        attrValue[1] = (byte)(isIPv6 ? 0x02 : 0x01);

        // X-Port (2 bytes) - XOR with most significant 16 bits of magic cookie
        var port = (ushort)endPoint.Port;
        var xorPort = (ushort)(port ^ (MagicCookie >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(attrValue.AsSpan(2, 2), xorPort);

        // X-Address - XOR with magic cookie (and transaction ID for IPv6)
        var magicCookieBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(magicCookieBytes, MagicCookie);

        if (isIPv6)
        {
            // IPv6: XOR with magic cookie + transaction ID
            var xorMask = new byte[16];
            Array.Copy(magicCookieBytes, 0, xorMask, 0, 4);
            Array.Copy(transactionId, 0, xorMask, 4, 12);

            for (int i = 0; i < 16; i++)
            {
                attrValue[4 + i] = (byte)(addressBytes[i] ^ xorMask[i]);
            }
        }
        else
        {
            // IPv4: XOR with magic cookie only
            for (int i = 0; i < 4; i++)
            {
                attrValue[4 + i] = (byte)(addressBytes[i] ^ magicCookieBytes[i]);
            }
        }

        return new StunAttribute
        {
            Type = StunAttributeType.XorMappedAddress,
            Value = attrValue
        };
    }

    /// <summary>
    /// Creates SOFTWARE attribute.
    /// </summary>
    private static StunAttribute CreateSoftwareAttribute(string software)
    {
        var softwareBytes = System.Text.Encoding.UTF8.GetBytes(software);

        return new StunAttribute
        {
            Type = StunAttributeType.Software,
            Value = softwareBytes
        };
    }
}

/// <summary>
/// Represents a STUN attribute.
/// </summary>
public class StunAttribute
{
    public StunAttributeType Type { get; set; }
    public byte[] Value { get; set; } = Array.Empty<byte>();
}
