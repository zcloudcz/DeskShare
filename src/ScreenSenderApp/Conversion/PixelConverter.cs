using DeskShare.Common.Interfaces;
using DeskShare.Common.Models;

namespace DeskShare.ScreenSenderApp.Conversion;

/// <summary>
/// High-performance pixel format converter from BGRA to I420 (YUV 4:2:0 planar).
/// Uses unsafe pointer operations for optimal performance.
/// </summary>
/// <remarks>
/// Conversion follows ITU-R BT.601 standard:
/// Y  =  0.299R + 0.587G + 0.114B
/// U  = -0.169R - 0.331G + 0.500B + 128
/// V  =  0.500R - 0.419G - 0.081B + 128
///
/// For performance, we use integer arithmetic with fixed-point math:
/// Y = ( 66R + 129G +  25B + 128) >> 8 + 16
/// U = (-38R -  74G + 112B + 128) >> 8 + 128
/// V = (112R -  94G -  18B + 128) >> 8 + 128
/// </remarks>
public sealed class PixelConverter : IFrameConverter
{
    // Fixed-point coefficients for RGB to YUV conversion (ITU-R BT.601)
    private const int YR = 66;
    private const int YG = 129;
    private const int YB = 25;
    private const int UR = 38;
    private const int UG = 74;
    private const int UB = 112;
    private const int VR = 112;
    private const int VG = 94;
    private const int VB = 18;

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

        // For now, only support same-size conversion
        // Scaling will be added in future optimization phase
        if (targetWidth != source.Width || targetHeight != source.Height)
        {
            throw new NotImplementedException("Scaling is not yet implemented. Use same dimensions as source.");
        }

        return ConvertInternal(source, targetWidth, targetHeight);
    }

    /// <summary>
    /// Internal conversion method that performs BGRA to I420 conversion.
    /// </summary>
    /// <param name="source">Source frame in BGRA format.</param>
    /// <param name="width">Output width.</param>
    /// <param name="height">Output height.</param>
    /// <returns>Converted video frame in I420 format.</returns>
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
                ConvertBgraToI420Unsafe(
                    srcPtr,
                    yPtr,
                    uPtr,
                    vPtr,
                    width,
                    height,
                    source.Stride);
            }
        }

        return new VideoFrame(
            width,
            height,
            yPlane,
            uPlane,
            vPlane,
            width,
            width / 2,
            width / 2,
            source.Timestamp);
    }

    /// <summary>
    /// Unsafe conversion from BGRA to I420 using pointer arithmetic.
    /// This is the performance-critical hot path.
    /// </summary>
    /// <param name="src">Pointer to source BGRA data.</param>
    /// <param name="yDst">Pointer to Y plane destination.</param>
    /// <param name="uDst">Pointer to U plane destination.</param>
    /// <param name="vDst">Pointer to V plane destination.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="stride">Source stride in bytes.</param>
    private static unsafe void ConvertBgraToI420Unsafe(
        byte* src,
        byte* yDst,
        byte* uDst,
        byte* vDst,
        int width,
        int height,
        int stride)
    {
        // Process Y plane (every pixel)
        for (var y = 0; y < height; y++)
        {
            var srcRow = src + (y * stride);
            var yRow = yDst + (y * width);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4; // BGRA = 4 bytes per pixel
                var b = srcRow[offset + 0];
                var g = srcRow[offset + 1];
                var r = srcRow[offset + 2];
                // Alpha channel (offset + 3) is ignored

                // Calculate Y component
                var yValue = (YR * r + YG * g + YB * b + 128) >> 8;
                yRow[x] = (byte)Math.Clamp(yValue + 16, 0, 255);
            }
        }

        // Process U and V planes (subsampled 2x2)
        // We average 4 pixels (2x2 block) to get one U/V value
        var uvWidth = width / 2;
        var uvHeight = height / 2;

        for (var y = 0; y < uvHeight; y++)
        {
            var srcY = y * 2;
            var srcRow1 = src + (srcY * stride);
            var srcRow2 = src + ((srcY + 1) * stride);
            var uvOffset = y * uvWidth;

            for (var x = 0; x < uvWidth; x++)
            {
                var srcX = x * 2;
                var offset1 = srcX * 4;
                var offset2 = (srcX + 1) * 4;

                // Sample 4 pixels in 2x2 block
                var b1 = srcRow1[offset1 + 0];
                var g1 = srcRow1[offset1 + 1];
                var r1 = srcRow1[offset1 + 2];

                var b2 = srcRow1[offset2 + 0];
                var g2 = srcRow1[offset2 + 1];
                var r2 = srcRow1[offset2 + 2];

                var b3 = srcRow2[offset1 + 0];
                var g3 = srcRow2[offset1 + 1];
                var r3 = srcRow2[offset1 + 2];

                var b4 = srcRow2[offset2 + 0];
                var g4 = srcRow2[offset2 + 1];
                var r4 = srcRow2[offset2 + 2];

                // Average the 4 pixels
                var rAvg = (r1 + r2 + r3 + r4) / 4;
                var gAvg = (g1 + g2 + g3 + g4) / 4;
                var bAvg = (b1 + b2 + b3 + b4) / 4;

                // Calculate U and V components
                var uValue = (-UR * rAvg - UG * gAvg + UB * bAvg + 128) >> 8;
                var vValue = (VR * rAvg - VG * gAvg - VB * bAvg + 128) >> 8;

                uDst[uvOffset + x] = (byte)Math.Clamp(uValue + 128, 0, 255);
                vDst[uvOffset + x] = (byte)Math.Clamp(vValue + 128, 0, 255);
            }
        }
    }
}
