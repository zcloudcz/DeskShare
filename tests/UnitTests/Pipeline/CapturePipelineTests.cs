using DeskShare.Core.Conversion;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.Pipeline;

namespace DeskShare.UnitTests.Pipeline;

public sealed class CapturePipelineTests
{
    [Theory]
    [InlineData(3440, 1440, 1920, 1920, 804)]
    [InlineData(1920, 1080, 1920, 1920, 1080)] // no scaling needed
    [InlineData(3440, 1440, 0, 3440, 1440)]    // native
    public async Task Pipeline_DeliversFramesAtOutputSize(int srcW, int srcH, int maxWidth, int expectedW, int expectedH)
    {
        var capturer = new FakeCapturer(srcW, srcH);
        var source = new RecordingVideoSource();
        using var pipeline = new CapturePipeline(capturer, new SimdPixelConverter(), source, maxWidth);

        Assert.True(pipeline.Start(30));
        var got = await Task.WhenAny(source.FirstFrame.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        await pipeline.StopAsync();

        Assert.Same(source.FirstFrame.Task, got);
        Assert.Equal((expectedW, expectedH), source.FirstFrame.Task.Result);
        // The video source must be initialised with the output size, not the screen size.
        Assert.Equal((expectedW, expectedH), source.InitializedSize);
    }

    private sealed class FakeCapturer(int width, int height) : IScreenCapturer
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public bool Initialize() => true;

        public bool TryAcquireFrame(out Frame? frame)
        {
            frame = new Frame(Width, Height, new byte[Width * Height * 4], Width * 4, DateTime.UtcNow);
            return true;
        }

        public void ReleaseFrame() { }
        public void Dispose() { }
    }

    private sealed class RecordingVideoSource : IVideoSource
    {
        public TaskCompletionSource<(int, int)> FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public (int, int) InitializedSize { get; private set; }
        public bool IsActive => IsInitialized;
        public bool IsInitialized { get; private set; }

        public bool Initialize(int width, int height, int frameRate)
        {
            InitializedSize = (width, height);
            IsInitialized = true;
            return true;
        }

        public bool PushFrame(VideoFrame frame)
        {
            FirstFrame.TrySetResult((frame.Width, frame.Height));
            return true;
        }

        public VideoSourceStatistics GetStatistics() => new();
        public void Dispose() { }
    }
}
