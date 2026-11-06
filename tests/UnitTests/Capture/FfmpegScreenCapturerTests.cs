using DeskShare.Core.Platforms;
using SIPSorceryMedia.Abstractions;

namespace DeskShare.UnitTests.Capture;

/// <summary>
/// Tests for <see cref="FfmpegScreenCapturer"/>: the pure pixel conversion, plus one real capture
/// through FFmpeg (gdigrab) that only runs where the bundled FFmpeg DLLs are available.
/// </summary>
public sealed class FfmpegScreenCapturerTests
{
    [Fact]
    public void ConvertToBgra_Bgra_CopiesPixelsAndIgnoresRowPadding()
    {
        // 2x2 image, source rows padded to 12 bytes (8 pixel bytes + 4 padding)
        byte[] src =
        {
            1, 2, 3, 4,  5, 6, 7, 8,  99, 99, 99, 99,
            9, 10, 11, 12,  13, 14, 15, 16,  99, 99, 99, 99,
        };
        var dst = new byte[16];

        Assert.True(BgraPixelConverter.TryConvertToBgra(src, 12, VideoPixelFormatsEnum.Bgra, 2, 2, dst, 8));

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, dst);
    }

    [Fact]
    public void ConvertToBgra_Bgr_AddsOpaqueAlpha()
    {
        byte[] src = { 10, 20, 30, 40, 50, 60 }; // two pixels: B,G,R
        var dst = new byte[8];

        Assert.True(BgraPixelConverter.TryConvertToBgra(src, 6, VideoPixelFormatsEnum.Bgr, 2, 1, dst, 8));

        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, dst);
    }

    [Fact]
    public void ConvertToBgra_Rgb_SwapsRedAndBlue()
    {
        byte[] src = { 10, 20, 30, 40, 50, 60 }; // two pixels: R,G,B
        var dst = new byte[8];

        Assert.True(BgraPixelConverter.TryConvertToBgra(src, 6, VideoPixelFormatsEnum.Rgb, 2, 1, dst, 8));

        Assert.Equal(new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 }, dst);
    }

    [Fact]
    public void ConvertToBgra_OddStride_CropsToRequestedWidth()
    {
        // 3 px wide RGB rows with 1 byte of padding (stride 10, odd-ish), cropped to the first 2 pixels
        byte[] src =
        {
            1, 2, 3,  4, 5, 6,  7, 8, 9,  0,
            11, 12, 13,  14, 15, 16,  17, 18, 19,  0,
        };
        var dst = new byte[16];

        Assert.True(BgraPixelConverter.TryConvertToBgra(src, 10, VideoPixelFormatsEnum.Rgb, 2, 2, dst, 8));

        Assert.Equal(new byte[] { 3, 2, 1, 255, 6, 5, 4, 255, 13, 12, 11, 255, 16, 15, 14, 255 }, dst);
    }

    [Fact]
    public void ConvertToBgra_UnsupportedFormat_ReturnsFalse()
    {
        Assert.False(BgraPixelConverter.TryConvertToBgra(new byte[64], 8, VideoPixelFormatsEnum.I420, 2, 2, new byte[16], 8));
    }

    [Fact]
    public void ConvertToBgra_SourceTooSmall_ReturnsFalse()
    {
        Assert.False(BgraPixelConverter.TryConvertToBgra(new byte[5], 6, VideoPixelFormatsEnum.Bgr, 2, 1, new byte[8], 8));
    }

    /// <summary>
    /// Real capture of the primary monitor. Needs Windows (gdigrab, a desktop session) and the FFmpeg
    /// DLLs, which are downloaded into the Avalonia project folder and are not in git, so CI skips it.
    /// </summary>
    [WindowsFfmpegFact]
    public void Capturer_OnWindows_DeliversNonEmptyBgraFrame()
    {
        string ffmpegFolder = WindowsFfmpegFactAttribute.FindFfmpegFolder()!;

        // Mirror ScreenSenderService: a short-lived capturer reads the size, then a second one streams.
        using (var probe = new FfmpegScreenCapturer(0, 20, ffmpegFolder))
        {
            Assert.True(probe.Initialize());
        }

        using var capturer = new FfmpegScreenCapturer(0, 20, ffmpegFolder);
        Assert.True(capturer.Initialize());
        Assert.True(capturer.Width > 0 && capturer.Height > 0);
        Assert.Equal(0, capturer.Width % 2);
        Assert.Equal(0, capturer.Height % 2);

        DeskShare.Core.Models.Frame? frame = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !capturer.TryAcquireFrame(out frame))
        {
            Thread.Sleep(10);
        }

        Assert.NotNull(frame);
        using (frame)
        {
            Assert.Equal(capturer.Width, frame!.Width);
            Assert.Equal(capturer.Height, frame.Height);
            Assert.Equal(capturer.Width * 4, frame.Stride);
            Assert.Contains(frame.Data.Take(frame.Stride * frame.Height), b => b != 0);
        }
    }

    /// <summary>
    /// Regression: the desktop app probes the screen size from its UI thread. Disposing the probe blocked on
    /// FFmpegVideoSource.Close, whose continuation needed that same thread, and froze the Avalonia window on Linux.
    /// A context that never runs posted work stands in for the busy UI thread.
    /// </summary>
    [LinuxX11FfmpegFact]
    public void Capturer_OnX11_InitializeAndDispose_DoNotNeedTheCallersContext()
    {
        // Run on a dedicated thread that has the blocked context, like the UI thread that started sharing.
        var probeRun = Task.Factory.StartNew(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverRunsContext());
            using var probe = new FfmpegScreenCapturer(0, 20, LinuxX11FfmpegFactAttribute.FindFfmpegFolder(), drawPointer: false);
            Assert.True(probe.Initialize());
            Assert.True(probe.Width > 0 && probe.Height > 0);
        }, TaskCreationOptions.LongRunning);

        Assert.True(probeRun.Wait(TimeSpan.FromSeconds(20)), "Initialize/Dispose deadlocked on the caller's SynchronizationContext");
    }

    private sealed class NeverRunsContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) { }
    }
}

/// <summary>
/// Skips the test unless running on Linux with an X display and FFmpeg: the folder in DESKSHARE_FFMPEG_DIR (e.g. usr/bin/ffmpeg
/// of an extracted AppImage), an "ffmpeg" folder next to the tests, or the system libraries.
/// </summary>
internal sealed class LinuxX11FfmpegFactAttribute : FactAttribute
{
    public LinuxX11FfmpegFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "X11 capture test runs on Linux only.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Skip = "No X display (DISPLAY is not set).";
        else if (FindFfmpegFolder() == null && !Enumerable.Range(58, 10).Any(v => System.Runtime.InteropServices.NativeLibrary.TryLoad($"libavdevice.so.{v}", out _)))
            Skip = "FFmpeg libraries (libavdevice) not found; set DESKSHARE_FFMPEG_DIR.";
    }

    /// <summary>Folder with the FFmpeg shared libraries, or null to let FFmpeg use the system ones.</summary>
    internal static string? FindFfmpegFolder()
    {
        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("DESKSHARE_FFMPEG_DIR"), Path.Combine(AppContext.BaseDirectory, "ffmpeg") })
        {
            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && Directory.GetFiles(candidate, "libavdevice.so*").Length > 0)
                return candidate;
        }

        return null;
    }
}

/// <summary>Skips the test unless running on Windows with the bundled FFmpeg DLLs present.</summary>
internal sealed class WindowsFfmpegFactAttribute : FactAttribute
{
    public WindowsFfmpegFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Real screen capture test runs on Windows only.";
        else if (FindFfmpegFolder() == null)
            Skip = "FFmpeg DLLs not found (src/DeskShare.DesktopAvalonia/ffmpeg).";
    }

    /// <summary>Looks next to the test assembly, then walks up to the repo's Avalonia ffmpeg folder.</summary>
    internal static string? FindFfmpegFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "ffmpeg"), Path.Combine(dir.FullName, "src", "DeskShare.DesktopAvalonia", "ffmpeg") })
            {
                if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "avcodec-*.dll").Length > 0)
                    return candidate;
            }
        }

        return null;
    }
}
