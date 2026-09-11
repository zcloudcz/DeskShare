namespace DeskShare.Desktop.Shared.Models;

/// <summary>
/// Represents the current state of the screen sharing server.
/// Shared between WPF and Avalonia desktop clients.
/// </summary>
public enum ServerStatus
{
    /// <summary>
    /// Server is not running.
    /// </summary>
    Stopped,

    /// <summary>
    /// Server is running and accepting connections.
    /// </summary>
    Running,

    /// <summary>
    /// Server encountered an error.
    /// </summary>
    Error
}
