using DeskShare.Core.Models;
using DeskShare.Core.Conversion;

namespace DeskShare.UnitTests.Conversion;

/// <summary>
/// Unit tests for PixelConverter BGRA to I420 conversion.
/// </summary>
public sealed class PixelConverterTests
{
    private readonly PixelConverter _converter = new();

    [Fact]
    public void Convert_ValidFrame_ReturnsVideoFrame()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        var frame = CreateTestFrame(width, height);

        // Act
        var videoFrame = _converter.Convert(frame);

        // Assert
        Assert.NotNull(videoFrame);
        Assert.Equal(width, videoFrame.Width);
        Assert.Equal(height, videoFrame.Height);
        Assert.Equal(width, videoFrame.YStride);
        Assert.Equal(width / 2, videoFrame.UStride);
        Assert.Equal(width / 2, videoFrame.VStride);
    }

    [Fact]
    public void Convert_NullFrame_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _converter.Convert(null!));
    }

    [Fact]
    public void Convert_OddWidth_ThrowsArgumentException()
    {
        // Arrange - width is odd
        var frame = CreateTestFrame(1921, 1080);

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => _converter.Convert(frame));
        Assert.Contains("must be even", exception.Message);
    }

    [Fact]
    public void Convert_OddHeight_ThrowsArgumentException()
    {
        // Arrange - height is odd
        var frame = CreateTestFrame(1920, 1081);

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => _converter.Convert(frame));
        Assert.Contains("must be even", exception.Message);
    }

    [Fact]
    public void Convert_WhitePixel_CorrectYUVValues()
    {
        // Arrange - 2x2 frame with white pixels (R=255, G=255, B=255)
        var width = 2;
        var height = 2;
        var data = new byte[width * height * 4];

        // Fill with white (BGRA = 255, 255, 255, 255)
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = 255;     // B
            data[i + 1] = 255; // G
            data[i + 2] = 255; // R
            data[i + 3] = 255; // A
        }

        var frame = new Frame(width, height, data, width * 4, DateTime.UtcNow);

        // Act
        var videoFrame = _converter.Convert(frame);

        // Assert - White in YUV: Y=235, U=128, V=128 (ITU-R BT.601)
        // With our conversion: Y should be ~235, U/V should be ~128
        Assert.InRange(videoFrame.YPlane[0], 230, 240); // Y value for white
        Assert.InRange(videoFrame.UPlane[0], 125, 131); // U value (neutral)
        Assert.InRange(videoFrame.VPlane[0], 125, 131); // V value (neutral)
    }

    [Fact]
    public void Convert_BlackPixel_CorrectYUVValues()
    {
        // Arrange - 2x2 frame with black pixels (R=0, G=0, B=0)
        var width = 2;
        var height = 2;
        var data = new byte[width * height * 4]; // Already zeros (black)

        var frame = new Frame(width, height, data, width * 4, DateTime.UtcNow);

        // Act
        var videoFrame = _converter.Convert(frame);

        // Assert - Black in YUV: Y=16, U=128, V=128 (ITU-R BT.601)
        Assert.InRange(videoFrame.YPlane[0], 14, 18);   // Y value for black
        Assert.InRange(videoFrame.UPlane[0], 125, 131); // U value (neutral)
        Assert.InRange(videoFrame.VPlane[0], 125, 131); // V value (neutral)
    }

    [Fact]
    public void Convert_RedPixel_CorrectYUVValues()
    {
        // Arrange - 2x2 frame with red pixels (R=255, G=0, B=0)
        var width = 2;
        var height = 2;
        var data = new byte[width * height * 4];

        // Fill with red (BGRA = 0, 0, 255, 255)
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = 0;       // B
            data[i + 1] = 0;   // G
            data[i + 2] = 255; // R
            data[i + 3] = 255; // A
        }

        var frame = new Frame(width, height, data, width * 4, DateTime.UtcNow);

        // Act
        var videoFrame = _converter.Convert(frame);

        // Assert - Red has high V value
        Assert.InRange(videoFrame.YPlane[0], 70, 90);   // Y value
        Assert.InRange(videoFrame.UPlane[0], 80, 100);  // U value (low for red)
        Assert.InRange(videoFrame.VPlane[0], 230, 250); // V value (high for red)
    }

    [Fact]
    public void ConvertAndScale_SameDimensions_Success()
    {
        // Arrange
        var frame = CreateTestFrame(1920, 1080);

        // Act
        var videoFrame = _converter.ConvertAndScale(frame, 1920, 1080);

        // Assert
        Assert.NotNull(videoFrame);
        Assert.Equal(1920, videoFrame.Width);
        Assert.Equal(1080, videoFrame.Height);
    }

    [Fact]
    public void ConvertAndScale_DifferentDimensions_ThrowsNotImplementedException()
    {
        // Arrange
        var frame = CreateTestFrame(1920, 1080);

        // Act & Assert - Scaling not yet implemented
        Assert.Throws<NotImplementedException>(
            () => _converter.ConvertAndScale(frame, 1280, 720));
    }

    [Fact]
    public void ConvertAndScale_OddTargetDimensions_ThrowsArgumentException()
    {
        // Arrange
        var frame = CreateTestFrame(1920, 1080);

        // Act & Assert
        Assert.Throws<ArgumentException>(
            () => _converter.ConvertAndScale(frame, 1921, 1080));
    }

    /// <summary>
    /// Creates a test frame with specified dimensions filled with gray pixels.
    /// </summary>
    private static Frame CreateTestFrame(int width, int height)
    {
        var stride = width * 4;
        var data = new byte[stride * height];

        // Fill with gray (128, 128, 128, 255) for testing
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = 128;     // B
            data[i + 1] = 128; // G
            data[i + 2] = 128; // R
            data[i + 3] = 255; // A
        }

        return new Frame(width, height, data, stride, DateTime.UtcNow);
    }
}
