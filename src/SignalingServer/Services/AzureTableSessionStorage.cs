using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using DeskShare.Core.Auth;
using DeskShare.Core.Validation;

namespace DeskShare.SignalingServer.Services;

/// <summary>
/// Azure Table Storage implementation for server session management.
/// Provides persistent, scalable storage for server sessions and trusted clients.
/// </summary>
public class AzureTableSessionStorage : IServerSessionStorage
{
    private readonly TableClient _sessionsTable;
    private readonly TableClient _trustedClientsTable;
    private readonly ILogger<AzureTableSessionStorage> _logger;
    private readonly NonceCache _nonceCache;
    private readonly bool _requireHmacSignature;

    /// <summary>
    /// Initializes Azure Table Storage with connection string and logger.
    /// </summary>
    public AzureTableSessionStorage(
        string connectionString,
        ILogger<AzureTableSessionStorage> logger,
        bool requireHmacSignature = false)
    {
        Guard.NotNullOrWhiteSpace(connectionString, nameof(connectionString));
        Guard.NotNull(logger, nameof(logger));

        _logger = logger;
        _requireHmacSignature = requireHmacSignature;
        _nonceCache = new NonceCache(
            nonceTtl: TimeSpan.FromSeconds(RequestSigningService.SignatureValiditySeconds),
            cleanupInterval: TimeSpan.FromSeconds(30));

        var tableServiceClient = new TableServiceClient(connectionString);

        // Create tables if they don't exist
        _sessionsTable = tableServiceClient.GetTableClient("ServerSessions");
        _trustedClientsTable = tableServiceClient.GetTableClient("TrustedClients");

        _sessionsTable.CreateIfNotExists();
        _trustedClientsTable.CreateIfNotExists();

        _logger.LogInformation("Azure Table Storage initialized (tables: ServerSessions, TrustedClients)");
    }

    /// <inheritdoc/>
    public async Task<ServerRegistrationResponse> RegisterServerAsync(ServerRegistrationMessage registration)
    {
        Guard.NotNull(registration, nameof(registration));
        Guard.NotNullOrWhiteSpace(registration.ServerId, nameof(registration.ServerId));
        Guard.NotNullOrWhiteSpace(registration.Passkey, nameof(registration.Passkey));

        try
        {
            var sessionEntity = new ServerSessionEntity
            {
                PartitionKey = "sessions",
                RowKey = registration.ServerId,
                ServerId = registration.ServerId,
                Passkey = HashPasskey(registration.Passkey),
                ValidTo = registration.ValidTo,
                RemoteControlEnabled = registration.RemoteControlEnabled,
                TrustClientPermanent = registration.TrustClientPermanent,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            await _sessionsTable.UpsertEntityAsync(sessionEntity);

            _logger.LogInformation(
                "Server registered | ServerId: {ServerId} | ValidTo: {ValidTo} | RemoteControl: {RemoteControl}",
                registration.ServerId,
                registration.ValidTo,
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
                ErrorMessage = "Failed to register server"
            };
        }
    }

    /// <inheritdoc/>
    public async Task<ClientAuthenticationResponse> AuthenticateClientAsync(ClientAuthenticationMessage authRequest)
    {
        Guard.NotNull(authRequest, nameof(authRequest));
        Guard.NotNullOrWhiteSpace(authRequest.ServerId, nameof(authRequest.ServerId));
        Guard.NotNullOrWhiteSpace(authRequest.ClientId, nameof(authRequest.ClientId));

        try
        {
            // HMAC signature validation
            if (!string.IsNullOrEmpty(authRequest.Signature))
            {
                if (!RequestSigningService.ValidateSignatureWithReason(
                    authRequest.ServerId, authRequest.Passkey,
                    authRequest.Timestamp, authRequest.Nonce,
                    authRequest.Signature, out string failureReason))
                {
                    _logger.LogWarning("Authentication failed - invalid HMAC: {ServerId}/{ClientId} | {Reason}",
                        authRequest.ServerId, authRequest.ClientId, failureReason);
                    return CreateFailureResponse("Invalid request signature");
                }

                if (!_nonceCache.TryUseNonce(authRequest.Nonce))
                {
                    _logger.LogWarning("Replay attack detected: {ServerId}/{ClientId}",
                        authRequest.ServerId, authRequest.ClientId);
                    return CreateFailureResponse("Request already processed (replay detected)");
                }
            }
            else if (_requireHmacSignature)
            {
                _logger.LogWarning("HMAC signature required but not provided: {ServerId}/{ClientId}",
                    authRequest.ServerId, authRequest.ClientId);
                return CreateFailureResponse("HMAC signature is required");
            }
            else
            {
                _logger.LogWarning("Client authenticated without HMAC (legacy): {ServerId}/{ClientId}",
                    authRequest.ServerId, authRequest.ClientId);
            }

            var session = await GetSessionEntityAsync(authRequest.ServerId);

            if (session == null)
            {
                _logger.LogWarning("Authentication failed - server not found: {ServerId}", authRequest.ServerId);
                return CreateFailureResponse("Server not found");
            }

            if (session.ValidTo < DateTime.UtcNow)
            {
                _logger.LogWarning("Authentication failed - session expired: {ServerId}", authRequest.ServerId);
                return CreateFailureResponse("Session expired");
            }

            if (!string.IsNullOrEmpty(session.ConnectedClientId) &&
                session.ConnectedClientId != authRequest.ClientId)
            {
                _logger.LogWarning(
                    "Authentication failed - another client connected: {ServerId}/{ClientId}",
                    authRequest.ServerId, session.ConnectedClientId);
                return CreateFailureResponse("Another client already connected");
            }

            bool isTrusted = await IsTrustedClientAsync(authRequest.ServerId, authRequest.ClientId);

            if (!isTrusted)
            {
                if (!AuthenticationService.ValidatePasskey(
                    authRequest.ServerId,
                    authRequest.Passkey,
                    DateTime.UtcNow))
                {
                    _logger.LogWarning(
                        "Authentication failed - invalid passkey: {ServerId}/{ClientId}",
                        authRequest.ServerId, authRequest.ClientId);
                    return CreateFailureResponse("Invalid passkey");
                }
            }

            // Mark client as connected
            session.ConnectedClientId = authRequest.ClientId;
            session.LastActivity = DateTime.UtcNow;
            await _sessionsTable.UpsertEntityAsync(session);

            // Trust client if permanent trust enabled
            if (session.TrustClientPermanent && !isTrusted)
            {
                await TrustClientAsync(authRequest.ServerId, authRequest.ClientId);
            }

            _logger.LogInformation(
                "Client authenticated | ServerId: {ServerId} | ClientId: {ClientId} | Trusted: {Trusted}",
                authRequest.ServerId, authRequest.ClientId, isTrusted);

            return new ClientAuthenticationResponse
            {
                Success = true,
                RemoteControlEnabled = session.RemoteControlEnabled
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication: {ServerId}/{ClientId}",
                authRequest.ServerId, authRequest.ClientId);
            return CreateFailureResponse("Authentication failed");
        }
    }

    /// <inheritdoc/>
    public async Task<ServerStatusInfo?> GetServerStatusAsync(string serverId)
    {
        Guard.NotNullOrWhiteSpace(serverId, nameof(serverId));

        var session = await GetSessionEntityAsync(serverId);
        return session != null ? MapToStatusInfo(session) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ServerStatusInfo>> GetAllServerStatusesAsync()
    {
        var statuses = new List<ServerStatusInfo>();

        try
        {
            await foreach (var session in _sessionsTable.QueryAsync<ServerSessionEntity>(
                filter: $"PartitionKey eq 'sessions'"))
            {
                statuses.Add(MapToStatusInfo(session));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query all server statuses");
        }

        return statuses;
    }

    /// <inheritdoc/>
    public async Task<bool> IsTrustedClientAsync(string serverId, string clientId)
    {
        Guard.NotNullOrWhiteSpace(serverId, nameof(serverId));
        Guard.NotNullOrWhiteSpace(clientId, nameof(clientId));

        try
        {
            var response = await _trustedClientsTable.GetEntityAsync<TrustedClientEntity>(
                partitionKey: serverId,
                rowKey: clientId);

            // Update last used timestamp
            var entity = response.Value;
            entity.LastUsed = DateTime.UtcNow;
            await _trustedClientsTable.UpsertEntityAsync(entity);

            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task TrustClientAsync(string serverId, string clientId)
    {
        Guard.NotNullOrWhiteSpace(serverId, nameof(serverId));
        Guard.NotNullOrWhiteSpace(clientId, nameof(clientId));

        var entity = new TrustedClientEntity
        {
            PartitionKey = serverId,
            RowKey = clientId,
            ServerId = serverId,
            ClientId = clientId,
            EstablishedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow
        };

        await _trustedClientsTable.UpsertEntityAsync(entity);
        _logger.LogInformation("Client trusted | ServerId: {ServerId} | ClientId: {ClientId}", serverId, clientId);
    }

    /// <inheritdoc/>
    public async Task UpdateServerActivityAsync(string serverId, string? connectedClientId = null)
    {
        Guard.NotNullOrWhiteSpace(serverId, nameof(serverId));

        try
        {
            var session = await GetSessionEntityAsync(serverId);
            if (session != null)
            {
                session.LastActivity = DateTime.UtcNow;
                if (connectedClientId != null)
                {
                    session.ConnectedClientId = connectedClientId;
                }
                await _sessionsTable.UpsertEntityAsync(session);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update server activity: {ServerId}", serverId);
        }
    }

    /// <inheritdoc/>
    public async Task CleanupExpiredSessionsAsync()
    {
        var now = DateTime.UtcNow;
        var expiredSessions = new List<string>();

        try
        {
            await foreach (var session in _sessionsTable.QueryAsync<ServerSessionEntity>(
                filter: $"PartitionKey eq 'sessions'"))
            {
                if (session.ValidTo < now)
                {
                    expiredSessions.Add(session.ServerId);
                    await _sessionsTable.DeleteEntityAsync("sessions", session.ServerId);
                }
            }

            if (expiredSessions.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired sessions", expiredSessions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session cleanup");
        }
    }

    private async Task<ServerSessionEntity?> GetSessionEntityAsync(string serverId)
    {
        try
        {
            var response = await _sessionsTable.GetEntityAsync<ServerSessionEntity>(
                partitionKey: "sessions",
                rowKey: serverId);

            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private ServerStatusInfo MapToStatusInfo(ServerSessionEntity session)
    {
        var now = DateTime.UtcNow;
        var timeSinceActivity = now - session.LastActivity;
        var isOnline = timeSinceActivity.TotalSeconds < 60; // Consider online if activity within 60s

        // Determine session status
        var status = DeskShare.Core.Auth.SessionStatus.Registered;
        if (session.ValidTo < now)
        {
            status = DeskShare.Core.Auth.SessionStatus.Expired;
        }
        else if (!string.IsNullOrEmpty(session.ConnectedClientId))
        {
            status = DeskShare.Core.Auth.SessionStatus.Connected;
        }

        return new ServerStatusInfo
        {
            ServerId = session.ServerId,
            IsOnline = isOnline,
            Status = status,
            LastActivity = session.LastActivity,
            ConnectedClientId = session.ConnectedClientId,
            RemoteControlEnabled = session.RemoteControlEnabled,
            CreatedAt = session.CreatedAt,
            ConnectedAt = !string.IsNullOrEmpty(session.ConnectedClientId) ? session.LastActivity : null
        };
    }

    private ClientAuthenticationResponse CreateFailureResponse(string errorMessage)
    {
        return new ClientAuthenticationResponse
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }

    private static string HashPasskey(string passkey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(passkey));
        return Convert.ToBase64String(hash);
    }
}

// ============================================================================
// Table Entities
// ============================================================================

/// <summary>
/// Azure Table entity for server sessions.
/// PartitionKey: "sessions" (all sessions in one partition for query efficiency)
/// RowKey: ServerId (unique server identifier)
/// </summary>
internal class ServerSessionEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "sessions";
    public string RowKey { get; set; } = string.Empty; // ServerId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string ServerId { get; set; } = string.Empty;
    public string Passkey { get; set; } = string.Empty;
    public DateTime ValidTo { get; set; }
    public bool RemoteControlEnabled { get; set; }
    public bool TrustClientPermanent { get; set; }
    public string? ConnectedClientId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivity { get; set; }
}

/// <summary>
/// Azure Table entity for trusted client relationships.
/// PartitionKey: ServerId (groups all trusted clients by server)
/// RowKey: ClientId (unique client identifier)
/// </summary>
internal class TrustedClientEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // ServerId
    public string RowKey { get; set; } = string.Empty; // ClientId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string ServerId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public DateTime EstablishedAt { get; set; }
    public DateTime LastUsed { get; set; }
}
