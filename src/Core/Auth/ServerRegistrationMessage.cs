namespace DeskShare.Core.Auth;

/// <summary>
/// Message sent from Server to SignalingServer to register/update authentication details.
/// </summary>
/// <remarks>
/// For junior developers:
/// This is the data packet that the server sends to register itself.
/// Think of it like a "I'm ready to accept connections" announcement.
///
/// Contains:
/// - Who I am (ServerId)
/// - My current password (Passkey)
/// - When password expires (ValidTo)
/// - What I allow (RemoteControlEnabled)
/// </remarks>
public sealed class ServerRegistrationMessage
{
    /// <summary>
    /// Server identifier (from MAC address).
    /// </summary>
    public required string ServerId { get; set; }

    /// <summary>
    /// Current time-based passkey (9 characters).
    /// </summary>
    public required string Passkey { get; set; }

    /// <summary>
    /// When the passkey expires (UTC).
    /// </summary>
    public required DateTime ValidTo { get; set; }

    /// <summary>
    /// Whether remote control is enabled for this session.
    /// </summary>
    public required bool RemoteControlEnabled { get; set; }

    /// <summary>
    /// Whether to trust the connected client permanently (no passkey required for reconnection).
    /// </summary>
    public required bool TrustClientPermanent { get; set; }

    /// <summary>
    /// Per-installation secret that proves ownership of <see cref="ServerId"/>. The SignalingServer
    /// binds the first secret it sees (trust on first use) and rejects registrations with a different one.
    /// Null for older senders that predate this field.
    /// </summary>
    public string? OwnerSecret { get; set; }
}

/// <summary>
/// Response from SignalingServer to server registration.
/// </summary>
public sealed class ServerRegistrationResponse
{
    /// <summary>
    /// Whether registration was successful.
    /// </summary>
    public required bool Success { get; set; }

    /// <summary>
    /// Error message if registration failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The registered server ID (echo back for confirmation).
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// One-time, short-lived token the sender must present on the /signal WebSocket upgrade
    /// (query parameter <c>token</c>). Issued only on successful registration.
    /// </summary>
    public string? WebSocketToken { get; set; }

    /// <summary>
    /// STUN/TURN servers (including fresh TURN credentials) the sender should use for new peer connections.
    /// </summary>
    public List<IceServerInfo>? IceServers { get; set; }
}

/// <summary>
/// Message sent from Client to SignalingServer to authenticate and connect.
/// </summary>
/// <remarks>
/// For junior developers:
/// This is what the client sends to prove they should be allowed to connect.
/// Like showing an ID card + password at a door.
///
/// Security features (HMAC):
/// - Timestamp: Prevents replay of old requests
/// - Nonce: Unique ID prevents duplicate requests
/// - Signature: HMAC proves request wasn't tampered with
///
/// Legacy support:
/// - Signature is optional (nullable) for backward compatibility
/// - Old clients without HMAC will still work (but less secure)
/// - New clients should always include Timestamp, Nonce, Signature
/// </remarks>
public sealed class ClientAuthenticationMessage
{
    /// <summary>
    /// The server ID the client wants to connect to.
    /// </summary>
    public required string ServerId { get; set; }

    /// <summary>
    /// The passkey provided by the client (user typed this from server screen).
    /// </summary>
    public required string Passkey { get; set; }

    /// <summary>
    /// The client's identifier.
    /// </summary>
    public required string ClientId { get; set; }

    /// <summary>
    /// Request timestamp in UTC. Used for signature validation and replay prevention.
    /// </summary>
    /// <remarks>
    /// The timestamp serves two purposes:
    /// 1. Part of HMAC signature (ensures timestamp can't be modified)
    /// 2. Expiration check (requests older than 60 seconds are rejected)
    ///
    /// Should be set to DateTime.UtcNow when creating request.
    /// </remarks>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Unique request identifier (nonce). Prevents replay attacks.
    /// </summary>
    /// <remarks>
    /// The nonce (number used once) ensures each request is unique.
    /// Even if an attacker captures a valid signed request, they can't
    /// replay it because the nonce will be marked as "already used".
    ///
    /// Should be generated using Guid.NewGuid().ToString() for each request.
    ///
    /// Example:
    /// <code>
    /// var message = new ClientAuthenticationMessage
    /// {
    ///     ServerId = serverId,
    ///     Passkey = passkey,
    ///     ClientId = clientId,
    ///     Nonce = Guid.NewGuid().ToString()  // ← Unique for this request
    /// };
    /// </code>
    /// </remarks>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 signature of the request. Ensures integrity and authenticity.
    /// </summary>
    /// <remarks>
    /// The signature is computed as:
    /// HMAC-SHA256(serverId|timestamp|nonce, passkey)
    ///
    /// This provides:
    /// - Authenticity: Only someone with the correct passkey can create valid signature
    /// - Integrity: Any modification to serverId/timestamp/nonce invalidates signature
    /// - Non-repudiation: Proof that request came from someone with the passkey
    ///
    /// Null for legacy clients (backward compatibility).
    /// New clients should always set this using RequestSigningService.SignRequest().
    ///
    /// Example:
    /// <code>
    /// var signature = RequestSigningService.SignRequest(
    ///     serverId, passkey, timestamp, nonce);
    ///
    /// var message = new ClientAuthenticationMessage
    /// {
    ///     // ... other fields ...
    ///     Signature = signature
    /// };
    /// </code>
    /// </remarks>
    public string? Signature { get; set; }
}

/// <summary>
/// Response from SignalingServer to client authentication attempt.
/// </summary>
public sealed class ClientAuthenticationResponse
{
    /// <summary>
    /// Whether authentication was successful.
    /// </summary>
    public required bool Success { get; set; }

    /// <summary>
    /// Error message if authentication failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether remote control is enabled (if authentication succeeded).
    /// </summary>
    public bool RemoteControlEnabled { get; set; }

    /// <summary>
    /// One-time token the client must present on the /signal WebSocket upgrade (query parameter <c>token</c>).
    /// It is bound to the ClientId used in the authentication request.
    /// </summary>
    public string? WebSocketToken { get; set; }

    /// <summary>
    /// Single-use token the client can POST to /resume to reconnect without the passkey (which rotates every 45 s).
    /// Every /resume response carries the next one.
    /// </summary>
    public string? ResumeToken { get; set; }

    /// <summary>
    /// STUN/TURN servers (including fresh TURN credentials) the client should use for its peer connection.
    /// </summary>
    public List<IceServerInfo>? IceServers { get; set; }
}

/// <summary>
/// Body of POST /resume: a viewer that lost its WebSocket trades its resume token for a new WebSocket token.
/// </summary>
public sealed class ClientResumeMessage
{
    /// <summary>
    /// The resume token from the last /authenticate or /resume response.
    /// </summary>
    public string ResumeToken { get; set; } = string.Empty;
}
