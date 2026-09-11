using DeskShare.Core.Models;

namespace DeskShare.UnitTests.Models;

/// <summary>
/// Unit tests for VideoFrame model validation and construction.
/// </summary>
public sealed class VideoFrameTests
{
    [Fact]
    public void Constructor_ValidParameters_CreatesVideoFrame()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        var yPlane = new byte[width * height];
        var uPlane = new byte[(width / 2) * (height / 2)];
        var vPlane = new byte[(width / 2) * (height / 2)];
        var timestamp = DateTime.UtcNow;

        // Act
        var frame = new VideoFrame(
            width, height,
            yPlane, uPlane, vPlane,
            width, width / 2, width / 2,
            timestamp);

        // Assert
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Same(yPlane, frame.YPlane);
        Assert.Same(uPlane, frame.UPlane);
        Assert.Same(vPlane, frame.VPlane);
        Assert.Equal(width, frame.YStride);
        Assert.Equal(width / 2, frame.UStride);
        Assert.Equal(width / 2, frame.VStride);
        Assert.Equal(timestamp, frame.Timestamp);
    }

    [Fact]
    public void Constructor_OddWidth_ThrowsArgumentException()
    {
        // Arrange
        var width = 1921; // Odd
        var height = 1080;
        var yPlane = new byte[100];
        var uPlane = new byte[100];
        var vPlane = new byte[100];

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new VideoFrame(width, height, yPlane, uPlane, vPlane, width, width / 2, width / 2, DateTime.UtcNow));

        Assert.Contains("must be positive and even", exception.Message);
    }

    [Fact]
    public void Constructor_OddHeight_ThrowsArgumentException()
    {
        // Arrange
        var width = 1920;
        var height = 1081; // Odd
        var yPlane = new byte[100];
        var uPlane = new byte[100];
        var vPlane = new byte[100];

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new VideoFrame(width, height, yPlane, uPlane, vPlane, width, width / 2, width / 2, DateTime.UtcNow));

        Assert.Contains("must be positive and even", exception.Message);
    }

    [Fact]
    public void Constructor_NullYPlane_ThrowsArgumentNullException()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        byte[]? yPlane = null;
        var uPlane = new byte[100];
        var vPlane = new byte[100];

        // Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => new VideoFrame(width, height, yPlane!, uPlane, vPlane, width, width / 2, width / 2, DateTime.UtcNow));
    }

    [Fact]
    public void Constructor_NullUPlane_ThrowsArgumentNullException()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        var yPlane = new byte[100];
        byte[]? uPlane = null;
        var vPlane = new byte[100];

        // Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => new VideoFrame(width, height, yPlane, uPlane!, vPlane, width, width / 2, width / 2, DateTime.UtcNow));
    }

    [Fact]
    public void Constructor_InvalidYStride_ThrowsArgumentException()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        var yPlane = new byte[100];
        var uPlane = new byte[100];
        var vPlane = new byte[100];
        var invalidStride = width - 1; // Too small

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new VideoFrame(width, height, yPlane, uPlane, vPlane, invalidStride, width / 2, width / 2, DateTime.UtcNow));

        Assert.Contains("Y stride", exception.Message);
    }

    [Fact]
    public void Constructor_InvalidUStride_ThrowsArgumentException()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        var yPlane = new byte[100];
        var uPlane = new byte[100];
        var vPlane = new byte[100];
        var invalidUStride = (width / 2) - 1; // Too small

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new VideoFrame(width, height, yPlane, uPlane, vPlane, width, invalidUStride, width / 2, DateTime.UtcNow));

        Assert.Contains("U stride", exception.Message);
    }

    [Fact]
    public void Dispose_MultipleCall_DoesNotThrow()
    {
        // Arrange
        var frame = CreateValidVideoFrame();

        // Act & Assert (should not throw)
        frame.Dispose();
        frame.Dispose();
    }

    private static VideoFrame CreateValidVideoFrame()
    {
        var width = 1920;
        var height = 1080;
        return new VideoFrame(
            width, height,
            new byte[width * height],
            new byte[(width / 2) * (height / 2)],
            new byte[(width / 2) * (height / 2)],
            width, width / 2, width / 2,
            DateTime.UtcNow);
    }
}
