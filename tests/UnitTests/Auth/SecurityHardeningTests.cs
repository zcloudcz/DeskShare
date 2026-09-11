using DeskShare.Core.Auth;
using DeskShare.SignalingServer.Services;
using DeskShare.Turn;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DeskShare.UnitTests.Auth;

public class SecurityHardeningTests
{
    #region Constant-Time Passkey Comparison

    [Fact]
    public void ValidatePasskey_CorrectPasskey_ReturnsTrue()
    {
        var serverId = "TEST-SERVER-001";
        var timestamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var passkey = AuthenticationService.GeneratePasskey(serverId, timestamp);

        Assert.True(AuthenticationService.ValidatePasskey(serverId, passkey, timestamp));
    }

    [Fact]
    public void ValidatePasskey_LowercasePasskey_ReturnsTrue()
    {
        var serverId = "TEST-SERVER-001";
        var timestamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var passkey = AuthenticationService.GeneratePasskey(serverId, timestamp).ToLowerInvariant();

        Assert.True(AuthenticationService.ValidatePasskey(serverId, passkey, timestamp));
    }

    [Fact]
    public void ValidatePasskey_WrongPasskey_ReturnsFalse()
    {
        var serverId = "TEST-SERVER-001";
        var timestamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(AuthenticationService.ValidatePasskey(serverId, "XXXXXXXXX", timestamp));
    }

    [Fact]
    public void ValidatePasskey_EmptyInputs_ReturnsFalse()
    {
        Assert.False(AuthenticationService.ValidatePasskey("", "ABC", DateTime.UtcNow));
        Assert.False(AuthenticationService.ValidatePasskey("server", "", DateTime.UtcNow));
        Assert.False(AuthenticationService.ValidatePasskey(null!, "ABC", DateTime.UtcNow));
    }

    #endregion

    #region HMAC Requirement Enforcement

    [Fact]
    public void AuthenticateClient_RequireHmac_RejectsLegacyClient()
    {
        var logger = Substitute.For<ILogger<ConnectionManager>>();
        var cm = new ConnectionManager(logger);
        cm.Configure(requireHmacSignature: true);

        var serverId = "server-test";
        var now = DateTime.UtcNow;
        var passkey = AuthenticationService.GeneratePasskey(serverId, now);

        cm.RegisterServer(new ServerRegistrationMessage
        {
            ServerId = serverId,
            Passkey = passkey,
            ValidTo = now.AddMinutes(5),
            RemoteControlEnabled = false,
            TrustClientPermanent = false
        });

        var result = cm.AuthenticateClient(new ClientAuthenticationMessage
        {
            ServerId = serverId,
            Passkey = passkey,
            ClientId = "client-1"
            // No Signature = legacy client
        });

        Assert.False(result.Success);
        Assert.Contains("HMAC signature is required", result.ErrorMessage);
    }

    [Fact]
    public void AuthenticateClient_NoHmacRequired_AllowsLegacyClient()
    {
        var logger = Substitute.For<ILogger<ConnectionManager>>();
        var cm = new ConnectionManager(logger);
        cm.Configure(requireHmacSignature: false);

        var serverId = "server-test";
        var now = DateTime.UtcNow;
        var passkey = AuthenticationService.GeneratePasskey(serverId, now);

        cm.RegisterServer(new ServerRegistrationMessage
        {
            ServerId = serverId,
            Passkey = passkey,
            ValidTo = now.AddMinutes(5),
            RemoteControlEnabled = false,
            TrustClientPermanent = false
        });

        var result = cm.AuthenticateClient(new ClientAuthenticationMessage
        {
            ServerId = serverId,
            Passkey = passkey,
            ClientId = "client-1"
        });

        Assert.True(result.Success);
    }

    #endregion

    #region Thread-Safe Message Counters

    [Fact]
    public void MessageCounters_ConcurrentAccess_CorrectCount()
    {
        var logger = Substitute.For<ILogger<ConnectionManager>>();
        var cm = new ConnectionManager(logger);
        var ws = Substitute.For<System.Net.WebSockets.WebSocket>();

        cm.RegisterClient("client-1", ws);

        var tasks = Enumerable.Range(0, 1000)
            .Select(_ => Task.Run(() => cm.RecordMessageReceived("client-1")));

        Task.WaitAll(tasks.ToArray());

        var stats = cm.GetStatistics();
        Assert.Equal(1000, stats.TotalMessagesReceived);
    }

    #endregion

    #region TURN Authenticator Thread Safety

    [Fact]
    public void TurnAuthenticator_ConcurrentAddRemove_NoException()
    {
        var auth = new TurnAuthenticator("test-realm");

        var addTasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() => auth.AddUser($"user{i}", $"pass{i}")));

        Task.WaitAll(addTasks.ToArray());
        Assert.Equal(100, auth.UserCount);

        var removeTasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() => auth.RemoveUser($"user{i}")));

        Task.WaitAll(removeTasks.ToArray());
        Assert.Equal(0, auth.UserCount);
    }

    #endregion

    #region VideoFrame Pooled Buffers

    [Fact]
    public void VideoFrame_PooledConstructor_DisposesCleanly()
    {
        var y = System.Buffers.ArrayPool<byte>.Shared.Rent(100);
        var u = System.Buffers.ArrayPool<byte>.Shared.Rent(25);
        var v = System.Buffers.ArrayPool<byte>.Shared.Rent(25);

        var frame = new DeskShare.Core.Models.VideoFrame(
            10, 10, y, u, v, 10, 5, 5, DateTime.UtcNow,
            100, 25, 25);

        Assert.Equal(100, frame.YPlaneLength);
        Assert.Equal(25, frame.UPlaneLength);

        frame.Dispose();
        // No exception = buffers returned to pool
    }

    [Fact]
    public void VideoFrame_NonPooled_DisposesWithoutPoolReturn()
    {
        var frame = new DeskShare.Core.Models.VideoFrame(
            10, 10,
            new byte[100], new byte[25], new byte[25],
            10, 5, 5, DateTime.UtcNow);

        Assert.Equal(100, frame.YPlaneLength);
        frame.Dispose();
    }

    #endregion

    #region Broadcast Double Enumeration Fix

    [Fact]
    public async Task BroadcastAsync_NoRecipients_DoesNotThrow()
    {
        var logger = Substitute.For<ILogger<ConnectionManager>>();
        var cm = new ConnectionManager(logger);

        var msg = new DeskShare.Core.Models.SignalingMessage
        {
            Type = DeskShare.Core.Models.SignalingMessageType.Ping,
            SenderId = "sender-1"
        };

        await cm.BroadcastAsync("sender-1", msg);
    }

    #endregion
}
