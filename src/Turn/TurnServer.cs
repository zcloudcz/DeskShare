using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeskShare.Stun;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace DeskShare.Turn;

/// <summary>
/// TURN server implementation according to RFC 5766.
/// Provides relay functionality for clients behind restrictive NATs.
/// </summary>
public class TurnServer : BackgroundService
{
    private readonly ILogger<TurnServer> _logger;
    private readonly TurnServerOptions _options;
    private readonly TurnAuthenticator _authenticator;
    private readonly RelayEngine _relayEngine;
    private UdpClient? _udpListener;
    private long _requestsProcessed;
    private long _allocationsCreated;
    private long _errorsEncountered;

    public TurnServer(
        ILogger<TurnServer> logger,
        ILogger<RelayEngine> relayLogger,
        TurnServerOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new TurnServerOptions();
        _authenticator = new TurnAuthenticator(_options.Realm);
        _relayEngine = new RelayEngine(relayLogger);

        // Add default test users if configured
        if (_options.EnableTestUser)
        {
            _authenticator.AddUser("testuser", "testpass");
            _logger.LogWarning("TURN test user enabled: testuser/testpass (disable in production!)");
        }
    }

    /// <summary>
    /// Adds a user for TURN authentication.
    /// </summary>
    public void AddUser(string username, string password)
    {
        _authenticator.AddUser(username, password);
        _logger.LogInformation("Added TURN user: {Username}", username);
    }

    /// <summary>
    /// Statistics about the TURN server.
    /// </summary>
    public TurnServerStatistics GetStatistics()
    {
        var relayStats = _relayEngine.GetStatistics();

        return new TurnServerStatistics
        {
            RequestsProcessed = _requestsProcessed,
            AllocationsCreated = _allocationsCreated,
            ActiveAllocations = relayStats.ActiveAllocations,
            ErrorsEncountered = _errorsEncountered,
            TotalByteRelayed = relayStats.TotalBytesSent + relayStats.TotalBytesReceived,
            IsRunning = _udpListener != null
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting TURN server on port {Port}, realm: {Realm}",
            _options.Port, _options.Realm);

        try
        {
            _udpListener = new UdpClient(_options.Port);
            _logger.LogInformation("TURN server listening on UDP port {Port}", _options.Port);

            // Start cleanup task for expired allocations
            _ = Task.Run(() => CleanupTaskAsync(stoppingToken), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpListener.ReceiveAsync(stoppingToken);
                    Interlocked.Increment(ref _requestsProcessed);

                    // Process in background
                    _ = Task.Run(() => ProcessRequestAsync(result.Buffer, result.RemoteEndPoint, stoppingToken), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _errorsEncountered);
                    _logger.LogError(ex, "Error receiving TURN request");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in TURN server");
            throw;
        }
        finally
        {
            _udpListener?.Dispose();
            _udpListener = null;
            _logger.LogInformation("TURN server stopped");
        }
    }

    private async Task ProcessRequestAsync(byte[] data, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        try
        {
            // Check if this is a STUN/TURN message (magic cookie = 0x2112A442)
            if (data.Length < 20)
                return;

            var magicCookie = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
            if (magicCookie != StunMessage.MagicCookie)
                return;

            // Parse STUN message
            var request = StunMessage.Parse(data);

            _logger.LogDebug("Received TURN request: {MessageType} from {RemoteEndPoint}",
                request.MessageType, remoteEndPoint);

            // Handle different TURN message types
            switch (request.MessageType)
            {
                case StunMessageType.BindingRequest:
                    // STUN Binding Request (for connectivity checks)
                    await HandleBindingRequestAsync(request, remoteEndPoint, cancellationToken);
                    break;

                case (StunMessageType)0x0003: // Allocate Request
                    await HandleAllocateRequestAsync(request, remoteEndPoint, cancellationToken);
                    break;

                case (StunMessageType)0x0004: // Refresh Request
                    await HandleRefreshRequestAsync(request, remoteEndPoint, cancellationToken);
                    break;

                case (StunMessageType)0x0006: // Send Indication
                    await HandleSendIndicationAsync(request, remoteEndPoint, cancellationToken);
                    break;

                case (StunMessageType)0x0008: // CreatePermission Request
                    await HandleCreatePermissionRequestAsync(request, remoteEndPoint, cancellationToken);
                    break;

                case (StunMessageType)0x0009: // ChannelBind Request
                    await HandleChannelBindRequestAsync(request, remoteEndPoint, cancellationToken);
                    break;

                default:
                    _logger.LogWarning("Unsupported TURN message type: {MessageType}", request.MessageType);
                    break;
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorsEncountered);
            _logger.LogError(ex, "Error processing TURN request from {RemoteEndPoint}", remoteEndPoint);
        }
    }

    private async Task HandleBindingRequestAsync(StunMessage request, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        // Simple STUN Binding Response (same as STUN server)
        var response = StunMessage.CreateBindingResponse(request.TransactionId, remoteEndPoint);
        var responseBytes = response.ToBytes();

        await _udpListener!.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

        _logger.LogDebug("Sent STUN Binding Response to {RemoteEndPoint}", remoteEndPoint);
    }

    private async Task HandleAllocateRequestAsync(StunMessage request, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing Allocate Request from {RemoteEndPoint}", remoteEndPoint);

        // TODO: Extract USERNAME, REALM, NONCE, MESSAGE-INTEGRITY attributes
        // TODO: Validate authentication
        // TODO: For now, simplified version without full auth

        try
        {
            // Create allocation
            var allocation = await _relayEngine.CreateAllocationAsync(
                remoteEndPoint,
                "anonymous", // TODO: Extract from USERNAME attribute
                _options.Realm,
                cancellationToken);

            Interlocked.Increment(ref _allocationsCreated);

            // Build Allocate Success Response
            var response = new StunMessage
            {
                MessageType = (StunMessageType)0x0103, // Allocate Success Response
                TransactionId = request.TransactionId
            };

            // Add XOR-RELAYED-ADDRESS attribute (0x0016)
            var relayedAddress = CreateXorRelayedAddressAttribute(allocation.RelayedEndPoint, request.TransactionId);
            response.Attributes.Add(relayedAddress);

            // Add LIFETIME attribute (0x000D)
            var lifetime = CreateLifetimeAttribute((int)TurnAllocation.DefaultLifetime.TotalSeconds);
            response.Attributes.Add(lifetime);

            var responseBytes = response.ToBytes();
            await _udpListener!.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

            _logger.LogInformation(
                "Allocated relay address {RelayedEndPoint} for client {ClientEndPoint}",
                allocation.RelayedEndPoint, remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create allocation for {RemoteEndPoint}", remoteEndPoint);

            // Send error response
            var errorResponse = new StunMessage
            {
                MessageType = (StunMessageType)0x0113, // Allocate Error Response
                TransactionId = request.TransactionId
            };

            var errorBytes = errorResponse.ToBytes();
            await _udpListener!.SendAsync(errorBytes, errorBytes.Length, remoteEndPoint);
        }
    }

    private async Task HandleRefreshRequestAsync(StunMessage request, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var allocation = _relayEngine.FindAllocationByClient(remoteEndPoint);

        if (allocation == null)
        {
            _logger.LogWarning("Refresh Request from {RemoteEndPoint} with no allocation", remoteEndPoint);
            // Send error response
            return;
        }

        // Refresh allocation
        allocation.Refresh();

        // Send Refresh Success Response
        var response = new StunMessage
        {
            MessageType = (StunMessageType)0x0104, // Refresh Success Response
            TransactionId = request.TransactionId
        };

        // Add LIFETIME attribute
        var lifetime = CreateLifetimeAttribute((int)(allocation.ExpiresAt - DateTime.UtcNow).TotalSeconds);
        response.Attributes.Add(lifetime);

        var responseBytes = response.ToBytes();
        await _udpListener!.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

        _logger.LogDebug("Refreshed allocation for {ClientEndPoint}", remoteEndPoint);
    }

    private async Task HandleSendIndicationAsync(StunMessage request, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var allocation = _relayEngine.FindAllocationByClient(remoteEndPoint);

        if (allocation == null)
        {
            _logger.LogWarning("Send Indication from {RemoteEndPoint} with no allocation", remoteEndPoint);
            return;
        }

        // TODO: Extract XOR-PEER-ADDRESS and DATA attributes
        // TODO: Relay data to peer

        _logger.LogTrace("Processed Send Indication from {ClientEndPoint}", remoteEndPoint);
    }

    private async Task HandleCreatePermissionRequestAsync(StunMessage request, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var allocation = _relayEngine.FindAllocationByClient(remoteEndPoint);

        if (allocation == null)
        {
            _logger.LogWarning("CreatePermission Request from {RemoteEndPoint} with no allocation", remoteEndPoint);
            return;
        }

        // TODO: Extract XOR-PEER-ADDRESS attribute(s)
        // For now, simplified: grant permission to all

        // Send CreatePermission Success Response
        var response = new StunMessage
        {
            MessageType = (StunMessageType)0x0108, // CreatePermission Success Response
            TransactionId = request.TransactionId
        };

        var responseBytes = response.ToBytes();
        await _udpListener!.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

        _logger.LogDebug("Created permission for {ClientEndPoint}", remoteEndPoint);
    }

    private async Task HandleChannelBindRequestAsync(StunMessage request, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var allocation = _relayEngine.FindAllocationByClient(remoteEndPoint);

        if (allocation == null)
        {
            _logger.LogWarning("ChannelBind Request from {RemoteEndPoint} with no allocation", remoteEndPoint);
            return;
        }

        // TODO: Extract CHANNEL-NUMBER and XOR-PEER-ADDRESS attributes

        // Send ChannelBind Success Response
        var response = new StunMessage
        {
            MessageType = (StunMessageType)0x0109, // ChannelBind Success Response
            TransactionId = request.TransactionId
        };

        var responseBytes = response.ToBytes();
        await _udpListener!.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

        _logger.LogDebug("Bound channel for {ClientEndPoint}", remoteEndPoint);
    }

    private StunAttribute CreateXorRelayedAddressAttribute(IPEndPoint relayedEndPoint, byte[] transactionId)
    {
        var addressBytes = relayedEndPoint.Address.GetAddressBytes();
        var attrValue = new byte[8]; // IPv4: 8 bytes (family + port + address)

        attrValue[0] = 0x00;
        attrValue[1] = 0x01; // IPv4

        // XOR port with magic cookie
        var port = (ushort)relayedEndPoint.Port;
        var xorPort = (ushort)(port ^ (StunMessage.MagicCookie >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(attrValue.AsSpan(2, 2), xorPort);

        // XOR address with magic cookie
        var magicCookieBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(magicCookieBytes, StunMessage.MagicCookie);

        for (int i = 0; i < 4; i++)
        {
            attrValue[4 + i] = (byte)(addressBytes[i] ^ magicCookieBytes[i]);
        }

        return new StunAttribute
        {
            Type = StunAttributeType.XorRelayedAddress,
            Value = attrValue
        };
    }

    private StunAttribute CreateLifetimeAttribute(int seconds)
    {
        var attrValue = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(attrValue, seconds);

        return new StunAttribute
        {
            Type = StunAttributeType.Lifetime,
            Value = attrValue
        };
    }

    private async Task CleanupTaskAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                _relayEngine.CleanupExpiredAllocations();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup task");
            }
        }
    }

    public override void Dispose()
    {
        _udpListener?.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Configuration options for the TURN server.
/// </summary>
public class TurnServerOptions
{
    /// <summary>
    /// UDP port for TURN server. Default is 3479 (3478 is typically used by STUN).
    /// Note: TURN and STUN can share the same port in production, but this requires
    /// a unified server implementation. This default uses a separate port for simplicity.
    /// </summary>
    public int Port { get; set; } = 3479;

    /// <summary>
    /// Realm for long-term credentials.
    /// </summary>
    public string Realm { get; set; } = "remotedesktop.net";

    /// <summary>
    /// Enable test user (testuser/testpass) for development. Disable in production!
    /// </summary>
    public bool EnableTestUser { get; set; } = false;

    /// <summary>
    /// Primary IP address to bind to.
    /// </summary>
    public IPAddress? BindAddress { get; set; } = null;

    /// <summary>
    /// Minimum port for relay allocations.
    /// </summary>
    public int MinRelayPort { get; set; } = 49152;

    /// <summary>
    /// Maximum port for relay allocations.
    /// </summary>
    public int MaxRelayPort { get; set; } = 65535;
}

/// <summary>
/// Runtime statistics for the TURN server.
/// </summary>
public class TurnServerStatistics
{
    public long RequestsProcessed { get; init; }
    public long AllocationsCreated { get; init; }
    public int ActiveAllocations { get; init; }
    public long ErrorsEncountered { get; init; }
    public long TotalByteRelayed { get; init; }
    public bool IsRunning { get; init; }
}
