using System.Buffers;

namespace DeskShare.Common.Models;

/// <summary>
/// Represents a video frame in I420 (YUV 4:2:0 planar) format.
/// This format is optimized for video encoding and WebRTC transmission.
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private readonly bool _pooled;

    /// <summary>Width of the frame in pixels.</summary>
    public int Width { get; }

    /// <summary>Height of the frame in pixels.</summary>
    public int Height { get; }

    /// <summary>Y plane data (luminance).</summary>
    public byte[] YPlane { get; }

    /// <summary>U plane data (chrominance blue).</summary>
    public byte[] UPlane { get; }

    /// <summary>V plane data (chrominance red).</summary>
    public byte[] VPlane { get; }

    /// <summary>Stride for Y plane in bytes.</summary>
    public int YStride { get; }

    /// <summary>Stride for U plane in bytes.</summary>
    public int UStride { get; }

    /// <summary>Stride for V plane in bytes.</summary>
    public int VStride { get; }

    /// <summary>Timestamp when the frame was captured.</summary>
    public DateTime Timestamp { get; }

    /// <summary>Actual data length of Y plane (may differ from YPlane.Length for pooled buffers).</summary>
    public int YPlaneLength { get; }

    /// <summary>Actual data length of U plane.</summary>
    public int UPlaneLength { get; }

    /// <summary>Actual data length of V plane.</summary>
    public int VPlaneLength { get; }

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoFrame"/> class.
    /// </summary>
    public VideoFrame(
        int width,
        int height,
        byte[] yPlane,
        byte[] uPlane,
        byte[] vPlane,
        int yStride,
        int uStride,
        int vStride,
        DateTime timestamp)
    {
        if (width <= 0 || width % 2 != 0)
            throw new ArgumentException("Width must be positive and even.", nameof(width));

        if (height <= 0 || height % 2 != 0)
            throw new ArgumentException("Height must be positive and even.", nameof(height));

        if (yStride < width)
            throw new ArgumentException("Y stride must be at least equal to width.", nameof(yStride));

        if (uStride < width / 2)
            throw new ArgumentException("U stride must be at least width/2.", nameof(uStride));

        if (vStride < width / 2)
            throw new ArgumentException("V stride must be at least width/2.", nameof(vStride));

        Width = width;
        Height = height;
        YPlane = yPlane ?? throw new ArgumentNullException(nameof(yPlane));
        UPlane = uPlane ?? throw new ArgumentNullException(nameof(uPlane));
        VPlane = vPlane ?? throw new ArgumentNullException(nameof(vPlane));
        YStride = yStride;
        UStride = uStride;
        VStride = vStride;
        Timestamp = timestamp;
        YPlaneLength = yPlane.Length;
        UPlaneLength = uPlane.Length;
        VPlaneLength = vPlane.Length;
    }

    /// <summary>
    /// Creates a VideoFrame backed by ArrayPool buffers. Dispose returns them to the pool.
    /// </summary>
    public VideoFrame(
        int width, int height,
        byte[] yPlane, byte[] uPlane, byte[] vPlane,
        int yStride, int uStride, int vStride,
        DateTime timestamp,
        int yPlaneLength, int uPlaneLength, int vPlaneLength)
        : this(width, height, yPlane, uPlane, vPlane, yStride, uStride, vStride, timestamp)
    {
        _pooled = true;
        YPlaneLength = yPlaneLength;
        UPlaneLength = uPlaneLength;
        VPlaneLength = vPlaneLength;
    }

    /// <summary>
    /// Releases all resources used by the VideoFrame.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_pooled)
        {
            ArrayPool<byte>.Shared.Return(YPlane);
            ArrayPool<byte>.Shared.Return(UPlane);
            ArrayPool<byte>.Shared.Return(VPlane);
        }

        _disposed = true;
    }
}
