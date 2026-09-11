using DeskShare.Core.Auth;
using DeskShare.Core.Validation;

namespace DeskShare.SignalingServer.Services;

/// <summary>
/// In-memory implementation for server session management.
/// Delegates to ConnectionManager for backward compatibility.
/// </summary>
public class InMemorySessionStorage : IServerSessionStorage
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger<InMemorySessionStorage> _logger;

    /// <summary>
    /// Initializes in-memory session storage with ConnectionManager delegate.
    /// </summary>
    public InMemorySessionStorage(
        ConnectionManager connectionManager,
        ILogger<InMemorySessionStorage> logger)
    {
        Guard.NotNull(connectionManager, nameof(connectionManager));
        Guard.NotNull(logger, nameof(logger));

        _connectionManager = connectionManager;
        _logger = logger;

        _logger.LogInformation("In-memory session storage initialized");
    }

    /// <inheritdoc/>
    public Task<ServerRegistrationResponse> RegisterServerAsync(ServerRegistrationMessage registration)
    {
        var response = _connectionManager.RegisterServer(registration);
        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public Task<ClientAuthenticationResponse> AuthenticateClientAsync(ClientAuthenticationMessage authRequest)
    {
        var response = _connectionManager.AuthenticateClient(authRequest);
        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public Task<ServerStatusInfo?> GetServerStatusAsync(string serverId)
    {
        var status = _connectionManager.GetServerStatus(serverId);
        return Task.FromResult(status);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ServerStatusInfo>> GetAllServerStatusesAsync()
    {
        var statuses = _connectionManager.GetAllServerStatuses();
        return Task.FromResult<IReadOnlyList<ServerStatusInfo>>(statuses);
    }

    /// <inheritdoc/>
    public Task<bool> IsTrustedClientAsync(string serverId, string clientId)
    {
        // ConnectionManager doesn't expose IsTrusted directly, but it's checked during authentication
        // For in-memory mode, we'll return false to always require passkey validation
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public Task TrustClientAsync(string serverId, string clientId)
    {
        // In-memory mode handles trust via ConnectionManager internally during authentication
        _logger.LogDebug("Trust operation called in in-memory mode (handled by ConnectionManager)");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateServerActivityAsync(string serverId, string? connectedClientId = null)
    {
        // ConnectionManager updates activity automatically via RecordMessageReceived
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task CleanupExpiredSessionsAsync()
    {
        // ConnectionManager's internal _serverRegistry uses concurrent dictionary
        // Cleanup would need to be added to ConnectionManager if needed
        _logger.LogDebug("Cleanup called in in-memory mode (no automatic cleanup currently)");
        return Task.CompletedTask;
    }
}
