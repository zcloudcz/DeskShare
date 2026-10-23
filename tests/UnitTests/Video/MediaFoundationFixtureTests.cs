using System.Net;
using System.Runtime.InteropServices;
using DeskShare.Core.Video;
using DeskShare.Desktop.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorceryMedia.Abstractions;
using Xunit.Abstractions;

namespace DeskShare.UnitTests.Video;

/// <summary>
/// Recorded access units under tests/UnitTests/TestData, so the decoders can be tested without an encoder (CI has no GPU).
/// File format: a plain sequence of records, each record = 4-byte little-endian length, then that many bytes of one
/// access unit (H.264 Annex B with start codes, or one VP8 frame), in decode order.
/// Files: h264-1080p.au (29 AU, first is SPS+PPS+IDR), h264-480p.au (7 AU, same layout), vp8-192p.au (5 frames, first is a keyframe).
/// All show the four colour blocks of <see cref="MediaFoundationDecoderTests.ColourBlocks"/>.
/// Regenerate with the opt-in test MediaFoundationDecoderTests.GenerateFixtures (needs the GPU encoder).
/// </summary>
internal static class MfFixtures
{
    public const string H264Hd = "h264-1080p.au";
    public const string H264Sd = "h264-480p.au";
    public const string Vp8 = "vp8-192p.au";

    /// <summary>The copy in the build output (csproj copies TestData\** next to the test assembly).</summary>
    public static List<byte[]> Read(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", name));
        var result = new List<byte[]>();
        for (var pos = 0; pos < bytes.Length;)
        {
            var length = BitConverter.ToInt32(bytes, pos);
            result.Add(bytes.AsSpan(pos + 4, length).ToArray());
            pos += 4 + length;
        }
        return result;
    }

    public static void Write(string path, IEnumerable<byte[]> accessUnits)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        foreach (var au in accessUnits)
        {
            writer.Write(au.Length); // BinaryWriter writes Int32 little-endian
            writer.Write(au);
        }
    }

    /// <summary>The checked-in folder (not the build output): the generator must update the files that get committed.</summary>
    public static string SourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var project = Path.Combine(dir.FullName, "tests", "UnitTests");
            if (Directory.Exists(project)) return Directory.CreateDirectory(Path.Combine(project, "TestData")).FullName;
        }
        throw new DirectoryNotFoundException("tests/UnitTests/TestData not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Decoder tests over the recorded streams: they run wherever Media Foundation exists (also on CI runners without a GPU
/// or an encoder) and are skipped, with the reason shown in the results, where it does not.
/// </summary>
public sealed class MediaFoundationFixtureTests(ITestOutputHelper output)
{
    private static readonly IPEndPoint Ep = new(IPAddress.Loopback, 1);
    private static readonly VideoFormat H264 = new(VideoCodecsEnum.H264, 102);

    private sealed class MfFixtureFactAttribute : FactAttribute
    {
        public MfFixtureFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || !MediaFoundationH264Decoder.IsAvailable())
                Skip = "Needs Windows with the Media Foundation H.264 decoder (missing on N editions and some Server images); the viewer would use FFmpeg there.";
        }
    }

    /// <summary>Also needs a working D3D11 + DXVA decoder, which a runner without a GPU does not have.</summary>
    private sealed class MfGpuFixtureFactAttribute : FactAttribute
    {
        public MfGpuFixtureFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || !MediaFoundationH264Decoder.IsAvailable())
            {
                Skip = "Needs Windows with the Media Foundation H.264 decoder.";
                return;
            }
            try
            {
                using var probe = new MediaFoundationH264Decoder(hardware: true);
            }
            catch (Exception ex)
            {
                Skip = "No GPU (D3D11/DXVA) H.264 decoder on this machine: " + ex.Message;
            }
        }
    }

    private static MediaFoundationVideoSink AutoSink()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build(); // empty: Video:Decoder defaults to Auto
        return MediaFoundationVideoSink.TryCreate(config, NullLogger.Instance)
            ?? throw new InvalidOperationException("Auto mode chose FFmpeg although the Media Foundation decoder is available.");
    }

    private static List<(byte[] Bgra, int W, int H)> DecodeAll(MediaFoundationH264Decoder decoder, IEnumerable<byte[]> stream)
    {
        var frames = new List<(byte[], int, int)>();
        foreach (var au in stream)
            decoder.Decode(au, (bgra, w, h) => frames.Add((bgra, w, h)));
        return frames;
    }

    // The sink's event hands out a pinned buffer that is only valid during the event, so the pixels are copied.
    private static void CollectPictures(MediaFoundationVideoSink sink, List<(int W, int H, byte[] Data)> pictures) =>
        sink.OnVideoSinkDecodedSampleFaster += image =>
        {
            var data = new byte[image.Width * image.Height * 4];
            Marshal.Copy(image.Sample, data, 0, data.Length);
            pictures.Add((image.Width, image.Height, data));
        };

    private static void AssertHdPicture(IReadOnlyList<(byte[] Bgra, int W, int H)> frames)
    {
        Assert.NotEmpty(frames);
        var last = frames[^1];
        // 1920x1080 on purpose: the coded picture is 1088 rows, so this covers the crop and the UV plane offset.
        Assert.Equal((1920, 1080), (last.W, last.H));
        Assert.Equal(1920 * 1080 * 4, last.Bgra.Length);
        MediaFoundationDecoderTests.AssertColours(last.Bgra, 1920, 1080);
    }

    [MfFixtureFact]
    public void Software_Decodes1080pFixture_WithCorrectSizeAndColours()
    {
        using var decoder = new MediaFoundationH264Decoder(hardware: false);
        AssertHdPicture(DecodeAll(decoder, MfFixtures.Read(MfFixtures.H264Hd)));
    }

    [MfGpuFixtureFact]
    public void Gpu_Decodes1080pFixture_WithCorrectSizeAndColours()
    {
        using var decoder = new MediaFoundationH264Decoder(hardware: true);
        AssertHdPicture(DecodeAll(decoder, MfFixtures.Read(MfFixtures.H264Hd)));
    }

    private static void FollowsResolutionChange(bool hardware)
    {
        // 640x480 first, then the 1080p stream, whose SPS+PPS+IDR arrive like they do after a display switch on the host.
        using var decoder = new MediaFoundationH264Decoder(hardware);
        var frames = DecodeAll(decoder, MfFixtures.Read(MfFixtures.H264Sd).Concat(MfFixtures.Read(MfFixtures.H264Hd)));

        Assert.Contains(frames, f => (f.W, f.H) == (640, 480));
        AssertHdPicture(frames);
    }

    [MfFixtureFact]
    public void Software_FollowsAResolutionChange_OnTheSameDecoder() => FollowsResolutionChange(hardware: false);

    [MfGpuFixtureFact]
    public void Gpu_FollowsAResolutionChange_OnTheSameDecoder() => FollowsResolutionChange(hardware: true);

    /// <summary>VP8 goes through libvpx's I420 planes and our BT.601 converter, like FFmpeg does.</summary>
    [MfFixtureFact]
    public void Vp8Fixture_DecodesWithTheSameColoursAsFfmpeg()
    {
        using var sink = new VideoSink(NullLogger<VideoSink>.Instance, AutoSink());
        VideoFrameReceivedEventArgs? received = null;
        sink.FrameReceived += (_, e) => received = e;
        foreach (var frame in MfFixtures.Read(MfFixtures.Vp8))
            sink.GotVideoFrame(Ep, 0, frame, new VideoFormat(VideoCodecsEnum.VP8, 96));

        Assert.NotNull(received);
        Assert.Equal((320, 192), (received!.Width, received.Height));
        MediaFoundationDecoderTests.AssertColours(received.FrameData, 320, 192);
    }

    /// <summary>
    /// The production selection (Video:Decoder = Auto) over the fixture. Which path Auto picks depends on the machine
    /// (GPU, no GPU, no Media Foundation), so the test prints it: CI logs then show what windows-latest really uses.
    /// </summary>
    [MfFixtureFact]
    public void AutoSink_DecodesTheFixture_AndReportsWhichPathItChose()
    {
        using var sink = AutoSink();
        output.WriteLine($"Auto mode chose: {sink.CurrentPath}");

        var pictures = new List<(int W, int H, byte[] Data)>();
        CollectPictures(sink, pictures);
        foreach (var au in MfFixtures.Read(MfFixtures.H264Hd))
            sink.GotVideoFrame(Ep, 0, au, H264);

        output.WriteLine($"{pictures.Count} pictures out, path after decoding: {sink.CurrentPath}");
        Assert.NotEqual(H264Path.FFmpeg, sink.CurrentPath); // the attribute guarantees MF exists, so Auto must not fall to FFmpeg
        Assert.NotEmpty(pictures);
        Assert.Equal((1920, 1080), (pictures[^1].W, pictures[^1].H));
        MediaFoundationDecoderTests.AssertColours(pictures[^1].Data, 1920, 1080);
    }

    /// <summary>Sends the sink's log lines to the test output, so a failed run shows which decoder gave up and why.</summary>
    private sealed class TestLogger(ITestOutputHelper o) : ILogger
    {
        public IDisposable? BeginScope<T>(T state) where T : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<T>(LogLevel logLevel, EventId eventId, T state, Exception? exception, Func<T, Exception?, string> formatter) =>
            o.WriteLine($"{logLevel}: {formatter(state, exception)}");
    }

    /// <summary>Wraps a real decoder so the test can reach the device-loss seam and see that the sink disposed it.</summary>
    private sealed class TrackedDecoder(MediaFoundationH264Decoder inner) : IH264FrameDecoder
    {
        public readonly MediaFoundationH264Decoder Inner = inner;
        public bool Disposed;
        public int Decode(byte[] annexB, Action<byte[], int, int> onFrame) => Inner.Decode(annexB, onFrame);
        public void Dispose() { Disposed = true; Inner.Dispose(); }
    }

    /// <summary>
    /// Simulated GPU loss on this PC (no driver reset): partway through the stream the running GPU decoder's device is
    /// swapped away. The sink must step down to software, replay from the last IDR and keep showing pictures.
    /// </summary>
    [MfGpuFixtureFact]
    public void GpuDeviceLostMidStream_StepsDownToSoftware_AndThePictureContinues()
    {
        var created = new List<(H264Path Path, TrackedDecoder Decoder)>();
        var ffmpegRequested = false;
        using var sink = new MediaFoundationVideoSink(new TestLogger(output),
            path =>
            {
                var tracked = new TrackedDecoder(new MediaFoundationH264Decoder(hardware: path == H264Path.MediaFoundationGpu));
                created.Add((path, tracked));
                return tracked;
            },
            () => { ffmpegRequested = true; throw new InvalidOperationException("FFmpeg must not be needed"); },
            H264Path.MediaFoundationGpu);
        var pictures = new List<(int W, int H, byte[] Data)>();
        CollectPictures(sink, pictures);

        var stream = MfFixtures.Read(MfFixtures.H264Hd);
        const int lossAt = 10;
        for (var i = 0; i < stream.Count; i++)
        {
            if (i == lossAt)
            {
                Assert.Equal(H264Path.MediaFoundationGpu, sink.CurrentPath);
                Assert.NotEmpty(pictures); // the GPU path was alive before the loss
                created[0].Decoder.Inner.SimulateDeviceLoss();
            }
            sink.GotVideoFrame(Ep, 0, stream[i], H264); // must not throw: the sink swallows and logs decoder errors
        }

        output.WriteLine($"path after the loss: {sink.CurrentPath}, decoders created: {string.Join(", ", created.Select(c => c.Path))}, pictures: {pictures.Count}");
        Assert.Equal(H264Path.MediaFoundationSoftware, sink.CurrentPath);
        Assert.False(ffmpegRequested);
        Assert.Equal(new[] { H264Path.MediaFoundationGpu, H264Path.MediaFoundationSoftware }, created.Select(c => c.Path));
        Assert.True(created[0].Decoder.Disposed, "the lost GPU decoder must be disposed (native resources)");
        Assert.False(created[1].Decoder.Disposed);

        // The picture continued after the loss, and it is the right one (the replay started from the IDR, no new keyframe needed).
        Assert.True(pictures.Count > lossAt, $"only {pictures.Count} pictures");
        Assert.Equal((1920, 1080), (pictures[^1].W, pictures[^1].H));
        MediaFoundationDecoderTests.AssertColours(pictures[^1].Data, 1920, 1080);

        sink.Dispose();
        Assert.True(created[1].Decoder.Disposed);
    }
}
