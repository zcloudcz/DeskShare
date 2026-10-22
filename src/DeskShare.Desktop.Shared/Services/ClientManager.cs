using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeskShare.Core.Auth;
using DeskShare.Core.Models;
using DeskShare.Core.WebRTC;
using DeskShare.Core.Interfaces;
using DeskShare.Desktop.Shared.Models;
using SIPSorcery.Net;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Manages WebRTC client connection to remote server.
/// Platform-independent: uses IClipboardManager injection instead of
/// creating platform-specific clipboard managers directly.
/// Shared between WPF and Avalonia desktop clients.
/// </summary>
public class ClientManager : IDisposable
{
    private readonly ILogger<ClientManager> _logger;
    private readonly ILogger<VideoSink> _videoSinkLogger;
    private readonly IConfiguration _configuration;
    private ClientWebSocket? _signalingWebSocket;
    private RTCPeerConnection? _peerConnection;
    private VideoSink? _videoSink;
    private RTCDataChannel? _dataChannel;
    private IClipboardManager? _clipboardManager;
    private ClipboardSync? _clipboardSync;
    private bool _shareClipboard;
    private bool _isConnected;
    private string? _serverId;
    private string? _clientId;
    private string? _password;
    private string? _signalingUrl;
    private IReadOnlyList<IceServerInfo>? _iceServers;

    // Single-use token for POST /resume; each response carries the next one. Lets a reconnect skip the passkey,
    // which rotates every 45 s and is long stale by the time we need to reconnect.
    private string? _resumeToken;
    private static readonly HttpClient ResumeHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    // End-to-end binding (see E2EBinding). The key comes from the passkey the user typed and stays in memory for
    // the whole session, so a re-negotiated peer connection (reconnect, resume) can prove itself again.
    private byte[]? _e2eKey;
    private volatile bool _peerVerified;
    private volatile bool _bindSent;
    private string? _disconnectReason;

    /// <summary>Shown (and raised via <see cref="Disconnected"/>) when the end-to-end check fails.</summary>
    public const string BindFailedMessage = "Secure connection check failed. The connection may be intercepted. Do not continue.";

    private CancellationTokenSource? _receiveCts;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 5;
    private bool _isReconnecting;

    public bool IsConnected => _isConnected;
    public string? ServerId => _serverId;

    /// <summary>
    /// Fired when a decoded video frame is received (BGRA32 byte array).
    /// </summary>
    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;

    /// <summary>
    /// Fired when connection statistics are updated.
    /// </summary>
    public event EventHandler<ConnectionStats>? StatsUpdated;

    /// <summary>
    /// Fired when WebRTC connection is established.
    /// </summary>
    public event EventHandler? Connected;

    /// <summary>
    /// Fired when disconnected (with reason string).
    /// </summary>
    public event EventHandler<string>? Disconnected;

    /// <summary>
    /// Fired when clipboard data is received from server.
    /// </summary>
    public event EventHandler<ClipboardMessage>? ClipboardReceived;

    /// <summary>
    /// Whether the host allows clipboard sharing (told by the host in its authorization_response).
    /// </summary>
    public bool HostAllowsClipboard { get; private set; }

    /// <summary>
    /// Fired when <see cref="HostAllowsClipboard"/> changes.
    /// </summary>
    public event EventHandler<bool>? HostAllowsClipboardChanged;

    /// <summary>
    /// Whether the host accepts files from this viewer (told by the host in its authorization_response).
    /// </summary>
    public bool HostAllowsFiles { get; private set; }

    /// <summary>
    /// Fired when <see cref="HostAllowsFiles"/> changes.
    /// </summary>
    public event EventHandler<bool>? HostAllowsFilesChanged;

    private FileUploader? _fileUploader;

    /// <summary>
    /// Sends a file to the host (viewer to host only). Returns the name it was saved under there.
    /// Throws <see cref="IOException"/> with a readable reason when the host refuses or fails the transfer.
    /// The UI only offers this while <see cref="HostAllowsFiles"/>; the host enforces the grant on its side anyway.
    /// </summary>
    public Task<string> SendFileAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default, string? offeredName = null)
    {
        if (_fileUploader == null)
        {
            throw new InvalidOperationException("Not connected to a host.");
        }

        return _fileUploader.SendAsync(path, progress, cancellationToken, offeredName);
    }

    /// <summary>
    /// Fired with a user-visible reason when clipboard text was refused (for example too large).
    /// </summary>
    public event EventHandler<string>? ClipboardRejected;

    /// <summary>
    /// The viewer's own opt-in ("Share my clipboard with the host"). Default off. The clipboard is only
    /// exchanged when this is on AND the host allowed it.
    /// </summary>
    public bool ShareClipboard
    {
        get => _shareClipboard;
        set
        {
            _shareClipboard = value;
            UpdateClipboardEnabled();
            SendClipboardSubscription();
        }
    }

    // Tells the host whether this viewer wants its clipboard; the host sends nothing until we subscribed.
    private void SendClipboardSubscription()
    {
        if (!HostAllowsClipboard || _dataChannel?.readyState != RTCDataChannelState.open)
        {
            return;
        }

        _dataChannel.send($"{{\"type\":\"clipboard_subscribe\",\"enabled\":{(_shareClipboard ? "true" : "false")}}}");
    }

    private void UpdateClipboardEnabled()
    {
        if (_clipboardSync != null)
        {
            _clipboardSync.Enabled = _shareClipboard && HostAllowsClipboard;
        }
    }

    /// <summary>
    /// Creates a new ClientManager with injected dependencies.
    /// The IClipboardManager is injected via DI so each platform can provide
    /// its own implementation (AvaloniaClipboardManager or WindowsClipboardManager).
    /// </summary>
    public ClientManager(
        ILogger<ClientManager> logger,
        ILogger<VideoSink> videoSinkLogger,
        IConfiguration configuration,
        IClipboardManager clipboardManager)
    {
        _logger = logger;
        _videoSinkLogger = videoSinkLogger;
        _configuration = configuration;

        // IClipboardManager is now injected instead of hardcoding a platform-specific type
        _clipboardManager = clipboardManager;

        // Sends/applies nothing until the host allowed it and the viewer opted in (see UpdateClipboardEnabled).
        _clipboardSync = new ClipboardSync(_clipboardManager, SendDataChannelJson);
        _clipboardSync.Rejected += (_, reason) => ClipboardRejected?.Invoke(this, reason);
        _clipboardSync.Applied += (_, message) => ClipboardReceived?.Invoke(this, message);
    }

    private bool SendDataChannelJson(string json)
    {
        if (!_isConnected || _dataChannel?.readyState != RTCDataChannelState.open)
        {
            return false;
        }

        _dataChannel.send(json);
        _logger.LogDebug("Sent clipboard message: Size={Size} bytes", Encoding.UTF8.GetByteCount(json));
        return true;
    }

    /// <summary>
    /// Connects to a remote server via signaling WebSocket and WebRTC.
    /// </summary>
    /// <param name="webSocketToken">One-time token from /authenticate. Hosted servers reject WebSockets without it.</param>
    /// <param name="clientId">The ClientId sent to /authenticate; the token is bound to it, so we must use the same one.</param>
    /// <param name="iceServers">STUN/TURN servers from /authenticate; falls back to public STUN when null.</param>
    /// <param name="resumeToken">Resume token from /authenticate; used by reconnects instead of the passkey.</param>
    /// <param name="e2eKey">Key from <see cref="E2EBinding.DeriveKeys"/> for the passkey the user typed. Without it the
    /// sender cannot be verified and the connection is refused (video stays hidden, then it is dropped).</param>
    public async Task ConnectAsync(
        string serverId,
        string? password,
        string? signalingUrl = null,
        string? webSocketToken = null,
        string? clientId = null,
        IReadOnlyList<IceServerInfo>? iceServers = null,
        string? resumeToken = null,
        byte[]? e2eKey = null)
    {
        if (_isConnected)
        {
            throw new InvalidOperationException("Already connected");
        }

        signalingUrl ??= _configuration.GetValue<string>("Client:SignalingServerUrl");

        if (string.IsNullOrEmpty(signalingUrl))
        {
            throw new InvalidOperationException(
                "SignalingServerUrl is not configured. Please set 'Client:SignalingServerUrl' in appsettings.json");
        }

        _logger.LogInformation("Connecting to server {ServerId} via {SignalingUrl}", serverId, signalingUrl);
        _serverId = serverId;
        _password = password;
        _signalingUrl = signalingUrl;
        _iceServers = iceServers;
        _resumeToken = resumeToken;
        _e2eKey = e2eKey;
        _disconnectReason = null;

        try
        {
            // Connect to signaling server with timeout
            _signalingWebSocket = new ClientWebSocket();
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                // The token is single-use, so it is only appended for this first connect.
                var connectUrl = string.IsNullOrEmpty(webSocketToken)
                    ? signalingUrl
                    : $"{signalingUrl}{(signalingUrl.Contains('?') ? "&" : "?")}token={Uri.EscapeDataString(webSocketToken)}";
                await _signalingWebSocket.ConnectAsync(new Uri(connectUrl), connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Connection to signaling server timed out after 10 seconds");
            }

            _logger.LogInformation("Connected to signaling server");

            _clientId = clientId ?? Guid.NewGuid().ToString();

            // The peer connection must exist before we ask for an offer and before the receive loop runs:
            // an offer that arrives earlier is dropped ("peer connection not initialized") and never answered.
            // Setup (FFmpeg init) can take longer than the sender needs to build its offer, e.g. on first start.
            // The reconnect path below uses the same order.
            await SetupWebRtcConnectionAsync();

            // Send connection request
            var joinMessage = new SignalingMessage
            {
                Type = SignalingMessageType.ConnectionRequest,
                SenderId = _clientId,
                TargetId = serverId
            };

            await SendSignalingMessageAsync(joinMessage);

            // Start receiving messages
            _receiveCts = new CancellationTokenSource();
            _ = ReceiveSignalingMessagesAsync(_receiveCts.Token);

            _isConnected = true;
            Connected?.Invoke(this, EventArgs.Empty);

            _logger.LogInformation("Successfully connected to server {ServerId}", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to server {ServerId}", serverId);
            await DisconnectAsync();
            throw;
        }
    }

    private async Task SetupWebRtcConnectionAsync()
    {
        _logger.LogInformation("Setting up WebRTC peer connection...");

        var mappedIceServers = IceServerMapper.ToRtcIceServers(_iceServers);
        var config = new RTCConfiguration
        {
            iceServers = mappedIceServers.Count > 0 ? mappedIceServers : IceServerMapper.DefaultIceServers()
        };

        _peerConnection = new RTCPeerConnection(config);
        _peerVerified = false; // every new peer connection must pass the end-to-end check again
        _bindSent = false;

        // Handle ICE candidates
        _peerConnection.onicecandidate += async (candidate) =>
        {
            if (candidate != null)
            {
                _logger.LogDebug("ICE candidate generated");
                var msg = new SignalingMessage
                {
                    Type = SignalingMessageType.IceCandidate,
                    SenderId = _clientId,
                    TargetId = _serverId,
                    Candidate = candidate.ToString(),
                    SdpMLineIndex = candidate.sdpMLineIndex,
                    SdpMid = candidate.sdpMid
                };
                // SIPSorcery calls this handler as async void: an exception here (e.g. the socket closed while
                // the viewer disconnects) would crash the whole process, so log it instead. A lost candidate
                // only matters while connecting, and then the connection state handler reports the failure.
                try
                {
                    await SendSignalingMessageAsync(msg);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not send ICE candidate");
                }
            }
        };

        // Handle connection state changes
        var thisPeerConnection = _peerConnection; // events of a replaced peer connection must not fail or verify the new one
        _peerConnection.onconnectionstatechange += async (state) =>
        {
            _logger.LogInformation("WebRTC connection state: {State}", state);

            // The sender drops a viewer whose binding does not verify. Losing the connection after we sent our
            // binding but before the sender's arrived is treated as a failed check, not as a network glitch to retry.
            if (ReferenceEquals(thisPeerConnection, _peerConnection) && _bindSent && !_peerVerified && state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
            {
                await FailBindingAsync();
                return;
            }

            if (state == RTCPeerConnectionState.connected)
            {
                _logger.LogInformation("WebRTC peer connection established");
                _reconnectAttempts = 0;
                StartBindDeadline(_peerConnection);
            }
            else if (state == RTCPeerConnectionState.disconnected)
            {
                _logger.LogWarning("WebRTC connection lost, attempting to reconnect...");
                await TryReconnectAsync();
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                _logger.LogError("WebRTC connection failed");
                await TryReconnectAsync();
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                _logger.LogInformation("WebRTC connection closed");
                if (!_isReconnecting)
                {
                    Disconnected?.Invoke(this, "Connection closed");
                }
            }
        };

        // Setup video sink to receive video frames
        _videoSink = new VideoSink(_videoSinkLogger);
        _videoSink.FrameReceived += (sender, args) =>
        {
            // Nothing is shown until the sender proved (e2e binding) that nobody sits in the middle.
            if (!_peerVerified) return;

            _logger.LogDebug("[ClientManager] VideoSink.FrameReceived event fired - frame size: {Size} bytes", args.FrameData.Length);
            FrameReceived?.Invoke(this, args);

            if (FrameReceived == null)
            {
                _logger.LogWarning("[ClientManager] FrameReceived event has no subscribers!");
            }
        };

        await _videoSink.StartVideoSink();
        _logger.LogInformation("Video sink started and ready to decode frames");

        // Add video track as receive-only
        var videoTrack = new MediaStreamTrack(
            _videoSink.GetVideoSinkFormats(),
            MediaStreamStatusEnum.RecvOnly);
        _peerConnection.addTrack(videoTrack);

        // Connect peer connection's video frame receiver to our video sink
        _peerConnection.OnVideoFrameReceived += _videoSink.GotVideoFrame;
        _logger.LogInformation("Video sink connected to peer connection video frame events");

        // Setup data channel for remote control input and clipboard
        _dataChannel = await _peerConnection.createDataChannel("input", new RTCDataChannelInit());
        var thisDataChannel = _dataChannel;
        _dataChannel.onopen += () =>
        {
            if (!ReferenceEquals(thisDataChannel, _dataChannel)) return; // channel of a replaced connection
            _logger.LogInformation("Data channel opened");
            _clipboardManager?.StartMonitoring();

            _fileUploader = new FileUploader(
                json => { if (_dataChannel?.readyState != RTCDataChannelState.open) return false; _dataChannel.send(json); return true; },
                () => _dataChannel?.bufferedAmount ?? 0);

            // First message on the channel is our e2e binding; the authorization_request follows once the
            // sender's binding verified (see OnDataChannelMessage). Nothing else is sent before that.
            if (!TrySendBind())
            {
                _ = FailBindingAsync();
            }
        };
        _dataChannel.onclose += () =>
        {
            _logger.LogInformation("Data channel closed");
            _clipboardManager?.StopMonitoring();
            SetHostAllowsClipboard(false);
            SetHostAllowsFiles(false);
        };
        _dataChannel.onerror += (error) =>
        {
            _logger.LogError("Data channel error: {Error}", error);
        };
        _dataChannel.onmessage += OnDataChannelMessage;

        _logger.LogInformation("WebRTC peer connection configured with video sink and data channel");

        // Start stats monitoring
        StartStatsMonitoring();

        _logger.LogInformation("WebRTC setup complete, waiting for offer from server");
    }

    private void OnDataChannelMessage(RTCDataChannel channel, DataChannelPayloadProtocols protocol, byte[] data)
    {
        // The sender's binding is verified against the CURRENT peer connection's fingerprints, so a message from a
        // channel of a replaced connection must never reach that check.
        if (!ReferenceEquals(channel, _dataChannel)) return;

        try
        {
            var json = Encoding.UTF8.GetString(data);
            _logger.LogDebug("Received data channel message: {Size} bytes", data.Length);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeElement))
            {
                var messageType = typeElement.GetString();

                if (messageType == "e2e_bind")
                {
                    if (!_peerVerified && !VerifySenderBind(doc.RootElement))
                    {
                        _ = FailBindingAsync();
                    }
                    return;
                }

                // Until the sender's binding verified, whatever else arrives is ignored (it could come from a man in the middle).
                if (!_peerVerified)
                {
                    return;
                }

                if (messageType == "authorization_response")
                {
                    SetHostAllowsClipboard(doc.RootElement.TryGetProperty("clipboard", out var clip) && clip.ValueKind == JsonValueKind.True);
                    SetHostAllowsFiles(doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.True);
                }
                else if (messageType is "file_accept" or "file_reject" or "file_done" or "file_error" or "file_cancel")
                {
                    _fileUploader?.OnHostMessage(doc.RootElement);
                }
                else if (messageType == "clipboard" && doc.RootElement.TryGetProperty("data", out var dataElement))
                {
                    var message = JsonSerializer.Deserialize<ClipboardMessage>(dataElement.GetRawText());
                    if (message != null)
                    {
                        _logger.LogInformation("Received clipboard message from host: Size={Size} bytes", message.SizeBytes);
                        _ = ApplyHostClipboardAsync(message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing data channel message");
        }
    }

    // ---- End-to-end binding (see E2EBinding) ----

    /// <summary>Fingerprints as this side sees them: (sender = remote description, viewer = local description).</summary>
    private (string? SenderFp, string? ViewerFp) GetFingerprints() =>
        (E2EBinding.GetFingerprint(_peerConnection?.remoteDescription?.sdp?.ToString()),
         E2EBinding.GetFingerprint(_peerConnection?.localDescription?.sdp?.ToString()));

    private bool TrySendBind()
    {
        var (senderFp, viewerFp) = GetFingerprints();
        if (_e2eKey == null || senderFp == null || viewerFp == null || _dataChannel?.readyState != RTCDataChannelState.open)
        {
            return false;
        }

        _dataChannel.send(E2EBinding.BuildBindMessage(E2EBinding.ViewerRole,
            E2EBinding.ComputeMac(_e2eKey, E2EBinding.ViewerRole, senderFp, viewerFp)));
        _bindSent = true;
        return true;
    }

    /// <summary>Checks the sender's binding; on success releases video and asks the host what it allows.</summary>
    private bool VerifySenderBind(JsonElement message)
    {
        var (senderFp, viewerFp) = GetFingerprints();
        if (_e2eKey == null || senderFp == null || viewerFp == null
            || !E2EBinding.TryReadBind(message, out var role, out var mac) || role != E2EBinding.SenderRole
            || !E2EBinding.VerifyMac(_e2eKey, E2EBinding.SenderRole, senderFp, viewerFp, mac))
        {
            return false;
        }

        _peerVerified = true;
        _logger.LogInformation("Sender passed the end-to-end binding check");

        // The host answers with authorization_response, which also says whether it shares the clipboard.
        _dataChannel?.send("{\"type\":\"authorization_request\"}");
        return true;
    }

    /// <summary>Drops the connection if the sender has not verified within <see cref="E2EBinding.Timeout"/>.</summary>
    private void StartBindDeadline(RTCPeerConnection? peerConnection)
    {
        if (_peerVerified) return;

        _ = Task.Delay(E2EBinding.Timeout).ContinueWith(_ =>
        {
            // Only the peer connection that was waiting counts, not a newer one created by a reconnect.
            if (!_peerVerified && ReferenceEquals(peerConnection, _peerConnection))
            {
                return FailBindingAsync();
            }
            return Task.CompletedTask;
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task FailBindingAsync()
    {
        // Only the fact is logged, never keys or fingerprints.
        _logger.LogWarning("End-to-end check failed: the sender could not be verified. The connection may be intercepted.");
        _bindSent = false;
        _disconnectReason = BindFailedMessage;
        try
        {
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            // Called from async-void event handlers and timers: an exception here would take the process down.
            _logger.LogWarning(ex, "Disconnecting after a failed end-to-end check failed");
        }
    }

    private void SetHostAllowsFiles(bool allowed)
    {
        if (HostAllowsFiles == allowed)
        {
            return;
        }

        HostAllowsFiles = allowed;
        HostAllowsFilesChanged?.Invoke(this, allowed);
    }

    private void SetHostAllowsClipboard(bool allowed)
    {
        if (HostAllowsClipboard == allowed)
        {
            return;
        }

        HostAllowsClipboard = allowed;
        UpdateClipboardEnabled();
        if (allowed)
        {
            SendClipboardSubscription(); // (re)connect: state the current opt-in
        }
        HostAllowsClipboardChanged?.Invoke(this, allowed);
    }

    private async Task ApplyHostClipboardAsync(ClipboardMessage message)
    {
        try
        {
            await _clipboardSync!.ApplyAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying clipboard from host");
        }
    }

    private void StartStatsMonitoring()
    {
        var statsTimer = new System.Timers.Timer(1000);
        var lastBytesReceived = 0L;
        var lastStatsTime = DateTime.UtcNow;

        statsTimer.Elapsed += (s, e) =>
        {
            if (_peerConnection == null) return;

            try
            {
                var state = _peerConnection.connectionState;

                if (state == RTCPeerConnectionState.connected)
                {
                    var now = DateTime.UtcNow;
                    var timeDiff = (now - lastStatsTime).TotalSeconds;

                    long currentBytesReceived = 0;
                    int fps = 30;
                    string resolution = "1920x1080";

                    var bitrateKbps = 0;
                    if (timeDiff > 0)
                    {
                        var bytesDiff = currentBytesReceived - lastBytesReceived;
                        bitrateKbps = (int)((bytesDiff * 8) / (timeDiff * 1000));
                        lastBytesReceived = currentBytesReceived;
                    }

                    var latencyMs = 50;
                    var packetLoss = 0.0;

                    StatsUpdated?.Invoke(this, new ConnectionStats
                    {
                        Fps = fps,
                        LatencyMs = latencyMs,
                        BitrateKbps = bitrateKbps > 0 ? bitrateKbps : 2500,
                        PacketLoss = packetLoss,
                        Resolution = resolution
                    });

                    lastStatsTime = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get WebRTC stats");
            }
        };
        statsTimer.Start();
    }

    private async Task SendSignalingMessageAsync(SignalingMessage message)
    {
        if (_signalingWebSocket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Signaling WebSocket is not connected");
        }

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _signalingWebSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        _logger.LogDebug("Sent signaling message: {Type}", message.Type);
    }

    private async Task ReceiveSignalingMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (_signalingWebSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _signalingWebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Signaling server closed connection");
                    await OnSignalingLostAsync();
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var message = JsonSerializer.Deserialize<SignalingMessage>(json);

                if (message != null)
                {
                    await HandleSignalingMessageAsync(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Signaling message receiving cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving signaling messages");
            await OnSignalingLostAsync();
        }
    }

    /// <summary>
    /// Video and input run peer-to-peer (or via TURN), not through the signaling server, so losing the
    /// signaling socket (e.g. during a server deployment) must not end a running session. If the peer
    /// connection later fails, the existing TryReconnectAsync path resumes signaling.
    /// </summary>
    private async Task OnSignalingLostAsync()
    {
        if (_peerConnection?.connectionState == RTCPeerConnectionState.connected)
        {
            _logger.LogWarning("Signaling connection lost; peer-to-peer session continues");
            return;
        }

        await DisconnectAsync();
    }

    private async Task HandleSignalingMessageAsync(SignalingMessage message)
    {
        _logger.LogDebug("Received signaling message: {Type}", message.Type);

        switch (message.Type)
        {
            case SignalingMessageType.Offer:
                await HandleOfferAsync(message);
                break;

            case SignalingMessageType.Answer:
                await HandleAnswerAsync(message);
                break;

            case SignalingMessageType.IceCandidate:
                await HandleIceCandidateAsync(message);
                break;

            case SignalingMessageType.Error:
                _logger.LogError("Signaling error: {Error}", message.ErrorMessage);
                // "Client <sender> disconnected" also arrives when only the sender's signaling socket
                // blipped (it reconnects by itself); keep a session whose media is still connected.
                if (_peerConnection?.connectionState == RTCPeerConnectionState.connected &&
                    message.ErrorMessage?.Contains("disconnected", StringComparison.OrdinalIgnoreCase) == true)
                {
                    break;
                }
                await DisconnectAsync();
                break;

            default:
                _logger.LogWarning("Unknown signaling message type: {Type}", message.Type);
                break;
        }
    }

    private async Task HandleOfferAsync(SignalingMessage message)
    {
        _logger.LogInformation("Handling SDP offer from server");

        if (_peerConnection == null || string.IsNullOrEmpty(message.Sdp))
        {
            _logger.LogWarning("Cannot handle offer: peer connection not initialized or SDP empty");
            return;
        }

        try
        {
            var remoteDescription = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = message.Sdp
            };

            _peerConnection.setRemoteDescription(remoteDescription);
            _logger.LogInformation("Remote description (offer) set");

            var answer = _peerConnection.createAnswer();
            await _peerConnection.setLocalDescription(answer);

            _logger.LogInformation("Created answer, sending to server");

            var answerMsg = new SignalingMessage
            {
                Type = SignalingMessageType.Answer,
                SenderId = _clientId,
                TargetId = _serverId,
                Sdp = answer.sdp
            };

            await SendSignalingMessageAsync(answerMsg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle offer");
        }
    }

    private async Task HandleAnswerAsync(SignalingMessage message)
    {
        _logger.LogInformation("Handling SDP answer from server");

        if (_peerConnection == null || string.IsNullOrEmpty(message.Sdp))
        {
            _logger.LogWarning("Cannot handle answer: peer connection not initialized or SDP empty");
            return;
        }

        try
        {
            var remoteDescription = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = message.Sdp
            };

            _peerConnection.setRemoteDescription(remoteDescription);
            _logger.LogInformation("Remote description (answer) set - WebRTC negotiation complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle answer");
        }

        await Task.CompletedTask;
    }

    private async Task HandleIceCandidateAsync(SignalingMessage message)
    {
        _logger.LogInformation("Handling ICE candidate from server");

        if (_peerConnection == null || string.IsNullOrEmpty(message.Candidate))
        {
            _logger.LogWarning("Cannot handle ICE candidate: peer connection not initialized or candidate empty");
            return;
        }

        try
        {
            var candidateInit = new RTCIceCandidateInit
            {
                candidate = message.Candidate,
                sdpMLineIndex = (ushort)(message.SdpMLineIndex ?? 0),
                sdpMid = message.SdpMid
            };

            _peerConnection.addIceCandidate(candidateInit);
            _logger.LogDebug("ICE candidate added");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add ICE candidate");
        }

        await Task.CompletedTask;
    }

    private async Task TryReconnectAsync()
    {
        if (_isReconnecting || _reconnectAttempts >= MaxReconnectAttempts)
        {
            if (_reconnectAttempts >= MaxReconnectAttempts)
            {
                _logger.LogError("Max reconnect attempts reached, giving up");
                Disconnected?.Invoke(this, "Max reconnect attempts reached");
            }
            return;
        }

        _isReconnecting = true;
        _reconnectAttempts++;

        try
        {
            _logger.LogInformation("Reconnect attempt {Attempt}/{Max}", _reconnectAttempts, MaxReconnectAttempts);

            var delaySeconds = Math.Pow(2, _reconnectAttempts);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            if (_peerConnection != null)
            {
                _peerConnection.close();
                _peerConnection.Dispose();
                _peerConnection = null;
            }

            if (!string.IsNullOrEmpty(_serverId) && !string.IsNullOrEmpty(_signalingUrl))
            {
                // The WebSocket token from the first connect is spent, so signaling can only be reopened
                // with a fresh one from /resume. Without a resume token we keep the old signaling socket.
                var signalingResumed = await ResumeSignalingAsync();

                await SetupWebRtcConnectionAsync();

                if (signalingResumed)
                {
                    // Same order as ConnectAsync: listen first, then ask the sender for a new offer.
                    _receiveCts = new CancellationTokenSource();
                    _ = ReceiveSignalingMessagesAsync(_receiveCts.Token);
                    await SendSignalingMessageAsync(new SignalingMessage
                    {
                        Type = SignalingMessageType.ConnectionRequest,
                        SenderId = _clientId,
                        TargetId = _serverId
                    });
                }
                _logger.LogInformation("Reconnect successful");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnect attempt failed");

            if (_reconnectAttempts < MaxReconnectAttempts)
            {
                await TryReconnectAsync();
            }
            else
            {
                Disconnected?.Invoke(this, "Reconnection failed after max attempts");
            }
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    /// <summary>
    /// Trades the resume token for a new WebSocket token (POST /resume) and reopens the signaling socket with it.
    /// </summary>
    /// <returns>True when signaling was reopened; false when there is no resume token or the server refused it
    /// (expired, already used, sender gone), in which case the existing signaling socket is left untouched.</returns>
    /// <exception cref="HttpRequestException">The server could not be reached or answered with an error other than 401.</exception>
    private async Task<bool> ResumeSignalingAsync()
    {
        if (string.IsNullOrEmpty(_resumeToken) || string.IsNullOrEmpty(_signalingUrl))
            return false;

        var response = await ResumeHttpClient.PostAsJsonAsync(
            $"{SignalingUrl.ToHttpBase(_signalingUrl)}/resume",
            new ClientResumeMessage { ResumeToken = _resumeToken });

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The server burns a token on first use, so a refused one is worthless.
            _logger.LogWarning("Resume token was refused by the server");
            _resumeToken = null;
            return false;
        }

        // Other failures (server down, 5xx) keep the token: the server may not have consumed it.
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClientAuthenticationResponse>();
        if (result is not { Success: true } || string.IsNullOrEmpty(result.WebSocketToken))
            throw new InvalidOperationException("Resume response did not contain a WebSocket token");

        // Rotation: the token we just used is spent, the response carries the next one.
        _resumeToken = result.ResumeToken;
        if (result.IceServers is { Count: > 0 })
            _iceServers = result.IceServers;

        // Stop the old receive loop first: it would treat the old socket closing as "server went away"
        // and tear the whole session down.
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        var oldSocket = _signalingWebSocket;
        var newSocket = new ClientWebSocket();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var separator = _signalingUrl.Contains('?') ? "&" : "?";
            await newSocket.ConnectAsync(
                new Uri($"{_signalingUrl}{separator}token={Uri.EscapeDataString(result.WebSocketToken)}"),
                connectCts.Token);
        }
        catch
        {
            newSocket.Dispose();
            throw;
        }

        _signalingWebSocket = newSocket;
        if (oldSocket != null)
        {
            // Closing it lets the server free our ClientId; otherwise the new socket would be rejected as a duplicate.
            try
            {
                if (oldSocket.State == WebSocketState.Open)
                    await oldSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Resuming", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Old signaling socket could not be closed cleanly");
            }
            oldSocket.Dispose();
        }

        _logger.LogInformation("Signaling reopened with resume token");
        return true;
    }

    /// <summary>
    /// Sends an input message (mouse/keyboard) to the remote server via data channel.
    /// </summary>
    public async Task SendInputAsync(InputMessage input)
    {
        if (!_isConnected || _dataChannel == null)
        {
            throw new InvalidOperationException("Not connected or data channel not available");
        }

        if (!_peerVerified)
        {
            return; // no input to a peer that has not passed the end-to-end check
        }

        if (_dataChannel.readyState != RTCDataChannelState.open)
        {
            _logger.LogWarning("Data channel is not open, cannot send input");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(input);
            var bytes = Encoding.UTF8.GetBytes(json);
            _dataChannel.send(bytes);
            _logger.LogDebug("Sent input: {Type}", input.Type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send input via data channel");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Disconnects from the remote server and cleans up all resources.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (!_isConnected && _signalingWebSocket == null) return;

        _logger.LogInformation("Disconnecting from server...");

        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        if (_signalingWebSocket != null)
        {
            if (_signalingWebSocket.State == WebSocketState.Open)
            {
                await _signalingWebSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnecting",
                    CancellationToken.None);
            }
            _signalingWebSocket.Dispose();
            _signalingWebSocket = null;
        }

        if (_dataChannel != null)
        {
            _dataChannel.close();
            _dataChannel = null;
        }

        if (_peerConnection != null)
        {
            _peerConnection?.close();
            _peerConnection?.Dispose();
            _peerConnection = null;
        }

        if (_videoSink != null)
        {
            _videoSink.Dispose();
            _videoSink = null;
        }

        if (_clipboardManager != null)
        {
            _clipboardManager.StopMonitoring();
            _clipboardSync?.Dispose();
            _clipboardSync = null;
            _clipboardManager.Dispose();
            _clipboardManager = null;
        }

        _isConnected = false;
        _serverId = null;
        _resumeToken = null;
        _e2eKey = null; // the session is over: the next connect derives it again from the passkey

        Disconnected?.Invoke(this, _disconnectReason ?? "User disconnected");
        _disconnectReason = null;
        _logger.LogInformation("Disconnected successfully");
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
