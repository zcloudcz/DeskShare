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
    /// <summary>
    /// Generates a unique Server ID based on the first available MAC address.
    /// The ID is deterministic - the same machine will always generate the same ID.
    /// </summary>
    /// <returns>A persistent Server ID string</returns>
    public static string GenerateServerId()
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
