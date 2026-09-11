using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeskShare.Core.Models;
using DeskShare.Core.Auth;
using DeskShare.Core.Validation;

namespace DeskShare.SignalingServer.Services;

/// <summary>
/// Manages WebSocket connections and message routing between peers.
/// Provides connection tracking, statistics, and event logging.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ServerSession> _serverSessions = new();
    private readonly ConcurrentDictionary<string, TrustedConnection> _trustedConnections = new();
    private readonly ILogger<ConnectionManager> _logger;
    private readonly Stopwatch _serverUptime = Stopwatch.StartNew();
    private readonly Timer _sessionCleanupTimer;
    private readonly NonceCache _nonceCache;

    // WebSocket tokens: maps token -> (clientId, createdAt)
    // Tokens are single-use and expire after a configurable number of seconds.
    private readonly ConcurrentDictionary<string, (string ClientId, DateTime CreatedAt)> _wsTokens = new();
    private int _tokenExpirationSeconds = 30;

    // Connection limits
    private int _maxConnections = 100;
    private int _idleTimeoutSeconds = 300;
    private bool _requireHmacSignature;

    // Aggregate statistics
    private long _totalConnectionsEver;
    private long _totalDisconnectsEver;
    private long _totalMessagesSent;
    private long _totalMessagesReceived;
    private long _totalRateLimitHits;
    private long _totalAuthFailures;
    private long _totalAuthSuccesses;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionManager"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public ConnectionManager(ILogger<ConnectionManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Initialize nonce cache for replay attack prevention
        _nonceCache = new NonceCache(
            nonceTtl: TimeSpan.FromSeconds(RequestSigningService.SignatureValiditySeconds),
            cleanupInterval: TimeSpan.FromSeconds(30));

        // Start cleanup timer to remove expired/invalid sessions every 10 seconds
        _sessionCleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Registers a new client connection.
    /// </summary>
    /// <param name="clientId">Unique client identifier.</param>
    /// <param name="webSocket">WebSocket connection.</param>
    /// <param name="remoteAddress">Client's remote address (optional).</param>
    /// <returns>True if registered successfully, false if client ID already exists.</returns>
    public bool RegisterClient(string clientId, WebSocket webSocket, string remoteAddress = "unknown")
    {
        Guard.NotNullOrWhiteSpace(clientId, nameof(clientId));
        Guard.NotNull(webSocket, nameof(webSocket));

        var connection = new ClientConnection
        {
            ClientId = clientId,
            WebSocket = webSocket,
            RemoteAddress = remoteAddress,
            ConnectedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        var added = _connections.TryAdd(clientId, connection);
        if (added)
        {
            Interlocked.Increment(ref _totalConnectionsEver);
            _logger.LogInformation(
                "Client connected | ClientId: {ClientId} | RemoteAddress: {RemoteAddress} | ActiveConnections: {ActiveCount} | TotalConnectionsEver: {TotalEver}",
                clientId, remoteAddress, _connections.Count, _totalConnectionsEver);
        }
        else
        {
            _logger.LogWarning("Client registration failed - ID already exists | ClientId: {ClientId}", clientId);
        }

        return added;
    }

    /// <summary>
    /// Unregisters a client connection.
    /// </summary>
    /// <param name="clientId">Client identifier to unregister.</param>
    /// <param name="reason">Reason for disconnection (optional).</param>
    public void UnregisterClient(string clientId, string reason = "")
    {
        if (_connections.TryRemove(clientId, out var connection))
        {
            Interlocked.Increment(ref _totalDisconnectsEver);

            var sessionDuration = DateTime.UtcNow - connection.ConnectedAt;
            _logger.LogInformation(
                "Client disconnected | ClientId: {ClientId} | RemoteAddress: {RemoteAddress} | Reason: {Reason} | SessionDuration: {Duration:hh\\:mm\\:ss} | MessagesSent: {Sent} | MessagesReceived: {Received} | ActiveConnections: {ActiveCount}",
                clientId,
                connection.RemoteAddress,
                string.IsNullOrWhiteSpace(reason) ? "Normal disconnect" : reason,
                sessionDuration,
                connection.MessagesSent,
                connection.MessagesReceived,
                _connections.Count);
        }
    }

    /// <summary>
    /// Sends a signaling message to a specific client.
    /// </summary>
    /// <param name="message">Message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    public async Task SendMessageAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        if (string.IsNullOrWhiteSpace(message.TargetId))
        {
            _logger.LogWarning("Message has no target ID");
            return;
        }

        if (!_connections.TryGetValue(message.TargetId, out var connection))
        {
            _logger.LogWarning("Target client {TargetId} not found. Available clients: {AvailableClients}",
                message.TargetId, string.Join(", ", _connections.Keys));
            return;
        }

        if (connection.WebSocket.State != WebSocketState.Open)
        {
            _logger.LogWarning("Target client {TargetId} WebSocket is not open (state: {State})",
                message.TargetId, connection.WebSocket.State);
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(bytes);

            await connection.WebSocket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken);

            Interlocked.Increment(ref connection._messagesSent);
            connection.LastActivityAt = DateTime.UtcNow;
            Interlocked.Increment(ref _totalMessagesSent);

            _logger.LogDebug("Message sent | TargetId: {TargetId} | Type: {Type} | Size: {Size} bytes",
                message.TargetId, message.Type, bytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message | TargetId: {TargetId} | Type: {Type}",
                message.TargetId, message.Type);
        }
    }

    /// <summary>
    /// Broadcasts a message to all connected clients except the sender.
    /// </summary>
    /// <param name="senderId">Sender client ID to exclude.</param>
    /// <param name="message">Message to broadcast.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    public async Task BroadcastAsync(string senderId, SignalingMessage message, CancellationToken cancellationToken = default)
    {
        var taskList = _connections
            .Where(kvp => kvp.Key != senderId && kvp.Value.WebSocket.State == WebSocketState.Open)
            .Select(kvp =>
            {
                var broadcastMessage = new SignalingMessage
                {
                    Type = message.Type,
                    SenderId = message.SenderId,
                    TargetId = kvp.Key,
                    Sdp = message.Sdp,
                    Candidate = message.Candidate,
                    SdpMLineIndex = message.SdpMLineIndex,
                    SdpMid = message.SdpMid,
                    ErrorMessage = message.ErrorMessage,
                    Timestamp = message.Timestamp
                };
                return SendMessageAsync(broadcastMessage, cancellationToken);
            })
            .ToList();

        await Task.WhenAll(taskList);

        _logger.LogDebug("Broadcast message | SenderId: {SenderId} | Type: {Type} | Recipients: {Count}",
            senderId, message.Type, taskList.Count);
    }

    /// <summary>
    /// Records that a message was received from a client.
    /// Updates client statistics and activity timestamp.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    public void RecordMessageReceived(string clientId)
    {
        if (_connections.TryGetValue(clientId, out var connection))
        {
            Interlocked.Increment(ref connection._messagesReceived);
            connection.LastActivityAt = DateTime.UtcNow;
            Interlocked.Increment(ref _totalMessagesReceived);
        }
    }

    /// <summary>
    /// Gets comprehensive statistics about all connections.
    /// </summary>
    /// <returns>Statistics object with connection and message counts.</returns>
    public ConnectionStatistics GetStatistics()
    {
        var connections = _connections.Values.ToList();

        return new ConnectionStatistics
        {
            ActiveConnections = connections.Count,
            MaxConnections = _maxConnections,
            TotalConnectionsEver = _totalConnectionsEver,
            TotalDisconnectsEver = _totalDisconnectsEver,
            TotalMessagesSent = _totalMessagesSent,
            TotalMessagesReceived = _totalMessagesReceived,
            TotalRateLimitHits = _totalRateLimitHits,
            TotalAuthFailures = _totalAuthFailures,
            TotalAuthSuccesses = _totalAuthSuccesses,
            ServerUptime = _serverUptime.Elapsed,
            AverageSessionDuration = connections.Any()
                ? TimeSpan.FromSeconds(connections.Average(c => (DateTime.UtcNow - c.ConnectedAt).TotalSeconds))
                : TimeSpan.Zero,
            Connections = connections.Select(c => new ClientConnectionInfo
            {
                ClientId = c.ClientId,
                RemoteAddress = c.RemoteAddress,
                ConnectedAt = c.ConnectedAt,
                LastActivityAt = c.LastActivityAt,
                SessionDuration = DateTime.UtcNow - c.ConnectedAt,
                MessagesSent = c.MessagesSent,
                MessagesReceived = c.MessagesReceived,
                WebSocketState = c.WebSocket.State.ToString()
            }).ToList()
        };
    }

    /// <summary>
    /// Gets the current number of connected clients.
    /// </summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Gets the status of a specific server.
    /// </summary>
    /// <param name="serverId">Server ID to check</param>
    /// <returns>Server status information or null if server not found</returns>
    public ServerStatusInfo? GetServerStatus(string serverId)
    {
        if (!_serverSessions.TryGetValue(serverId, out var session))
        {
            return null; // Server not registered
        }

        return new ServerStatusInfo
        {
            ServerId = serverId,
            Status = session.Status,
            IsOnline = session.IsValid(DateTime.UtcNow),
            LastActivity = session.LastActivity,
            ConnectedClientId = session.ConnectedClientId,
            RemoteControlEnabled = session.RemoteControlEnabled,
            CreatedAt = session.CreatedAt,
            ConnectedAt = session.ConnectedAt
        };
    }

    /// <summary>
    /// Gets status for all registered servers.
    /// </summary>
    public List<ServerStatusInfo> GetAllServerStatuses()
    {
        return _serverSessions.Values
            .Select(session => new ServerStatusInfo
            {
                ServerId = session.ServerId,
                Status = session.Status,
                IsOnline = session.IsValid(DateTime.UtcNow),
                LastActivity = session.LastActivity,
                ConnectedClientId = session.ConnectedClientId,
                RemoteControlEnabled = session.RemoteControlEnabled,
                CreatedAt = session.CreatedAt,
                ConnectedAt = session.ConnectedAt
            })
            .ToList();
    }

    #region Server Session Management

    /// <summary>
    /// Registers a server with authentication details.
    /// Called when server starts and wants to accept client connections.
    /// </summary>
    public ServerRegistrationResponse RegisterServer(ServerRegistrationMessage registration)
    {
        try
        {
            var session = new ServerSession
            {
                ServerId = registration.ServerId,
                Passkey = registration.Passkey,
                ValidTo = registration.ValidTo,
                RemoteControlEnabled = registration.RemoteControlEnabled,
                TrustClientPermanent = registration.TrustClientPermanent,
                Status = SessionStatus.Registered,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            // Add or update session
            _serverSessions.AddOrUpdate(registration.ServerId, session, (key, existing) =>
            {
                // Update existing session with new passkey
                existing.Passkey = registration.Passkey;
                existing.ValidTo = registration.ValidTo;
                existing.RemoteControlEnabled = registration.RemoteControlEnabled;
                existing.TrustClientPermanent = registration.TrustClientPermanent;
                existing.Status = SessionStatus.Registered;
                existing.LastActivity = DateTime.UtcNow;
                return existing;
            });

            _logger.LogInformation(
                "Server registered | ServerId: {ServerId} | Passkey: {Passkey} | ValidTo: {ValidTo} | RemoteControl: {RemoteControl}",
                registration.ServerId,
                AuthenticationService.FormatPasskeyForDisplay(registration.Passkey),
                registration.ValidTo.ToLocalTime(),
                registration.RemoteControlEnabled);

            return new ServerRegistrationResponse
            {
                Success = true,
                ServerId = registration.ServerId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register server: {ServerId}", registration.ServerId);
            return new ServerRegistrationResponse
            {
                Success = false,
                ErrorMessage = $"Registration failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Authenticates a client attempting to connect to a server.
    /// Validates HMAC signature, passkey, and checks if server is available.
    /// If client is trusted, skips passkey validation.
    /// </summary>
    public ClientAuthenticationResponse AuthenticateClient(ClientAuthenticationMessage auth)
    {
        // === STEP 1: HMAC SIGNATURE VALIDATION (if provided) ===
        // This MUST be checked BEFORE passkey validation to prevent timing attacks
        if (!string.IsNullOrEmpty(auth.Signature))
        {
            _logger.LogDebug(
                "Validating HMAC signature | ServerId: {ServerId} | ClientId: {ClientId} | Nonce: {Nonce}",
                auth.ServerId, auth.ClientId, auth.Nonce);

            // Validate HMAC signature
            if (!RequestSigningService.ValidateSignatureWithReason(
                auth.ServerId,
                auth.Passkey,
                auth.Timestamp,
                auth.Nonce,
                auth.Signature,
                out string failureReason))
            {
                _logger.LogWarning(
                    "Authentication failed - invalid HMAC signature | ServerId: {ServerId} | ClientId: {ClientId} | Reason: {Reason}",
                    auth.ServerId, auth.ClientId, failureReason);

                return new ClientAuthenticationResponse
                {
                    Success = false,
                    ErrorMessage = "Invalid request signature"
                };
            }

            // Check nonce (prevent replay attacks)
            if (!_nonceCache.TryUseNonce(auth.Nonce))
            {
                _logger.LogWarning(
                    "Replay attack detected - nonce already used | ServerId: {ServerId} | ClientId: {ClientId} | Nonce: {Nonce}",
                    auth.ServerId, auth.ClientId, auth.Nonce);

                return new ClientAuthenticationResponse
                {
                    Success = false,
                    ErrorMessage = "Request already processed (replay detected)"
                };
            }

            _logger.LogInformation(
                "HMAC signature validated successfully | ServerId: {ServerId} | ClientId: {ClientId}",
                auth.ServerId, auth.ClientId);
        }
        else if (_requireHmacSignature)
        {
            _logger.LogWarning(
                "Authentication rejected — HMAC signature required but not provided | ServerId: {ServerId} | ClientId: {ClientId}",
                auth.ServerId, auth.ClientId);

            return new ClientAuthenticationResponse
            {
                Success = false,
                ErrorMessage = "HMAC signature is required"
            };
        }
        else
        {
            _logger.LogWarning(
                "Client authenticated without HMAC signature (legacy mode) | ServerId: {ServerId} | ClientId: {ClientId} | SECURITY RISK",
                auth.ServerId, auth.ClientId);
        }

        // === STEP 2: SESSION VALIDATION ===
        // Check if server session exists
        if (!_serverSessions.TryGetValue(auth.ServerId, out var session))
        {
            _logger.LogWarning("Client authentication failed - server not found | ServerId: {ServerId} | ClientId: {ClientId}",
                auth.ServerId, auth.ClientId);

            return new ClientAuthenticationResponse
            {
                Success = false,
                ErrorMessage = $"Server '{auth.ServerId}' not found or not accepting connections"
            };
        }

        // Check if session is valid (not expired)
        if (!session.IsValid(DateTime.UtcNow))
        {
            _logger.LogWarning("Client authentication failed - session expired | ServerId: {ServerId} | ClientId: {ClientId}",
                auth.ServerId, auth.ClientId);

            return new ClientAuthenticationResponse
            {
                Success = false,
                ErrorMessage = "Server session has expired. Please refresh the passkey."
            };
        }

        // Check if another client is already connected
        if (session.Status == SessionStatus.Connected && session.ConnectedClientId != auth.ClientId)
        {
            _logger.LogWarning(
                "Client authentication failed - another client already connected | ServerId: {ServerId} | ClientId: {ClientId} | ConnectedClient: {ConnectedClient}",
                auth.ServerId, auth.ClientId, session.ConnectedClientId);

            return new ClientAuthenticationResponse
            {
                Success = false,
                ErrorMessage = "Another client is already connected to this server"
            };
        }

        // === STEP 3: PASSKEY VALIDATION (unless trusted) ===
        // Check if client is permanently trusted - if so, skip passkey validation
        bool isTrusted = IsTrustedClient(auth.ServerId, auth.ClientId);

        if (!isTrusted)
        {
            // Not trusted - validate passkey
            if (!AuthenticationService.ValidatePasskey(auth.ServerId, auth.Passkey, DateTime.UtcNow))
            {
                _logger.LogWarning("Client authentication failed - invalid passkey | ServerId: {ServerId} | ClientId: {ClientId}",
                    auth.ServerId, auth.ClientId);

                return new ClientAuthenticationResponse
                {
                    Success = false,
                    ErrorMessage = "Invalid passkey"
                };
            }
        }
        else
        {
            _logger.LogInformation(
                "Client authenticated via permanent trust (passkey skipped) | ServerId: {ServerId} | ClientId: {ClientId}",
                auth.ServerId, auth.ClientId);
        }

        // === STEP 4: SUCCESS - ESTABLISH CONNECTION ===
        // Authentication successful - mark session as connected
        session.MarkAsConnected(auth.ClientId);

        // If server has TrustClientPermanent enabled and client is not yet trusted, establish trust
        if (session.TrustClientPermanent && !isTrusted)
        {
            TrustClient(auth.ServerId, auth.ClientId);
        }

        _logger.LogInformation(
            "Client authenticated successfully | ServerId: {ServerId} | ClientId: {ClientId} | RemoteControl: {RemoteControl} | Trusted: {Trusted} | HMAC: {HmacUsed}",
            auth.ServerId, auth.ClientId, session.RemoteControlEnabled, isTrusted, !string.IsNullOrEmpty(auth.Signature));

        return new ClientAuthenticationResponse
        {
            Success = true,
            RemoteControlEnabled = session.RemoteControlEnabled
        };
    }

    /// <summary>
    /// Periodic cleanup of expired or invalid sessions.
    /// </summary>
    private void CleanupExpiredSessions(object? state)
    {
        try
        {
            var now = DateTime.UtcNow;
            var sessionsToRemove = new List<string>();

            foreach (var kvp in _serverSessions)
            {
                var session = kvp.Value;

                // Check if session is still valid
                if (!session.IsValid(now))
                {
                    sessionsToRemove.Add(kvp.Key);

                    _logger.LogInformation(
                        "Removing invalid session | ServerId: {ServerId} | Status: {Status} | Reason: {Reason}",
                        kvp.Key,
                        session.Status,
                        session.Status == SessionStatus.Expired ? "Passkey expired" : "Session timeout");
                }
            }

            // Remove invalid sessions
            foreach (var serverId in sessionsToRemove)
            {
                _serverSessions.TryRemove(serverId, out _);
            }

            if (sessionsToRemove.Count > 0)
            {
                _logger.LogDebug("Cleaned up {Count} expired sessions", sessionsToRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session cleanup");
        }
    }

    /// <summary>
    /// Establishes a permanent trust relationship between server and client.
    /// Once trusted, client doesn't need passkey for future connections.
    /// </summary>
    public void TrustClient(string serverId, string clientId)
    {
        var key = GetTrustKey(serverId, clientId);
        var trust = new TrustedConnection
        {
            ServerId = serverId,
            ClientId = clientId,
            EstablishedAt = DateTime.UtcNow
        };

        _trustedConnections.AddOrUpdate(key, trust, (k, existing) =>
        {
            existing.EstablishedAt = DateTime.UtcNow;
            existing.LastUsed = DateTime.UtcNow;
            return existing;
        });

        _logger.LogInformation(
            "Trust relationship established | ServerId: {ServerId} | ClientId: {ClientId}",
            serverId, clientId);
    }

    /// <summary>
    /// Checks if a client is permanently trusted by a server.
    /// </summary>
    public bool IsTrustedClient(string serverId, string clientId)
    {
        var key = GetTrustKey(serverId, clientId);
        if (_trustedConnections.TryGetValue(key, out var trust))
        {
            // Update last used timestamp
            trust.LastUsed = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Removes trust relationship between server and client.
    /// </summary>
    public void RevokeTrust(string serverId, string clientId)
    {
        var key = GetTrustKey(serverId, clientId);
        if (_trustedConnections.TryRemove(key, out _))
        {
            _logger.LogInformation(
                "Trust relationship revoked | ServerId: {ServerId} | ClientId: {ClientId}",
                serverId, clientId);
        }
    }

    private static string GetTrustKey(string serverId, string clientId)
    {
        return $"{serverId}:{clientId}";
    }

    #endregion

    #region WebSocket Token Management

    /// <summary>
    /// Configures connection limits and token settings from configuration.
    /// Call this during startup before accepting connections.
    /// </summary>
    public void Configure(int maxConnections = 100, int idleTimeoutSeconds = 300, int tokenExpirationSeconds = 30, bool requireHmacSignature = false)
    {
        _maxConnections = maxConnections;
        _idleTimeoutSeconds = idleTimeoutSeconds;
        _tokenExpirationSeconds = tokenExpirationSeconds;
        _requireHmacSignature = requireHmacSignature;
        _logger.LogInformation(
            "ConnectionManager configured | MaxConnections: {Max} | IdleTimeout: {Idle}s | TokenExpiry: {Token}s | RequireHmac: {Hmac}",
            _maxConnections, _idleTimeoutSeconds, _tokenExpirationSeconds, _requireHmacSignature);
    }

    /// <summary>
    /// Issues a one-time WebSocket upgrade token for an authenticated client.
    /// The token must be used within the configured expiration window.
    /// </summary>
    /// <param name="clientId">The authenticated client ID.</param>
    /// <returns>A short-lived single-use token string.</returns>
    public string IssueWebSocketToken(string clientId)
    {
        var token = Guid.NewGuid().ToString("N");
        _wsTokens[token] = (clientId, DateTime.UtcNow);

        _logger.LogDebug("WebSocket token issued | ClientId: {ClientId} | Token: {Token}", clientId, token[..8]);
        return token;
    }

    /// <summary>
    /// Validates and consumes a WebSocket upgrade token.
    /// Each token can only be used once (prevents replay).
    /// </summary>
    /// <param name="token">The token to validate.</param>
    /// <param name="clientId">Output: the authenticated client ID if valid.</param>
    /// <returns>True if the token is valid, not expired, and not already used.</returns>
    public bool ValidateAndConsumeToken(string token, out string clientId)
    {
        clientId = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        // Remove token atomically (single-use)
        if (!_wsTokens.TryRemove(token, out var tokenData))
        {
            _logger.LogWarning("WebSocket token validation failed - token not found or already used");
            return false;
        }

        // Check expiration
        var age = DateTime.UtcNow - tokenData.CreatedAt;
        if (age.TotalSeconds > _tokenExpirationSeconds)
        {
            _logger.LogWarning("WebSocket token expired | Age: {Age}s | MaxAge: {Max}s",
                age.TotalSeconds, _tokenExpirationSeconds);
            return false;
        }

        clientId = tokenData.ClientId;
        return true;
    }

    /// <summary>
    /// Checks whether the server can accept another connection.
    /// </summary>
    public bool CanAcceptConnection()
    {
        return _connections.Count < _maxConnections;
    }

    /// <summary>
    /// Gets the maximum allowed connections.
    /// </summary>
    public int MaxConnections => _maxConnections;

    /// <summary>
    /// Disconnects clients that have been idle longer than the configured timeout.
    /// Called periodically by the session cleanup timer.
    /// </summary>
    public List<string> GetIdleClients()
    {
        var now = DateTime.UtcNow;
        var threshold = TimeSpan.FromSeconds(_idleTimeoutSeconds);

        return _connections
            .Where(kvp => (now - kvp.Value.LastActivityAt) > threshold)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Records a rate limit hit for statistics.
    /// </summary>
    public void RecordRateLimitHit() => Interlocked.Increment(ref _totalRateLimitHits);

    /// <summary>
    /// Records an authentication failure for statistics.
    /// </summary>
    public void RecordAuthFailure() => Interlocked.Increment(ref _totalAuthFailures);

    /// <summary>
    /// Records an authentication success for statistics.
    /// </summary>
    public void RecordAuthSuccess() => Interlocked.Increment(ref _totalAuthSuccesses);

    /// <summary>
    /// Sends a close message to a specific client and unregisters them.
    /// Used during graceful shutdown and idle cleanup.
    /// </summary>
    public async Task DisconnectClientAsync(string clientId, string reason, CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(clientId, out var connection) &&
            connection.WebSocket.State == WebSocketState.Open)
        {
            try
            {
                await connection.WebSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    reason,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing WebSocket for {ClientId}", clientId);
            }
        }
        UnregisterClient(clientId, reason);
    }

    #endregion

    /// <summary>
    /// Disposes resources used by ConnectionManager.
    /// </summary>
    public void Dispose()
    {
        _sessionCleanupTimer?.Dispose();
        _nonceCache?.Dispose();
    }
}

/// <summary>
/// Represents a single client connection with statistics.
/// </summary>
internal sealed class ClientConnection
{
    public string ClientId { get; set; } = string.Empty;
    public WebSocket WebSocket { get; set; } = null!;
    public string RemoteAddress { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; }
    public DateTime LastActivityAt { get; set; }

    internal long _messagesSent;
    internal long _messagesReceived;

    public long MessagesSent => Interlocked.Read(ref _messagesSent);
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);
}

/// <summary>
/// Comprehensive connection statistics for the signaling server.
/// </summary>
public sealed class ConnectionStatistics
{
    /// <summary>Number of currently active connections.</summary>
    public int ActiveConnections { get; set; }

    /// <summary>Maximum allowed connections.</summary>
    public int MaxConnections { get; set; }

    /// <summary>Total connections ever established since server start.</summary>
    public long TotalConnectionsEver { get; set; }

    /// <summary>Total disconnects ever occurred since server start.</summary>
    public long TotalDisconnectsEver { get; set; }

    /// <summary>Total messages sent by the server.</summary>
    public long TotalMessagesSent { get; set; }

    /// <summary>Total messages received by the server.</summary>
    public long TotalMessagesReceived { get; set; }

    /// <summary>Total rate limit rejections.</summary>
    public long TotalRateLimitHits { get; set; }

    /// <summary>Total failed authentication attempts.</summary>
    public long TotalAuthFailures { get; set; }

    /// <summary>Total successful authentication attempts.</summary>
    public long TotalAuthSuccesses { get; set; }

    /// <summary>Server uptime duration.</summary>
    public TimeSpan ServerUptime { get; set; }

    /// <summary>Average session duration across all active connections.</summary>
    public TimeSpan AverageSessionDuration { get; set; }

    /// <summary>List of individual client connection details.</summary>
    public List<ClientConnectionInfo> Connections { get; set; } = new();
}

/// <summary>
/// Information about a specific client connection.
/// </summary>
public sealed class ClientConnectionInfo
{
    /// <summary>Unique client identifier.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Client's remote address.</summary>
    public string RemoteAddress { get; set; } = string.Empty;

    /// <summary>When the connection was established.</summary>
    public DateTime ConnectedAt { get; set; }

    /// <summary>Last activity timestamp.</summary>
    public DateTime LastActivityAt { get; set; }

    /// <summary>Session duration (how long connected).</summary>
    public TimeSpan SessionDuration { get; set; }

    /// <summary>Total messages sent to this client.</summary>
    public long MessagesSent { get; set; }

    /// <summary>Total messages received from this client.</summary>
    public long MessagesReceived { get; set; }

    /// <summary>Current WebSocket state.</summary>
    public string WebSocketState { get; set; } = string.Empty;
}

/// <summary>
/// Represents a permanent trust relationship between server and client.
/// When established, client doesn't need passkey for future connections.
/// </summary>
internal sealed class TrustedConnection
{
    /// <summary>Server ID in the trust relationship.</summary>
    public required string ServerId { get; set; }

    /// <summary>Client ID in the trust relationship.</summary>
    public required string ClientId { get; set; }

    /// <summary>When the trust was established.</summary>
    public DateTime EstablishedAt { get; set; }

    /// <summary>Last time this trust was used for authentication.</summary>
    public DateTime LastUsed { get; set; }
}

/// <summary>
/// Status information for a registered server.
/// Used for online/offline monitoring by clients.
/// </summary>
public sealed class ServerStatusInfo
{
    /// <summary>Unique server identifier.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Current session status (Registered, Connected, Expired, Disconnected).</summary>
    public SessionStatus Status { get; set; }

    /// <summary>Whether the server is currently online and accepting connections.</summary>
    public bool IsOnline { get; set; }

    /// <summary>Last activity timestamp (UTC).</summary>
    public DateTime LastActivity { get; set; }

    /// <summary>ID of connected client (null if no client connected).</summary>
    public string? ConnectedClientId { get; set; }

    /// <summary>Whether remote control is enabled on this server.</summary>
    public bool RemoteControlEnabled { get; set; }

    /// <summary>When the session was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When client connected (UTC), null if not connected.</summary>
    public DateTime? ConnectedAt { get; set; }

    /// <summary>Uptime duration since server registered.</summary>
    public TimeSpan Uptime => DateTime.UtcNow - CreatedAt;

    /// <summary>Session duration if client is connected.</summary>
    public TimeSpan? SessionDuration => ConnectedAt.HasValue
        ? DateTime.UtcNow - ConnectedAt.Value
        : null;
}
