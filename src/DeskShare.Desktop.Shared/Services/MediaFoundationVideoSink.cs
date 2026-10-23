using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>Where H.264 is decoded right now. The order is the fallback order: each step down is slower or more compatible.</summary>
internal enum H264Path { MediaFoundationGpu, MediaFoundationSoftware, FFmpeg }

/// <summary>
/// Decoder endpoint that replaces FFmpegVideoEndPoint inside <see cref="VideoSink"/> (through its test seam
/// constructor): H.264 goes to the Windows Media Foundation decoder (GPU first, then software), VP8 to libvpx
/// (<see cref="VpxDecoder"/>). It raises OnVideoSinkDecodedSampleFaster with a BGRA <see cref="RawImage"/>, exactly like
/// FFmpegVideoEndPoint, so VideoSink and everything behind it stay unchanged. (The RawImage costs one extra copy in
/// VideoSink; removing it would need VideoSink changes beyond a few lines, so it is left alone.)
///
/// If a decoder breaks mid-session (GPU reset, driver update) the sink steps down GPU, then software, then FFmpeg
/// instead of leaving the viewer with a frozen picture.
/// </summary>
internal sealed class MediaFoundationVideoSink : IVideoSink, IDisposable
{
    /// <summary>Consecutive failed frames before a decoder is given up. One bad packet must not cost the GPU path, a dead device fails every frame.</summary>
    internal const int FailuresBeforeFallback = 3;

    // Cap for the frames kept for replay (see _gop). A 6 Mbit/s stream is about 8 MB per 10 s.
    private const long MaxReplayBytes = 32 * 1024 * 1024;

    // Same format the host's HardwareH264Encoder advertises (constrained baseline, packetization-mode=1).
    private static readonly VideoFormat H264Format = new(
        VideoCodecsEnum.H264, 102, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");
    private static readonly VideoFormat Vp8Format = new(VideoCodecsEnum.VP8, 96);

    private readonly ILogger _logger;
    private readonly Func<H264Path, IH264FrameDecoder> _createH264;
    private readonly Func<IVideoSink> _createFfmpeg;
    private readonly object _lock = new(); // decoders keep state between frames, so frames are decoded one at a time

    private H264Path _path;
    private IH264FrameDecoder? _h264;
    private int _h264Failures;
    private VpxDecoder? _vp8;
    private int _vp8Failures;
    private bool _vp8OnFfmpeg;
    private IVideoSink? _ffmpeg;
    private bool _suppressEvents; // true while old frames are replayed into a new decoder: only the last picture is shown
    private bool _disposed;

    // Every H.264 access unit since the last keyframe. The viewer cannot ask the host for a new keyframe (there is no
    // feedback channel), and a new decoder needs one to start, so on a switch we replay these instead.
    private readonly List<(IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)> _gop = new();
    private long _gopBytes;
    private bool _gopOverflow; // too long without a keyframe: replay is not possible, the new decoder waits for the next one

    internal MediaFoundationVideoSink(ILogger logger, Func<H264Path, IH264FrameDecoder> createH264, Func<IVideoSink> createFfmpeg,
        H264Path start, IH264FrameDecoder? firstDecoder = null)
    {
        _logger = logger;
        _createH264 = createH264;
        _createFfmpeg = createFfmpeg;
        _path = start;
        _h264 = firstDecoder;
    }

    /// <summary>The decoder in use right now (tests assert the fallback step; also handy in logs).</summary>
    internal H264Path CurrentPath => _path;

#pragma warning disable CS0067 // part of IVideoSink, never raised by this sink (only the "faster" variant is used)
    public event VideoSinkSampleDecodedDelegate? OnVideoSinkDecodedSample;
#pragma warning restore CS0067
    public event VideoSinkSampleDecodedFasterDelegate? OnVideoSinkDecodedSampleFaster;

    /// <summary>
    /// Picks the decoder from "Video:Decoder" (or env DESKSHARE_VIDEO_DECODER): Auto (default) | MediaFoundation |
    /// MediaFoundationSoftware | FFmpeg. Returns null when FFmpeg should be used, either by choice or because Media
    /// Foundation cannot be created here (Windows N editions without the Media Feature Pack). Logs the outcome once.
    /// Callers must check OperatingSystem.IsWindows() first so that no Media Foundation type is touched elsewhere.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static MediaFoundationVideoSink? TryCreate(IConfiguration configuration, ILogger logger)
    {
        var requested = Environment.GetEnvironmentVariable("DESKSHARE_VIDEO_DECODER") ?? configuration["Video:Decoder"];
        if (string.Equals(requested, "FFmpeg", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Video decoder: FFmpeg (Video:Decoder=FFmpeg)");
            return null;
        }
        var software = string.Equals(requested, "MediaFoundationSoftware", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(requested) && !software &&
            !string.Equals(requested, "Auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(requested, "MediaFoundation", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("Unknown Video:Decoder '{Value}', using Auto", requested);

        // Creating the real decoder is the availability probe: it fails on N editions and when D3D11/DXVA is unusable.
        for (var path = software ? H264Path.MediaFoundationSoftware : H264Path.MediaFoundationGpu; path != H264Path.FFmpeg; path++)
        {
            try
            {
                var first = CreateDecoder(path);
                logger.LogInformation("Video decoder: Media Foundation ({Path}) for H.264, libvpx for VP8", path);
                return new MediaFoundationVideoSink(logger, CreateDecoder, CreateFfmpegEndpoint, path, first);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Media Foundation H.264 ({Path}) unavailable: {Message}", path, ex.Message);
            }
        }
        logger.LogWarning("Video decoder: FFmpeg (Media Foundation H.264 is not available, e.g. a Windows N edition without the Media Feature Pack)");
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static IH264FrameDecoder CreateDecoder(H264Path path) =>
        new MediaFoundationH264Decoder(hardware: path == H264Path.MediaFoundationGpu);

    // Same setup as the VideoSink constructor does for its own endpoint (FFmpeg itself is initialised from <app>/ffmpeg there).
    private static IVideoSink CreateFfmpegEndpoint()
    {
        var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
        if (Directory.Exists(folder))
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, folder);
        var endpoint = new FFmpegVideoEndPoint();
        endpoint.RestrictFormats(format => format.Codec == VideoCodecsEnum.VP8 || format.Codec == VideoCodecsEnum.H264);
        endpoint.SetVideoSinkFormat(Vp8Format);
        return endpoint;
    }

    public void GotVideoFrame(IPEndPoint remoteEndPoint, uint timestamp, byte[] frame, VideoFormat format)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                if (format.Codec == VideoCodecsEnum.H264)
                    DecodeH264(remoteEndPoint, timestamp, frame, format);
                else if (format.Codec == VideoCodecsEnum.VP8)
                    DecodeVp8(remoteEndPoint, timestamp, frame, format);
            }
            catch (Exception ex)
            {
                // Last line of defence: whatever goes wrong in here must never reach the WebRTC receive thread.
                _logger.LogError(ex, "Video decode failed for {Codec} frame of {Size} bytes", format.Codec, frame.Length);
            }
        }
    }

    private void DecodeH264(IPEndPoint ep, uint ts, byte[] frame, VideoFormat format)
    {
        if (_path == H264Path.FFmpeg)
        {
            if (_ffmpeg == null)
            {
                // Creating the FFmpeg endpoint failed when we switched to it: retry, then catch up from the last keyframe
                // (this frame included). Throws again if it still fails; the caller logs it and the next frame retries.
                Remember(ep, ts, frame, format);
                EnsureFfmpeg();
                Replay(f => _ffmpeg!.GotVideoFrame(f.Ep, f.Ts, f.Data, f.Format));
                return;
            }
            _ffmpeg.GotVideoFrame(ep, ts, frame, format);
            return;
        }

        Remember(ep, ts, frame, format);
        try
        {
            _h264 ??= _createH264(_path);
            _h264.Decode(frame, Raise);
            _h264Failures = 0;
        }
        catch (Exception ex)
        {
            // A decoder that cannot even be created is given up at once; a running one only after repeated failures.
            _h264Failures = _h264 == null ? FailuresBeforeFallback : _h264Failures + 1;
            _logger.LogWarning("Media Foundation H.264 ({Path}) failed ({Count}/{Max}): {Message}", _path, _h264Failures, FailuresBeforeFallback, ex.Message);
            if (_h264Failures >= FailuresBeforeFallback)
                StepDown(); // replays the current frame too: it is the newest one remembered
            // Below the limit the frame is dropped; the next one may work.
        }
    }

    // Moves to the next decoder and feeds it everything since the last keyframe so the picture continues without a new keyframe.
    private void StepDown()
    {
        _h264?.Dispose();
        _h264 = null;
        _h264Failures = 0;
        _path++;
        _logger.LogWarning("Switching H.264 decoding to {Path}", _path);

        if (_path == H264Path.FFmpeg)
        {
            EnsureFfmpeg();
            Replay(frame => _ffmpeg!.GotVideoFrame(frame.Ep, frame.Ts, frame.Data, frame.Format));
            return;
        }
        Replay(frame =>
        {
            _h264 ??= _createH264(_path);
            _h264.Decode(frame.Data, Raise);
        });
    }

    private void Replay(Action<(IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)> decode)
    {
        if (_gopOverflow)
            _logger.LogWarning("No replay possible (too long since the last keyframe): the picture resumes at the next keyframe");
        var frames = _gop.ToArray();
        _suppressEvents = true;
        try
        {
            for (var i = 0; i < frames.Length; i++)
            {
                _suppressEvents = i < frames.Length - 1; // show only the newest picture, not a burst of old ones
                decode(frames[i]);
            }
        }
        catch (Exception ex) when (_path != H264Path.FFmpeg)
        {
            // The next decoder is broken as well: keep stepping down (ends at FFmpeg, which is not guarded here on purpose).
            _suppressEvents = false;
            _logger.LogWarning("Media Foundation H.264 ({Path}) failed during replay: {Message}", _path, ex.Message);
            StepDown();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void Remember(IPEndPoint ep, uint ts, byte[] frame, VideoFormat format)
    {
        if (ContainsIdr(frame))
        {
            _gop.Clear();
            _gopBytes = 0;
            _gopOverflow = false;
        }
        if (_gopOverflow) return;
        _gop.Add((ep, ts, frame, format));
        _gopBytes += frame.Length;
        if (_gopBytes > MaxReplayBytes)
        {
            _gop.Clear();
            _gopBytes = 0;
            _gopOverflow = true;
        }
    }

    // NAL unit type 5 (IDR slice) behind a 00 00 01 start code.
    internal static bool ContainsIdr(byte[] annexB)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1 && (annexB[i + 3] & 0x1F) == 5)
                return true;
        return false;
    }

    private void DecodeVp8(IPEndPoint ep, uint ts, byte[] frame, VideoFormat format)
    {
        if (!_vp8OnFfmpeg)
        {
            try
            {
                _vp8 ??= new VpxDecoder();
                _vp8.Decode(frame, Raise);
                _vp8Failures = 0;
                return;
            }
            catch (Exception ex)
            {
                _vp8Failures = _vp8 == null ? FailuresBeforeFallback : _vp8Failures + 1;
                _logger.LogWarning("libvpx VP8 decode failed ({Count}/{Max}): {Message}", _vp8Failures, FailuresBeforeFallback, ex.Message);
                if (_vp8Failures < FailuresBeforeFallback) return;
                _vp8?.Dispose();
                _vp8 = null;
                _vp8OnFfmpeg = true;
                _logger.LogWarning("Switching VP8 decoding to FFmpeg");
            }
        }
        EnsureFfmpeg();
        _ffmpeg!.GotVideoFrame(ep, ts, frame, format);
    }

    private void EnsureFfmpeg()
    {
        if (_ffmpeg != null) return;
        _ffmpeg = _createFfmpeg();
        _ffmpeg.OnVideoSinkDecodedSampleFaster += image =>
        {
            if (!_suppressEvents) OnVideoSinkDecodedSampleFaster?.Invoke(image);
        };
    }

    // Pins the pixels only for the duration of the event: VideoSink copies them straight away.
    private void Raise(byte[] bgra, int width, int height)
    {
        if (_suppressEvents) return;
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
            (_ffmpeg as IDisposable)?.Dispose();
            _gop.Clear();
        }
    }
}
