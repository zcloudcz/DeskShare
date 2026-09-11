namespace DeskShare.Desktop.Shared.Models;

/// <summary>
/// Represents a saved connection entry that can be reused for quick connect.
/// Shared between WPF and Avalonia desktop clients.
/// </summary>
public class SavedConnection
{
    /// <summary>
    /// Unique identifier for this saved connection.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly name for the connection (e.g., "Work PC", "Home Desktop").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The remote server's unique identifier (MAC-based).
    /// </summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// Whether this connection requires a password.
    /// </summary>
    public bool HasPassword { get; set; }

    /// <summary>
    /// Display string showing when this connection was last used.
    /// </summary>
    public string LastConnected { get; set; } = string.Empty;
}
