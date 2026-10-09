using System.Buffers;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using FFmpeg.AutoGen;
using Serilog;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace DeskShare.Core.Platforms;

/// <summary>
/// Cross-platform screen capturer built on the FFmpeg libraries the app already ships
/// (gdigrab on Windows, avfoundation on macOS, x11grab on Linux). Used on macOS and Linux
/// instead of hand-written native capture code that nobody could test.
/// </summary>
/// <remarks>
/// FFmpeg pushes frames from its own decode thread (event), while the capture pipeline pulls them
/// (<see cref="TryAcquireFrame"/>). The bridge is a single "latest frame" slot: a newer frame
/// replaces an unread older one, so a slow consumer never builds up a queue of 20 MB frames.
/// </remarks>
public sealed class FfmpegScreenCapturer : IScreenCapturer
{
    private static readonly ILogger Logger = Log.ForContext<FfmpegScreenCapturer>();

    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

    private readonly int _monitorIndex;
    private readonly int _fps;
    private readonly string _ffmpegFolder;

    private readonly object _lock = new();
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private FFmpegVideoSource? _source;
    private Frame? _latest; // guarded by _lock
    private bool _disposed; // guarded by _lock
    private bool _unsupportedFormatLogged;
    private bool _sizeMismatchLogged;

    /// <inheritdoc />
    public int Width { get; private set; }

    /// <inheritdoc />
    public int Height { get; private set; }

    /// <param name="monitorIndex">Index into the FFmpeg monitor list; a negative or out-of-range value falls back to the primary (or first) monitor.</param>
    /// <param name="fps">Capture frame rate requested from FFmpeg.</param>
    /// <param name="ffmpegFolder">Folder with the FFmpeg shared libraries; defaults to "ffmpeg" next to the executable.</param>
    public FfmpegScreenCapturer(int monitorIndex = 0, int fps = 30, string? ffmpegFolder = null)
    {
        _monitorIndex = monitorIndex;
        _fps = fps > 0 ? fps : 30;
        _ffmpegFolder = ffmpegFolder ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    }

    /// <inheritdoc />
    public bool Initialize()
    {
        if (_source != null)
            throw new InvalidOperationException("Capturer is already initialized.");

        try
        {
            // Initialise is safe to call repeatedly (the viewer calls it too). Without the bundled folder
            // FFmpeg falls back to its own auto-discovery of system libraries.
            if (Directory.Exists(_ffmpegFolder))
                FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, _ffmpegFolder);

            var monitors = FFmpegMonitorManager.GetMonitorDevices();
            if (monitors == null || monitors.Count == 0)
            {
                Logger.Error("[FfmpegScreenCapturer] FFmpeg found no monitors to capture");
                return false;
            }

            var monitor = _monitorIndex >= 0 && _monitorIndex < monitors.Count
                ? monitors[_monitorIndex]
                : monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
            Logger.Information("[FfmpegScreenCapturer] Capturing monitor {Name} ({Path}, {Rect}) at {Fps} fps",
                monitor.Name, monitor.Path, monitor.Rect, _fps);

            _source = CreateSource(monitor);
            _source.SetVideoSourceFormat(_source.GetVideoSourceFormats()[0]); // otherwise FFmpegVideoSource throws "Codec Unknown"
            _source.OnVideoSourceRawSampleFaster += OnRawSample;
            _source.OnVideoSourceError += error => Logger.Error("[FfmpegScreenCapturer] FFmpeg error: {Error}", error);
            _source.StartVideo().GetAwaiter().GetResult();

            // The real frame size is only known once a frame arrives (the macOS monitor list has no rectangle,
            // and Retina displays capture more pixels than points). Width/Height are set by OnRawSample.
            if (!_firstFrame.Wait(FirstFrameTimeout))
            {
                Logger.Error("[FfmpegScreenCapturer] No frame within {Timeout}; check screen capture permission", FirstFrameTimeout);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[FfmpegScreenCapturer] Initialization failed");
            return false;
        }
    }

    private FFmpegVideoSource CreateSource(SIPSorceryMedia.FFmpeg.Monitor monitor)
    {
        if (!OperatingSystem.IsMacOS())
        {
            // On Linux the library hard-codes display ":0"; honour $DISPLAY like the X11 input controller does.
            var display = OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("DISPLAY") : null;
            return new FFmpegScreenSource(string.IsNullOrEmpty(display) ? monitor.Path : display, monitor.Rect, _fps);
        }

        // FFmpegScreenSource always adds a crop filter built from the monitor rectangle, but the avfoundation
        // monitor list leaves that rectangle empty (crop=0:0:0:0). Build the avfoundation source ourselves
        // without the filter so the full screen is captured at native pixel size.
        var source = new FFmpegVideoSource();
        unsafe
        {
            source.CreateVideoDecoder(monitor.Path, ffmpeg.av_find_input_format("avfoundation"), repeat: false, isCamera: true);
        }
        source.InitialiseDecoder(new Dictionary<string, string>
        {
            ["framerate"] = _fps.ToString(),
            ["capture_cursor"] = "1",
        });
        return source;
    }

    /// <summary>
    /// Runs on FFmpeg's decode thread. The sample pointer is only valid during this call, so the pixels
    /// are copied (and converted to BGRA) immediately.
    /// </summary>
    private unsafe void OnRawSample(uint durationMs, RawImage image)
    {
        if (image.Sample == IntPtr.Zero || image.Width <= 0 || image.Height <= 0)
            return;

        // The first frame fixes the size. Even dimensions because the I420 converter needs them.
        if (Width == 0)
        {
            Width = image.Width & ~1;
            Height = image.Height & ~1;
        }

        if (image.Width < Width || image.Height < Height)
        {
            if (!_sizeMismatchLogged)
            {
                _sizeMismatchLogged = true;
                Logger.Warning("[FfmpegScreenCapturer] Frame {W}x{H} is smaller than {Width}x{Height} (resolution change?); dropping",
                    image.Width, image.Height, Width, Height);
            }
            return;
        }

        int bytesPerPixel = image.PixelFormat == VideoPixelFormatsEnum.Bgra || image.PixelFormat == VideoPixelFormatsEnum.Rgba ? 4 : 3;
        var source = new ReadOnlySpan<byte>((void*)image.Sample, image.Stride * (image.Height - 1) + image.Width * bytesPerPixel);

        int dstStride = Width * 4;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(dstStride * Height);
        if (!BgraPixelConverter.TryConvertToBgra(source, image.Stride, image.PixelFormat, Width, Height, buffer, dstStride))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!_unsupportedFormatLogged)
            {
                _unsupportedFormatLogged = true;
                Logger.Error("[FfmpegScreenCapturer] Unsupported pixel format {Format}; no frames will be delivered", image.PixelFormat);
            }
            return;
        }

        var frame = Frame.FromPooled(Width, Height, buffer, dstStride, DateTime.UtcNow);
        Frame? replaced;
        lock (_lock)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }
            replaced = _latest;
            _latest = frame;
        }

        replaced?.Dispose(); // never consumed: give the buffer back to the pool
        _firstFrame.Set();
    }

    /// <inheritdoc />
    public bool TryAcquireFrame(out Frame? frame)
    {
        lock (_lock)
        {
            frame = _latest;
            _latest = null;
        }

        return frame != null;
    }

    /// <inheritdoc />
    public void ReleaseFrame()
    {
        // Nothing to release: ownership of the frame passed to the caller in TryAcquireFrame.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Frame? pending;
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            pending = _latest;
            _latest = null;
        }

        pending?.Dispose();

        try
        {
            _source?.CloseVideo().GetAwaiter().GetResult();
            _source?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "[FfmpegScreenCapturer] Error while closing the FFmpeg source");
        }

        _firstFrame.Dispose();
    }
}

/// <summary>
/// Pure pixel-format conversion, kept separate so it can be unit tested without FFmpeg.
/// </summary>
internal static class BgraPixelConverter
{
    /// <summary>
    /// Copies the top-left <paramref name="width"/> x <paramref name="height"/> pixels of a source image
    /// into a BGRA destination, honouring both strides (rows may be padded).
    /// </summary>
    /// <returns>False when <paramref name="format"/> is not supported or the buffers are too small.</returns>
    internal static bool TryConvertToBgra(ReadOnlySpan<byte> src, int srcStride, VideoPixelFormatsEnum format,
        int width, int height, Span<byte> dst, int dstStride)
    {
        int srcBpp = format switch
        {
            VideoPixelFormatsEnum.Bgra or VideoPixelFormatsEnum.Rgba => 4,
            VideoPixelFormatsEnum.Bgr or VideoPixelFormatsEnum.Rgb => 3,
            _ => 0,
        };

        if (srcBpp == 0 || srcStride < width * srcBpp || dstStride < width * 4
            || src.Length < srcStride * (height - 1) + width * srcBpp
            || dst.Length < dstStride * height)
        {
            return false;
        }

        for (int y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStride, width * srcBpp);
            var d = dst.Slice(y * dstStride, width * 4);

            switch (format)
            {
                case VideoPixelFormatsEnum.Bgra:
                    s.CopyTo(d);
                    break;
                case VideoPixelFormatsEnum.Rgba:
                    for (int x = 0; x < width; x++)
                    {
                        d[x * 4] = s[x * 4 + 2];
                        d[x * 4 + 1] = s[x * 4 + 1];
                        d[x * 4 + 2] = s[x * 4];
                        d[x * 4 + 3] = s[x * 4 + 3];
                    }
                    break;
                case VideoPixelFormatsEnum.Bgr:
                    for (int x = 0; x < width; x++)
                    {
                        d[x * 4] = s[x * 3];
                        d[x * 4 + 1] = s[x * 3 + 1];
                        d[x * 4 + 2] = s[x * 3 + 2];
                        d[x * 4 + 3] = 255;
                    }
                    break;
                default: // Rgb
                    for (int x = 0; x < width; x++)
                    {
                        d[x * 4] = s[x * 3 + 2];
                        d[x * 4 + 1] = s[x * 3 + 1];
                        d[x * 4 + 2] = s[x * 3];
                        d[x * 4 + 3] = 255;
                    }
                    break;
            }
        }

        return true;
    }
}
