using System.Text.Json.Serialization;

namespace DeskShare.Core.Auth;

/// <summary>
/// One ICE (STUN/TURN) server as handed out by the SignalingServer.
/// Same shape as the browser's <c>RTCIceServer</c>, so the web client can pass it straight to RTCPeerConnection.
/// </summary>
public sealed class IceServerInfo
{
    /// <summary>One or more STUN/TURN URLs that share the same credentials.</summary>
    public string[] Urls { get; set; } = [];

    // Omitted from JSON when null: a browser would turn a JSON null into the string "null".
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Credential { get; set; }
}
