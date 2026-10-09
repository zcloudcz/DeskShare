using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeskShare.Core.Models;
using DeskShare.Core.Signaling;

namespace DeskShare.UnitTests.Signaling;

/// <summary>
/// The sender relies on <see cref="WebSocketSignaler.ConnectionLost"/> to know when to re-register and reconnect.
/// Uses a tiny in-process WebSocket server (HttpListener) so no real signaling server is needed.
/// </summary>
public class WebSocketSignalerTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 30)]
    [InlineData(5, 30)]
    [InlineData(1000, 30)]
    [InlineData(-1, 2)]
    public void Backoff_FollowsScheduleThenStaysAtCap(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ReconnectBackoff.GetDelay(attempt));
    }

    /// <summary>Accepts WebSocket connections, answers each with an Identify message, then runs <c>afterIdentify</c>.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<WebSocket, Task> _afterIdentify;
        public string Url { get; }
        public int Connections;

        public FakeServer(Func<WebSocket, Task> afterIdentify)
        {
            _afterIdentify = afterIdentify;

            // Ask the OS for a free port, then hand it to HttpListener (which cannot bind port 0 itself).
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Url = $"ws://localhost:{port}/signal";
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    Interlocked.Increment(ref Connections);
                    _ = HandleAsync(context);
                }
            }
            catch (Exception)
            {
                // Listener stopped at the end of the test.
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var identify = JsonSerializer.Serialize(new SignalingMessage
            {
                Type = SignalingMessageType.Identify,
                SenderId = "server",
                TargetId = "server-test",
                Timestamp = DateTime.UtcNow
            });
            await ws.SendAsync(Encoding.UTF8.GetBytes(identify), WebSocketMessageType.Text, true, CancellationToken.None);
            await _afterIdentify(ws);
        }

        public void Dispose() => _listener.Close();
    }

    private static async Task<bool> WaitAsync(Task task, int timeoutMs = 5000) =>
        await Task.WhenAny(task, Task.Delay(timeoutMs)) == task;

    [Fact]
    public async Task ConnectionLost_IsRaised_WhenServerClosesTheSocket()
    {
        using var server = new FakeServer(ws => ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "restart", CancellationToken.None));
        using var signaler = new WebSocketSignaler();
        var lost = new TaskCompletionSource();
        signaler.ConnectionLost += (_, _) => lost.TrySetResult();

        await signaler.ConnectAsync(server.Url, "server-test");

        Assert.True(await WaitAsync(lost.Task), "ConnectionLost was not raised after the server closed the socket");
    }

    [Fact]
    public async Task ConnectionLost_IsNotRaised_OnClientInitiatedDisconnect()
    {
        // Server keeps the socket open until the client closes it.
        using var server = new FakeServer(async ws =>
        {
            var buffer = new byte[256];
            try { while ((await ws.ReceiveAsync(buffer, CancellationToken.None)).MessageType != WebSocketMessageType.Close) { } }
            catch (Exception) { }
        });
        using var signaler = new WebSocketSignaler();
        var raised = 0;
        signaler.ConnectionLost += (_, _) => Interlocked.Increment(ref raised);

        await signaler.ConnectAsync(server.Url, "server-test");
        await signaler.DisconnectAsync();
        await Task.Delay(300); // give a (wrong) late event time to show up

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Signaler_CanReconnect_AfterConnectionLost_AndKeepsItsClientId()
    {
        FakeServer? server = null;
        server = new FakeServer(async ws =>
        {
            // First connection is dropped by the server; the second one stays open.
            if (server!.Connections == 1)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "restart", CancellationToken.None);
                return;
            }
            var buffer = new byte[256];
            try { while ((await ws.ReceiveAsync(buffer, CancellationToken.None)).MessageType != WebSocketMessageType.Close) { } }
            catch (Exception) { }
        });
        using var serverScope = server;
        using var signaler = new WebSocketSignaler();
        var lost = new TaskCompletionSource();
        signaler.ConnectionLost += (_, _) => lost.TrySetResult();

        await signaler.ConnectAsync(server.Url, "server-test");
        Assert.True(await WaitAsync(lost.Task));

        await signaler.ConnectAsync(server.Url, "server-test");

        Assert.True(signaler.IsConnected);
        Assert.Equal("server-test", signaler.ClientId);
        Assert.Equal(2, server.Connections);
    }
}
