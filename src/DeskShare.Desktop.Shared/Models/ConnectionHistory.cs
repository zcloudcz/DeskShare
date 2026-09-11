namespace DeskShare.Desktop.Shared.Models;

/// <summary>
/// Represents a historical connection entry with trust status.
/// Tracks both client and server connection history.
/// Shared between WPF and Avalonia desktop clients.
/// </summary>
public class ConnectionHistoryEntry
{
    /// <summary>
    /// Unique identifier for this history entry.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Server ID (MAC-based identifier).
    /// </summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// Optional friendly name for the connection.
    /// </summary>
    public string? ConnectionName { get; set; }

    /// <summary>
    /// Timestamp when connection was established.
    /// </summary>
    public DateTime ConnectedAt { get; set; }

    /// <summary>
    /// Timestamp when connection was disconnected (null if still active).
    /// </summary>
    public DateTime? DisconnectedAt { get; set; }

    /// <summary>
    /// Connection type (Client or Server).
    /// </summary>
    public ConnectionType Type { get; set; }

    /// <summary>
    /// Whether this connection is trusted (no passkey required for future connections).
    /// </summary>
    public bool IsTrusted { get; set; }

    /// <summary>
    /// Whether authentication was successful.
    /// </summary>
    public bool AuthenticationSuccessful { get; set; }

    /// <summary>
    /// Online status (null = unknown, true = online, false = offline).
    /// Only checked for trusted connections.
    /// </summary>
    public bool? IsOnline { get; set; }

    /// <summary>
    /// Last time online status was checked.
    /// </summary>
    public DateTime? LastOnlineCheck { get; set; }

    /// <summary>
    /// Connection duration (calculated from ConnectedAt and DisconnectedAt).
    /// </summary>
    public TimeSpan? Duration => DisconnectedAt.HasValue
        ? DisconnectedAt.Value - ConnectedAt
        : null;

    /// <summary>
    /// User-friendly display name.
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(ConnectionName)
        ? $"{Type} - {ServerId.Substring(0, Math.Min(12, ServerId.Length))}"
        : ConnectionName;

    /// <summary>
    /// User-friendly timestamp display.
    /// </summary>
    public string TimestampDisplay
    {
        get
        {
            var now = DateTime.Now;
            var timeAgo = now - ConnectedAt;

            if (timeAgo.TotalMinutes < 1)
                return "Just now";
            if (timeAgo.TotalMinutes < 60)
                return $"{(int)timeAgo.TotalMinutes}m ago";
            if (timeAgo.TotalHours < 24)
                return $"{(int)timeAgo.TotalHours}h ago";
            if (timeAgo.TotalDays < 7)
                return $"{(int)timeAgo.TotalDays}d ago";

            return ConnectedAt.ToString("yyyy-MM-dd HH:mm");
        }
    }

    /// <summary>
    /// Status badge text for UI display.
    /// </summary>
    public string StatusBadge
    {
        get
        {
            if (IsTrusted)
            {
                if (IsOnline == true)
                    return "Online";
                if (IsOnline == false)
                    return "Offline";
                return "Trusted";
            }
            if (AuthenticationSuccessful)
                return "Connected";
            return "Failed";
        }
    }

    /// <summary>
    /// Background color for status badge (CSS-style hex color).
    /// </summary>
    public string StatusBadgeColor
    {
        get
        {
            if (IsTrusted && IsOnline == true)
                return "#D4EDDA"; // Green
            if (IsTrusted && IsOnline == false)
                return "#F8D7DA"; // Red
            return "#E7F5FF"; // Blue (default)
        }
    }

    /// <summary>
    /// Border color for status badge (CSS-style hex color).
    /// </summary>
    public string StatusBadgeBorderColor
    {
        get
        {
            if (IsTrusted && IsOnline == true)
                return "#28A745"; // Green
            if (IsTrusted && IsOnline == false)
                return "#DC3545"; // Red
            return "#4DABF7"; // Blue (default)
        }
    }

    /// <summary>
    /// Text color for status badge (CSS-style hex color).
    /// </summary>
    public string StatusBadgeTextColor
    {
        get
        {
            if (IsTrusted && IsOnline == true)
                return "#155724"; // Dark green
            if (IsTrusted && IsOnline == false)
                return "#721C24"; // Dark red
            return "#1971C2"; // Dark blue (default)
        }
    }
}

/// <summary>
/// Type of connection (Client or Server mode).
/// </summary>
public enum ConnectionType
{
    /// <summary>
    /// This machine acted as a client (connected TO a server).
    /// </summary>
    Client,

    /// <summary>
    /// This machine acted as a server (accepted connections FROM clients).
    /// </summary>
    Server
}
