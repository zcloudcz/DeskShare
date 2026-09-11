using DeskShare.Common.Models;

namespace DeskShare.Common.Interfaces;

/// <summary>
/// Defines the contract for remote input control.
/// Handles application of remote mouse and keyboard inputs to the local system.
/// </summary>
/// <remarks>
/// SECURITY WARNING: Implementations must enforce proper authorization and user consent
/// before applying any remote inputs. All input commands must be validated and rate-limited
/// to prevent abuse.
/// </remarks>
public interface IInputController : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether remote input control is currently enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the current authorization state for remote input control.
    /// </summary>
    InputAuthorizationState AuthorizationState { get; }

    /// <summary>
    /// Event raised when authorization state changes.
    /// </summary>
    event EventHandler<InputAuthorizationState>? AuthorizationStateChanged;

    /// <summary>
    /// Requests user authorization to enable remote input control.
    /// This must show a clear dialog to the user explaining what will be controlled.
    /// </summary>
    /// <param name="requestingClientId">Identifier of the client requesting control.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes with true if authorized, false if denied.</returns>
    /// <exception cref="ArgumentNullException">Thrown when requestingClientId is null.</exception>
    Task<bool> RequestAuthorizationAsync(string requestingClientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes current authorization and disables remote input control.
    /// User can call this at any time to immediately stop remote control.
    /// </summary>
    void RevokeAuthorization();

    /// <summary>
    /// Applies a remote input command to the local system.
    /// </summary>
    /// <param name="input">Input message to apply.</param>
    /// <returns>True if input was applied successfully, false if rejected or failed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when authorization is not granted.</exception>
    /// <remarks>
    /// This method validates the input message and applies rate limiting before execution.
    /// Invalid or suspicious inputs are rejected and logged for security audit.
    /// </remarks>
    bool ApplyInput(InputMessage input);

    /// <summary>
    /// Gets statistics about remote input operations.
    /// </summary>
    /// <returns>Statistics object containing input counts and authorization information.</returns>
    InputControllerStatistics GetStatistics();
}

/// <summary>
/// Authorization state for remote input control.
/// </summary>
public enum InputAuthorizationState
{
    /// <summary>
    /// No authorization requested or granted.
    /// </summary>
    NotAuthorized,

    /// <summary>
    /// Authorization request is pending user response.
    /// </summary>
    Pending,

    /// <summary>
    /// Authorization granted and remote control is active.
    /// </summary>
    Authorized,

    /// <summary>
    /// Authorization was denied by user.
    /// </summary>
    Denied,

    /// <summary>
    /// Authorization was revoked (either by user or timeout).
    /// </summary>
    Revoked
}

/// <summary>
/// Contains statistics about input controller operations.
/// </summary>
public sealed class InputControllerStatistics
{
    /// <summary>
    /// Total number of input messages received.
    /// </summary>
    public long TotalInputsReceived { get; set; }

    /// <summary>
    /// Total number of input messages successfully applied.
    /// </summary>
    public long TotalInputsApplied { get; set; }

    /// <summary>
    /// Total number of input messages rejected.
    /// </summary>
    public long TotalInputsRejected { get; set; }

    /// <summary>
    /// Timestamp when current authorization was granted.
    /// </summary>
    public DateTime? AuthorizationGrantedAt { get; set; }

    /// <summary>
    /// Client ID that was granted authorization.
    /// </summary>
    public string? AuthorizedClientId { get; set; }

    /// <summary>
    /// Duration of current authorization session.
    /// </summary>
    public TimeSpan? SessionDuration { get; set; }
}
