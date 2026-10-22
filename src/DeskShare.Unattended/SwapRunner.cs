using System.Text.Json;

namespace DeskShare.Unattended;

/// <summary>What the supervisor reports, written to %ProgramData%\DeskShare\status.json so the installer can confirm a new version really works.</summary>
/// <param name="InstallId">Random id the installer handed to THIS service start (install-id file); ties the status to one installation attempt.</param>
public sealed record ServiceStatus(string Version, string State, int? AgentPid, DateTime UpdatedUtc, string? InstallId = null, int? ServicePid = null)
{
    public const string AgentRunning = "agent-running";
    public const string NoSession = "no-session";      // nobody at the console: the agent cannot start, supervisor is alive
    public const string AgentFailed = "agent-failed";  // start threw or the agent exited

    public string ToJson() => JsonSerializer.Serialize(this);

    public static ServiceStatus? TryParse(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ServiceStatus>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Install is confirmed only when THIS installation's service instance (installId) of the NEW version runs and either
    /// there is legitimately no console session, or the agent itself reported ready (agent-ready.json, same installId and pid)
    /// and that process is alive. A bare "agent was launched" is not enough; stale files from a previous run carry another id.
    /// </summary>
    public static bool IsConfirmed(ServiceStatus? s, AgentReady? ready, string expectedVersion, string expectedInstallId,
        bool serviceRunning, Func<int, bool> processExists)
    {
        if (!serviceRunning || s == null || s.Version != expectedVersion || s.InstallId != expectedInstallId) return false;
        if (s.State == NoSession) return true;
        return s.State == AgentRunning && s.AgentPid is int pid
            && ready != null && ready.InstallId == expectedInstallId && ready.Pid == pid
            && processExists(pid);
    }
}

/// <summary>Written by the agent (as SYSTEM) once its ScreenSenderService.StartAsync succeeded.</summary>
public sealed record AgentReady(string InstallId, int Pid, DateTime UpdatedUtc)
{
    public static AgentReady? TryParse(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AgentReady>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>A condition must hold continuously for <c>hold</c> before it counts (a single lucky sample proves nothing).</summary>
public sealed class ConfirmationGate(TimeSpan hold)
{
    private DateTime? _since;

    public bool Observe(bool ok, DateTime now)
    {
        if (!ok) { _since = null; return false; }
        _since ??= now;
        return now - _since.Value >= hold;
    }
}

/// <summary>Files that must exist in a staged / source folder, or the installed service could loop forever without an agent.</summary>
public static class StagingCheck
{
    public static readonly string[] Required =
    {
        UnattendedPaths.ServiceExeName,
        Path.Combine(UnattendedPaths.AgentFolder, UnattendedPaths.AgentExeName),
        Path.Combine(UnattendedPaths.AgentFolder, "DeskShare.ScreenSenderApp.dll"),
        Path.Combine(UnattendedPaths.AgentFolder, "DeskShare.ScreenSenderApp.runtimeconfig.json"),
        Path.Combine(UnattendedPaths.AgentFolder, "DeskShare.Core.dll"),
    };

    public static IReadOnlyList<string> Missing(string root) =>
        Required.Where(f => !File.Exists(Path.Combine(root, f))).ToList();
}

public enum SwapResult { Success, FailedRolledBack, FailedRollbackFailed, FailedNothingToRestore }

/// <summary>File-system / SCM operations of an update, abstracted so the rollback decisions can be unit-tested.</summary>
public interface ISwapOps
{
    bool HadLive { get; }
    void StopService();
    /// <summary>Persists "a swap is in flight" (install.pending) so a crash mid-swap is repaired by the next install.</summary>
    void WriteMarker();
    void ClearMarker();
    void MoveLiveToOld();
    void MoveStagingToLive();
    /// <summary>Registers + starts the new version and waits for the confirmation (see <see cref="ServiceStatus.IsConfirmed"/>).</summary>
    bool StartNewAndConfirm();
    /// <summary>Renames the bad live folder to Service.failed-* (a rename cannot half-succeed, unlike a recursive delete).</summary>
    void QuarantineLive();
    void RestoreOldToLive();
    /// <summary>Registers + starts whatever is in the live folder (the restored old version).</summary>
    bool StartRestored();
    void DeleteOld();
    /// <summary>Best effort removal of quarantined folders; may throw, callers ignore.</summary>
    void DeleteQuarantined();
}

public static class SwapRunner
{
    /// <summary>
    /// stop -> marker -> live to .old -> staging to live -> start + confirm. ANY failure after the first move (exception or an
    /// unconfirmed start) quarantines the new live folder, puts the old version back and starts it. The old version is never
    /// deleted before the new one is confirmed, and the marker stays on disk whenever the end state is not clean.
    /// </summary>
    public static SwapResult Run(ISwapOps ops, Action<string> log)
    {
        var movedLiveToOld = false;
        var liveIsNew = false;
        try
        {
            ops.StopService();
            ops.WriteMarker();
            if (ops.HadLive) { ops.MoveLiveToOld(); movedLiveToOld = true; }
            ops.MoveStagingToLive();
            liveIsNew = true;
            if (ops.StartNewAndConfirm())
            {
                try { ops.ClearMarker(); } catch (Exception ex) { log("Could not clear the install marker: " + ex.Message); }
                try { ops.DeleteOld(); } catch (Exception ex) { log("Could not remove the previous version: " + ex.Message); }
                return SwapResult.Success;
            }
            log("New version did not confirm a working agent.");
        }
        catch (Exception ex)
        {
            log("Update step failed: " + ex.Message);
        }

        try
        {
            ops.StopService();
            if (liveIsNew) ops.QuarantineLive();
            if (!ops.HadLive)
            {
                ops.ClearMarker();
                return SwapResult.FailedNothingToRestore;
            }
            if (movedLiveToOld) ops.RestoreOldToLive();
            if (!ops.StartRestored()) return SwapResult.FailedRollbackFailed; // marker stays: next install repairs
            ops.ClearMarker();
            try { ops.DeleteQuarantined(); } catch (Exception ex) { log("Could not remove the quarantined folder: " + ex.Message); }
            return SwapResult.FailedRolledBack;
        }
        catch (Exception ex)
        {
            log("Rollback failed: " + ex.Message);
            return SwapResult.FailedRollbackFailed;
        }
    }
}

/// <summary>Operations of the crash-repair step that runs before every install.</summary>
public interface IRecoveryOps
{
    bool MarkerExists { get; }
    bool OldExists { get; }
    bool LiveExists { get; }
    void StopService();
    void QuarantineLive();
    void RestoreOldToLive();
    bool StartRestored();
    void ClearMarker();
}

public static class RecoveryRunner
{
    /// <summary>
    /// A leftover Service.old or install.pending means a previous swap never finished cleanly. The old folder is the last
    /// known-good version, so it is put back (the current live folder is quarantined, never deleted first) BEFORE anything else.
    /// Returns false when the restored version could not be started (the caller must not continue the install).
    /// </summary>
    public static bool Run(IRecoveryOps ops)
    {
        if (!ops.MarkerExists && !ops.OldExists) return true;
        if (ops.OldExists)
        {
            ops.StopService();
            if (ops.LiveExists) ops.QuarantineLive();
            ops.RestoreOldToLive();
            if (!ops.StartRestored()) return false;
        }
        ops.ClearMarker();
        return true;
    }
}
