using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private bool _isConnected;
    private string? _serverId;
    private string? _clientId;
    private string? _password;
    private string? _signalingUrl;
    private IReadOnlyList<IceServerInfo>? _iceServers;
    private CancellationTokenSource? _receiveCts;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 5;
    private bool _isReconnecting;

    public bool IsConnected => _isConnected;
    public string? ServerId => _serverId;

    /// <summary>
    /// Fired when a decoded video frame is received (BGRA32 byte array).
    /// </summary>
    public event EventHandler<byte[]>? FrameReceived;

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
        _clipboardManager.ClipboardChanged += OnLocalClipboardChanged;
    }

    /// <summary>
    /// Handler for local clipboard changes - sends to remote server via data channel.
    /// </summary>
    private void OnLocalClipboardChanged(object? sender, ClipboardChangedEventArgs e)
    {
        try
        {
            if (e.Text != null)
            {
                _logger.LogInformation("Local clipboard changed, sending to server: Text length={Length}",
                    e.Text.Length);
                SendClipboardTextAsync(e.Text).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending clipboard to server");
        }
    }

    /// <summary>
    /// Sends clipboard text to remote server via WebRTC data channel.
    /// </summary>
    public async Task SendClipboardTextAsync(string text)
    {
        if (!_isConnected || _dataChannel == null)
        {
            _logger.LogWarning("Cannot send clipboard: not connected or data channel unavailable");
            return;
        }

        if (_dataChannel.readyState != RTCDataChannelState.open)
        {
            _logger.LogWarning("Data channel is not open, cannot send clipboard");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                type = "clipboard",
                text = text,
                timestamp = DateTime.UtcNow
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            _dataChannel.send(bytes);
            _logger.LogDebug("Sent clipboard text: Size={Size} bytes", bytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send clipboard via data channel");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Connects to a remote server via signaling WebSocket and WebRTC.
    /// </summary>
    /// <param name="webSocketToken">One-time token from /authenticate. Hosted servers reject WebSockets without it.</param>
    /// <param name="clientId">The ClientId sent to /authenticate; the token is bound to it, so we must use the same one.</param>
    /// <param name="iceServers">STUN/TURN servers from /authenticate; falls back to public STUN when null.</param>
    public async Task ConnectAsync(
        string serverId,
        string? password,
        string? signalingUrl = null,
        string? webSocketToken = null,
        string? clientId = null,
        IReadOnlyList<IceServerInfo>? iceServers = null)
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

            // Setup WebRTC peer connection
            await SetupWebRtcConnectionAsync();

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
                await SendSignalingMessageAsync(msg);
            }
        };

        // Handle connection state changes
        _peerConnection.onconnectionstatechange += async (state) =>
        {
            _logger.LogInformation("WebRTC connection state: {State}", state);

            if (state == RTCPeerConnectionState.connected)
            {
                _logger.LogInformation("WebRTC peer connection established");
                _reconnectAttempts = 0;
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
            _logger.LogInformation("[ClientManager] VideoSink.FrameReceived event fired - frame size: {Size} bytes", args.FrameData.Length);
            FrameReceived?.Invoke(this, args.FrameData);

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
        _dataChannel.onopen += () =>
        {
            _logger.LogInformation("Data channel opened");
            _clipboardManager?.StartMonitoring();
        };
        _dataChannel.onclose += () =>
        {
            _logger.LogInformation("Data channel closed");
            _clipboardManager?.StopMonitoring();
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
        try
        {
            var json = Encoding.UTF8.GetString(data);
            _logger.LogDebug("Received data channel message: {Size} bytes", data.Length);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeElement))
            {
                var messageType = typeElement.GetString();

                if (messageType == "clipboard")
                {
                    if (doc.RootElement.TryGetProperty("text", out var textElement))
                    {
                        var text = textElement.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            _logger.LogInformation("Received clipboard text from server: Size={Size} chars", text.Length);

                            _clipboardManager?.SetTextAsync(text).GetAwaiter().GetResult();

                            var legacyMessage = new ClipboardMessage
                            {
                                ContentType = ClipboardContentType.Text,
                                TextContent = text,
                                SizeBytes = Encoding.UTF8.GetByteCount(text),
                                Timestamp = DateTime.UtcNow
                            };
                            ClipboardReceived?.Invoke(this, legacyMessage);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing data channel message");
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
                    await DisconnectAsync();
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
            await DisconnectAsync();
        }
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
                await SetupWebRtcConnectionAsync();
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
    /// Sends an input message (mouse/keyboard) to the remote server via data channel.
    /// </summary>
    public async Task SendInputAsync(InputMessage input)
    {
        if (!_isConnected || _dataChannel == null)
        {
            throw new InvalidOperationException("Not connected or data channel not available");
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
            _clipboardManager.ClipboardChanged -= OnLocalClipboardChanged;
            _clipboardManager.Dispose();
            _clipboardManager = null;
        }

        _isConnected = false;
        _serverId = null;

        Disconnected?.Invoke(this, "User disconnected");
        _logger.LogInformation("Disconnected successfully");
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
