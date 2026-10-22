using DeskShare.Unattended;

namespace DeskShare.UnitTests.Unattended;

public class SwapRunnerTests
{
    /// <summary>Records calls; any step can be told to fail.</summary>
    private sealed class FakeOps : ISwapOps
    {
        public bool HadLive { get; set; } = true;
        public List<string> Calls = new();
        public string? ThrowAt;
        public bool NewConfirms = true, RestoredStarts = true;

        private void Do(string name) { Calls.Add(name); if (ThrowAt == name) throw new IOException(name); }
        public void StopService() => Do("stop");
        public void WriteMarker() => Do("marker+");
        public void MarkConfirmed() => Do("confirmed");
        public void ClearMarker() => Do("marker-");
        public void MoveLiveToOld() => Do("live->old");
        public void MoveStagingToLive() => Do("staging->live");
        public bool StartNewAndConfirm() { Do("startNew"); return NewConfirms; }
        public void QuarantineLive() => Do("quarantine");
        public void RestoreOldToLive() => Do("old->live");
        public bool StartRestored() { Do("startOld"); return RestoredStarts; }
        public void DeleteOld() => Do("deleteOld");
        public void DeleteQuarantined() => Do("deleteQuarantined");
    }

    private static SwapResult Run(FakeOps o) => SwapRunner.Run(o, _ => { });

    [Fact]
    public void Success_ClearsMarkerThenDeletesOld_AndNeverRestores()
    {
        var o = new FakeOps();
        Assert.Equal(SwapResult.Success, Run(o));
        Assert.Equal(new[] { "stop", "marker+", "live->old", "staging->live", "startNew", "confirmed", "deleteOld", "marker-" }, o.Calls);
    }

    [Fact]
    public void InterruptedDeleteOld_KeepsTheMarker_SoRecoveryKnowsItWasConfirmed()
    {
        var o = new FakeOps { ThrowAt = "deleteOld" };
        Assert.Equal(SwapResult.Success, Run(o));
        Assert.Contains("confirmed", o.Calls);
        Assert.DoesNotContain("marker-", o.Calls);
    }

    [Fact]
    public void MarkConfirmedFails_OldIsKept()
    {
        var o = new FakeOps { ThrowAt = "confirmed" };
        Assert.Equal(SwapResult.Success, Run(o));
        Assert.DoesNotContain("deleteOld", o.Calls);
    }

    [Fact]
    public void MarkerIsWrittenBeforeTheFirstMove()
    {
        var o = new FakeOps();
        Run(o);
        Assert.True(o.Calls.IndexOf("marker+") < o.Calls.IndexOf("live->old"));
    }

    [Fact]
    public void UnconfirmedStart_QuarantinesNew_RestoresOld_AndOnlyThenCleansUp()
    {
        var o = new FakeOps { NewConfirms = false };
        Assert.Equal(SwapResult.FailedRolledBack, Run(o));
        Assert.Equal(new[] { "stop", "marker+", "live->old", "staging->live", "startNew", "stop", "quarantine", "old->live", "startOld", "marker-", "deleteQuarantined" }, o.Calls);
    }

    [Fact]
    public void SecondRenameFails_StillRestoresOld()
    {
        var o = new FakeOps { ThrowAt = "staging->live" };
        Assert.Equal(SwapResult.FailedRolledBack, Run(o));
        Assert.Contains("old->live", o.Calls);
        Assert.DoesNotContain("quarantine", o.Calls); // staging never became live
        Assert.Equal("deleteQuarantined", o.Calls[^1]);
    }

    [Fact]
    public void FirstRenameFails_KeepsLiveAndJustRestartsIt()
    {
        var o = new FakeOps { ThrowAt = "live->old" };
        Assert.Equal(SwapResult.FailedRolledBack, Run(o));
        Assert.DoesNotContain("quarantine", o.Calls);   // live is still the OLD version here
        Assert.DoesNotContain("old->live", o.Calls);
        Assert.Contains("startOld", o.Calls);
    }

    [Fact]
    public void QuarantineFails_OldIsNotTouched_MarkerStays_ReportedAsFailed()
    {
        // The old "recursive delete" rollback could stop half way and lose the last good version; a failing rename cannot.
        var o = new FakeOps { NewConfirms = false, ThrowAt = "quarantine" };
        Assert.Equal(SwapResult.FailedRollbackFailed, Run(o));
        Assert.DoesNotContain("old->live", o.Calls);
        Assert.DoesNotContain("deleteOld", o.Calls);
        Assert.DoesNotContain("marker-", o.Calls);
    }

    [Fact]
    public void RestoredOldDoesNotStart_IsReported_AndMarkerStays()
    {
        var o = new FakeOps { NewConfirms = false, RestoredStarts = false };
        Assert.Equal(SwapResult.FailedRollbackFailed, Run(o));
        Assert.DoesNotContain("marker-", o.Calls);
        Assert.DoesNotContain("deleteOld", o.Calls);
    }

    [Fact]
    public void QuarantinedCleanupFailure_DoesNotChangeTheResult()
    {
        var o = new FakeOps { NewConfirms = false, ThrowAt = "deleteQuarantined" };
        Assert.Equal(SwapResult.FailedRolledBack, Run(o));
    }

    [Fact]
    public void FirstInstallFailure_QuarantinesNewAndHasNothingToRestore()
    {
        var o = new FakeOps { HadLive = false, NewConfirms = false };
        Assert.Equal(SwapResult.FailedNothingToRestore, Run(o));
        Assert.Contains("quarantine", o.Calls);
        Assert.DoesNotContain("old->live", o.Calls);
    }
}

public class RecoveryRunnerTests
{
    private sealed class FakeRecovery : IRecoveryOps
    {
        public MarkerState Marker { get; set; }
        public bool OldExists { get; set; }
        public bool DeleteOldFails;
        public bool LiveExists { get; set; }
        public bool RestoredStarts = true;
        public List<string> Calls = new();
        public void StopService() => Calls.Add("stop");
        public void QuarantineLive() { Calls.Add("quarantine"); LiveExists = false; }
        public void RestoreOldToLive() { Calls.Add("old->live"); OldExists = false; LiveExists = true; }
        public bool StartRestored() { Calls.Add("startOld"); return RestoredStarts; }
        public void DeleteOld() { Calls.Add("deleteOld"); if (DeleteOldFails) throw new IOException(); OldExists = false; }
        public void ClearMarker() { Calls.Add("marker-"); Marker = MarkerState.None; }
    }

    [Fact]
    public void CleanState_DoesNothing()
    {
        var r = new FakeRecovery { LiveExists = true };
        Assert.True(RecoveryRunner.Run(r));
        Assert.Empty(r.Calls);
    }

    [Fact]
    public void CrashBetweenTheTwoRenames_LiveMissing_OldIsRestored()
    {
        var r = new FakeRecovery { Marker = MarkerState.Pending, OldExists = true, LiveExists = false };
        Assert.True(RecoveryRunner.Run(r));
        Assert.Equal(new[] { "stop", "old->live", "startOld", "marker-" }, r.Calls);
    }

    [Fact]
    public void OldAndLiveBothExist_LiveIsQuarantinedNotDeleted_BeforeOldIsRestored()
    {
        // Previously "live exists" made the next install delete .old, i.e. the last good version.
        var r = new FakeRecovery { Marker = MarkerState.Pending, OldExists = true, LiveExists = true };
        Assert.True(RecoveryRunner.Run(r));
        Assert.Equal(new[] { "stop", "quarantine", "old->live", "startOld", "marker-" }, r.Calls);
    }

    [Fact]
    public void ConfirmedMarkerWithLeftoverOld_DeletesOld_NeverRestoresIt()
    {
        // A half-deleted .old after a CONFIRMED update must not replace the working live version.
        var r = new FakeRecovery { Marker = MarkerState.Confirmed, OldExists = true, LiveExists = true };
        Assert.True(RecoveryRunner.Run(r));
        Assert.Equal(new[] { "deleteOld", "marker-" }, r.Calls);
    }

    [Fact]
    public void ConfirmedMarker_DeleteStillFailing_StopsTheInstallAndKeepsMarker()
    {
        var r = new FakeRecovery { Marker = MarkerState.Confirmed, OldExists = true, LiveExists = true, DeleteOldFails = true };
        Assert.False(RecoveryRunner.Run(r));
        Assert.Equal(new[] { "deleteOld" }, r.Calls);
    }

    [Fact]
    public void LeftoverOldWithoutAnyMarker_IsDeleted_NotRestored()
    {
        var r = new FakeRecovery { OldExists = true, LiveExists = true };
        Assert.True(RecoveryRunner.Run(r));
        Assert.Equal(new[] { "deleteOld" }, r.Calls);
    }

    [Fact]
    public void StaleMarkerOnly_IsJustCleared()
    {
        var r = new FakeRecovery { Marker = MarkerState.Pending, LiveExists = true };
        Assert.True(RecoveryRunner.Run(r));
        Assert.Equal(new[] { "marker-" }, r.Calls);
    }

    [Fact]
    public void RestoredVersionNotStarting_BlocksTheInstall_AndKeepsMarker()
    {
        var r = new FakeRecovery { Marker = MarkerState.Pending, OldExists = true, RestoredStarts = false };
        Assert.False(RecoveryRunner.Run(r));
        Assert.DoesNotContain("marker-", r.Calls);
    }
}

public class ServiceStatusTests
{
    private const string Id = "abc123";
    private static ServiceStatus S(string state, int? pid = null, string version = "1.2.3", string? id = Id)
        => new(version, state, pid, DateTime.UtcNow, id, 1000);
    private static AgentReady R(int pid = 42, string id = Id) => new(id, pid, DateTime.UtcNow);
    private static readonly Func<int, bool> Alive = pid => pid == 42;

    private static bool Confirmed(ServiceStatus? s, AgentReady? r, bool running = true)
        => ServiceStatus.IsConfirmed(s, r, "1.2.3", Id, running, Alive);

    [Fact]
    public void Confirmed_WhenNewVersionRunsAndAgentReportedReady()
        => Assert.True(Confirmed(S(ServiceStatus.AgentRunning, 42), R()));

    [Fact]
    public void Confirmed_WhenNoConsoleSession_WithoutAgentFile()
        => Assert.True(Confirmed(S(ServiceStatus.NoSession), null));

    [Fact]
    public void AgentLaunchedButNotReady_IsNotConfirmed()
        => Assert.False(Confirmed(S(ServiceStatus.AgentRunning, 42), null));

    [Fact]
    public void AgentReadyFromAnotherInstall_IsRejected()
        => Assert.False(Confirmed(S(ServiceStatus.AgentRunning, 42), R(id: "old-install")));

    [Fact]
    public void AgentReadyForAnotherPid_IsRejected()
        => Assert.False(Confirmed(S(ServiceStatus.AgentRunning, 42), R(pid: 7)));

    [Fact]
    public void StaleNoSessionFromPreviousServiceInstance_IsRejected()
    {
        Assert.False(Confirmed(S(ServiceStatus.NoSession, id: "previous-run"), null));
        Assert.False(Confirmed(S(ServiceStatus.NoSession, id: null), null));
    }

    [Theory]
    [InlineData(false, "1.2.3", ServiceStatus.AgentRunning, 42)]   // service not running
    [InlineData(true, "1.2.2", ServiceStatus.AgentRunning, 42)]    // written by the old version
    [InlineData(true, "1.2.3", ServiceStatus.AgentFailed, null)]   // agent missing / crash loop
    [InlineData(true, "1.2.3", ServiceStatus.AgentRunning, 7)]     // pid not alive
    [InlineData(true, "1.2.3", ServiceStatus.AgentRunning, null)]
    public void NotConfirmed(bool running, string version, string state, int? pid)
        => Assert.False(Confirmed(S(state, pid, version), R(pid ?? 42), running));

    [Fact]
    public void NoStatusFile_IsNotConfirmed()
        => Assert.False(Confirmed(null, R()));

    [Fact]
    public void Json_RoundTrips_AndGarbageIsNull()
    {
        var s = S(ServiceStatus.AgentRunning, 42);
        var back = ServiceStatus.TryParse(s.ToJson())!;
        Assert.Equal(Id, back.InstallId);
        Assert.Equal(42, back.AgentPid);
        Assert.Null(ServiceStatus.TryParse("{not json"));
        Assert.Null(ServiceStatus.TryParse(""));
        Assert.Null(AgentReady.TryParse("{not json"));
    }

    [Fact]
    public void AgentReady_ParsesTheShapeTheAgentWrites()
    {
        // The agent has no reference to this library and writes this anonymous shape itself.
        var json = System.Text.Json.JsonSerializer.Serialize(new { InstallId = "x", Pid = 5, UpdatedUtc = DateTime.UtcNow });
        var r = AgentReady.TryParse(json)!;
        Assert.Equal("x", r.InstallId);
        Assert.Equal(5, r.Pid);
    }

    [Fact]
    public void Supervisor_ReportsStatusOnChangeOnly()
    {
        var reports = new List<(string, int?)>();
        var session = SessionPolicy.NoSession;
        var s = new Supervisor(() => session, _ => new Fake(42), _ => { }, onStatus: (st, pid) => reports.Add((st, pid)));
        s.Tick(); s.Tick();
        session = 1;
        s.Tick(); s.Tick();
        Assert.Equal(new[] { (ServiceStatus.NoSession, (int?)null), (ServiceStatus.AgentRunning, (int?)42) }, reports);
    }

    private sealed class Fake(int id) : IAgentProcess
    {
        public int Id => id; public bool HasExited => false; public int ExitCode => 0;
        public void Kill() { } public void Dispose() { }
    }
}

public class ConfirmationGateTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SingleSample_IsNotEnough_ButSustainedOkIs()
    {
        var g = new ConfirmationGate(TimeSpan.FromSeconds(2));
        Assert.False(g.Observe(true, T0));
        Assert.False(g.Observe(true, T0.AddSeconds(1)));
        Assert.True(g.Observe(true, T0.AddSeconds(2)));
    }

    [Fact]
    public void AnyFailure_RestartsTheHoldPeriod()
    {
        var g = new ConfirmationGate(TimeSpan.FromSeconds(2));
        g.Observe(true, T0);
        g.Observe(false, T0.AddSeconds(1));       // e.g. the agent died and the pid is gone
        Assert.False(g.Observe(true, T0.AddSeconds(2)));
        Assert.False(g.Observe(true, T0.AddSeconds(3)));
        Assert.True(g.Observe(true, T0.AddSeconds(4)));
    }
}

public class QuarantineCleanupTests
{
    [Fact]
    public void FirstInstall_ParentFolderDoesNotExist_DoesNotThrow()
    {
        var live = Path.Combine(Path.GetTempPath(), "no-pf-" + Guid.NewGuid().ToString("N"), "DeskShare", "Service");
        QuarantineCleanup.Delete(live); // the bug: GetDirectories on a missing %ProgramFiles%\DeskShare crashed the very first install
        Assert.False(Directory.Exists(Path.GetDirectoryName(live)));
    }

    [Fact]
    public void RemovesOnlyFailedSiblings()
    {
        var parent = Path.Combine(Path.GetTempPath(), "qc-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(parent, "Service");
        try
        {
            Directory.CreateDirectory(live);
            Directory.CreateDirectory(live + ".failed-1");
            Directory.CreateDirectory(live + ".old");
            QuarantineCleanup.Delete(live);
            Assert.True(Directory.Exists(live));
            Assert.True(Directory.Exists(live + ".old"));
            Assert.False(Directory.Exists(live + ".failed-1"));
        }
        finally { Directory.Delete(parent, true); }
    }
}

public class StagingCheckTests
{
    [Fact]
    public void Missing_ListsEverythingInAnEmptyFolder_AndNothingWhenComplete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stg-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            Assert.Equal(StagingCheck.Required.Length, StagingCheck.Missing(dir).Count);

            foreach (var f in StagingCheck.Required)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, f))!);
                File.WriteAllText(Path.Combine(dir, f), "x");
            }
            Assert.Empty(StagingCheck.Missing(dir));

            File.Delete(Path.Combine(dir, "agent", "DeskShare.Core.dll"));
            Assert.Single(StagingCheck.Missing(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
