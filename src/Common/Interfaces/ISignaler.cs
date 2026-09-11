using DeskShare.Common.Models;

namespace DeskShare.Common.Interfaces;

/// <summary>
/// Defines the contract for WebRTC signaling operations.
/// Handles exchange of SDP offers/answers and ICE candidates between peers.
/// </summary>
public interface ISignaler : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the signaler is currently connected to the signaling server.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the unique identifier of this client.
    /// </summary>
    string? ClientId { get; }

    /// <summary>
    /// Event raised when a signaling message is received from a peer.
    /// </summary>
    event EventHandler<SignalingMessage>? MessageReceived;

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// Event raised when an error occurs during signaling.
    /// </summary>
    event EventHandler<SignalingErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// Connects to the signaling server asynchronously.
    /// </summary>
    /// <param name="serverUrl">WebSocket URL of the signaling server (e.g., wss://server.com/signal).</param>
    /// <param name="clientId">Optional client identifier. If null, server will assign one.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Task that completes when connection is established.</returns>
    /// <exception cref="ArgumentNullException">Thrown when serverUrl is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when already connected.</exception>
    /// <exception cref="SignalingException">Thrown when connection fails.</exception>
    Task ConnectAsync(string serverUrl, string? clientId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the signaling server asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Task that completes when disconnection is complete.</returns>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a signaling message to a peer asynchronously.
    /// </summary>
    /// <param name="message">Message to send.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Task that completes when message is sent.</returns>
    /// <exception cref="ArgumentNullException">Thrown when message is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when not connected.</exception>
    /// <exception cref="SignalingException">Thrown when send fails.</exception>
    Task SendAsync(SignalingMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Event arguments for signaling errors.
/// </summary>
public sealed class SignalingErrorEventArgs : EventArgs
{
    /// <summary>
    /// Error message describing what went wrong.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Optional exception that caused the error.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalingErrorEventArgs"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="exception">Optional exception.</param>
    public SignalingErrorEventArgs(string message, Exception? exception = null)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Exception = exception;
    }
}

/// <summary>
/// Exception thrown when signaling operations fail.
/// </summary>
public sealed class SignalingException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SignalingException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    public SignalingException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalingException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="innerException">Inner exception.</param>
    public SignalingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
