using System.Buffers;

namespace DeskShare.Core.Models;

/// <summary>
/// Represents a video frame in I420 (YUV 4:2:0 planar) format.
/// This format is optimized for video encoding and WebRTC transmission.
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private readonly bool _pooled;
    /// <summary>
    /// Width of the frame in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Height of the frame in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Y plane data (luminance). Size: Width * Height.
    /// </summary>
    public byte[] YPlane { get; }

    /// <summary>
    /// U plane data (chrominance blue). Size: (Width/2) * (Height/2).
    /// </summary>
    public byte[] UPlane { get; }

    /// <summary>
    /// V plane data (chrominance red). Size: (Width/2) * (Height/2).
    /// </summary>
    public byte[] VPlane { get; }

    /// <summary>
    /// Stride for Y plane in bytes (typically equal to Width).
    /// </summary>
    public int YStride { get; }

    /// <summary>
    /// Stride for U plane in bytes (typically Width/2).
    /// </summary>
    public int UStride { get; }

    /// <summary>
    /// Stride for V plane in bytes (typically Width/2).
    /// </summary>
    public int VStride { get; }

    /// <summary>
    /// Timestamp when the frame was captured.
    /// </summary>
    public DateTime Timestamp { get; }

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoFrame"/> class.
    /// </summary>
    /// <param name="width">Width of the frame in pixels (must be even).</param>
    /// <param name="height">Height of the frame in pixels (must be even).</param>
    /// <param name="yPlane">Y plane data.</param>
    /// <param name="uPlane">U plane data.</param>
    /// <param name="vPlane">V plane data.</param>
    /// <param name="yStride">Y plane stride.</param>
    /// <param name="uStride">U plane stride.</param>
    /// <param name="vStride">V plane stride.</param>
    /// <param name="timestamp">Timestamp when the frame was captured.</param>
    /// <exception cref="ArgumentException">Thrown when dimensions are invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when any plane is null.</exception>
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

    public int YPlaneLength { get; }
    public int UPlaneLength { get; }
    public int VPlaneLength { get; }

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
