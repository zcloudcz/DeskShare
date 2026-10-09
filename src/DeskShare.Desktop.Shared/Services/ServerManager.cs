using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DeskShare.Core;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Manages the screen sender server (capture + streaming).
/// Uses in-process Core library for all functionality - no external console processes.
/// Platform-independent: shared between WPF and Avalonia desktop clients.
/// </summary>
public class ServerManager : IDisposable
{
    private readonly ILogger<ServerManager> _logger;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private ScreenSenderService? _screenSenderService;
    private bool _isRunning;
    private string? _serverId;
    private int _activeConnections;

    public bool IsRunning => _isRunning;
    public string? ServerId => _serverId;
    public int ActiveConnections => _activeConnections;

    /// <summary>
    /// Last passkey announced by the sender. The first passkey is generated inside <see cref="StartAsync"/>,
    /// i.e. before the UI can subscribe to <see cref="PasskeyChanged"/>, so the UI reads this after starting.
    /// </summary>
    public PasskeyChangedEventArgs? CurrentPasskey { get; private set; }

    // Events for UI updates
    public event EventHandler<int>? ConnectionCountChanged;
    public event EventHandler<ServerStats>? StatsUpdated;
    public event EventHandler<PasskeyChangedEventArgs>? PasskeyChanged;

    public ServerManager(ILogger<ServerManager> logger, IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Starts the server with signaling + screen sender services.
    /// </summary>
    public async Task StartAsync(string serverId, string? password = null, bool enableRemoteControl = false, bool trustClientPermanent = false)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("Server is already running");
        }

        _logger.LogInformation("Starting server with ID: {ServerId}, RemoteControl: {RemoteControl}, TrustClientPermanent: {TrustClientPermanent}",
            serverId, enableRemoteControl, trustClientPermanent);
        _serverId = serverId;

        try
        {
            // Signaling is a hosted service (see Server:SignalingServerUrl); nothing to spawn locally.
            // Start ScreenSenderService IN-PROCESS using Core library
            _logger.LogInformation("Starting ScreenSenderService in-process with Server ID: {ServerId}", serverId);

            var signalingUrl = _configuration.GetValue<string>("Server:SignalingServerUrl");

            if (string.IsNullOrEmpty(signalingUrl))
            {
                throw new InvalidOperationException(
                    "SignalingServerUrl is not configured. Please set 'Server:SignalingServerUrl' in appsettings.json");
            }

            var targetFps = _configuration.GetValue<int>("Server:TargetFps", 30);
            var adapterIndex = _configuration.GetValue<int>("Server:AdapterIndex", 0);
            var outputIndex = _configuration.GetValue<int>("Server:OutputIndex", 0);

            var configuration = new ScreenSenderConfiguration
            {
                ServerId = serverId,
                SignalingServerUrl = signalingUrl,
                TargetFps = targetFps,
                AdapterIndex = adapterIndex,
                OutputIndex = outputIndex,
                EnableRemoteControl = enableRemoteControl,
                TrustClientPermanent = trustClientPermanent
            };

            configuration.EnsureServerId();

            _logger.LogInformation("Configuration: SignalingServerUrl={SignalingUrl}, TargetFps={Fps}",
                signalingUrl, targetFps);

            var serviceLogger = _loggerFactory.CreateLogger<ScreenSenderService>();
            _screenSenderService = new ScreenSenderService(serviceLogger, configuration);

            // Subscribe to passkey changes
            _screenSenderService.PasskeyChanged += (sender, args) =>
            {
                CurrentPasskey = args;
                PasskeyChanged?.Invoke(this, args);
            };

            await _screenSenderService.StartAsync(CancellationToken.None);

            _logger.LogInformation("ScreenSenderService started in-process (no console window)");

            _isRunning = true;
            _logger.LogInformation("Server started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server");
            await StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops the server and all related services.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _logger.LogInformation("Stopping server...");

        try
        {
            if (_screenSenderService != null)
            {
                _logger.LogInformation("Stopping ScreenSenderService gracefully...");
                await _screenSenderService.StopAsync(CancellationToken.None);
                _screenSenderService.Dispose();
                _screenSenderService = null;
                _logger.LogInformation("ScreenSenderService stopped");
            }

            _isRunning = false;
            _serverId = null;
            CurrentPasskey = null;
            _activeConnections = 0;

            _logger.LogInformation("Server stopped successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping server");
            throw;
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// Server performance statistics from Prometheus metrics endpoint.
/// </summary>
public class ServerStats
{
    public int ActiveConnections { get; set; }
    public int CurrentFps { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsageMb { get; set; }
}
