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

    /// <summary><c>set-password [--pipe &lt;name&gt;]</c>: the keys come over that pipe from the app, else over stdin/stdout.</summary>
    public static int Set(string[] args)
    {
        var i = Array.IndexOf(args, "--pipe");
        var pipeName = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (pipeName is null)
            return Run(Console.In, Console.Out);

        // The app (same user, not elevated) created the pipe so that only this user can open it. The name is random
        // and was handed to us on the UAC command line, which is fine: it is not a secret, only a meeting point.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        try { pipe.Connect(10_000); }
        catch (System.TimeoutException) { FileLog.Write("ERROR: set-password: the app's pipe did not answer."); return 1; }
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

        var error = PasswordSetup.RunHelper(input, output, serverId!, (authKey, e2eKey) => PermanentKeyFile.Save(KeyPath, serverId!, authKey, e2eKey));
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
