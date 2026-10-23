using DeskShare.Core.Auth;
using DeskShare.SignalingServer.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DeskShare.UnitTests.SignalingServer;

/// <summary>
/// The signaling server side of the permanent password: it stores only the password's authKey, accepts it as an
/// alternative to the current passkey's authKey, and locks a ServerId after repeated failures.
/// </summary>
public class PermanentPasswordAuthTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string ServerId = "server-123456789";
    private const string PasskeyAuth = "PASSKEYAUTH000000000000000000000000000000000";
    private const string PermanentAuth = "PERMANENTAUTH00000000000000000000000000000000";

    private static ConnectionManager CreateManager() => new(Substitute.For<ILogger<ConnectionManager>>());

    private static void Register(ConnectionManager m, string? permanentAuthKey) => m.RegisterServer(new ServerRegistrationMessage
    {
        ServerId = ServerId,
        Passkey = PasskeyAuth,
        PermanentAuthKey = permanentAuthKey,
        OwnerSecret = "secret-A",
        ValidTo = DateTime.UtcNow.AddMinutes(5),
        RemoteControlEnabled = false,
        TrustClientPermanent = false
    });

    private static bool Authenticate(ConnectionManager m, string credential, string clientId = "client-1") =>
        m.AuthenticateClient(new ClientAuthenticationMessage { ServerId = ServerId, ClientId = clientId, Passkey = credential }).Success;

    [Fact]
    public void Authenticate_AcceptsThePermanentKey()
    {
        using var m = CreateManager();
        Register(m, PermanentAuth);

        Assert.True(Authenticate(m, PermanentAuth));
    }

    [Fact]
    public void Authenticate_StillAcceptsThePasskey_AndRejectsAnythingElse()
    {
        using var m = CreateManager();
        Register(m, PermanentAuth);

        Assert.False(Authenticate(m, "WRONG000000000000000000000000000000000000000"));
        Assert.True(Authenticate(m, PasskeyAuth));
    }

    [Fact]
    public void ReRegisteringWithoutThePermanentKey_RemovesIt()
    {
        using var m = CreateManager();
        Register(m, PermanentAuth);
        Register(m, null); // the owner removed the password

        Assert.False(Authenticate(m, PermanentAuth));
        Assert.True(Authenticate(m, PasskeyAuth));
    }

    [Fact]
    public void Lockout_StartsAfterFiveFailures_NeedsAFreshFiveEachTime_DoublesUpTo15Minutes_AndResetsOnSuccess()
    {
        using var m = CreateManager();
        m.SetPermanentPasswordRegistered(ServerId, true);

        for (var i = 0; i < ConnectionManager.PasswordLockoutThreshold - 1; i++)
            m.RecordPasswordAttempt(ServerId, success: false, T0);
        Assert.False(m.IsPasswordLockedOut(ServerId, T0)); // 4 failures: still open
        m.RecordPasswordAttempt(ServerId, success: false, T0);

        var now = T0;
        var lockouts = new List<double>();
        for (var round = 0; round < 6; round++)
        {
            if (round > 0)
            {
                // After a lockout, one failure is not enough to lock again: it takes a fresh window of 5.
                for (var i = 0; i < ConnectionManager.PasswordLockoutThreshold - 1; i++)
                    m.RecordPasswordAttempt(ServerId, success: false, now);
                Assert.False(m.IsPasswordLockedOut(ServerId, now));
                m.RecordPasswordAttempt(ServerId, success: false, now);
            }
            var minutes = 0;
            while (m.IsPasswordLockedOut(ServerId, now.AddMinutes(minutes))) minutes++;
            lockouts.Add(minutes);
            now = now.AddMinutes(minutes); // the next attempts come right after the lockout
        }
        Assert.Equal(new double[] { 1, 2, 4, 8, 15, 15 }, lockouts);

        m.RecordPasswordAttempt(ServerId, success: true, now);
        for (var i = 0; i < ConnectionManager.PasswordLockoutThreshold; i++)
            m.RecordPasswordAttempt(ServerId, success: false, now);
        var after = 0;
        while (m.IsPasswordLockedOut(ServerId, now.AddMinutes(after))) after++;
        Assert.Equal(1, after); // a success also resets the doubling
    }

    [Fact]
    public void PasskeyOnly_StillAcceptsThePasskey_ButNeverComparesThePermanentKey()
    {
        using var m = CreateManager();
        Register(m, PermanentAuth);
        bool Auth(string credential) => m.AuthenticateClient(new ClientAuthenticationMessage
        {
            ServerId = ServerId, ClientId = "client-1", Passkey = credential, PasskeyOnly = true
        }).Success;

        Assert.False(Auth(PermanentAuth));
        Assert.True(Auth(PasskeyAuth));
    }

    [Fact]
    public void Lockout_OnlyAppliesWhileAPermanentPasswordIsRegistered()
    {
        using var m = CreateManager();
        for (var i = 0; i < 10; i++) m.RecordPasswordAttempt(ServerId, success: false, T0);
        Assert.False(m.IsPasswordLockedOut(ServerId, T0)); // passkey-only server: per-IP limits only

        m.SetPermanentPasswordRegistered(ServerId, true);
        for (var i = 0; i < 5; i++) m.RecordPasswordAttempt(ServerId, success: false, T0);
        Assert.True(m.IsPasswordLockedOut(ServerId, T0));

        m.SetPermanentPasswordRegistered(ServerId, false); // password removed
        Assert.False(m.IsPasswordLockedOut(ServerId, T0));
    }
}
