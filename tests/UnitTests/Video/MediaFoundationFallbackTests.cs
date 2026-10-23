using System.Diagnostics;
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

    // The sink decodes on its own thread; wait for it so the assertions see the result of each frame.
    private static void Feed(MediaFoundationVideoSink sink, IPEndPoint ep, uint ts, byte[] data, VideoFormat format)
    {
        sink.GotVideoFrame(ep, ts, data, format);
        Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)), "the decoder thread did not finish");
    }

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

    // A decoder whose device died silently: accepts everything, never outputs, never throws.
    private sealed class SilentDecoder : IH264FrameDecoder
    {
        public readonly List<byte> Seen = new();
        public bool Disposed;
        public int Decode(byte[] annexB, Action<byte[], int, int> onFrame) { Seen.Add(annexB[5]); return 0; }
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

        Feed(sink, Ep, 0, Key(1), H264);     // failure 1: dropped
        Feed(sink, Ep, 1, Delta(2), H264);   // failure 2: dropped
        Assert.Empty(pictures);
        Feed(sink, Ep, 2, Delta(3), H264);   // failure 3: step down, replay key + 2 deltas

        Assert.True(gpu.Disposed);
        Assert.Equal(new byte[] { 1, 2, 3 }, software.Seen);  // no new keyframe was needed
        Assert.Single(pictures);                               // only the newest picture is shown, not the burst
        Feed(sink, Ep, 3, Delta(4), H264);
        Assert.Equal(2, pictures.Count);
    }

    [Fact]
    public void OneBadFrame_DoesNotCostTheGpuPath()
    {
        var calls = 0;
        var gpu = new FakeDecoder(_ => ++calls != 2);  // only the second frame fails
        var (sink, pictures) = Make(_ => gpu);

        for (byte i = 1; i <= 5; i++)
            Feed(sink, Ep, i, i == 1 ? Key(i) : Delta(i), H264);

        Assert.False(gpu.Disposed);
        Assert.Equal(4, pictures.Count);
    }

    [Fact]
    public void ADecoderThatCannotBeCreated_IsSkippedAtOnce()
    {
        var software = new FakeDecoder(_ => true);
        var (sink, pictures) = Make(p => p == H264Path.MediaFoundationGpu ? throw new InvalidOperationException("no D3D11") : software);

        Feed(sink, Ep, 0, Key(1), H264);

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
            Feed(sink, Ep, i, i == 1 ? Key(i) : Delta(i), H264);
        Feed(sink, Ep, 7, Delta(7), H264);

        ffmpeg.Received().GotVideoFrame(Ep, 7, Arg.Is<byte[]>(b => b[5] == 7), H264);
        ffmpeg.Received(1).GotVideoFrame(Ep, 1, Arg.Is<byte[]>(b => b[5] == 1), H264); // replayed from the keyframe
    }

    [Fact]
    public void KeyframeStartsANewGop_SoOldFramesAreNotReplayed()
    {
        var gpu = new FakeDecoder(c => c[5] <= 3);   // works for frames 1..3, then dies
        var software = new FakeDecoder(_ => true);
        var (sink, _) = Make(p => p == H264Path.MediaFoundationGpu ? gpu : software);

        Feed(sink, Ep, 0, Key(1), H264);
        Feed(sink, Ep, 1, Delta(2), H264);
        Feed(sink, Ep, 2, Key(3), H264);     // new GOP starts here
        for (byte i = 4; i <= 6; i++)
            Feed(sink, Ep, i, Delta(i), H264);

        Assert.Equal(new byte[] { 3, 4, 5, 6 }, software.Seen);
    }

    [Fact]
    public void FfmpegFallbackThatFailsToStart_IsRetried_AndCatchesUpFromTheKeyframe()
    {
        var ffmpeg = Substitute.For<IVideoSink>();
        var attempts = 0;
        var sink = new MediaFoundationVideoSink(NullLogger.Instance, _ => throw new InvalidOperationException("no MF"),
            () => ++attempts == 1 ? throw new InvalidOperationException("FFmpeg not ready") : ffmpeg, H264Path.MediaFoundationSoftware);

        Feed(sink, Ep, 0, Key(1), H264);   // MF cannot be created -> FFmpeg, whose creation fails (logged, not thrown)
        Feed(sink, Ep, 1, Delta(2), H264); // retried: works now, gets the keyframe and this frame

        Assert.Equal(2, attempts);
        ffmpeg.Received(1).GotVideoFrame(Ep, 0, Arg.Is<byte[]>(b => b[5] == 1), H264);
        ffmpeg.Received(1).GotVideoFrame(Ep, 1, Arg.Is<byte[]>(b => b[5] == 2), H264);
    }

    [Fact]
    public void DecoderThatGoesSilentAfterAKeyframe_IsGivenUpAfterTheLimit_AndSoftwareTakesOverFromTheKeyframe()
    {
        var gpu = new SilentDecoder();
        var software = new FakeDecoder(_ => true);
        var (sink, pictures) = Make(p => p == H264Path.MediaFoundationGpu ? gpu : software);
        var limit = MediaFoundationVideoSink.SilentUnitsBeforeStall;

        Feed(sink, Ep, 0, Key(1), H264);
        for (var i = 1; i < limit - 1; i++)
            Feed(sink, Ep, (uint)i, Delta(2), H264);
        Assert.False(gpu.Disposed);                   // one unit short of the limit: still waiting
        Assert.Empty(software.Seen);

        Feed(sink, Ep, (uint)limit, Delta(3), H264);   // the limit is reached: step down without any exception

        Assert.True(gpu.Disposed);
        Assert.Equal(limit, software.Seen.Count);     // key frame + everything since, no new keyframe needed
        Assert.Equal(1, software.Seen[0]);
        Assert.Single(pictures);                      // only the newest picture is shown
    }

    [Fact]
    public void SilenceBeforeTheFirstKeyframe_IsNotAStall()
    {
        var gpu = new SilentDecoder();
        var (sink, _) = Make(_ => gpu);

        // A stream that joins mid-GOP: no picture is possible until the first IDR, however long that takes.
        for (var i = 0; i < MediaFoundationVideoSink.SilentUnitsBeforeStall * 2; i++)
            Feed(sink, Ep, (uint)i, Delta(2), H264);

        Assert.False(gpu.Disposed);
        Assert.Equal(H264Path.MediaFoundationGpu, sink.CurrentPath);
    }

    [Fact]
    public void ADecoderThatKeepsProducingPictures_NeverStalls()
    {
        var gpu = new FakeDecoder(_ => true);
        var (sink, _) = Make(_ => gpu);

        Feed(sink, Ep, 0, Key(1), H264);
        for (var i = 1; i < MediaFoundationVideoSink.SilentUnitsBeforeStall * 3; i++)
            Feed(sink, Ep, (uint)i, Delta(2), H264);

        Assert.False(gpu.Disposed);
    }

    // A decoder stuck in a native call: Decode blocks until released (the test releases it at the end so the thread can exit).
    private sealed class HangingDecoder : IH264FrameDecoder
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly ManualResetEventSlim Entered = new();
        public int Calls;
        public bool Disposed;
        public int Decode(byte[] annexB, Action<byte[], int, int> onFrame)
        {
            Interlocked.Increment(ref Calls);
            Entered.Set();
            Release.Wait();
            onFrame(new byte[16], 2, 2); // a late picture of the abandoned decoder must never be shown
            return 1;
        }
        public void Dispose() => Disposed = true;
    }

    private static MediaFoundationVideoSink MakeWithHangLimit(Func<H264Path, IH264FrameDecoder> create, TimeSpan limit, List<int> pictures, IH264FrameDecoder? first = null)
    {
        var sink = new MediaFoundationVideoSink(NullLogger.Instance, create, () => Substitute.For<IVideoSink>(), H264Path.MediaFoundationGpu, first) { HangLimit = limit };
        sink.OnVideoSinkDecodedSampleFaster += image => { lock (pictures) pictures.Add(image.Width); };
        return sink;
    }

    [Fact]
    public void HungDecoder_IsAbandoned_SinkStepsDown_AndTheReceiveThreadNeverBlocks()
    {
        var hung = new HangingDecoder();
        var software = new FakeDecoder(_ => true);
        var pictures = new List<int>();
        using var sink = MakeWithHangLimit(p => p == H264Path.MediaFoundationGpu ? hung : software, TimeSpan.FromMilliseconds(200), pictures);
        try
        {
            var slowest = TimeSpan.Zero;
            void Send(uint i, byte[] data)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                sink.GotVideoFrame(Ep, i, data, H264);
                var took = System.Diagnostics.Stopwatch.GetElapsedTime(start);
                if (took > slowest) slowest = took;
            }

            Send(0, Key(1));
            Assert.True(hung.Entered.Wait(5000));    // the decoder thread is now stuck inside Decode
            Send(1, Delta(2));
            Send(2, Delta(3));
            Thread.Sleep(400);                        // longer than the hang limit
            Send(3, Delta(4));                        // this frame notices the hang

            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));
            Assert.Equal(H264Path.MediaFoundationSoftware, sink.CurrentPath);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, software.Seen);   // replayed from the keyframe, queued frames not lost or doubled
            Assert.False(hung.Disposed);                              // abandoned, never disposed from another thread
            Assert.Equal(2, pictures.Count);                          // the recovery replay shows only its newest picture, plus the live frame 4
            Send(4, Delta(5));
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));
            Assert.Equal(3, pictures.Count);                          // pictures continue
            Assert.True(slowest < TimeSpan.FromMilliseconds(100), $"GotVideoFrame blocked for {slowest.TotalMilliseconds:F0} ms");
        }
        finally { hung.Release.Set(); }

        Thread.Sleep(200);                                            // the woken abandoned thread must stay silent
        lock (pictures) Assert.Equal(3, pictures.Count);
    }

    [Fact]
    public void SecondHang_GoesStraightToFfmpeg()
    {
        var first = new HangingDecoder();
        var second = new HangingDecoder();
        var ffmpeg = Substitute.For<IVideoSink>();
        var pictures = new List<int>();
        var sink = new MediaFoundationVideoSink(NullLogger.Instance, p => p == H264Path.MediaFoundationGpu ? first : second, () => ffmpeg, H264Path.MediaFoundationGpu)
        { HangLimit = TimeSpan.FromMilliseconds(150) };
        using (sink)
        try
        {
            sink.GotVideoFrame(Ep, 0, Key(1), H264);
            Assert.True(first.Entered.Wait(5000));
            Thread.Sleep(300);
            sink.GotVideoFrame(Ep, 1, Delta(2), H264);   // first hang found -> software (also hangs)
            Assert.True(second.Entered.Wait(5000));
            Thread.Sleep(300);
            sink.GotVideoFrame(Ep, 2, Delta(3), H264);   // second hang -> FFmpeg, no more Media Foundation attempts
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));

            Assert.Equal(H264Path.FFmpeg, sink.CurrentPath);
            ffmpeg.Received().GotVideoFrame(Ep, 2, Arg.Is<byte[]>(b => b[5] == 3), H264);
            ffmpeg.Received(1).GotVideoFrame(Ep, 0, Arg.Is<byte[]>(b => b[5] == 1), H264);   // caught up from the keyframe
        }
        finally { first.Release.Set(); second.Release.Set(); }
    }

    [Fact]
    public void FullQueue_NeverBlocks_AndAKeyframeReplacesEverythingQueuedBeforeIt()
    {
        var hung = new HangingDecoder();
        var pictures = new List<int>();
        using var sink = MakeWithHangLimit(_ => hung, TimeSpan.FromHours(1), pictures); // never abandoned: only the queue is tested
        try
        {
            sink.GotVideoFrame(Ep, 0, Key(1), H264);
            Assert.True(hung.Entered.Wait(5000));
            for (var i = 0; i < 200; i++)               // far more than the queue holds: must neither block nor throw
                sink.GotVideoFrame(Ep, (uint)i, Delta(2), H264);
            sink.GotVideoFrame(Ep, 999, Key(9), H264);  // the keyframe must survive the full queue
        }
        finally { hung.Release.Set(); }

        Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, hung.Calls);   // the in-flight keyframe and the new one; the deltas (and the catch-up) queued before it were useless and are gone
    }

    [Fact]
    public void Dispose_WithAHungDecoder_ReturnsAtOnce()
    {
        var hung = new HangingDecoder();
        var sink = MakeWithHangLimit(_ => hung, TimeSpan.FromHours(1), new List<int>());
        try
        {
            sink.GotVideoFrame(Ep, 0, Key(1), H264);
            Assert.True(hung.Entered.Wait(5000));

            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            sink.Dispose();
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(200));
            Assert.False(hung.Disposed);   // not disposed behind the back of a thread that is inside it
        }
        finally { hung.Release.Set(); }
    }

    [Fact]
    public void Dispose_WithAnIdleDecoder_StopsTheThreadAndDisposesTheDecoder()
    {
        var decoder = new FakeDecoder(_ => true);
        var (sink, _) = Make(_ => decoder);
        Feed(sink, Ep, 0, Key(1), H264);

        sink.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!decoder.Disposed && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(decoder.Disposed);
    }

    // Decodes the first <ok> access units normally, then blocks forever (a hang in the middle of a long GOP).
    private sealed class HangAfterDecoder(int ok) : IH264FrameDecoder
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly ManualResetEventSlim Entered = new();
        private int _calls;
        public int Decode(byte[] annexB, Action<byte[], int, int> onFrame)
        {
            if (++_calls > ok) { Entered.Set(); Release.Wait(); }
            onFrame(new byte[16], 2, 2);
            return 1;
        }
        public void Dispose() { }
    }

    [Fact]
    public void HangDeepInALongGop_ReplaysTheWholeGopInOrder_NotJustWhatFitsTheQueue()
    {
        const int gopLength = 300;   // far more than the queue capacity (90): hosts send keyframes rarely
        var hung = new HangAfterDecoder(gopLength);
        var software = new FakeDecoder(_ => true);
        var pictures = new List<int>();
        using var sink = MakeWithHangLimit(p => p == H264Path.MediaFoundationGpu ? hung : software, TimeSpan.FromMilliseconds(200), pictures);
        var sent = new List<byte>();
        void Send(int i)
        {
            var tag = (byte)(i == 0 ? 1 : 2 + i % 200);    // 1 = the keyframe; the rest cycle so that order is checkable
            sent.Add(tag);
            sink.GotVideoFrame(Ep, (uint)i, i == 0 ? Key(tag) : Delta(tag), H264);
        }
        try
        {
            for (var i = 0; i < gopLength; i++)
            {
                Send(i);
                Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));
            }
            Send(gopLength);                              // this one hangs the decoder
            Assert.True(hung.Entered.Wait(5000));
            Send(gopLength + 1);
            Send(gopLength + 2);
            Thread.Sleep(400);
            Send(gopLength + 3);                          // notices the hang
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));

            Assert.Equal(H264Path.MediaFoundationSoftware, sink.CurrentPath);
            Assert.Equal(sent, software.Seen);            // whole GOP from the keyframe, then the queued and live frames, in order
            var before = pictures.Count;
            Send(gopLength + 4);
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));
            Assert.Equal(before + 1, pictures.Count);     // pictures continue without a new keyframe
        }
        finally { hung.Release.Set(); }
    }

    // Throws an ordinary exception, but only when released: the test releases it after the sink has abandoned the thread,
    // which is the order that used to let the exception escape the decoder thread and crash the process.
    private sealed class ThrowWhenReleasedDecoder : IH264FrameDecoder
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly ManualResetEventSlim Entered = new();
        public int Decode(byte[] annexB, Action<byte[], int, int> onFrame)
        {
            Entered.Set();
            Release.Wait();
            throw new InvalidOperationException("decoder died while being abandoned");
        }
        public void Dispose() { }
    }

    [Fact]
    public void ExceptionThrownAfterTheThreadWasAbandoned_DoesNotEscapeTheDecoderThread()
    {
        var unhandled = new List<object>();
        UnhandledExceptionEventHandler hook = (_, e) => { lock (unhandled) unhandled.Add(e.ExceptionObject); };
        AppDomain.CurrentDomain.UnhandledException += hook;
        var first = new ThrowWhenReleasedDecoder();
        var software = new FakeDecoder(_ => true);
        var pictures = new List<int>();
        using var sink = MakeWithHangLimit(p => p == H264Path.MediaFoundationGpu ? first : software, TimeSpan.FromMilliseconds(150), pictures);
        try
        {
            sink.GotVideoFrame(Ep, 0, Key(1), H264);
            Assert.True(first.Entered.Wait(5000));
            Thread.Sleep(300);
            sink.GotVideoFrame(Ep, 1, Delta(2), H264);              // abandons the thread that sits in Decode
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));

            first.Release.Set();                                    // now it throws, after the abandon flag is set
            Thread.Sleep(300);

            lock (unhandled) Assert.Empty(unhandled);               // before the fix the process died here
            Feed(sink, Ep, 2, Delta(3), H264);
            Assert.Equal(new byte[] { 1, 2, 3 }, software.Seen);    // and the new thread carries on
        }
        finally { AppDomain.CurrentDomain.UnhandledException -= hook; first.Release.Set(); }
    }

    [Fact]
    public void SecondHangInTheMiddleOfARecoveryReplay_StillGivesTheNextDecoderTheWholeGop()
    {
        const int gopLength = 300;
        var gpu = new HangAfterDecoder(gopLength);                // hangs on frame 300
        var software = new HangAfterDecoder(150);                 // hangs half way through the 300-frame recovery replay
        var ffmpeg = Substitute.For<IVideoSink>();
        var seen = new List<byte>();
        ffmpeg.When(f => f.GotVideoFrame(Arg.Any<IPEndPoint>(), Arg.Any<uint>(), Arg.Any<byte[]>(), Arg.Any<VideoFormat>()))
            .Do(call => seen.Add(call.ArgAt<byte[]>(2)[5]));
        var sink = new MediaFoundationVideoSink(NullLogger.Instance, p => p == H264Path.MediaFoundationGpu ? gpu : software, () => ffmpeg, H264Path.MediaFoundationGpu)
        { HangLimit = TimeSpan.FromMilliseconds(200) };
        var sent = new List<byte>();
        void Send(int i)
        {
            var tag = (byte)(i == 0 ? 1 : 2 + i % 200);
            sent.Add(tag);
            sink.GotVideoFrame(Ep, (uint)i, i == 0 ? Key(tag) : Delta(tag), H264);
        }
        using (sink)
        try
        {
            for (var i = 0; i < gopLength; i++)
            {
                Send(i);
                Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));
            }
            Send(gopLength);                                      // hangs the first decoder
            Assert.True(gpu.Entered.Wait(5000));
            Thread.Sleep(400);
            Send(gopLength + 1);                                  // abandon 1: software starts replaying and hangs at its 151st frame
            Assert.True(software.Entered.Wait(5000));
            Send(gopLength + 2);                                  // queued behind the second hang
            Thread.Sleep(400);
            Send(gopLength + 3);                                  // abandon 2: capped, straight to FFmpeg
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));
            Send(gopLength + 4);                                  // live frame after the recovery
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));

            Assert.Equal(H264Path.FFmpeg, sink.CurrentPath);
            Assert.Equal(sent, seen);                             // the whole GOP in order, then the live frames, each once
        }
        finally { gpu.Release.Set(); software.Release.Set(); }
    }

    // Fails every decode, then hangs in Dispose (native cleanup after a GPU fault can block like a decode).
    private sealed class FailsThenHangsOnDispose : IH264FrameDecoder
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly ManualResetEventSlim DisposeEntered = new();
        public int Decode(byte[] annexB, Action<byte[], int, int> onFrame) => throw new InvalidOperationException("device lost");
        public void Dispose() { DisposeEntered.Set(); Release.Wait(); }
    }

    [Fact]
    public void DecoderWhoseDisposeHangs_DuringStepDown_IsAbandoned_AndAnotherDecoderTakesOver()
    {
        var broken = new FailsThenHangsOnDispose();
        var software = new FakeDecoder(_ => true);
        var ffmpeg = Substitute.For<IVideoSink>();
        var sink = new MediaFoundationVideoSink(NullLogger.Instance, p => p == H264Path.MediaFoundationGpu ? broken : software,
            () => ffmpeg, H264Path.MediaFoundationGpu) { HangLimit = TimeSpan.FromMilliseconds(150) };
        try
        {
            sink.GotVideoFrame(Ep, 0, Key(1), H264);
            sink.GotVideoFrame(Ep, 1, Delta(2), H264);
            sink.GotVideoFrame(Ep, 2, Delta(3), H264);   // third failure: step down, Dispose hangs
            Assert.True(broken.DisposeEntered.Wait(5000));
            Thread.Sleep(300);
            var sw = Stopwatch.StartNew();
            sink.GotVideoFrame(Ep, 3, Delta(4), H264);   // notices the hung cleanup
            Assert.True(sw.ElapsedMilliseconds < 100);   // the receive thread never waits for it
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));

            Assert.NotEqual(H264Path.MediaFoundationGpu, sink.CurrentPath);
            // The next decoder got the whole GOP from the keyframe (no freeze until the next keyframe).
            var replayedToSoftware = software.Seen.Count > 0 && software.Seen[0] == 1;
            var replayedToFfmpeg = ffmpeg.ReceivedCalls().Any();
            Assert.True(replayedToSoftware || replayedToFfmpeg);
        }
        finally
        {
            broken.Release.Set();
            sink.Dispose();
        }
    }

    [Fact]
    public void HangOnTheLastFrame_OfAStaticScreen_IsNoticedWithoutAnotherFrame()
    {
        var hung = new HangingDecoder();
        var software = new FakeDecoder(_ => true);
        var pictures = new List<int>();
        var sink = MakeWithHangLimit(p => p == H264Path.MediaFoundationGpu ? hung : software, TimeSpan.FromMilliseconds(150), pictures);
        try
        {
            sink.GotVideoFrame(Ep, 0, Key(1), H264);   // the last change of the screen; nothing follows
            Assert.True(hung.Entered.Wait(5000));

            // The timer alone must abandon the decoder and replay the keyframe into the next one.
            var deadline = Stopwatch.StartNew();
            while (software.Seen.Count == 0 && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(20);

            Assert.Equal(new byte[] { 1 }, software.Seen);
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(5)));
            lock (pictures) Assert.Single(pictures);    // the update that had arrived is shown after all
        }
        finally
        {
            hung.Release.Set();
            sink.Dispose();
        }
    }

    [Fact]
    public void ReplacementThreadAlreadyWaiting_IsWokenForTheRecoveryReplay()
    {
        var hung = new HangingDecoder();
        var software = new FakeDecoder(_ => true);
        // The pause lets the new thread reach its wait on the empty queue before the replay is put in (the race Codex found).
        var sink = new MediaFoundationVideoSink(NullLogger.Instance, p => p == H264Path.MediaFoundationGpu ? hung : software,
            () => Substitute.For<IVideoSink>(), H264Path.MediaFoundationGpu)
        { HangLimit = TimeSpan.FromMilliseconds(150), AfterReplacementStarted = () => Thread.Sleep(200) };
        try
        {
            sink.GotVideoFrame(Ep, 0, Key(1), H264);   // static screen: nothing follows
            Assert.True(hung.Entered.Wait(5000));
            var deadline = Stopwatch.StartNew();
            while (software.Seen.Count == 0 && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            Assert.Equal(new byte[] { 1 }, software.Seen);
        }
        finally
        {
            hung.Release.Set();
            sink.Dispose();
        }
    }

    [Fact]
    public void DecoderGoingSilent_AndThenNoMoreFrames_IsStillReplacedWithinBoundedTime()
    {
        // Works for the keyframe and one delta, then accepts input but gives no pictures (device swapped under it).
        var goesSilent = new SilentAfterDecoder(2);
        var software = new FakeDecoder(_ => true);
        var pictures = new List<int>();
        var sink = MakeWithHangLimit(p => p == H264Path.MediaFoundationGpu ? goesSilent : software, TimeSpan.FromSeconds(2), pictures);
        try
        {
            sink.GotVideoFrame(Ep, 0, Key(1), H264);
            sink.GotVideoFrame(Ep, 1, Delta(2), H264);
            sink.GotVideoFrame(Ep, 2, Delta(3), H264);   // the last changes of a static screen: silent from here on
            sink.GotVideoFrame(Ep, 3, Delta(4), H264);
            var deadline = Stopwatch.StartNew();
            while (software.Seen.Count == 0 && deadline.ElapsedMilliseconds < 6000) Thread.Sleep(50);

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, software.Seen);   // the whole GOP, no new frame was needed
            Assert.InRange(deadline.ElapsedMilliseconds, 0, 4000);
            Assert.Equal(H264Path.MediaFoundationSoftware, sink.CurrentPath);
        }
        finally { sink.Dispose(); }
    }

    private sealed class SilentAfterDecoder(int pictures) : IH264FrameDecoder
    {
        private int _n;
        public int Decode(byte[] annexB, Action<byte[], int, int> onFrame)
        {
            if (++_n > pictures) return 0;
            onFrame(new byte[16], 2, 2);
            return 1;
        }
        public void Dispose() { }
    }

    // Blocks the first call until released (a slow moment, well below the hang limit), then decodes normally.
    private sealed class SlowStartDecoder : IH264FrameDecoder
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly ManualResetEventSlim Entered = new();
        public readonly List<byte> Seen = new();
        public int Decode(byte[] annexB, Action<byte[], int, int> onFrame)
        {
            Entered.Set();
            Release.Wait();
            lock (Seen) Seen.Add(annexB[5]);
            onFrame(new byte[16], 2, 2);
            return 1;
        }
        public void Dispose() { }
    }

    [Fact]
    public void QueueOverflow_DoesNotLoseTheLastDelta_TheDecoderCatchesUpFromTheKeyframe()
    {
        var slow = new SlowStartDecoder();
        var pictures = new List<int>();
        var sink = MakeWithHangLimit(_ => slow, TimeSpan.FromSeconds(30), pictures);
        try
        {
            var sent = new List<byte> { 1 };
            sink.GotVideoFrame(Ep, 0, Key(1), H264);
            Assert.True(slow.Entered.Wait(5000));             // decoder busy on the keyframe
            for (var i = 1; i <= 150; i++)                      // more than the queue holds (90)
            {
                var tag = (byte)(2 + i % 200);
                sent.Add(tag);
                sink.GotVideoFrame(Ep, (uint)i, Delta(tag), H264);
            }
            slow.Release.Set();                                 // the screen is static now: nothing else arrives
            Assert.True(sink.WaitIdle(TimeSpan.FromSeconds(10)));

            lock (slow.Seen)
            {
                Assert.Equal(sent.Last(), slow.Seen.Last());    // the last change was decoded
                // ... after a catch-up that fed the whole GOP in order from the keyframe (no broken reference chain).
                var lastKey = slow.Seen.LastIndexOf(1);
                Assert.Equal(sent, slow.Seen.Skip(lastKey).ToList());
            }
        }
        finally
        {
            slow.Release.Set();
            sink.Dispose();
        }
    }
}
