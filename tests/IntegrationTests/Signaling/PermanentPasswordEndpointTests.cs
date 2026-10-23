using System.Net;
using System.Net.Http.Json;
using DeskShare.Core.Auth;

namespace DeskShare.IntegrationTests.Signaling;

/// <summary>
/// /register and /authenticate with a permanent password, against the real in-process server: the password's authKey
/// works in place of the passkey's, only the owner can set it, and a ServerId is locked after repeated failures.
/// </summary>
[Collection(SignalingServerCollection.Name)]
public class PermanentPasswordEndpointTests
{
    private const string PasskeyAuth = "PASSKEYAUTH000000000000000000000000000000000";
    private const string PermanentAuth = "Perm-Auth_000000000000000000000000000000000";

    private readonly SignalingEndpointTests.ServerFactory _factory;

    public PermanentPasswordEndpointTests(SignalingEndpointTests.ServerFactory factory) => _factory = factory;

    private static string NewServerId() => "server-" + Random.Shared.Next(100_000_000, 1_000_000_000);

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient http, string serverId, string ownerSecret, string? permanentAuthKey) =>
        http.PostAsJsonAsync("/register", new ServerRegistrationMessage
        {
            ServerId = serverId,
            Passkey = PasskeyAuth,
            PermanentAuthKey = permanentAuthKey,
            ValidTo = DateTime.UtcNow.AddMinutes(1),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            OwnerSecret = ownerSecret
        });

    private static async Task<HttpStatusCode> AuthenticateAsync(HttpClient http, string serverId, string credential)
    {
        var now = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();
        var response = await http.PostAsJsonAsync("/authenticate", new ClientAuthenticationMessage
        {
            ServerId = serverId,
            Passkey = credential,
            ClientId = "client-" + Guid.NewGuid().ToString("N"),
            Timestamp = now,
            Nonce = nonce,
            Signature = RequestSigningService.SignRequest(serverId, credential, now, nonce)
        });
        return response.StatusCode;
    }

    [Fact]
    public async Task Authenticate_AcceptsPermanentKeyOrPasskey_RejectsWrong()
    {
        var http = _factory.CreateClient();
        var serverId = NewServerId();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(http, serverId, "secret-1", PermanentAuth)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await AuthenticateAsync(http, serverId, "Wrong-Auth_00000000000000000000000000000000"));
        Assert.Equal(HttpStatusCode.OK, await AuthenticateAsync(http, serverId, PermanentAuth));
    }

    [Fact]
    public async Task Passkey_StillWorks_WhenAPermanentKeyIsRegistered()
    {
        var http = _factory.CreateClient();
        var serverId = NewServerId();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(http, serverId, "secret-1", PermanentAuth)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, await AuthenticateAsync(http, serverId, PasskeyAuth));
    }

    [Fact]
    public async Task OnlyTheOwner_CanSetThePermanentKey()
    {
        var http = _factory.CreateClient();
        var serverId = NewServerId();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(http, serverId, "secret-1", null)).StatusCode);

        // A stranger tries to plant its own password on someone else's ServerId.
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterAsync(http, serverId, "secret-2", PermanentAuth)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await AuthenticateAsync(http, serverId, PermanentAuth));
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("has spaces 000000000000000000000000000000000")]
    public async Task Register_WithMalformedPermanentKey_IsRejected(string key)
    {
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(http, NewServerId(), "secret-1", key)).StatusCode);
    }

    [Fact]
    public async Task FiveFailures_LockThePermanentPassword_ButNotTheCurrentPasskey()
    {
        var http = _factory.CreateClient();
        var serverId = NewServerId();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(http, serverId, "secret-1", PermanentAuth)).StatusCode);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await AuthenticateAsync(http, serverId, "Wrong-Auth_00000000000000000000000000000000"));

        // Locked: even the right password is not compared any more...
        Assert.Equal(HttpStatusCode.TooManyRequests, await AuthenticateAsync(http, serverId, PermanentAuth));
        Assert.Equal(HttpStatusCode.TooManyRequests, await AuthenticateAsync(http, serverId, "Wrong-Auth_00000000000000000000000000000000"));
        // ...but whoever can read the current passkey on the host still gets in.
        Assert.Equal(HttpStatusCode.OK, await AuthenticateAsync(http, serverId, PasskeyAuth));
    }
}
