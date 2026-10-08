using System.Collections.Concurrent;
using System.Diagnostics;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using IVideoSource = DeskShare.Core.Interfaces.IVideoSource;

namespace DeskShare.Core.Video;

/// <summary>
/// Multi-client WebRTC video source that supports multiple concurrent viewers.
/// Each client gets their own peer connection and encoder instance.
/// </summary>
/// <remarks>
/// This class manages multiple WebRTC connections simultaneously, allowing
/// multiple users to view the same screen at the same time. Each client has:
/// - Dedicated RTCPeerConnection for isolated connection state
/// - Dedicated VideoEncoderEndPoint for independent encoding
/// - Automatic cleanup when clients disconnect
/// </remarks>
public sealed class MultiClientVideoSource : IVideoSource, IDisposable
{
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly object _statsLock = new();
    private bool _initialized;
    private bool _disposed;

    private int _width;
    private int _height;
    private int _targetFrameRate;

    // Global statistics across all clients
    private long _totalFramesPushed;
    private long _totalFramesDropped;
    private readonly Stopwatch _statsTimer = Stopwatch.StartNew();

    /// <inheritdoc/>
    public bool IsActive => _initialized && !_disposed && _clients.Count > 0;

    /// <inheritdoc/>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Gets the number of currently connected clients.
    /// </summary>
    public int ConnectedClientCount => _clients.Count(c =>
        c.Value.PeerConnection.connectionState == RTCPeerConnectionState.connected);

    /// <summary>
    /// Represents a single client connection with dedicated WebRTC resources.
    /// </summary>
    private sealed class ClientConnection : IDisposable
    {
        public string ClientId { get; }
        public RTCPeerConnection PeerConnection { get; }
        public VideoEncoderEndPoint VideoEncoder { get; }
        public DateTime LastFrameTime { get; set; } = DateTime.MinValue;
        public long FramesPushed { get; set; }
        public long FramesDropped { get; set; }
        private bool _disposed;

        public ClientConnection(string clientId, RTCPeerConnection peerConnection, VideoEncoderEndPoint videoEncoder)
        {
            ClientId = clientId;
            PeerConnection = peerConnection;
            VideoEncoder = videoEncoder;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // Disconnect encoder from peer connection
            VideoEncoder.OnVideoSourceEncodedSample -= PeerConnection.SendVideo;

            // Close and dispose resources
            VideoEncoder.Dispose();
            PeerConnection.close();
            PeerConnection.Dispose();

            _disposed = true;
        }
    }

    /// <inheritdoc/>
    public bool Initialize(int width, int height, int frameRate)
    {
        if (_initialized)
            throw new InvalidOperationException("VideoSource is already initialized.");

        if (width <= 0 || width % 2 != 0)
            throw new ArgumentException("Width must be positive and even.", nameof(width));

        if (height <= 0 || height % 2 != 0)
            throw new ArgumentException("Height must be positive and even.", nameof(height));

        if (frameRate <= 0 || frameRate > 120)
            throw new ArgumentException("Frame rate must be between 1 and 120.", nameof(frameRate));

        _width = width;
        _height = height;
        _targetFrameRate = frameRate;
        _initialized = true;

        Log.Information("Initialized: {Width}x{Height} @ {TargetFrameRate}fps, ready for multiple concurrent clients", _width, _height, _targetFrameRate);

        return true;
    }

    /// <summary>
    /// Adds a new client connection with dedicated WebRTC resources.
    /// </summary>
    /// <param name="clientId">Unique identifier for the client.</param>
    /// <returns>The peer connection for this client.</returns>
    /// <exception cref="InvalidOperationException">If video source is not initialized.</exception>
    /// <exception cref="ArgumentException">If client already exists.</exception>
    public RTCPeerConnection AddClient(string clientId)
    {
        if (!_initialized)
            throw new InvalidOperationException("VideoSource is not initialized.");

        if (_clients.ContainsKey(clientId))
            throw new ArgumentException($"Client {clientId} already exists.", nameof(clientId));

        Log.Information("Adding new client: {ClientId}", clientId);

        // Create dedicated encoder for this client
        var videoEncoder = new VideoEncoderEndPoint();

        // Create peer connection with STUN servers for NAT traversal
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
            }
        };

        var peerConnection = new RTCPeerConnection(config);

        // Add video track to peer connection (send-only, we don't receive)
        var videoTrack = new MediaStreamTrack(
            EncoderFormatShim.AdvertisedFormats(videoEncoder),
            MediaStreamStatusEnum.SendOnly);

        peerConnection.addTrack(videoTrack);

        // Connect encoder output to peer connection
        // When encoder produces VP8 frames, they'll be sent via WebRTC
        videoEncoder.OnVideoSourceEncodedSample += peerConnection.SendVideo;

        // Handle codec negotiation - when client chooses a codec, configure encoder
        peerConnection.OnVideoFormatsNegotiated += (formats) =>
        {
            videoEncoder.SetVideoSourceFormat(EncoderFormatShim.ToEncoder(formats.First()));
        };

        // Handle connection state changes for automatic cleanup
        peerConnection.onconnectionstatechange += (state) =>
        {
            Log.Information("Client {ClientId} state: {State}", clientId, state);

            // Remove client if disconnected or failed
            if (state == RTCPeerConnectionState.disconnected ||
                state == RTCPeerConnectionState.failed ||
                state == RTCPeerConnectionState.closed)
            {
                RemoveClient(clientId);
            }
        };

        // Create and store client connection
        var client = new ClientConnection(clientId, peerConnection, videoEncoder);
        _clients.TryAdd(clientId, client);

        Log.Information("Client {ClientId} added. Total clients: {TotalClients}", clientId, _clients.Count);

        return peerConnection;
    }

    /// <summary>
    /// Removes a client connection and cleans up its resources.
    /// </summary>
    /// <param name="clientId">Client identifier to remove.</param>
    public void RemoveClient(string clientId)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            Log.Information("Removing client: {ClientId}", clientId);
            client.Dispose();
            Log.Information("Client {ClientId} removed. Remaining clients: {RemainingClients}", clientId, _clients.Count);
        }
    }

    /// <summary>
    /// Gets the peer connection for a specific client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <returns>Peer connection or null if client not found.</returns>
    public RTCPeerConnection? GetClientPeerConnection(string clientId)
    {
        return _clients.TryGetValue(clientId, out var client) ? client.PeerConnection : null;
    }

    /// <inheritdoc/>
    public bool PushFrame(VideoFrame frame)
    {
        if (!_initialized)
            throw new InvalidOperationException("VideoSource is not initialized.");

        if (_disposed)
            throw new ObjectDisposedException(nameof(MultiClientVideoSource));

        if (frame == null)
            throw new ArgumentNullException(nameof(frame));

        // If no clients connected, drop the frame
        if (_clients.IsEmpty)
        {
            lock (_statsLock)
            {
                _totalFramesDropped++;
            }
            return false;
        }

        var now = DateTime.UtcNow;
        var minFrameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFrameRate);
        var successCount = 0;
        var droppedCount = 0;

        // Push frame to all connected clients
        foreach (var kvp in _clients)
        {
            var client = kvp.Value;

            // Check if this specific client's peer connection is ready
            if (client.PeerConnection.connectionState != RTCPeerConnectionState.connected)
            {
                client.FramesDropped++;
                droppedCount++;
                continue;
            }

            // Per-client backpressure handling - respect target frame rate
            if (client.LastFrameTime != DateTime.MinValue)
            {
                var timeSinceLastFrame = now - client.LastFrameTime;
                if (timeSinceLastFrame < minFrameInterval * 0.8) // 20% tolerance
                {
                    client.FramesDropped++;
                    droppedCount++;
                    continue;
                }
            }

            client.LastFrameTime = now;

            try
            {
                // Convert VideoFrame to byte array in I420 format expected by SIPSorcery
                // Format: Y plane, then U plane, then V plane (contiguous)
                var totalSize = frame.YPlane.Length + frame.UPlane.Length + frame.VPlane.Length;
                var i420Data = new byte[totalSize];

                // Copy planes
                Buffer.BlockCopy(frame.YPlane, 0, i420Data, 0, frame.YPlane.Length);
                Buffer.BlockCopy(frame.UPlane, 0, i420Data, frame.YPlane.Length, frame.UPlane.Length);
                Buffer.BlockCopy(frame.VPlane, 0, i420Data, frame.YPlane.Length + frame.UPlane.Length, frame.VPlane.Length);

                // Push raw I420 frame to this client's encoder
                // The encoder will encode it to VP8 and trigger OnVideoSourceEncodedSample
                // which will automatically send it via the peer connection
                var durationMs = (uint)(1000.0 / _targetFrameRate);
                client.VideoEncoder.ExternalVideoSourceRawSample(
                    durationMs,
                    frame.Width,
                    frame.Height,
                    i420Data,
                    VideoPixelFormatsEnum.I420);

                client.FramesPushed++;
                successCount++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error pushing frame to client {ClientId}", kvp.Key);
                client.FramesDropped++;
                droppedCount++;
            }
        }

        // Update global statistics
        lock (_statsLock)
        {
            _totalFramesPushed += successCount;
            _totalFramesDropped += droppedCount;
        }

        // Return true if at least one client received the frame
        return successCount > 0;
    }

    /// <inheritdoc/>
    public VideoSourceStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            var elapsed = _statsTimer.Elapsed.TotalSeconds;
            var currentFps = elapsed > 0 ? _totalFramesPushed / elapsed / Math.Max(_clients.Count, 1) : 0;

            // Estimate bitrate from frames and client count
            long bitrateKbps = (long)(currentFps * _width * _height * 12 / 1000 * _clients.Count);

            return new VideoSourceStatistics
            {
                FramesPushed = _totalFramesPushed,
                FramesDropped = _totalFramesDropped,
                CurrentFps = currentFps,
                BitrateKbps = bitrateKbps,
                AverageFrameTimeMs = currentFps > 0 ? 1000.0 / currentFps : 0
            };
        }
    }

    /// <summary>
    /// Gets detailed statistics for all connected clients.
    /// </summary>
    /// <returns>Dictionary of client IDs to their statistics.</returns>
    public Dictionary<string, ClientStatistics> GetClientStatistics()
    {
        var stats = new Dictionary<string, ClientStatistics>();

        foreach (var kvp in _clients)
        {
            var client = kvp.Value;
            stats[kvp.Key] = new ClientStatistics
            {
                ClientId = kvp.Key,
                ConnectionState = client.PeerConnection.connectionState.ToString(),
                FramesPushed = client.FramesPushed,
                FramesDropped = client.FramesDropped,
                LastFrameTime = client.LastFrameTime
            };
        }

        return stats;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        var stats = GetStatistics();
        Log.Information("Disposing. Final stats: FramesPushed={FramesPushed}, FramesDropped={FramesDropped}, AvgFps={AvgFps:F2}",
            stats.FramesPushed, stats.FramesDropped, stats.CurrentFps);

        // Dispose all client connections
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
        _disposed = true;
    }
}

/// <summary>
/// Statistics for a single client connection.
/// </summary>
public sealed class ClientStatistics
{
    /// <summary>
    /// Unique client identifier.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Current WebRTC connection state.
    /// </summary>
    public string ConnectionState { get; set; } = string.Empty;

    /// <summary>
    /// Total frames successfully pushed to this client.
    /// </summary>
    public long FramesPushed { get; set; }

    /// <summary>
    /// Total frames dropped for this client.
    /// </summary>
    public long FramesDropped { get; set; }

    /// <summary>
    /// Timestamp of last frame sent to this client.
    /// </summary>
    public DateTime LastFrameTime { get; set; }
}
