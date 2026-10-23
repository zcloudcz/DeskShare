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
        string? policyAside;
        try { aside = PrepareDataDir(); policyAside = QuarantineUntrustedPolicyDir(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return Fail(ex.Message, 3); }
        FileLog.FileEnabled = true;
        if (aside != null) FileLog.Write($"Existing {UnattendedPaths.DataDir} was not protected as expected, moved to {aside}; configuration reset to defaults.");
        if (policyAside != null) FileLog.Write($"Existing machine policy folder was not admin-only, moved to {policyAside}; its settings are ignored.");

        sourceDir = Path.GetFullPath(sourceDir);
        var live = UnattendedPaths.InstallDir;
        var staging = live + ".new";
        var old = live + ".old";
        foreach (var own in new[] { live, staging, old })
            if (UnattendedPaths.IsInside(own, sourceDir) || Same(own, sourceDir))
                return Fail("Source must not be inside the install folder.");
        var missingSrc = StagingCheck.Missing(sourceDir);
        if (missingSrc.Count > 0) return Fail("Missing in " + sourceDir + ": " + string.Join(", ", missingSrc));

        // A previous swap that never finished (Service.old and/or install.pending left behind) is repaired FIRST: Service.old is the
        // last known-good version and is never deleted just because a live folder exists.
        try
        {
            if (!RecoveryRunner.Run(new RecoveryOps(live, old)))
                return Fail("A previous update left the backup folder Service.old behind and it could not be restored/started or deleted; fix that first.", 4);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not repair an interrupted update: " + ex.Message, 4);
        }
        DeleteDir(staging);
        DeleteQuarantinedFolders(live);

        try
        {
            CopyDir(sourceDir, staging);
            // Agent files shared with the app are packaged once in the app root (<app>\service is our source dir); copy them into
            // staging now so the checks below see the complete folder. No-op when the package has no agent-shared.txt (MSI).
            AgentSharedFiles.Restore(staging, Directory.GetParent(sourceDir)!.FullName);
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
                // Only after the new version is confirmed running, so a failed or rolled-back install leaves the policy alone.
                // Lets the agent send Ctrl+Alt+Del to the sign-in screen. Best effort: a failure must not fail the install.
                try
                {
                    if (SasPolicy.EnableForServices(new SasRegistryStore()))
                        FileLog.Write("Set SoftwareSASGeneration=1 so remote Ctrl+Alt+Del works; it is removed again on uninstall.");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    FileLog.Write("WARNING: could not set SoftwareSASGeneration, remote Ctrl+Alt+Del will not work: " + ex.Message);
                }
                AuditEventSource.Register(); // session audit (Event Viewer > Windows Logs > Application > source DeskShare)
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

    private static string MarkerPath(string live) => Path.Combine(Directory.GetParent(live)!.FullName, "install.pending");

    /// <summary>Moves a bad live folder out of the way by renaming it (atomic), instead of a recursive delete that can stop half way.</summary>
    private static void QuarantineLiveFolder(string live)
    {
        if (Directory.Exists(live)) MoveWithRetry(live, live + ".failed-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
    }

    private static void DeleteQuarantinedFolders(string live) => QuarantineCleanup.Delete(live);

    private static MarkerState ReadMarker(string live)
    {
        var path = MarkerPath(live);
        if (!File.Exists(path)) return MarkerState.None;
        return File.ReadAllText(path).StartsWith("confirmed", StringComparison.Ordinal) ? MarkerState.Confirmed : MarkerState.Pending;
    }

    private sealed class RecoveryOps(string live, string old) : IRecoveryOps
    {
        public MarkerState Marker => ReadMarker(live);
        public bool OldExists => Directory.Exists(old);
        public bool LiveExists => Directory.Exists(live);
        public void StopService() => Installer.StopService();
        public void QuarantineLive() => QuarantineLiveFolder(live);
        public void RestoreOldToLive() => Directory.Move(old, live);
        public bool StartRestored() => RegisterAndStart(live);
        public void DeleteOld() => DeleteDir(old);
        public void ClearMarker() => File.Delete(MarkerPath(live));
    }

    /// <summary>The real file-system / SCM side of <see cref="SwapRunner"/>.</summary>
    private sealed class Ops(string live, string staging, string old) : ISwapOps
    {
        private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MustHoldFor = TimeSpan.FromSeconds(2);
        public bool HadLive { get; } = Directory.Exists(live);
        public void StopService() => Installer.StopService();
        public void WriteMarker() => File.WriteAllText(MarkerPath(live), "pending " + DateTime.UtcNow.ToString("O"));
        public void MarkConfirmed() => File.WriteAllText(MarkerPath(live), "confirmed " + DateTime.UtcNow.ToString("O"));
        public void ClearMarker() => File.Delete(MarkerPath(live));
        public void MoveLiveToOld() => MoveWithRetry(live, old);
        public void MoveStagingToLive() => Directory.Move(staging, live);
        public void QuarantineLive() => QuarantineLiveFolder(live);
        public void RestoreOldToLive() => Directory.Move(old, live);
        public void DeleteOld() => DeleteDir(old);
        public void DeleteQuarantined() => DeleteQuarantinedFolders(live);
        public bool StartRestored() => RegisterAndStart(live);

        public bool StartNewAndConfirm()
        {
            var data = UnattendedPaths.DataDir;
            var statusPath = Path.Combine(data, "status.json");
            var readyPath = Path.Combine(data, "agent-ready.json");
            // Fresh id for THIS attempt: the service echoes it in status.json and hands it to the agent, which echoes it in
            // agent-ready.json. Files from an earlier run (or an earlier service instance) carry another id and are ignored.
            var installId = Guid.NewGuid().ToString("N");
            File.Delete(statusPath);
            File.Delete(readyPath);
            File.WriteAllText(Path.Combine(data, "install-id"), installId);
            if (!RegisterAndStart(live)) return false;

            var expected = FileVersionInfo.GetVersionInfo(Path.Combine(live, UnattendedPaths.ServiceExeName)).ProductVersion ?? "";
            var gate = new ConfirmationGate(MustHoldFor);
            var deadline = DateTime.UtcNow + ConfirmTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var status = ServiceStatus.TryParse(ReadSafe(statusPath));
                var ready = AgentReady.TryParse(ReadSafe(readyPath));
                if (gate.Observe(ServiceStatus.IsConfirmed(status, ready, expected, installId, IsRunning(), ProcessExists), DateTime.UtcNow))
                    return true;
                Thread.Sleep(500);
            }
            return false;
        }

        private static string? ReadSafe(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch (IOException) { return null; } // being rewritten
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
        try { DeleteQuarantinedFolders(UnattendedPaths.InstallDir); File.Delete(MarkerPath(UnattendedPaths.InstallDir)); } catch (IOException) { }
        try
        {
            if (SasPolicy.RestoreOnUninstall(new SasRegistryStore()))
                FileLog.Write("Removed SoftwareSASGeneration that the installer had set.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            FileLog.Write("Could not restore SoftwareSASGeneration: " + ex.Message);
        }
        AuditEventSource.Unregister();
        // %ProgramData%\DeskShare (server id, owner secret, config) is kept on purpose so re-enabling keeps the same identity.
        FileLog.Write("Uninstalled.");
        return 0;
    }

    /// <summary>
    /// Checks the staged (already admin-protected) copy. ponytail: integrity is only enforced when this helper is itself
    /// Authenticode-signed; an unsigned build cannot tell a swapped DLL from a real one. Upgrade path: sign every release
    /// binary (then the check below becomes mandatory automatically).
    /// Known ceiling, not fixed here: the framework-dependent apphost
    /// loads hostfxr.dll from its own (user-writable, Velopack) folder BEFORE any of our code or this check runs, so a same-user
    /// process can plant a native DLL into the elevated helper; that is also why the signature check cannot protect the helper
    /// itself. Upgrade path once code signing exists: ship the elevated installer as a signed MSI (msiexec from System32 verifies
    /// the package) or as a NativeAOT bootstrap calling SetDefaultDllDirectories(System32). Until then the UAC prompt cannot
    /// show a verified publisher either. Mitigation today: the helper is one single-file exe (all managed code inside the signed image), so no DLL next to it is loaded with
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
    internal static string? PrepareDataDir() // also used by PasswordCommand
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

        // Remote viewers use the permanent password (set-password, see PasswordCommand) or the rotating passkey from the agent log.
        if (!File.Exists(UnattendedPaths.ConfigPath))
            File.WriteAllText(UnattendedPaths.ConfigPath, """
                {
                  "Signaling": { "ServerUrl": "wss://app.deskshare.zcloud.cz/signal" },
                  "Security": { "EnableRemoteControl": false, "TrustClientPermanent": false, "RequireConfirmationWhenUserLoggedOn": true }
                }
                """);
        return aside;
    }

    /// <summary>
    /// %ProgramData%\DeskShare.Policy is created by the MSI with an admin-only-write DACL. A standard user can pre-create it
    /// when the service is installed WITHOUT the MSI, and the SYSTEM agent would then obey that policy; PolicyTrust already makes
    /// the reader ignore it, and here it is also moved aside (like PrepareDataDir does) so a later MSI starts clean.
    /// </summary>
    private static string? QuarantineUntrustedPolicyDir()
    {
        var dir = Path.GetDirectoryName(MachinePolicy.DefaultPath)!;
        if (!Directory.Exists(dir) || PolicyTrust.Problem(MachinePolicy.DefaultPath) == null) return null;
        var aside = dir + ".untrusted-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        try { Directory.Move(dir, aside); }
        catch (IOException ex) { throw new InvalidOperationException("Cannot move an untrusted " + dir + " aside (" + ex.Message + "); delete it manually and retry."); }
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
