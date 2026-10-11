using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DeskShare.Core.Auth;
using DeskShare.Core.Models;
using DeskShare.SignalingServer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DeskShare.IntegrationTests.Signaling;

/// <summary>
/// A Server ID is a short public address, so the signaling server must enforce who may talk to whom:
/// a viewer only to the server it authenticated for, a sender only to that server's viewers.
/// These tests use real WebSockets against the in-process server.
/// </summary>
[Collection(SignalingServerCollection.Name)]
public class SignalingRoutingTests
{
    private const string Passkey = "TESTPASS2";
    private static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(5);

    private readonly SignalingEndpointTests.ServerFactory _factory;

    public SignalingRoutingTests(SignalingEndpointTests.ServerFactory factory) => _factory = factory;

    /// <summary>A connected socket that collects every message the server pushes to it.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly Channel<SignalingMessage> _inbox = Channel.CreateUnbounded<SignalingMessage>();
        private readonly Task _pump;

        public Peer(WebSocket socket)
        {
            _socket = socket;
            _pump = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            var buffer = new byte[16384];
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _inbox.Writer.TryWrite(JsonSerializer.Deserialize<SignalingMessage>(json)!);
                }
            }
            catch (Exception) { /* socket closed or aborted: nothing more will arrive */ }
            finally { _inbox.Writer.TryComplete(); }
        }

        public async Task SendAsync(SignalingMessage message) =>
            await _socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)),
                WebSocketMessageType.Text, true, CancellationToken.None);

        /// <summary>Next message of the given type, or null if none arrives within the timeout.</summary>
        public async Task<SignalingMessage?> NextAsync(SignalingMessageType type, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (await _inbox.Reader.WaitToReadAsync(cts.Token))
                {
                    while (_inbox.Reader.TryRead(out var m))
                        if (m.Type == type) return m;
                }
            }
            catch (OperationCanceledException) { }
            return null;
        }

        public async Task CloseAsync()
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            _socket.Dispose();
            await _pump;
        }
    }

    private async Task<Peer> ConnectRawAsync(string token)
    {
        var socket = await _factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri($"ws://localhost/signal?token={Uri.EscapeDataString(token)}"), CancellationToken.None);
        return new Peer(socket);
    }

    private async Task<Peer> ConnectAsync(string token)
    {
        var peer = await ConnectRawAsync(token);
        // The server greets every socket with Identify; waiting for it means the socket is registered.
        Assert.NotNull(await peer.NextAsync(SignalingMessageType.Identify, Delivery));
        return peer;
    }

    /// <summary>Registers (or re-registers) a server ID as an installation and returns its one-time socket token.</summary>
    private static async Task<string> RegisterForTokenAsync(HttpClient http, string serverId, string ownerSecret)
    {
        var response = await http.PostAsJsonAsync("/register", new ServerRegistrationMessage
        {
            ServerId = serverId,
            Passkey = Passkey,
            ValidTo = DateTime.UtcNow.AddMinutes(5),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            OwnerSecret = ownerSecret
        });
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<ServerRegistrationResponse>())!;
        return body.WebSocketToken!;
    }

    /// <summary>Registers a sender as its own installation and returns its connected socket.</summary>
    private async Task<Peer> ConnectSenderAsync(HttpClient http, string serverId, string? ownerSecret = null) =>
        await ConnectAsync(await RegisterForTokenAsync(http, serverId, ownerSecret ?? "secret-" + serverId));

    /// <summary>Authenticates a viewer for <paramref name="serverId"/> (the sender must be registered) and connects it.</summary>
    private async Task<Peer> ConnectViewerAsync(HttpClient http, string serverId, string clientId)
    {
        var now = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();
        var response = await http.PostAsJsonAsync("/authenticate", new ClientAuthenticationMessage
        {
            ServerId = serverId,
            Passkey = Passkey,
            ClientId = clientId,
            Timestamp = now,
            Nonce = nonce,
            Signature = RequestSigningService.SignRequest(serverId, Passkey, now, nonce)
        });
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<ClientAuthenticationResponse>())!;
        return await ConnectAsync(body.WebSocketToken!);
    }

    private static SignalingMessage Message(SignalingMessageType type, string? targetId = null) =>
        new() { Type = type, TargetId = targetId, Sdp = "payload" };

    [Fact]
    public async Task Attacker_WhoRegisteredItsOwnServerId_CannotReachAnotherSender()
    {
        var http = _factory.CreateClient();
        await using var victim = await ConnectSenderAsync(http, "server-it-route-victim");
        await using var attacker = await ConnectSenderAsync(http, "server-it-route-attacker");

        // Targeted and broadcast: neither may reach the victim (it is not the attacker's viewer).
        await attacker.SendAsync(Message(SignalingMessageType.ConnectionRequest, "server-it-route-victim"));
        await attacker.SendAsync(Message(SignalingMessageType.ConnectionRequest));

        Assert.Null(await victim.NextAsync(SignalingMessageType.ConnectionRequest, Silence));
    }

    [Fact]
    public async Task SecondSocketWithTheSameId_DoesNotKickTheFirst_NorWidenItsReach()
    {
        var http = _factory.CreateClient();
        await using var victim = await ConnectSenderAsync(http, "server-it-s7-victim");
        await using var victimViewer = await ConnectViewerAsync(http, "server-it-s7-victim", "client-it-s7-victim");

        // The attacker owns its ID and holds two valid tokens for it.
        var token1 = await RegisterForTokenAsync(http, "server-it-s7-attacker", "secret-attacker");
        var token2 = await RegisterForTokenAsync(http, "server-it-s7-attacker", "secret-attacker");
        await using var socket1 = await ConnectAsync(token1);
        await using var socket2 = await ConnectRawAsync(token2);
        // Socket 2 is refused ("already in use"); its cleanup must not remove socket 1's registration.
        Assert.NotNull(await socket2.NextAsync(SignalingMessageType.Error, Delivery));

        await socket1.SendAsync(Message(SignalingMessageType.ConnectionRequest, "server-it-s7-victim"));
        await socket1.SendAsync(Message(SignalingMessageType.ConnectionRequest, "client-it-s7-victim"));
        await socket1.SendAsync(Message(SignalingMessageType.ConnectionRequest)); // broadcast

        Assert.Null(await victim.NextAsync(SignalingMessageType.ConnectionRequest, Silence));
        Assert.Null(await victimViewer.NextAsync(SignalingMessageType.ConnectionRequest, Silence));

        // Socket 1 is still the registered connection: its own viewer can still reach it.
        await using var attackerViewer = await ConnectViewerAsync(http, "server-it-s7-attacker", "client-it-s7-attacker");
        await attackerViewer.SendAsync(Message(SignalingMessageType.ConnectionRequest, "server-it-s7-attacker"));
        Assert.NotNull(await socket1.NextAsync(SignalingMessageType.ConnectionRequest, Delivery));
    }

    [Fact]
    public async Task ViewerAuthenticatedUnderAPreviousOwner_IsNotAPeerOfTheRealOwnerWhoReturns()
    {
        var http = _factory.CreateClient();
        var cm = _factory.Services.GetRequiredService<ConnectionManager>();
        const string serverId = "server-it-s8-victim";

        // The victim is offline; an attacker claims its ID and authenticates a viewer against its own passkey.
        await RegisterForTokenAsync(http, serverId, "secret-attacker");
        await using var attackerViewer = await ConnectViewerAsync(http, serverId, "client-it-s8-attacker");

        // The claim lapses (10 minutes without a registration) and the real owner comes back.
        cm.ExpireOwnerClaims(DateTime.UtcNow.AddMinutes(11));
        await using var victim = await ConnectSenderAsync(http, serverId, "secret-victim");
        await using var realViewer = await ConnectViewerAsync(http, serverId, "client-it-s8-real");

        await attackerViewer.SendAsync(Message(SignalingMessageType.ConnectionRequest, serverId));
        await attackerViewer.SendAsync(Message(SignalingMessageType.ConnectionRequest)); // broadcast
        await victim.SendAsync(Message(SignalingMessageType.Offer, "client-it-s8-attacker"));

        // The old viewer neither reaches the sender (no offer is triggered) nor receives anything from it.
        Assert.Null(await victim.NextAsync(SignalingMessageType.ConnectionRequest, Silence));
        Assert.Null(await attackerViewer.NextAsync(SignalingMessageType.Offer, Silence));

        // The legitimate viewer of the returned owner is unaffected.
        await realViewer.SendAsync(Message(SignalingMessageType.ConnectionRequest, serverId));
        Assert.NotNull(await victim.NextAsync(SignalingMessageType.ConnectionRequest, Delivery));
    }

    [Fact]
    public async Task Viewer_AuthenticatedForServerA_CannotReachServerB_OrOtherViewers()
    {
        var http = _factory.CreateClient();
        await using var serverA = await ConnectSenderAsync(http, "server-it-route-a");
        await using var serverB = await ConnectSenderAsync(http, "server-it-route-b");
        await using var viewerA = await ConnectViewerAsync(http, "server-it-route-a", "client-it-route-va");
        await using var viewerB = await ConnectViewerAsync(http, "server-it-route-b", "client-it-route-vb");

        await viewerA.SendAsync(Message(SignalingMessageType.ConnectionRequest, "server-it-route-b"));
        await viewerA.SendAsync(Message(SignalingMessageType.ConnectionRequest, "client-it-route-vb"));
        await viewerA.SendAsync(Message(SignalingMessageType.IceCandidate)); // broadcast

        Assert.Null(await serverB.NextAsync(SignalingMessageType.ConnectionRequest, Silence));
        Assert.Null(await viewerB.NextAsync(SignalingMessageType.ConnectionRequest, Silence));
        Assert.Null(await serverB.NextAsync(SignalingMessageType.IceCandidate, Silence));
        Assert.Null(await viewerB.NextAsync(SignalingMessageType.IceCandidate, Silence));

        // The broadcast did reach the viewer's own server, so the silence above is the rule, not a broken test.
        Assert.NotNull(await serverA.NextAsync(SignalingMessageType.IceCandidate, Delivery));
    }

    [Fact]
    public async Task LegitimateViewerAndSender_ExchangeMessagesInBothDirections()
    {
        var http = _factory.CreateClient();
        await using var server = await ConnectSenderAsync(http, "server-it-route-ok");
        await using var viewer = await ConnectViewerAsync(http, "server-it-route-ok", "client-it-route-ok");

        // Viewer -> sender, addressed (ConnectionRequest, Answer) ...
        await viewer.SendAsync(Message(SignalingMessageType.ConnectionRequest, "server-it-route-ok"));
        var request = await server.NextAsync(SignalingMessageType.ConnectionRequest, Delivery);
        Assert.Equal("client-it-route-ok", request?.SenderId);

        // ... sender -> viewer, addressed (Offer) and broadcast (ICE candidates carry no TargetId) ...
        await server.SendAsync(Message(SignalingMessageType.Offer, "client-it-route-ok"));
        Assert.NotNull(await viewer.NextAsync(SignalingMessageType.Offer, Delivery));
        await server.SendAsync(Message(SignalingMessageType.IceCandidate));
        Assert.NotNull(await viewer.NextAsync(SignalingMessageType.IceCandidate, Delivery));

        // ... and the keep-alive ping every client sends to itself keeps working.
        await viewer.SendAsync(Message(SignalingMessageType.Ping, "client-it-route-ok"));
        Assert.NotNull(await viewer.NextAsync(SignalingMessageType.Ping, Delivery));
    }

    [Fact]
    public async Task DisconnectNotice_ReachesOnlyThePeer()
    {
        var http = _factory.CreateClient();
        await using var server = await ConnectSenderAsync(http, "server-it-route-dc");
        await using var viewer = await ConnectViewerAsync(http, "server-it-route-dc", "client-it-route-dc");
        await using var otherServer = await ConnectSenderAsync(http, "server-it-route-dc-other");
        await using var otherViewer = await ConnectViewerAsync(http, "server-it-route-dc-other", "client-it-route-dc-other");

        await viewer.CloseAsync();

        var notice = await server.NextAsync(SignalingMessageType.Error, Delivery);
        Assert.Contains("client-it-route-dc", notice?.ErrorMessage);
        Assert.Null(await otherServer.NextAsync(SignalingMessageType.Error, Silence));
        Assert.Null(await otherViewer.NextAsync(SignalingMessageType.Error, Silence));
    }
}
