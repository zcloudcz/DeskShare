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
