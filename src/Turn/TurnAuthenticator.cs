using System.Security.Cryptography;
using System.Text;

namespace DeskShare.Turn;

/// <summary>
/// Authenticator for TURN long-term credentials (RFC 5766 Section 10).
/// </summary>
public class TurnAuthenticator
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _credentials = new();
    private readonly string _realm;

    public TurnAuthenticator(string realm = "remotedesktop.net")
    {
        _realm = realm;
    }

    public string Realm => _realm;

    /// <summary>
    /// Adds a user with password for authentication.
    /// </summary>
    public void AddUser(string username, string password)
    {
        _credentials[username] = password;
    }

    /// <summary>
    /// Removes a user.
    /// </summary>
    public void RemoveUser(string username)
    {
        _credentials.TryRemove(username, out _);
    }

    /// <summary>
    /// Generates a nonce for authentication challenge.
    /// </summary>
    public string GenerateNonce()
    {
        var randomBytes = new byte[16];
        RandomNumberGenerator.Fill(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    /// <summary>
    /// Validates MESSAGE-INTEGRITY attribute using long-term credentials.
    ///
    /// Key = MD5(username:realm:password)
    /// MESSAGE-INTEGRITY = HMAC-SHA1(key, STUN message)
    /// </summary>
    public bool ValidateMessageIntegrity(
        string username,
        byte[] messageIntegrityValue,
        byte[] stunMessageBytes)
    {
        if (!_credentials.TryGetValue(username, out var password))
            return false;

        // Compute key: MD5(username:realm:password)
        var keyInput = $"{username}:{_realm}:{password}";
        var keyBytes = MD5.HashData(Encoding.UTF8.GetBytes(keyInput));

        // Compute HMAC-SHA1 of the STUN message (excluding MESSAGE-INTEGRITY attribute itself)
        using var hmac = new HMACSHA1(keyBytes);
        var computedHash = hmac.ComputeHash(stunMessageBytes);

        return CryptographicOperations.FixedTimeEquals(messageIntegrityValue, computedHash);
    }

    /// <summary>
    /// Computes MESSAGE-INTEGRITY value for a response.
    /// </summary>
    public byte[] ComputeMessageIntegrity(string username, byte[] stunMessageBytes)
    {
        if (!_credentials.TryGetValue(username, out var password))
            throw new InvalidOperationException($"User '{username}' not found");

        // Compute key: MD5(username:realm:password)
        var keyInput = $"{username}:{_realm}:{password}";
        var keyBytes = MD5.HashData(Encoding.UTF8.GetBytes(keyInput));

        // Compute HMAC-SHA1
        using var hmac = new HMACSHA1(keyBytes);
        return hmac.ComputeHash(stunMessageBytes);
    }

    /// <summary>
    /// Checks if username exists.
    /// </summary>
    public bool UserExists(string username)
    {
        return _credentials.ContainsKey(username);
    }

    /// <summary>
    /// Gets user count.
    /// </summary>
    public int UserCount => _credentials.Count;
}
