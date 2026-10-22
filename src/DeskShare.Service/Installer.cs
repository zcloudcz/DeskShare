using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using DeskShare.Unattended;

namespace DeskShare.Service;

/// <summary>
/// Elevated install / update / uninstall. Runs from the app's (user-writable) copy of the service folder, so nothing
/// there is trusted: files are copied into a protected staging folder under Program Files, verified THERE, and only
/// then swapped in. The LocalSystem service therefore only ever executes admin-protected binaries.
/// Every process started here uses an absolute path (System32), never PATH or the current directory.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Installer
{
    public static int Install(string sourceDir)
    {
        FileLog.FileEnabled = false; // console only until %ProgramData%\DeskShare is proven ours (see PrepareDataDir)
        if (!IsAdmin()) return Fail("Administrator rights are required.");
        string? aside;
        try { aside = PrepareDataDir(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return Fail(ex.Message, 3); }
        FileLog.FileEnabled = true;
        if (aside != null) FileLog.Write($"Existing {UnattendedPaths.DataDir} was not protected as expected, moved to {aside}; configuration reset to defaults.");

        sourceDir = Path.GetFullPath(sourceDir);
        var live = UnattendedPaths.InstallDir;
        var staging = live + ".new";
        var old = live + ".old";
        foreach (var own in new[] { live, staging, old })
            if (UnattendedPaths.IsInside(own, sourceDir) || Same(own, sourceDir))
                return Fail("Source must not be inside the install folder.");
        var missingSrc = StagingCheck.Missing(sourceDir);
        if (missingSrc.Count > 0) return Fail("Missing in " + sourceDir + ": " + string.Join(", ", missingSrc));

        // A previous install that died between the two renames leaves only ".old": put it back first.
        if (!Directory.Exists(live) && Directory.Exists(old)) Directory.Move(old, live);
        DeleteDir(staging);
        DeleteDir(old);

        try
        {
            CopyDir(sourceDir, staging);
            // Re-check AFTER the copy: the source is user-writable and may have changed since the first look.
            var missing = StagingCheck.Missing(staging);
            if (missing.Count > 0) throw new InvalidOperationException("Missing after copy: " + string.Join(", ", missing));
            var problem = VerifyStaging(staging);
            if (problem != null) throw new InvalidOperationException(problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DeleteDir(staging);
            return Fail(ex.Message);
        }

        var result = SwapRunner.Run(new Ops(live, staging, old), FileLog.Write);
        switch (result)
        {
            case SwapResult.Success:
                FileLog.Write("Installed and started.");
                return 0;
            case SwapResult.FailedRolledBack:
                return Fail("New version did not start correctly; the previous version was restored and started.");
            case SwapResult.FailedRollbackFailed:
                return Fail("New version failed AND the previous version could not be restored/started. Re-run Enable, or reinstall.", 4);
            default:
                return Fail("New version did not start correctly; there was no previous version.");
        }
    }

    /// <summary>The real file-system / SCM side of <see cref="SwapRunner"/>.</summary>
    private sealed class Ops(string live, string staging, string old) : ISwapOps
    {
        private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(20);
        public bool HadLive { get; } = Directory.Exists(live);
        public void StopService() => Installer.StopService();
        public void MoveLiveToOld() => MoveWithRetry(live, old);
        public void MoveStagingToLive() => Directory.Move(staging, live);
        public void DeleteLive() => DeleteDir(live);
        public void RestoreOldToLive() => Directory.Move(old, live);
        public void DeleteOld() => DeleteDir(old);
        public bool StartRestored() => RegisterAndStart(live);

        public bool StartNewAndConfirm()
        {
            var statusPath = Path.Combine(UnattendedPaths.DataDir, "status.json");
            File.Delete(statusPath); // a leftover from the previous run must not confirm the new one
            if (!RegisterAndStart(live)) return false;
            var expected = FileVersionInfo.GetVersionInfo(Path.Combine(live, UnattendedPaths.ServiceExeName)).ProductVersion ?? "";
            var deadline = DateTime.UtcNow + ConfirmTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var status = File.Exists(statusPath) ? ServiceStatus.TryParse(ReadSafe(statusPath)) : null;
                if (ServiceStatus.IsConfirmed(status, expected, IsRunning(), ProcessExists)) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        private static string? ReadSafe(string path)
        {
            try { return File.ReadAllText(path); } catch (IOException) { return null; } // being rewritten
        }

        private static bool IsRunning()
        {
            try { using var sc = new ServiceController(UnattendedPaths.ServiceName); return sc.Status == ServiceControllerStatus.Running; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool ProcessExists(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; }
        }
    }

    public static int Uninstall()
    {
        FileLog.FileEnabled = false; // ProgramData is not verified here, so never write through it as admin
        if (!IsAdmin()) return Fail("Administrator rights are required.");
        StopService();
        Sc("delete", UnattendedPaths.ServiceName);
        foreach (var d in new[] { UnattendedPaths.InstallDir, UnattendedPaths.InstallDir + ".new", UnattendedPaths.InstallDir + ".old" })
        {
            try { DeleteDir(d); }
            catch (IOException ex) { FileLog.Write("Could not remove " + d + ": " + ex.Message); }
        }
        // %ProgramData%\DeskShare (server id, owner secret, config) is kept on purpose so re-enabling keeps the same identity.
        FileLog.Write("Uninstalled.");
        return 0;
    }

    /// <summary>
    /// Checks the staged (already admin-protected) copy. ponytail: integrity is only enforced when this helper is itself
    /// Authenticode-signed; an unsigned build cannot tell a swapped DLL from a real one. Upgrade path: sign every release
    /// binary (then the check below becomes mandatory automatically).
    /// The helper is one single-file exe (all managed code inside the signed image), so no DLL next to it is loaded with
    /// admin rights; unsigned, that exe is still only as trustworthy as the folder it was started from.
    /// </summary>
    private static string? VerifyStaging(string staging)
    {
        var helper = Environment.ProcessPath!;
        var pinned = Authenticode.SignerThumbprint(helper);
        if (pinned == null)
        {
            FileLog.Write("WARNING: DeskShare.Service.exe is not Authenticode-signed, so the files being installed cannot be verified. " +
                          "A process running as your user could have replaced them; install only from a build you trust.");
            return null;
        }

        var unverifiedThirdParty = 0;
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                continue;
            var ours = Authenticode.SignerThumbprint(file) == pinned;
            var firstParty = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                             Path.GetFileName(file).StartsWith("DeskShare.", StringComparison.OrdinalIgnoreCase);
            if (ours) continue;
            if (firstParty) return $"{Path.GetRelativePath(staging, file)} is not signed by the same certificate as the installer.";
            unverifiedThirdParty++; // third-party DLLs (Serilog, SIPSorcery, ...) keep their own / no signature
        }
        if (unverifiedThirdParty > 0)
            FileLog.Write($"WARNING: {unverifiedThirdParty} third-party DLL(s) are not signed by our certificate and were not integrity-checked.");
        return null;
    }

    /// <summary>
    /// Makes sure %ProgramData%\DeskShare is a folder only SYSTEM/Administrators own and write. A standard user can pre-create
    /// it (ProgramData allows that), so an existing folder is trusted only if it ALREADY has exactly our protected DACL and an
    /// admin owner; otherwise it is renamed aside (we never reuse its config / ids / secret) and recreated with the
    /// descriptor applied at creation time. Junctions / symlinks anywhere inside make us refuse.
    /// Must not log to a file: nothing in that folder is trustworthy until this method has returned.
    /// Returns the quarantine path when an untrusted folder was moved aside, otherwise null.
    /// </summary>
    private static string? PrepareDataDir()
    {
        var dir = UnattendedPaths.DataDir;
        string? aside = null;
        if (Directory.Exists(dir))
        {
            if (HasReparsePoint(dir)) throw new InvalidOperationException(dir + " contains a junction or symbolic link; remove it and retry.");
            if (!IsTrusted(dir))
            {
                aside = dir + ".untrusted-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                try { Directory.Move(dir, aside); }
                catch (IOException ex) { throw new InvalidOperationException("Cannot move an untrusted " + dir + " aside (" + ex.Message + "); delete it manually and retry."); }
            }
        }
        if (!Directory.Exists(dir))
        {
            var sec = new DirectorySecurity();
            sec.SetSecurityDescriptorSddlForm(AclSpec.DataDirSddl);
            new DirectoryInfo(dir).Create(sec);
        }
        // Closes the race where someone re-creates the folder between our move and our create.
        if (!IsTrusted(dir)) throw new InvalidOperationException(dir + " is not protected as expected after creation; aborting.");

        // ponytail: U1 keeps the rotating passkey (read it from the agent log). Permanent password + auth arrive in U3.
        if (!File.Exists(UnattendedPaths.ConfigPath))
            File.WriteAllText(UnattendedPaths.ConfigPath, """
                {
                  "Signaling": { "ServerUrl": "wss://app.deskshare.zcloud.cz/signal" },
                  "Security": { "EnableRemoteControl": false, "TrustClientPermanent": false }
                }
                """);
        return aside;
    }

    private static bool HasReparsePoint(string dir)
    {
        if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).Any(e => e.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>Owner is SYSTEM/Administrators, DACL is protected and consists of exactly Allow FullControl for SYSTEM and Administrators.</summary>
    private static bool IsTrusted(string dir)
    {
        var sec = new DirectoryInfo(dir).GetAccessControl();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || (!owner.Equals(system) && !owner.Equals(admins))) return false;
        if (!sec.AreAccessRulesProtected) return false;
        var rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return rules.Count == 2
            && rules.All(r => r.AccessControlType == AccessControlType.Allow && r.FileSystemRights == FileSystemRights.FullControl && !r.IsInherited)
            && rules.Any(r => r.IdentityReference.Equals(system))
            && rules.Any(r => r.IdentityReference.Equals(admins));
    }

    private static bool RegisterAndStart(string installDir)
    {
        // sc.exe instead of the SCM API: no extra package and the semantics are well known. "binPath= " needs the space after '='.
        var bin = $"\"{Path.Combine(installDir, UnattendedPaths.ServiceExeName)}\"";
        var exists = Sc("query", UnattendedPaths.ServiceName) == 0;
        if (Sc(exists ? "config" : "create", UnattendedPaths.ServiceName, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem",
                "DisplayName=", "DeskShare unattended access") != 0)
            return false;
        Sc("description", UnattendedPaths.ServiceName, "Keeps the DeskShare screen sender running in the active session.");
        // Restart on crash: after 5 s, 5 s, 30 s; failure counter resets after a day.
        Sc("failure", UnattendedPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
        return Sc("start", UnattendedPaths.ServiceName) == 0;
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

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private static void MoveWithRetry(string from, string to)
    {
        for (var i = 0; ; i++)
        {
            try { Directory.Move(from, to); return; }
            catch (IOException) when (i < 10) { Thread.Sleep(500); } // exe still locked while the service winds down
        }
    }

    private static bool Same(string a, string b) =>
        Path.TrimEndingDirectorySeparator(a).Equals(Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static int Sc(params string[] args)
    {
        // Absolute path: a relative "sc.exe" would be searched in the current directory first.
        var system32 = Environment.SystemDirectory;
        var psi = new ProcessStartInfo(Path.Combine(system32, "sc.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = system32
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static int Fail(string message, int code = 1)
    {
        FileLog.Write("ERROR: " + message); // console only while FileEnabled is off
        Console.Error.WriteLine(message);
        return code;
    }
}
