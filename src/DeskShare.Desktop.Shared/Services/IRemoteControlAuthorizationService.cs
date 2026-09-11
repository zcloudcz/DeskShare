namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Abstraction for remote control authorization dialog.
/// Platform implementations show a modal dialog asking the user to allow/deny
/// remote control from a specific client.
///
/// For junior developers:
/// When someone tries to remotely control your PC, this service is responsible
/// for asking you "Do you want to allow this?" and remembering your decision.
/// Different platforms (console, WPF, Avalonia) provide their own UI for this prompt.
/// </summary>
public interface IRemoteControlAuthorizationService
{
    /// <summary>
    /// Shows an authorization dialog to the user and returns their decision.
    /// If the user does not respond within the timeout period, the request is auto-denied.
    /// </summary>
    /// <param name="clientId">Unique identifier of the remote client requesting control.</param>
    /// <param name="remoteAddress">Network address of the remote client (for display purposes).</param>
    /// <param name="cancellationToken">Token to cancel the authorization request.</param>
    /// <returns>
    /// The user's authorization decision:
    /// - Allowed: user approved remote control
    /// - Denied: user explicitly denied
    /// - Blocked: user denied and wants to block this client for the session
    /// - Timeout: user didn't respond in time (treated as denial)
    /// </returns>
    Task<AuthorizationResult> RequestAuthorizationAsync(
        string clientId,
        string remoteAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a client ID has been blocked for this session.
    /// Blocked clients are immediately denied without showing a dialog.
    /// </summary>
    /// <param name="clientId">The client identifier to check.</param>
    /// <returns>True if the client is blocked, false otherwise.</returns>
    bool IsClientBlocked(string clientId);

    /// <summary>
    /// Blocks a client ID for the remainder of this application session.
    /// Future authorization requests from this client will be immediately denied.
    /// </summary>
    /// <param name="clientId">The client identifier to block.</param>
    void BlockClient(string clientId);
}

/// <summary>
/// Represents the possible outcomes of a remote control authorization request.
/// </summary>
public enum AuthorizationResult
{
    /// <summary>
    /// User explicitly allowed remote control.
    /// </summary>
    Allowed,

    /// <summary>
    /// User explicitly denied remote control.
    /// </summary>
    Denied,

    /// <summary>
    /// User denied remote control AND blocked the client for the rest of the session.
    /// No further authorization dialogs will be shown for this client.
    /// </summary>
    Blocked,

    /// <summary>
    /// User did not respond within the timeout period.
    /// Treated as a denial for security (fail-closed).
    /// </summary>
    Timeout
}
