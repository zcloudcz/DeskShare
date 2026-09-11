using DeskShare.Common.Models;

namespace DeskShare.Common.Interfaces;

/// <summary>
/// Defines the contract for video source abstraction over WebRTC.
/// Responsible for pushing video frames to WebRTC peer connection.
/// </summary>
public interface IVideoSource : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the video source is currently active and ready to receive frames.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Gets a value indicating whether the video source has been initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initializes the video source and prepares it for frame transmission.
    /// </summary>
    /// <param name="width">Width of video frames in pixels.</param>
    /// <param name="height">Height of video frames in pixels.</param>
    /// <param name="frameRate">Target frame rate in frames per second.</param>
    /// <returns>True if initialization succeeded, false otherwise.</returns>
    /// <exception cref="ArgumentException">Thrown when parameters are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when already initialized.</exception>
    bool Initialize(int width, int height, int frameRate);

    /// <summary>
    /// Pushes a video frame to the WebRTC peer connection for transmission.
    /// </summary>
    /// <param name="frame">Video frame in I420 format to transmit.</param>
    /// <returns>True if frame was accepted, false if dropped due to backpressure.</returns>
    /// <exception cref="ArgumentNullException">Thrown when frame is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when source is not initialized.</exception>
    /// <remarks>
    /// This method implements backpressure handling. If the WebRTC stack cannot keep up
    /// with incoming frames, this method returns false to signal the caller to slow down.
    /// Frames should not be pushed faster than the initialized frame rate.
    /// </remarks>
    bool PushFrame(VideoFrame frame);

    /// <summary>
    /// Gets current statistics about frame transmission.
    /// </summary>
    /// <returns>Statistics object containing frame counts, drops, and bitrate information.</returns>
    VideoSourceStatistics GetStatistics();
}

/// <summary>
/// Contains statistics about video source transmission performance.
/// </summary>
public sealed class VideoSourceStatistics
{
    /// <summary>
    /// Total number of frames pushed to the video source.
    /// </summary>
    public long FramesPushed { get; set; }

    /// <summary>
    /// Total number of frames dropped due to backpressure.
    /// </summary>
    public long FramesDropped { get; set; }

    /// <summary>
    /// Current average bitrate in bits per second.
    /// </summary>
    public long BitrateKbps { get; set; }

    /// <summary>
    /// Current frames per second being transmitted.
    /// </summary>
    public double CurrentFps { get; set; }

    /// <summary>
    /// Average frame processing time in milliseconds.
    /// </summary>
    public double AverageFrameTimeMs { get; set; }
}
