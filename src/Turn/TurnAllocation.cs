using System.Net;
using System.Net.Sockets;

namespace DeskShare.Turn;

/// <summary>
/// Represents a TURN allocation - a relay address allocated for a client.
/// RFC 5766 Section 5: Allocations
/// </summary>
public class TurnAllocation
{
    /// <summary>
    /// Unique identifier for this allocation.
    /// </summary>
    public string AllocationId { get; }

    /// <summary>
    /// Client's 5-tuple (source IP, source port, transport, destination IP, destination port).
    /// </summary>
    public IPEndPoint ClientEndPoint { get; }

    /// <summary>
    /// Relayed transport address (server-side address used for relay).
    /// </summary>
    public IPEndPoint RelayedEndPoint { get; }

    /// <summary>
    /// UDP socket for relaying data.
    /// </summary>
    public UdpClient RelaySocket { get; }

    /// <summary>
    /// Username from long-term credential (for authentication).
    /// </summary>
    public string Username { get; }

    /// <summary>
    /// Realm from authentication.
    /// </summary>
    public string Realm { get; }

    /// <summary>
    /// Time when allocation was created.
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Time when allocation expires.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Default lifetime for TURN allocations (10 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum lifetime for TURN allocations (1 hour).
    /// </summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Permissions: which peer addresses are allowed to send data to this client.
    /// Key = peer IP address, Value = expiration time
    /// </summary>
    public Dictionary<IPAddress, DateTime> Permissions { get; } = new();

    /// <summary>
    /// Channel bindings: optimized relay for specific peers.
    /// Key = channel number (0x4000-0x7FFF), Value = peer endpoint
    /// </summary>
    public Dictionary<ushort, TurnChannelBinding> Channels { get; } = new();

    /// <summary>
    /// Statistics for this allocation.
    /// </summary>
    public TurnAllocationStatistics Statistics { get; } = new();

    public TurnAllocation(
        IPEndPoint clientEndPoint,
        IPEndPoint relayedEndPoint,
        UdpClient relaySocket,
        string username,
        string realm)
    {
        AllocationId = Guid.NewGuid().ToString();
        ClientEndPoint = clientEndPoint;
        RelayedEndPoint = relayedEndPoint;
        RelaySocket = relaySocket;
        Username = username;
        Realm = realm;
        CreatedAt = DateTime.UtcNow;
        ExpiresAt = DateTime.UtcNow.Add(DefaultLifetime);
    }

    /// <summary>
    /// Checks if allocation has expired.
    /// </summary>
    public bool IsExpired() => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Refreshes the allocation lifetime.
    /// </summary>
    public void Refresh(TimeSpan? lifetime = null)
    {
        var requestedLifetime = lifetime ?? DefaultLifetime;
        var actualLifetime = requestedLifetime > MaxLifetime ? MaxLifetime : requestedLifetime;

        ExpiresAt = DateTime.UtcNow.Add(actualLifetime);
    }

    /// <summary>
    /// Checks if a peer address has permission to send data.
    /// </summary>
    public bool HasPermission(IPAddress peerAddress)
    {
        if (Permissions.TryGetValue(peerAddress, out var expiresAt))
        {
            if (DateTime.UtcNow < expiresAt)
                return true;

            // Permission expired, remove it
            Permissions.Remove(peerAddress);
        }

        return false;
    }

    /// <summary>
    /// Creates or refreshes permission for a peer address.
    /// Permissions expire after 5 minutes.
    /// </summary>
    public void CreatePermission(IPAddress peerAddress)
    {
        Permissions[peerAddress] = DateTime.UtcNow.AddMinutes(5);
    }

    /// <summary>
    /// Binds a channel to a peer endpoint for optimized relay.
    /// </summary>
    public bool BindChannel(ushort channelNumber, IPEndPoint peerEndPoint)
    {
        // Channel numbers must be in range 0x4000-0x7FFF
        if (channelNumber < 0x4000 || channelNumber > 0x7FFF)
            return false;

        // Check if channel is already bound
        if (Channels.ContainsKey(channelNumber))
            return false;

        Channels[channelNumber] = new TurnChannelBinding
        {
            ChannelNumber = channelNumber,
            PeerEndPoint = peerEndPoint,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        return true;
    }

    /// <summary>
    /// Finds channel binding by peer endpoint.
    /// </summary>
    public ushort? FindChannelByPeer(IPEndPoint peerEndPoint)
    {
        foreach (var kvp in Channels)
        {
            if (kvp.Value.PeerEndPoint.Equals(peerEndPoint) && !kvp.Value.IsExpired())
                return kvp.Key;
        }

        return null;
    }

    /// <summary>
    /// Gets channel binding by channel number.
    /// </summary>
    public TurnChannelBinding? GetChannel(ushort channelNumber)
    {
        if (Channels.TryGetValue(channelNumber, out var binding) && !binding.IsExpired())
            return binding;

        // Remove expired binding
        if (Channels.ContainsKey(channelNumber))
            Channels.Remove(channelNumber);

        return null;
    }
}

/// <summary>
/// Channel binding for optimized data relay.
/// </summary>
public class TurnChannelBinding
{
    public ushort ChannelNumber { get; set; }
    public IPEndPoint PeerEndPoint { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public bool IsExpired() => DateTime.UtcNow >= ExpiresAt;
}

/// <summary>
/// Statistics for a TURN allocation.
/// </summary>
public class TurnAllocationStatistics
{
    public long BytesSentToPeer { get; set; }
    public long BytesReceivedFromPeer { get; set; }
    public long PacketsSentToPeer { get; set; }
    public long PacketsReceivedFromPeer { get; set; }
}
