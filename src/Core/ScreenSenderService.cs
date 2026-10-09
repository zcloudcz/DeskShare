using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeskShare.Core.WebRTC;
using DeskShare.Core.Conversion;
using DeskShare.Core.Pipeline;
using DeskShare.Core.Video;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Auth;
using DeskShare.Core.Platform;
using DeskShare.Core.Signaling;
using Serilog;
using System.Net.Http.Json;
using SIPSorcery.Net;

namespace DeskShare.Core;

/// <summary>
/// Core screen sender service that can be hosted in-process.
/// Provides screen capture and WebRTC streaming functionality without console UI.
/// </summary>
public class ScreenSenderService : IHostedService, IDisposable
{
    private readonly ILogger<ScreenSenderService> _logger;
    private readonly ScreenSenderConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private Task? _executingTask;
    private CancellationTokenSource? _stoppingCts;
    private WebRTCSession? _webrtcSession;
    private WebRTCSessionWithInput? _webrtcSessionWithInput;
    private CapturePipeline? _pipeline;
    private bool _disposed;

    // One-time WebSocket upgrade token received from /register (see ServerRegistrationResponse)
    private string? _webSocketToken;

    // Proves to the SignalingServer that we own our ServerId (see ServerRegistrationMessage.OwnerSecret)
    private readonly ServerOwnerSecretStore _ownerSecretStore = new();

    // Latest STUN/TURN servers from /register. The first registration happens before the WebRTC session exists,
    // so we keep them here and apply them when the session is created and again after every re-registration.
    private volatile List<RTCIceServer>? _iceServers;

    // 1 while a signaling reconnect loop is running, so a burst of "connection lost" events starts only one loop.
    private int _reconnecting;

    // Passkey rotation fields
    private string? _currentPasskey;
    private DateTime _passkeyValidTo;
    private Task? _passkeyRotationTask;

    public ScreenSenderService(
        ILogger<ScreenSenderService> logger,
        ScreenSenderConfiguration configuration)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>
    /// Gets the Server ID for this instance.
    /// </summary>
    public string ServerId => _configuration.ServerId;

    /// <summary>
    /// Gets the current passkey (9-character authentication code).
    /// </summary>
    public string? CurrentPasskey => _currentPasskey;

    /// <summary>
    /// Gets when the current passkey expires.
    /// </summary>
    public DateTime PasskeyValidTo => _passkeyValidTo;

    /// <summary>
    /// Gets whether the service is currently running.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Event raised when a new passkey is generated.
    /// Subscribers (UI) should update display when this fires.
    /// </summary>
    public event EventHandler<PasskeyChangedEventArgs>? PasskeyChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting ScreenSenderService with Server ID: {ServerId}", ServerId);

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _executingTask = ExecuteAsync(_stoppingCts.Token);

        if (_executingTask.IsCompleted)
        {
            return _executingTask;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executingTask == null)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Stopping ScreenSenderService...");
            _stoppingCts?.Cancel();
        }
        finally
        {
            await Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }

        IsRunning = false;
        _logger.LogInformation("ScreenSenderService stopped");
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScreenSenderService executing...");
        _logger.LogInformation("Server ID: {ServerId}", ServerId);
        _logger.LogInformation("Signaling Server: {SignalingServer}", _configuration.SignalingServerUrl);
        _logger.LogInformation("Target FPS: {TargetFps}", _configuration.TargetFps);

        IsRunning = true;

        // Generate initial passkey
        await GenerateNewPasskeyAsync();

        // Start passkey rotation task
        _passkeyRotationTask = Task.Run(async () => await PasskeyRotationLoopAsync(stoppingToken), stoppingToken);

        try
        {
            // Get screen resolution
            int width, height;
            using (var tempCapturer = PlatformServiceFactory.CreateScreenCapturer(_configuration.AdapterIndex, _configuration.OutputIndex))
            {
                if (!tempCapturer.Initialize())
                {
                    _logger.LogError("Failed to initialize screen capturer");
                    throw new InvalidOperationException("Failed to initialize screen capturer");
                }

                width = tempCapturer.Width;
                height = tempCapturer.Height;
            }  // Automatically disposed, even if Initialize() throws

            _logger.LogInformation("Screen resolution: {Width}x{Height}", width, height);

            // Create WebRTC session - use WebRTCSessionWithInput if remote control is enabled
            IVideoSource videoSource;
            bool initialized;

            if (_configuration.EnableRemoteControl && OperatingSystem.IsWindows())
            {
                _logger.LogInformation("Creating WebRTC session WITH remote control support");

                // Create input controller for remote control
                var serilogLogger = Serilog.Log.ForContext<IInputController>();
                var inputController = PlatformServiceFactory.CreateInputController(serilogLogger);

                // Create session with input support
                var serilogSessionLogger = Serilog.Log.ForContext<WebRTCSessionWithInput>();
                _webrtcSessionWithInput = new WebRTCSessionWithInput(serilogSessionLogger, inputController, clipboardManager: null);
                ApplyIceServers(_webrtcSessionWithInput.VideoSource);

                // Subscribe to events
                _webrtcSessionWithInput.ConnectionStateChanged += (sender, state) =>
                {
                    _logger.LogInformation("WebRTC connection state: {State}", state);
                };

                _webrtcSessionWithInput.ErrorOccurred += (sender, error) =>
                {
                    _logger.LogError("WebRTC error: {Error}", error);
                };
                _webrtcSessionWithInput.SignalingConnectionLost += (sender, args) => OnSignalingConnectionLost();

                // Initialize
                initialized = await _webrtcSessionWithInput.InitializeAsync(
                    width,
                    height,
                    _configuration.TargetFps,
                    BuildSignalingUrl(),
                    _configuration.ServerId);

                if (!initialized)
                {
                    _logger.LogError("Failed to initialize WebRTC session with input");
                    throw new InvalidOperationException("Failed to initialize WebRTC session with input");
                }

                // Auto-authorize remote control since user explicitly enabled it via checkbox. The controller
                // asks AuthorizationRequested for consent; the checkbox is that consent, so answer yes.
                _logger.LogInformation("Auto-authorizing remote control (user enabled via checkbox)");
                if (inputController is Platforms.Windows.WindowsInputController windowsInput)
                {
                    windowsInput.AuthorizationRequested = _ => true;
                }
                await inputController.RequestAuthorizationAsync("auto-authorized", stoppingToken);

                videoSource = _webrtcSessionWithInput.VideoSource;
                _logger.LogInformation("WebRTC session with input initialized. Client ID: {ClientId}", _webrtcSessionWithInput.ClientId);
            }
            else
            {
                if (_configuration.EnableRemoteControl && !OperatingSystem.IsWindows())
                {
                    _logger.LogWarning("Remote control is only supported on Windows. Falling back to view-only mode.");
                }

                _logger.LogInformation("Creating WebRTC session WITHOUT remote control (view-only mode)");

                // Create basic session (view-only)
                _webrtcSession = new WebRTCSession();
                ApplyIceServers(_webrtcSession.VideoSource);

                // Subscribe to events
                _webrtcSession.ConnectionStateChanged += (sender, state) =>
                {
                    _logger.LogInformation("WebRTC connection state: {State}", state);
                };

                _webrtcSession.ErrorOccurred += (sender, error) =>
                {
                    _logger.LogError("WebRTC error: {Error}", error);
                };
                _webrtcSession.SignalingConnectionLost += (sender, args) => OnSignalingConnectionLost();

                // Initialize with configured ServerId
                initialized = await _webrtcSession.InitializeAsync(
                    width,
                    height,
                    _configuration.TargetFps,
                    BuildSignalingUrl(),
                    _configuration.ServerId);

                if (!initialized)
                {
                    _logger.LogError("Failed to initialize WebRTC session");
                    throw new InvalidOperationException("Failed to initialize WebRTC session");
                }

                videoSource = _webrtcSession.VideoSource;
                _logger.LogInformation("WebRTC session initialized (view-only). Client ID: {ClientId}", _webrtcSession.ClientId);
            }

            // Start capture pipeline with WebRTC video source
            var capturer = PlatformServiceFactory.CreateScreenCapturer(_configuration.AdapterIndex, _configuration.OutputIndex);
            var converter = new SimdPixelConverter(); // Use SIMD-optimized converter

            _pipeline = new CapturePipeline(capturer, converter, videoSource);
            _logger.LogInformation("Capture pipeline initialized with SIMD converter");

            if (_pipeline.Start(_configuration.TargetFps))
            {
                _logger.LogInformation("Capture pipeline started successfully");
                _logger.LogInformation("ScreenSenderService is now streaming. Waiting for clients to connect...");

                // Keep service running until cancellation
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            else
            {
                _logger.LogError("Failed to start capture pipeline");
                throw new InvalidOperationException("Failed to start capture pipeline");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ScreenSenderService operation cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ScreenSenderService execution");
            throw;
        }
        finally
        {
            // Stop pipeline
            if (_pipeline?.IsRunning == true)
            {
                await _pipeline.StopAsync();
            }

            _pipeline?.Dispose();
            _pipeline = null;

            _webrtcSession?.Dispose();
            _webrtcSession = null;

            _webrtcSessionWithInput?.Dispose();
            _webrtcSessionWithInput = null;

            IsRunning = false;
        }
    }

    /// <summary>
    /// Generates a new passkey and updates expiration time.
    /// Raises PasskeyChanged event for UI update and registers with SignalingServer.
    /// </summary>
    private async Task GenerateNewPasskeyAsync()
    {
        var now = DateTime.UtcNow;
        _currentPasskey = AuthenticationService.GeneratePasskey(ServerId, now);
        _passkeyValidTo = AuthenticationService.GetPasskeyExpiration(now);

        _logger.LogInformation("Generated new passkey: {Passkey} (valid until {ValidTo})",
            AuthenticationService.FormatPasskeyForDisplay(_currentPasskey), _passkeyValidTo.ToLocalTime());

        // Raise event for UI update
        PasskeyChanged?.Invoke(this, new PasskeyChangedEventArgs
        {
            Passkey = _currentPasskey,
            ValidTo = _passkeyValidTo
        });

        // Register with SignalingServer
        await RegisterWithSignalingServerAsync();
    }

    /// <summary>
    /// Signaling URL with the one-time token from registration appended, if we have one.
    /// The signaler adds its own <c>clientId</c> parameter; the server prefers <c>token</c> when both are present.
    /// </summary>
    private string BuildSignalingUrl()
    {
        if (string.IsNullOrEmpty(_webSocketToken))
            return _configuration.SignalingServerUrl;

        var separator = _configuration.SignalingServerUrl.Contains('?') ? "&" : "?";
        return $"{_configuration.SignalingServerUrl}{separator}token={Uri.EscapeDataString(_webSocketToken)}";
    }

    /// <summary>
    /// The signaling socket dropped (server restart, network blip). Peer connections that are already
    /// streaming keep working, but without signaling no new viewer can reach us, so reconnect in the background.
    /// </summary>
    private void OnSignalingConnectionLost()
    {
        var stoppingToken = _stoppingCts?.Token ?? CancellationToken.None;
        if (stoppingToken.IsCancellationRequested)
            return;

        // Only one loop at a time; the running loop re-checks the connection when it finishes.
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
            return;

        _logger.LogWarning("Signaling connection lost, reconnecting in the background");
        _ = Task.Run(() => ReconnectSignalingLoopAsync(stoppingToken));
    }

    /// <summary>
    /// Retries "re-register for a fresh one-time token, then reopen the WebSocket" with growing delays
    /// until it works or the service stops. The capture pipeline and video source are not touched.
    /// </summary>
    private async Task ReconnectSignalingLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            for (var attempt = 0; !stoppingToken.IsCancellationRequested; attempt++)
            {
                var delay = ReconnectBackoff.GetDelay(attempt);
                _logger.LogInformation("Signaling reconnect attempt {Attempt} in {Delay} s", attempt + 1, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);

                // The token from the last registration was consumed by the first connection. Clear it so a failed
                // registration (server still down) is detected instead of retrying with the dead token.
                _webSocketToken = null;
                await RegisterWithSignalingServerAsync();
                if (string.IsNullOrEmpty(_webSocketToken))
                    continue;

                try
                {
                    var url = BuildSignalingUrl();
                    if (_webrtcSessionWithInput != null)
                        await _webrtcSessionWithInput.ReconnectSignalingAsync(url, ServerId, stoppingToken);
                    else if (_webrtcSession != null)
                        await _webrtcSession.ReconnectSignalingAsync(url, ServerId, stoppingToken);
                    else
                        return; // service is shutting down, sessions are gone

                    _logger.LogInformation("Signaling reconnected after {Attempts} attempt(s)", attempt + 1);
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Signaling reconnect attempt {Attempt} failed", attempt + 1);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Service is stopping
        }
        finally
        {
            Volatile.Write(ref _reconnecting, 0);

            // The socket may have dropped again after we connected but before the flag was cleared;
            // that event was ignored, so check once more.
            var stillConnected = _webrtcSessionWithInput?.IsSignalingConnected ?? _webrtcSession?.IsSignalingConnected ?? true;
            if (!stillConnected)
                OnSignalingConnectionLost();
        }
    }

    /// <summary>
    /// Hands the latest ICE servers to the video source; it uses them for peer connections created from now on.
    /// </summary>
    private void ApplyIceServers(IVideoSource? videoSource)
    {
        if (_iceServers != null && videoSource is SIPSorceryVideoSource sipSource)
            sipSource.IceServers = _iceServers;
    }

    /// <summary>
    /// Returns the installation's owner secret, or null if the key file cannot be read/written
    /// (registration then still works against a server that has not claimed our ServerId yet).
    /// </summary>
    private string? GetOwnerSecret()
    {
        try
        {
            return _ownerSecretStore.GetOrCreate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load or create the server owner secret");
            return null;
        }
    }

    /// <summary>
    /// Registers this server with the SignalingServer so clients can discover and authenticate.
    /// </summary>
    private async Task RegisterWithSignalingServerAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_currentPasskey))
            {
                _logger.LogWarning("Cannot register - no passkey generated yet");
                return;
            }

            // Extract base URL from SignalingServerUrl (ws://localhost:5151/signal → http://localhost:5151)
            var signalingWsUrl = _configuration.SignalingServerUrl;
            var signalingHttpUrl = signalingWsUrl
                .Replace("ws://", "http://")
                .Replace("wss://", "https://")
                .Replace("/signal", "");

            var registration = new ServerRegistrationMessage
            {
                ServerId = ServerId,
                Passkey = _currentPasskey,
                ValidTo = _passkeyValidTo,
                RemoteControlEnabled = _configuration.EnableRemoteControl,
                TrustClientPermanent = _configuration.TrustClientPermanent,
                OwnerSecret = GetOwnerSecret()
            };

            var registerUrl = $"{signalingHttpUrl}/register";

            _logger.LogDebug("Registering with SignalingServer: {Url}", registerUrl);

            var response = await _httpClient.PostAsJsonAsync(registerUrl, registration);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ServerRegistrationResponse>();
                if (result?.Success == true)
                {
                    _webSocketToken = result.WebSocketToken;

                    // New peer connections (one per viewer) pick up the fresh TURN credentials.
                    if (result.IceServers is { Count: > 0 })
                    {
                        var mapped = IceServerMapper.ToRtcIceServers(result.IceServers);
                        if (mapped.Count > 0)
                        {
                            _iceServers = mapped;
                            ApplyIceServers(_webrtcSession?.VideoSource ?? _webrtcSessionWithInput?.VideoSource);
                        }
                    }
                    _logger.LogInformation("Successfully registered with SignalingServer | ServerId: {ServerId}", ServerId);
                }
                else
                {
                    _logger.LogWarning("SignalingServer registration failed: {Error}", result?.ErrorMessage ?? "Unknown error");
                }
            }
            else
            {
                _logger.LogWarning("SignalingServer registration HTTP error: {StatusCode}", response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to connect to SignalingServer for registration (server may not be running)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering with SignalingServer");
        }
    }

    /// <summary>
    /// Background task that monitors passkey expiration and generates new ones.
    /// Runs until service is stopped.
    /// </summary>
    private async Task PasskeyRotationLoopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Passkey rotation loop started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Calculate time until passkey expires
                var now = DateTime.UtcNow;
                var timeUntilExpiration = _passkeyValidTo - now;

                if (timeUntilExpiration.TotalSeconds <= 0)
                {
                    // Passkey expired, generate new one
                    _logger.LogInformation("Passkey expired, generating new one");
                    await GenerateNewPasskeyAsync();

                    // Wait a small amount before checking again
                    await Task.Delay(1000, stoppingToken);
                }
                else
                {
                    // Wait until passkey expires (or service stops)
                    var delayMs = (int)Math.Min(timeUntilExpiration.TotalMilliseconds, int.MaxValue);
                    _logger.LogDebug("Passkey valid for {Seconds} more seconds", timeUntilExpiration.TotalSeconds);

                    await Task.Delay(delayMs, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Passkey rotation loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in passkey rotation loop");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _stoppingCts?.Cancel();
        _stoppingCts?.Dispose();

        _pipeline?.Dispose();
        _webrtcSession?.Dispose();
        _webrtcSessionWithInput?.Dispose();
        _httpClient?.Dispose();

        _disposed = true;
    }
}

/// <summary>
/// Configuration for ScreenSenderService.
/// </summary>
public class ScreenSenderConfiguration
{
    /// <summary>
    /// Server ID for this instance. If not provided, will be generated from MAC address.
    /// </summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// Signaling server WebSocket URL.
    /// </summary>
    public string SignalingServerUrl { get; set; } = "ws://localhost:5151/signal";

    /// <summary>
    /// Target FPS for screen capture.
    /// </summary>
    public int TargetFps { get; set; } = 30;

    /// <summary>
    /// Adapter index for screen capture (0 = primary GPU).
    /// </summary>
    public int AdapterIndex { get; set; } = 0;

    /// <summary>
    /// Output index for screen capture (0 = primary monitor).
    /// </summary>
    public int OutputIndex { get; set; } = 0;

    /// <summary>
    /// Whether to enable remote control (keyboard and mouse input from client).
    /// Default is false for security reasons - must be explicitly enabled by user.
    /// </summary>
    /// <remarks>
    /// For junior developers:
    /// This is a security setting. When false, clients can only VIEW the screen.
    /// When true, clients can also CONTROL keyboard and mouse.
    /// We default to false (view-only) to prevent unauthorized control.
    /// </remarks>
    public bool EnableRemoteControl { get; set; } = false;

    /// <summary>
    /// Whether to trust the connected client permanently (no passkey required for reconnection).
    /// Default is false for security reasons - must be explicitly enabled by user.
    /// </summary>
    public bool TrustClientPermanent { get; set; } = false;

    /// <summary>
    /// Ensures the configuration has a valid Server ID.
    /// If not set, generates one from MAC address.
    /// </summary>
    public void EnsureServerId()
    {
        if (string.IsNullOrEmpty(ServerId))
        {
            ServerId = ServerIdGenerator.GenerateServerId();
        }
    }
}

/// <summary>
/// Event arguments for passkey change events.
/// </summary>
public class PasskeyChangedEventArgs : EventArgs
{
    /// <summary>
    /// The new passkey (9 characters).
    /// </summary>
    public required string Passkey { get; init; }

    /// <summary>
    /// When the passkey expires (UTC).
    /// </summary>
    public required DateTime ValidTo { get; init; }
}
