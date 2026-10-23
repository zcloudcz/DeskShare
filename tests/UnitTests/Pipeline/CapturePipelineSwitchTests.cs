using System.Collections.Concurrent;
using DeskShare.Core.Conversion;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.Pipeline;

namespace DeskShare.UnitTests.Pipeline;

/// <summary>Switching the capturer of a running pipeline (display switch) with fake capturers and a recording video source.</summary>
public sealed class CapturePipelineSwitchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Switch_UsesNewCapturer_DisposesOld_ForcesKeyFrame_AndFollowsTheNewSize()
    {
        var first = new FakeCapturer(1920, 1080);
        var second = new FakeCapturer(1280, 1024);
        var source = new RecordingVideoSource();
        using var pipeline = new CapturePipeline(first, new SimdPixelConverter(), source);
        Assert.True(pipeline.Start(30));
        await source.WaitForFrameAsync(1920, 1080);
        var keyFramesBefore = source.KeyFrameRequests;

        var switched = await pipeline.SwitchCapturerAsync(second).WaitAsync(Timeout);

        Assert.True(switched);
        Assert.True(second.Initialized);
        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        // Frames now arrive in the new capture size (the output is not pinned to the first display's size) ...
        await source.WaitForFrameAsync(1280, 1024);
        // ... and the encoder was told to start from a keyframe.
        Assert.True(source.KeyFrameRequests > keyFramesBefore);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Switch_ToACapturerThatFailsToInitialize_KeepsTheOldOne()
    {
        var first = new FakeCapturer(1920, 1080);
        var broken = new FakeCapturer(1280, 1024) { InitializeResult = false };
        var source = new RecordingVideoSource();
        using var pipeline = new CapturePipeline(first, new SimdPixelConverter(), source);
        Assert.True(pipeline.Start(30));
        await source.WaitForFrameAsync(1920, 1080);

        var switched = await pipeline.SwitchCapturerAsync(broken).WaitAsync(Timeout);

        Assert.False(switched);
        Assert.True(broken.Disposed);
        Assert.False(first.Disposed);
        await source.WaitForFrameAsync(1920, 1080, afterReset: true); // still streaming from the old display
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Switch_OnAStoppedPipeline_DisposesTheNewCapturerAndFails()
    {
        var next = new FakeCapturer(800, 600);
        using var pipeline = new CapturePipeline(new FakeCapturer(1920, 1080), new SimdPixelConverter(), new RecordingVideoSource());

        Assert.False(await pipeline.SwitchCapturerAsync(next).WaitAsync(Timeout));
        Assert.True(next.Disposed);
    }

    [Fact]
    public async Task Switch_RespectsMaxOutputWidth()
    {
        var source = new RecordingVideoSource();
        using var pipeline = new CapturePipeline(new FakeCapturer(1920, 1080), new SimdPixelConverter(), source, maxOutputWidth: 960);
        Assert.True(pipeline.Start(30));
        await source.WaitForFrameAsync(960, 540);

        Assert.True(await pipeline.SwitchCapturerAsync(new FakeCapturer(3840, 1600)).WaitAsync(Timeout));

        await source.WaitForFrameAsync(960, 400);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Switch_NeverSendsOldDisplayFramesAfterTheFirstNewOne_AndRunsTheCallbackThen()
    {
        // A slow encoder keeps the queues full of old-display frames when the switch happens.
        var source = new RecordingVideoSource { PushDelayMs = 40 };
        using var pipeline = new CapturePipeline(new FakeCapturer(1920, 1080), new SimdPixelConverter(), source);
        Assert.True(pipeline.Start(30));
        await source.WaitForFrameAsync(1920, 1080);

        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await pipeline.SwitchCapturerAsync(new FakeCapturer(1280, 1024), onFirstFrameSent: () =>
        {
            source.Mark();
            callbackRan.SetResult();
        }).WaitAsync(Timeout));
        await callbackRan.Task.WaitAsync(Timeout);
        await source.WaitForFrameAsync(1280, 1024);
        await pipeline.StopAsync();

        var log = source.Log();
        var marker = log.IndexOf((0, 0));
        Assert.True(marker > 0);
        Assert.Equal((1280, 1024), log[marker - 1]); // the callback follows a new-display frame, never an old one
        Assert.DoesNotContain((1920, 1080), log.Skip(marker - 1)); // and no old frame is sent from the first new frame on
    }

    [Fact]
    public async Task Switch_CancelledByTheGuard_KeepsTheOldCapturer_AndSkipsTheCallback()
    {
        var first = new FakeCapturer(1920, 1080);
        var next = new FakeCapturer(1280, 1024);
        var source = new RecordingVideoSource();
        using var pipeline = new CapturePipeline(first, new SimdPixelConverter(), source);
        Assert.True(pipeline.Start(30));
        await source.WaitForFrameAsync(1920, 1080);
        var callbackRan = false;

        var switched = await pipeline.SwitchCapturerAsync(next, canCommit: () => false, onFirstFrameSent: () => callbackRan = true).WaitAsync(Timeout);

        Assert.False(switched);
        Assert.True(next.Disposed);
        Assert.False(first.Disposed);
        await Task.Delay(200);
        Assert.False(callbackRan);
        await pipeline.StopAsync();
    }

    private sealed class FakeCapturer(int width, int height) : IScreenCapturer
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public bool InitializeResult { get; init; } = true;
        public volatile bool Initialized;
        public volatile bool Disposed;

        public bool Initialize()
        {
            Initialized = InitializeResult;
            return InitializeResult;
        }

        public bool TryAcquireFrame(out Frame? frame)
        {
            frame = new Frame(Width, Height, new byte[Width * Height * 4], Width * 4, DateTime.UtcNow);
            return true;
        }

        public void ReleaseFrame() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingVideoSource : IVideoSource
    {
        private readonly ConcurrentQueue<(int, int)> _sizes = new();
        private int _keyFrames;
        public int KeyFrameRequests => Volatile.Read(ref _keyFrames);
        public bool IsActive => IsInitialized;
        public bool IsInitialized { get; private set; }

        public bool Initialize(int width, int height, int frameRate)
        {
            IsInitialized = true;
            return true;
        }

        public int PushDelayMs { get; init; }

        public bool PushFrame(VideoFrame frame)
        {
            _sizes.Enqueue((frame.Width, frame.Height));
            if (PushDelayMs > 0)
                Thread.Sleep(PushDelayMs);
            return true;
        }

        /// <summary>Puts a (0,0) entry into the push log, e.g. from a callback, to see what was sent before it.</summary>
        public void Mark() => _sizes.Enqueue((0, 0));
        public List<(int, int)> Log() => _sizes.ToList();

        public void RequestKeyFrame() => Interlocked.Increment(ref _keyFrames);
        public VideoSourceStatistics GetStatistics() => new();
        public void Dispose() { }

        /// <summary>Waits until a frame of the given size is pushed; with <paramref name="afterReset"/> only frames pushed from now on count.</summary>
        public async Task WaitForFrameAsync(int width, int height, bool afterReset = false)
        {
            if (afterReset)
                _sizes.Clear();
            var deadline = DateTime.UtcNow + Timeout;
            while (!_sizes.Contains((width, height)))
            {
                Assert.True(DateTime.UtcNow < deadline, $"no {width}x{height} frame; saw {string.Join(",", _sizes.Distinct())}");
                await Task.Delay(20);
            }
        }
    }
}
