namespace DeskShare.Core.Models;

/// <summary>
/// Type of signaling message.
/// </summary>
public enum SignalingMessageType
{
    /// <summary>
    /// Session Description Protocol offer message.
    /// </summary>
    Offer,

    /// <summary>
    /// Session Description Protocol answer message.
    /// </summary>
    Answer,

    /// <summary>
    /// Interactive Connectivity Establishment candidate message.
    /// </summary>
    IceCandidate,

    /// <summary>
    /// Client identification message.
    /// </summary>
    Identify,

    /// <summary>
    /// Error message.
    /// </summary>
    Error,

    /// <summary>
    /// Connection request from client to specific server.
    /// </summary>
    ConnectionRequest,

    /// <summary>
    /// Ping message to check if peer is alive.
    /// </summary>
    Ping,

    /// <summary>
    /// Pong response to Ping message.
    /// </summary>
    Pong
}

/// <summary>
/// Represents a signaling message exchanged between peers for WebRTC connection establishment.
/// </summary>
public sealed class SignalingMessage
{
    /// <summary>
    /// Type of the message.
    /// </summary>
    public SignalingMessageType Type { get; set; }

    /// <summary>
    /// Unique identifier of the sender client.
    /// </summary>
    public string? SenderId { get; set; }

    /// <summary>
    /// Unique identifier of the target client.
    /// </summary>
    public string? TargetId { get; set; }

    /// <summary>
    /// SDP (Session Description Protocol) content for Offer/Answer messages.
    /// </summary>
    public string? Sdp { get; set; }

    /// <summary>
    /// ICE candidate data in JSON format.
    /// </summary>
    public string? Candidate { get; set; }

    /// <summary>
    /// SDP media line index for ICE candidate.
    /// </summary>
    public int? SdpMLineIndex { get; set; }

    /// <summary>
    /// SDP media ID for ICE candidate.
    /// </summary>
    public string? SdpMid { get; set; }

    /// <summary>
    /// Error message content.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Timestamp when the message was created.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
