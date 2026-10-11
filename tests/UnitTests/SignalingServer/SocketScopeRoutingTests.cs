using System.Net.WebSockets;
using DeskShare.Core.Models;
using DeskShare.SignalingServer.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DeskShare.UnitTests.SignalingServer;

/// <summary>
/// Sender and viewer are peers only for the same ServerId AND the same owner secret. Owner claims lapse, so a
/// ServerId can change hands while sockets stay connected.
/// </summary>
public class SocketScopeRoutingTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string ServerId = "server-123456789";

    private static WebSocket OpenSocket()
    {
        var socket = Substitute.For<WebSocket>();
        socket.State.Returns(WebSocketState.Open);
        return socket;
    }

    private static int Deliveries(WebSocket socket) =>
        socket.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(WebSocket.SendAsync));

    /// <summary>Registers a socket the way /signal does: consume its one-time token, then register the connection.</summary>
    private static WebSocket Connect(ConnectionManager manager, string clientId, string token)
    {
        Assert.True(manager.ValidateAndConsumeToken(token, out var id, out var scope));
        Assert.Equal(clientId, id);
        var socket = OpenSocket();
        Assert.True(manager.RegisterClient(clientId, socket, "127.0.0.1", scope));
        return socket;
    }

    private static async Task<bool> Delivers(ConnectionManager manager, string fromId, WebSocket fromSocket, string toId, WebSocket toSocket)
    {
        var before = Deliveries(toSocket);
        await manager.RouteMessageAsync(fromId, fromSocket,
            new SignalingMessage { Type = SignalingMessageType.ConnectionRequest, SenderId = fromId, TargetId = toId });
        return Deliveries(toSocket) > before;
    }

    [Fact]
    public async Task SameServerIdAndSameOwner_ArePeers()
    {
        using var manager = new ConnectionManager(Substitute.For<ILogger<ConnectionManager>>());
        manager.AuthorizeServerOwner(ServerId, "secret-A", "1.2.3.4", T0);
        var sender = Connect(manager, ServerId, manager.IssueServerWebSocketToken(ServerId));
        var viewer = Connect(manager, "client-1", manager.IssueViewerWebSocketToken("client-1", ServerId));

        Assert.True(await Delivers(manager, "client-1", viewer, ServerId, sender));
        Assert.True(await Delivers(manager, ServerId, sender, "client-1", viewer));
    }

    [Fact]
    public async Task SameServerId_ButDifferentOwner_ArePeersInNeitherDirection()
    {
        using var manager = new ConnectionManager(Substitute.For<ILogger<ConnectionManager>>());
        // A stranger owns the ID and authenticates a viewer against its own passkey ...
        manager.AuthorizeServerOwner(ServerId, "secret-attacker", "5.6.7.8", T0);
        var strangerViewer = Connect(manager, "client-attacker", manager.IssueViewerWebSocketToken("client-attacker", ServerId));

        // ... the claim lapses and the real owner comes back.
        manager.ExpireOwnerClaims(T0.AddMinutes(11));
        manager.AuthorizeServerOwner(ServerId, "secret-A", "1.2.3.4", T0.AddMinutes(11));
        var realSender = Connect(manager, ServerId, manager.IssueServerWebSocketToken(ServerId));

        Assert.False(await Delivers(manager, "client-attacker", strangerViewer, ServerId, realSender));
        Assert.False(await Delivers(manager, ServerId, realSender, "client-attacker", strangerViewer));
    }

    [Fact]
    public async Task ASocketIssuedWithoutAnyOwner_IsNeverAPeer()
    {
        using var manager = new ConnectionManager(Substitute.For<ILogger<ConnectionManager>>());
        // No claim at all: both scopes carry no owner hash, which must not count as "equal".
        var sender = Connect(manager, ServerId, manager.IssueServerWebSocketToken(ServerId));
        var viewer = Connect(manager, "client-1", manager.IssueViewerWebSocketToken("client-1", ServerId));

        Assert.False(await Delivers(manager, "client-1", viewer, ServerId, sender));
    }
}
