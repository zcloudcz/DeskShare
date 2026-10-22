namespace DeskShare.Unattended;

/// <summary>An agent process the supervisor owns. Implementations must keep the OS handle open until Dispose, so
/// <see cref="ExitCode"/> stays readable after the process is gone.</summary>
public interface IAgentProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    void Kill();
}

/// <summary>
/// Keeps exactly one agent alive in the active console session: starts it, restarts it with backoff when it exits,
/// moves it when the console session changes. The service does no capture itself.
/// </summary>
public sealed class Supervisor
{
    private readonly Func<uint> _activeSession;
    private readonly Func<uint, IAgentProcess?> _start;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _clock;
    private readonly RestartBackoff _backoff;
    private readonly SemaphoreSlim _wake = new(0, 1);

    private IAgentProcess? _agent;
    private uint _agentSession;
    private DateTime _agentStarted;
    private DateTime _noStartBefore = DateTime.MinValue;

    public Supervisor(Func<uint> activeSession, Func<uint, IAgentProcess?> start, Action<string> log,
        RestartBackoff? backoff = null, Func<DateTime>? clock = null)
    {
        _activeSession = activeSession;
        _start = start;
        _log = log;
        _backoff = backoff ?? new RestartBackoff();
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Session change notification: re-evaluate now instead of waiting for the next poll.</summary>
    public void Poke()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { /* a wake-up is already pending */ }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex) { _log("Supervisor tick failed (continuing): " + ex.Message); } // the loop must never die
                // Poll is the safety net (agent exit has no event here, boot-time session races); Poke makes session changes instant.
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            StopAgent();
        }
    }

    /// <summary>One evaluation step; public so tests can drive it with a fake clock.</summary>
    public void Tick()
    {
        if (_agent is { HasExited: true })
        {
            var ranFor = _clock() - _agentStarted;
            var delay = _backoff.NextDelay(ranFor);
            _log($"Agent exited (code {_agent.ExitCode}, ran {ranFor.TotalSeconds:F0}s), restart in {delay.TotalSeconds:F0}s");
            _agent.Dispose();
            _agent = null;
            _noStartBefore = _clock() + delay;
        }

        var active = _activeSession();
        var action = SessionPolicy.Decide(_agent is null ? null : _agentSession, active);
        switch (action)
        {
            case SupervisorAction.Stop:
                _log("No usable console session, stopping agent");
                StopAgent();
                break;
            case SupervisorAction.Restart:
                _log($"Console session changed {_agentSession} -> {active}, moving agent");
                StopAgent();
                _backoff.Reset();
                _noStartBefore = DateTime.MinValue;
                StartAgent(active);
                break;
            case SupervisorAction.Start when _clock() >= _noStartBefore:
                StartAgent(active);
                break;
        }
    }

    private void StartAgent(uint session)
    {
        try
        {
            _agent = _start(session);
            _agentSession = session;
            _agentStarted = _clock();
            if (_agent != null)
                _log($"Agent started in session {session} (pid {_agent.Id})");
            else
                ScheduleRetry("start returned nothing");
        }
        catch (Exception ex)
        {
            ScheduleRetry(ex.Message);
        }
    }

    private void ScheduleRetry(string reason)
    {
        var delay = _backoff.NextDelay(TimeSpan.Zero);
        _noStartBefore = _clock() + delay;
        _log($"Agent start failed ({reason}), retry in {delay.TotalSeconds:F0}s");
    }

    private void StopAgent()
    {
        // ponytail: hard kill; the agent holds no state worth a graceful shutdown (viewer reconnects on its own)
        try { _agent?.Kill(); } catch (Exception) { /* already gone */ }
        _agent?.Dispose();
        _agent = null;
    }
}
