using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace DeskShare.Turn;

/// <summary>
/// Engine for relaying data between clients and peers through TURN allocations.
/// </summary>
public class RelayEngine
{
    private readonly ILogger<RelayEngine> _logger;
    private readonly Dictionary<string, TurnAllocation> _allocations = new();
    private readonly object _lock = new();

    public RelayEngine(ILogger<RelayEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a new allocation and starts relay listener.
    /// </summary>
    public async Task<TurnAllocation> CreateAllocationAsync(
        IPEndPoint clientEndPoint,
        string username,
        string realm,
        CancellationToken cancellationToken)
    {
        // Allocate a relay socket (bind to any available port)
        var relaySocket = new UdpClient(0, AddressFamily.InterNetwork);
        var relayedEndPoint = (IPEndPoint)relaySocket.Client.LocalEndPoint!;

        var allocation = new TurnAllocation(
            clientEndPoint,
            relayedEndPoint,
            relaySocket,
            username,
            realm);

        lock (_lock)
        {
            _allocations[allocation.AllocationId] = allocation;
        }

        _logger.LogInformation(
            "Created TURN allocation {AllocationId} for {Username}: {ClientEndPoint} -> {RelayedEndPoint}",
            allocation.AllocationId, username, clientEndPoint, relayedEndPoint);

        // Start relay listener for this allocation
        _ = Task.Run(() => RelayListenerAsync(allocation, cancellationToken), cancellationToken);

        return allocation;
    }

    /// <summary>
    /// Removes an allocation and closes relay socket.
    /// </summary>
    public void RemoveAllocation(string allocationId)
    {
        TurnAllocation? allocation;

        lock (_lock)
        {
            if (!_allocations.TryGetValue(allocationId, out allocation))
                return;

            _allocations.Remove(allocationId);
        }

        allocation.RelaySocket.Dispose();

        _logger.LogInformation(
            "Removed TURN allocation {AllocationId} for {Username}",
            allocationId, allocation.Username);
    }

    /// <summary>
    /// Finds allocation by client endpoint.
    /// </summary>
    public TurnAllocation? FindAllocationByClient(IPEndPoint clientEndPoint)
    {
        lock (_lock)
        {
            return _allocations.Values.FirstOrDefault(a =>
                a.ClientEndPoint.Address.Equals(clientEndPoint.Address) &&
                a.ClientEndPoint.Port == clientEndPoint.Port);
        }
    }

    /// <summary>
    /// Sends data from client to peer through relay.
    /// </summary>
    public async Task<bool> SendToPeerAsync(
        TurnAllocation allocation,
        IPEndPoint peerEndPoint,
        byte[] data,
        CancellationToken cancellationToken)
    {
        // Check permission
        if (!allocation.HasPermission(peerEndPoint.Address))
        {
            _logger.LogWarning(
                "Permission denied for {AllocationId} to send to {PeerEndPoint}",
                allocation.AllocationId, peerEndPoint);
            return false;
        }

        try
        {
            // Send data through relay socket to peer
            await allocation.RelaySocket.SendAsync(data, data.Length, peerEndPoint);

            allocation.Statistics.BytesSentToPeer += data.Length;
            allocation.Statistics.PacketsSentToPeer++;

            _logger.LogTrace(
                "Relayed {Bytes} bytes from client {ClientEndPoint} to peer {PeerEndPoint}",
                data.Length, allocation.ClientEndPoint, peerEndPoint);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to relay data from {ClientEndPoint} to {PeerEndPoint}",
                allocation.ClientEndPoint, peerEndPoint);
            return false;
        }
    }

    /// <summary>
    /// Listens for data from peers and relays back to client.
    /// </summary>
    private async Task RelayListenerAsync(TurnAllocation allocation, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Started relay listener for allocation {AllocationId}", allocation.AllocationId);

        try
        {
            while (!cancellationToken.IsCancellationRequested && !allocation.IsExpired())
            {
                // Receive data from any peer
                var result = await allocation.RelaySocket.ReceiveAsync(cancellationToken);

                var peerEndPoint = result.RemoteEndPoint;
                var data = result.Buffer;

                // Check if we have permission for this peer
                if (!allocation.HasPermission(peerEndPoint.Address))
                {
                    _logger.LogDebug(
                        "Dropping packet from unauthorized peer {PeerEndPoint} for allocation {AllocationId}",
                        peerEndPoint, allocation.AllocationId);
                    continue;
                }

                allocation.Statistics.BytesReceivedFromPeer += data.Length;
                allocation.Statistics.PacketsReceivedFromPeer++;

                _logger.LogTrace(
                    "Received {Bytes} bytes from peer {PeerEndPoint}, relaying to client {ClientEndPoint}",
                    data.Length, peerEndPoint, allocation.ClientEndPoint);

                // TODO: Send DATA indication back to client through TURN server
                // This will be handled by TurnServer.SendDataIndicationAsync()
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in relay listener for allocation {AllocationId}", allocation.AllocationId);
        }
        finally
        {
            _logger.LogDebug("Stopped relay listener for allocation {AllocationId}", allocation.AllocationId);
        }
    }

    /// <summary>
    /// Cleans up expired allocations.
    /// </summary>
    public void CleanupExpiredAllocations()
    {
        var expiredIds = new List<string>();

        lock (_lock)
        {
            foreach (var allocation in _allocations.Values)
            {
                if (allocation.IsExpired())
                {
                    expiredIds.Add(allocation.AllocationId);
                }
            }
        }

        foreach (var id in expiredIds)
        {
            RemoveAllocation(id);
        }

        if (expiredIds.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired TURN allocations", expiredIds.Count);
        }
    }

    /// <summary>
    /// Gets statistics for all allocations.
    /// </summary>
    public TurnRelayStatistics GetStatistics()
    {
        lock (_lock)
        {
            return new TurnRelayStatistics
            {
                ActiveAllocations = _allocations.Count,
                TotalBytesSent = _allocations.Values.Sum(a => a.Statistics.BytesSentToPeer),
                TotalBytesReceived = _allocations.Values.Sum(a => a.Statistics.BytesReceivedFromPeer),
                TotalPacketsSent = _allocations.Values.Sum(a => a.Statistics.PacketsSentToPeer),
                TotalPacketsReceived = _allocations.Values.Sum(a => a.Statistics.PacketsReceivedFromPeer)
            };
        }
    }

    /// <summary>
    /// Gets all active allocations (for debugging/monitoring).
    /// </summary>
    public List<TurnAllocation> GetAllAllocations()
    {
        lock (_lock)
        {
            return new List<TurnAllocation>(_allocations.Values);
        }
    }
}

/// <summary>
/// Statistics for the relay engine.
/// </summary>
public class TurnRelayStatistics
{
    public int ActiveAllocations { get; init; }
    public long TotalBytesSent { get; init; }
    public long TotalBytesReceived { get; init; }
    public long TotalPacketsSent { get; init; }
    public long TotalPacketsReceived { get; init; }
}
