using System.Text.Json;
using DeskShare.Core.Models;

namespace DeskShare.Core.WebRTC;

/// <summary>
/// Single place that decides which data channel message needs which capability.
/// Anything not listed here is rejected before it reaches a handler (default deny).
/// </summary>
internal static class DataChannelPolicy
{
    /// <summary>Largest accepted message in bytes. Real messages (input, clipboard text) are far smaller.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    // Messages that carry a lowercase "type" string (authorization/clipboard protocol).
    // Sent by: web viewer remote-control.js (authorization_request), .NET viewer ClientManager.cs (clipboard).
    private static readonly Dictionary<string, SessionCapabilities> ProtocolMessages = new(StringComparer.Ordinal)
    {
        // None on purpose: the only message an unverified peer may send (see DataChannelManager.GrantedCapabilities),
        // so it has to pass without any grant. Every other message stays behind a grant that unverified peers lack.
        ["e2e_bind"] = SessionCapabilities.None,
        ["authorization_request"] = SessionCapabilities.View,
        ["clipboard"] = SessionCapabilities.Clipboard,
        ["clipboard_subscribe"] = SessionCapabilities.Clipboard,
        // File transfer viewer -> host. file_accept/reject/done/error are host -> viewer and deliberately absent.
        ["file_offer"] = SessionCapabilities.FileTransfer,
        ["file_chunk"] = SessionCapabilities.FileTransfer,
        ["file_end"] = SessionCapabilities.FileTransfer,
        ["file_cancel"] = SessionCapabilities.FileTransfer,
        // Hiding the host's screen from its user is only acceptable for someone who may also control it.
        ["privacy_screen"] = SessionCapabilities.PrivacyScreen | SessionCapabilities.Control,
        // Changing what the host shows is as invasive as controlling it.
        ["switch_display"] = SessionCapabilities.Control
    };

    /// <summary>
    /// Maps a parsed message to the capability it needs.
    /// Input messages (web viewer) use a PascalCase "Type" holding an <see cref="InputMessageType"/> name
    /// (or its number); all of them need <see cref="SessionCapabilities.Control"/>.
    /// The two key spellings are kept apart on purpose: a lowercase "type":"MouseMove" would deserialize
    /// into an InputMessage with the default type, so it must not be treated as input.
    /// </summary>
    /// <returns>False when the message is unknown; <paramref name="typeName"/> is then safe to log.</returns>
    public static bool TryGetRequiredCapability(JsonElement root, out string typeName, out SessionCapabilities required)
    {
        typeName = "<none>";
        required = SessionCapabilities.None;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("type", out var protocolType))
        {
            // Truncate: the name comes from an untrusted peer and ends up in the log.
            typeName = protocolType.ValueKind == JsonValueKind.String ? Truncate(protocolType.GetString()) : "<non-string>";
            return protocolType.ValueKind == JsonValueKind.String
                   && ProtocolMessages.TryGetValue(protocolType.GetString()!, out required);
        }

        if (root.TryGetProperty("Type", out var inputType))
        {
            if (inputType.ValueKind == JsonValueKind.String)
            {
                var name = inputType.GetString()!;
                typeName = Truncate(name);
                // Enum.TryParse would also accept "1" or "MouseMove,KeyUp"; only exact names pass.
                if (Array.IndexOf(Enum.GetNames<InputMessageType>(), name) >= 0)
                {
                    required = SessionCapabilities.Control;
                    return true;
                }
            }
            else if (inputType.ValueKind == JsonValueKind.Number && inputType.TryGetInt32(out var number))
            {
                // The .NET serializer writes enums as numbers.
                typeName = $"InputMessageType#{number}";
                if (Enum.IsDefined(typeof(InputMessageType), number))
                {
                    required = SessionCapabilities.Control;
                    return true;
                }
            }
        }

        return false;
    }

    private static string Truncate(string? value) =>
        value is null ? "<null>" : value.Length <= 40 ? value : value[..40];
}
