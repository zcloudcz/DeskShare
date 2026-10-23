using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Decoder endpoint that replaces FFmpegVideoEndPoint inside <see cref="VideoSink"/> (through its test seam
/// constructor): H.264 goes to the Windows Media Foundation decoder, VP8 to SIPSorcery's own libvpx wrapper
/// (vpxmd.dll, Windows only). Raises OnVideoSinkDecodedSampleFaster with a BGRA <see cref="RawImage"/>, exactly like
/// FFmpegVideoEndPoint, so VideoSink and everything behind it stay unchanged. PROTOTYPE, opt-in with Video:Decoder=MediaFoundation.
/// </summary>
internal sealed class MediaFoundationVideoSink : IVideoSink, IDisposable
{
    // Same format the host's HardwareH264Encoder advertises (constrained baseline, packetization-mode=1).
    private static readonly VideoFormat H264Format = new(
        VideoCodecsEnum.H264, 102, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");
    private static readonly VideoFormat Vp8Format = new(VideoCodecsEnum.VP8, 96);

    private readonly ILogger _logger;
    private readonly object _lock = new(); // decoders keep state between frames, so frames are decoded one at a time
    private MediaFoundationH264Decoder? _h264;
    private VpxVideoEncoder? _vp8;
    private bool _disposed;

    public MediaFoundationVideoSink(ILogger logger) => _logger = logger;

#pragma warning disable CS0067 // part of IVideoSink, never raised by this sink (only the "faster" variant is used)
    public event VideoSinkSampleDecodedDelegate? OnVideoSinkDecodedSample;
#pragma warning restore CS0067
    public event VideoSinkSampleDecodedFasterDelegate? OnVideoSinkDecodedSampleFaster;

    /// <summary>True when "Video:Decoder" asks for Media Foundation AND this Windows has it (N editions may not).</summary>
    public static bool ShouldUse(IConfiguration configuration, ILogger logger)
    {
        var requested = Environment.GetEnvironmentVariable("DESKSHARE_VIDEO_DECODER") ?? configuration["Video:Decoder"];
        if (!string.Equals(requested, "MediaFoundation", StringComparison.OrdinalIgnoreCase))
            return false;
        if (MediaFoundationH264Decoder.IsAvailable())
            return true;
        logger.LogWarning("Video:Decoder=MediaFoundation requested but Media Foundation H.264 is unavailable " +
                          "(not Windows, or a Windows N edition without the Media Feature Pack); using FFmpeg");
        return false;
    }

    public void GotVideoFrame(IPEndPoint remoteEndPoint, uint timestamp, byte[] frame, VideoFormat format)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                if (format.Codec == VideoCodecsEnum.H264)
                {
                    _h264 ??= new MediaFoundationH264Decoder();
                    _h264.Decode(frame, Raise);
                }
                else if (format.Codec == VideoCodecsEnum.VP8)
                {
                    _vp8 ??= new VpxVideoEncoder();
                    // libvpx hands back packed BGR (3 bytes per pixel); the viewer wants BGRA.
                    foreach (var s in _vp8.DecodeVideo(frame, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8))
                        RaiseBgr(s.Sample, (int)s.Width, (int)s.Height);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Media Foundation decode failed for {Codec} frame of {Size} bytes", format.Codec, frame.Length);
            }
        }
    }

    // Both Raise helpers pin the pixels only for the duration of the event: VideoSink copies them straight away.
    private void Raise(byte[] bgra, int width, int height)
    {
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            OnVideoSinkDecodedSampleFaster?.Invoke(new RawImage
            {
                Width = width, Height = height, Stride = width * 4,
                Sample = handle.AddrOfPinnedObject(), PixelFormat = VideoPixelFormatsEnum.Bgra
            });
        }
        finally { handle.Free(); }
    }

    private void RaiseBgr(byte[] bgr, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (int i = 0, j = 0; i < width * height; i++, j += 3)
        {
            bgra[i * 4] = bgr[j];
            bgra[i * 4 + 1] = bgr[j + 1];
            bgra[i * 4 + 2] = bgr[j + 2];
            bgra[i * 4 + 3] = 255;
        }
        Raise(bgra, width, height);
    }

    // The WebRTC stack reassembles RTP packets into whole frames itself and calls GotVideoFrame, so raw RTP is not used.
    public void GotVideoRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload) { }

    public List<VideoFormat> GetVideoSinkFormats() => new() { Vp8Format, H264Format };
    public void SetVideoSinkFormat(VideoFormat videoFormat) { }
    public void RestrictFormats(Func<VideoFormat, bool> filter) { }
    public Task PauseVideoSink() => Task.CompletedTask;
    public Task ResumeVideoSink() => Task.CompletedTask;
    public Task StartVideoSink() => Task.CompletedTask;
    public Task CloseVideoSink() { Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _h264?.Dispose();
            _vp8?.Dispose();
        }
    }
}
