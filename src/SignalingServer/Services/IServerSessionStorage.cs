using DeskShare.Core.Auth;

namespace DeskShare.SignalingServer.Services;

/// <summary>
/// Interface for server session storage backends.
/// Allows pluggable storage implementations (in-memory, Azure Table Storage, etc.)
/// </summary>
public interface IServerSessionStorage
{
    /// <summary>
    /// Registers a new server session.
    /// </summary>
    Task<ServerRegistrationResponse> RegisterServerAsync(ServerRegistrationMessage registration);

    /// <summary>
    /// Authenticates a client against a server session.
    /// </summary>
    Task<ClientAuthenticationResponse> AuthenticateClientAsync(ClientAuthenticationMessage authRequest);

    /// <summary>
    /// Gets the status of a specific server.
    /// </summary>
    Task<ServerStatusInfo?> GetServerStatusAsync(string serverId);

    /// <summary>
    /// Gets the status of all registered servers.
    /// </summary>
    Task<IReadOnlyList<ServerStatusInfo>> GetAllServerStatusesAsync();

    /// <summary>
    /// Checks if a client is trusted for reconnection to a server.
    /// </summary>
    Task<bool> IsTrustedClientAsync(string serverId, string clientId);

    /// <summary>
    /// Marks a client as trusted for a server (for permanent reconnection).
    /// </summary>
    Task TrustClientAsync(string serverId, string clientId);

    /// <summary>
    /// Updates the last activity timestamp for a server.
    /// </summary>
    Task UpdateServerActivityAsync(string serverId, string? connectedClientId = null);

    /// <summary>
    /// Cleans up expired sessions (called periodically).
    /// </summary>
    Task CleanupExpiredSessionsAsync();
}
