using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeskShare.Core.WebRTC;
using DeskShare.Core.Conversion;
using DeskShare.Core.Displays;
using DeskShare.Core.Pipeline;
using DeskShare.Core.Video;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Auth;
using DeskShare.Core.Platform;
using DeskShare.Core.Signaling;
using DeskShare.Core.Safety;
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
    private readonly string _serverIdPath;

    // Rotation and signaling reconnects can both register. Without serialization an older request could finish
    // last and put an old passkey back on the server, so the viewer would be told the new one but be refused.
    private readonly SemaphoreSlim _registerLock = new(1, 1);
    private Task? _executingTask;
    private CancellationTokenSource? _stoppingCts;
    private WebRTCSessionWithInput? _webrtcSessionWithInput;
    private CapturePipeline? _pipeline;

    // The host pointer is drawn into the picture (Linux X11, macOS) unless a viewer is controlling right now: then the
    // viewer's browser already shows its own pointer and a second one in the video trails behind it. "Right now" =
    // input arrived within PointerHideAfterInput, since the viewer does not report switching control off.
    private static readonly TimeSpan PointerHideAfterInput = TimeSpan.FromSeconds(5);
    private volatile bool _pointerDrawn = true;  // what the running capturer does
    // Adapter and output of the running capturer: exactly what was opened (the configured target, or the display the
    // viewer switched to), so a pointer swap never changes which screen is shared.
    private (int Adapter, int Output)? _captureTarget;
    private bool _disposed;
    private SessionSafety? _safety;

    /// <summary>
    /// Asks the person at the computer whether a viewer may take control (attended app, see
    /// <see cref="ScreenSenderConfiguration.RequireControlApproval"/>). The token is cancelled when the question is moot or
    /// times out; the prompt must then close. Set before <see cref="StartAsync"/>.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? ControlApprovalPrompt { get; set; }

    /// <summary>Unattended agent: asks whether a verified viewer may connect at all. Null admits everyone. Set before <see cref="StartAsync"/>.</summary>
    public Func<CancellationToken, Task<bool>>? ViewerAdmission { get; set; }

    // One-time WebSocket upgrade token received from /register (see ServerRegistrationResponse)
    private string? _webSocketToken;

    // Proves to the SignalingServer that we own our ServerId (see ServerRegistrationMessage.OwnerSecret)
    private readonly ServerOwnerSecretStore _ownerSecretStore;

    // Server IDs are short (9 digits), so two installations can pick the same one. The signaling server then
    // answers /register with 403; we pick a new ID this many times before giving up.
    private const int MaxServerIdAttempts = 5;

    // Latest STUN/TURN servers from /register. The first registration happens before the WebRTC session exists,
    // so we keep them here and apply them when the session is created and again after every re-registration.
    private volatile List<RTCIceServer>? _iceServers;

    // 1 while a signaling reconnect loop is running, so a burst of "connection lost" events starts only one loop.
    private int _reconnecting;

    // e2e keys of the recent passkeys, used to verify each viewer's binding (memory only, never sent anywhere)
    private readonly E2EKeyRing _e2eKeys = new();

    // Passkey rotation fields
    private string? _currentPasskey;
    private DateTime _passkeyValidTo;
    private Task? _passkeyRotationTask;

    public ScreenSenderService(
        ILogger<ScreenSenderService> logger,
        ScreenSenderConfiguration configuration)
        : this(logger, configuration, new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
            ServerIdGenerator.DefaultIdPath, new ServerOwnerSecretStore())
    {
    }

    /// <summary>Same as the default constructor but with Server ID / owner secret kept at explicit paths (unattended agent: %ProgramData%).</summary>
    public ScreenSenderService(
        ILogger<ScreenSenderService> logger,
        ScreenSenderConfiguration configuration,
        string serverIdPath,
        ServerOwnerSecretStore ownerSecretStore)
        : this(logger, configuration, new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, serverIdPath, ownerSecretStore)
    {
    }

    /// <summary>Lets tests supply a fake HTTP handler and temp files instead of the real ones.</summary>
    internal ScreenSenderService(
        ILogger<ScreenSenderService> logger,
        ScreenSenderConfiguration configuration,
        HttpClient httpClient,
        string serverIdPath,
        ServerOwnerSecretStore ownerSecretStore)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient;
        _serverIdPath = serverIdPath;
        _ownerSecretStore = ownerSecretStore;
        VideoEncoderFactory.Preference = VideoEncoderFactory.ParsePreference(configuration.Codec);
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

    /// <summary>
    /// Raised when the first registration hit a Server ID collision and a new ID was picked.
    /// The UI shows the ID, so it must update it when this fires.
    /// </summary>
    public event EventHandler<string>? ServerIdChanged;

    /// <summary>Raised when a file from the viewer was received and verified (the UI shows a notice).</summary>
    public event EventHandler<ReceivedFile>? FileReceived;

    private readonly TaskCompletionSource _streaming = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when capture + WebRTC are up and frames are flowing; faults/cancels if the service stops before that.</summary>
    public Task Streaming => _streaming.Task;

    /// <summary>The running service task (faults when the service dies); completed task before StartAsync.</summary>
    public Task Execution => _executingTask ?? Task.CompletedTask;

    internal void SignalStreaming() => _streaming.TrySetResult();

    /// <summary>If the service ends before it ever streamed, <see cref="Streaming"/> must not stay pending forever.</summary>
    internal void WireStreamingSignal(Task execution) => execution.ContinueWith(t =>
    {
        if (t.IsFaulted) _streaming.TrySetException(t.Exception!.InnerExceptions);
        else _streaming.TrySetCanceled();
    }, TaskScheduler.Default);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting ScreenSenderService with Server ID: {ServerId}", ServerId);

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _executingTask = ExecuteAsync(_stoppingCts.Token);
        WireStreamingSignal(_executingTask);

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
        await GenerateNewPasskeyAsync(allowServerIdChange: true);

        // Start passkey rotation task
        _passkeyRotationTask = Task.Run(async () => await PasskeyRotationLoopAsync(stoppingToken), stoppingToken);

        try
        {
            // Get screen resolution
            int width, height;
            using (var tempCapturer = PlatformServiceFactory.CreateScreenCapturer(_configuration.AdapterIndex, _configuration.OutputIndex, _configuration.TargetFps, drawPointer: _pointerDrawn))
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

            // The configured output is the default; a viewer with remote control may switch to another one.
            var displays = DisplayCatalog.Enumerate(width, height);
            var currentDisplay = DisplayCatalog.FindConfigured(displays, _configuration.AdapterIndex, _configuration.OutputIndex);

            // The session (and its encoders) must be created with the size we actually send, not the screen size.
            var (outputWidth, outputHeight) = OutputSize.Compute(width, height, _configuration.MaxOutputWidth);
            _logger.LogInformation("Output resolution: {Width}x{Height} (MaxOutputWidth={Max})", outputWidth, outputHeight, _configuration.MaxOutputWidth);

            // Create WebRTC session - use WebRTCSessionWithInput if remote control is enabled
            IVideoSource videoSource;
            bool initialized;

            // File receiving saves into the profile of the process user; as LocalSystem that would be the SYSTEM
            // profile, not the logged-in user's Downloads. Impersonation is a later task, so refuse it for now.
            var enableFileReceive = _configuration.EnableFileReceive;
            if (enableFileReceive && OperatingSystem.IsWindows() && System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
            {
                _logger.LogWarning("File receiving is disabled: the sender runs as LocalSystem and cannot save into a user's folder yet.");
                enableFileReceive = false;
            }

            // The data channel session is always used, even for a view-only host: the end-to-end binding
            // (E2EBinding) runs over the data channel, so a session without one could not verify its viewer.
            // Input, clipboard and files stay unauthorized unless the host enabled them.
            {
                _logger.LogInformation("Creating WebRTC session WITH data channel (remote control: {Control}, clipboard: {Clipboard})", _configuration.EnableRemoteControl, _configuration.EnableClipboard);

                // macOS ignores injected input until Accessibility is granted; asking now shows the system prompt.
                if (_configuration.EnableRemoteControl && OperatingSystem.IsMacOS() && !Platforms.macOS.MacAccessibility.IsTrustedPromptingUser())
                {
                    _logger.LogWarning("Accessibility permission is not granted yet: remote control will not work until the app is enabled in System Settings > Privacy & Security > Accessibility, then restart sharing.");
                }

                var clipboardManager = _configuration.EnableClipboard ? PlatformServiceFactory.CreateClipboardManager() : null;

                // Create input controller for remote control. Nothing that can throw runs between this and the
                // session that takes ownership of it (the finally block below disposes the session).
                var serilogLogger = Serilog.Log.ForContext<IInputController>();
                var inputController = PlatformServiceFactory.CreateInputController(serilogLogger);

                // Create session with input support
                var serilogSessionLogger = Serilog.Log.ForContext<WebRTCSessionWithInput>();
                _webrtcSessionWithInput = new WebRTCSessionWithInput(serilogSessionLogger, inputController, clipboardManager);
                _webrtcSessionWithInput.ClipboardSharingEnabled = _configuration.EnableClipboard;
                if (enableFileReceive)
                {
                    var folder = string.IsNullOrWhiteSpace(_configuration.FileReceiveFolder)
                        ? ScreenSenderConfiguration.DefaultFileReceiveFolder
                        : _configuration.FileReceiveFolder;
                    _webrtcSessionWithInput.EnableFileReceive(folder, _configuration.FileReceiveMaxBytes);
                    _webrtcSessionWithInput.FileReceived += (sender, file) =>
                    {
                        _logger.LogInformation("Received {Name} ({Size} bytes) from viewer into {Folder}", file.SavedAs, file.SizeBytes, Path.GetDirectoryName(file.FullPath));
                        FileReceived?.Invoke(this, file);
                    };
                    _logger.LogInformation("File receiving enabled, folder: {Folder}, max {Max} bytes", folder, _configuration.FileReceiveMaxBytes);
                }
                if (_configuration.EnablePrivacyScreen)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        _webrtcSessionWithInput.EnablePrivacyScreen(new Platforms.Windows.PrivacyOverlay((inputController as Platforms.Windows.WindowsInputController) is { } wic ? wic.SetLocalInputBlocked : null), _configuration.PrivacyScreenBlocksInput);
                        _logger.LogInformation("Privacy screen enabled (block local input: {Block})", _configuration.PrivacyScreenBlocksInput);
                    }
                    else
                    {
                        _logger.LogWarning("Privacy screen is only available on Windows; not granted.");
                    }
                }
                ApplyIceServers(_webrtcSessionWithInput.VideoSource);
                _webrtcSessionWithInput.EnableE2EBinding(_e2eKeys);
                _safety = new SessionSafety(_webrtcSessionWithInput, _configuration, AuditLog.Default, _logger, ControlApprovalPrompt, ViewerAdmission);

                inputController.SetCapturedDisplay(InputRectFor(displays[currentDisplay]));
                _webrtcSessionWithInput.DataChannel.DisplaySwitcher = new DisplaySwitchService(displays, currentDisplay, async (display, stillAllowed) =>
                {
                    if (_pipeline == null)
                        return false;
                    var next = PlatformServiceFactory.CreateScreenCapturer(display.AdapterIndex, display.OutputIndex, _configuration.TargetFps, drawPointer: _pointerDrawn);
                    // Pointer input is mapped to the new display only once the viewer is actually receiving its picture,
                    // otherwise clicks would hit the new monitor while the viewer still sees the old one.
                    var switched = await _pipeline.SwitchCapturerAsync(next, stillAllowed, () =>
                    {
                        inputController.SetCapturedDisplay(InputRectFor(display));
                        _captureTarget = (display.AdapterIndex, display.OutputIndex);
                        _logger.LogInformation("Now sharing {Display}", display.Name);
                        _safety?.DisplaySwitched(display.Name);
                    });
                    return switched;
                });

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
                    outputWidth,
                    outputHeight,
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
                if (_configuration.EnableRemoteControl)
                {
                    inputController.AuthorizationRequested = _ => true;
                    // With RequireControlApproval the checkbox only means "viewers may ask": control is authorized later, per session.
                    if (!_configuration.RequireControlApproval)
                    {
                        _logger.LogInformation("Auto-authorizing remote control (user enabled via checkbox)");
                        await inputController.RequestAuthorizationAsync("auto-authorized", stoppingToken);
                    }
                }

                videoSource = _webrtcSessionWithInput.VideoSource;
                _logger.LogInformation("WebRTC session with input initialized. Client ID: {ClientId}", _webrtcSessionWithInput.ClientId);
                if (!_webrtcSessionWithInput.IsSignalingConnected)
                {
                    OnSignalingConnectionLost(); // server was unreachable at startup: retry in the background
                }
            }

            // Start capture pipeline with WebRTC video source
            var capturer = PlatformServiceFactory.CreateScreenCapturer(_configuration.AdapterIndex, _configuration.OutputIndex, _configuration.TargetFps, drawPointer: _pointerDrawn);
            _captureTarget = (_configuration.AdapterIndex, _configuration.OutputIndex);
            var converter = new SimdPixelConverter(); // Use SIMD-optimized converter

            _pipeline = new CapturePipeline(capturer, converter, videoSource, _configuration.MaxOutputWidth);
            _logger.LogInformation("Capture pipeline initialized with SIMD converter");

            if (_pipeline.Start(_configuration.TargetFps))
            {
                _logger.LogInformation("Capture pipeline started successfully");
                _logger.LogInformation("ScreenSenderService is now streaming. Waiting for clients to connect...");
                if (_webrtcSessionWithInput != null && PlatformServiceFactory.CapturerDrawsPointer)
                    _ = Task.Run(() => PointerVisibilityLoopAsync(_webrtcSessionWithInput.DataChannel, stoppingToken), stoppingToken);
                SignalStreaming();

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

            _safety?.Dispose();
            _safety = null;
            _webrtcSessionWithInput?.Dispose();
            _webrtcSessionWithInput = null;

            IsRunning = false;
        }
    }

    // Swaps the capturer for one with or without the drawn pointer when a viewer starts or stops controlling (x11grab
    // and avfoundation cannot change it on a running capture). A swap the pipeline refuses (another one in progress)
    // is simply retried on the next tick.
    private async Task PointerVisibilityLoopAsync(DataChannelManager dataChannel, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                var controlling = Environment.TickCount64 - dataChannel.LastInputTicks < PointerHideAfterInput.TotalMilliseconds;
                var draw = !controlling;
                if (draw == _pointerDrawn || _pipeline is not { } pipeline || _captureTarget is not { } target)
                    continue;
                var next = PlatformServiceFactory.CreateScreenCapturer(target.Adapter, target.Output, _configuration.TargetFps, drawPointer: draw);
                if (await pipeline.SwitchCapturerAsync(next))
                {
                    _pointerDrawn = draw;
                    _logger.LogInformation("Host pointer {State} the picture", draw ? "drawn into" : "left out of");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pointer visibility loop stopped");
        }
    }

    // The primary display is what SendInput targets by default; only another display needs an explicit rectangle.
    private static ScreenRect? InputRectFor(DisplayInfo display) =>
        display.Primary ? null : new ScreenRect(display.Left, display.Top, display.Width, display.Height);

    /// <summary>
    /// Generates a new passkey and updates expiration time.
    /// Raises PasskeyChanged event for UI update and registers with SignalingServer.
    /// </summary>
    internal async Task GenerateNewPasskeyAsync(bool allowServerIdChange = false)
    {
        var now = DateTime.UtcNow;
        _currentPasskey = AuthenticationService.GeneratePasskey();
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
        await RegisterWithSignalingServerAsync(allowServerIdChange);
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
            var stillConnected = _webrtcSessionWithInput?.IsSignalingConnected ?? true;
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

    // Logged once, not on every 45 s re-registration.
    private bool _permanentKeyWarned;

    /// <summary>
    /// Keys of the permanent password, or null when none is configured. Read on every registration (a tiny file), so
    /// a Server ID change after a collision is noticed: the keys are bound to the old ID and stop being offered.
    /// Its e2e key joins the key ring for good, so a viewer that typed the password passes the binding check.
    /// </summary>
    private (string AuthKey, byte[] E2EKey)? LoadPermanentKeys()
    {
        var path = _configuration.PermanentPasswordKeyFile;
        if (string.IsNullOrEmpty(path)) return null;

        var keys = PermanentKeyFile.Load(path, ServerId);
        if (keys is { } k)
            _e2eKeys.AddPermanent(k.E2EKey);
        else if (File.Exists(path) && !_permanentKeyWarned)
        {
            _permanentKeyWarned = true;
            _logger.LogWarning("Permanent password file is unreadable or was set for another Server ID; set the password again | ServerId: {ServerId}", ServerId);
        }
        return keys;
    }

    /// <summary>
    /// Registers this server with the SignalingServer so clients can discover and authenticate.
    /// </summary>
    /// <param name="allowServerIdChange">
    /// True only for the first registration: the WebRTC session is created afterwards with the final ID.
    /// Later registrations keep the ID, because the running session is already bound to it.
    /// </param>
    private async Task RegisterWithSignalingServerAsync(bool allowServerIdChange = false)
    {
        // One registration at a time. The request is built from the current passkey only after the lock is held,
        // so whichever call runs last always sends the newest passkey.
        await _registerLock.WaitAsync();
        try
        {
            await RegisterCoreAsync(allowServerIdChange);
        }
        finally
        {
            _registerLock.Release();
        }
    }

    private async Task RegisterCoreAsync(bool allowServerIdChange)
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
                .Replace("wss://", "https://");
            // Strip only the trailing path: a blanket Replace("/signal", "") also mangles hosts such as
            // "signaling" or "signal.example.com" ("//signaling" contains "/signal").
            if (signalingHttpUrl.EndsWith("/signal", StringComparison.Ordinal))
                signalingHttpUrl = signalingHttpUrl[..^"/signal".Length];

            var registerUrl = $"{signalingHttpUrl}/register";

            _logger.LogDebug("Registering with SignalingServer: {Url}", registerUrl);

            HttpResponseMessage response;
            for (var attempt = 1; ; attempt++)
            {
                // The server never sees the passkey: it gets authKey (it only compares strings), while e2eKey stays
                // here for the end-to-end check of the viewer (see E2EBinding). Derived per attempt because the
                // ServerId is part of the salt and can change on a 403.
                var keys = E2EBinding.DeriveKeys(_currentPasskey, ServerId);
                _e2eKeys.Add(keys.E2EKey, _passkeyValidTo);
                var permanent = LoadPermanentKeys();

                var registration = new ServerRegistrationMessage
                {
                    ServerId = ServerId,
                    Passkey = keys.AuthKey,
                    ValidTo = _passkeyValidTo,
                    RemoteControlEnabled = _configuration.EnableRemoteControl,
                    TrustClientPermanent = _configuration.TrustClientPermanent,
                    OwnerSecret = GetOwnerSecret(),
                    PermanentAuthKey = permanent?.AuthKey
                };

                response = await _httpClient.PostAsJsonAsync(registerUrl, registration);

                // 403 means another installation already owns this ServerId: pick and persist a new one.
                if (response.StatusCode != System.Net.HttpStatusCode.Forbidden ||
                    !allowServerIdChange || attempt >= MaxServerIdAttempts)
                    break;

                _configuration.ServerId = ServerIdGenerator.RegenerateServerId(_serverIdPath);
                _logger.LogWarning("Server ID was already taken, using a new one | ServerId: {ServerId}", ServerId);
                ServerIdChanged?.Invoke(this, ServerId);
            }

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
                            ApplyIceServers(_webrtcSessionWithInput?.VideoSource);
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
    /// Maximum width of the video sent to viewers. Larger screens are downscaled (aspect ratio kept),
    /// smaller ones are never upscaled. 0 = send the native screen resolution (the default: on a fast PC the
    /// scaling costs about what the encoder saves, so a cap only pays off on slow CPUs or slow links).
    /// </summary>
    public int MaxOutputWidth { get; set; } = 0;

    /// <summary>
    /// Video codec: "vp8" (default, software), "h264" (hardware H.264 when a GPU encoder initialises, VP8 as
    /// fallback) or "auto" (same, but a missing GPU encoder is not a warning).
    /// </summary>
    public string Codec { get; set; } = "vp8";

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
    /// With <see cref="EnableRemoteControl"/>: the person at the computer must approve control for every session
    /// (a "viewer wants to control your computer" question, auto-deny after 30 s). Then <see cref="EnableRemoteControl"/>
    /// only means "viewers may ask". The attended desktop app turns this on; the headless default is off.
    /// </summary>
    public bool RequireControlApproval { get; set; } = false;

    /// <summary>Show the "remote session" bar while a viewer is connected (Windows only; other systems skip it).</summary>
    public bool ShowSessionBanner { get; set; } = true;

    /// <summary>
    /// Whether the connected viewer may exchange text clipboard content with this machine (both directions).
    /// Default is false: the clipboard often holds passwords, so the host must opt in.
    /// </summary>
    public bool EnableClipboard { get; set; } = false;

    /// <summary>
    /// Whether the connected viewer may send files to this machine (viewer to host only).
    /// Default is false: saving files from a remote peer is something the host must opt in to.
    /// </summary>
    public bool EnableFileReceive { get; set; } = false;

    /// <summary>
    /// Whether the viewer may black out this machine's local monitors while it keeps seeing the desktop (Windows only).
    /// Default is false: it hides what the local user sees, so the host must opt in.
    /// </summary>
    public bool EnablePrivacyScreen { get; set; } = false;

    /// <summary>
    /// With the privacy screen on, also block the local mouse and keyboard (BlockInput; needs an elevated process,
    /// and Ctrl+Alt+Del always gives the local user control back).
    /// </summary>
    public bool PrivacyScreenBlocksInput { get; set; } = false;

    /// <summary>Where received files are saved. Empty means <see cref="DefaultFileReceiveFolder"/>.</summary>
    public string FileReceiveFolder { get; set; } = string.Empty;

    /// <summary>Largest accepted file in bytes (default 2 GB).</summary>
    public long FileReceiveMaxBytes { get; set; } = FileReceiver.DefaultMaxBytes;

    /// <summary>"Downloads\DeskShare" in the profile of the user running the sender.</summary>
    public static string DefaultFileReceiveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DeskShare");

    /// <summary>
    /// Whether to trust the connected client permanently (no passkey required for reconnection).
    /// Default is false for security reasons - must be explicitly enabled by user.
    /// </summary>
    public bool TrustClientPermanent { get; set; } = false;

    /// <summary>
    /// Path of a <see cref="PermanentKeyFile"/>. When it holds keys for this Server ID, viewers may also connect with
    /// the permanent password instead of the current passkey. Empty = passkey only.
    /// </summary>
    public string PermanentPasswordKeyFile { get; set; } = string.Empty;

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
