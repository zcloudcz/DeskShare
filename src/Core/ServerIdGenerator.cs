using System.Security.Cryptography;

namespace DeskShare.Core;

/// <summary>
/// Generates and persists this installation's Server ID (short, shareable) and viewer Client ID (private).
/// </summary>
public static class ServerIdGenerator
{
    /// <summary>Prefix of every server identity; the signaling server refuses it as a viewer ClientId.</summary>
    public const string ServerIdPrefix = "server-";

    /// <summary>Prefix of every viewer identity.</summary>
    public const string ClientIdPrefix = "client-";

    /// <summary>Number of decimal digits after the prefix in a Server ID.</summary>
    private const int ServerIdDigits = 9;

    /// <summary>Number of hex characters after the prefix in a Client ID (128 bits).</summary>
    private const int ClientIdHexLength = 32;

    /// <summary>Where the Server ID is persisted.</summary>
    public static string DefaultIdPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskShare", "server-id");

    /// <summary>Where the Client ID is persisted.</summary>
    public static string DefaultClientIdPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskShare", "client-id");

    /// <summary>
    /// Stable viewer identity for this machine. It is random and independent of the Server ID: the Client ID acts
    /// as a bearer credential for "trusted" connections, so it must not be derivable from the Server ID that is
    /// shown on screen. The different prefix also keeps a machine that both shares and views from using one
    /// identity for both roles (the signaling server rejects that).
    /// </summary>
    public static string GenerateClientId() => GetOrCreateClientId(DefaultClientIdPath);

    /// <summary>Reads the persisted Client ID from <paramref name="path"/>, or creates and saves a new random one.</summary>
    public static string GetOrCreateClientId(string path)
    {
        var stored = ReadStored(path);
        if (stored != null &&
            stored.Length == ClientIdPrefix.Length + ClientIdHexLength &&
            stored.StartsWith(ClientIdPrefix, StringComparison.Ordinal) &&
            stored[ClientIdPrefix.Length..].All(char.IsAsciiHexDigitLower))
            return stored;

        var id = ClientIdPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(ClientIdHexLength / 2)).ToLowerInvariant();
        Persist(path, id);
        return id;
    }

    /// <summary>
    /// Returns this installation's Server ID: "server-" plus 9 random decimal digits (short enough to read out
    /// or type). It is created once and persisted, because a changing ID would break saved and trusted connections
    /// and the server-side ownership claim.
    /// </summary>
    public static string GenerateServerId() => GetOrCreateServerId(DefaultIdPath);

    /// <summary>
    /// Reads the persisted ID from <paramref name="path"/>, or creates and saves a new one. A stored value in an
    /// older format (derived from a MAC address) is discarded; old installs do not need to keep their ID.
    /// </summary>
    public static string GetOrCreateServerId(string path)
    {
        var stored = ReadStored(path);
        if (stored != null && TryGetServerIdDigits(stored, out _))
            return stored;

        return RegenerateServerId(path);
    }

    /// <summary>
    /// Creates, persists and returns a new random Server ID. Used when the signaling server reports that another
    /// installation already owns the current one (a collision is possible because the ID space is small).
    /// </summary>
    public static string RegenerateServerId(string path)
    {
        // 100_000_000..999_999_999 are exactly the 9-digit numbers, so the first digit is 1-9 and the draw is uniform.
        var id = ServerIdPrefix + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        Persist(path, id);
        return id;
    }

    /// <summary>Formats "server-123456789" as "123 456 789" for display. Any other value is returned unchanged.</summary>
    public static string FormatServerIdForDisplay(string serverId)
    {
        if (serverId == null || !TryGetServerIdDigits(serverId, out var digits))
            return serverId!;

        return $"{digits[..3]} {digits[3..6]} {digits[6..]}";
    }

    /// <summary>
    /// Turns what a user typed ("123 456 789", "123456789", "123-456-789", "server-123456789") into the wire format
    /// "server-123456789". Anything else is returned trimmed and otherwise unchanged, so manually typed legacy IDs
    /// still reach the server as entered.
    /// </summary>
    public static string NormalizeServerIdInput(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim();

        var rest = trimmed.StartsWith(ServerIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[ServerIdPrefix.Length..]
            : trimmed;
        var digits = new string(rest.Where(c => c is not (' ' or '-')).ToArray());

        return digits.Length == ServerIdDigits && digits.All(char.IsAsciiDigit) && digits[0] != '0'
            ? ServerIdPrefix + digits
            : trimmed;
    }

    /// <summary>True for "server-" followed by exactly 9 digits, the first being 1-9.</summary>
    private static bool TryGetServerIdDigits(string serverId, out string digits)
    {
        digits = string.Empty;
        if (!serverId.StartsWith(ServerIdPrefix, StringComparison.Ordinal))
            return false;

        var rest = serverId[ServerIdPrefix.Length..];
        if (rest.Length != ServerIdDigits || !rest.All(char.IsAsciiDigit) || rest[0] == '0')
            return false;

        digits = rest;
        return true;
    }

    private static string? ReadStored(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException) { return null; /* unreadable file: create a new ID */ }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Persist(string path, string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
        }
        catch (IOException) { /* read-only profile: still usable, just not persisted */ }
        catch (UnauthorizedAccessException) { }
    }
}
