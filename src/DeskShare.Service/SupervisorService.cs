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

        _supervisor = new Supervisor(SessionLauncher.ActiveConsoleSessionId, session => SessionLauncher.Start(session, agent, agentArgs), FileLog.Write, onStatus: WriteStatus);
        _cts = new CancellationTokenSource();
        _run = Task.Run(() => _supervisor.RunAsync(_cts.Token));
        // If the supervisor ever dies, SCM would keep showing "Running" with nothing supervised. Exit non-zero instead so
        // the service recovery action (restart) kicks in.
        _run.ContinueWith(t =>
        {
            FileLog.Write("Supervisor faulted, exiting: " + t.Exception?.GetBaseException().Message);
            Environment.Exit(1);
        }, TaskContinuationOptions.OnlyOnFaulted);
        FileLog.Write("Service started");
    }

    /// <summary>Own version as the installer sees it (FileVersionInfo of the exe), so it can tell the NEW service from the old one.</summary>
    internal static string OwnVersion() => System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion ?? "";

    /// <summary>status.json is how the installer confirms that this version really started its agent (see ServiceStatus).</summary>
    private static void WriteStatus(string state, int? agentPid)
    {
        var path = Path.Combine(UnattendedPaths.DataDir, "status.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, new ServiceStatus(OwnVersion(), state, agentPid, DateTime.UtcNow).ToJson());
        File.Move(tmp, path, overwrite: true);
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
