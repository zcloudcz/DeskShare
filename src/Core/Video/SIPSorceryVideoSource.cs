using System.Diagnostics;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.WebRTC;
using Serilog;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using IVideoSource = DeskShare.Core.Interfaces.IVideoSource;
using System.Collections.Generic;

namespace DeskShare.Core.Video;

/// <summary>
/// WebRTC video source implementation using SIPSorcery library.
/// Provides real peer-to-peer video streaming over WebRTC.
/// Uses VideoEncoderFactory for VP8 encoding (SIPSorcery encoder on Windows, FFmpeg elsewhere).
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
    private SIPSorceryMedia.Abstractions.IVideoSource? _videoEncoder;

    private int _framesSinceKeyframe = 0;
    private const int KeyframeInterval = 90;

    // Reusable buffer for I420 frame data
    private byte[]? _i420Buffer;

    // Statistics
    private long _framesPushed;
    private long _framesDropped;
    private readonly Stopwatch _statsTimer = Stopwatch.StartNew();
    private DateTime _lastFrameTime = DateTime.MinValue;

    /// <inheritdoc/>
    public bool IsActive => _initialized && !_disposed && _peerConnection?.connectionState == RTCPeerConnectionState.connected;

    /// <inheritdoc/>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// ICE servers used for peer connections created from now on (Initialize and ResetPeerConnection).
    /// Updated after each registration so every new viewer gets fresh TURN credentials; defaults to public STUN.
    /// </summary>
    public IReadOnlyList<RTCIceServer> IceServers { get; set; } = IceServerMapper.DefaultIceServers();

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

        Log.Information("Resetting peer connection for new client...");

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
        var config = new RTCConfiguration { iceServers = new List<RTCIceServer>(IceServers) };

        _peerConnection = new RTCPeerConnection(config);

        // Add video track to peer connection (send-only, we don't receive)
        var videoTrack = new MediaStreamTrack(
            VideoEncoderFactory.AdvertisedFormats(_videoEncoder!),
            MediaStreamStatusEnum.SendOnly);

        _peerConnection.addTrack(videoTrack);

        // Connect encoder output to peer connection
        // When encoder produces VP8 frames, they'll be sent via WebRTC
        _videoEncoder!.OnVideoSourceEncodedSample += _peerConnection.SendVideo;

        // Handle codec negotiation - when client chooses a codec, configure encoder
        _peerConnection.OnVideoFormatsNegotiated += (formats) =>
        {
            _videoEncoder!.SetVideoSourceFormat(VideoEncoderFactory.ToEncoder(_videoEncoder!, formats.First()));
        };

        Log.Information("Peer connection reset complete");
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
        // Note: We'll force keyframes every 2 frames in PushFrame for low latency
        _videoEncoder = VideoEncoderFactory.Create();

        // Create peer connection with configuration
        var config = new RTCConfiguration { iceServers = new List<RTCIceServer>(IceServers) };

        _peerConnection = new RTCPeerConnection(config);

        // Create video track with formats from encoder
        var videoTrack = new MediaStreamTrack(
            VideoEncoderFactory.AdvertisedFormats(_videoEncoder),
            MediaStreamStatusEnum.SendOnly);

        _peerConnection.addTrack(videoTrack);

        // Wire up the encoder to peer connection
        _videoEncoder.OnVideoSourceEncodedSample += _peerConnection.SendVideo;

        // Handle format negotiation
        _peerConnection.OnVideoFormatsNegotiated += (formats) =>
        {
            _videoEncoder.SetVideoSourceFormat(VideoEncoderFactory.ToEncoder(_videoEncoder, formats.First()));
        };

        _initialized = true;

        Log.Information("Initialized: {Width}x{Height} @ {TargetFrameRate}fps, Codec: VP8, Format: I420", _width, _height, _targetFrameRate);

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

        // Check if peer connection is in a valid state for sending
        // Allow sending during 'connecting' and 'connected' states
        // The peer connection will buffer frames internally during negotiation
        if (_peerConnection == null)
        {
            Log.Warning("PushFrame: Peer connection is null, dropping frame");
            lock (_statsLock)
            {
                _framesDropped++;
            }
            return false;
        }

        var state = _peerConnection.connectionState;
        if (state != RTCPeerConnectionState.connected && state != RTCPeerConnectionState.connecting)
        {
            lock (_statsLock)
            {
                _framesDropped++;
                if (_framesDropped % 100 == 1)
                    Log.Debug("PushFrame: dropping, peer connection state is {State}", state);
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
                    if (_framesDropped % 100 == 1)
                        Log.Debug("PushFrame: dropping for backpressure ({SinceLast:F1} ms since last frame)", timeSinceLastFrame.TotalMilliseconds);
                }
                return false;
            }
        }

        _lastFrameTime = now;

        try
        {
            var yLen = frame.YPlaneLength;
            var uLen = frame.UPlaneLength;
            var vLen = frame.VPlaneLength;
            var totalSize = yLen + uLen + vLen;

            if (_i420Buffer == null || _i420Buffer.Length < totalSize)
                _i420Buffer = new byte[totalSize];

            Buffer.BlockCopy(frame.YPlane, 0, _i420Buffer, 0, yLen);
            Buffer.BlockCopy(frame.UPlane, 0, _i420Buffer, yLen, uLen);
            Buffer.BlockCopy(frame.VPlane, 0, _i420Buffer, yLen + uLen, vLen);

            // Force keyframe every N frames for low latency decoding
            // This ensures decoder can start displaying video quickly
            if (_framesSinceKeyframe >= KeyframeInterval)
            {
                _videoEncoder!.ForceKeyFrame();
                _framesSinceKeyframe = 0;
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
                _i420Buffer,
                VideoPixelFormatsEnum.I420);

            lock (_statsLock)
            {
                _framesPushed++;

                // Log every 30 frames (once per second at 30fps)
                if (_framesPushed % 30 == 0)
                {
                    Log.Debug("Pushed {FramesPushed} frames (state: {ConnectionState})", _framesPushed, _peerConnection?.connectionState);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error pushing frame");
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
    /// Creates an SDP answer in response to a received offer for WebRTC negotiation.
    /// This is the correct method to use when responding to an offer from a remote peer.
    /// </summary>
    /// <returns>SDP answer description.</returns>
    public async Task<RTCSessionDescriptionInit> CreateAnswerAsync()
    {
        if (_peerConnection == null)
            throw new InvalidOperationException("Peer connection not initialized.");

        // Remote description must be set before creating answer
        if (_peerConnection.remoteDescription == null)
            throw new InvalidOperationException("Remote description must be set before creating answer.");

        var answer = _peerConnection.createAnswer();
        await _peerConnection.setLocalDescription(answer);
        return answer;
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

    /// <summary>
    /// Manually sets the video format for the encoder.
    /// </summary>
    /// <param name="format">Video format to use (e.g., VP8).</param>
    public void SetVideoFormat(VideoFormat format)
    {
        if (_videoEncoder == null)
        {
            Log.Warning("Cannot set format, encoder is null");
            return;
        }

        Log.Information("Video format set: {Codec}", format.Codec);
        _videoEncoder.SetVideoSourceFormat(VideoEncoderFactory.ToEncoder(_videoEncoder, format));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        var stats = GetStatistics();
        Log.Information("Disposing. Final stats: FramesPushed={FramesPushed}, FramesDropped={FramesDropped}, AvgFps={AvgFps:F2}",
            stats.FramesPushed, stats.FramesDropped, stats.CurrentFps);

        if (_videoEncoder != null)
        {
            _videoEncoder.OnVideoSourceEncodedSample -= _peerConnection!.SendVideo;
            (_videoEncoder as IDisposable)?.Dispose();
            _videoEncoder = null;
        }

        _peerConnection?.close();
        _peerConnection?.Dispose();
        _peerConnection = null;

        _disposed = true;
    }
}
