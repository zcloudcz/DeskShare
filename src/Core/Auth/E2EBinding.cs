using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskShare.Core.Auth;

/// <summary>
/// End-to-end binding between sender and viewer that does not depend on the signaling server.
/// </summary>
/// <remarks>
/// Why this exists: the signaling server relays the SDP, and the SDP carries the DTLS fingerprint that
/// identifies the peer's certificate. A malicious server could swap the fingerprints and sit between the two
/// peers (man in the middle). The server also sees the passkey, so the passkey alone cannot protect us.
///
/// Fix: from the passkey the user types we derive TWO independent keys (HKDF, RFC 5869):
/// - <c>authKey</c> goes to the server instead of the passkey. The server only compares strings, so it needs no change.
/// - <c>e2eKey</c> never leaves the sender and the viewer. Over the data channel (which runs inside DTLS) both sides
///   send an HMAC, made with <c>e2eKey</c>, over the two DTLS fingerprints they see. If a man in the middle replaced
///   a fingerprint, the two sides see different fingerprints and the HMACs do not match.
///
/// The HMAC covers the role ("sender"/"viewer") so a peer cannot simply echo the other side's message back.
/// Limit: the server can brute-force the 9-character passkey from <c>authKey</c> offline, but the passkey changes
/// every 45 s and the check has a 10 s deadline, so this only helps an attacker who is very well equipped.
/// </remarks>
public static class E2EBinding
{
    /// <summary>Wire format version in the <c>e2e_bind</c> message.</summary>
    public const int Version = 1;

    /// <summary>How long a peer may stay unverified before the connection is dropped.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public const string SenderRole = "sender";
    public const string ViewerRole = "viewer";

    private const int KeyBytes = 32;
    private const string SaltPrefix = "DeskShare/v1/";
    private const string MacContext = "DeskShare-bind-v1|";

    // "a=fingerprint:sha-256 AB:CD:..." - one per session or media section in the SDP.
    private static readonly Regex FingerprintLine = new(
        @"^a=fingerprint:sha-256[ \t]+([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){31})[ \t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The two keys derived from one passkey.</summary>
    /// <param name="AuthKey">base64url, 43 characters. Sent to the signaling server in place of the passkey.</param>
    /// <param name="E2EKey">32 random-looking bytes. Never sent anywhere.</param>
    public readonly record struct DerivedKeys(string AuthKey, byte[] E2EKey);

    /// <summary>Uppercase, without dashes or spaces: what the user typed ("a3f-7k9-m2p") becomes "A3F7K9M2P".</summary>
    public static string NormalizePasskey(string passkey) =>
        passkey.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();

    /// <param name="passkey">What the user typed or the sender generated; normalized here.</param>
    /// <param name="serverId">Full wire id, e.g. "server-123456789". It is the salt, so a key is bound to one sender.</param>
    public static DerivedKeys DeriveKeys(string passkey, string serverId)
    {
        ArgumentException.ThrowIfNullOrEmpty(passkey);
        ArgumentException.ThrowIfNullOrEmpty(serverId);

        var ikm = Encoding.UTF8.GetBytes(NormalizePasskey(passkey));
        var salt = Encoding.UTF8.GetBytes(SaltPrefix + serverId);

        var auth = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyBytes, salt, "auth"u8.ToArray());
        var e2e = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyBytes, salt, "e2e"u8.ToArray());

        return new DerivedKeys(Base64UrlEncode(auth), e2e);
    }

    /// <summary>
    /// Returns the DTLS fingerprint (sha-256, uppercase, colon separated) of an SDP, or null when there is none or
    /// the SDP is ambiguous. Session level and media level lines both count; they must all agree, because a second,
    /// different fingerprint would let a peer present a certificate we did not bind.
    /// </summary>
    public static string? GetFingerprint(string? sdp)
    {
        if (string.IsNullOrEmpty(sdp)) return null;

        string? found = null;
        foreach (Match m in FingerprintLine.Matches(sdp.Replace("\r", "")))
        {
            var fp = m.Groups[1].Value.ToUpperInvariant();
            if (found != null && found != fp) return null;
            found = fp;
        }
        return found;
    }

    /// <summary>Lowercase hex HMAC-SHA256 over role and both fingerprints, in the fixed order sender, viewer.</summary>
    public static string ComputeMac(byte[] e2eKey, string role, string senderFingerprint, string viewerFingerprint)
    {
        var data = Encoding.UTF8.GetBytes($"{MacContext}{role}|{senderFingerprint}|{viewerFingerprint}");
        return Convert.ToHexString(HMACSHA256.HashData(e2eKey, data)).ToLowerInvariant();
    }

    /// <summary>Constant-time check, so timing does not reveal how many leading bytes of a guess were right.</summary>
    public static bool VerifyMac(byte[] e2eKey, string role, string senderFingerprint, string viewerFingerprint, string? mac)
    {
        if (string.IsNullOrEmpty(mac) || mac.Length != 64) return false;

        byte[] provided;
        try { provided = Convert.FromHexString(mac); }
        catch (FormatException) { return false; }

        var expected = Convert.FromHexString(ComputeMac(e2eKey, role, senderFingerprint, viewerFingerprint));
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>The <c>e2e_bind</c> data channel message.</summary>
    public static string BuildBindMessage(string role, string mac) =>
        JsonSerializer.Serialize(new { type = "e2e_bind", v = Version, role, mac });

    /// <summary>Reads <c>role</c> and <c>mac</c> from an <c>e2e_bind</c> message; false if the shape or version is wrong.</summary>
    public static bool TryReadBind(JsonElement root, out string role, out string mac)
    {
        role = mac = string.Empty;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != Version
            || !root.TryGetProperty("role", out var r) || r.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("mac", out var m) || m.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        role = r.GetString()!;
        mac = m.GetString()!;
        return true;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
