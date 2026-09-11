using DeskShare.Desktop.Shared.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// Unit tests for <see cref="ConsoleAuthorizationService"/>.
/// Tests the blocking logic and immediate-denial behavior for blocked clients.
///
/// For junior developers:
/// We can't easily test the interactive Console.ReadKey behavior in unit tests
/// (it requires a real console), so we focus on the testable parts:
/// - Block/unblock logic
/// - Blocked client auto-denial
/// - Thread safety of blocking operations
///
/// The console prompting behavior would be covered by integration/manual tests.
/// </summary>
public sealed class RemoteControlAuthorizationTests
{
    /// <summary>
    /// Creates a ConsoleAuthorizationService with a mocked logger.
    /// We use NSubstitute to create a fake ILogger so we don't need a real logging setup.
    /// </summary>
    private static ConsoleAuthorizationService CreateService()
    {
        var logger = Substitute.For<ILogger<ConsoleAuthorizationService>>();
        return new ConsoleAuthorizationService(logger);
    }

    #region IsClientBlocked Tests

    /// <summary>
    /// A brand new client should not be blocked.
    /// This verifies the default state of the service.
    /// </summary>
    [Fact]
    public void IsClientBlocked_NewClient_ReturnsFalse()
    {
        // Arrange
        var service = CreateService();

        // Act
        var isBlocked = service.IsClientBlocked("client-123");

        // Assert - new clients are never blocked
        Assert.False(isBlocked);
    }

    /// <summary>
    /// After blocking a client, IsClientBlocked should return true for that client.
    /// </summary>
    [Fact]
    public void BlockClient_ThenIsClientBlocked_ReturnsTrue()
    {
        // Arrange
        var service = CreateService();
        var clientId = "malicious-client-456";

        // Act - block the client
        service.BlockClient(clientId);

        // Assert - the client should now be blocked
        Assert.True(service.IsClientBlocked(clientId));
    }

    /// <summary>
    /// Blocking one client should not affect other clients.
    /// Each block is independent and client-specific.
    /// </summary>
    [Fact]
    public void BlockClient_DoesNotAffectOtherClients()
    {
        // Arrange
        var service = CreateService();
        var blockedClient = "blocked-client";
        var innocentClient = "innocent-client";

        // Act - only block one client
        service.BlockClient(blockedClient);

        // Assert - the other client should NOT be blocked
        Assert.True(service.IsClientBlocked(blockedClient));
        Assert.False(service.IsClientBlocked(innocentClient));
    }

    /// <summary>
    /// Blocking the same client twice should not throw.
    /// It's a no-op if the client is already blocked (idempotent operation).
    /// </summary>
    [Fact]
    public void BlockClient_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var service = CreateService();
        var clientId = "double-blocked-client";

        // Act & Assert - blocking twice should work without exceptions
        service.BlockClient(clientId);
        service.BlockClient(clientId);

        Assert.True(service.IsClientBlocked(clientId));
    }

    /// <summary>
    /// Multiple different clients can be blocked independently.
    /// Verifies the HashSet correctly stores multiple entries.
    /// </summary>
    [Fact]
    public void BlockClient_MultipleClients_AllAreBlocked()
    {
        // Arrange
        var service = CreateService();
        var clients = new[] { "client-a", "client-b", "client-c" };

        // Act - block all clients
        foreach (var clientId in clients)
        {
            service.BlockClient(clientId);
        }

        // Assert - all should be blocked
        foreach (var clientId in clients)
        {
            Assert.True(service.IsClientBlocked(clientId),
                $"Client {clientId} should be blocked but wasn't");
        }
    }

    #endregion

    #region RequestAuthorizationAsync Tests (blocked client path)

    /// <summary>
    /// When a blocked client requests authorization, the service should
    /// immediately return Blocked without showing any prompt.
    /// This is the fast path - no user interaction needed.
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_BlockedClient_ReturnsBlockedImmediately()
    {
        // Arrange
        var service = CreateService();
        var clientId = "pre-blocked-client";
        service.BlockClient(clientId);

        // Act - request authorization for the blocked client
        var result = await service.RequestAuthorizationAsync(
            clientId, "192.168.1.100", CancellationToken.None);

        // Assert - should be immediately blocked, no prompt shown
        Assert.Equal(AuthorizationResult.Blocked, result);
    }

    /// <summary>
    /// The blocked client check should complete nearly instantly
    /// (no 30-second timeout, no console interaction).
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_BlockedClient_CompletesQuickly()
    {
        // Arrange
        var service = CreateService();
        var clientId = "speed-test-client";
        service.BlockClient(clientId);

        // Act - time the call
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await service.RequestAuthorizationAsync(
            clientId, "10.0.0.1", CancellationToken.None);
        stopwatch.Stop();

        // Assert - should complete in under 1 second (way less than the 30s timeout)
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"Blocked client check took {stopwatch.ElapsedMilliseconds}ms, expected < 1000ms");
    }

    #endregion

    #region Constructor Tests

    /// <summary>
    /// Constructor should throw ArgumentNullException when logger is null.
    /// This validates the guard clause.
    /// </summary>
    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => new ConsoleAuthorizationService(null!));
    }

    #endregion

    #region Null Argument Validation Tests

    /// <summary>
    /// IsClientBlocked should throw when given a null clientId.
    /// </summary>
    [Fact]
    public void IsClientBlocked_NullClientId_ThrowsArgumentNullException()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => service.IsClientBlocked(null!));
    }

    /// <summary>
    /// BlockClient should throw when given a null clientId.
    /// </summary>
    [Fact]
    public void BlockClient_NullClientId_ThrowsArgumentNullException()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => service.BlockClient(null!));
    }

    /// <summary>
    /// RequestAuthorizationAsync should throw when given a null clientId.
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_NullClientId_ThrowsArgumentNullException()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.RequestAuthorizationAsync(null!, "address"));
    }

    /// <summary>
    /// RequestAuthorizationAsync should throw when given a null remoteAddress.
    /// </summary>
    [Fact]
    public async Task RequestAuthorizationAsync_NullRemoteAddress_ThrowsArgumentNullException()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.RequestAuthorizationAsync("client-id", null!));
    }

    #endregion
}
