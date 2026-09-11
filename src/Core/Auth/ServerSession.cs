namespace DeskShare.Core.Auth;

/// <summary>
/// Represents the status of a server registration/session.
/// </summary>
/// <remarks>
/// For junior developers:
/// This enum tracks the lifecycle of a server connection:
///
/// Registered → waiting for client to connect (passkey valid)
/// Connected → client successfully connected (session active)
/// Expired → passkey expired, no client connected (will be removed)
/// Disconnected → client or server disconnected (session ending)
/// </remarks>
public enum SessionStatus
{
    /// <summary>
    /// Server registered with SignalingServer, waiting for client connection.
    /// Passkey is valid, no client connected yet.
    /// </summary>
    Registered,

    /// <summary>
    /// Client successfully connected to server.
    /// Session is active, additional clients are blocked.
    /// </summary>
    Connected,

    /// <summary>
    /// Passkey expired before client could connect.
    /// Registration will be removed.
    /// </summary>
    Expired,

    /// <summary>
    /// Client or server disconnected.
    /// Session is ending, will be removed after timeout grace period.
    /// </summary>
    Disconnected
}

/// <summary>
/// Represents a server session with authentication details.
/// Stored by SignalingServer to track registered servers and their connections.
/// </summary>
/// <remarks>
/// For junior developers:
/// This class holds all information about a server that wants to accept connections.
///
/// Lifecycle:
/// 1. Server creates session with Registered status and passkey
/// 2. Client connects with correct passkey → status changes to Connected
/// 3. Either:
///    - Passkey expires (45s) without connection → Expired → removed
///    - Client disconnects → Disconnected → removed after 1 min grace period
///    - Server disconnects → Disconnected → removed after 1 min grace period
/// </remarks>
public sealed class ServerSession
{
    /// <summary>
    /// Unique server identifier (derived from MAC address).
    /// Example: "A1B2C3D4E5F6"
    /// </summary>
    public required string ServerId { get; set; }

    /// <summary>
    /// Current time-based passkey for authentication.
    /// 9-character alphanumeric code, valid for 45 seconds.
    /// Example: "A3F7K9M2P"
    /// </summary>
    public required string Passkey { get; set; }

    /// <summary>
    /// When the current passkey expires (UTC).
    /// After this time, a new passkey must be generated.
    /// </summary>
    public required DateTime ValidTo { get; set; }

    /// <summary>
    /// Whether remote control (keyboard/mouse input) is enabled.
    /// If false, client can only view the screen.
    /// </summary>
    public required bool RemoteControlEnabled { get; set; }

    /// <summary>
    /// Whether to trust the connected client permanently (no passkey required for reconnection).
    /// If true, SignalingServer will establish permanent trust after successful connection.
    /// </summary>
    public required bool TrustClientPermanent { get; set; }

    /// <summary>
    /// Current status of the session.
    /// </summary>
    public SessionStatus Status { get; set; } = SessionStatus.Registered;

    /// <summary>
    /// ID of the connected client (null if no client connected yet).
    /// Once set, no other clients can connect to this server.
    /// </summary>
    public string? ConnectedClientId { get; set; }

    /// <summary>
    /// When the session was created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the client connected (UTC), null if not connected yet.
    /// </summary>
    public DateTime? ConnectedAt { get; set; }

    /// <summary>
    /// Last activity timestamp (UTC).
    /// Updated when server or client sends a message.
    /// Used for timeout detection (1 minute grace period).
    /// </summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Checks if the session is still valid (not expired or disconnected).
    /// </summary>
    /// <param name="currentTime">Current time to check against (typically DateTime.UtcNow).</param>
    /// <param name="disconnectGracePeriodSeconds">Grace period after disconnect (default 60 seconds).</param>
    /// <returns>True if session is valid, false otherwise.</returns>
    /// <remarks>
    /// For junior developers:
    /// A session is valid if:
    /// - Status is Registered AND passkey hasn't expired yet
    /// - Status is Connected AND last activity was within grace period (handles temporary disconnects)
    ///
    /// Grace period allows for brief network hiccups without terminating the session.
    /// </remarks>
    public bool IsValid(DateTime currentTime, int disconnectGracePeriodSeconds = 60)
    {
        return Status switch
        {
            SessionStatus.Registered => currentTime < ValidTo,
            SessionStatus.Connected => (currentTime - LastActivity).TotalSeconds <= disconnectGracePeriodSeconds,
            SessionStatus.Expired => false,
            SessionStatus.Disconnected => (currentTime - LastActivity).TotalSeconds <= disconnectGracePeriodSeconds,
            _ => false
        };
    }

    /// <summary>
    /// Updates the session to Connected status with client information.
    /// </summary>
    /// <param name="clientId">The ID of the connecting client.</param>
    /// <remarks>
    /// For junior developers:
    /// Call this when a client successfully authenticates and connects.
    /// This locks the session to this specific client - no other clients can join.
    /// </remarks>
    public void MarkAsConnected(string clientId)
    {
        Status = SessionStatus.Connected;
        ConnectedClientId = clientId;
        ConnectedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates the last activity timestamp.
    /// Call this whenever server or client sends a message.
    /// </summary>
    public void UpdateActivity()
    {
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the session as disconnected.
    /// Session will be removed after grace period expires.
    /// </summary>
    public void MarkAsDisconnected()
    {
        Status = SessionStatus.Disconnected;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the session as expired (passkey timeout).
    /// Session will be removed immediately.
    /// </summary>
    public void MarkAsExpired()
    {
        Status = SessionStatus.Expired;
    }
}
