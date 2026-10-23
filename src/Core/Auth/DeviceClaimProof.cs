using System.Security.Cryptography;
using System.Text;

namespace DeskShare.Core.Auth;

/// <summary>
/// Proof that the caller of <c>POST /me/devices/claim</c> owns a Server ID, so a signed-in user can add only their
/// own computers to "My devices".
/// </summary>
/// <remarks>
/// Who owns a Server ID is already decided by the owner secret the sender presents in /register (trust on first use,
/// see SignalingServer ConnectionManager.AuthorizeServerOwner). The server never keeps that secret, only
/// SHA-256(secret). So the proof is an HMAC keyed with exactly that hash: the device computes the hash from its secret,
/// the server already has it, and neither side has to send or store anything new. The secret itself never leaves the
/// device and a captured proof does not reveal it.
/// The message binds the proof to one Server ID, one account (a proof made for account A is useless for account B),
/// a timestamp (freshness window) and a nonce (replay protection; the server remembers used nonces for the window).
/// Fields are joined with "|", so none of them may contain one (callers validate the formats).
/// This file is also compiled into DeskShare.Service (linked source, see its csproj), so it must stay dependency-free.
/// The web client (src/WebClient/accounts.js) does not compute proofs; tests/js/claim-vector.mjs checks that the
/// same construction in WebCrypto gives the same bytes, for clients written in JS later (mobile).
/// </remarks>
public static class DeviceClaimProof
{
    /// <summary>Domain separation: an HMAC made for something else with the same key can never pass as a claim.</summary>
    public const string Context = "DeskShare-claim-v1";

    /// <summary>How far the claim timestamp may be from the server clock, either direction.</summary>
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

    /// <summary>The HMAC key: SHA-256 of the owner secret, the same value the signaling server stores per Server ID.</summary>
    public static byte[] ClaimKey(string ownerSecret) => SHA256.HashData(Encoding.UTF8.GetBytes(ownerSecret));

    /// <summary>The exact bytes that are MACed (UTF-8 of this string).</summary>
    public static string Message(string serverId, string accountKey, long unixSeconds, string nonce) =>
        $"{Context}|{serverId}|{accountKey}|{unixSeconds}|{nonce}";

    /// <summary>Base64 HMAC-SHA256 of <see cref="Message"/> keyed with <paramref name="claimKey"/>.</summary>
    public static string Compute(byte[] claimKey, string serverId, string accountKey, long unixSeconds, string nonce) =>
        Convert.ToBase64String(HMACSHA256.HashData(claimKey,
            Encoding.UTF8.GetBytes(Message(serverId, accountKey, unixSeconds, nonce))));

    /// <summary>Constant-time check of a proof (so response timing does not tell how many bytes matched).</summary>
    public static bool Verify(byte[] claimKey, string serverId, string accountKey, long unixSeconds, string nonce, string proof)
    {
        byte[] provided;
        try { provided = Convert.FromBase64String(proof); }
        catch (FormatException) { return false; }

        var expected = HMACSHA256.HashData(claimKey, Encoding.UTF8.GetBytes(Message(serverId, accountKey, unixSeconds, nonce)));
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
