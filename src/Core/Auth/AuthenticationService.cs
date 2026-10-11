using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;

namespace DeskShare.Core.Auth;

/// <summary>
/// Provides authentication services for server-client connections.
/// Generates server IDs from MAC address and time-based passkeys for secure connections.
/// </summary>
/// <remarks>
/// For junior developers:
/// This class handles the security aspect of remote desktop connections.
///
/// How it works:
/// 1. Server ID: Generated from MAC address (stable identifier for this machine)
/// 2. Passkey: random 9-character code (rotates every 45 seconds), registered with the SignalingServer
/// 3. The passkey proves that someone physically has access to the server machine
///
/// Why time-based passkeys?
/// - Short expiration (45s) limits window for unauthorized access
/// - Auto-rotation means if someone sees the passkey, it's only valid briefly
/// - No need to store passwords - a fresh random one is generated on demand
/// </remarks>
public sealed class AuthenticationService
{
    /// <summary>
    /// Passkey validity duration in seconds.
    /// After this time, a new passkey must be generated.
    /// </summary>
    public const int PasskeyValiditySeconds = 45;

    /// <summary>
    /// Passkey length (9 characters for user-friendliness).
    /// Displayed as XXX-XXX-XXX for better readability.
    /// </summary>
    public const int PasskeyLength = 9;

    /// <summary>
    /// Characters allowed in passkey (uppercase alphanumeric, excluding ambiguous chars).
    /// Excluded: 0, O, 1, I to prevent confusion.
    /// </summary>
    private const string PasskeyCharset = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>
    /// Generates a server ID from the machine's primary MAC address.
    /// </summary>
    /// <returns>Server ID in format "XXXXXXXXXXXX" (12 hex chars from MAC address).</returns>
    /// <remarks>
    /// For junior developers:
    /// The server ID is derived from the MAC address, which is unique to each network card.
    /// This provides a stable identifier for the server that won't change between restarts.
    ///
    /// We use the same logic as ServerIdGenerator for consistency.
    /// </remarks>
    public static string GenerateServerId()
    {
        return ServerIdGenerator.GenerateServerId();
    }

    /// <summary>
    /// Generates a random passkey for authentication.
    /// </summary>
    /// <returns>A 9-character passkey using safe alphanumeric characters.</returns>
    /// <remarks>
    /// For junior developers:
    /// The passkey must be unguessable, so it comes from a cryptographic random number generator and is NOT
    /// derived from anything a third party could know (such as the Server ID, which is shown in the UI).
    /// The sender registers the value with the SignalingServer, which compares what a viewer types against it.
    /// <see cref="RandomNumberGenerator.GetInt32(int)"/> avoids modulo bias, so every character is equally likely.
    /// </remarks>
    public static string GeneratePasskey()
    {
        return string.Create(PasskeyLength, 0, static (span, _) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = PasskeyCharset[RandomNumberGenerator.GetInt32(PasskeyCharset.Length)];
        });
    }

    /// <summary>
    /// Calculates the expiration time for a passkey generated at the given timestamp.
    /// </summary>
    /// <param name="timestamp">The timestamp when passkey was generated.</param>
    /// <returns>DateTime when the passkey expires (timestamp + 45 seconds).</returns>
    public static DateTime GetPasskeyExpiration(DateTime timestamp)
    {
        // Round to start of current interval, then add validity duration
        long totalSeconds = (long)timestamp.ToUniversalTime().TimeOfDay.TotalSeconds;
        long intervalNumber = totalSeconds / PasskeyValiditySeconds;
        long roundedSeconds = intervalNumber * PasskeyValiditySeconds;

        var roundedTime = timestamp.ToUniversalTime().Date.AddSeconds(roundedSeconds);
        return roundedTime.AddSeconds(PasskeyValiditySeconds);
    }

    /// <summary>
    /// Compares a provided passkey with the registered one in constant time, ignoring case.
    /// </summary>
    /// <remarks>
    /// Fails closed: an empty registered passkey (session without a stored value) never matches anything.
    /// Constant time so response timing cannot be used to guess the passkey character by character.
    /// </remarks>
    public static bool PasskeyMatches(string? registeredPasskey, string? providedPasskey)
    {
        if (string.IsNullOrEmpty(registeredPasskey) || string.IsNullOrEmpty(providedPasskey))
            return false;

        return FixedTimeEquals(
            Encoding.UTF8.GetBytes(registeredPasskey.ToUpperInvariant()),
            Encoding.UTF8.GetBytes(providedPasskey.ToUpperInvariant()));
    }

    /// <summary>
    /// Formats a passkey for display as XXX-XXX-XXX for better readability.
    /// </summary>
    /// <param name="passkey">The 9-character passkey.</param>
    /// <returns>Formatted passkey with dashes.</returns>
    /// <remarks>
    /// For junior developers:
    /// Humans read groups of 3-4 characters better than long strings.
    /// Example: "A3F7K9M2P" → "A3F-7K9-M2P"
    ///
    /// This is similar to how phone numbers are formatted: 123-456-7890
    /// </remarks>
    public static string FormatPasskeyForDisplay(string passkey)
    {
        if (string.IsNullOrEmpty(passkey) || passkey.Length != PasskeyLength)
            return passkey;

        return $"{passkey.Substring(0, 3)}-{passkey.Substring(3, 3)}-{passkey.Substring(6, 3)}";
    }

    /// <summary>
    /// Removes formatting from a passkey (strips dashes).
    /// </summary>
    /// <param name="formattedPasskey">Passkey with dashes (XXX-XXX-XXX).</param>
    /// <returns>Passkey without dashes (XXXXXXXXX).</returns>
    public static string UnformatPasskey(string formattedPasskey)
    {
        if (string.IsNullOrEmpty(formattedPasskey))
            return formattedPasskey;

        return formattedPasskey.Replace("-", "").ToUpperInvariant();
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
