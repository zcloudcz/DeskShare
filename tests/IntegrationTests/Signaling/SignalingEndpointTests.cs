using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskShare.Core.Auth;

namespace DeskShare.IntegrationTests.Signaling;

/// <summary>
/// Runs the real SignalingServer in-process and walks the client flow the desktop viewer uses:
/// /register (sender) -> /authenticate (viewer) -> /signal?token=...
/// </summary>
public class SignalingEndpointTests : IClassFixture<SignalingEndpointTests.ServerFactory>
{
    /// <summary>Hosts the server with production-like security and no UDP STUN listener (port 3478 would clash).</summary>
    public sealed class ServerFactory : WebApplicationFactory<Program>
    {
        public ServerFactory()
        {
            // Environment variable (not UseSetting): Program.cs reads Stun:Enabled while building services.
            Environment.SetEnvironmentVariable("Stun__Enabled", "false");
            // appsettings.Development.json allows tokenless sockets; run with the production setting instead.
            Environment.SetEnvironmentVariable("Security__AllowLegacyConnections", "false");
            // Every test registers servers from the same in-process "IP"; the per-IP limits are not under test here.
            Environment.SetEnvironmentVariable("RateLimiting__Register__PermitLimit", "1000");
            Environment.SetEnvironmentVariable("RateLimiting__Authenticate__PermitLimit", "1000");
        }
    }

    private readonly ServerFactory _factory;

    public SignalingEndpointTests(ServerFactory factory) => _factory = factory;

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient http, string serverId, string? ownerSecret)
    {
        var registration = new ServerRegistrationMessage
        {
            ServerId = serverId,
            Passkey = "ignored", // the server derives the real passkey from ServerId + time
            ValidTo = DateTime.UtcNow.AddMinutes(1),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            OwnerSecret = ownerSecret
        };
        return await http.PostAsJsonAsync("/register", registration);
    }

    private static async Task<ClientAuthenticationResponse> AuthenticateAsync(HttpClient http, string serverId, string clientId)
    {
        var now = DateTime.UtcNow;
        var passkey = AuthenticationService.GeneratePasskey(serverId, now);
        var nonce = Guid.NewGuid().ToString();

        var response = await http.PostAsJsonAsync("/authenticate", new ClientAuthenticationMessage
        {
            ServerId = serverId,
            Passkey = passkey,
            ClientId = clientId,
            Timestamp = now,
            Nonce = nonce,
            Signature = RequestSigningService.SignRequest(serverId, passkey, now, nonce)
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClientAuthenticationResponse>())!;
    }

    [Fact]
    public async Task Authenticate_ThenOpenSignalWithToken_Succeeds_AsTheAuthenticatedClientId()
    {
        var http = _factory.CreateClient();
        var serverId = "server-it-viewer";
        var clientId = "client-it-viewer";

        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(http, serverId, "secret-1")).StatusCode);
        var auth = await AuthenticateAsync(http, serverId, clientId);

        Assert.True(auth.Success);
        Assert.False(string.IsNullOrEmpty(auth.WebSocketToken));
        Assert.NotEmpty(auth.IceServers!);

        var wsClient = _factory.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/signal?token={Uri.EscapeDataString(auth.WebSocketToken!)}"), CancellationToken.None);

        // The server confirms with an Identify message that carries the id the token was issued for.
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        var message = Encoding.UTF8.GetString(buffer, 0, received.Count);

        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Contains(clientId, message);
    }

    [Fact]
    public async Task Signal_WithoutToken_IsRejected()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();

        // Production runs with Security:AllowLegacyConnections=false, so an anonymous upgrade must fail.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            wsClient.ConnectAsync(new Uri("ws://localhost/signal?clientId=intruder"), CancellationToken.None));
    }

    [Fact]
    public async Task Register_WithDifferentOwnerSecret_IsForbidden_AndGetsNoToken()
    {
        var http = _factory.CreateClient();
        var serverId = "server-it-owner";

        var first = await RegisterAsync(http, serverId, "secret-owner");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var hijack = await RegisterAsync(http, serverId, "secret-attacker");
        Assert.Equal(HttpStatusCode.Forbidden, hijack.StatusCode);
        var body = (await hijack.Content.ReadFromJsonAsync<ServerRegistrationResponse>())!;
        Assert.False(body.Success);
        Assert.Null(body.WebSocketToken);

        var noSecret = await RegisterAsync(http, serverId, null);
        Assert.Equal(HttpStatusCode.Forbidden, noSecret.StatusCode);
    }

    [Theory]
    [InlineData("server-it-victim")]      // a currently registered server
    [InlineData("server-not-registered")] // any id with the server prefix
    public async Task Authenticate_WithServerIdentityAsClientId_IsRejected(string clientId)
    {
        // Attacker owns their own server and can pass its passkey check, but asks for a token bound to the
        // victim's identity. That would let them take over the victim's signaling socket.
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(http, "server-it-victim", "secret-victim")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(http, "server-it-attacker", "secret-attacker")).StatusCode);

        var now = DateTime.UtcNow;
        var passkey = AuthenticationService.GeneratePasskey("server-it-attacker", now);
        var nonce = Guid.NewGuid().ToString();
        var response = await http.PostAsJsonAsync("/authenticate", new ClientAuthenticationMessage
        {
            ServerId = "server-it-attacker",
            Passkey = passkey,
            ClientId = clientId,
            Timestamp = now,
            Nonce = nonce,
            Signature = RequestSigningService.SignRequest("server-it-attacker", passkey, now, nonce)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("webSocketToken", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static Task<HttpResponseMessage> ResumeAsync(HttpClient http, string resumeToken) =>
        http.PostAsJsonAsync("/resume", new ClientResumeMessage { ResumeToken = resumeToken });

    [Fact]
    public async Task Resume_AfterAuthenticate_ReturnsNewWebSocketToken_ThatOpensSignal()
    {
        var http = _factory.CreateClient();
        var serverId = "server-it-resume";
        var clientId = "client-it-resume";

        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(http, serverId, "secret-resume")).StatusCode);
        var auth = await AuthenticateAsync(http, serverId, clientId);
        Assert.False(string.IsNullOrEmpty(auth.ResumeToken));

        // The first socket token is burned by a normal connect, exactly like the viewer's initial connection.
        var wsClient = _factory.Server.CreateWebSocketClient();
        using (await wsClient.ConnectAsync(
            new Uri($"ws://localhost/signal?token={Uri.EscapeDataString(auth.WebSocketToken!)}"), CancellationToken.None))
        {
        }

        // Reconnect without the passkey: /resume hands out a fresh socket token, the next resume token and ICE servers.
        var response = await ResumeAsync(http, auth.ResumeToken!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resumed = (await response.Content.ReadFromJsonAsync<ClientAuthenticationResponse>())!;

        Assert.True(resumed.Success);
        Assert.False(string.IsNullOrEmpty(resumed.WebSocketToken));
        Assert.NotEqual(auth.WebSocketToken, resumed.WebSocketToken);
        Assert.False(string.IsNullOrEmpty(resumed.ResumeToken));
        Assert.NotEqual(auth.ResumeToken, resumed.ResumeToken);
        Assert.NotEmpty(resumed.IceServers!);

        using var socket = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/signal?token={Uri.EscapeDataString(resumed.WebSocketToken!)}"), CancellationToken.None);
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);

        // Same ClientId as before the drop, so the sender sees the same viewer.
        Assert.Contains(clientId, Encoding.UTF8.GetString(buffer, 0, received.Count));

        // Rotation: the new resume token works once more.
        Assert.Equal(HttpStatusCode.OK, (await ResumeAsync(http, resumed.ResumeToken!)).StatusCode);
    }

    [Fact]
    public async Task Resume_SameTokenTwice_SecondIsUnauthorized()
    {
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(http, "server-it-resume-twice", "secret-twice")).StatusCode);
        var auth = await AuthenticateAsync(http, "server-it-resume-twice", "client-it-resume-twice");

        Assert.Equal(HttpStatusCode.OK, (await ResumeAsync(http, auth.ResumeToken!)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ResumeAsync(http, auth.ResumeToken!)).StatusCode);
    }

    [Theory]
    [InlineData("garbage-token")]
    [InlineData("")]
    public async Task Resume_WithInvalidToken_IsUnauthorized(string token)
    {
        var http = _factory.CreateClient();

        var response = await ResumeAsync(http, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("webSocketToken", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
