using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using DeskShare.Unattended;

namespace DeskShare.Service;

/// <summary>
/// Elevated install / update / uninstall. Runs from the app's (user-writable) copy of the service folder and copies it to
/// %ProgramFiles%\DeskShare\Service, so the LocalSystem service only ever executes admin-protected binaries.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Installer
{
    public static int Install(string sourceDir)
    {
        if (!IsAdmin()) return Fail("Administrator rights are required.");
        sourceDir = Path.GetFullPath(sourceDir);
        var target = UnattendedPaths.InstallDir;
        if (UnattendedPaths.IsInside(target, sourceDir) || Path.TrimEndingDirectorySeparator(sourceDir).Equals(Path.TrimEndingDirectorySeparator(target), StringComparison.OrdinalIgnoreCase))
            return Fail("Source is already the install folder.");
        if (!File.Exists(Path.Combine(sourceDir, UnattendedPaths.ServiceExeName)) || !File.Exists(UnattendedPaths.ResolveAgentPath(sourceDir)))
            return Fail("Service or agent binaries missing in " + sourceDir);

        StopService();

        // Replace the folder wholesale so files dropped from a newer version do not linger. Program Files' inherited ACL protects the result.
        for (var i = 0; ; i++)
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, true); break; }
            catch (IOException) when (i < 10) { Thread.Sleep(500); } // exe still locked while the service winds down
        }
        CopyDir(sourceDir, target);

        PrepareDataDir();

        // sc.exe instead of the SCM API: no extra package and the semantics are well known. "binPath= " needs the space after '='.
        var bin = $"\"{Path.Combine(target, UnattendedPaths.ServiceExeName)}\"";
        var exists = Sc("query", UnattendedPaths.ServiceName) == 0;
        if (Sc(exists ? "config" : "create", UnattendedPaths.ServiceName, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem",
                "DisplayName=", "DeskShare unattended access") != 0)
            return Fail("sc create/config failed");
        Sc("description", UnattendedPaths.ServiceName, "Keeps the DeskShare screen sender running in the active session.");
        // Restart on crash: after 5 s, 5 s, 30 s; failure counter resets after a day.
        Sc("failure", UnattendedPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
        if (Sc("start", UnattendedPaths.ServiceName) != 0)
            return Fail("sc start failed");
        FileLog.Write("Installed and started.");
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsAdmin()) return Fail("Administrator rights are required.");
        StopService();
        Sc("delete", UnattendedPaths.ServiceName);
        try { if (Directory.Exists(UnattendedPaths.InstallDir)) Directory.Delete(UnattendedPaths.InstallDir, true); }
        catch (IOException ex) { FileLog.Write("Could not remove install folder: " + ex.Message); }
        // %ProgramData%\DeskShare (server id, owner secret, config) is kept on purpose so re-enabling keeps the same identity.
        FileLog.Write("Uninstalled.");
        return 0;
    }

    private static void PrepareDataDir()
    {
        var dir = UnattendedPaths.DataDir;
        Directory.CreateDirectory(dir);
        var sec = new DirectorySecurity();
        sec.SetSecurityDescriptorSddlForm(AclSpec.DataDirSddl);
        new DirectoryInfo(dir).SetAccessControl(sec);

        // ponytail: U1 keeps the rotating passkey (read it from the agent log). Permanent password + auth arrive in U3.
        if (!File.Exists(UnattendedPaths.ConfigPath))
            File.WriteAllText(UnattendedPaths.ConfigPath, """
                {
                  "Signaling": { "ServerUrl": "wss://app.deskshare.zcloud.cz/signal" },
                  "Security": { "EnableRemoteControl": false, "TrustClientPermanent": false }
                }
                """);
    }

    private static void StopService()
    {
        try
        {
            using var sc = new ServiceController(UnattendedPaths.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException) { /* service not installed */ }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static int Fail(string message)
    {
        FileLog.Write("ERROR: " + message);
        return 1;
    }
}
