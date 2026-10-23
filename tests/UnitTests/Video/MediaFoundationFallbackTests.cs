using System.Net;
using DeskShare.Desktop.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SIPSorceryMedia.Abstractions;

namespace DeskShare.UnitTests.Video;

/// <summary>
/// Runtime fallback of the Media Foundation sink (GPU, then software, then FFmpeg) with fake decoders, so it is
/// testable anywhere: a GPU reset or driver update must not freeze the viewer.
/// </summary>
public sealed class MediaFoundationFallbackTests
{
    private static readonly IPEndPoint Ep = new(IPAddress.Loopback, 1);
    private static readonly VideoFormat H264 = new(VideoCodecsEnum.H264, 102);

    // Annex B bytes with an IDR (key) or non-IDR slice NAL; content is irrelevant to the fakes.
    private static byte[] Key(byte tag) => new byte[] { 0, 0, 0, 1, 0x65, tag };
    private static byte[] Delta(byte tag) => new byte[] { 0, 0, 0, 1, 0x41, tag };

    private sealed class FakeDecoder(Func<byte[], bool> works) : IH264FrameDecoder
    {
        public readonly List<byte> Seen = new();
        public bool Disposed;
        public int Decode(byte[] annexB, Action<byte[], int, int> onFrame)
        {
            Seen.Add(annexB[5]);
            if (!works(annexB)) throw new InvalidOperationException("device lost");
            onFrame(new byte[16], 2, 2);
            return 1;
        }
        public void Dispose() => Disposed = true;
    }

    private static (MediaFoundationVideoSink Sink, List<int> Pictures) Make(
        Func<H264Path, IH264FrameDecoder> create, IVideoSink? ffmpeg = null, H264Path start = H264Path.MediaFoundationGpu)
    {
        var sink = new MediaFoundationVideoSink(NullLogger.Instance, create, () => ffmpeg ?? Substitute.For<IVideoSink>(), start);
        var pictures = new List<int>();
        sink.OnVideoSinkDecodedSampleFaster += image => pictures.Add(image.Width);
        return (sink, pictures);
    }

    [Fact]
    public void GpuFailingRepeatedly_SwitchesToSoftware_AndReplaysTheGopSoThePictureContinues()
    {
        var gpu = new FakeDecoder(_ => false);   // dead from the start (driver reset)
        var software = new FakeDecoder(_ => true);
        var (sink, pictures) = Make(p => p == H264Path.MediaFoundationGpu ? gpu : software);

        sink.GotVideoFrame(Ep, 0, Key(1), H264);     // failure 1: dropped
        sink.GotVideoFrame(Ep, 1, Delta(2), H264);   // failure 2: dropped
        Assert.Empty(pictures);
        sink.GotVideoFrame(Ep, 2, Delta(3), H264);   // failure 3: step down, replay key + 2 deltas

        Assert.True(gpu.Disposed);
        Assert.Equal(new byte[] { 1, 2, 3 }, software.Seen);  // no new keyframe was needed
        Assert.Single(pictures);                               // only the newest picture is shown, not the burst
        sink.GotVideoFrame(Ep, 3, Delta(4), H264);
        Assert.Equal(2, pictures.Count);
    }

    [Fact]
    public void OneBadFrame_DoesNotCostTheGpuPath()
    {
        var calls = 0;
        var gpu = new FakeDecoder(_ => ++calls != 2);  // only the second frame fails
        var (sink, pictures) = Make(_ => gpu);

        for (byte i = 1; i <= 5; i++)
            sink.GotVideoFrame(Ep, i, i == 1 ? Key(i) : Delta(i), H264);

        Assert.False(gpu.Disposed);
        Assert.Equal(4, pictures.Count);
    }

    [Fact]
    public void ADecoderThatCannotBeCreated_IsSkippedAtOnce()
    {
        var software = new FakeDecoder(_ => true);
        var (sink, pictures) = Make(p => p == H264Path.MediaFoundationGpu ? throw new InvalidOperationException("no D3D11") : software);

        sink.GotVideoFrame(Ep, 0, Key(1), H264);

        Assert.Single(pictures);
        Assert.Equal(new byte[] { 1 }, software.Seen);
    }

    [Fact]
    public void WhenEveryMediaFoundationDecoderFails_FfmpegGetsTheGopAndAllLaterFrames()
    {
        var ffmpeg = Substitute.For<IVideoSink>();
        var (sink, _) = Make(_ => new FakeDecoder(_ => false), ffmpeg);

        // 3 failures on the GPU decoder and 3 on the software one (the replay of the first fails at once, then the live frames).
        for (byte i = 1; i <= 6; i++)
            sink.GotVideoFrame(Ep, i, i == 1 ? Key(i) : Delta(i), H264);
        sink.GotVideoFrame(Ep, 7, Delta(7), H264);

        ffmpeg.Received().GotVideoFrame(Ep, 7, Arg.Is<byte[]>(b => b[5] == 7), H264);
        ffmpeg.Received(1).GotVideoFrame(Ep, 1, Arg.Is<byte[]>(b => b[5] == 1), H264); // replayed from the keyframe
    }

    [Fact]
    public void KeyframeStartsANewGop_SoOldFramesAreNotReplayed()
    {
        var gpu = new FakeDecoder(c => c[5] <= 3);   // works for frames 1..3, then dies
        var software = new FakeDecoder(_ => true);
        var (sink, _) = Make(p => p == H264Path.MediaFoundationGpu ? gpu : software);

        sink.GotVideoFrame(Ep, 0, Key(1), H264);
        sink.GotVideoFrame(Ep, 1, Delta(2), H264);
        sink.GotVideoFrame(Ep, 2, Key(3), H264);     // new GOP starts here
        for (byte i = 4; i <= 6; i++)
            sink.GotVideoFrame(Ep, i, Delta(i), H264);

        Assert.Equal(new byte[] { 3, 4, 5, 6 }, software.Seen);
    }
}
