using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeskShare.Common.Interfaces;
using DeskShare.Common.Models;

namespace DeskShare.ScreenSenderApp.Signaling;

/// <summary>
/// WebSocket-based implementation of signaling for WebRTC connection establishment.
/// </summary>
public sealed class WebSocketSignaler : ISignaler
{
    private const int ReceiveBufferSize = 4096;
    private const int ReconnectDelayMs = 5000;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;
    private readonly TaskCompletionSource<bool> _identifyReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc/>
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <inheritdoc/>
    public string? ClientId { get; private set; }

    /// <inheritdoc/>
    public event EventHandler<SignalingMessage>? MessageReceived;

    /// <inheritdoc/>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <inheritdoc/>
    public event EventHandler<SignalingErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc/>
    public async Task ConnectAsync(string serverUrl, string? clientId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new ArgumentNullException(nameof(serverUrl));

        if (IsConnected)
            throw new InvalidOperationException("Already connected to signaling server.");

        try
        {
            // Create new WebSocket client with keep-alive to maintain connection
            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            // Establish WebSocket connection to signaling server
            await _webSocket.ConnectAsync(new Uri(serverUrl), cancellationToken);

            // Start background task to continuously receive messages from server
            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

            // Wait for server to send us our assigned client ID (Identify message)
            // Server always assigns a GUID in Phase 1 (custom IDs not yet supported)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            try
            {
                // Block until we receive the Identify message (or timeout after 5 seconds)
                await _identifyReceived.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new SignalingException("Timeout waiting for server Identify confirmation.");
            }

            // Notify listeners that we're successfully connected
            ConnectionStateChanged?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            CleanupConnection();
            throw new SignalingException("Failed to connect to signaling server.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            return;

        try
        {
            _receiveCts?.Cancel();

            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnect",
                    cancellationToken);
            }

            if (_receiveTask != null)
            {
                await _receiveTask;
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new SignalingErrorEventArgs("Error during disconnect", ex));
        }
        finally
        {
            CleanupConnection();
        }
    }

    /// <inheritdoc/>
    public async Task SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        if (!IsConnected)
            throw new InvalidOperationException("Not connected to signaling server.");

        try
        {
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(bytes);

            await _webSocket!.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new SignalingException("Failed to send message.", ex);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _webSocket?.Dispose();
        }
        catch
        {
            // Ignore errors during disposal
        }
        finally
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// Main receive loop for handling incoming signaling messages.
    /// Runs continuously in the background until cancelled or connection closes.
    /// </summary>
    /// <param name="cancellationToken">Token to signal loop cancellation.</param>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];

        try
        {
            // Continue receiving messages until cancellation or WebSocket closes
            while (!cancellationToken.IsCancellationRequested && _webSocket != null)
            {
                // Exit if WebSocket is no longer open
                if (_webSocket.State != WebSocketState.Open)
                    break;

                // Wait for next message from server
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                // Handle close message - server is disconnecting
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                // Process text messages (JSON signaling messages)
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessMessage(json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when DisconnectAsync() is called - not an error
        }
        catch (Exception ex)
        {
            // Unexpected error in receive loop - notify listeners
            ErrorOccurred?.Invoke(this, new SignalingErrorEventArgs("Error in receive loop", ex));
        }
        finally
        {
            // Always notify that connection is now closed
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Processes received JSON message and raises MessageReceived event.
    /// Handles special case for Identify messages to complete connection handshake.
    /// </summary>
    /// <param name="json">JSON string containing the signaling message.</param>
    private void ProcessMessage(string json)
    {
        try
        {
            // Deserialize JSON to SignalingMessage object
            var message = JsonSerializer.Deserialize<SignalingMessage>(json);

            if (message == null)
            {
                ErrorOccurred?.Invoke(this, new SignalingErrorEventArgs("Received null message"));
                return;
            }

            // Special handling for Identify message - this completes our connection handshake
            if (message.Type == SignalingMessageType.Identify &&
                !string.IsNullOrWhiteSpace(message.TargetId))
            {
                // If we already have an ID (reconnect scenario), verify it matches
                if (!string.IsNullOrWhiteSpace(ClientId))
                {
                    if (ClientId != message.TargetId)
                    {
                        ErrorOccurred?.Invoke(this, new SignalingErrorEventArgs(
                            $"Server assigned different ID. Expected: {ClientId}, Got: {message.TargetId}"));
                    }
                }
                else
                {
                    // First connection - store server-assigned ID
                    ClientId = message.TargetId;
                }

                // Signal that we received Identify confirmation (unblocks ConnectAsync)
                _identifyReceived.TrySetResult(true);
            }

            // Notify all message handlers about this message
            try
            {
                MessageReceived?.Invoke(this, message);
            }
            catch (Exception ex)
            {
                // If a message handler throws, don't crash the receive loop
                ErrorOccurred?.Invoke(this, new SignalingErrorEventArgs("Error in message handler", ex));
            }
        }
        catch (JsonException ex)
        {
            ErrorOccurred?.Invoke(this, new SignalingErrorEventArgs("Failed to parse message", ex));
        }
    }

    /// <summary>
    /// Cleans up connection resources.
    /// </summary>
    private void CleanupConnection()
    {
        try
        {
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _receiveCts = null;

            _webSocket?.Dispose();
            _webSocket = null;

            _receiveTask = null;
            ClientId = null;

            ConnectionStateChanged?.Invoke(this, false);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
