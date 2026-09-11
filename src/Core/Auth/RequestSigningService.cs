using System.Security.Cryptography;
using System.Text;

namespace DeskShare.Core.Auth;

/// <summary>
/// Provides HMAC-based request signing and validation.
/// Protects against replay attacks, tampering, and ensures request integrity.
/// </summary>
/// <remarks>
/// Security features:
/// - HMAC-SHA256 cryptographic signing
/// - Timestamp-based expiration (prevents old requests)
/// - Nonce-based replay prevention (each request unique)
/// - Constant-time comparison (prevents timing attacks)
///
/// How it works:
/// 1. Client creates signature: HMAC(serverId|timestamp|nonce, passkey)
/// 2. Client sends: serverId, timestamp, nonce, signature
/// 3. Server validates: recompute signature, compare with received
/// 4. Server checks: timestamp not expired, nonce not used before
/// </remarks>
public sealed class RequestSigningService
{
    /// <summary>
    /// Signature validity window in seconds.
    /// Requests older than this are rejected to prevent replay attacks.
    /// </summary>
    /// <remarks>
    /// 60 seconds provides balance between:
    /// - Security (short window limits attack opportunity)
    /// - Reliability (tolerates network delays and clock skew)
    ///
    /// If clients and servers have significant clock differences,
    /// consider increasing to 120 seconds.
    /// </remarks>
    public const int SignatureValiditySeconds = 60;

    /// <summary>
    /// Creates HMAC-SHA256 signature for authentication request.
    /// </summary>
    /// <param name="serverId">Server ID (public identifier, typically MAC-based)</param>
    /// <param name="passkey">Passkey (shared secret between client and server)</param>
    /// <param name="timestamp">Request timestamp in UTC</param>
    /// <param name="nonce">Unique request identifier (prevents replay attacks)</param>
    /// <returns>Base64-encoded HMAC-SHA256 signature</returns>
    /// <exception cref="ArgumentNullException">If any parameter is null or empty</exception>
    /// <remarks>
    /// Example usage:
    /// <code>
    /// var nonce = Guid.NewGuid().ToString();
    /// var timestamp = DateTime.UtcNow;
    /// var signature = RequestSigningService.SignRequest(
    ///     "AABBCCDDEEFF", "A3F7K9M2P", timestamp, nonce);
    /// </code>
    ///
    /// The signature ensures:
    /// - Authenticity: Only someone with the passkey can create valid signature
    /// - Integrity: Any modification to serverId/timestamp/nonce invalidates signature
    /// - Uniqueness: Nonce makes each request unique (prevents replay)
    /// </remarks>
    public static string SignRequest(
        string serverId,
        string passkey,
        DateTime timestamp,
        string nonce)
    {
        // Input validation
        if (string.IsNullOrEmpty(serverId))
            throw new ArgumentNullException(nameof(serverId));

        if (string.IsNullOrEmpty(passkey))
            throw new ArgumentNullException(nameof(passkey));

        if (string.IsNullOrEmpty(nonce))
            throw new ArgumentNullException(nameof(nonce));

        // Create message to sign
        // Format: serverId|timestamp|nonce
        // Using ISO 8601 format for timestamp (ensures consistency)
        string message = $"{serverId}|{timestamp:O}|{nonce}";

        // Compute HMAC-SHA256 using passkey as the secret key
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(passkey));
        byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));

        // Return Base64 encoded signature (URL-safe for HTTP transmission)
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Validates HMAC signature of authentication request.
    /// </summary>
    /// <param name="serverId">Server ID from request</param>
    /// <param name="passkey">Current valid passkey for this server</param>
    /// <param name="timestamp">Timestamp from request (UTC)</param>
    /// <param name="nonce">Nonce from request</param>
    /// <param name="signature">Signature to validate</param>
    /// <returns>True if signature is valid and not expired, false otherwise</returns>
    /// <remarks>
    /// Validation steps:
    /// 1. Check timestamp is not too old (within SignatureValiditySeconds)
    /// 2. Recompute expected signature using provided parameters
    /// 3. Compare with provided signature using constant-time comparison
    ///
    /// Security considerations:
    /// - Timestamp validation prevents replay of old requests
    /// - Constant-time comparison prevents timing attacks
    /// - Nonce must be checked separately (not done here - use NonceCache)
    ///
    /// Returns false if:
    /// - Any parameter is null/empty
    /// - Timestamp is too old (&gt; SignatureValiditySeconds)
    /// - Timestamp is in the future (&gt; 10 seconds ahead)
    /// - Signature doesn't match expected value
    /// </remarks>
    public static bool ValidateSignature(
        string serverId,
        string passkey,
        DateTime timestamp,
        string nonce,
        string signature)
    {
        // Input validation
        if (string.IsNullOrEmpty(serverId) ||
            string.IsNullOrEmpty(passkey) ||
            string.IsNullOrEmpty(nonce) ||
            string.IsNullOrEmpty(signature))
        {
            return false;
        }

        // Check timestamp validity
        var now = DateTime.UtcNow;
        var age = now - timestamp;

        // Reject if too old (expired)
        if (age.TotalSeconds > SignatureValiditySeconds)
        {
            return false;
        }

        // Reject if too far in future (clock skew protection)
        // Allow up to 10 seconds ahead to handle minor time differences
        if (age.TotalSeconds < -10)
        {
            return false;
        }

        try
        {
            // Recompute expected signature
            string expectedSignature = SignRequest(serverId, passkey, timestamp, nonce);

            // Constant-time comparison to prevent timing attacks
            // CryptographicOperations.FixedTimeEquals ensures comparison takes
            // same time regardless of where strings differ
            byte[] expectedBytes = Convert.FromBase64String(expectedSignature);
            byte[] providedBytes = Convert.FromBase64String(signature);

            // Lengths must match
            if (expectedBytes.Length != providedBytes.Length)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
        }
        catch (FormatException)
        {
            // Invalid Base64 string
            return false;
        }
        catch
        {
            // Any other error (e.g., crypto exceptions)
            return false;
        }
    }

    /// <summary>
    /// Validates signature with additional logging for diagnostics.
    /// Useful during development and debugging.
    /// </summary>
    /// <param name="serverId">Server ID from request</param>
    /// <param name="passkey">Current valid passkey</param>
    /// <param name="timestamp">Timestamp from request</param>
    /// <param name="nonce">Nonce from request</param>
    /// <param name="signature">Signature to validate</param>
    /// <param name="failureReason">Output parameter with failure reason if validation fails</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool ValidateSignatureWithReason(
        string serverId,
        string passkey,
        DateTime timestamp,
        string nonce,
        string signature,
        out string failureReason)
    {
        failureReason = string.Empty;

        if (string.IsNullOrEmpty(serverId))
        {
            failureReason = "ServerId is null or empty";
            return false;
        }

        if (string.IsNullOrEmpty(passkey))
        {
            failureReason = "Passkey is null or empty";
            return false;
        }

        if (string.IsNullOrEmpty(nonce))
        {
            failureReason = "Nonce is null or empty";
            return false;
        }

        if (string.IsNullOrEmpty(signature))
        {
            failureReason = "Signature is null or empty";
            return false;
        }

        // Check timestamp
        var now = DateTime.UtcNow;
        var age = now - timestamp;

        if (age.TotalSeconds > SignatureValiditySeconds)
        {
            failureReason = $"Signature expired (age: {age.TotalSeconds:F1}s, max: {SignatureValiditySeconds}s)";
            return false;
        }

        if (age.TotalSeconds < -10)
        {
            failureReason = $"Timestamp too far in future ({-age.TotalSeconds:F1}s ahead)";
            return false;
        }

        try
        {
            string expectedSignature = SignRequest(serverId, passkey, timestamp, nonce);
            byte[] expectedBytes = Convert.FromBase64String(expectedSignature);
            byte[] providedBytes = Convert.FromBase64String(signature);

            if (expectedBytes.Length != providedBytes.Length)
            {
                failureReason = "Signature length mismatch";
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
            {
                failureReason = "Signature mismatch (invalid passkey or tampered request)";
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            failureReason = "Invalid Base64 signature format";
            return false;
        }
        catch (Exception ex)
        {
            failureReason = $"Validation error: {ex.Message}";
            return false;
        }
    }
}
