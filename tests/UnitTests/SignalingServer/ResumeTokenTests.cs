using Microsoft.Extensions.Logging;
using NSubstitute;
using DeskShare.SignalingServer.Services;

namespace DeskShare.UnitTests.SignalingServer;

/// <summary>
/// Resume tokens let a viewer reconnect after a lost WebSocket without the (rotated) passkey.
/// Time is injected so expiry is tested without sleeping.
/// </summary>
public class ResumeTokenTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ConnectionManager CreateManager() => new(Substitute.For<ILogger<ConnectionManager>>());

    [Fact]
    public void IssuedToken_IsBase64Url_Of32Bytes_AndUnique()
    {
        using var manager = CreateManager();

        var a = manager.IssueResumeToken("client-1", "server-1", T0);
        var b = manager.IssueResumeToken("client-1", "server-1", T0);

        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length); // 32 bytes -> 43 base64 chars without padding
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }

    [Fact]
    public void Token_CanBeConsumedOnce_AndReturnsItsOwners()
    {
        using var manager = CreateManager();
        var token = manager.IssueResumeToken("client-1", "server-1", T0);

        Assert.True(manager.TryConsumeResumeToken(token, out var clientId, out var serverId, T0.AddMinutes(1)));
        Assert.Equal("client-1", clientId);
        Assert.Equal("server-1", serverId);

        Assert.False(manager.TryConsumeResumeToken(token, out _, out _, T0.AddMinutes(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public void UnknownOrBlankToken_IsRejected(string token)
    {
        using var manager = CreateManager();
        manager.IssueResumeToken("client-1", "server-1", T0);

        Assert.False(manager.TryConsumeResumeToken(token, out var clientId, out var serverId, T0));
        Assert.Empty(clientId);
        Assert.Empty(serverId);
    }

    [Fact]
    public void Token_ExpiresAfter15Minutes()
    {
        using var manager = CreateManager();
        var stillValid = manager.IssueResumeToken("client-1", "server-1", T0);
        var expired = manager.IssueResumeToken("client-2", "server-1", T0);

        Assert.True(manager.TryConsumeResumeToken(stillValid, out _, out _, T0.AddMinutes(14).AddSeconds(59)));
        Assert.False(manager.TryConsumeResumeToken(expired, out _, out _, T0.AddMinutes(15).AddSeconds(1)));
    }

    [Fact]
    public void Rotation_NewTokenWorks_OldOneDoesNot()
    {
        using var manager = CreateManager();
        var first = manager.IssueResumeToken("client-1", "server-1", T0);

        // /resume consumes the old token and issues the next one with a fresh 15 minute lifetime.
        Assert.True(manager.TryConsumeResumeToken(first, out var clientId, out var serverId, T0.AddMinutes(10)));
        var second = manager.IssueResumeToken(clientId, serverId, T0.AddMinutes(10));

        Assert.False(manager.TryConsumeResumeToken(first, out _, out _, T0.AddMinutes(10)));
        // 24 minutes after the first issue, i.e. past the first token's lifetime but inside the rotated one's.
        Assert.True(manager.TryConsumeResumeToken(second, out _, out _, T0.AddMinutes(24)));
    }

    [Fact]
    public void ExpireResumeTokens_RemovesOnlyExpiredOnes()
    {
        using var manager = CreateManager();
        manager.IssueResumeToken("client-1", "server-1", T0);
        var fresh = manager.IssueResumeToken("client-2", "server-1", T0.AddMinutes(10));

        manager.ExpireResumeTokens(T0.AddMinutes(16));

        Assert.Equal(1, manager.ResumeTokenCount);
        Assert.True(manager.TryConsumeResumeToken(fresh, out _, out _, T0.AddMinutes(16)));
    }
}
