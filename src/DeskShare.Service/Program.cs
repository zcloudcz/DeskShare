using System.Diagnostics;
using System.ServiceProcess;
using DeskShare.Service;
using DeskShare.Unattended;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("DeskShare.Service is Windows-only.");
    return 1;
}

var mode = args.Length > 0 ? args[0] : "";
switch (mode)
{
    case "install":
    case "update": // same operation: replaces the files and (re)starts the service
        return Installer.Install(AppContext.BaseDirectory);
    case "uninstall":
        return Installer.Uninstall();
    case PasswordSetup.SetVerb:
        return PasswordCommand.Set(args);
    case PasswordSetup.ClearVerb:
        return PasswordCommand.Clear();
    case "--console":
        return RunConsole(args);
    case "":
        if (Environment.UserInteractive)
        {
            Console.WriteLine("Usage: DeskShare.Service install | update | uninstall | set-password [--pipe <name>] | clear-password | --console [--agent <exe>] [--config <json>]");
            return 0;
        }
        ServiceBase.Run(new SupervisorService());
        return 0;
    default:
        Console.Error.WriteLine("Unknown argument: " + mode);
        return 2;
}

// Debug mode without the SCM: supervise in the CURRENT session (no token switching), Ctrl+C to stop.
// --agent is honoured only here; the installed service always resolves the agent inside its own folder.
static int RunConsole(string[] args)
{
    string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    var agent = Arg("--agent") ?? UnattendedPaths.ResolveAgentPath(AppContext.BaseDirectory);
    var config = Arg("--config");
    var session = (uint)Process.GetCurrentProcess().SessionId;
    FileLog.Write($"Console mode, session {session}, agent {agent}");

    var supervisor = new Supervisor(() => session, _ =>
    {
        var p = Process.Start(new ProcessStartInfo(agent, config is null ? "" : $"--config \"{config}\"") { UseShellExecute = false });
        return p is null ? null : new ProcessAgent(p);
    }, FileLog.Write);
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    supervisor.RunAsync(cts.Token).GetAwaiter().GetResult();
    return 0;
}
