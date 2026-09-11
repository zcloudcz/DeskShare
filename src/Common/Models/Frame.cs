namespace DeskShare.Common.Models;

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
    /// Releases all resources used by the Frame.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // Data array will be garbage collected
        _disposed = true;
    }
}
