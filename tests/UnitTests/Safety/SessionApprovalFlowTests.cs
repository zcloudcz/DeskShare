using DeskShare.Core.Auth;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Safety;
using DeskShare.Core.WebRTC;
using NSubstitute;
using Serilog;

namespace DeskShare.UnitTests.Safety;

/// <summary>
/// The approval rules as the data channel applies them: control only after the host's Allow, per session, and the
/// unattended admission question that holds a viewer back before it gets any capability.
/// Sends cannot be observed without a real network peer, so these tests check the effects that matter: what reached
/// the input controller and which events fired.
/// </summary>
public sealed class SessionApprovalFlowTests
{
    private const string BindJson = """{"type":"e2e_bind","v":1,"role":"viewer","mac":"abcd"}""";
    private const string AskInteractive = """{"type":"authorization_request","interactive":true}""";
    private const string AskAutomatic = """{"type":"authorization_request"}""";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static IInputController Controller()
    {
        var enabled = false;
        var controller = Substitute.For<IInputController>();
        controller.IsEnabled.Returns(_ => enabled);
        controller.RequestAuthorizationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => { enabled = true; return Task.FromResult(true); });
        controller.When(c => c.RevokeAuthorization()).Do(_ => enabled = false);
        return controller;
    }

    private static (DataChannelManager Manager, IInputController Controller) VerifiedManager(Func<CancellationToken, Task<bool>>? ask, TimeSpan? timeout = null)
    {
        var controller = Controller();
        var manager = new DataChannelManager(Substitute.For<ILogger>(), controller)
        {
            VerifyViewerBind = (_, _) => "sender-mac",
        };
        if (ask != null)
            manager.ControlApproval = new ControlApprovalGate(ask, timeout);
        manager.ProcessInputMessage(BindJson);
        Assert.True(manager.PeerVerified);
        return (manager, controller);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Guard;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Allow_AuthorizesControl()
    {
        var (manager, controller) = VerifiedManager(_ => Task.FromResult(true));

        manager.ProcessInputMessage(AskInteractive);

        await WaitUntil(() => controller.IsEnabled);
        await controller.Received(1).RequestAuthorizationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deny_NeverAuthorizes()
    {
        var resolved = new TaskCompletionSource<ApprovalOutcome>();
        var (manager, controller) = VerifiedManager(_ => Task.FromResult(false));
        manager.ControlApproval!.Resolved += o => resolved.TrySetResult(o);

        manager.ProcessInputMessage(AskInteractive);

        Assert.Equal(ApprovalOutcome.Denied, await resolved.Task.WaitAsync(Guard));
        await controller.DidNotReceive().RequestAuthorizationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoAnswer_TimesOutAndNeverAuthorizes()
    {
        var resolved = new TaskCompletionSource<ApprovalOutcome>();
        var (manager, controller) = VerifiedManager(async ct => { await Task.Delay(Timeout.Infinite, ct); return true; }, TimeSpan.FromMilliseconds(100));
        manager.ControlApproval!.Resolved += o => resolved.TrySetResult(o);

        manager.ProcessInputMessage(AskInteractive);

        Assert.Equal(ApprovalOutcome.TimedOut, await resolved.Task.WaitAsync(Guard));
        await controller.DidNotReceive().RequestAuthorizationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutomaticRequest_DoesNotAsk_OnlyInteractiveOnesDo()
    {
        var asked = 0;
        var (manager, controller) = VerifiedManager(_ => { Interlocked.Increment(ref asked); return Task.FromResult(true); });

        manager.ProcessInputMessage(AskAutomatic); // the viewer's capability probe right after connecting
        await Task.Delay(200);

        Assert.Equal(0, asked);
        await controller.DidNotReceive().RequestAuthorizationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ViewerGoneWhilePending_AllowIsIgnored_AndTheQuestionIsClosed()
    {
        var asking = new TaskCompletionSource<CancellationToken>();
        var answer = new TaskCompletionSource<bool>();
        var (manager, controller) = VerifiedManager(ct => { asking.SetResult(ct); return answer.Task; });

        manager.ProcessInputMessage(AskInteractive);
        var token = await asking.Task.WaitAsync(Guard);
        manager.BeginPeer(new PeerContext()); // another connection replaced the viewer
        answer.SetResult(true);                // the person clicks Allow anyway
        await Task.Delay(200);

        Assert.True(token.IsCancellationRequested);
        await controller.DidNotReceive().RequestAuthorizationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ControlEndsWithTheSession_SoTheNextViewerMustBeApprovedAgain()
    {
        var (manager, controller) = VerifiedManager(_ => Task.FromResult(true));
        var ended = 0;
        manager.ViewerEnded += (_, _) => ended++;
        manager.ProcessInputMessage(AskInteractive);
        await WaitUntil(() => controller.IsEnabled);

        manager.BeginPeer(new PeerContext());

        Assert.False(controller.IsEnabled);
        Assert.Equal(1, ended);
    }

    [Fact]
    public async Task WithoutApproval_ControlStaysAsTheHostSetItUp()
    {
        var (manager, controller) = VerifiedManager(ask: null);
        await controller.RequestAuthorizationAsync("auto-authorized"); // headless: authorized once at start

        manager.BeginPeer(new PeerContext());

        controller.DidNotReceive().RevokeAuthorization();
        Assert.True(controller.IsEnabled);
    }

    // ---- Unattended admission ----

    private static DataChannelManager ManagerWithAdmission(Func<CancellationToken, Task<bool>> admit, TimeSpan? timeout = null)
    {
        var manager = new DataChannelManager(Substitute.For<ILogger>(), Controller())
        {
            VerifyViewerBind = (_, _) => "sender-mac",
            AdmitViewer = admit,
        };
        if (timeout != null) manager.AdmissionTimeout = timeout.Value;
        return manager;
    }

    [Fact]
    public async Task Admission_HoldsTheViewerBackUntilTheHostAnswers()
    {
        var answer = new TaskCompletionSource<bool>();
        var manager = ManagerWithAdmission(_ => answer.Task);
        var verified = new TaskCompletionSource();
        manager.ViewerVerified += (_, _) => verified.TrySetResult();

        manager.ProcessInputMessage(BindJson);
        await Task.Delay(100);
        Assert.False(manager.PeerVerified); // no video, no grants while the question is open

        answer.SetResult(true);
        await verified.Task.WaitAsync(Guard);
        Assert.True(manager.PeerVerified);
    }

    [Fact]
    public async Task Admission_Denied_NeverVerifies()
    {
        var manager = ManagerWithAdmission(_ => Task.FromResult(false));
        var denied = new TaskCompletionSource();
        manager.ViewerAdmissionDenied += (_, _) => denied.TrySetResult();

        manager.ProcessInputMessage(BindJson);

        await denied.Task.WaitAsync(Guard);
        Assert.False(manager.PeerVerified);
    }

    [Fact]
    public async Task Admission_NoAnswer_IsDeniedAfterTheTimeout()
    {
        var manager = ManagerWithAdmission(async ct => { await Task.Delay(Timeout.Infinite, ct); return true; }, TimeSpan.FromMilliseconds(100));
        var denied = new TaskCompletionSource();
        manager.ViewerAdmissionDenied += (_, _) => denied.TrySetResult();

        manager.ProcessInputMessage(BindJson);

        await denied.Task.WaitAsync(Guard);
        Assert.False(manager.PeerVerified);
    }

    [Fact]
    public async Task Admission_AskedOncePerConnection_EvenIfTheViewerRepeatsItsBind()
    {
        var asked = 0;
        var answer = new TaskCompletionSource<bool>();
        var manager = ManagerWithAdmission(_ => { Interlocked.Increment(ref asked); return answer.Task; });

        manager.ProcessInputMessage(BindJson);
        manager.ProcessInputMessage(BindJson);
        answer.SetResult(true);
        await WaitUntil(() => manager.PeerVerified);

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Admission_ViewerReplacedWhileAsking_IsIgnoredForTheNewConnection()
    {
        var asking = new TaskCompletionSource<CancellationToken>();
        var answer = new TaskCompletionSource<bool>();
        var manager = ManagerWithAdmission(ct => { asking.TrySetResult(ct); return answer.Task; });
        var verified = 0;
        manager.ViewerVerified += (_, _) => verified++;

        manager.ProcessInputMessage(BindJson);
        var token = await asking.Task.WaitAsync(Guard);
        manager.BeginPeer(new PeerContext()); // a newer connection took over
        answer.SetResult(true);                 // the answer for the OLD one arrives late
        await Task.Delay(200);

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(0, verified);
        Assert.False(manager.PeerVerified); // the new connection did not inherit the old admission
    }

    [Fact]
    public async Task WithoutAdmission_ViewerIsVerifiedImmediately()
    {
        var manager = ManagerWithAdmission(_ => Task.FromResult(true));
        manager.AdmitViewer = null;

        manager.ProcessInputMessage(BindJson);
        await Task.Yield();

        Assert.True(manager.PeerVerified);
    }
}
