using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;

namespace DeskShare.Core.Signaling;

/// <summary>
/// WebSocket-based implementation of signaling for WebRTC connection establishment.
/// </summary>
public sealed class WebSocketSignaler : ISignaler
{
    private const int ReceiveBufferSize = 4096;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;
    // Replaced on every ConnectAsync: a completed TCS from the first connection would make a reconnect
    // "receive" its Identify instantly, before the server confirmed anything.
    private TaskCompletionSource<bool> _identifyReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>
    /// Raised when an established connection ends without us asking for it (server closed it, network error).
    /// Not raised after <see cref="DisconnectAsync"/>, <see cref="Dispose"/> or a failed <see cref="ConnectAsync"/>.
    /// Handlers must not block: it runs on the receive loop's thread. Reconnect by calling <see cref="ConnectAsync"/> again.
    /// </summary>
    public event EventHandler? ConnectionLost;

    /// <inheritdoc/>
    public async Task ConnectAsync(string serverUrl, string? clientId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new ArgumentNullException(nameof(serverUrl));

        if (IsConnected)
            throw new InvalidOperationException("Already connected to signaling server.");

        try
        {
            // After a lost connection the dead socket is still held here; release it before replacing it.
            // Cancelling its receive loop first keeps that loop from reporting a second "lost" event.
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _webSocket?.Dispose();
            _identifyReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // Create new WebSocket client with keep-alive to maintain connection
            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            // Build URL with optional clientId query parameter
            var url = serverUrl;
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                var separator = serverUrl.Contains('?') ? "&" : "?";
                url = $"{serverUrl}{separator}clientId={Uri.EscapeDataString(clientId)}";
            }

            // Establish WebSocket connection to signaling server
            await _webSocket.ConnectAsync(new Uri(url), cancellationToken);

            // Start background task to continuously receive messages from server
            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_webSocket, _receiveCts.Token);

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
    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        // Works on its own socket (not the _webSocket field) so a loop that is still winding down
        // never reads from the replacement socket created by a reconnect.
        var buffer = new byte[ReceiveBufferSize];
        var messageBuffer = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (webSocket.State != WebSocketState.Open)
                    break;

                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                        messageBuffer.SetLength(0);
                        ProcessMessage(json);
                    }
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

            // A cancelled token means we closed it ourselves (Disconnect/Dispose/failed Connect); anything else is a loss.
            if (!cancellationToken.IsCancellationRequested)
                ConnectionLost?.Invoke(this, EventArgs.Empty);
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
