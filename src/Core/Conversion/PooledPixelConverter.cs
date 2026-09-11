using System.Buffers;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;

namespace DeskShare.Core.Conversion;

/// <summary>
/// High-performance pixel format converter with buffer pooling to reduce GC pressure.
/// Uses ArrayPool&lt;byte&gt; to rent and return buffers, avoiding repeated allocations.
/// </summary>
/// <remarks>
/// This converter is ideal for high-frequency frame conversion (30+ FPS) where
/// allocating new byte arrays every frame would cause excessive garbage collection.
/// Buffers are rented from the shared ArrayPool and must be returned after use.
/// </remarks>
public sealed class PooledPixelConverter : IFrameConverter, IDisposable
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

    private bool _disposed;

    /// <inheritdoc/>
    public VideoFrame Convert(Frame source)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PooledPixelConverter));

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (source.Width % 2 != 0 || source.Height % 2 != 0)
            throw new ArgumentException("Frame dimensions must be even for I420 conversion.", nameof(source));

        return ConvertInternal(source, source.Width, source.Height);
    }

    /// <inheritdoc/>
    public VideoFrame ConvertAndScale(Frame source, int targetWidth, int targetHeight)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PooledPixelConverter));

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (targetWidth % 2 != 0 || targetHeight % 2 != 0)
            throw new ArgumentException("Target dimensions must be even for I420 conversion.");

        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentException("Target dimensions must be positive.");

        // Scaling not yet implemented - use same dimensions
        if (targetWidth != source.Width || targetHeight != source.Height)
        {
            throw new NotImplementedException("Scaling is not yet implemented. Use same dimensions as source.");
        }

        return ConvertInternal(source, targetWidth, targetHeight);
    }

    /// <summary>
    /// Internal conversion method using ArrayPool for buffer management.
    /// </summary>
    /// <param name="source">Source frame in BGRA format.</param>
    /// <param name="width">Output width.</param>
    /// <param name="height">Output height.</param>
    /// <returns>Converted video frame in I420 format with pooled buffers.</returns>
    private static VideoFrame ConvertInternal(Frame source, int width, int height)
    {
        var yPlaneSize = width * height;
        var uvPlaneSize = (width / 2) * (height / 2);

        // Rent buffers from ArrayPool instead of allocating new arrays
        // This significantly reduces GC pressure for high FPS scenarios
        var yPlane = ArrayPool<byte>.Shared.Rent(yPlaneSize);
        var uPlane = ArrayPool<byte>.Shared.Rent(uvPlaneSize);
        var vPlane = ArrayPool<byte>.Shared.Rent(uvPlaneSize);

        try
        {
            unsafe
            {
                fixed (byte* sourcePtr = source.Data)
                fixed (byte* yPtr = yPlane)
                fixed (byte* uPtr = uPlane)
                fixed (byte* vPtr = vPlane)
                {
                    var stride = source.Stride;

                    // Convert Y plane (full resolution - every pixel)
                    for (int y = 0; y < height; y++)
                    {
                        var rowPtr = sourcePtr + (y * stride);
                        var yRowPtr = yPtr + (y * width);

                        for (int x = 0; x < width; x++)
                        {
                            // BGRA format: [B, G, R, A]
                            var b = rowPtr[x * 4];
                            var g = rowPtr[x * 4 + 1];
                            var r = rowPtr[x * 4 + 2];

                            // Y = (66R + 129G + 25B + 128) >> 8 + 16
                            yRowPtr[x] = (byte)(((YR * r + YG * g + YB * b + 128) >> 8) + 16);
                        }
                    }

                    // Convert U and V planes (half resolution - 2x2 subsampling)
                    var uvWidth = width / 2;
                    var uvHeight = height / 2;

                    for (int y = 0; y < uvHeight; y++)
                    {
                        var row1Ptr = sourcePtr + (y * 2 * stride);
                        var row2Ptr = sourcePtr + (y * 2 + 1) * stride;
                        var uRowPtr = uPtr + (y * uvWidth);
                        var vRowPtr = vPtr + (y * uvWidth);

                        for (int x = 0; x < uvWidth; x++)
                        {
                            // Sample 2x2 block and average
                            var x2 = x * 2;
                            var b1 = row1Ptr[x2 * 4];
                            var g1 = row1Ptr[x2 * 4 + 1];
                            var r1 = row1Ptr[x2 * 4 + 2];

                            var b2 = row1Ptr[(x2 + 1) * 4];
                            var g2 = row1Ptr[(x2 + 1) * 4 + 1];
                            var r2 = row1Ptr[(x2 + 1) * 4 + 2];

                            var b3 = row2Ptr[x2 * 4];
                            var g3 = row2Ptr[x2 * 4 + 1];
                            var r3 = row2Ptr[x2 * 4 + 2];

                            var b4 = row2Ptr[(x2 + 1) * 4];
                            var g4 = row2Ptr[(x2 + 1) * 4 + 1];
                            var r4 = row2Ptr[(x2 + 1) * 4 + 2];

                            // Average of 2x2 block
                            var avgR = (r1 + r2 + r3 + r4) >> 2;
                            var avgG = (g1 + g2 + g3 + g4) >> 2;
                            var avgB = (b1 + b2 + b3 + b4) >> 2;

                            // U = (-38R - 74G + 112B + 128) >> 8 + 128
                            uRowPtr[x] = (byte)(((-UR * avgR - UG * avgG + UB * avgB + 128) >> 8) + 128);

                            // V = (112R - 94G - 18B + 128) >> 8 + 128
                            vRowPtr[x] = (byte)(((VR * avgR - VG * avgG - VB * avgB + 128) >> 8) + 128);
                        }
                    }
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
                source.Timestamp,
                yPlaneSize,
                uvPlaneSize,
                uvPlaneSize);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(yPlane);
            ArrayPool<byte>.Shared.Return(uPlane);
            ArrayPool<byte>.Shared.Return(vPlane);
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}
