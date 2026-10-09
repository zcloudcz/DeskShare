using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace DeskShare.Core.Video;

/// <summary>
/// VP8 encoder for platforms where SIPSorceryMedia.Encoders has no native library (macOS, Linux).
/// FFmpegVideoEndPoint would do the job, but it hides its FFmpegVideoEncoder, and libvpx's default of
/// 256 kbit/s turns screen content (text) into mush. This adapter drives the public FFmpegVideoEncoder
/// directly so it can set a bitrate that matches the frame size.
///
/// Only the IVideoSource members the senders use are implemented: the encoded-sample event, VP8-only formats,
/// I420 input and ForceKeyFrame. The rest are no-ops or throw <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class FfmpegVp8Encoder : IVideoSource, IDisposable
{
    // ~0.065 bit per pixel per frame at 30 fps: 1080p -> 4.0 Mbit/s, 3440x1440 -> 9.6 Mbit/s.
    private const double BitsPerPixel = 0.065;
    private const int Fps = 30; // FFmpegVideoEncoder.EncodeVideo hard-codes 30 fps, so budget for the same.
    private const int MinBitrate = 1_000_000;
    private const int MaxBitrate = 10_000_000;

    private readonly FFmpegVideoEncoder _encoder = new();
    private readonly VideoFormat _vp8 = FFmpegVideoEndPoint._supportedFormats.First(f => f.Codec == VideoCodecsEnum.VP8);
    private int _bitratePixels;

    public event EncodedSampleDelegate? OnVideoSourceEncodedSample;

    // Raw capture events do not apply: this class only encodes frames pushed by the app. Explicit empty
    // accessors avoid "event never used" warnings (warnings are errors in this project).
    public event RawVideoSampleDelegate OnVideoSourceRawSample { add { } remove { } }
    public event RawVideoSampleFasterDelegate OnVideoSourceRawSampleFaster { add { } remove { } }
    public event SourceErrorDelegate OnVideoSourceError { add { } remove { } }

    /// <summary>Target bitrate in bit/s for a frame size, clamped to 1-10 Mbit/s.</summary>
    internal static int TargetBitrate(int width, int height) =>
        (int)Math.Clamp(width * (double)height * Fps * BitsPerPixel, MinBitrate, MaxBitrate);

    public List<VideoFormat> GetVideoSourceFormats() => new() { _vp8 };

    public void SetVideoSourceFormat(VideoFormat videoFormat)
    {
        if (videoFormat.Codec != VideoCodecsEnum.VP8)
            throw new NotSupportedException($"Only VP8 is supported, not {videoFormat.Codec}.");
    }

    public void RestrictFormats(Func<VideoFormat, bool> filter) { } // VP8 is the only format anyway

    public void ForceKeyFrame() => _encoder.ForceKeyFrame();

    public bool HasEncodedVideoSubscribers() => OnVideoSourceEncodedSample != null;

    public bool IsVideoSourcePaused() => false;

    public Task PauseVideo() => Task.CompletedTask;

    public Task ResumeVideo() => Task.CompletedTask;

    public Task StartVideo() => Task.CompletedTask;

    public Task CloseVideo() => Task.CompletedTask;

    public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
    {
        var handler = OnVideoSourceEncodedSample;
        if (handler == null)
            return;

        if (pixelFormat != VideoPixelFormatsEnum.I420)
            throw new NotSupportedException($"Only I420 input is supported, not {pixelFormat}.");

        // SetBitrate resets the encoder, so it runs only when the frame size changes (and before the first frame).
        if (width * height != _bitratePixels)
        {
            _encoder.SetBitrate(TargetBitrate(width, height), null, null, null);
            _bitratePixels = width * height;
        }

        var encoded = _encoder.EncodeVideo(width, height, sample, pixelFormat, VideoCodecsEnum.VP8);
        if (encoded == null)
            return;

        // RTP timestamp increment for this frame at the pushed frame rate.
        var fps = durationMilliseconds != 0 ? Math.Max(1u, 1000 / durationMilliseconds) : (uint)Fps;
        handler((uint)_vp8.ClockRate / fps, encoded);
    }

    public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage) =>
        throw new NotSupportedException("Use ExternalVideoSourceRawSample.");

    public void Dispose() => _encoder.Dispose();
}
