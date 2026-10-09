using DeskShare.Core.Auth;
using Serilog;
using SIPSorcery.Net;

namespace DeskShare.Core.WebRTC;

/// <summary>
/// Converts the ICE servers returned by the SignalingServer into SIPSorcery's <see cref="RTCIceServer"/>.
/// </summary>
/// <remarks>
/// SIPSorcery's <c>RTCIceServer.urls</c> is a single string, and its TCP/TLS TURN handling inspects one URL
/// per entry, so every URL becomes its own RTCIceServer (browsers accept URL arrays, SIPSorcery does not).
/// Verified against SIPSorcery 10.0.16: STUNUri.TryParse accepts stun:, stuns:, turn: and turns: with
/// optional "?transport=udp|tcp"; anything it cannot parse is dropped here instead of failing the connection.
/// </remarks>
public static class IceServerMapper
{
    private static readonly string[] SupportedSchemes = { "stun:", "stuns:", "turn:", "turns:" };

    /// <summary>Public Google STUN servers, used until the SignalingServer tells us better.</summary>
    public static List<RTCIceServer> DefaultIceServers() => new()
    {
        new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
        new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
    };

    /// <summary>Maps server-provided ICE servers to SIPSorcery entries, one per supported URL.</summary>
    public static List<RTCIceServer> ToRtcIceServers(IEnumerable<IceServerInfo>? servers)
    {
        var result = new List<RTCIceServer>();
        var dropped = 0;

        foreach (var server in servers ?? Enumerable.Empty<IceServerInfo>())
        {
            foreach (var rawUrl in server.Urls ?? Array.Empty<string>())
            {
                // Some providers pack several URLs into one string separated by commas.
                foreach (var url in (rawUrl ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    // TryParse is lenient (it accepts e.g. "http://host" as a scheme-less STUN host), so check the scheme ourselves.
                    if (!SupportedSchemes.Any(scheme => url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) ||
                        !STUNUri.TryParse(url, out _))
                    {
                        dropped++;
                        continue;
                    }

                    result.Add(new RTCIceServer
                    {
                        urls = url,
                        username = string.IsNullOrEmpty(server.Username) ? null : server.Username,
                        credential = string.IsNullOrEmpty(server.Credential) ? null : server.Credential
                    });
                }
            }
        }

        if (dropped > 0)
            Log.Debug("Dropped {Count} ICE server URL(s) that SIPSorcery cannot parse", dropped);

        return result;
    }
}
