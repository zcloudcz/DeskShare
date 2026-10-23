using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using DeskShare.Core.Auth;
using DeskShare.Unattended;

namespace DeskShare.Service;

/// <summary>
/// Elevated "set-password" / "clear-password": writes or deletes %ProgramData%\DeskShare\permanent.key and restarts
/// the service so the agent picks the change up. The password itself never reaches this process, only the keys the
/// desktop app derived from it (see <see cref="PasswordSetup"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PasswordCommand
{
    private static string KeyPath => Path.Combine(UnattendedPaths.DataDir, PermanentKeyFile.FileName);

    // Bounds the whole exchange, so a stalled or hostile peer cannot keep an elevated process waiting forever.
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// <c>set-password [--pipe &lt;name&gt; --owner-pid &lt;pid&gt;]</c>: the keys come over that pipe from the app
    /// process with that id, else over stdin/stdout.
    /// </summary>
    public static int Set(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var pipeName = Arg("--pipe");
        if (pipeName is null)
            return Run(Console.In, Console.Out);
        if (!int.TryParse(Arg("--owner-pid"), out var ownerPid))
        {
            FileLog.Write("ERROR: set-password: --pipe needs --owner-pid.");
            return 2;
        }

        // The name is random and was handed to us on the UAC command line: not a secret, only a meeting point.
        // Identification level: the pipe server may ask who we are, but cannot act as this elevated process.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try { pipe.Connect(10_000); }
        catch (System.TimeoutException) { FileLog.Write("ERROR: set-password: the app's pipe did not answer."); return 1; }

        // Whoever serves a pipe of that name now is not necessarily the app (it may have closed its pipe and someone
        // else created one). Talk only to the process that launched us (see PasswordSetup); nothing is sent before.
        var serverPid = PipePeer.ServerProcessId(pipe);
        if (serverPid != ownerPid)
        {
            FileLog.Write($"ERROR: set-password: the pipe is served by process {serverPid?.ToString() ?? "?"}, expected {ownerPid}; nothing sent.");
            return 1;
        }

        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true);
        return Run(reader, writer);
    }

    private static int Run(TextReader input, TextWriter output)
    {
        var problem = PrepareDataDir();
        var serverId = problem is null ? PasswordSetup.ReadServerId(UnattendedPaths.DataDir) : null;
        if (problem is null && serverId is null)
            problem = "The service has not created its Server ID yet. Enable unattended access, wait a minute and try again.";
        if (problem != null)
        {
            FileLog.Write("ERROR: set-password: " + problem);
            PasswordSetup.Reply(output, problem);
            return 1;
        }

        // Synchronous pipe reads cannot be cancelled, so the exchange runs on a worker and we stop waiting after the
        // timeout; returning from Main then ends the process, the stuck read included.
        var exchange = Task.Run(() => PasswordSetup.RunHelper(input, output, serverId!,
            (authKey, e2eKey) => PermanentKeyFile.Save(KeyPath, serverId!, authKey, e2eKey)));
        var error = exchange.Wait(ExchangeTimeout) ? exchange.Result : "timed out waiting for the app";
        if (error != null)
        {
            FileLog.Write("ERROR: set-password: " + error);
            return 1;
        }
        FileLog.Write($"Permanent password set for {serverId}.");
        RestartService();
        return 0;
    }

    public static int Clear()
    {
        var problem = PrepareDataDir();
        if (problem != null) { FileLog.Write("ERROR: clear-password: " + problem); return 1; }
        File.Delete(KeyPath);
        FileLog.Write("Permanent password removed.");
        RestartService();
        return 0;
    }

    // Same rules as the installer: admin only, and the data folder must be the protected one before we write a secret into it.
    private static string? PrepareDataDir()
    {
        FileLog.FileEnabled = false; // see Installer.Install: no file log until the folder is proven ours
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            return "Administrator rights are required.";
        try { Installer.PrepareDataDir(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return ex.Message; }
        FileLog.FileEnabled = true;
        return null;
    }

    // The agent reads the key file when it starts; restarting the service restarts the agent. Not installed: nothing
    // to restart, the file is used on the next start.
    private static void RestartService()
    {
        try
        {
            using var sc = new ServiceController(UnattendedPaths.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            sc.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            FileLog.Write("Could not restart the service, the change applies on its next start: " + ex.Message);
        }
    }
}
