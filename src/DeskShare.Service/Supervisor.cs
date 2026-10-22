using System.Diagnostics;
using DeskShare.Unattended;

namespace DeskShare.Service;

/// <summary>
/// Keeps exactly one agent alive in the active console session: starts it, restarts it with backoff when it exits,
/// moves it when the console session changes. The service does no capture itself.
/// </summary>
public sealed class Supervisor
{
    private readonly Func<uint> _activeSession;
    private readonly Func<uint, Process?> _start;
    private readonly RestartBackoff _backoff = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private Process? _agent;
    private uint _agentSession;
    private DateTime _agentStarted;
    private DateTime _noStartBefore = DateTime.MinValue;

    public Supervisor(Func<uint> activeSession, Func<uint, Process?> start)
    {
        _activeSession = activeSession;
        _start = start;
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
                Tick();
                // Poll is the safety net (agent exit has no event here, boot-time session races); Poke makes session changes instant.
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            StopAgent();
        }
    }

    private void Tick()
    {
        if (_agent is { HasExited: true })
        {
            var ranFor = DateTime.UtcNow - _agentStarted;
            var delay = _backoff.NextDelay(ranFor);
            FileLog.Write($"Agent exited (code {_agent.ExitCode}, ran {ranFor.TotalSeconds:F0}s), restart in {delay.TotalSeconds:F0}s");
            _agent.Dispose();
            _agent = null;
            _noStartBefore = DateTime.UtcNow + delay;
        }

        var active = _activeSession();
        var action = SessionPolicy.Decide(_agent is null ? null : _agentSession, active);
        switch (action)
        {
            case SupervisorAction.Stop:
                FileLog.Write("No usable console session, stopping agent");
                StopAgent();
                break;
            case SupervisorAction.Restart:
                FileLog.Write($"Console session changed {_agentSession} -> {active}, moving agent");
                StopAgent();
                _backoff.Reset();
                _noStartBefore = DateTime.MinValue;
                StartAgent(active);
                break;
            case SupervisorAction.Start when DateTime.UtcNow >= _noStartBefore:
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
            _agentStarted = DateTime.UtcNow;
            if (_agent != null)
                FileLog.Write($"Agent started in session {session} (pid {_agent.Id})");
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
        _noStartBefore = DateTime.UtcNow + delay;
        FileLog.Write($"Agent start failed ({reason}), retry in {delay.TotalSeconds:F0}s");
    }

    private void StopAgent()
    {
        // ponytail: hard kill; the agent holds no state worth a graceful shutdown (viewer reconnects on its own)
        try { _agent?.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
        _agent?.Dispose();
        _agent = null;
    }
}
