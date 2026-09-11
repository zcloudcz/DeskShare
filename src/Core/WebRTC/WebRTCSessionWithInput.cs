using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;
using SIPSorcery.Net;

namespace DeskShare.Core.WebRTC;

/// <summary>
/// Extended WebRTC session with DataChannel support for remote input control.
/// Combines video streaming with bi-directional data communication.
/// </summary>
/// <remarks>
/// This class extends the basic WebRTCSession by adding:
/// - DataChannel for sending/receiving control messages
/// - Integration with IInputController for processing remote inputs
/// - Statistics and monitoring for both video and data channels
///
/// For junior developers:
/// WebRTC supports multiple types of communication simultaneously:
/// - MediaStream (video/audio): For streaming multimedia content
/// - DataChannel: For sending arbitrary data (like our input messages)
/// Both run over the same peer connection, sharing the ICE/DTLS setup.
/// </remarks>
public sealed class WebRTCSessionWithInput : IDisposable
{
    private readonly WebRTCSession _baseSession;
    private readonly DataChannelManager _dataChannelManager;
    private readonly IInputController? _inputController;
    private readonly IClipboardManager? _clipboardManager;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying video source for pushing frames.
    /// </summary>
    public IVideoSource VideoSource => _baseSession.VideoSource;

    /// <summary>
    /// Gets the data channel manager for sending/receiving messages.
    /// </summary>
    public DataChannelManager DataChannel => _dataChannelManager;

    /// <summary>
    /// Gets whether the WebRTC peer connection is established.
    /// </summary>
    public bool IsConnected => _baseSession.IsConnected;

    /// <summary>
    /// Gets the signaling server connection status.
    /// </summary>
    public bool IsSignalingConnected => _baseSession.IsSignalingConnected;

    /// <summary>
    /// Gets whether the data channel is open and ready.
    /// </summary>
    public bool IsDataChannelOpen => _dataChannelManager.IsOpen;

    /// <summary>
    /// Gets the client ID assigned by the signaling server.
    /// </summary>
    public string? ClientId => _baseSession.ClientId;

    /// <summary>
    /// Gets the input controller if configured.
    /// </summary>
    public IInputController? InputController => _inputController;

    /// <summary>
    /// Gets the clipboard manager if configured.
    /// </summary>
    public IClipboardManager? ClipboardManager => _clipboardManager;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event EventHandler<string>? ErrorOccurred
    {
        add => _baseSession.ErrorOccurred += value;
        remove => _baseSession.ErrorOccurred -= value;
    }

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    public event EventHandler<RTCPeerConnectionState>? ConnectionStateChanged
    {
        add => _baseSession.ConnectionStateChanged += value;
        remove => _baseSession.ConnectionStateChanged -= value;
    }

    /// <summary>
    /// Event raised when the data channel opens.
    /// </summary>
    public event EventHandler? DataChannelOpened
    {
        add => _dataChannelManager.ChannelOpened += value;
        remove => _dataChannelManager.ChannelOpened -= value;
    }

    /// <summary>
    /// Event raised when the data channel closes.
    /// </summary>
    public event EventHandler? DataChannelClosed
    {
        add => _dataChannelManager.ChannelClosed += value;
        remove => _dataChannelManager.ChannelClosed -= value;
    }

    /// <summary>
    /// Initializes a new instance of the WebRTCSessionWithInput class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    /// <param name="inputController">Optional input controller for processing remote inputs.</param>
    /// <param name="clipboardManager">Optional clipboard manager for clipboard synchronization.</param>
    public WebRTCSessionWithInput(ILogger logger, IInputController? inputController = null, IClipboardManager? clipboardManager = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inputController = inputController;
        _clipboardManager = clipboardManager;

        _baseSession = new WebRTCSession();
        _dataChannelManager = new DataChannelManager(logger, inputController);

        // Subscribe to clipboard changes to forward to remote peer
        if (_clipboardManager != null)
        {
            _clipboardManager.ClipboardChanged += OnLocalClipboardChanged;
        }

        // Subscribe to incoming clipboard messages from remote peer
        _dataChannelManager.ClipboardMessageReceived += OnRemoteClipboardMessageReceived;

        _logger.Information("WebRTCSessionWithInput initialized with input controller: {HasInput}, clipboard: {HasClipboard}",
            inputController != null, clipboardManager != null);
    }

    /// <summary>
    /// Called when local clipboard content changes.
    /// Forwards the clipboard content to the remote peer via data channel.
    /// </summary>
    private void OnLocalClipboardChanged(object? sender, ClipboardChangedEventArgs e)
    {
        try
        {
            _logger.Information("Local clipboard changed, forwarding to remote peer: Text length={Length}",
                e.Text?.Length ?? 0);

            // Send clipboard message via data channel
            var success = _dataChannelManager.SendJson(new
            {
                type = "clipboard",
                text = e.Text,
                timestamp = e.Timestamp
            });

            if (!success)
            {
                _logger.Warning("Failed to send clipboard message to remote peer");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error forwarding clipboard change");
        }
    }

    /// <summary>
    /// Called when a clipboard message is received from the remote peer.
    /// Updates the local clipboard with the received content.
    /// </summary>
    private async void OnRemoteClipboardMessageReceived(object? sender, ClipboardMessage message)
    {
        try
        {
            _logger.Information("Received clipboard from remote peer: Type={Type}, Size={Size}",
                message.ContentType, message.SizeBytes);

            if (_clipboardManager != null && message.TextContent != null)
            {
                var success = await _clipboardManager.SetTextAsync(message.TextContent);

                if (success)
                {
                    _logger.Information("Successfully updated local clipboard from remote");
                }
                else
                {
                    _logger.Warning("Failed to update local clipboard from remote");
                }
            }
            else
            {
                _logger.Warning("Clipboard manager not available, cannot update clipboard");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error applying remote clipboard");
        }
    }

    /// <summary>
    /// Initializes the session with video parameters and connects to signaling server.
    /// Also sets up the data channel for input control.
    /// </summary>
    /// <param name="width">Video width in pixels.</param>
    /// <param name="height">Video height in pixels.</param>
    /// <param name="frameRate">Target frame rate.</param>
    /// <param name="signalingServerUrl">WebSocket URL of signaling server.</param>
    /// <param name="serverId">Optional custom server ID.</param>
    /// <param name="enableDataChannel">Whether to create a data channel (default: true).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if initialization succeeded.</returns>
    public async Task<bool> InitializeAsync(
        int width,
        int height,
        int frameRate,
        string signalingServerUrl,
        string? serverId = null,
        bool enableDataChannel = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Initialize base WebRTC session (video + signaling)
            bool success = await _baseSession.InitializeAsync(
                width, height, frameRate, signalingServerUrl, serverId, cancellationToken);

            if (!success)
            {
                _logger.Error("Failed to initialize base WebRTC session");
                return false;
            }

            // Create data channel if enabled
            if (enableDataChannel)
            {
                success = await CreateDataChannelAsync();

                if (!success)
                {
                    _logger.Warning("Failed to create data channel, but video session is ready");
                    // Don't fail initialization just because data channel failed
                    // Video streaming will still work
                }
            }

            _logger.Information("WebRTCSessionWithInput initialized successfully. " +
                "Video: {VideoReady}, DataChannel: {DataChannelReady}",
                IsConnected, IsDataChannelOpen);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize WebRTCSessionWithInput");
            return false;
        }
    }

    /// <summary>
    /// Creates a data channel on the peer connection.
    /// This should be called after the base session is initialized.
    /// </summary>
    /// <param name="label">Label (name) for the data channel.</param>
    /// <returns>True if created successfully.</returns>
    private async Task<bool> CreateDataChannelAsync(string label = "input")
    {
        try
        {
            // Access the underlying peer connection from the video source
            var peerConnection = (_baseSession.VideoSource as Video.SIPSorceryVideoSource)?.PeerConnection;

            if (peerConnection == null)
            {
                _logger.Error("Cannot create DataChannel: PeerConnection not available");
                return false;
            }

            // Create the data channel (this will also attach it to the manager)
            var dataChannel = await _dataChannelManager.CreateDataChannelAsync(peerConnection, label);

            _logger.Information("DataChannel created: Label={Label}, ID={Id}",
                dataChannel.label, dataChannel.id);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create data channel");
            return false;
        }
    }

    /// <summary>
    /// Creates an SDP offer and sends it to the remote peer via signaling.
    /// </summary>
    /// <param name="targetId">Remote peer ID.</param>
    /// <returns>True if offer was created and sent successfully.</returns>
    public Task<bool> CreateOfferAsync(string targetId)
    {
        return _baseSession.CreateOfferAsync(targetId);
    }

    /// <summary>
    /// Sends a message to the remote peer via data channel.
    /// </summary>
    /// <param name="message">The text message to send.</param>
    /// <returns>True if sent successfully.</returns>
    public bool SendDataChannelMessage(string message)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WebRTCSessionWithInput));
        }

        return _dataChannelManager.SendMessage(message);
    }

    /// <summary>
    /// Sends a JSON-serialized object to the remote peer via data channel.
    /// </summary>
    /// <typeparam name="T">Type of object to send.</typeparam>
    /// <param name="obj">The object to serialize and send.</param>
    /// <returns>True if sent successfully.</returns>
    public bool SendDataChannelJson<T>(T obj)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WebRTCSessionWithInput));
        }

        return _dataChannelManager.SendJson(obj);
    }

    /// <summary>
    /// Requests authorization from the user to enable remote input control.
    /// </summary>
    /// <param name="requestingClientId">ID of the client requesting control.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if authorization was granted.</returns>
    public async Task<bool> RequestInputAuthorizationAsync(
        string requestingClientId,
        CancellationToken cancellationToken = default)
    {
        if (_inputController == null)
        {
            _logger.Warning("Cannot request authorization: No input controller configured");
            return false;
        }

        _logger.Information("Requesting input authorization for client: {ClientId}", requestingClientId);

        bool authorized = await _inputController.RequestAuthorizationAsync(requestingClientId, cancellationToken);

        if (authorized)
        {
            _logger.Information("Input control authorized for client: {ClientId}", requestingClientId);

            // Send authorization confirmation via data channel
            SendDataChannelJson(new
            {
                type = "authorization_response",
                authorized = true,
                timestamp = DateTime.UtcNow
            });
        }
        else
        {
            _logger.Information("Input control denied for client: {ClientId}", requestingClientId);

            // Send denial notification via data channel
            SendDataChannelJson(new
            {
                type = "authorization_response",
                authorized = false,
                timestamp = DateTime.UtcNow
            });
        }

        return authorized;
    }

    /// <summary>
    /// Revokes current input control authorization.
    /// </summary>
    public void RevokeInputAuthorization()
    {
        if (_inputController == null)
        {
            return;
        }

        _logger.Information("Revoking input control authorization");
        _inputController.RevokeAuthorization();

        // Notify remote peer via data channel
        SendDataChannelJson(new
        {
            type = "authorization_revoked",
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Gets comprehensive statistics about the session.
    /// </summary>
    /// <returns>Statistics object.</returns>
    public WebRTCSessionStatistics GetStatistics()
    {
        var dataChannelStats = _dataChannelManager.GetStatistics();
        var inputStats = _inputController?.GetStatistics();

        return new WebRTCSessionStatistics
        {
            IsConnected = IsConnected,
            IsSignalingConnected = IsSignalingConnected,
            IsDataChannelOpen = IsDataChannelOpen,
            ClientId = ClientId,
            DataChannelStatistics = dataChannelStats,
            InputStatistics = inputStats
        };
    }

    /// <summary>
    /// Disposes the session and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _logger.Information("Disposing WebRTCSessionWithInput");

        // Unsubscribe from events
        if (_clipboardManager != null)
        {
            _clipboardManager.ClipboardChanged -= OnLocalClipboardChanged;
        }
        _dataChannelManager.ClipboardMessageReceived -= OnRemoteClipboardMessageReceived;

        // Revoke authorization if active
        if (_inputController?.AuthorizationState == InputAuthorizationState.Authorized)
        {
            RevokeInputAuthorization();
        }

        // Stop clipboard monitoring
        _clipboardManager?.StopMonitoring();

        _dataChannelManager.Dispose();
        _baseSession.Dispose();
        _clipboardManager?.Dispose();

        _disposed = true;

        _logger.Information("WebRTCSessionWithInput disposed");
    }
}

/// <summary>
/// Contains comprehensive statistics about a WebRTC session with input control.
/// </summary>
public sealed class WebRTCSessionStatistics
{
    /// <summary>
    /// Whether the peer connection is established.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Whether connected to the signaling server.
    /// </summary>
    public bool IsSignalingConnected { get; set; }

    /// <summary>
    /// Whether the data channel is open.
    /// </summary>
    public bool IsDataChannelOpen { get; set; }

    /// <summary>
    /// Client ID assigned by signaling server.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Data channel statistics.
    /// </summary>
    public DataChannelStatistics? DataChannelStatistics { get; set; }

    /// <summary>
    /// Input controller statistics.
    /// </summary>
    public Interfaces.InputStatistics? InputStatistics { get; set; }
}
