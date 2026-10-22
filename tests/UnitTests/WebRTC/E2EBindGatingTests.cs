using DeskShare.Core.Auth;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.WebRTC;
using NSubstitute;
using Serilog;

namespace DeskShare.UnitTests.WebRTC;

/// <summary>
/// The "unverified" state of the sender's data channel: until the viewer's e2e_bind verified, nothing is granted
/// except e2e_bind itself; afterwards the normal grants apply.
/// </summary>
public sealed class E2EBindGatingTests
{
    private const string BindJson = """{"type":"e2e_bind","v":1,"role":"viewer","mac":"abcd"}""";
    private const string InputJson = """{"Type":"MouseMove","X":0.5,"Y":0.5}""";
    private const string ClipboardJson = """{"type":"clipboard","data":{"ContentType":0,"TextContent":"secret","SizeBytes":6}}""";
    private const string FileOfferJson = """{"type":"file_offer","id":"1","name":"a.txt","size":1}""";

    private static (DataChannelManager Manager, IInputController Controller) Create(Func<string, string?> verify)
    {
        var controller = Substitute.For<IInputController>();
        controller.IsEnabled.Returns(true);
        controller.ApplyInput(Arg.Any<InputMessage>()).Returns(true);
        var manager = new DataChannelManager(Substitute.For<ILogger>(), controller)
        {
            VerifyViewerBind = (_, mac) => verify(mac),
            HostGrantedCapabilities = SessionCapabilities.Clipboard | SessionCapabilities.FileTransfer
        };
        return (manager, controller);
    }

    [Fact]
    public void BeforeVerification_NothingIsGranted()
    {
        var (manager, _) = Create(_ => null);

        Assert.Equal(SessionCapabilities.None, manager.GrantedCapabilities);
    }

    [Fact]
    public void BeforeVerification_InputClipboardAndFilesAreRejected()
    {
        var (manager, controller) = Create(_ => null);
        var clipboardRaised = false;
        manager.ClipboardMessageReceived += (_, _) => clipboardRaised = true;

        manager.ProcessInputMessage(InputJson);
        manager.ProcessInputMessage(ClipboardJson);
        manager.ProcessInputMessage(FileOfferJson);
        manager.ProcessInputMessage("""{"type":"authorization_request"}""");

        controller.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
        Assert.False(clipboardRaised);
        Assert.False(manager.PeerVerified);
    }

    [Fact]
    public void OnlyBindIsProcessed_BeforeVerification()
    {
        string? seenMac = null;
        var (manager, _) = Create(mac => { seenMac = mac; return "sender-mac"; });

        manager.ProcessInputMessage(BindJson);

        Assert.Equal("abcd", seenMac);
        Assert.True(manager.PeerVerified);
    }

    [Fact]
    public void AfterVerification_NormalGrantsApply()
    {
        var (manager, controller) = Create(_ => "sender-mac");
        var clipboardRaised = false;
        manager.ClipboardMessageReceived += (_, _) => clipboardRaised = true;
        manager.ProcessInputMessage(BindJson);

        manager.ProcessInputMessage(InputJson);
        manager.ProcessInputMessage(ClipboardJson);

        Assert.Equal(
            SessionCapabilities.View | SessionCapabilities.Control | SessionCapabilities.Clipboard | SessionCapabilities.FileTransfer,
            manager.GrantedCapabilities);
        controller.Received(1).ApplyInput(Arg.Any<InputMessage>());
        Assert.True(clipboardRaised);
    }

    [Fact]
    public void WrongBind_StaysUnverified_AndRaisesFailure()
    {
        var failed = false;
        var (manager, controller) = Create(_ => null);
        manager.ViewerBindFailed += (_, _) => failed = true;

        manager.ProcessInputMessage(BindJson);
        manager.ProcessInputMessage(InputJson);

        Assert.True(failed);
        Assert.False(manager.PeerVerified);
        controller.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
    }

    [Theory]
    [InlineData("""{"type":"e2e_bind","v":1,"role":"sender","mac":"abcd"}""")] // reflected role
    [InlineData("""{"type":"e2e_bind","v":1,"mac":"abcd"}""")]                 // no role
    [InlineData("""{"type":"e2e_bind","v":9,"role":"viewer","mac":"abcd"}""")]  // unknown version
    public void MalformedOrReflectedBind_NeverReachesTheVerifier(string json)
    {
        var verifierCalled = false;
        var (manager, _) = Create(_ => { verifierCalled = true; return "sender-mac"; });

        manager.ProcessInputMessage(json);

        Assert.False(verifierCalled);
        Assert.False(manager.PeerVerified);
    }

    [Fact]
    public void NewPeer_StartsUnverified_EvenIfThePreviousOneWasVerified()
    {
        var (manager, controller) = Create(_ => "sender-mac");
        manager.ProcessInputMessage(BindJson);
        Assert.True(manager.PeerVerified);

        manager.DetachDataChannel(); // what a peer connection reset does

        Assert.False(manager.PeerVerified);
        Assert.Equal(SessionCapabilities.None, manager.GrantedCapabilities);
        manager.ProcessInputMessage(InputJson);
        controller.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
    }

    [Fact]
    public void LateVerificationOfAReplacedPeer_NeverVerifiesTheNewPeer()
    {
        // Deterministic race: connection B replaces A while A's MAC check is "in flight" (inside the verifier),
        // and A's check then finishes with a valid result.
        var (manager, controller) = Create(_ => "sender-mac");
        var peerA = new PeerContext();
        var peerB = new PeerContext();
        manager.BeginPeer(peerA);
        var verified = new List<PeerContext>();
        manager.ViewerVerified += (_, p) => verified.Add(p);
        manager.VerifyViewerBind = (_, _) =>
        {
            manager.BeginPeer(peerB); // paused after the MAC check, B takes over
            return "sender-mac";      // resumed: A's result is valid
        };

        manager.ProcessInputMessage(BindJson);
        manager.ProcessInputMessage(InputJson);
        manager.ProcessInputMessage(ClipboardJson);
        manager.ProcessInputMessage(FileOfferJson);

        Assert.True(peerA.IsRetired);
        Assert.False(peerA.IsVerified);
        Assert.False(peerB.IsVerified);       // the video source gates frames on this same object: none go to B
        Assert.False(manager.PeerVerified);
        Assert.Equal(SessionCapabilities.None, manager.GrantedCapabilities);
        Assert.Empty(verified);
        controller.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
    }

    [Fact]
    public void RetiredPeer_CanNeverBecomeVerified()
    {
        var peer = new PeerContext();
        peer.Retire();

        Assert.False(peer.TryVerify());
        Assert.False(peer.IsVerified);
    }

    [Fact]
    public void WithoutBindingConfigured_BehaviourIsUnchanged()
    {
        var controller = Substitute.For<IInputController>();
        controller.IsEnabled.Returns(true);
        var manager = new DataChannelManager(Substitute.For<ILogger>(), controller);

        Assert.False(manager.RequireE2EBinding);
        Assert.Equal(SessionCapabilities.View | SessionCapabilities.Control, manager.GrantedCapabilities);
    }
}
