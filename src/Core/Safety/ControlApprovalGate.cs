using DeskShare.Core.Auth;

namespace DeskShare.Core.Safety;

/// <summary>How a request for control ended.</summary>
public enum ApprovalOutcome
{
    /// <summary>The person at the computer clicked Allow.</summary>
    Allowed,
    /// <summary>The person at the computer clicked Deny (or the prompt failed: we fail closed).</summary>
    Denied,
    /// <summary>Nobody answered in time; counts as a denial.</summary>
    TimedOut,
    /// <summary>The answer no longer matters (the viewer left, or another viewer took its place): nothing is granted.</summary>
    Ignored,
}

/// <summary>
/// "A viewer wants to control your mouse and keyboard. [Deny] [Allow]": asks the person at the computer once per
/// request and turns the answer into an <see cref="ApprovalOutcome"/>.
/// </summary>
/// <remarks>
/// For junior developers: the prompt is a delegate so this class has no UI code and can be tested with a fake.
/// The delegate receives a token that is cancelled when the time runs out, when the request is cancelled
/// (<see cref="CancelPending"/>) or when another viewer replaces the asking one; the prompt must then close itself.
/// If the viewer disconnects while the question is open, the answer is thrown away (<see cref="ApprovalOutcome.Ignored"/>)
/// so a click on Allow cannot hand control to someone who is no longer there.
/// </remarks>
public sealed class ControlApprovalGate
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<bool>> _ask;
    private readonly TimeSpan _timeout;
    private readonly object _lock = new();
    private Pending? _pending;

    /// <param name="ask">Shows the question; true = Allow. It should end when the token is cancelled.</param>
    /// <param name="timeout">After this the request counts as denied (30 s by default).</param>
    public ControlApprovalGate(Func<CancellationToken, Task<bool>> ask, TimeSpan? timeout = null)
    {
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Raised when a request ended (also for <see cref="ApprovalOutcome.Ignored"/>), on a thread-pool thread.</summary>
    public event Action<ApprovalOutcome>? Resolved;

    /// <summary>
    /// Asks for control on behalf of <paramref name="peer"/>. A repeated request from the same peer while the question is
    /// open joins it (one prompt, not two); a request from another peer replaces the open question.
    /// </summary>
    public Task<ApprovalOutcome> RequestAsync(PeerContext peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        lock (_lock)
        {
            if (_pending is { } open)
            {
                if (ReferenceEquals(open.Peer, peer))
                    return open.Task;
                open.Cancel(); // the old viewer is gone or replaced: its question is moot
            }

            var pending = new Pending(peer);
            pending.Task = RunAsync(pending);
            _pending = pending;
            return pending.Task;
        }
    }

    /// <summary>Dismisses the open question (the viewer disconnected). Its outcome becomes <see cref="ApprovalOutcome.Ignored"/>.</summary>
    public void CancelPending()
    {
        Pending? open;
        lock (_lock) open = _pending;
        open?.Cancel();
    }

    private async Task<ApprovalOutcome> RunAsync(Pending pending)
    {
        // Runs the prompt on the thread pool so a prompt that blocks while building its window cannot stall the caller
        // (the caller is the data channel message handler).
        await Task.Yield();
        var outcome = ApprovalOutcome.Denied;
        try
        {
            var answer = Task.Run(() => _ask(pending.Cts.Token));
            var timer = Task.Delay(_timeout, pending.Cts.Token);
            var first = await Task.WhenAny(answer, timer).ConfigureAwait(false);

            if (pending.CancelledByUs || pending.Peer.IsRetired)
            {
                outcome = ApprovalOutcome.Ignored;
            }
            else if (first == answer)
            {
                outcome = await answer.ConfigureAwait(false) ? ApprovalOutcome.Allowed : ApprovalOutcome.Denied;
            }
            else
            {
                outcome = ApprovalOutcome.TimedOut;
            }
        }
        catch (Exception)
        {
            // A broken prompt must never grant control.
            outcome = pending.CancelledByUs ? ApprovalOutcome.Ignored : ApprovalOutcome.Denied;
        }
        finally
        {
            pending.Cts.Cancel(); // stops the timer and closes a prompt that is still open (timeout case)
            lock (_lock)
            {
                if (ReferenceEquals(_pending, pending)) _pending = null;
            }
        }

        // Re-check after the prompt closed: the viewer may have left while the person was reading.
        if (outcome == ApprovalOutcome.Allowed && pending.Peer.IsRetired)
            outcome = ApprovalOutcome.Ignored;

        try { Resolved?.Invoke(outcome); } catch (Exception) { /* a faulty subscriber must not change the outcome */ }
        return outcome;
    }

    private sealed class Pending(PeerContext peer)
    {
        public PeerContext Peer { get; } = peer;
        public CancellationTokenSource Cts { get; } = new();
        public Task<ApprovalOutcome> Task { get; set; } = null!;
        private volatile bool _cancelledByUs;
        public bool CancelledByUs => _cancelledByUs;

        public void Cancel()
        {
            _cancelledByUs = true;
            try { Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
