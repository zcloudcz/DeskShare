using System.Reflection;
using System.Text.Json;
using DeskShare.Core.Auth;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.Video;
using DeskShare.Core.WebRTC;
using DeskShare.Desktop.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SIPSorcery.Net;
using Serilog;

namespace DeskShare.UnitTests.WebRTC;

/// <summary>
/// Rule under test: nothing calls out (events, sends, handlers, other locks) while the peer's lock or a component's own
/// lock is held. Every test would hang or deadlock if a callout sat under a lock, so each one is guarded by a timeout:
/// a deadlock then FAILS instead of hanging the run.
/// </summary>
public sealed class LockDisciplineTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deskshare-lock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static async Task<PeerContext> OpenPeerAsync(bool verified)
    {
        var pc = new RTCPeerConnection();
        var peer = new PeerContext(pc)
        {
            Channel = await pc.createDataChannel("input"),
            ChannelOpenForTests = true
        };
        if (verified) peer.TryVerify();
        return peer;
    }

    [Fact]
    public async Task FileReceiver_IdleTimeoutReply_DoesNotDeadlockWithADispatchHoldingThePeerLock()
    {
        // The reply goes out through "the peer lock" (here a plain lock, like PeerContext.TrySend). The old receiver
        // sent while holding its own gate: idle timeout (gate -> peer lock) against a dispatch (peer lock -> gate).
        var peerLock = new object();
        var replies = new List<string>();
        var receiver = new FileReceiver(_folder, 1024, json => { lock (peerLock) { replies.Add(json); } return true; },
            Substitute.For<ILogger>(), idleTimeout: TimeSpan.FromMilliseconds(30));
        var id = Guid.NewGuid().ToString("D");
        receiver.Handle("file_offer", Json($$"""{"type":"file_offer","id":"{{id}}","name":"a.txt","size":10}"""));

        var dispatch = Task.Run(() =>
        {
            lock (peerLock)
            {
                Thread.Sleep(250); // the idle timer fires meanwhile and wants to reply
                receiver.Handle("file_cancel", Json($$"""{"type":"file_cancel","id":"{{id}}"}"""));
            }
        });

        await dispatch.WaitAsync(Guard); // would never finish with a reply sent under the receiver's gate
        await Task.Delay(100);
        lock (peerLock)
        {
            Assert.Contains(replies, r => r.Contains("file_accept"));
        }
        receiver.Dispose();
    }

    [Fact]
    public async Task FileReceiver_RepliesGoToTheRouteOfTheMessageThatStartedTheTransfer()
    {
        var viaA = new List<string>();
        var viaB = new List<string>();
        var receiver = new FileReceiver(_folder, 1024, _ => true, Substitute.For<ILogger>(), idleTimeout: TimeSpan.FromMilliseconds(30));
        var id = Guid.NewGuid().ToString("D");

        receiver.Handle("file_offer", Json($$"""{"type":"file_offer","id":"{{id}}","name":"a.txt","size":10}"""), json => { lock (viaA) viaA.Add(json); return true; });
        // A second viewer's message arrives later; the idle timeout of A's transfer must still answer A.
        receiver.Handle("file_cancel", Json("""{"type":"file_cancel","id":"00000000-0000-0000-0000-000000000000"}"""), json => { lock (viaB) viaB.Add(json); return true; });
        await Task.Delay(300);

        lock (viaA) Assert.Contains(viaA, r => r.Contains("file_error"));
        lock (viaB) Assert.Empty(viaB);
        receiver.Dispose();
    }

    [Fact]
    public async Task SenderDispatch_RunsEffectsOutsideThePeerLock()
    {
        // A clipboard handler that blocks until the connection was replaced. Replacing needs the peer's lock, so with
        // the event raised under the lock this would deadlock.
        var controller = Substitute.For<IInputController>();
        var manager = new DataChannelManager(Substitute.For<ILogger>(), controller)
        {
            VerifyViewerBind = (_, _) => "mac",
            HostGrantedCapabilities = SessionCapabilities.Clipboard
        };
        var peerA = await OpenPeerAsync(verified: true);
        manager.BeginPeer(peerA);
        var insideHandler = new ManualResetEventSlim();
        var replaced = new ManualResetEventSlim();
        manager.ClipboardMessageReceived += (_, _) =>
        {
            insideHandler.Set();
            replaced.Wait(Guard);
        };

        var message = Task.Run(() => manager.ProcessInputMessage(peerA, """{"type":"clipboard","data":{"ContentType":0,"TextContent":"x","SizeBytes":1}}"""));
        insideHandler.Wait(Guard);
        var replace = Task.Run(() => { manager.BeginPeer(new PeerContext()); replaced.Set(); });

        await replace.WaitAsync(Guard);
        await message.WaitAsync(Guard);
        Assert.True(peerA.IsRetired);
    }

    [Fact]
    public async Task DesktopViewer_InputDuringAnAuthorizationResponse_DoesNotDeadlock()
    {
        // The UI thread handles HostAllowsFilesChanged synchronously (Dispatcher.Invoke) and the same UI thread sends
        // input. Model: the event handler waits for an input send that is made from another thread.
        var client = new ClientManager(NullLogger<ClientManager>.Instance, NullLogger<VideoSink>.Instance,
            new ConfigurationBuilder().Build(), Substitute.For<IClipboardManager>());
        var peer = await OpenPeerAsync(verified: true);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ClientManager).GetField("_peer", flags)!.SetValue(client, peer);
        typeof(ClientManager).GetField("_isConnected", flags)!.SetValue(client, true);
        typeof(ClientManager).GetField("_dataChannel", flags)!.SetValue(client, peer.Channel);

        var insideHandler = new ManualResetEventSlim();
        var inputSent = new ManualResetEventSlim();
        client.HostAllowsFilesChanged += (_, _) =>
        {
            insideHandler.Set();
            inputSent.Wait(Guard); // blocks like Dispatcher.Invoke would until the UI thread is free
        };

        var onMessage = typeof(ClientManager).GetMethod("OnDataChannelMessage", flags)!;
        var json = System.Text.Encoding.UTF8.GetBytes("""{"type":"authorization_response","clipboard":false,"files":true}""");
        var message = Task.Run(() => onMessage.Invoke(client, new object[] { peer, peer.Channel!, json }));
        insideHandler.Wait(Guard);
        var input = Task.Run(async () =>
        {
            await client.SendInputAsync(new InputMessage { Type = InputMessageType.MouseMove, X = 0.5, Y = 0.5 });
            inputSent.Set();
        });

        await input.WaitAsync(Guard);   // takes the peer lock; must not be held by the message handling
        await message.WaitAsync(Guard);
        Assert.True(client.HostAllowsFiles);
    }

    [Fact]
    public async Task SampleEncodedForAReplacedPeer_IsNeverSentToTheNewOne()
    {
        // Frame of A is "paused" inside the encoder; B replaces A and verifies; A's sample then comes out.
        var peerA = await OpenPeerAsync(verified: true);
        var peerB = await OpenPeerAsync(verified: false);
        peerA.Retire();
        peerB.TryVerify();

        Assert.Null(SIPSorceryVideoSource.ChooseConnection(peerA, peerB, requireVerified: true));   // encoded for A, current is B
        Assert.Same(peerB.Connection, SIPSorceryVideoSource.ChooseConnection(peerB, peerB, requireVerified: true));
        Assert.Same(peerB.Connection, SIPSorceryVideoSource.ChooseConnection(null, peerB, requireVerified: true)); // untagged: current peer
    }

    [Fact]
    public async Task SampleEncodedForAnUnverifiedPeer_IsNotSent()
    {
        var peer = await OpenPeerAsync(verified: false);

        Assert.Null(SIPSorceryVideoSource.ChooseConnection(peer, peer, requireVerified: true));
        Assert.Same(peer.Connection, SIPSorceryVideoSource.ChooseConnection(peer, peer, requireVerified: false));
    }
}
