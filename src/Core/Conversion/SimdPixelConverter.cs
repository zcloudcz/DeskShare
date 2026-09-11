using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;

namespace DeskShare.Core.Conversion;

/// <summary>
/// Optimized pixel format converter from BGRA to I420 (YUV 4:2:0 planar).
/// Uses unsafe pointer arithmetic for fast conversion.
/// </summary>
public sealed class SimdPixelConverter : IFrameConverter
{
    /// <inheritdoc/>
    public VideoFrame Convert(Frame source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (source.Width % 2 != 0 || source.Height % 2 != 0)
            throw new ArgumentException("Frame dimensions must be even for I420 conversion.", nameof(source));

        return ConvertInternal(source, source.Width, source.Height);
    }

    /// <inheritdoc/>
    public VideoFrame ConvertAndScale(Frame source, int targetWidth, int targetHeight)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (targetWidth % 2 != 0 || targetHeight % 2 != 0)
            throw new ArgumentException("Target dimensions must be even for I420 conversion.");

        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentException("Target dimensions must be positive.");

        if (targetWidth != source.Width || targetHeight != source.Height)
        {
            throw new NotImplementedException("Scaling is not yet implemented.");
        }

        return ConvertInternal(source, targetWidth, targetHeight);
    }

    private static VideoFrame ConvertInternal(Frame source, int width, int height)
    {
        var yPlaneSize = width * height;
        var uvPlaneSize = (width / 2) * (height / 2);

        var yPlane = new byte[yPlaneSize];
        var uPlane = new byte[uvPlaneSize];
        var vPlane = new byte[uvPlaneSize];

        unsafe
        {
            fixed (byte* srcPtr = source.Data)
            fixed (byte* yPtr = yPlane)
            fixed (byte* uPtr = uPlane)
            fixed (byte* vPtr = vPlane)
            {
                var stride = source.Stride;

                for (var y = 0; y < height; y++)
                {
                    var srcRow = srcPtr + (y * stride);
                    var yRow = yPtr + (y * width);

                    for (var x = 0; x < width; x++)
                    {
                        var offset = x * 4;
                        var b = srcRow[offset];
                        var g = srcRow[offset + 1];
                        var r = srcRow[offset + 2];

                        yRow[x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                    }
                }

                var uvWidth = width / 2;
                var uvHeight = height / 2;

                for (var y = 0; y < uvHeight; y++)
                {
                    var srcRow1 = srcPtr + (y * 2 * stride);
                    var srcRow2 = srcPtr + ((y * 2 + 1) * stride);
                    var uvOffset = y * uvWidth;

                    for (var x = 0; x < uvWidth; x++)
                    {
                        var srcX = x * 2;
                        var offset1 = srcX * 4;
                        var offset2 = (srcX + 1) * 4;

                        var rAvg = (srcRow1[offset1 + 2] + srcRow1[offset2 + 2] +
                                    srcRow2[offset1 + 2] + srcRow2[offset2 + 2]) >> 2;
                        var gAvg = (srcRow1[offset1 + 1] + srcRow1[offset2 + 1] +
                                    srcRow2[offset1 + 1] + srcRow2[offset2 + 1]) >> 2;
                        var bAvg = (srcRow1[offset1] + srcRow1[offset2] +
                                    srcRow2[offset1] + srcRow2[offset2]) >> 2;

                        uPtr[uvOffset + x] = (byte)(((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128);
                        vPtr[uvOffset + x] = (byte)(((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128);
                    }
                }
            }
        }

        return new VideoFrame(
            width, height,
            yPlane, uPlane, vPlane,
            width, width / 2, width / 2,
            source.Timestamp);
    }
}
