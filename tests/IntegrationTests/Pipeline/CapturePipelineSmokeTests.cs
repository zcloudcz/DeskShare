using DeskShare.Core.Platforms.Windows;
using DeskShare.Core.Conversion;
using DeskShare.Core.Pipeline;
using DeskShare.Core.Video;

namespace DeskShare.IntegrationTests.Pipeline;

/// <summary>
/// Smoke tests for CapturePipeline to ensure basic functionality without actual screen capture.
/// These tests verify that components can be initialized and work together.
/// </summary>
public sealed class CapturePipelineSmokeTests
{
    [Fact]
    public void DesktopDuplicator_Initialize_DoesNotThrow()
    {
        // Arrange
        using var capturer = new DesktopDuplicator(0, 0);

        // Act & Assert - Should not throw
        // Note: May return false if no compatible adapter, but should not throw
        var result = capturer.Initialize();

        // We accept both true and false, just verify it doesn't throw
        Assert.True(result || !result); // Always passes, just tests no exception
    }

    [Fact]
    public void PixelConverter_Convert_SmallFrame_DoesNotThrow()
    {
        // Arrange
        var converter = new PixelConverter();
        var width = 4;
        var height = 4;
        var data = new byte[width * height * 4];

        // Fill with test pattern
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = 128;     // B
            data[i + 1] = 128; // G
            data[i + 2] = 128; // R
            data[i + 3] = 255; // A
        }

        using var frame = new DeskShare.Core.Models.Frame(
            width, height, data, width * 4, DateTime.UtcNow);

        // Act
        using var videoFrame = converter.Convert(frame);

        // Assert
        Assert.NotNull(videoFrame);
        Assert.Equal(width, videoFrame.Width);
        Assert.Equal(height, videoFrame.Height);
    }

    [Fact]
    public void StubVideoSource_Initialize_Succeeds()
    {
        // Arrange
        using var videoSource = new StubVideoSource();

        // Act
        var result = videoSource.Initialize(1920, 1080, 30);

        // Assert
        Assert.True(result);
        Assert.True(videoSource.IsActive);
    }

    [Fact]
    public void StubVideoSource_PushFrame_AfterInitialize_Succeeds()
    {
        // Arrange
        using var videoSource = new StubVideoSource();
        videoSource.Initialize(4, 4, 30);

        var videoFrame = CreateTestVideoFrame(4, 4);

        // Act
        var result = videoSource.PushFrame(videoFrame);

        // Assert
        Assert.True(result);

        var stats = videoSource.GetStatistics();
        Assert.Equal(1, stats.FramesPushed);
    }

    [Fact]
    public void StubVideoSource_GetStatistics_ReturnsValidData()
    {
        // Arrange
        using var videoSource = new StubVideoSource();
        videoSource.Initialize(4, 4, 30);

        // Act
        var stats = videoSource.GetStatistics();

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(0, stats.FramesPushed);
        Assert.Equal(0, stats.FramesDropped);
        Assert.True(stats.CurrentFps >= 0);
    }

    [Fact]
    public void CapturePipeline_Construction_DoesNotThrow()
    {
        // Arrange
        using var capturer = new DesktopDuplicator(0, 0);
        var converter = new PixelConverter();
        using var videoSource = new StubVideoSource();

        // Act & Assert - Should not throw
        using var pipeline = new CapturePipeline(capturer, converter, videoSource);

        Assert.NotNull(pipeline);
        Assert.False(pipeline.IsRunning);
    }

    /// <summary>
    /// Creates a test video frame with specified dimensions.
    /// </summary>
    private static DeskShare.Core.Models.VideoFrame CreateTestVideoFrame(int width, int height)
    {
        var yPlane = new byte[width * height];
        var uPlane = new byte[(width / 2) * (height / 2)];
        var vPlane = new byte[(width / 2) * (height / 2)];

        // Fill with mid-gray values
        Array.Fill(yPlane, (byte)128);
        Array.Fill(uPlane, (byte)128);
        Array.Fill(vPlane, (byte)128);

        return new DeskShare.Core.Models.VideoFrame(
            width, height,
            yPlane, uPlane, vPlane,
            width, width / 2, width / 2,
            DateTime.UtcNow);
    }
}
