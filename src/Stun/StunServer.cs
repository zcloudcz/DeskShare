using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace DeskShare.Stun;

/// <summary>
/// STUN server implementation according to RFC 5389.
/// Provides NAT binding discovery for WebRTC clients.
/// </summary>
public class StunServer : BackgroundService
{
    private readonly ILogger<StunServer> _logger;
    private readonly StunServerOptions _options;
    private UdpClient? _udpListener;
    private long _requestsProcessed;
    private long _responsesSent;
    private long _errorsEncountered;

    public StunServer(ILogger<StunServer> logger, StunServerOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new StunServerOptions();
    }

    /// <summary>
    /// Statistics about the STUN server.
    /// </summary>
    public StunServerStatistics GetStatistics() => new()
    {
        RequestsProcessed = _requestsProcessed,
        ResponsesSent = _responsesSent,
        ErrorsEncountered = _errorsEncountered,
        IsRunning = _udpListener != null
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting STUN server on port {Port}", _options.Port);

        try
        {
            _udpListener = new UdpClient(_options.Port);
            _logger.LogInformation("STUN server listening on UDP port {Port}", _options.Port);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Receive STUN request
                    var result = await _udpListener.ReceiveAsync(stoppingToken);
                    Interlocked.Increment(ref _requestsProcessed);

                    // Process in background to avoid blocking
                    _ = Task.Run(() => ProcessRequestAsync(result.Buffer, result.RemoteEndPoint), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _errorsEncountered);
                    _logger.LogError(ex, "Error receiving STUN request");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in STUN server");
            throw;
        }
        finally
        {
            _udpListener?.Dispose();
            _udpListener = null;
            _logger.LogInformation("STUN server stopped");
        }
    }

    private async Task ProcessRequestAsync(byte[] data, IPEndPoint remoteEndPoint)
    {
        try
        {
            _logger.LogDebug("Received STUN request from {RemoteEndPoint}, size: {Size} bytes",
                remoteEndPoint, data.Length);

            // Parse STUN message
            var request = StunMessage.Parse(data);

            if (request.MessageType == StunMessageType.BindingRequest)
            {
                // Create Binding Response
                var response = StunMessage.CreateBindingResponse(request.TransactionId, remoteEndPoint);

                // Send response
                var responseBytes = response.ToBytes();
                await _udpListener!.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

                Interlocked.Increment(ref _responsesSent);

                _logger.LogDebug("Sent STUN Binding Response to {RemoteEndPoint}, reflected address: {Address}:{Port}",
                    remoteEndPoint, remoteEndPoint.Address, remoteEndPoint.Port);
            }
            else
            {
                _logger.LogWarning("Unsupported STUN message type: {MessageType}", request.MessageType);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorsEncountered);
            _logger.LogError(ex, "Error processing STUN request from {RemoteEndPoint}", remoteEndPoint);
        }
    }

    public override void Dispose()
    {
        _udpListener?.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Configuration options for the STUN server.
/// </summary>
public class StunServerOptions
{
    /// <summary>
    /// UDP port for STUN server. Default is 3478 (standard STUN port).
    /// </summary>
    public int Port { get; set; } = 3478;

    /// <summary>
    /// Primary IP address to bind to. Null = bind to all interfaces.
    /// </summary>
    public IPAddress? BindAddress { get; set; } = null;

    /// <summary>
    /// Secondary IP address for CHANGE-REQUEST support (optional, deprecated).
    /// </summary>
    public IPAddress? AlternateAddress { get; set; } = null;

    /// <summary>
    /// Maximum number of concurrent requests to process.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 1000;
}

/// <summary>
/// Runtime statistics for the STUN server.
/// </summary>
public class StunServerStatistics
{
    public long RequestsProcessed { get; init; }
    public long ResponsesSent { get; init; }
    public long ErrorsEncountered { get; init; }
    public bool IsRunning { get; init; }
}
