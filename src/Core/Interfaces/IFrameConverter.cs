using DeskShare.Core.Models;

namespace DeskShare.Core.Interfaces;

/// <summary>
/// Defines the contract for frame format conversion.
/// Converts raw captured frames (BGRA) to video frames (I420) suitable for encoding.
/// </summary>
public interface IFrameConverter
{
    /// <summary>
    /// Converts a raw BGRA frame to I420 (YUV 4:2:0 planar) format.
    /// </summary>
    /// <param name="source">Source frame in BGRA format.</param>
    /// <returns>Converted video frame in I420 format.</returns>
    /// <exception cref="ArgumentNullException">Thrown when source is null.</exception>
    /// <exception cref="ArgumentException">Thrown when source dimensions are invalid (must be even).</exception>
    /// <remarks>
    /// This operation is performance-critical and should be optimized using:
    /// - Unsafe pointer operations for direct memory access
    /// - SIMD instructions where applicable
    /// - Buffer pooling to reduce allocations
    /// The conversion follows ITU-R BT.601 standard for RGB to YUV conversion.
    /// </remarks>
    VideoFrame Convert(Frame source);

    /// <summary>
    /// Converts a raw BGRA frame to I420 format with optional scaling.
    /// </summary>
    /// <param name="source">Source frame in BGRA format.</param>
    /// <param name="targetWidth">Target width for the output frame (must be even).</param>
    /// <param name="targetHeight">Target height for the output frame (must be even).</param>
    /// <returns>Converted and scaled video frame in I420 format.</returns>
    /// <exception cref="ArgumentNullException">Thrown when source is null.</exception>
    /// <exception cref="ArgumentException">Thrown when dimensions are invalid.</exception>
    /// <remarks>
    /// Scaling can be used for adaptive bitrate streaming to reduce resolution under poor network conditions.
    /// Uses bilinear interpolation for quality/performance balance.
    /// </remarks>
    VideoFrame ConvertAndScale(Frame source, int targetWidth, int targetHeight);
}
