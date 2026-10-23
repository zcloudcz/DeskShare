using DeskShare.Core.Video;
using SIPSorceryMedia.Abstractions;

namespace DeskShare.UnitTests.Video;

/// <summary>
/// A display switch can change the picture size mid-stream. The pipeline keeps the encoder, so every encoder path
/// must produce a decodable keyframe at the new size when it receives frames of a different size.
/// </summary>
public sealed class EncoderResolutionChangeTests
{
    private static string? FindFfmpegFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DeskShare.DesktopAvalonia", "ffmpeg");
            if (Directory.Exists(candidate))
                return Directory.EnumerateFiles(candidate, "avcodec*").Any() ? candidate : null;
        }
        return null;
    }

    /// <summary>Width and height from a VP8 keyframe header (frame tag, start code 9D 01 2A, then 14-bit width and height), or null for a delta frame.</summary>
    private static (int, int)? KeyFrameSize(byte[] vp8)
    {
        if (vp8.Length < 10 || (vp8[0] & 1) != 0 || vp8[3] != 0x9D || vp8[4] != 0x01 || vp8[5] != 0x2A)
            return null;
        return (((vp8[7] << 8) | vp8[6]) & 0x3FFF, ((vp8[9] << 8) | vp8[8]) & 0x3FFF);
    }

    private static byte[] Frame(int width, int height, int seed)
    {
        var i420 = new byte[width * height * 3 / 2];
        for (var i = 0; i < width * height; i++)
            i420[i] = (byte)(i % width / 3 + seed * 11);
        Array.Fill(i420, (byte)128, width * height, i420.Length - width * height);
        return i420;
    }

    private static void AssertKeyFrameAtNewSize(IVideoSource encoder)
    {
        using var _ = (IDisposable)encoder;
        encoder.SetVideoSourceFormat(VideoEncoderFactory.ToEncoder(encoder, VideoEncoderFactory.AdvertisedFormats(encoder)[0]));
        var samples = new List<byte[]>();
        encoder.OnVideoSourceEncodedSample += (_, data) => samples.Add(data);

        for (var i = 0; i < 4; i++)
            encoder.ExternalVideoSourceRawSample(33, 640, 480, Frame(640, 480, i), VideoPixelFormatsEnum.I420);
        samples.Clear();

        // What the pipeline does after a switch: ask for a keyframe, then push frames of the new size.
        encoder.ForceKeyFrame();
        for (var i = 0; i < 4; i++)
            encoder.ExternalVideoSourceRawSample(33, 1280, 720, Frame(1280, 720, i), VideoPixelFormatsEnum.I420);

        var keyFrame = samples.Select(KeyFrameSize).FirstOrDefault(s => s != null);
        Assert.True(keyFrame != null, $"no keyframe after the size change ({samples.Count} samples)");
        Assert.Equal((1280, 720), keyFrame!.Value);
    }

    [Fact]
    public void Vp8_DefaultEncoder_SwitchesToNewSizeWithKeyFrame()
    {
        if (!OperatingSystem.IsWindows())
            return; // the native libvpx wrapper is Windows-only; the FFmpeg route is covered below

        AssertKeyFrameAtNewSize(new ResizingVideoEncoder(() => VideoEncoderFactory.Create(codec: VideoCodecPreference.Vp8)));
    }

    [Fact]
    public void Vp8_FfmpegEncoder_SwitchesToNewSizeWithKeyFrame()
    {
        var folder = FindFfmpegFolder();
        if (folder == null)
            return; // FFmpeg libraries are gitignored; nothing to check without them

        AssertKeyFrameAtNewSize(new ResizingVideoEncoder(() => VideoEncoderFactory.Create(true, folder, VideoCodecPreference.Vp8)));
    }

    private sealed class SizeRecordingContext(int width, int height, List<(int, int)> created) : IH264Context
    {
        public (int, int) Size { get; } = (width, height);
        public byte[]? Encode(byte[] i420, bool forceKeyFrame)
        {
            created.Add(Size);
            return new byte[] { 1 };
        }
        public void Dispose() { }
    }

    [Fact]
    public void H264_HardwareEncoder_RebuildsItsContextForTheNewSize()
    {
        var created = new List<(int, int)>();
        var contexts = new List<(int, int)>();
        using var encoder = new HardwareH264Encoder("fake", (w, h) =>
        {
            contexts.Add((w, h));
            return new SizeRecordingContext(w, h, created);
        });
        var sent = 0;
        encoder.OnVideoSourceEncodedSample += (_, _) => sent++;

        encoder.ExternalVideoSourceRawSample(33, 640, 480, new byte[640 * 480 * 3 / 2], VideoPixelFormatsEnum.I420);
        encoder.ExternalVideoSourceRawSample(33, 1280, 720, new byte[1280 * 720 * 3 / 2], VideoPixelFormatsEnum.I420);
        encoder.ExternalVideoSourceRawSample(33, 1280, 720, new byte[1280 * 720 * 3 / 2], VideoPixelFormatsEnum.I420);

        Assert.Equal(new[] { (640, 480), (1280, 720) }, contexts); // one context per size, not per frame
        Assert.Equal(3, sent);
    }

    private sealed class FakeEncoder : IVideoSource, IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
        public List<VideoFormat> GetVideoSourceFormats() => new();
        public void SetVideoSourceFormat(VideoFormat videoFormat) { }
        public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat) { }
        public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage) { }
        public void ForceKeyFrame() { }
        public void RestrictFormats(Func<VideoFormat, bool> filter) { }
        public bool HasEncodedVideoSubscribers() => false;
        public bool IsVideoSourcePaused() => false;
        public Task PauseVideo() => Task.CompletedTask;
        public Task ResumeVideo() => Task.CompletedTask;
        public Task StartVideo() => Task.CompletedTask;
        public Task CloseVideo() => Task.CompletedTask;
#pragma warning disable CS0067
        public event EncodedSampleDelegate? OnVideoSourceEncodedSample;
        public event RawVideoSampleDelegate? OnVideoSourceRawSample;
        public event RawVideoSampleFasterDelegate? OnVideoSourceRawSampleFaster;
        public event SourceErrorDelegate? OnVideoSourceError;
#pragma warning restore CS0067
    }

    [Fact]
    public void Dispose_DuringRecreate_DoesNotLeakTheNewEncoder()
    {
        var created = new List<FakeEncoder>();
        using var inFactory = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var encoder = new ResizingVideoEncoder(() =>
        {
            var fake = new FakeEncoder();
            lock (created) created.Add(fake);
            if (created.Count > 1)
            {
                inFactory.Set();
                release.Wait(); // the second (replacement) encoder is slow to create
            }
            return fake;
        });
        encoder.ExternalVideoSourceRawSample(33, 640, 480, new byte[640 * 480 * 3 / 2], VideoPixelFormatsEnum.I420);

        // Dedicated threads instead of Task.Run: on a busy CI agent the thread pool may not start a work item for
        // seconds while other tests block pool threads, which made this test time out.
        var push = StartThread(() => encoder.ExternalVideoSourceRawSample(33, 1280, 720, new byte[1280 * 720 * 3 / 2], VideoPixelFormatsEnum.I420));
        Assert.True(inFactory.Wait(TimeSpan.FromSeconds(30)));
        var disposeStarted = new ManualResetEventSlim();
        var dispose = StartThread(() => { disposeStarted.Set(); encoder.Dispose(); }); // e.g. the client is removed on a connection state change
        Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(30)));
        Assert.False(dispose.Join(200)); // it has to wait for the recreate instead of racing it
        release.Set();

        Assert.True(push.Join(TimeSpan.FromSeconds(30)) && dispose.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, created.Count);
        Assert.All(created, f => Assert.True(f.Disposed));

        encoder.ExternalVideoSourceRawSample(33, 320, 240, new byte[320 * 240 * 3 / 2], VideoPixelFormatsEnum.I420); // after Dispose: ignored
        Assert.Equal(2, created.Count);
    }

    private static Thread StartThread(Action action)
    {
        var t = new Thread(() => action()) { IsBackground = true };
        t.Start();
        return t;
    }
}
