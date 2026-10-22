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
        public void MoveLiveToOld() => Do("live->old");
        public void MoveStagingToLive() => Do("staging->live");
        public bool StartNewAndConfirm() { Do("startNew"); return NewConfirms; }
        public void DeleteLive() => Do("deleteLive");
        public void RestoreOldToLive() => Do("old->live");
        public bool StartRestored() { Do("startOld"); return RestoredStarts; }
        public void DeleteOld() => Do("deleteOld");
    }

    private static SwapResult Run(FakeOps o) => SwapRunner.Run(o, _ => { });

    [Fact]
    public void Success_DeletesOld_AndNeverRestores()
    {
        var o = new FakeOps();
        Assert.Equal(SwapResult.Success, Run(o));
        Assert.Contains("deleteOld", o.Calls);
        Assert.DoesNotContain("old->live", o.Calls);
    }

    [Fact]
    public void UnconfirmedStart_RollsBackAndStartsOld()
    {
        var o = new FakeOps { NewConfirms = false };
        Assert.Equal(SwapResult.FailedRolledBack, Run(o));
        Assert.Equal(new[] { "stop", "live->old", "staging->live", "startNew", "stop", "deleteLive", "old->live", "startOld" }, o.Calls);
    }

    [Fact]
    public void SecondRenameFails_StillRestoresOld()
    {
        // The bug this guards: live was already moved to .old, then staging->live threw and nothing put the service back.
        var o = new FakeOps { ThrowAt = "staging->live" };
        Assert.Equal(SwapResult.FailedRolledBack, Run(o));
        Assert.Contains("old->live", o.Calls);
        Assert.DoesNotContain("deleteLive", o.Calls); // staging never became live, nothing of ours to delete
        Assert.Equal("startOld", o.Calls[^1]);
    }

    [Fact]
    public void FirstRenameFails_KeepsLiveAndJustRestartsIt()
    {
        var o = new FakeOps { ThrowAt = "live->old" };
        Assert.Equal(SwapResult.FailedRolledBack, Run(o));
        Assert.DoesNotContain("deleteLive", o.Calls);   // live is still the OLD version here
        Assert.DoesNotContain("old->live", o.Calls);
        Assert.Equal("startOld", o.Calls[^1]);
    }

    [Fact]
    public void RestoredOldDoesNotStart_IsReported()
    {
        var o = new FakeOps { NewConfirms = false, RestoredStarts = false };
        Assert.Equal(SwapResult.FailedRollbackFailed, Run(o));
    }

    [Fact]
    public void RestoreThrows_IsReportedNotThrown()
    {
        var o = new FakeOps { NewConfirms = false, ThrowAt = "old->live" };
        Assert.Equal(SwapResult.FailedRollbackFailed, Run(o));
    }

    [Fact]
    public void FirstInstallFailure_DeletesNewAndHasNothingToRestore()
    {
        var o = new FakeOps { HadLive = false, NewConfirms = false };
        Assert.Equal(SwapResult.FailedNothingToRestore, Run(o));
        Assert.Contains("deleteLive", o.Calls);
        Assert.DoesNotContain("old->live", o.Calls);
    }
}

public class ServiceStatusTests
{
    private static ServiceStatus S(string state, int? pid = null, string version = "1.2.3") => new(version, state, pid, DateTime.UtcNow);
    private static readonly Func<int, bool> Alive = pid => pid == 42;

    [Fact]
    public void Confirmed_WhenNewVersionRunsItsAgent()
        => Assert.True(ServiceStatus.IsConfirmed(S(ServiceStatus.AgentRunning, 42), "1.2.3", true, Alive));

    [Fact]
    public void Confirmed_WhenNoConsoleSession()
        => Assert.True(ServiceStatus.IsConfirmed(S(ServiceStatus.NoSession), "1.2.3", true, Alive));

    [Theory]
    [InlineData(false, "1.2.3", ServiceStatus.AgentRunning, 42)]   // service not running
    [InlineData(true, "1.2.2", ServiceStatus.AgentRunning, 42)]    // status written by the old version
    [InlineData(true, "1.2.3", ServiceStatus.AgentFailed, null)]   // agent missing / crash loop
    [InlineData(true, "1.2.3", ServiceStatus.AgentRunning, 7)]     // pid not alive
    [InlineData(true, "1.2.3", ServiceStatus.AgentRunning, null)]
    public void NotConfirmed(bool running, string version, string state, int? pid)
        => Assert.False(ServiceStatus.IsConfirmed(S(state, pid, version), "1.2.3", running, Alive));

    [Fact]
    public void NoStatusFile_IsNotConfirmed()
        => Assert.False(ServiceStatus.IsConfirmed(null, "1.2.3", true, Alive));

    [Fact]
    public void Json_RoundTrips_AndGarbageIsNull()
    {
        var s = S(ServiceStatus.AgentRunning, 42);
        Assert.Equal(s.AgentPid, ServiceStatus.TryParse(s.ToJson())!.AgentPid);
        Assert.Null(ServiceStatus.TryParse("{not json"));
        Assert.Null(ServiceStatus.TryParse(""));
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
