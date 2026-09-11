using DeskShare.Core.Models;
using DeskShare.Core.Conversion;

namespace DeskShare.UnitTests.Conversion;

/// <summary>
/// Unit tests for PooledPixelConverter BGRA to I420 conversion with buffer pooling.
/// Tests verify that ArrayPool buffer management works correctly and produces
/// the same conversion results as the standard PixelConverter.
/// </summary>
public sealed class PooledPixelConverterTests : IDisposable
{
    private readonly PooledPixelConverter _converter = new();

    /// <summary>
    /// Tests that conversion with valid frame dimensions returns a properly formatted VideoFrame.
    /// Verifies width, height, and stride values are correct.
    /// </summary>
    [Fact]
    public void Convert_ValidFrame_ReturnsVideoFrame()
    {
        // Arrange - Create a standard HD frame
        var width = 1920;
        var height = 1080;
        var frame = CreateTestFrame(width, height);

        // Act - Convert BGRA to I420
        var videoFrame = _converter.Convert(frame);

        // Assert - Verify dimensions and strides
        Assert.NotNull(videoFrame);
        Assert.Equal(width, videoFrame.Width);
        Assert.Equal(height, videoFrame.Height);
        Assert.Equal(width, videoFrame.YStride);       // Y plane stride = width
        Assert.Equal(width / 2, videoFrame.UStride);   // U plane stride = width/2
        Assert.Equal(width / 2, videoFrame.VStride);   // V plane stride = width/2
    }

    /// <summary>
    /// Tests that null frame input throws ArgumentNullException.
    /// This validates proper input validation.
    /// </summary>
    [Fact]
    public void Convert_NullFrame_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _converter.Convert(null!));
    }

    /// <summary>
    /// Tests that odd width throws ArgumentException.
    /// I420 format requires even dimensions for proper chroma subsampling.
    /// </summary>
    [Fact]
    public void Convert_OddWidth_ThrowsArgumentException()
    {
        // Arrange - Create frame with odd width (1921 instead of 1920)
        var frame = CreateTestFrame(1921, 1080);

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => _converter.Convert(frame));
        Assert.Contains("must be even", exception.Message);
    }

    /// <summary>
    /// Tests that odd height throws ArgumentException.
    /// I420 format requires even dimensions for proper chroma subsampling.
    /// </summary>
    [Fact]
    public void Convert_OddHeight_ThrowsArgumentException()
    {
        // Arrange - Create frame with odd height (1081 instead of 1080)
        var frame = CreateTestFrame(1920, 1081);

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => _converter.Convert(frame));
        Assert.Contains("must be even", exception.Message);
    }

    /// <summary>
    /// Tests conversion of white pixel (RGB: 255,255,255).
    /// White should produce Y≈235, U≈128, V≈128 in ITU-R BT.601 color space.
    /// </summary>
    [Fact]
    public void Convert_WhitePixel_CorrectYUVValues()
    {
        // Arrange - Create 2x2 frame with white pixels
        var width = 2;
        var height = 2;
        var data = new byte[width * height * 4];

        // Fill with white (BGRA format: B=255, G=255, R=255, A=255)
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = 255;     // B (Blue)
            data[i + 1] = 255; // G (Green)
            data[i + 2] = 255; // R (Red)
            data[i + 3] = 255; // A (Alpha - ignored)
        }

        var frame = new Frame(width, height, data, width * 4, DateTime.UtcNow);

        // Act - Convert to I420
        var videoFrame = _converter.Convert(frame);

        // Assert - Verify YUV values for white
        // Expected: Y≈235 (high luminance), U≈128 (neutral), V≈128 (neutral)
        Assert.InRange(videoFrame.YPlane[0], 230, 240); // Y value for white
        Assert.InRange(videoFrame.UPlane[0], 125, 131); // U value (neutral chroma)
        Assert.InRange(videoFrame.VPlane[0], 125, 131); // V value (neutral chroma)
    }

    /// <summary>
    /// Tests conversion of black pixel (RGB: 0,0,0).
    /// Black should produce Y≈16, U≈128, V≈128 in ITU-R BT.601 color space.
    /// </summary>
    [Fact]
    public void Convert_BlackPixel_CorrectYUVValues()
    {
        // Arrange - Create 2x2 frame with black pixels
        var width = 2;
        var height = 2;
        var data = new byte[width * height * 4]; // All zeros = black

        var frame = new Frame(width, height, data, width * 4, DateTime.UtcNow);

        // Act - Convert to I420
        var videoFrame = _converter.Convert(frame);

        // Assert - Verify YUV values for black
        // Expected: Y≈16 (low luminance), U≈128 (neutral), V≈128 (neutral)
        Assert.InRange(videoFrame.YPlane[0], 14, 18);   // Y value for black
        Assert.InRange(videoFrame.UPlane[0], 125, 131); // U value (neutral chroma)
        Assert.InRange(videoFrame.VPlane[0], 125, 131); // V value (neutral chroma)
    }

    /// <summary>
    /// Tests conversion of red pixel (RGB: 255,0,0).
    /// Red should produce high V value (≈240) while U is low (≈90).
    /// </summary>
    [Fact]
    public void Convert_RedPixel_CorrectYUVValues()
    {
        // Arrange - Create 2x2 frame with red pixels
        var width = 2;
        var height = 2;
        var data = new byte[width * height * 4];

        // Fill with red (BGRA format: B=0, G=0, R=255, A=255)
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = 0;       // B (Blue)
            data[i + 1] = 0;   // G (Green)
            data[i + 2] = 255; // R (Red)
            data[i + 3] = 255; // A (Alpha - ignored)
        }

        var frame = new Frame(width, height, data, width * 4, DateTime.UtcNow);

        // Act - Convert to I420
        var videoFrame = _converter.Convert(frame);

        // Assert - Verify YUV values for red
        // Expected: Y≈80 (medium luminance), U≈90 (low), V≈240 (high)
        Assert.InRange(videoFrame.YPlane[0], 70, 90);   // Y value
        Assert.InRange(videoFrame.UPlane[0], 80, 100);  // U value (low for red)
        Assert.InRange(videoFrame.VPlane[0], 230, 250); // V value (high for red)
    }

    /// <summary>
    /// Tests ConvertAndScale with same dimensions (no scaling).
    /// When target dimensions match source, no scaling occurs.
    /// </summary>
    [Fact]
    public void ConvertAndScale_SameDimensions_Success()
    {
        // Arrange
        var frame = CreateTestFrame(1920, 1080);

        // Act - Convert with same dimensions (no scaling needed)
        var videoFrame = _converter.ConvertAndScale(frame, 1920, 1080);

        // Assert
        Assert.NotNull(videoFrame);
        Assert.Equal(1920, videoFrame.Width);
        Assert.Equal(1080, videoFrame.Height);
    }

    /// <summary>
    /// Tests that ConvertAndScale with different dimensions throws NotImplementedException.
    /// Scaling is not yet implemented in this converter.
    /// </summary>
    [Fact]
    public void ConvertAndScale_DifferentDimensions_ThrowsNotImplementedException()
    {
        // Arrange
        var frame = CreateTestFrame(1920, 1080);

        // Act & Assert - Scaling not yet implemented
        Assert.Throws<NotImplementedException>(
            () => _converter.ConvertAndScale(frame, 1280, 720));
    }

    /// <summary>
    /// Tests that ConvertAndScale with odd target dimensions throws ArgumentException.
    /// I420 format requires even dimensions.
    /// </summary>
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
    /// Tests that multiple conversions work correctly (buffer pool reuse).
    /// This verifies that ArrayPool buffers are properly returned and can be reused.
    /// </summary>
    [Fact]
    public void Convert_MultipleConversions_AllSucceed()
    {
        // Arrange
        var frame = CreateTestFrame(1920, 1080);

        // Act - Perform 10 conversions to test buffer pool reuse
        for (var i = 0; i < 10; i++)
        {
            var videoFrame = _converter.Convert(frame);

            // Assert - Each conversion should succeed
            Assert.NotNull(videoFrame);
            Assert.Equal(1920, videoFrame.Width);
            Assert.Equal(1080, videoFrame.Height);
        }
    }

    /// <summary>
    /// Tests that converter can be disposed multiple times without error.
    /// This is important for safe cleanup in various scenarios.
    /// </summary>
    [Fact]
    public void Dispose_MultipleDispose_NoException()
    {
        // Arrange
        var converter = new PooledPixelConverter();

        // Act & Assert - Multiple dispose should not throw
        converter.Dispose();
        converter.Dispose(); // Second dispose should be safe
    }

    /// <summary>
    /// Tests that using converter after dispose throws ObjectDisposedException.
    /// This validates proper disposal handling.
    /// </summary>
    [Fact]
    public void Convert_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var converter = new PooledPixelConverter();
        var frame = CreateTestFrame(1920, 1080);
        converter.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => converter.Convert(frame));
    }

    /// <summary>
    /// Tests that conversion results match between PooledPixelConverter and PixelConverter.
    /// Both should produce identical YUV values for the same input.
    /// </summary>
    [Fact]
    public void Convert_ResultsMatchStandardConverter()
    {
        // Arrange
        var frame = CreateTestFrame(4, 4); // Small frame for easy comparison
        var standardConverter = new PixelConverter();
        var pooledConverter = new PooledPixelConverter();

        // Act - Convert with both converters
        var standardResult = standardConverter.Convert(frame);
        var pooledResult = pooledConverter.Convert(frame);

        // Assert - Results should be identical
        Assert.Equal(standardResult.Width, pooledResult.Width);
        Assert.Equal(standardResult.Height, pooledResult.Height);

        // Compare Y plane
        for (var i = 0; i < standardResult.YPlane.Length; i++)
        {
            Assert.Equal(standardResult.YPlane[i], pooledResult.YPlane[i]);
        }

        // Compare U plane
        for (var i = 0; i < standardResult.UPlane.Length; i++)
        {
            Assert.Equal(standardResult.UPlane[i], pooledResult.UPlane[i]);
        }

        // Compare V plane
        for (var i = 0; i < standardResult.VPlane.Length; i++)
        {
            Assert.Equal(standardResult.VPlane[i], pooledResult.VPlane[i]);
        }
    }

    /// <summary>
    /// Helper method to create a test frame with specified dimensions.
    /// Fills the frame with gray pixels (RGB: 128,128,128) for consistent testing.
    /// </summary>
    /// <param name="width">Frame width in pixels (must be even).</param>
    /// <param name="height">Frame height in pixels (must be even).</param>
    /// <returns>A Frame object filled with gray pixels.</returns>
    private static Frame CreateTestFrame(int width, int height)
    {
        var stride = width * 4; // BGRA format: 4 bytes per pixel
        var data = new byte[stride * height];

        // Fill with gray (BGRA: 128, 128, 128, 255) for testing
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = 128;     // B (Blue)
            data[i + 1] = 128; // G (Green)
            data[i + 2] = 128; // R (Red)
            data[i + 3] = 255; // A (Alpha - fully opaque)
        }

        return new Frame(width, height, data, stride, DateTime.UtcNow);
    }

    /// <summary>
    /// Disposes the converter after each test to ensure clean state.
    /// </summary>
    public void Dispose()
    {
        _converter.Dispose();
    }
}
