using DeskShare.Core.Conversion;

namespace DeskShare.UnitTests.Conversion;

public sealed class OutputSizeTests
{
    [Theory]
    [InlineData(3440, 1440, 1920, 1920, 804)]   // ultrawide: 803.7 rounds to 804
    [InlineData(2880, 1800, 1920, 1920, 1200)]
    [InlineData(3840, 2160, 1920, 1920, 1080)]
    [InlineData(1920, 1080, 1920, 1920, 1080)]  // already at the cap: unchanged
    [InlineData(1366, 768, 1920, 1366, 768)]    // smaller than the cap: never upscaled
    [InlineData(3440, 1440, 0, 3440, 1440)]     // 0 = native
    [InlineData(3440, 1440, -1, 3440, 1440)]
    [InlineData(1366, 768, 1280, 1280, 720)]    // 1280/1366*768 = 719.6 -> 720
    [InlineData(2560, 1080, 1281, 1280, 540)]   // odd cap is rounded down to even
    [InlineData(1920, 1200, 1280, 1280, 800)]
    [InlineData(1920, 1080, 1, 2, 2)]           // absurd cap still gives a valid (non-zero, even) size
    public void Compute_ReturnsExpectedSize(int srcW, int srcH, int max, int expectedW, int expectedH)
    {
        var (w, h) = OutputSize.Compute(srcW, srcH, max);

        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Theory]
    [InlineData(3439, 1441, 1001)]
    [InlineData(2561, 1083, 1279)]
    [InlineData(5000, 777, 333)]
    public void Compute_ScaledResultIsAlwaysEven(int srcW, int srcH, int max)
    {
        var (w, h) = OutputSize.Compute(srcW, srcH, max);

        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        Assert.True(w <= max);
    }
}
