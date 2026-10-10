using DeskShare.Core.Conversion;
using DeskShare.Core.Models;

namespace DeskShare.UnitTests.Conversion;

public sealed class SimdPixelConverterTests
{
    private readonly SimdPixelConverter _converter = new();

    [Fact]
    public void ConvertAndScale_Downscale_ProducesTargetDimensionsAndPlaneSizes()
    {
        var frame = CreateFrame(3440, 1440, 128, 128, 128);

        var video = _converter.ConvertAndScale(frame, 1920, 804);

        Assert.Equal(1920, video.Width);
        Assert.Equal(804, video.Height);
        Assert.Equal(1920 * 804, video.YPlane.Length);
        Assert.Equal(960 * 402, video.UPlane.Length);
        Assert.Equal(960 * 402, video.VPlane.Length);
    }

    [Fact]
    public void ConvertAndScale_SolidColor_IsPreservedByScaling()
    {
        // Averaging identical pixels must give the same pixel, so scaled output == unscaled output.
        var frame = CreateFrame(640, 360, 20, 200, 90);
        var reference = _converter.Convert(frame);

        var scaled = _converter.ConvertAndScale(frame, 320, 180);

        Assert.Equal(reference.YPlane[0], scaled.YPlane[0]);
        Assert.Equal(reference.UPlane[0], scaled.UPlane[0]);
        Assert.Equal(reference.VPlane[0], scaled.VPlane[0]);
    }

    [Fact]
    public void ConvertAndScale_BlackAndWhiteStripes_AverageToGray()
    {
        // 1-pixel black/white columns scaled by exactly 2x must blend to mid gray (box filter, not nearest).
        const int w = 64, h = 32;
        var data = new byte[w * 4 * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = (byte)(x % 2 == 0 ? 0 : 254);
                var o = (y * w + x) * 4;
                data[o] = data[o + 1] = data[o + 2] = v;
                data[o + 3] = 255;
            }

        var video = _converter.ConvertAndScale(new Frame(w, h, data, w * 4, DateTime.UtcNow), 32, 16);

        // Gray 127 -> Y of about 16 + 219 * 127 / 255 = 125
        Assert.InRange(video.YPlane[0], 120, 130);
    }

    private static Frame CreateFrame(int width, int height, byte r, byte g, byte b)
    {
        var data = new byte[width * 4 * height];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = b;
            data[i + 1] = g;
            data[i + 2] = r;
            data[i + 3] = 255;
        }

        return new Frame(width, height, data, width * 4, DateTime.UtcNow);
    }
}
