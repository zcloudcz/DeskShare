using System.Text.Json;

namespace DeskShare.Unattended;

/// <summary>What the supervisor reports after start-up, written to %ProgramData%\DeskShare\status.json so the installer can confirm a new version really works.</summary>
public sealed record ServiceStatus(string Version, string State, int? AgentPid, DateTime UpdatedUtc)
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

    /// <summary>Install is confirmed only when the NEW version runs and either the agent is alive or there is legitimately no console session.</summary>
    public static bool IsConfirmed(ServiceStatus? s, string expectedVersion, bool serviceRunning, Func<int, bool> processExists)
        => serviceRunning && s != null && s.Version == expectedVersion &&
           (s.State == NoSession || (s.State == AgentRunning && s.AgentPid is int pid && processExists(pid)));
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
    void MoveLiveToOld();
    void MoveStagingToLive();
    /// <summary>Registers + starts the new version and waits for the confirmation (service Running and agent started).</summary>
    bool StartNewAndConfirm();
    void DeleteLive();
    void RestoreOldToLive();
    /// <summary>Registers + starts whatever is in the live folder (the restored old version).</summary>
    bool StartRestored();
    void DeleteOld();
}

public static class SwapRunner
{
    /// <summary>
    /// stop -> live to .old -> staging to live -> start + confirm. ANY failure after the first move (exception or an
    /// unconfirmed start) puts the old version back and starts it; the result says whether that worked.
    /// </summary>
    public static SwapResult Run(ISwapOps ops, Action<string> log)
    {
        var movedLiveToOld = false;
        var liveIsNew = false;
        try
        {
            ops.StopService();
            if (ops.HadLive) { ops.MoveLiveToOld(); movedLiveToOld = true; }
            ops.MoveStagingToLive();
            liveIsNew = true;
            if (ops.StartNewAndConfirm())
            {
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
            if (liveIsNew) ops.DeleteLive();
            if (!ops.HadLive) return SwapResult.FailedNothingToRestore;
            if (movedLiveToOld) ops.RestoreOldToLive();
            return ops.StartRestored() ? SwapResult.FailedRolledBack : SwapResult.FailedRollbackFailed;
        }
        catch (Exception ex)
        {
            log("Rollback failed: " + ex.Message);
            return SwapResult.FailedRollbackFailed;
        }
    }
}
