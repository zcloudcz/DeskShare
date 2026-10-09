using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace DeskShare.Core;

/// <summary>
/// Generates a persistent Server ID based on the machine's MAC address.
/// The Server ID will remain the same for the same machine.
/// </summary>
public static class ServerIdGenerator
{
    /// <summary>Prefix of every server identity; the signaling server refuses it as a viewer ClientId.</summary>
    public const string ServerIdPrefix = "server-";

    /// <summary>
    /// Stable viewer identity for this machine. Same hash as the Server ID but a different prefix, so a machine
    /// that both shares and views never uses one identity for both roles (the signaling server rejects that).
    /// </summary>
    public static string GenerateClientId() => "client-" + GenerateServerId()[ServerIdPrefix.Length..];

    /// <summary>Where the Server ID is persisted after it is first derived.</summary>
    public static string DefaultIdPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskShare", "server-id");

    /// <summary>
    /// Returns this machine's Server ID. It is derived from a MAC address once and then persisted, because the
    /// "first active physical adapter" changes with docks, Wi-Fi vs. cable or USB adapters; a changing ID would
    /// break saved and trusted connections and the server-side ownership claim.
    /// </summary>
    public static string GenerateServerId() => GetOrCreateServerId(DefaultIdPath);

    /// <summary>Reads the persisted ID from <paramref name="path"/>, or derives and saves a new one.</summary>
    public static string GetOrCreateServerId(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var stored = File.ReadAllText(path).Trim();
                if (stored.StartsWith(ServerIdPrefix, StringComparison.Ordinal) && stored.Length > ServerIdPrefix.Length)
                    return stored;
            }
        }
        catch (IOException) { /* unreadable file: derive a new ID below */ }
        catch (UnauthorizedAccessException) { }

        var id = DeriveServerIdFromMac();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
        }
        catch (IOException) { /* read-only profile: still usable, just not persisted */ }
        catch (UnauthorizedAccessException) { }
        return id;
    }

    /// <summary>Derives an ID from the first physical MAC address (machine name as fallback).</summary>
    private static string DeriveServerIdFromMac()
    {
        var macAddress = GetFirstPhysicalMacAddress();

        if (string.IsNullOrEmpty(macAddress))
        {
            // Fallback to machine name if no MAC address found
            macAddress = Environment.MachineName;
        }

        // Create SHA256 hash of MAC address
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(macAddress));

        // Take first 12 bytes and convert to base64
        var serverId = Convert.ToBase64String(hashBytes, 0, 12)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");

        return $"server-{serverId}";
    }

    /// <summary>
    /// Gets the first physical (non-virtual) MAC address from the system.
    /// </summary>
    /// <returns>MAC address as string, or empty string if none found</returns>
    private static string GetFirstPhysicalMacAddress()
    {
        try
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                    !IsVirtualAdapter(ni))
                .OrderBy(ni => ni.Name)
                .ToList();

            foreach (var ni in networkInterfaces)
            {
                var macAddress = ni.GetPhysicalAddress();
                if (macAddress != null && macAddress.GetAddressBytes().Length > 0)
                {
                    var macString = string.Join(":", macAddress.GetAddressBytes().Select(b => b.ToString("X2")));
                    if (!string.IsNullOrEmpty(macString) && macString != "00:00:00:00:00:00")
                    {
                        return macString;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Fallback handled by caller
        }

        return string.Empty;
    }

    /// <summary>
    /// Checks if a network adapter is virtual (VM, VPN, etc).
    /// </summary>
    private static bool IsVirtualAdapter(NetworkInterface ni)
    {
        var description = ni.Description.ToLowerInvariant();
        var name = ni.Name.ToLowerInvariant();

        var virtualKeywords = new[]
        {
            "virtual", "vmware", "vbox", "hyper-v", "vpn", "tap", "vethernet",
            "microsoft wi-fi direct", "bluetooth", "wireless hosted"
        };

        return virtualKeywords.Any(keyword =>
            description.Contains(keyword) || name.Contains(keyword));
    }
}
