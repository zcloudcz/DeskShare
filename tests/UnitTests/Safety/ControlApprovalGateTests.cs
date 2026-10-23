using DeskShare.Core.Auth;
using DeskShare.Core.Safety;

namespace DeskShare.UnitTests.Safety;

/// <summary>The approval state machine: allow, deny, timeout, and "the viewer left while the question was open".</summary>
public sealed class ControlApprovalGateTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Allow_ReturnsAllowed()
    {
        var gate = new ControlApprovalGate(_ => Task.FromResult(true));

        Assert.Equal(ApprovalOutcome.Allowed, await gate.RequestAsync(new PeerContext()).WaitAsync(Guard));
    }

    [Fact]
    public async Task Deny_ReturnsDenied()
    {
        var gate = new ControlApprovalGate(_ => Task.FromResult(false));

        Assert.Equal(ApprovalOutcome.Denied, await gate.RequestAsync(new PeerContext()).WaitAsync(Guard));
    }

    [Fact]
    public async Task NoAnswer_TimesOut_AndTheQuestionIsToldToClose()
    {
        CancellationToken seen = default;
        var gate = new ControlApprovalGate(async ct =>
        {
            seen = ct;
            await Task.Delay(Timeout.Infinite, ct); // a prompt nobody answers
            return true;
        }, TimeSpan.FromMilliseconds(100));

        var outcome = await gate.RequestAsync(new PeerContext()).WaitAsync(Guard);

        Assert.Equal(ApprovalOutcome.TimedOut, outcome);
        Assert.True(seen.IsCancellationRequested); // the dialog gets closed
    }

    [Fact]
    public async Task ViewerLeavesWhileQuestionIsOpen_AnswerAllowIsIgnored()
    {
        var answer = new TaskCompletionSource<bool>();
        var gate = new ControlApprovalGate(_ => answer.Task);
        var peer = new PeerContext();

        var pending = gate.RequestAsync(peer);
        peer.Retire(); // the viewer disconnected
        answer.SetResult(true); // ...and the person clicks Allow anyway

        Assert.Equal(ApprovalOutcome.Ignored, await pending.WaitAsync(Guard));
    }

    [Fact]
    public async Task CancelPending_IgnoresTheRequest_AndClosesTheQuestion()
    {
        var started = new TaskCompletionSource<CancellationToken>();
        var gate = new ControlApprovalGate(async ct =>
        {
            started.SetResult(ct);
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        });

        var pending = gate.RequestAsync(new PeerContext());
        var token = await started.Task.WaitAsync(Guard);
        gate.CancelPending();

        Assert.Equal(ApprovalOutcome.Ignored, await pending.WaitAsync(Guard));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task BrokenPrompt_NeverGrants()
    {
        var gate = new ControlApprovalGate(_ => throw new InvalidOperationException("no window"));

        Assert.Equal(ApprovalOutcome.Denied, await gate.RequestAsync(new PeerContext()).WaitAsync(Guard));
    }

    [Fact]
    public async Task SecondRequestFromTheSamePeer_JoinsTheOpenQuestion()
    {
        var asked = 0;
        var answer = new TaskCompletionSource<bool>();
        var gate = new ControlApprovalGate(_ => { Interlocked.Increment(ref asked); return answer.Task; });
        var peer = new PeerContext();

        var first = gate.RequestAsync(peer);
        var second = gate.RequestAsync(peer);
        answer.SetResult(true);

        Assert.Equal(ApprovalOutcome.Allowed, await first.WaitAsync(Guard));
        Assert.Equal(ApprovalOutcome.Allowed, await second.WaitAsync(Guard));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task ARequestFromANewPeer_ReplacesTheOldQuestion()
    {
        var gate = new ControlApprovalGate(async ct => { await Task.Delay(Timeout.Infinite, ct); return true; }, TimeSpan.FromSeconds(30));

        var old = gate.RequestAsync(new PeerContext());
        _ = gate.RequestAsync(new PeerContext());

        Assert.Equal(ApprovalOutcome.Ignored, await old.WaitAsync(Guard));
    }

    [Fact]
    public async Task Resolved_ReportsTheOutcome()
    {
        var gate = new ControlApprovalGate(_ => Task.FromResult(false));
        var reported = new TaskCompletionSource<ApprovalOutcome>();
        gate.Resolved += o => reported.TrySetResult(o);

        await gate.RequestAsync(new PeerContext()).WaitAsync(Guard);

        Assert.Equal(ApprovalOutcome.Denied, await reported.Task.WaitAsync(Guard));
    }
}
