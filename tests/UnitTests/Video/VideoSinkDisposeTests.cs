using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using DeskShare.Desktop.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SIPSorceryMedia.Abstractions;

namespace DeskShare.UnitTests.Video;

/// <summary>
/// The native decoder must never be freed while a decode is still inside it (that crashed the viewer), and Dispose
/// must not wait for that decode: the decode can be blocked on the UI thread, which is the thread disposing.
/// </summary>
public sealed class VideoSinkDisposeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly VideoFormat Vp8 = new(VideoCodecsEnum.VP8, 96);

    // A decoder whose "decode" synchronously raises one decoded 2x2 BGRA frame, like FFmpegVideoEndPoint does.
    private static IVideoSink FakeDecoder()
    {
        var decoder = Substitute.For<IVideoSink, IDisposable>();
        decoder.When(d => d.GotVideoFrame(Arg.Any<IPEndPoint>(), Arg.Any<uint>(), Arg.Any<byte[]>(), Arg.Any<VideoFormat>()))
            .Do(_ =>
            {
                var pixels = Marshal.AllocHGlobal(16);
                try
                {
                    var image = new RawImage { Width = 2, Height = 2, Stride = 8, Sample = pixels, PixelFormat = VideoPixelFormatsEnum.Bgra };
                    decoder.OnVideoSinkDecodedSampleFaster += Raise.Event<VideoSinkSampleDecodedFasterDelegate>(image);
                }
                finally
                {
                    Marshal.FreeHGlobal(pixels);
                }
            });
        return decoder;
    }

    private static void Decode(VideoSink sink) => sink.GotVideoFrame(new IPEndPoint(IPAddress.Loopback, 1), 0, new byte[1], Vp8);

    [Fact]
    public async Task DisposeDuringADecode_ReturnsAtOnce_AndTheDecodeFreesTheDecoderWhenItLeaves()
    {
        var decoder = FakeDecoder();
        var sink = new VideoSink(NullLogger<VideoSink>.Instance, decoder);
        using var inCallback = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        sink.FrameReceived += (_, _) => { inCallback.Set(); release.Wait(Guard); }; // e.g. waiting for the UI thread

        var decode = Task.Run(() => Decode(sink));
        Assert.True(inCallback.Wait(Guard));

        var watch = Stopwatch.StartNew();
        sink.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"Dispose blocked for {watch.Elapsed}");
        ((IDisposable)decoder).DidNotReceive().Dispose(); // the decode is still inside the decoder

        release.Set();
        await decode.WaitAsync(Guard);
        ((IDisposable)decoder).Received(1).Dispose();     // freed by the decode on its way out
    }

    [Fact]
    public void DisposeWhileIdle_FreesAtOnce_AndLaterFramesNeverTouchTheDecoder()
    {
        var decoder = FakeDecoder();
        var sink = new VideoSink(NullLogger<VideoSink>.Instance, decoder);
        Decode(sink);

        sink.Dispose();
        ((IDisposable)decoder).Received(1).Dispose();

        decoder.ClearReceivedCalls();
        Decode(sink); // a late frame from the network thread
        sink.Dispose();
        decoder.DidNotReceiveWithAnyArgs().GotVideoFrame(default!, default, default!, default!);
        ((IDisposable)decoder).DidNotReceive().Dispose(); // freed exactly once
    }
}
