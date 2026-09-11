using DeskShare.Common.Interfaces;
using DeskShare.Common.Models;
using DeskShare.ScreenSenderApp.Signaling;
using DeskShare.ScreenSenderApp.Video;
using SIPSorcery.Net;

namespace DeskShare.ScreenSenderApp.WebRTC;

/// <summary>
/// Manages multiple concurrent WebRTC sessions with different clients.
/// Supports broadcasting screen to multiple viewers simultaneously.
/// </summary>
/// <remarks>
/// This class coordinates between WebSocket signaling and multiple WebRTC peer connections.
/// Each client gets their own dedicated peer connection and encoder instance.
/// Automatic cleanup when clients disconnect.
/// </remarks>
public sealed class MultiClientWebRTCSession : IDisposable
{
    private readonly MultiClientVideoSource _videoSource;
    private readonly WebSocketSignaler _signaler;
    private bool _disposed;

    /// <summary>
    /// Gets the video source for pushing frames to all clients.
    /// </summary>
    public IVideoSource VideoSource => _videoSource;

    /// <summary>
    /// Gets whether any clients are connected.
    /// </summary>
    public bool IsConnected => _videoSource.IsActive;

    /// <summary>
    /// Gets the signaling server connection status.
    /// </summary>
    public bool IsSignalingConnected => _signaler.IsConnected;

    /// <summary>
    /// Gets the server ID assigned by the signaling server.
    /// </summary>
    public string? ServerId => _signaler.ClientId;

    /// <summary>
    /// Gets the number of currently connected clients.
    /// </summary>
    public int ConnectedClientCount => _videoSource.ConnectedClientCount;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Event raised when a client's connection state changes.
    /// </summary>
    public event EventHandler<ClientConnectionStateChanged>? ClientConnectionStateChanged;

    /// <summary>
    /// Event raised when a new client connects.
    /// </summary>
    public event EventHandler<string>? ClientConnected;

    /// <summary>
    /// Event raised when a client disconnects.
    /// </summary>
    public event EventHandler<string>? ClientDisconnected;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiClientWebRTCSession"/> class.
    /// </summary>
    public MultiClientWebRTCSession()
    {
        _videoSource = new MultiClientVideoSource();
        _signaler = new WebSocketSignaler();
    }

    /// <summary>
    /// Initializes the session with video parameters and connects to signaling server.
    /// </summary>
    /// <param name="width">Video width in pixels.</param>
    /// <param name="height">Video height in pixels.</param>
    /// <param name="frameRate">Target frame rate.</param>
    /// <param name="signalingServerUrl">WebSocket URL of signaling server.</param>
    /// <param name="serverId">Optional custom server ID. If not provided, uses auto-generated ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if initialization succeeded.</returns>
    public async Task<bool> InitializeAsync(
        int width,
        int height,
        int frameRate,
        string signalingServerUrl,
        string? serverId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Subscribe to signaling messages BEFORE connecting
            _signaler.MessageReceived += OnSignalingMessageReceived;

            // Connect to signaling server
            await _signaler.ConnectAsync(signalingServerUrl, serverId, cancellationToken);

            Console.WriteLine($"[MultiClientWebRTCSession] Connected to signaling server. Server ID: {ServerId}");

            // Initialize video source (doesn't create peer connections yet, waits for clients)
            if (!_videoSource.Initialize(width, height, frameRate))
            {
                OnError("Failed to initialize video source.");
                return false;
            }

            Console.WriteLine($"[MultiClientWebRTCSession] Initialized successfully. Ready for clients.");
            return true;
        }
        catch (Exception ex)
        {
            OnError($"Initialization failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Handles incoming signaling messages.
    /// </summary>
    private async void OnSignalingMessageReceived(object? sender, SignalingMessage message)
    {
        try
        {
            // Important: SenderId is the client who sent the message
            // TargetId should be our ServerId
            var clientId = message.SenderId;

            if (string.IsNullOrEmpty(clientId))
            {
                Console.WriteLine("[MultiClientWebRTCSession] Received message with no sender ID");
                return;
            }

            switch (message.Type)
            {
                case SignalingMessageType.ConnectionRequest:
                    // New client wants to connect
                    Console.WriteLine($"[MultiClientWebRTCSession] Connection request from client: {clientId}");
                    await HandleConnectionRequestAsync(clientId);
                    break;

                case SignalingMessageType.Answer:
                    // Client sent SDP answer
                    Console.WriteLine($"[MultiClientWebRTCSession] Received answer from client: {clientId}");
                    await HandleAnswerAsync(clientId, message);
                    break;

                case SignalingMessageType.IceCandidate:
                    // Client sent ICE candidate
                    Console.WriteLine($"[MultiClientWebRTCSession] Received ICE candidate from client: {clientId}");
                    HandleIceCandidate(clientId, message);
                    break;

                case SignalingMessageType.Ping:
                    // Respond to ping with pong
                    await RespondToPingAsync(clientId);
                    break;

                case SignalingMessageType.Error:
                    Console.WriteLine($"[MultiClientWebRTCSession] Error from client {clientId}: {message.ErrorMessage}");
                    break;

                default:
                    Console.WriteLine($"[MultiClientWebRTCSession] Unhandled message type: {message.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiClientWebRTCSession] Error handling message: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles a connection request from a new client.
    /// Creates dedicated peer connection and sends SDP offer.
    /// </summary>
    private async Task HandleConnectionRequestAsync(string clientId)
    {
        try
        {
            // Add new client with dedicated peer connection
            var peerConnection = _videoSource.AddClient(clientId);

            // Subscribe to peer connection events for this client
            peerConnection.onconnectionstatechange += (state) =>
            {
                Console.WriteLine($"[MultiClientWebRTCSession] Client {clientId} state: {state}");
                ClientConnectionStateChanged?.Invoke(this, new ClientConnectionStateChanged(clientId, state));

                if (state == RTCPeerConnectionState.connected)
                {
                    ClientConnected?.Invoke(this, clientId);
                }
                else if (state == RTCPeerConnectionState.disconnected ||
                         state == RTCPeerConnectionState.failed ||
                         state == RTCPeerConnectionState.closed)
                {
                    ClientDisconnected?.Invoke(this, clientId);
                }
            };

            peerConnection.onicecandidate += async (candidate) =>
            {
                if (candidate != null)
                {
                    Console.WriteLine($"[MultiClientWebRTCSession] Local ICE candidate for {clientId}: {candidate.candidate}");
                    await SendIceCandidateAsync(clientId, candidate);
                }
            };

            // Create and send SDP offer to client
            var offer = peerConnection.createOffer();
            await peerConnection.setLocalDescription(offer);

            var message = new SignalingMessage
            {
                Type = SignalingMessageType.Offer,
                SenderId = ServerId,
                TargetId = clientId,
                Sdp = offer.sdp,
                Timestamp = DateTime.UtcNow
            };

            await _signaler.SendAsync(message);

            Console.WriteLine($"[MultiClientWebRTCSession] Sent offer to client: {clientId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiClientWebRTCSession] Error handling connection request from {clientId}: {ex.Message}");
            OnError($"Failed to connect client {clientId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles SDP answer from a client.
    /// </summary>
    private async Task HandleAnswerAsync(string clientId, SignalingMessage message)
    {
        try
        {
            var peerConnection = _videoSource.GetClientPeerConnection(clientId);
            if (peerConnection == null)
            {
                Console.WriteLine($"[MultiClientWebRTCSession] No peer connection found for client: {clientId}");
                return;
            }

            if (string.IsNullOrEmpty(message.Sdp))
            {
                Console.WriteLine($"[MultiClientWebRTCSession] Answer has no SDP");
                return;
            }

            var answer = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = message.Sdp
            };

            var result = peerConnection.setRemoteDescription(answer);
            if (result != SetDescriptionResultEnum.OK)
            {
                OnError($"Failed to set remote description for client {clientId}: {result}");
            }
            else
            {
                Console.WriteLine($"[MultiClientWebRTCSession] Set remote description for client: {clientId}");
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiClientWebRTCSession] Error handling answer from {clientId}: {ex.Message}");
            OnError($"Failed to process answer from client {clientId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles ICE candidate from a client.
    /// </summary>
    private void HandleIceCandidate(string clientId, SignalingMessage message)
    {
        try
        {
            var peerConnection = _videoSource.GetClientPeerConnection(clientId);
            if (peerConnection == null)
            {
                Console.WriteLine($"[MultiClientWebRTCSession] No peer connection found for client: {clientId}");
                return;
            }

            if (string.IsNullOrEmpty(message.Candidate))
            {
                Console.WriteLine($"[MultiClientWebRTCSession] ICE candidate is empty");
                return;
            }

            var candidate = new RTCIceCandidateInit
            {
                candidate = message.Candidate,
                sdpMid = message.SdpMid ?? "0",
                sdpMLineIndex = (ushort)(message.SdpMLineIndex ?? 0)
            };

            peerConnection.addIceCandidate(candidate);
            Console.WriteLine($"[MultiClientWebRTCSession] Added ICE candidate for client: {clientId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiClientWebRTCSession] Error handling ICE candidate from {clientId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends an ICE candidate to a specific client via signaling.
    /// </summary>
    private async Task SendIceCandidateAsync(string clientId, RTCIceCandidate candidate)
    {
        try
        {
            var message = new SignalingMessage
            {
                Type = SignalingMessageType.IceCandidate,
                SenderId = ServerId,
                TargetId = clientId,
                Candidate = candidate.candidate,
                SdpMid = candidate.sdpMid,
                SdpMLineIndex = candidate.sdpMLineIndex,
                Timestamp = DateTime.UtcNow
            };

            await _signaler.SendAsync(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiClientWebRTCSession] Error sending ICE candidate to {clientId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Responds to a ping message with a pong.
    /// </summary>
    private async Task RespondToPingAsync(string clientId)
    {
        try
        {
            var pong = new SignalingMessage
            {
                Type = SignalingMessageType.Pong,
                SenderId = ServerId,
                TargetId = clientId,
                Timestamp = DateTime.UtcNow
            };

            await _signaler.SendAsync(pong);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiClientWebRTCSession] Error sending pong to {clientId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets detailed statistics for all connected clients.
    /// </summary>
    /// <returns>Dictionary of client statistics.</returns>
    public Dictionary<string, ClientStatistics> GetClientStatistics()
    {
        return _videoSource.GetClientStatistics();
    }

    /// <summary>
    /// Disconnects a specific client.
    /// </summary>
    /// <param name="clientId">Client to disconnect.</param>
    public void DisconnectClient(string clientId)
    {
        _videoSource.RemoveClient(clientId);
    }

    /// <summary>
    /// Raises the error event.
    /// </summary>
    private void OnError(string error)
    {
        Console.WriteLine($"[MultiClientWebRTCSession] Error: {error}");
        ErrorOccurred?.Invoke(this, error);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        Console.WriteLine($"[MultiClientWebRTCSession] Disposing...");

        _signaler.MessageReceived -= OnSignalingMessageReceived;
        _signaler.Dispose();
        _videoSource.Dispose();

        _disposed = true;
    }
}

/// <summary>
/// Event args for client connection state changes.
/// </summary>
public sealed class ClientConnectionStateChanged
{
    /// <summary>
    /// Client identifier.
    /// </summary>
    public string ClientId { get; }

    /// <summary>
    /// New connection state.
    /// </summary>
    public RTCPeerConnectionState State { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientConnectionStateChanged"/> class.
    /// </summary>
    public ClientConnectionStateChanged(string clientId, RTCPeerConnectionState state)
    {
        ClientId = clientId;
        State = state;
    }
}
