namespace DeskShare.Stun;

/// <summary>
/// STUN message types according to RFC 5389.
/// </summary>
public enum StunMessageType : ushort
{
    // Request messages (0x0000 - 0x00FF)
    BindingRequest = 0x0001,

    // Success responses (0x0100 - 0x01FF)
    BindingResponse = 0x0101,

    // Error responses (0x0110 - 0x011F)
    BindingErrorResponse = 0x0111,

    // Shared Secret (deprecated but included for completeness)
    SharedSecretRequest = 0x0002,
    SharedSecretResponse = 0x0102,
    SharedSecretErrorResponse = 0x0112
}
