namespace DeskShare.Stun;

/// <summary>
/// STUN attribute types according to RFC 5389.
/// </summary>
public enum StunAttributeType : ushort
{
    // Comprehension-required range (0x0000-0x7FFF)
    MappedAddress = 0x0001,
    ResponseAddress = 0x0002,      // Deprecated
    ChangeRequest = 0x0003,         // Deprecated
    SourceAddress = 0x0004,         // Deprecated
    ChangedAddress = 0x0005,        // Deprecated
    Username = 0x0006,
    Password = 0x0007,              // Deprecated
    MessageIntegrity = 0x0008,
    ErrorCode = 0x0009,
    UnknownAttributes = 0x000A,
    ReflectedFrom = 0x000B,         // Deprecated
    Realm = 0x0014,
    Nonce = 0x0015,
    XorMappedAddress = 0x0020,

    // Comprehension-optional range (0x8000-0xFFFF)
    Software = 0x8022,
    AlternateServer = 0x8023,
    Fingerprint = 0x8028,

    // TURN-specific attributes (RFC 5766)
    ChannelNumber = 0x000C,
    Lifetime = 0x000D,
    XorPeerAddress = 0x0012,
    Data = 0x0013,
    XorRelayedAddress = 0x0016,
    EvenPort = 0x0018,
    RequestedTransport = 0x0019,
    DontFragment = 0x001A,
    ReservationToken = 0x0022
}
