namespace DeskShare.Core.Interfaces;

/// <summary>
/// Defines the contract for platform-specific clipboard management implementations.
/// Handles clipboard synchronization between server and client.
/// Platform-specific implementations: Windows (Clipboard API), Linux (X11 selection), macOS (NSPasteboard).
/// </summary>
public interface IClipboardManager : IDisposable
{
    /// <summary>
    /// Event raised when clipboard content changes.
    /// </summary>
    event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    /// <summary>
    /// Gets text from the system clipboard.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Clipboard text content, or null if clipboard is empty or contains non-text data.</returns>
    Task<string?> GetTextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets text to the system clipboard.
    /// </summary>
    /// <param name="text">Text to set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts monitoring clipboard changes.
    /// </summary>
    void StartMonitoring();

    /// <summary>
    /// Stops monitoring clipboard changes.
    /// </summary>
    void StopMonitoring();

    /// <summary>
    /// Gets whether clipboard monitoring is currently active.
    /// </summary>
    bool IsMonitoring { get; }
}

/// <summary>
/// Event arguments for clipboard change events.
/// </summary>
public class ClipboardChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the new clipboard text content.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Gets when the clipboard changed.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
