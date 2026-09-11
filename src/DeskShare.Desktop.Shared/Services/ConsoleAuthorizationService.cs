using Microsoft.Extensions.Logging;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Console-based implementation of <see cref="IRemoteControlAuthorizationService"/>.
/// Used by ScreenSenderApp (a console application) to prompt the user for authorization
/// via standard console input/output.
///
/// For junior developers:
/// This service prints a prompt to the console window asking the user to press a key:
///   Y = Allow remote control
///   N = Deny remote control
///   B = Block client for the rest of this session
/// If the user doesn't press anything within 30 seconds, the request is automatically denied.
///
/// The blocking feature uses an in-memory HashSet, so blocks only last until the app restarts.
/// </summary>
public class ConsoleAuthorizationService : IRemoteControlAuthorizationService
{
    /// <summary>
    /// Thread-safe set of client IDs that have been blocked for this session.
    /// We use lock-based synchronization via the HashSet itself since concurrent
    /// access is possible from multiple WebRTC connections.
    /// </summary>
    private readonly HashSet<string> _blockedClients = new();

    /// <summary>
    /// Lock object to synchronize access to _blockedClients.
    /// </summary>
    private readonly object _blockLock = new();

    /// <summary>
    /// Logger instance for diagnostic output.
    /// </summary>
    private readonly ILogger<ConsoleAuthorizationService> _logger;

    /// <summary>
    /// How long to wait for user input before auto-denying (in seconds).
    /// </summary>
    private const int TimeoutSeconds = 30;

    /// <summary>
    /// Creates a new instance of the console authorization service.
    /// </summary>
    /// <param name="logger">Logger for recording authorization decisions and diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when logger is null.</exception>
    public ConsoleAuthorizationService(ILogger<ConsoleAuthorizationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Prompts the user in the console to allow, deny, or block a remote control request.
    /// Auto-denies after <see cref="TimeoutSeconds"/> seconds if no response is given.
    /// </summary>
    /// <param name="clientId">Unique identifier of the requesting client.</param>
    /// <param name="remoteAddress">Network address of the requesting client.</param>
    /// <param name="cancellationToken">Token to cancel the request externally.</param>
    /// <returns>The user's authorization decision.</returns>
    public async Task<AuthorizationResult> RequestAuthorizationAsync(
        string clientId,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        // Check if the client is already blocked - no need to prompt the user
        if (IsClientBlocked(clientId))
        {
            _logger.LogWarning(
                "Client {ClientId} from {RemoteAddress} is blocked for this session, auto-denying",
                clientId, remoteAddress);
            return AuthorizationResult.Blocked;
        }

        // Display the authorization prompt to the console
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              REMOTE CONTROL AUTHORIZATION REQUEST            ║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  Client ID: {clientId,-47} ║");
        Console.WriteLine($"║  Address:   {remoteAddress,-47} ║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
        Console.WriteLine("║  [Y] Allow   [N] Deny   [B] Block (deny + block for session)║");
        Console.WriteLine($"║  Auto-deny in {TimeoutSeconds} seconds if no response.                    ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.Write("Your choice: ");

        _logger.LogInformation(
            "Authorization prompt shown for client {ClientId} from {RemoteAddress}",
            clientId, remoteAddress);

        // Create a combined cancellation source: timeout + external cancellation
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, cancellationToken);

        try
        {
            // Read user input on a background thread because Console.ReadKey blocks
            // the calling thread, and we need to support timeout via cancellation.
            var keyResult = await Task.Run(() =>
            {
                // Poll for key availability so we can check cancellation
                while (!Console.KeyAvailable)
                {
                    // Check if we've been cancelled (timeout or external)
                    linkedCts.Token.ThrowIfCancellationRequested();
                    Thread.Sleep(100); // Small delay to avoid busy-waiting
                }

                return Console.ReadKey(intercept: true);
            }, linkedCts.Token);

            Console.WriteLine(keyResult.KeyChar);

            // Map the pressed key to an authorization result
            var result = char.ToUpperInvariant(keyResult.KeyChar) switch
            {
                'Y' => AuthorizationResult.Allowed,
                'N' => AuthorizationResult.Denied,
                'B' => AuthorizationResult.Blocked,
                _ => AuthorizationResult.Denied // Unknown key = deny for safety
            };

            _logger.LogInformation(
                "User responded {Result} for client {ClientId}",
                result, clientId);

            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timeout expired - auto-deny for security (fail-closed approach)
            Console.WriteLine();
            Console.WriteLine("[Authorization] Timed out - automatically denied.");
            _logger.LogWarning(
                "Authorization timed out after {Timeout}s for client {ClientId}, auto-denying",
                TimeoutSeconds, clientId);
            return AuthorizationResult.Timeout;
        }
        catch (OperationCanceledException)
        {
            // External cancellation - also deny
            Console.WriteLine();
            Console.WriteLine("[Authorization] Cancelled - automatically denied.");
            _logger.LogWarning(
                "Authorization cancelled externally for client {ClientId}, denying",
                clientId);
            return AuthorizationResult.Denied;
        }
    }

    /// <summary>
    /// Checks if a client ID has been blocked during this session.
    /// Thread-safe: multiple WebRTC connections may call this concurrently.
    /// </summary>
    /// <param name="clientId">The client identifier to check.</param>
    /// <returns>True if the client is blocked, false otherwise.</returns>
    public bool IsClientBlocked(string clientId)
    {
        ArgumentNullException.ThrowIfNull(clientId);

        lock (_blockLock)
        {
            return _blockedClients.Contains(clientId);
        }
    }

    /// <summary>
    /// Blocks a client ID for the rest of the application session.
    /// Once blocked, all future authorization requests from this client are auto-denied.
    /// Thread-safe: uses a lock to protect the internal HashSet.
    /// </summary>
    /// <param name="clientId">The client identifier to block.</param>
    public void BlockClient(string clientId)
    {
        ArgumentNullException.ThrowIfNull(clientId);

        lock (_blockLock)
        {
            // HashSet.Add returns false if already present, but that's fine -
            // blocking an already-blocked client is a no-op.
            if (_blockedClients.Add(clientId))
            {
                _logger.LogWarning(
                    "Client {ClientId} has been blocked for the rest of this session",
                    clientId);
            }
            else
            {
                _logger.LogDebug(
                    "Client {ClientId} was already blocked", clientId);
            }
        }
    }
}
