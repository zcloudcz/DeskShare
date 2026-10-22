using FFmpeg.AutoGen;
using Serilog;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace DeskShare.Core.Video;

/// <summary>
/// H.264 encoder on the GPU's fixed-function block, driven through the bundled LGPL FFmpeg
/// (h264_amf, h264_nvenc, h264_qsv, h264_mf). Never libx264: that one is GPL.
/// SIPSorceryMedia.FFmpeg's own <c>FFmpegVideoEndPoint</c> cannot be used because it always asks FFmpeg for the
/// default H.264 encoder (libx264, missing in our build), so this wraps its public <see cref="FFmpegVideoEncoder"/>,
/// which does accept an explicit encoder name.
/// </summary>
internal sealed class HardwareH264Encoder : IVideoSource, IDisposable
{
    // Order = preference. h264_mf (Media Foundation) is the vendor-neutral last resort.
    private static readonly string[] Candidates = { "h264_amf", "h264_nvenc", "h264_qsv", "h264_mf" };

    // Browsers answer with their own profile-level-id; we send constrained baseline, which every WebRTC decoder accepts.
    internal static readonly VideoFormat Format = new(
        VideoCodecsEnum.H264, 102, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");

    private static readonly Lazy<string?> Available = new(Probe);

    private readonly string _name;
    private readonly object _lock = new();
    private FFmpegVideoEncoder? _encoder;
    private int _width, _height;
    private bool _forceKeyFrame;
    private int _failures;

    public HardwareH264Encoder(string encoderName) => _name = encoderName;

    /// <summary>Name of the first hardware encoder that really initialises on this machine, or null. Probed once.</summary>
    public static string? FindAvailable() => Available.Value;

    /// <summary>
    /// "Listed in FFmpeg" is not enough (h264_nvenc exists in every build, but fails without an NVIDIA driver),
    /// so each candidate encodes a small test frame.
    /// </summary>
    private static string? Probe()
    {
        foreach (var name in Candidates)
        {
            try
            {
                using var probe = new HardwareH264Encoder(name);
                probe.Encode(320, 192, new byte[320 * 192 * 3 / 2]);
                Log.Information("Hardware H.264 encoder available: {Encoder}", name);
                return name;
            }
            catch (Exception ex)
            {
                Log.Debug("H.264 encoder {Encoder} not usable: {Message}", name, ex.Message);
            }
        }
        return null;
    }

    private static Dictionary<string, string> OptionsFor(string name) => name switch
    {
        // forced_idr: without it AMF ignores ForceKeyFrame. header_insertion_mode: SPS/PPS before every IDR so a
        // viewer that joins on a later keyframe can decode.
        "h264_amf" => new() { ["usage"] = "ultralowlatency", ["quality"] = "speed", ["rc"] = "cbr", ["profile"] = "constrained_baseline", ["forced_idr"] = "1", ["header_insertion_mode"] = "idr" },
        "h264_nvenc" => new() { ["preset"] = "p1", ["tune"] = "ull", ["zerolatency"] = "1", ["rc"] = "cbr", ["profile"] = "baseline", ["forced-idr"] = "1" },
        "h264_qsv" => new() { ["preset"] = "veryfast", ["profile"] = "baseline", ["forced_idr"] = "1" },
        _ => new() { ["scenario"] = "display_remoting", ["rate_control"] = "cbr", ["low_latency"] = "1" },
    };

    // Encoder is rebuilt per resolution because the options and the bitrate depend on the frame size.
    private byte[]? Encode(int width, int height, byte[] i420)
    {
        if (_encoder == null || width != _width || height != _height)
        {
            _encoder?.Dispose();
            var options = OptionsFor(_name);
            _encoder = new FFmpegVideoEncoder(options);
            if (!_encoder.SetCodec(AVCodecID.AV_CODEC_ID_H264, _name, options))
                throw new InvalidOperationException($"FFmpeg has no encoder '{_name}'.");
            // ~0.1 bit per pixel at 30 fps: 6 Mbit/s at 1080p, 15 Mbit/s at 2880x1800. Text stays readable on a LAN.
            _encoder.SetBitrate((long)(width * (long)height * 3), null, null, null);
            _width = width;
            _height = height;
        }

        if (_forceKeyFrame)
        {
            _encoder.ForceKeyFrame();
            _forceKeyFrame = false;
        }

        // Returns null while the hardware encoder still buffers its first frame.
        return _encoder.EncodeVideo(width, height, i420, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264);
    }

    public string EncoderName => _name;

    public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
    {
        if (pixelFormat != VideoPixelFormatsEnum.I420)
            throw new NotSupportedException("The H.264 encoder takes I420 input only.");

        byte[]? encoded;
        lock (_lock)
        {
            try
            {
                encoded = Encode(width, height, sample);
                _failures = 0;
            }
            catch (Exception ex)
            {
                // A GPU reset or driver update can break the encoder mid-stream. Keep dropping frames (the viewer
                // sees a frozen picture and can reconnect) instead of killing the capture pipeline.
                if (_failures++ % 100 == 0)
                    Log.Error(ex, "H.264 encoder {Encoder} failed ({Count} consecutive)", _name, _failures);
                return;
            }
        }

        if (encoded is { Length: > 0 })
            OnVideoSourceEncodedSample?.Invoke(durationMilliseconds * 90, encoded); // RTP clock: 90 kHz
    }

    public void ForceKeyFrame() => _forceKeyFrame = true;

    public List<VideoFormat> GetVideoSourceFormats() => new() { Format };
    public void SetVideoSourceFormat(VideoFormat videoFormat) { }
    public void RestrictFormats(Func<VideoFormat, bool> filter) { }
    public bool HasEncodedVideoSubscribers() => OnVideoSourceEncodedSample != null;
    public bool IsVideoSourcePaused() => false;
    public Task PauseVideo() => Task.CompletedTask;
    public Task ResumeVideo() => Task.CompletedTask;
    public Task StartVideo() => Task.CompletedTask;
    public Task CloseVideo() => Task.CompletedTask;
    public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage) =>
        throw new NotSupportedException("Use ExternalVideoSourceRawSample.");

    public event EncodedSampleDelegate? OnVideoSourceEncodedSample;
#pragma warning disable CS0067 // part of IVideoSource, never raised by an encoder-only source
    public event RawVideoSampleDelegate? OnVideoSourceRawSample;
    public event RawVideoSampleFasterDelegate? OnVideoSourceRawSampleFaster;
    public event SourceErrorDelegate? OnVideoSourceError;
#pragma warning restore CS0067

    public void Dispose()
    {
        lock (_lock)
        {
            _encoder?.Dispose();
            _encoder = null;
        }
    }
}
