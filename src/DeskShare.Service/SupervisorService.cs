using System.Runtime.Versioning;
using System.ServiceProcess;
using DeskShare.Unattended;

namespace DeskShare.Service;

/// <summary>The SCM-facing service. It supervises the agent; it never captures or streams anything itself.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SupervisorService : ServiceBase
{
    private CancellationTokenSource? _cts;
    private Task? _run;
    private Supervisor? _supervisor;

    public SupervisorService()
    {
        ServiceName = UnattendedPaths.ServiceName;
        CanHandleSessionChangeEvent = true; // logon / logoff / console connect / disconnect -> OnSessionChange
    }

    protected override void OnStart(string[] args)
    {
        // Agent location comes from our own install folder, never from config (security: only admin-writable binaries run as SYSTEM).
        var agent = UnattendedPaths.ResolveAgentPath(AppContext.BaseDirectory);
        var agentArgs = $"--config \"{UnattendedPaths.ConfigPath}\" \"--Serilog:WriteTo:1:Args:path={Path.Combine(UnattendedPaths.LogDir, "agent-.log")}\"";

        _supervisor = new Supervisor(SessionLauncher.ActiveConsoleSessionId, session => SessionLauncher.Start(session, agent, agentArgs));
        _cts = new CancellationTokenSource();
        _run = Task.Run(() => _supervisor.RunAsync(_cts.Token));
        FileLog.Write("Service started");
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        FileLog.Write($"Session change: {change.Reason} (session {change.SessionId})");
        _supervisor?.Poke();
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        FileLog.Write("Service stopped");
    }
}
