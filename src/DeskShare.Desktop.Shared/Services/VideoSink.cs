using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using System.Collections.Concurrent;
using System.Net;
using SIPSorcery.Net;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Video sink for receiving and decoding WebRTC video frames.
/// Implements IVideoSink to receive encoded VP8 frames from RTCPeerConnection.
/// Uses FFmpegVideoEndPoint for VP8 decoding.
/// Platform-independent: shared between WPF and Avalonia desktop clients.
/// </summary>
public class VideoSink : IVideoSink, IDisposable
{
    private readonly ILogger<VideoSink> _logger;
    private readonly FFmpegVideoEndPoint? _ffmpegEndpoint;
    private bool _disposed;
    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private const int MaxQueueSize = 30; // Keep max 30 frames (1 second at 30fps)
    private bool _isInitialized;

    /// <summary>
    /// Event fired when a new video frame is decoded.
    /// Frame is in raw BGRA32 format ready for rendering.
    /// </summary>
    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;

    /// <summary>
    /// Event from IVideoSink for decoded samples.
    /// </summary>
    public event VideoSinkSampleDecodedDelegate? OnVideoSinkDecodedSample;

    /// <summary>
    /// Event from IVideoSink for decoded samples (faster variant).
    /// </summary>
    public event VideoSinkSampleDecodedFasterDelegate? OnVideoSinkDecodedSampleFaster;

    public VideoSink(ILogger<VideoSink> logger)
    {
        _logger = logger;

        try
        {
            // Initialize FFmpeg library from bundled DLLs
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
            if (Directory.Exists(ffmpegPath))
            {
                FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, ffmpegPath);
                _logger.LogInformation("FFmpeg initialized from: {Path}", ffmpegPath);
            }
            else
            {
                _logger.LogWarning("FFmpeg directory not found at {Path}, using system FFmpeg", ffmpegPath);
            }

            // Create FFmpeg video decoder endpoint for VP8
            _ffmpegEndpoint = new FFmpegVideoEndPoint();
            _logger.LogInformation("Initializing FFmpeg VP8 decoder endpoint");

            // Restrict to VP8 only
            _ffmpegEndpoint.RestrictFormats(format => format.Codec == VideoCodecsEnum.VP8);

            // Set video format explicitly before receiving frames
            var vp8Format = new VideoFormat(VideoCodecsEnum.VP8, 96);
            _ffmpegEndpoint.SetVideoSinkFormat(vp8Format);
            _logger.LogInformation("Video sink format set to VP8");

            // Subscribe to decoded frame events
            _ffmpegEndpoint.OnVideoSinkDecodedSampleFaster += OnDecodedSampleFaster;

            _isInitialized = true;
            _logger.LogInformation("VideoSink initialized with FFmpeg VP8 decoder");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize FFmpeg video decoder");
            _ffmpegEndpoint = null;
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Handles decoded video samples from FFmpeg (faster variant using RawImage).
    /// </summary>
    private void OnDecodedSampleFaster(RawImage rawImage)
    {
        try
        {
            _logger.LogDebug("Decoded video sample (faster): {Width}x{Height}, format: {Format}, stride: {Stride}",
                rawImage.Width, rawImage.Height, rawImage.PixelFormat, rawImage.Stride);

            var bgra32Data = ConvertRawImageToBGRA32(rawImage);

            if (bgra32Data != null)
            {
                _frameQueue.Enqueue(bgra32Data);

                while (_frameQueue.Count > MaxQueueSize)
                {
                    _frameQueue.TryDequeue(out _);
                }

                FrameReceived?.Invoke(this, new VideoFrameReceivedEventArgs(bgra32Data, rawImage.Width, rawImage.Height));
                OnVideoSinkDecodedSampleFaster?.Invoke(rawImage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process decoded video sample (faster)");
        }
    }

    /// <summary>
    /// Called by SIPSorcery when encoded video sample is received via RTP.
    /// </summary>
    public void GotVideoRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID,
        bool marker, byte[] payload)
    {
        _logger.LogInformation("GotVideoRtp: seq={SeqNum}, timestamp={Timestamp}, payloadSize={Size}, marker={Marker}",
            seqnum, timestamp, payload.Length, marker);

        _ffmpegEndpoint?.GotVideoRtp(remoteEndPoint, ssrc, seqnum, timestamp, payloadID, marker, payload);
    }

    /// <summary>
    /// Called when a complete video frame is received (newer API).
    /// </summary>
    public void GotVideoFrame(IPEndPoint remoteEndPoint, uint timestamp, byte[] frame, VideoFormat format)
    {
        try
        {
            if (!_isInitialized || _ffmpegEndpoint == null)
            {
                _logger.LogWarning("Decoder not initialized, cannot decode frame");
                return;
            }

            _logger.LogInformation("Received video frame: {Size} bytes, format: {Format}, timestamp: {Timestamp}",
                frame.Length, format.Codec, timestamp);

            _ffmpegEndpoint.GotVideoFrame(remoteEndPoint, timestamp, frame, format);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process video frame");
        }
    }

    /// <summary>
    /// Converts RawImage (unsafe pointer) to BGRA32 byte array for rendering.
    /// </summary>
    private unsafe byte[]? ConvertRawImageToBGRA32(RawImage rawImage)
    {
        try
        {
            int width = rawImage.Width;
            int height = rawImage.Height;
            int stride = rawImage.Stride;
            var pixelFormat = rawImage.PixelFormat;

            int sourceSize = Math.Abs(stride) * height;
            byte[] sourceData = new byte[sourceSize];
            System.Runtime.InteropServices.Marshal.Copy(rawImage.Sample, sourceData, 0, sourceSize);

            return ConvertToBGRA32(sourceData, width, height, stride, pixelFormat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert RawImage to BGRA32");
            return null;
        }
    }

    /// <summary>
    /// Converts decoded video data to BGRA32 byte array.
    /// </summary>
    private byte[]? ConvertToBGRA32(byte[] sourceData, int width, int height, int stride, VideoPixelFormatsEnum pixelFormat)
    {
        try
        {
            var bgra32Size = width * height * 4;

            if (pixelFormat == VideoPixelFormatsEnum.Bgra)
            {
                if (stride == width * 4)
                {
                    return sourceData;
                }
                else
                {
                    var bgra32 = new byte[bgra32Size];
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.BlockCopy(sourceData, y * stride, bgra32, y * width * 4, width * 4);
                    }
                    return bgra32;
                }
            }
            else if (pixelFormat == VideoPixelFormatsEnum.Rgb)
            {
                var bgra32 = new byte[bgra32Size];
                for (int i = 0; i < width * height; i++)
                {
                    bgra32[i * 4 + 0] = sourceData[i * 3 + 2]; // B
                    bgra32[i * 4 + 1] = sourceData[i * 3 + 1]; // G
                    bgra32[i * 4 + 2] = sourceData[i * 3 + 0]; // R
                    bgra32[i * 4 + 3] = 255;                    // A
                }
                return bgra32;
            }
            else
            {
                _logger.LogWarning("Unsupported pixel format for conversion: {Format}", pixelFormat);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert to BGRA32");
            return null;
        }
    }

    // IVideoSink interface implementation methods

    public List<VideoFormat> GetVideoSinkFormats()
    {
        return _ffmpegEndpoint?.GetVideoSinkFormats() ?? new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.VP8, 96)
        };
    }

    public void SetVideoSinkFormat(VideoFormat videoFormat)
    {
        _logger.LogInformation("Video sink format set: {Format}", videoFormat.Codec);
        _ffmpegEndpoint?.SetVideoSinkFormat(videoFormat);
    }

    public void RestrictFormats(Func<VideoFormat, bool> filter)
    {
        _ffmpegEndpoint?.RestrictFormats(filter);
    }

    public Task PauseVideoSink()
    {
        _logger.LogInformation("Video sink paused");
        return _ffmpegEndpoint?.PauseVideoSink() ?? Task.CompletedTask;
    }

    public Task ResumeVideoSink()
    {
        _logger.LogInformation("Video sink resumed");
        return _ffmpegEndpoint?.ResumeVideoSink() ?? Task.CompletedTask;
    }

    public Task StartVideoSink()
    {
        _logger.LogInformation("Video sink started");
        return _ffmpegEndpoint?.StartVideoSink() ?? Task.CompletedTask;
    }

    public Task CloseVideoSink()
    {
        _logger.LogInformation("Video sink closing");
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _frameQueue.Clear();

        if (_ffmpegEndpoint != null)
        {
            _ffmpegEndpoint.OnVideoSinkDecodedSampleFaster -= OnDecodedSampleFaster;
        }

        _ffmpegEndpoint?.Dispose();
        _logger.LogInformation("VideoSink disposed");
    }
}

/// <summary>
/// Event args for video frame received event.
/// </summary>
public class VideoFrameReceivedEventArgs : EventArgs
{
    /// <summary>Tightly packed BGRA32 pixels (Width * 4 bytes per row).</summary>
    public byte[] FrameData { get; }

    /// <summary>Frame size in pixels; the viewer sizes its bitmap from these (the sender's screen, not a fixed 1080p).</summary>
    public int Width { get; }
    public int Height { get; }

    public VideoFrameReceivedEventArgs(byte[] frameData, int width, int height)
    {
        FrameData = frameData;
        Width = width;
        Height = height;
    }
}
