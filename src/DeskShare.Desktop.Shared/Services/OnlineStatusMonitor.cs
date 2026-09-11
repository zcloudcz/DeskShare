using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using DeskShare.Desktop.Shared.Models;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Monitors online status of trusted servers by periodically checking SignalingServer.
/// Platform-independent: shared between WPF and Avalonia desktop clients.
/// </summary>
public class OnlineStatusMonitor : IDisposable
{
    private readonly ILogger<OnlineStatusMonitor> _logger;
    private readonly ConnectionManager _connectionManager;
    private readonly HttpClient _httpClient;
    private Timer? _checkTimer;
    private bool _disposed;

    private const string SignalingServerUrl = "http://localhost:5151";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Event raised when online status changes for a trusted server.
    /// </summary>
    public event EventHandler<OnlineStatusChangedEventArgs>? OnlineStatusChanged;

    public OnlineStatusMonitor(
        ILogger<OnlineStatusMonitor> logger,
        ConnectionManager connectionManager)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));

        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };

        _logger.LogInformation("OnlineStatusMonitor initialized");
    }

    /// <summary>
    /// Starts monitoring trusted servers.
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OnlineStatusMonitor));

        if (_checkTimer != null)
        {
            _logger.LogWarning("Online status monitoring already started");
            return;
        }

        _logger.LogInformation("Starting online status monitoring (interval: {Interval}s)", CheckInterval.TotalSeconds);

        // First check immediately
        _ = CheckAllTrustedServersAsync();

        // Then schedule periodic checks
        _checkTimer = new Timer(
            async _ => await CheckAllTrustedServersAsync(),
            null,
            CheckInterval,
            CheckInterval);
    }

    /// <summary>
    /// Stops monitoring.
    /// </summary>
    public void Stop()
    {
        if (_checkTimer != null)
        {
            _checkTimer.Dispose();
            _checkTimer = null;
            _logger.LogInformation("Online status monitoring stopped");
        }
    }

    private async Task CheckAllTrustedServersAsync()
    {
        try
        {
            var trustedServers = await _connectionManager.GetTrustedConnectionsAsync();

            if (trustedServers.Count == 0)
            {
                _logger.LogDebug("No trusted servers to check");
                return;
            }

            _logger.LogDebug("Checking online status for {Count} trusted server(s)", trustedServers.Count);

            var checkTasks = trustedServers.Select(async server =>
            {
                var wasOnline = server.IsOnline;
                var isOnline = await CheckServerOnlineAsync(server.ServerId);

                server.IsOnline = isOnline;
                server.LastOnlineCheck = DateTime.UtcNow;

                await _connectionManager.UpdateHistoryEntryAsync(server.Id, entry =>
                {
                    entry.IsOnline = isOnline;
                    entry.LastOnlineCheck = DateTime.UtcNow;
                });

                if (wasOnline != isOnline)
                {
                    _logger.LogInformation(
                        "Server {ServerId} status changed: {OldStatus} -> {NewStatus}",
                        server.ServerId,
                        wasOnline?.ToString() ?? "Unknown",
                        isOnline?.ToString() ?? "Unknown");

                    OnlineStatusChanged?.Invoke(this, new OnlineStatusChangedEventArgs
                    {
                        ServerId = server.ServerId,
                        IsOnline = isOnline,
                        PreviousStatus = wasOnline
                    });
                }
            });

            await Task.WhenAll(checkTasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking online status of trusted servers");
        }
    }

    private async Task<bool?> CheckServerOnlineAsync(string serverId)
    {
        try
        {
            _logger.LogDebug("Checking online status for server {ServerId}", serverId);

            var response = await _httpClient.GetAsync($"{SignalingServerUrl}/servers/{serverId}/status");

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Server {ServerId} not found in registry (offline)", serverId);
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SignalingServer returned error for {ServerId}: {StatusCode}",
                    serverId, response.StatusCode);
                return null;
            }

            var serverStatus = await response.Content.ReadFromJsonAsync<ServerStatusResponse>();

            if (serverStatus == null)
            {
                _logger.LogWarning("Failed to parse server status response for {ServerId}", serverId);
                return null;
            }

            var isOnline = serverStatus.IsOnline;
            _logger.LogDebug(
                "Server {ServerId} status: {Status} | IsOnline: {IsOnline} | LastActivity: {LastActivity}",
                serverId, serverStatus.Status, isOnline, serverStatus.LastActivity);

            return isOnline;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Network error checking server {ServerId}", serverId);
            return false;
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Timeout checking server {ServerId}", serverId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error checking server {ServerId}", serverId);
            return null;
        }
    }

    /// <summary>
    /// Manually triggers an online status check for all trusted servers.
    /// </summary>
    public async Task CheckNowAsync()
    {
        await CheckAllTrustedServersAsync();
    }

    /// <summary>
    /// Checks online status for a specific server.
    /// </summary>
    public async Task<bool?> CheckServerAsync(string serverId)
    {
        return await CheckServerOnlineAsync(serverId);
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _httpClient?.Dispose();
        _disposed = true;

        _logger.LogInformation("OnlineStatusMonitor disposed");
    }
}

/// <summary>
/// Event args for online status change notifications.
/// </summary>
public class OnlineStatusChangedEventArgs : EventArgs
{
    public string ServerId { get; set; } = string.Empty;
    public bool? IsOnline { get; set; }
    public bool? PreviousStatus { get; set; }
}

/// <summary>
/// Response from SignalingServer /servers/{serverId}/status endpoint.
/// </summary>
internal class ServerStatusResponse
{
    public string ServerId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public DateTime LastActivity { get; set; }
    public string? ConnectedClientId { get; set; }
    public bool RemoteControlEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
}
