using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http;
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
    private Process? _signalingProcess;
    private ScreenSenderService? _screenSenderService;
    private bool _isRunning;
    private string? _serverId;
    private int _activeConnections;

    public bool IsRunning => _isRunning;
    public string? ServerId => _serverId;
    public int ActiveConnections => _activeConnections;

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
            // Start SignalingServer process
            _logger.LogInformation("Starting SignalingServer...");
            var signalingExePath = FindExecutable("RemoteDesktop.SignalingServer.exe");

            if (signalingExePath != null && File.Exists(signalingExePath))
            {
                _signalingProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = signalingExePath,
                        Arguments = "--urls http://localhost:5151",
                        WorkingDirectory = Path.GetDirectoryName(signalingExePath),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                _signalingProcess.Start();
                _logger.LogInformation("SignalingServer started (PID: {ProcessId})", _signalingProcess.Id);

                // Wait for SignalingServer to be ready
                await Task.Delay(3000);
            }
            else
            {
                _logger.LogWarning("SignalingServer executable not found");
                throw new InvalidOperationException("SignalingServer executable not found");
            }

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
                PasskeyChanged?.Invoke(this, args);
            };

            await _screenSenderService.StartAsync(CancellationToken.None);

            _logger.LogInformation("ScreenSenderService started in-process (no console window)");

            _isRunning = true;
            _logger.LogInformation("Server started successfully");

            StartMonitoringConnections();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server");
            await StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Searches common paths for the given executable name.
    /// </summary>
    private string? FindExecutable(string exeName)
    {
        var searchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ScreenSenderApp", "bin", "Debug", "net8.0", exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SignalingServer", "bin", "Debug", "net8.0", exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "ScreenSenderApp", exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "SignalingServer", exeName),
            Path.Combine(AppContext.BaseDirectory, exeName)
        };

        foreach (var path in searchPaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                _logger.LogDebug("Found executable: {Path}", fullPath);
                return fullPath;
            }
        }

        _logger.LogWarning("Executable not found: {ExeName}, searched paths: {Paths}",
            exeName, string.Join(", ", searchPaths.Select(p => Path.GetFullPath(p))));
        return null;
    }

    /// <summary>
    /// Periodically polls the Prometheus metrics endpoint for server stats.
    /// </summary>
    private void StartMonitoringConnections()
    {
        var monitorTimer = new System.Timers.Timer(2000);
        monitorTimer.Elapsed += async (s, e) =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await client.GetStringAsync("http://localhost:9090/metrics");
                var stats = ParsePrometheusMetrics(response);

                var prevConnections = _activeConnections;
                _activeConnections = stats.ActiveConnections;

                if (_activeConnections != prevConnections)
                {
                    ConnectionCountChanged?.Invoke(this, _activeConnections);
                }

                StatsUpdated?.Invoke(this, stats);
            }
            catch (HttpRequestException)
            {
                _logger.LogDebug("Metrics endpoint not available");
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Metrics endpoint timeout (not available)");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to query server metrics");
            }
        };
        monitorTimer.Start();
    }

    /// <summary>
    /// Parses Prometheus-format metrics text into a ServerStats object.
    /// </summary>
    private ServerStats ParsePrometheusMetrics(string metricsText)
    {
        var stats = new ServerStats();
        var lines = metricsText.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith("#")) continue;

            if (line.StartsWith("webrtc_connections_active"))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var count))
                    stats.ActiveConnections = count;
            }
            else if (line.StartsWith("capture_fps"))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var fps))
                    stats.CurrentFps = fps;
            }
            else if (line.StartsWith("system_cpu_usage"))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2 && double.TryParse(parts[1], out var cpu))
                    stats.CpuUsage = cpu;
            }
            else if (line.StartsWith("system_memory_mb"))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2 && double.TryParse(parts[1], out var mem))
                    stats.MemoryUsageMb = mem;
            }
        }
        return stats;
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

            if (_signalingProcess != null && !_signalingProcess.HasExited)
            {
                _logger.LogInformation("Stopping SignalingServer gracefully...");
                _signalingProcess.CloseMainWindow();

                if (!_signalingProcess.WaitForExit(3000))
                {
                    _logger.LogWarning("SignalingServer did not exit gracefully, forcing termination");
                    _signalingProcess.Kill();
                    _signalingProcess.WaitForExit(1000);
                }

                _signalingProcess.Dispose();
                _signalingProcess = null;
            }

            _isRunning = false;
            _serverId = null;
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
