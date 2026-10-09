using Microsoft.Extensions.Logging;
using NSubstitute;
using DeskShare.SignalingServer.Services;

namespace DeskShare.UnitTests.SignalingServer;

/// <summary>
/// ServerId ownership (trust on first use) enforced on /register so a stranger cannot take over a ServerId.
/// </summary>
public class ServerOwnershipTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ConnectionManager CreateManager() => new(Substitute.For<ILogger<ConnectionManager>>());

    [Fact]
    public void FirstRegistrationWithSecret_ClaimsServerId()
    {
        using var manager = CreateManager();

        Assert.True(manager.AuthorizeServerOwner("server-1", "secret-A", "1.2.3.4", T0));
        // The claim is now binding: another secret is rejected.
        Assert.False(manager.AuthorizeServerOwner("server-1", "secret-B", "5.6.7.8", T0));
    }

    [Fact]
    public void SameSecret_IsAcceptedRepeatedly()
    {
        using var manager = CreateManager();

        Assert.True(manager.AuthorizeServerOwner("server-1", "secret-A", "1.2.3.4", T0));
        Assert.True(manager.AuthorizeServerOwner("server-1", "secret-A", "1.2.3.4", T0.AddSeconds(45)));
    }

    [Fact]
    public void DifferentSecret_IsRejected()
    {
        using var manager = CreateManager();
        manager.AuthorizeServerOwner("server-1", "secret-A", "1.2.3.4", T0);

        Assert.False(manager.AuthorizeServerOwner("server-1", "secret-B", "5.6.7.8", T0.AddSeconds(5)));
    }

    [Fact]
    public void MissingSecret_IsRejectedOnceClaimed()
    {
        using var manager = CreateManager();
        manager.AuthorizeServerOwner("server-1", "secret-A", "1.2.3.4", T0);

        Assert.False(manager.AuthorizeServerOwner("server-1", null, "5.6.7.8", T0.AddSeconds(5)));
        Assert.False(manager.AuthorizeServerOwner("server-1", "", "5.6.7.8", T0.AddSeconds(5)));
    }

    [Fact]
    public void MissingSecret_IsAllowedWhileUnclaimed()
    {
        using var manager = CreateManager();

        // Old senders (v1.0/v1.1) do not know OwnerSecret yet and must keep working.
        Assert.True(manager.AuthorizeServerOwner("server-legacy", null, "1.2.3.4", T0));
        // An unrelated ServerId being claimed does not affect it.
        manager.AuthorizeServerOwner("server-other", "secret-A", "1.2.3.4", T0);
        Assert.True(manager.AuthorizeServerOwner("server-legacy", null, "1.2.3.4", T0));
    }

    [Fact]
    public void Claim_ExpiresTenMinutesAfterLastSeen()
    {
        using var manager = CreateManager();
        manager.AuthorizeServerOwner("server-1", "secret-A", "1.2.3.4", T0);

        // Still within 10 minutes: claim stays.
        manager.ExpireOwnerClaims(T0.AddMinutes(9));
        Assert.False(manager.AuthorizeServerOwner("server-1", "secret-B", "5.6.7.8", T0.AddMinutes(9)));

        // 11 minutes after the last successful registration (T0): the claim lapses and can be taken over,
        // e.g. by a reinstall that lost its key file.
        manager.ExpireOwnerClaims(T0.AddMinutes(11));
        Assert.True(manager.AuthorizeServerOwner("server-1", "secret-B", "5.6.7.8", T0.AddMinutes(11)));
    }

    [Fact]
    public void SuccessfulRegistration_RefreshesLastSeen()
    {
        using var manager = CreateManager();
        manager.AuthorizeServerOwner("server-1", "secret-A", "1.2.3.4", T0);
        manager.AuthorizeServerOwner("server-1", "secret-A", "1.2.3.4", T0.AddMinutes(8));

        // 12 min after the first but only 4 after the refresh: still claimed.
        manager.ExpireOwnerClaims(T0.AddMinutes(12));
        Assert.False(manager.AuthorizeServerOwner("server-1", "secret-B", "5.6.7.8", T0.AddMinutes(12)));
    }
}
