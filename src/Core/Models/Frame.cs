using System.Buffers;

namespace DeskShare.Core.Models;

/// <summary>
/// Represents a captured frame from screen capture system.
/// Contains raw pixel data in BGRA format.
/// </summary>
public sealed class Frame : IDisposable
{
    /// <summary>
    /// Width of the frame in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Height of the frame in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Raw pixel data in BGRA format (4 bytes per pixel).
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// Timestamp when the frame was captured.
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Stride (row pitch) in bytes. Typically Width * 4 for BGRA.
    /// </summary>
    public int Stride { get; }

    private bool _disposed;
    private readonly bool _pooled;

    /// <summary>
    /// Initializes a new instance of the <see cref="Frame"/> class.
    /// </summary>
    /// <param name="width">Width of the frame in pixels.</param>
    /// <param name="height">Height of the frame in pixels.</param>
    /// <param name="data">Raw pixel data in BGRA format.</param>
    /// <param name="stride">Stride (row pitch) in bytes.</param>
    /// <param name="timestamp">Timestamp when the frame was captured.</param>
    /// <exception cref="ArgumentException">Thrown when dimensions are invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when data is null.</exception>
    public Frame(int width, int height, byte[] data, int stride, DateTime timestamp)
    {
        if (width <= 0)
            throw new ArgumentException("Width must be positive.", nameof(width));

        if (height <= 0)
            throw new ArgumentException("Height must be positive.", nameof(height));

        if (stride < width * 4)
            throw new ArgumentException("Stride must be at least width * 4 for BGRA format.", nameof(stride));

        Width = width;
        Height = height;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Stride = stride;
        Timestamp = timestamp;
    }

    /// <summary>
    /// Creates a frame whose <paramref name="data"/> was rented from <see cref="ArrayPool{T}.Shared"/>.
    /// The array may be longer than Stride * Height; consumers must use Stride/Height, not Data.Length.
    /// Dispose returns the array to the pool.
    /// </summary>
    public static Frame FromPooled(int width, int height, byte[] data, int stride, DateTime timestamp)
        => new(width, height, data, stride, timestamp, pooled: true);

    private Frame(int width, int height, byte[] data, int stride, DateTime timestamp, bool pooled)
        : this(width, height, data, stride, timestamp)
    {
        _pooled = pooled;
    }

    /// <summary>
    /// Releases all resources used by the Frame.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_pooled)
            ArrayPool<byte>.Shared.Return(Data);
    }
}
