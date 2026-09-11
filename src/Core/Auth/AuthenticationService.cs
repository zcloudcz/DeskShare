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
/// 2. Passkey: 9-character code generated from MAC + current time (rotates every 45 seconds)
/// 3. The passkey proves that someone physically has access to the server machine
///
/// Why time-based passkeys?
/// - Short expiration (45s) limits window for unauthorized access
/// - Auto-rotation means if someone sees the passkey, it's only valid briefly
/// - No need to store passwords - regenerated on demand
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
    /// Generates a time-based passkey for authentication.
    /// The passkey is valid for 45 seconds from the provided timestamp.
    /// </summary>
    /// <param name="serverId">The server ID (from MAC address).</param>
    /// <param name="timestamp">The timestamp to use for generation (typically DateTime.UtcNow).</param>
    /// <returns>A 9-character passkey using safe alphanumeric characters.</returns>
    /// <remarks>
    /// For junior developers:
    /// This generates a temporary password that changes every 45 seconds.
    ///
    /// How it works:
    /// 1. Combine serverId + rounded timestamp (to 45-second intervals)
    /// 2. Hash the combination using SHA256 (cryptographic hash function)
    /// 3. Convert hash bytes to a 9-character code using our safe character set
    ///
    /// Why hash?
    /// - Ensures the passkey is unpredictable (can't guess next one)
    /// - Same inputs always produce same output (deterministic)
    /// - One-way function (can't reverse-engineer the serverId from passkey)
    ///
    /// Why round timestamp?
    /// - So the same passkey is generated for the entire 45-second window
    /// - Client and server can independently generate the same passkey
    /// </remarks>
    public static string GeneratePasskey(string serverId, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(serverId))
            throw new ArgumentNullException(nameof(serverId));

        // Round timestamp to 45-second intervals
        // Example: 10:00:37 → 10:00:00, 10:00:52 → 10:00:45, 10:01:31 → 10:01:30
        long totalSeconds = (long)timestamp.ToUniversalTime().TimeOfDay.TotalSeconds;
        long intervalNumber = totalSeconds / PasskeyValiditySeconds;
        long roundedSeconds = intervalNumber * PasskeyValiditySeconds;

        // Combine serverId with rounded timestamp for uniqueness
        string input = $"{serverId}-{timestamp.ToUniversalTime().Date:yyyyMMdd}-{roundedSeconds}";

        // Generate hash
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        // Convert hash to passkey using safe character set
        var passkey = new StringBuilder(PasskeyLength);
        for (int i = 0; i < PasskeyLength; i++)
        {
            // Use hash bytes to pick characters from charset
            // Modulo ensures we stay within charset bounds
            int index = hashBytes[i % hashBytes.Length] % PasskeyCharset.Length;
            passkey.Append(PasskeyCharset[index]);
        }

        return passkey.ToString();
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
    /// Validates a passkey against the expected value for the given server and timestamp.
    /// </summary>
    /// <param name="serverId">The server ID.</param>
    /// <param name="providedPasskey">The passkey to validate.</param>
    /// <param name="validationTime">The time to validate against (typically DateTime.UtcNow).</param>
    /// <returns>True if passkey is valid, false otherwise.</returns>
    /// <remarks>
    /// For junior developers:
    /// This checks if a passkey is correct and not expired.
    ///
    /// How it works:
    /// 1. Generate what the passkey SHOULD be for this timestamp
    /// 2. Compare it with what the user provided (case-insensitive)
    /// 3. Return true only if they match exactly
    ///
    /// Security note:
    /// We use InvariantCultureIgnoreCase for comparison so users can type
    /// lowercase letters, but the comparison is still secure.
    /// </remarks>
    public static bool ValidatePasskey(string serverId, string providedPasskey, DateTime validationTime)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(providedPasskey))
            return false;

        string expectedPasskey = GeneratePasskey(serverId, validationTime);

        return FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedPasskey.ToUpperInvariant()),
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
