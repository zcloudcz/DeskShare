using System.Diagnostics;
using DeskShare.Common.Interfaces;
using DeskShare.Common.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using IVideoSource = DeskShare.Common.Interfaces.IVideoSource;

namespace DeskShare.ScreenSenderApp.Video;

/// <summary>
/// WebRTC video source implementation using SIPSorcery library.
/// Provides real peer-to-peer video streaming over WebRTC.
/// Uses VideoEncoderEndPoint for VP8 encoding.
/// </summary>
public sealed class SIPSorceryVideoSource : IVideoSource
{
    private readonly object _statsLock = new();
    private bool _initialized;
    private bool _disposed;

    private int _width;
    private int _height;
    private int _targetFrameRate;
    private RTCPeerConnection? _peerConnection;
    private VideoEncoderEndPoint? _videoEncoder;

    // Statistics
    private long _framesPushed;
    private long _framesDropped;
    private readonly Stopwatch _statsTimer = Stopwatch.StartNew();
    private DateTime _lastFrameTime = DateTime.MinValue;

    // Keyframe forcing for low latency
    private int _framesSinceKeyframe = 0;
    private const int KeyframeInterval = 5; // Force keyframe every 5 frames

    /// <inheritdoc/>
    public bool IsActive => _initialized && !_disposed && _peerConnection?.connectionState == RTCPeerConnectionState.connected;

    /// <inheritdoc/>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Gets the underlying peer connection for signaling integration.
    /// </summary>
    public RTCPeerConnection? PeerConnection => _peerConnection;

    /// <summary>
    /// Initializes a new instance of the <see cref="SIPSorceryVideoSource"/> class.
    /// </summary>
    public SIPSorceryVideoSource()
    {
    }

    /// <summary>
    /// Resets the peer connection for a new client connection.
    /// Phase 1: Only one client can connect at a time. This method disconnects the
    /// previous client and creates a fresh peer connection for the new client.
    /// </summary>
    /// <remarks>
    /// This is necessary because RTCPeerConnection is stateful and can only maintain
    /// one connection. A future improvement would be to support multiple concurrent
    /// clients by managing multiple peer connections.
    /// </remarks>
    public void ResetPeerConnection()
    {
        if (!_initialized)
            return;

        Console.WriteLine("[SIPSorceryVideoSource] Resetting peer connection for new client...");

        // Reset keyframe counter to force immediate keyframe for new client
        _framesSinceKeyframe = KeyframeInterval; // Will trigger keyframe on next PushFrame

        // Disconnect encoder from old peer connection
        if (_videoEncoder != null && _peerConnection != null)
        {
            _videoEncoder.OnVideoSourceEncodedSample -= _peerConnection.SendVideo;
        }

        // Close and dispose old peer connection
        _peerConnection?.close();
        _peerConnection?.Dispose();

        // Create new peer connection with STUN servers for NAT traversal
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
            }
        };

        _peerConnection = new RTCPeerConnection(config);

        // Add video track to peer connection (send-only, we don't receive)
        var videoTrack = new MediaStreamTrack(
            _videoEncoder!.GetVideoSourceFormats(),
            MediaStreamStatusEnum.SendOnly);

        _peerConnection.addTrack(videoTrack);

        // Connect encoder output to peer connection
        // When encoder produces VP8 frames, they'll be sent via WebRTC
        _videoEncoder.OnVideoSourceEncodedSample += _peerConnection.SendVideo;

        // Handle codec negotiation - when client chooses a codec, configure encoder
        _peerConnection.OnVideoFormatsNegotiated += (formats) =>
        {
            _videoEncoder.SetVideoSourceFormat(formats.First());
        };

        Console.WriteLine("[SIPSorceryVideoSource] Peer connection reset complete");
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

        // Create video encoder endpoint
        // Note: We'll force keyframes every 5 frames in PushFrame for low latency
        _videoEncoder = new VideoEncoderEndPoint();

        // Create peer connection with configuration
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
            }
        };

        _peerConnection = new RTCPeerConnection(config);

        // Create video track with formats from encoder
        var videoTrack = new MediaStreamTrack(
            _videoEncoder.GetVideoSourceFormats(),
            MediaStreamStatusEnum.SendOnly);

        _peerConnection.addTrack(videoTrack);

        // Wire up the encoder to peer connection
        _videoEncoder.OnVideoSourceEncodedSample += _peerConnection.SendVideo;

        // Handle format negotiation
        _peerConnection.OnVideoFormatsNegotiated += (formats) =>
        {
            _videoEncoder.SetVideoSourceFormat(formats.First());
        };

        _initialized = true;

        Console.WriteLine($"[SIPSorceryVideoSource] Initialized: {_width}x{_height} @ {_targetFrameRate}fps");
        Console.WriteLine($"[SIPSorceryVideoSource] Codec: VP8, Format: I420");

        return true;
    }

    /// <inheritdoc/>
    public bool PushFrame(VideoFrame frame)
    {
        if (!_initialized)
            throw new InvalidOperationException("VideoSource is not initialized.");

        if (_disposed)
            throw new ObjectDisposedException(nameof(SIPSorceryVideoSource));

        if (frame == null)
            throw new ArgumentNullException(nameof(frame));

        // Check if peer connection is ready
        if (_peerConnection?.connectionState != RTCPeerConnectionState.connected)
        {
            lock (_statsLock)
            {
                _framesDropped++;
            }
            return false;
        }

        // Backpressure handling - respect target frame rate
        var now = DateTime.UtcNow;
        var minFrameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFrameRate);

        if (_lastFrameTime != DateTime.MinValue)
        {
            var timeSinceLastFrame = now - _lastFrameTime;
            if (timeSinceLastFrame < minFrameInterval * 0.8) // 20% tolerance
            {
                lock (_statsLock)
                {
                    _framesDropped++;
                }
                return false;
            }
        }

        _lastFrameTime = now;

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

            // Force keyframe every N frames for low latency decoding
            // This ensures decoder can start displaying video quickly
            if (_framesSinceKeyframe >= KeyframeInterval)
            {
                _videoEncoder!.ForceKeyFrame();
                _framesSinceKeyframe = 0;
                Console.WriteLine($"[SIPSorceryVideoSource] Forced keyframe at frame {_framesPushed}");
            }
            _framesSinceKeyframe++;

            // Push raw I420 frame to encoder
            // The encoder will encode it to VP8 and trigger OnVideoSourceEncodedSample
            // which will automatically send it via the peer connection
            var durationMs = (uint)(1000.0 / _targetFrameRate);
            _videoEncoder!.ExternalVideoSourceRawSample(
                durationMs,
                frame.Width,
                frame.Height,
                i420Data,
                VideoPixelFormatsEnum.I420);

            lock (_statsLock)
            {
                _framesPushed++;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SIPSorceryVideoSource] Error pushing frame: {ex.Message}");
            lock (_statsLock)
            {
                _framesDropped++;
            }
            return false;
        }
    }

    /// <inheritdoc/>
    public VideoSourceStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            var elapsed = _statsTimer.Elapsed.TotalSeconds;
            var currentFps = elapsed > 0 ? _framesPushed / elapsed : 0;

            // Try to get WebRTC stats
            long bitrateKbps = 0;
            if (_peerConnection != null)
            {
                // SIPSorcery doesn't expose detailed stats easily, estimate from frames
                bitrateKbps = (long)(currentFps * _width * _height * 12 / 1000); // Rough estimate for VP8
            }

            return new VideoSourceStatistics
            {
                FramesPushed = _framesPushed,
                FramesDropped = _framesDropped,
                CurrentFps = currentFps,
                BitrateKbps = bitrateKbps,
                AverageFrameTimeMs = currentFps > 0 ? 1000.0 / currentFps : 0
            };
        }
    }

    /// <summary>
    /// Creates an SDP offer for WebRTC negotiation.
    /// </summary>
    /// <returns>SDP offer description.</returns>
    public async Task<RTCSessionDescriptionInit> CreateOfferAsync()
    {
        if (_peerConnection == null)
            throw new InvalidOperationException("Peer connection not initialized.");

        var offer = _peerConnection.createOffer();
        await _peerConnection.setLocalDescription(offer);
        return offer;
    }

    /// <summary>
    /// Sets the remote SDP answer.
    /// </summary>
    /// <param name="answer">SDP answer from remote peer.</param>
    public void SetRemoteDescription(RTCSessionDescriptionInit answer)
    {
        if (_peerConnection == null)
            throw new InvalidOperationException("Peer connection not initialized.");

        var result = _peerConnection.setRemoteDescription(answer);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Failed to set remote description: {result}");
        }
    }

    /// <summary>
    /// Adds an ICE candidate received from remote peer.
    /// </summary>
    /// <param name="candidate">ICE candidate.</param>
    public void AddIceCandidate(RTCIceCandidateInit candidate)
    {
        if (_peerConnection == null)
            throw new InvalidOperationException("Peer connection not initialized.");

        _peerConnection.addIceCandidate(candidate);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        var stats = GetStatistics();
        Console.WriteLine($"[SIPSorceryVideoSource] Disposing...");
        Console.WriteLine($"[SIPSorceryVideoSource] Final stats: {stats.FramesPushed} frames, " +
                         $"{stats.FramesDropped} dropped, " +
                         $"Avg FPS: {stats.CurrentFps:F2}");

        if (_videoEncoder != null)
        {
            _videoEncoder.OnVideoSourceEncodedSample -= _peerConnection!.SendVideo;
            _videoEncoder.Dispose();
            _videoEncoder = null;
        }

        _peerConnection?.close();
        _peerConnection?.Dispose();
        _peerConnection = null;

        _disposed = true;
    }
}
