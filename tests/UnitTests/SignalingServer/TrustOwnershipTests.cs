using DeskShare.Core.Auth;
using DeskShare.SignalingServer.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DeskShare.UnitTests.SignalingServer;

/// <summary>
/// Permanent trust belongs to the owner secret that was in force when it was granted. Owner claims lapse after
/// 10 minutes, so someone else can temporarily own a ServerId; their trust must not outlive that and the real
/// owner's trust must not carry over to them.
/// </summary>
public class TrustOwnershipTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string ServerId = "server-123456789";

    private static ConnectionManager CreateManager() => new(Substitute.For<ILogger<ConnectionManager>>());

    private static void Register(ConnectionManager m, string secret, string passkey, DateTime at)
    {
        Assert.True(m.AuthorizeServerOwner(ServerId, secret, "1.2.3.4", at));
        m.RegisterServer(new ServerRegistrationMessage
        {
            ServerId = ServerId,
            Passkey = passkey,
            OwnerSecret = secret,
            ValidTo = DateTime.UtcNow.AddMinutes(5),
            RemoteControlEnabled = false,
            TrustClientPermanent = true
        });
    }

    private static bool Authenticate(ConnectionManager m, string clientId, string passkey) =>
        m.AuthenticateClient(new ClientAuthenticationMessage { ServerId = ServerId, ClientId = clientId, Passkey = passkey }).Success;

    [Fact]
    public void TrustGrantedByAnOwner_IsNotHonouredUnderADifferentOwner_ButReturnsWithTheRealOwner()
    {
        using var manager = CreateManager();
        Register(manager, "secret-A", "OWNERAPK2", T0);
        Assert.True(Authenticate(manager, "client-1", "OWNERAPK2")); // passkey login; trust is granted to client-1
        Assert.True(manager.IsTrustedClient(ServerId, "client-1"));

        // The real owner goes away; the claim lapses and a stranger takes the ID.
        manager.ExpireOwnerClaims(T0.AddMinutes(11));
        Register(manager, "secret-B", "STRANGER2", T0.AddMinutes(11));

        Assert.False(manager.IsTrustedClient(ServerId, "client-1"));
        Assert.False(Authenticate(manager, "client-1", "WRONGPASS")); // no passkey bypass for the old trust

        // The real owner comes back with the same secret: the trust it granted applies again.
        manager.ExpireOwnerClaims(T0.AddMinutes(22));
        Register(manager, "secret-A", "OWNERAPK3", T0.AddMinutes(22));
        Assert.True(manager.IsTrustedClient(ServerId, "client-1"));
        Assert.True(Authenticate(manager, "client-1", "WRONGPASS"));
    }

    [Fact]
    public void SessionOfALapsedOwner_IsGone_SoItsPasskeyCannotBeUsedUnderTheNextOwner()
    {
        using var manager = CreateManager();
        Register(manager, "secret-A", "OWNERAPK2", T0);

        manager.ExpireOwnerClaims(T0.AddMinutes(11));

        // The claim lapsed: the old session must not linger for whoever claims the ID next.
        Assert.False(Authenticate(manager, "client-1", "OWNERAPK2"));

        // The next owner has authorized but not stored its registration yet: still nothing to authenticate against.
        Assert.True(manager.AuthorizeServerOwner(ServerId, "secret-B", "5.6.7.8", T0.AddMinutes(11)));
        Assert.False(Authenticate(manager, "client-1", "OWNERAPK2"));
        Assert.False(manager.IsStillOwnedBy(ServerId, capturedOwner: null)); // owner changed from "nobody" to B
    }

    [Fact]
    public void ClaimingAnUnownedId_DropsALeftoverSession_BeforeTheNewOwnerIsVisible()
    {
        using var manager = CreateManager();
        // A session without any owner claim (e.g. left behind by an earlier owner).
        manager.RegisterServer(new ServerRegistrationMessage
        {
            ServerId = ServerId, Passkey = "LEFTOVER2", ValidTo = DateTime.UtcNow.AddMinutes(5),
            RemoteControlEnabled = false, TrustClientPermanent = false
        });

        Assert.True(manager.AuthorizeServerOwner(ServerId, "secret-B", "5.6.7.8", T0));

        Assert.False(Authenticate(manager, "client-1", "LEFTOVER2"));
    }

    [Fact]
    public void RegistrationThatLostTheIdToAnotherOwner_CannotOverwriteThatOwnersSession()
    {
        using var manager = CreateManager();
        Register(manager, "secret-B", "OWNERBPK2", T0);

        // A request that was authorized earlier under another owner arrives late and tries to store its session.
        var late = manager.RegisterServer(new ServerRegistrationMessage
        {
            ServerId = ServerId, Passkey = "LATEPASS2", OwnerSecret = "secret-A", ValidTo = DateTime.UtcNow.AddMinutes(5),
            RemoteControlEnabled = false, TrustClientPermanent = false
        });

        Assert.False(late.Success);
        Assert.False(Authenticate(manager, "client-1", "LATEPASS2"));
        Assert.True(Authenticate(manager, "client-1", "OWNERBPK2"));
    }

    [Fact]
    public void SuccessfulAuthentication_ReportsTheOwnerItWasVerifiedAgainst()
    {
        using var manager = CreateManager();
        Register(manager, "secret-A", "OWNERAPK2", T0);

        var response = manager.AuthenticateClient(
            new ClientAuthenticationMessage { ServerId = ServerId, ClientId = "client-1", Passkey = "OWNERAPK2" });

        Assert.True(response.Success);
        Assert.NotNull(response.VerifiedOwnerHash);
        Assert.Equal(manager.CaptureOwner(ServerId), response.VerifiedOwnerHash);
        // Server-side detail: it must never be serialized to the viewer.
        Assert.DoesNotContain("VerifiedOwnerHash", System.Text.Json.JsonSerializer.Serialize(response));
    }

    [Fact]
    public void Passkey_IsRejectedAfterItsValidTo_EvenWhileTheSessionIsConnected()
    {
        using var manager = CreateManager();
        var validTo = DateTime.UtcNow.AddMinutes(1);
        manager.RegisterServer(new ServerRegistrationMessage
        {
            ServerId = ServerId,
            Passkey = "EXPIRYPK2",
            ValidTo = validTo,
            RemoteControlEnabled = false,
            TrustClientPermanent = false
        });
        ClientAuthenticationResponse Auth(DateTime at) => manager.AuthenticateClient(
            new ClientAuthenticationMessage { ServerId = ServerId, ClientId = "client-1", Passkey = "EXPIRYPK2" }, at);

        Assert.True(Auth(validTo.AddSeconds(-1)).Success); // session is now Connected
        Assert.False(Auth(validTo).Success);               // same passkey after expiry, same connected viewer
    }

    [Fact]
    public void TrustGrantedByAStranger_DoesNotSurviveTheRealOwnerReturning()
    {
        using var manager = CreateManager();
        Register(manager, "secret-B", "STRANGER2", T0);
        manager.TrustClient(ServerId, "client-attacker");
        Assert.True(manager.IsTrustedClient(ServerId, "client-attacker"));

        manager.ExpireOwnerClaims(T0.AddMinutes(11));
        Register(manager, "secret-A", "OWNERAPK2", T0.AddMinutes(11));

        Assert.False(manager.IsTrustedClient(ServerId, "client-attacker"));
    }
}
