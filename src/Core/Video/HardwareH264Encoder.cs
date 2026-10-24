using Serilog;
using SIPSorceryMedia.Abstractions;

namespace DeskShare.Core.Video;

/// <summary>
/// H.264 encoder on the GPU's fixed-function block, driven through the bundled LGPL FFmpeg
/// (h264_amf, h264_nvenc, h264_qsv, h264_mf). Never libx264: that one is GPL.
/// SIPSorceryMedia.FFmpeg's <c>FFmpegVideoEndPoint</c> cannot be used: it always asks for the default H.264 encoder
/// (libx264, missing in our build) and its FFmpegVideoEncoder leaks a packet per frame and mislabels the pixel format
/// (see <see cref="NativeH264Context"/>). Owns the failure handling: rebuild the context after repeated errors, then give up.
/// </summary>
internal sealed class HardwareH264Encoder : IVideoSource, IDisposable
{
    // Order = preference. h264_mf (Media Foundation) is the vendor-neutral last resort.
    private static readonly string[] Candidates = { "h264_amf", "h264_nvenc", "h264_qsv", "h264_mf" };

    // Browsers answer with their own profile-level-id; we send constrained baseline, which every WebRTC decoder accepts.
    internal static readonly VideoFormat Format = new(
        VideoCodecsEnum.H264, 102, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");

    private static readonly Lazy<string?> Available = new(Probe);

    /// <summary>Consecutive failed frames (about 1 s at 30 fps) before the encoder context is rebuilt.</summary>
    internal const int FailuresBeforeRecreate = 30;

    private readonly string _name;
    private readonly Func<int, int, IH264Context> _createContext;
    private readonly object _lock = new();
    private IH264Context? _context;
    private int _width, _height;
    private bool _forceKeyFrame;
    private int _failures;
    private int _invalidFrames;
    private bool _recreated;

    public HardwareH264Encoder(string encoderName, Func<int, int, IH264Context>? createContext = null)
    {
        _name = encoderName;
        // ~0.1 bit per pixel at 30 fps: 6 Mbit/s at 1080p, 15 Mbit/s at 2880x1800. Text stays readable on a LAN.
        _createContext = createContext ?? ((w, h) => new NativeH264Context(encoderName, OptionsFor(encoderName), w, h, w * (long)h * 3));
    }

    /// <summary>
    /// True once the encoder failed again right after its context was rebuilt. From then on frames are dropped and
    /// <see cref="HybridVideoEncoder"/> stops advertising H.264, so the next viewer negotiates VP8.
    /// </summary>
    public bool Failed => _failed;

    /// <summary>
    /// Whether H.264 may be offered to a new negotiation: not when this encoder failed, nor when another encoder in
    /// this process gave up (<see cref="HardwareDisabled"/>). A running, healthy encoder keeps encoding either way:
    /// another viewer's GPU failure (e.g. a session limit) must not freeze this viewer's picture.
    /// </summary>
    public bool CanAdvertise => !_failed && !HardwareDisabled;

    private bool _failed;

    /// <summary>
    /// Set when any encoder gave up: the GPU encoder is not used again by this process, so encoders created later
    /// (a new viewer, a reconnect) advertise VP8 only instead of negotiating H.264 and sending nothing.
    /// Cleared only by restarting the app.
    /// </summary>
    internal static volatile bool HardwareDisabled;

    /// <summary>Raised once, when <see cref="Failed"/> becomes true.</summary>
    public event Action? HardwareFailed;

    /// <summary>Name of the first hardware encoder that really initialises on this machine, or null. Probed once.</summary>
    public static string? FindAvailable() => HardwareDisabled ? null : Available.Value;

    /// <summary>
    /// "Listed in FFmpeg" is not enough (h264_nvenc exists in every build, but fails without an NVIDIA driver),
    /// so each candidate must encode known colours that decode back correctly.
    /// </summary>
    private static string? Probe()
    {
        foreach (var name in Candidates)
        {
            try
            {
                if (NativeH264Context.EncodesColoursCorrectly(name, OptionsFor(name), out var detail))
                {
                    Log.Information("Hardware H.264 encoder available: {Encoder}", name);
                    return name;
                }
                Log.Warning("H.264 encoder {Encoder} produced wrong output ({Detail}), skipped", name, detail);
            }
            catch (Exception ex)
            {
                Log.Debug("H.264 encoder {Encoder} not usable: {Message}", name, ex.Message);
            }
        }
        return null;
    }

    internal static Dictionary<string, string> OptionsFor(string name) => name switch
    {
        // forced_idr: without it AMF ignores a requested keyframe. header_insertion_mode: SPS/PPS before every IDR so a
        // viewer that joins on a later keyframe can decode.
        "h264_amf" => new() { ["usage"] = "ultralowlatency", ["quality"] = "speed", ["rc"] = "cbr", ["profile"] = "constrained_baseline", ["forced_idr"] = "1", ["header_insertion_mode"] = "idr" },
        "h264_nvenc" => new() { ["preset"] = "p1", ["tune"] = "ull", ["zerolatency"] = "1", ["rc"] = "cbr", ["profile"] = "baseline", ["forced-idr"] = "1" },
        "h264_qsv" => new() { ["preset"] = "veryfast", ["profile"] = "baseline", ["forced_idr"] = "1" },
        // hw_encoding: only hardware MFTs. Without it h264_mf falls back to Microsoft's software H.264 MFT on a PC
        // without a GPU encoder, which then wins the probe over VP8 (more CPU and delay, and it is not what "hardware
        // encoder" promises).
        _ => new() { ["scenario"] = "display_remoting", ["rate_control"] = "cbr", ["low_latency"] = "1", ["hw_encoding"] = "1" },
    };

    // The context is rebuilt per resolution because the bitrate depends on the frame size.
    private byte[]? Encode(int width, int height, byte[] i420)
    {
        if (_context == null || width != _width || height != _height)
        {
            _context?.Dispose();
            _context = null;
            _context = _createContext(width, height);
            _width = width;
            _height = height;
        }

        var key = _forceKeyFrame;
        _forceKeyFrame = false;
        return _context.Encode(i420, key);
    }

    public string EncoderName => _name;

    public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
    {
        if (pixelFormat != VideoPixelFormatsEnum.I420)
            throw new NotSupportedException("The H.264 encoder takes I420 input only.");

        // VideoFrame planes can be shorter than width x height; native code must never read past the array.
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0 || sample.Length < (long)width * height * 3 / 2)
        {
            if (_invalidFrames++ % 100 == 0)
                Log.Warning("Dropping invalid I420 frame: {Width}x{Height}, {Length} bytes", width, height, sample.Length);
            return;
        }

        byte[]? encoded;
        lock (_lock)
        {
            if (Failed)
                return;
            try
            {
                encoded = Encode(width, height, sample);
                _failures = 0;
                // A null (hardware still buffering its first frame) is no proof the context works.
                if (encoded is { Length: > 0 })
                    _recreated = false;
            }
            catch (Exception ex)
            {
                // A GPU reset or driver update can break the encoder mid-stream. Drop frames for a moment, then rebuild
                // the context; if the fresh one fails too the hardware is gone for good.
                if (_failures == 0)
                    Log.Error(ex, "H.264 encoder {Encoder} failed", _name);
                if (++_failures >= FailuresBeforeRecreate)
                    OnPersistentFailure();
                return;
            }
        }

        if (encoded is { Length: > 0 })
            OnVideoSourceEncodedSample?.Invoke(durationMilliseconds * 90, encoded); // RTP clock: 90 kHz
    }

    private void OnPersistentFailure()
    {
        _failures = 0;
        _context?.Dispose();
        _context = null;
        _forceKeyFrame = true; // a fresh context must start with an IDR
        if (!_recreated)
        {
            _recreated = true;
            Log.Warning("H.264 encoder {Encoder} keeps failing, rebuilding it", _name);
            return;
        }
        _failed = true;
        HardwareDisabled = true;
        Log.Error("H.264 encoder {Encoder} failed again after a rebuild, giving up: new viewers will get VP8", _name);
        HardwareFailed?.Invoke();
    }

    public void ForceKeyFrame() => _forceKeyFrame = true;

    /// <summary>
    /// Throws away whatever the GPU still holds. A hardware encoder lags a frame: the packet it releases during the next
    /// push is the PREVIOUS input. At a viewer switch that previous input is the old viewer's screen, and it would go to
    /// the new viewer as a delta frame. Dropping the context loses that packet; the next push opens a fresh context,
    /// which starts with an IDR.
    /// </summary>
    public void DiscardPending()
    {
        lock (_lock)
        {
            _context?.Dispose();
            _context = null;
            _forceKeyFrame = true;
        }
    }

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
            _context?.Dispose();
            _context = null;
        }
    }
}
