using DeskShare.Core.Models;

namespace DeskShare.Core.Interfaces;

/// <summary>
/// Defines the contract for platform-specific input control implementations.
/// Handles keyboard and mouse input injection from remote clients.
/// Platform-specific implementations: Windows (SendInput), Linux (XTest), macOS (CGEvent).
/// </summary>
public interface IInputController : IDisposable
{
    /// <summary>
    /// Gets whether input control is currently enabled and authorized.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the current authorization state.
    /// </summary>
    InputAuthorizationState AuthorizationState { get; }

    /// <summary>
    /// Event raised when authorization state changes.
    /// </summary>
    event EventHandler<InputAuthorizationState>? AuthorizationStateChanged;

    /// <summary>
    /// Consent callback consulted by <see cref="RequestAuthorizationAsync"/>: receives the client id and
    /// returns true to allow. If not set, every request is denied.
    /// </summary>
    Func<string, bool>? AuthorizationRequested { get; set; }

    /// <summary>
    /// Requests authorization to control input from a specific client.
    /// </summary>
    /// <param name="clientId">The client requesting authorization.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if authorized, false otherwise.</returns>
    Task<bool> RequestAuthorizationAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes current authorization.
    /// </summary>
    void RevokeAuthorization();

    /// <summary>
    /// Applies an input message (keyboard or mouse event).
    /// </summary>
    /// <param name="message">The input message to apply.</param>
    /// <returns>True if the input was applied successfully, false otherwise.</returns>
    bool ApplyInput(InputMessage message);

    /// <summary>
    /// Gets current input statistics.
    /// </summary>
    InputStatistics GetStatistics();
}

/// <summary>
/// Input authorization states.
/// </summary>
public enum InputAuthorizationState
{
    /// <summary>Not authorized.</summary>
    NotAuthorized,

    /// <summary>Authorization pending (waiting for user approval).</summary>
    Pending,

    /// <summary>Authorized and active.</summary>
    Authorized,

    /// <summary>Authorization denied.</summary>
    Denied,

    /// <summary>Authorization was revoked.</summary>
    Revoked
}

/// <summary>
/// Input controller statistics.
/// </summary>
public class InputStatistics
{
    /// <summary>Total keyboard events processed.</summary>
    public long KeyboardEventsProcessed { get; set; }

    /// <summary>Total mouse events processed.</summary>
    public long MouseEventsProcessed { get; set; }

    /// <summary>Total input events rejected (not authorized).</summary>
    public long EventsRejected { get; set; }

    /// <summary>When statistics started being collected.</summary>
    public DateTime StartTime { get; set; }
}
