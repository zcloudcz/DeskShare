using System.Text;
using System.Text.Json;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;
using SIPSorcery.Net;

namespace DeskShare.Core.WebRTC;

/// <summary>
/// Manages WebRTC DataChannel for bi-directional communication.
/// Handles incoming input messages and provides statistics.
/// </summary>
/// <remarks>
/// DataChannel provides a reliable, ordered data transport mechanism over WebRTC
/// alongside the video stream. This is perfect for low-latency control messages
/// like mouse and keyboard inputs.
///
/// For junior developers:
/// - DataChannel is like WebSocket but runs over the same WebRTC connection as video
/// - It supports both reliable (TCP-like) and unreliable (UDP-like) delivery
/// - We use reliable mode for input messages to ensure no commands are lost
/// </remarks>
public sealed class DataChannelManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly IInputController? _inputController;
    private RTCDataChannel? _dataChannel;
    private bool _disposed;

    // Statistics tracking
    private long _messagesReceived;
    private long _messagesSent;
    private long _bytesReceived;
    private long _bytesSent;
    private DateTime _channelOpenedAt;

    /// <summary>
    /// Event raised when a message is received on the data channel.
    /// </summary>
    public event EventHandler<string>? MessageReceived;

    /// <summary>
    /// Event raised when a clipboard message is received.
    /// </summary>
    public event EventHandler<ClipboardMessage>? ClipboardMessageReceived;

    /// <summary>
    /// Event raised when the data channel opens.
    /// </summary>
    public event EventHandler? ChannelOpened;

    /// <summary>
    /// Event raised when the data channel closes.
    /// </summary>
    public event EventHandler? ChannelClosed;

    /// <summary>
    /// Gets whether the data channel is currently open and ready.
    /// </summary>
    public bool IsOpen => _dataChannel?.readyState == RTCDataChannelState.open;

    /// <summary>
    /// Gets the label (name) of the data channel.
    /// </summary>
    public string? Label => _dataChannel?.label;

    /// <summary>
    /// Initializes a new instance of the DataChannelManager class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    /// <param name="inputController">Optional input controller for processing remote input.</param>
    public DataChannelManager(ILogger logger, IInputController? inputController = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inputController = inputController;
        _logger.Information("DataChannelManager initialized");
    }

    /// <summary>
    /// Attaches to an existing data channel created by the peer connection.
    /// This is typically called when the remote peer creates the data channel.
    /// </summary>
    /// <param name="dataChannel">The data channel to attach to.</param>
    public void AttachDataChannel(RTCDataChannel dataChannel)
    {
        ArgumentNullException.ThrowIfNull(dataChannel);

        if (_dataChannel != null)
        {
            _logger.Warning("DataChannel already attached, replacing with new channel");
            DetachDataChannel();
        }

        _dataChannel = dataChannel;
        _channelOpenedAt = DateTime.UtcNow;

        // Subscribe to data channel events
        _dataChannel.onopen += OnDataChannelOpen;
        _dataChannel.onclose += OnDataChannelClose;
        _dataChannel.onerror += OnDataChannelError;
        _dataChannel.onmessage += OnDataChannelMessage;

        _logger.Information("DataChannel attached: Label={Label}, ID={Id}, State={State}",
            dataChannel.label, dataChannel.id, dataChannel.readyState);
    }

    /// <summary>
    /// Creates a new data channel on the peer connection.
    /// This is typically called by the offerer (screen sender) to establish the channel.
    /// </summary>
    /// <param name="peerConnection">The peer connection to create the channel on.</param>
    /// <param name="label">Label (name) for the data channel.</param>
    /// <param name="options">Optional configuration options.</param>
    /// <returns>The created data channel.</returns>
    public async Task<RTCDataChannel> CreateDataChannelAsync(
        RTCPeerConnection peerConnection,
        string label = "input",
        RTCDataChannelInit? options = null)
    {
        ArgumentNullException.ThrowIfNull(peerConnection);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        // Default options for reliable, ordered delivery
        options ??= new RTCDataChannelInit
        {
            ordered = true,  // Messages delivered in order
            // maxRetransmits and maxPacketLifeTime are null for reliable mode
        };

        var dataChannel = await peerConnection.createDataChannel(label, options);
        AttachDataChannel(dataChannel);

        _logger.Information("DataChannel created: Label={Label}, Ordered={Ordered}",
            label, options.ordered);

        return dataChannel;
    }

    /// <summary>
    /// Sends a text message over the data channel.
    /// </summary>
    /// <param name="message">The text message to send.</param>
    /// <returns>True if sent successfully, false otherwise.</returns>
    public bool SendMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DataChannelManager));
        }

        if (!IsOpen)
        {
            _logger.Warning("Cannot send message: DataChannel not open (State={State})",
                _dataChannel?.readyState);
            return false;
        }

        try
        {
            _dataChannel!.send(message);

            Interlocked.Increment(ref _messagesSent);
            Interlocked.Add(ref _bytesSent, Encoding.UTF8.GetByteCount(message));

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send message over DataChannel");
            return false;
        }
    }

    /// <summary>
    /// Sends an object as JSON over the data channel.
    /// </summary>
    /// <typeparam name="T">Type of object to send.</typeparam>
    /// <param name="obj">The object to serialize and send.</param>
    /// <returns>True if sent successfully, false otherwise.</returns>
    public bool SendJson<T>(T obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        try
        {
            var json = JsonSerializer.Serialize(obj);
            return SendMessage(json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to serialize object to JSON");
            return false;
        }
    }

    /// <summary>
    /// Gets statistics about data channel operations.
    /// </summary>
    /// <returns>Statistics object.</returns>
    public DataChannelStatistics GetStatistics()
    {
        return new DataChannelStatistics
        {
            IsOpen = IsOpen,
            Label = Label,
            MessagesReceived = Interlocked.Read(ref _messagesReceived),
            MessagesSent = Interlocked.Read(ref _messagesSent),
            BytesReceived = Interlocked.Read(ref _bytesReceived),
            BytesSent = Interlocked.Read(ref _bytesSent),
            ChannelOpenedAt = _channelOpenedAt,
            Uptime = DateTime.UtcNow - _channelOpenedAt
        };
    }

    #region Event Handlers

    /// <summary>
    /// Called when the data channel opens and becomes ready.
    /// </summary>
    private void OnDataChannelOpen()
    {
        _channelOpenedAt = DateTime.UtcNow;
        _logger.Information("DataChannel opened: Label={Label}, ID={Id}",
            _dataChannel?.label, _dataChannel?.id);

        ChannelOpened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called when the data channel closes.
    /// </summary>
    private void OnDataChannelClose()
    {
        _logger.Information("DataChannel closed: Label={Label}, ID={Id}",
            _dataChannel?.label, _dataChannel?.id);

        ChannelClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called when an error occurs on the data channel.
    /// </summary>
    /// <param name="error">Error message.</param>
    private void OnDataChannelError(string error)
    {
        _logger.Error("DataChannel error: {Error}", error);
    }

    /// <summary>
    /// Called when a message is received on the data channel.
    /// Processes input messages if an input controller is configured.
    /// </summary>
    /// <param name="dc">The data channel that received the message.</param>
    /// <param name="protocol">Protocol type (string or binary).</param>
    /// <param name="data">The message data.</param>
    private void OnDataChannelMessage(RTCDataChannel dc, DataChannelPayloadProtocols protocol, byte[] data)
    {
        try
        {
            Interlocked.Increment(ref _messagesReceived);
            Interlocked.Add(ref _bytesReceived, data.Length);

            // Convert byte array to string
            var message = Encoding.UTF8.GetString(data);

            _logger.Debug("DataChannel message received: Length={Length} bytes", data.Length);

            // Raise event for general message handling
            MessageReceived?.Invoke(this, message);

            // If input controller is configured, try to process as input message
            if (_inputController != null)
            {
                ProcessInputMessage(message);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing DataChannel message");
        }
    }

    /// <summary>
    /// Processes a message as an input command or clipboard message.
    /// Deserializes JSON and applies to appropriate handler.
    /// </summary>
    /// <param name="message">JSON message containing command.</param>
    private void ProcessInputMessage(string message)
    {
        try
        {
            // Try to parse as a wrapper object with "type" field
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("type", out var typeElement))
            {
                var messageType = typeElement.GetString();

                if (messageType == "clipboard" && doc.RootElement.TryGetProperty("data", out var dataElement))
                {
                    // This is a clipboard message
                    var clipboardMessage = JsonSerializer.Deserialize<ClipboardMessage>(dataElement.GetRawText());
                    if (clipboardMessage != null)
                    {
                        _logger.Information("Received clipboard message: Type={Type}, Size={Size}",
                            clipboardMessage.ContentType, clipboardMessage.SizeBytes);
                        ClipboardMessageReceived?.Invoke(this, clipboardMessage);
                        return;
                    }
                }
            }

            // Fall back to treating as InputMessage
            var inputMessage = JsonSerializer.Deserialize<InputMessage>(message);

            if (inputMessage == null)
            {
                _logger.Warning("Failed to deserialize message: Result was null");
                return;
            }

            // Apply input to controller if available
            if (_inputController != null)
            {
                bool success = _inputController.ApplyInput(inputMessage);

                if (!success)
                {
                    _logger.Warning("Input message rejected: Type={Type}", inputMessage.Type);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "Invalid JSON format for message");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing message");
        }
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Detaches from the current data channel and unsubscribes from events.
    /// </summary>
    private void DetachDataChannel()
    {
        if (_dataChannel == null)
        {
            return;
        }

        _dataChannel.onopen -= OnDataChannelOpen;
        _dataChannel.onclose -= OnDataChannelClose;
        _dataChannel.onerror -= OnDataChannelError;
        _dataChannel.onmessage -= OnDataChannelMessage;

        _dataChannel = null;
    }

    /// <summary>
    /// Disposes the data channel manager and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DetachDataChannel();
        _disposed = true;

        _logger.Information("DataChannelManager disposed. Final statistics: Received={Received}, Sent={Sent}",
            _messagesReceived, _messagesSent);
    }

    #endregion
}

/// <summary>
/// Contains statistics about data channel operations.
/// </summary>
public sealed class DataChannelStatistics
{
    /// <summary>
    /// Whether the data channel is currently open.
    /// </summary>
    public bool IsOpen { get; set; }

    /// <summary>
    /// Label (name) of the data channel.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Total number of messages received.
    /// </summary>
    public long MessagesReceived { get; set; }

    /// <summary>
    /// Total number of messages sent.
    /// </summary>
    public long MessagesSent { get; set; }

    /// <summary>
    /// Total bytes received.
    /// </summary>
    public long BytesReceived { get; set; }

    /// <summary>
    /// Total bytes sent.
    /// </summary>
    public long BytesSent { get; set; }

    /// <summary>
    /// Timestamp when channel was opened.
    /// </summary>
    public DateTime ChannelOpenedAt { get; set; }

    /// <summary>
    /// Duration since channel was opened.
    /// </summary>
    public TimeSpan Uptime { get; set; }
}
