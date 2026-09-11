using DeskShare.Common.Interfaces;
using DeskShare.Common.Models;
using DeskShare.ScreenSenderApp.Signaling;
using DeskShare.ScreenSenderApp.Video;
using SIPSorcery.Net;

namespace DeskShare.ScreenSenderApp.WebRTC;

/// <summary>
/// Manages a WebRTC session including signaling and peer connection.
/// Coordinates between WebSocket signaling and the WebRTC video source.
/// </summary>
public sealed class WebRTCSession : IDisposable
{
    private readonly SIPSorceryVideoSource _videoSource;
    private readonly WebSocketSignaler _signaler;
    private bool _disposed;

    /// <summary>
    /// Gets the video source for pushing frames.
    /// </summary>
    public IVideoSource VideoSource => _videoSource;

    /// <summary>
    /// Gets whether the WebRTC connection is established.
    /// </summary>
    public bool IsConnected => _videoSource.IsActive;

    /// <summary>
    /// Gets the signaling server connection status.
    /// </summary>
    public bool IsSignalingConnected => _signaler.IsConnected;

    /// <summary>
    /// Gets the client ID assigned by the signaling server.
    /// </summary>
    public string? ClientId => _signaler.ClientId;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    public event EventHandler<RTCPeerConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebRTCSession"/> class.
    /// </summary>
    public WebRTCSession()
    {
        _videoSource = new SIPSorceryVideoSource();
        _signaler = new WebSocketSignaler();
    }

    /// <summary>
    /// Initializes the session with video parameters and connects to signaling server.
    /// </summary>
    /// <param name="width">Video width in pixels.</param>
    /// <param name="height">Video height in pixels.</param>
    /// <param name="frameRate">Target frame rate.</param>
    /// <param name="signalingServerUrl">WebSocket URL of signaling server.</param>
    /// <param name="serverId">Optional custom server ID. If not provided, uses auto-generated client ID.</param>
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
            _signaler.MessageReceived += OnSignalingMessageReceived;
            await _signaler.ConnectAsync(signalingServerUrl, serverId, cancellationToken);
            // IMPORTANT: Connect to signaling server FIRST, before initializing VideoSource
            // Otherwise ICE candidates will be generated before signaling connection is ready
            // Subscribe to events BEFORE connecting so we don't miss any messages


            Console.WriteLine($"[WebRTCSession] Connected to signaling server. Server ID: {ClientId}");

            // Now initialize video source (this will trigger ICE candidate generation)
            if (!_videoSource.Initialize(width, height, frameRate))
            {
                OnError("Failed to initialize video source.");
                return false;
            }

            // Subscribe to peer connection events
            if (_videoSource.PeerConnection != null)
            {
                _videoSource.PeerConnection.onconnectionstatechange += (state) =>
                {
                    Console.WriteLine($"[WebRTCSession] Connection state: {state}");
                    ConnectionStateChanged?.Invoke(this, state);
                };

                _videoSource.PeerConnection.onicecandidate += (candidate) =>
                {
                    if (candidate != null)
                    {
                        Console.WriteLine($"[WebRTCSession] Local ICE candidate: {candidate.candidate}");
                        // Send ICE candidate via signaling
                        _ = SendIceCandidateAsync(candidate);
                    }
                };
            }

            Console.WriteLine($"[WebRTCSession] Initialized successfully. Server ID: {ClientId}");
            return true;
        }
        catch (Exception ex)
        {
            OnError($"Initialization failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates an SDP offer and sends it to the remote peer via signaling.
    /// </summary>
    /// <param name="targetId">Remote peer ID.</param>
    /// <returns>True if offer was created and sent successfully.</returns>
    public async Task<bool> CreateOfferAsync(string targetId)
    {
        try
        {
            var offer = await _videoSource.CreateOfferAsync();

            var message = new SignalingMessage
            {
                Type = SignalingMessageType.Offer,
                SenderId = ClientId,
                TargetId = targetId,
                Sdp = offer.sdp,
                Timestamp = DateTime.UtcNow
            };

            await _signaler.SendAsync(message);

            Console.WriteLine($"[WebRTCSession] Sent offer to {targetId}");
            return true;
        }
        catch (Exception ex)
        {
            OnError($"Failed to create offer: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends an ICE candidate to the remote peer.
    /// </summary>
    private async Task SendIceCandidateAsync(RTCIceCandidate candidate)
    {
        try
        {
            // Serialize the entire candidate object to JSON, matching the format WebClient expects
            var candidateJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                candidate = candidate.candidate,
                sdpMid = candidate.sdpMid,
                sdpMLineIndex = candidate.sdpMLineIndex,
                usernameFragment = candidate.usernameFragment
            });

            var message = new SignalingMessage
            {
                Type = SignalingMessageType.IceCandidate,
                SenderId = ClientId,
                Candidate = candidateJson,
                SdpMLineIndex = candidate.sdpMLineIndex,
                SdpMid = candidate.sdpMid,
                Timestamp = DateTime.UtcNow
            };

            await _signaler.SendAsync(message);
        }
        catch (Exception ex)
        {
            OnError($"Failed to send ICE candidate: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles incoming signaling messages from the WebSocket connection.
    /// Routes messages to appropriate handlers based on message type.
    /// </summary>
    /// <param name="sender">Event sender (WebSocketSignaler).</param>
    /// <param name="message">The received signaling message.</param>
    /// <remarks>
    /// Uses async void pattern for event handler to properly await async operations.
    /// Exceptions are caught and logged to prevent unhandled exceptions in async void context.
    /// </remarks>
    private async void OnSignalingMessageReceived(object? sender, SignalingMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case SignalingMessageType.ConnectionRequest:
                    // Client wants to establish WebRTC connection
                    HandleConnectionRequest(message);
                    break;

                case SignalingMessageType.Offer:
                    // Received SDP offer (we're acting as answerer)
                    await HandleOfferAsync(message);
                    break;

                case SignalingMessageType.Answer:
                    // Received SDP answer (we're acting as offerer)
                    await HandleAnswerAsync(message);
                    break;

                case SignalingMessageType.IceCandidate:
                    // Received ICE candidate for connection establishment
                    await HandleIceCandidateAsync(message);
                    break;

                case SignalingMessageType.Error:
                    // Signaling error from server or peer
                    OnError($"Signaling error: {message.ErrorMessage}");
                    break;

                case SignalingMessageType.Ping:
                    // Respond to ping with pong to confirm we're alive
                    await RespondToPingAsync(message.SenderId);
                    break;
            }
        }
        catch (Exception ex)
        {
            OnError($"Error handling signaling message: {ex.Message}");
        }
    }

    /// <summary>
    /// Responds to a ping message with a pong.
    /// This confirms to the peer that we're still alive and responding.
    /// </summary>
    /// <param name="senderId">ID of the peer that sent the ping.</param>
    private async Task RespondToPingAsync(string? senderId)
    {
        if (string.IsNullOrEmpty(senderId))
            return;

        try
        {
            var pong = new SignalingMessage
            {
                Type = SignalingMessageType.Pong,
                SenderId = ClientId,
                TargetId = senderId,
                Timestamp = DateTime.UtcNow
            };

            await _signaler.SendAsync(pong);
        }
        catch (Exception ex)
        {
            // Don't throw - pong failure shouldn't crash the app
            Console.WriteLine($"[WebRTC] Failed to send pong: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles connection request from a client.
    /// Phase 1: Only one client supported at a time - disconnects previous client.
    /// </summary>
    /// <param name="message">Connection request message.</param>
    private void HandleConnectionRequest(SignalingMessage message)
    {
        if (string.IsNullOrEmpty(message.SenderId))
        {
            OnError("Connection request has no SenderId");
            return;
        }

        Console.WriteLine($"[WebRTC] Connection request from {message.SenderId}");

        // Reset peer connection for new client (Phase 1: single client only)
        _videoSource.ResetPeerConnection();

        // Wire up event handlers for the new peer connection
        if (_videoSource.PeerConnection != null)
        {
            // Track connection state changes (connecting, connected, disconnected, failed)
            _videoSource.PeerConnection.onconnectionstatechange += (state) =>
            {
                Console.WriteLine($"[WebRTC] Connection state: {state}");
                ConnectionStateChanged?.Invoke(this, state);
            };

            // Send ICE candidates as they're discovered
            _videoSource.PeerConnection.onicecandidate += (candidate) =>
            {
                if (candidate != null)
                {
                    _ = SendIceCandidateAsync(candidate);
                }
            };
        }

        // Create and send offer to initiate WebRTC connection
        _ = CreateOfferAsync(message.SenderId);
    }

    /// <summary>
    /// Handles an incoming SDP offer (typically for receiver role).
    /// </summary>
    private async Task HandleOfferAsync(SignalingMessage message)
    {
        if (string.IsNullOrEmpty(message.Sdp))
        {
            OnError("Received offer with no SDP.");
            return;
        }

        var offer = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = message.Sdp
        };

        _videoSource.SetRemoteDescription(offer);

        // Create and send answer
        var answer = await _videoSource.CreateOfferAsync(); // Note: For answer, we'd use createAnswer in full impl
        var answerMessage = new SignalingMessage
        {
            Type = SignalingMessageType.Answer,
            SenderId = ClientId,
            TargetId = message.SenderId,
            Sdp = answer.sdp,
            Timestamp = DateTime.UtcNow
        };

        await _signaler.SendAsync(answerMessage);
    }

    /// <summary>
    /// Handles an incoming SDP answer from the client.
    /// Completes the offer/answer exchange to establish the WebRTC connection.
    /// </summary>
    /// <param name="message">Signaling message containing the SDP answer.</param>
    private async Task HandleAnswerAsync(SignalingMessage message)
    {
        if (string.IsNullOrEmpty(message.Sdp))
        {
            OnError("Received answer with no SDP.");
            return;
        }

        // Convert SDP string to RTCSessionDescriptionInit structure
        var answer = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = message.Sdp
        };

        // Apply the remote description - this completes the offer/answer exchange
        // After this, ICE candidates can be exchanged to find the best connection path
        _videoSource.SetRemoteDescription(answer);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handles an incoming ICE candidate from the client.
    /// ICE candidates represent possible network paths for the connection.
    /// </summary>
    /// <param name="message">Signaling message containing the ICE candidate.</param>
    private async Task HandleIceCandidateAsync(SignalingMessage message)
    {
        if (string.IsNullOrEmpty(message.Candidate))
        {
            OnError("Received ICE candidate with no candidate data.");
            return;
        }

        // Create ICE candidate structure from the JSON candidate data
        var candidate = new RTCIceCandidateInit
        {
            candidate = message.Candidate,
            sdpMLineIndex = (ushort)(message.SdpMLineIndex ?? 0),
            sdpMid = message.SdpMid
        };

        // Add the candidate to our peer connection
        // WebRTC will test this path and use it if it's the best option
        _videoSource.AddIceCandidate(candidate);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Raises the ErrorOccurred event.
    /// </summary>
    private void OnError(string error)
    {
        Console.WriteLine($"[WebRTCSession] ERROR: {error}");
        ErrorOccurred?.Invoke(this, error);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _signaler.MessageReceived -= OnSignalingMessageReceived;
        _signaler.Dispose();
        _videoSource.Dispose();

        _disposed = true;

        Console.WriteLine("[WebRTCSession] Disposed.");
    }
}
