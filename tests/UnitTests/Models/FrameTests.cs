using DeskShare.Core.Models;

namespace DeskShare.UnitTests.Models;

/// <summary>
/// Unit tests for Frame model validation and construction.
/// </summary>
public sealed class FrameTests
{
    [Fact]
    public void FromPooled_Dispose_ReturnsBufferToPool()
    {
        // Arrange: rent a buffer big enough for a 2x2 BGRA frame; pool may hand back a larger array
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(16);
        var frame = Frame.FromPooled(2, 2, rented, 8, DateTime.UtcNow);
        Assert.Same(rented, frame.Data);

        // Act
        frame.Dispose();
        frame.Dispose(); // idempotent - must not double-return

        // Assert: the very next rent of the same size hands back the returned array
        var again = System.Buffers.ArrayPool<byte>.Shared.Rent(16);
        Assert.Same(rented, again);
        System.Buffers.ArrayPool<byte>.Shared.Return(again);
    }

    [Fact]
    public void Constructor_ValidParameters_CreatesFrame()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        var stride = width * 4;
        var data = new byte[stride * height];
        var timestamp = DateTime.UtcNow;

        // Act
        var frame = new Frame(width, height, data, stride, timestamp);

        // Assert
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(stride, frame.Stride);
        Assert.Same(data, frame.Data);
        Assert.Equal(timestamp, frame.Timestamp);
    }

    [Fact]
    public void Constructor_ZeroWidth_ThrowsArgumentException()
    {
        // Arrange
        var width = 0;
        var height = 1080;
        var data = new byte[100];
        var stride = 4;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new Frame(width, height, data, stride, DateTime.UtcNow));

        Assert.Contains("Width must be positive", exception.Message);
    }

    [Fact]
    public void Constructor_NegativeHeight_ThrowsArgumentException()
    {
        // Arrange
        var width = 1920;
        var height = -1080;
        var data = new byte[100];
        var stride = width * 4;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new Frame(width, height, data, stride, DateTime.UtcNow));

        Assert.Contains("Height must be positive", exception.Message);
    }

    [Fact]
    public void Constructor_NullData_ThrowsArgumentNullException()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        byte[]? data = null;
        var stride = width * 4;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => new Frame(width, height, data!, stride, DateTime.UtcNow));
    }

    [Fact]
    public void Constructor_InvalidStride_ThrowsArgumentException()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        var data = new byte[100];
        var stride = width * 2; // Too small for BGRA (should be width * 4)

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => new Frame(width, height, data, stride, DateTime.UtcNow));

        Assert.Contains("Stride must be at least width * 4", exception.Message);
    }

    [Fact]
    public void Dispose_MultipleCall_DoesNotThrow()
    {
        // Arrange
        var frame = CreateValidFrame();

        // Act & Assert (should not throw)
        frame.Dispose();
        frame.Dispose();
    }

    private static Frame CreateValidFrame()
    {
        var width = 1920;
        var height = 1080;
        var stride = width * 4;
        var data = new byte[stride * height];
        return new Frame(width, height, data, stride, DateTime.UtcNow);
    }
}
