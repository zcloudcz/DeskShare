using FFmpeg.AutoGen;
using Serilog;
using SIPSorceryMedia.Abstractions;

namespace DeskShare.Core.Video;

/// <summary>
/// VP8 for a live screen through the bundled FFmpeg's libvpx, with settings made for text. Replaces SIPSorcery's VP8
/// encoders, which both gave a blurry desktop (about 23 dB on 12 px text at 1600x900):
/// <list type="bullet">
/// <item>FFmpegVideoEndPoint (Linux, macOS) opens libvpx with FFmpeg's file defaults (256 kbit/s, "good" deadline) and a
/// key frame every second; a sharp key frame of a desktop is 100-250 KB, so once a second is a burst the network has to
/// swallow, and capping it (max-intra-rate) is what made the text blurry.</item>
/// <item>The libvpx wrapper in SIPSorceryMedia.Encoders (Windows) has a similarly low fixed bitrate.</item>
/// </list>
/// Here the key frames are only the ones the sender asks for (new viewer, its periodic refresh): libvpx itself never
/// starts one (long GOP), so they can stay full quality. Realtime deadline and no look-ahead keep each frame
/// immediately sendable.
/// </summary>
internal sealed class LibvpxVp8Encoder : IVideoSource, IDisposable
{
    internal static readonly VideoFormat Format = new(VideoCodecsEnum.VP8, 96);

    private static readonly Dictionary<string, string> Options = new()
    {
        ["deadline"] = "realtime",
        ["cpu-used"] = "8",       // fastest realtime speed; quality comes from the bitrate here, not the search effort
        ["lag-in-frames"] = "0",  // no look-ahead: a frame is sent as soon as it is captured
        ["auto-alt-ref"] = "0",   // alt-ref frames need look-ahead
    };

    // ~2 bits per pixel per second: 2.9 Mbit/s at 1600x900, 4.1 at 1080p. A still screen uses a fraction (VBR).
    internal static long BitrateFor(int width, int height) => width * (long)height * 2;

    // libvpx would otherwise insert its own key frame every gop_size frames. The sender forces them when needed.
    private const int GopSize = 100_000;

    private readonly Func<int, int, IH264Context> _createContext;
    private readonly object _lock = new();
    private IH264Context? _context;
    private int _width, _height;
    private bool _forceKeyFrame = true;
    private int _failures;

    public LibvpxVp8Encoder(Func<int, int, IH264Context>? createContext = null)
    {
        _createContext = createContext ?? ((w, h) => new NativeH264Context("libvpx", Options, w, h, BitrateFor(w, h), planarI420: true, gopSize: GopSize));
    }

    /// <summary>True when the loaded FFmpeg has the libvpx VP8 encoder (call after FFmpeg is initialised).</summary>
    internal static unsafe bool IsAvailable()
    {
        try
        {
            return ffmpeg.avcodec_find_encoder_by_name("libvpx") != null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or NotSupportedException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
    {
        if (pixelFormat != VideoPixelFormatsEnum.I420)
            throw new NotSupportedException("The VP8 encoder takes I420 input only.");
        // Native code must never read past the array (VideoFrame planes can be shorter than width x height).
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0 || sample.Length < (long)width * height * 3 / 2)
            return;

        byte[]? encoded;
        lock (_lock)
        {
            try
            {
                if (_context == null || width != _width || height != _height)
                {
                    _context?.Dispose();
                    _context = null;
                    _context = _createContext(width, height); // the bitrate depends on the frame size
                    _width = width;
                    _height = height;
                    _forceKeyFrame = true;
                }
                var key = _forceKeyFrame;
                _forceKeyFrame = false;
                encoded = _context.Encode(sample, key);
                _failures = 0;
            }
            catch (Exception ex)
            {
                // Rebuild the context on the next frame (starting with a key frame) instead of sending nothing for good.
                if (_failures++ % 100 == 0)
                    Log.Error(ex, "VP8 encoder failed");
                _context?.Dispose();
                _context = null;
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
            _context?.Dispose();
            _context = null;
        }
    }
}
