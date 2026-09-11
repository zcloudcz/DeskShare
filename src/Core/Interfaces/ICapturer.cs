using DeskShare.Core.Models;

namespace DeskShare.Core.Interfaces;

/// <summary>
/// Defines the contract for screen capture implementations.
/// Responsible for capturing frames from displays or windows.
/// Platform-specific implementations: Windows (Desktop Duplication), Linux (X11/Wayland), macOS (CGDisplayStream).
/// </summary>
public interface IScreenCapturer : IDisposable
{
    /// <summary>
    /// Gets the width of the captured area in pixels.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// Gets the height of the captured area in pixels.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Initializes the capturer and prepares it for frame acquisition.
    /// </summary>
    /// <returns>True if initialization succeeded, false otherwise.</returns>
    /// <exception cref="InvalidOperationException">Thrown when capturer is already initialized.</exception>
    bool Initialize();

    /// <summary>
    /// Attempts to acquire the next available frame from the capture source.
    /// This method should not block for extended periods (max timeout configurable).
    /// </summary>
    /// <param name="frame">The captured frame if successful, null otherwise.</param>
    /// <returns>True if a frame was successfully captured, false otherwise (no update or timeout).</returns>
    /// <remarks>
    /// This method uses Desktop Duplication API which only provides frames when screen content changes.
    /// Returns false if no new frame is available within the timeout period.
    /// </remarks>
    bool TryAcquireFrame(out Frame? frame);

    /// <summary>
    /// Releases the currently held frame resources and allows the next frame to be acquired.
    /// Must be called after processing each frame from TryAcquireFrame.
    /// </summary>
    void ReleaseFrame();
}

/// <summary>
/// Backward compatibility alias for IScreenCapturer.
/// </summary>
[Obsolete("Use IScreenCapturer instead. This will be removed in a future version.")]
public interface ICapturer : IScreenCapturer
{
}
