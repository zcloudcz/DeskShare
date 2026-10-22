using DeskShare.Unattended;

namespace DeskShare.UnitTests.Unattended;

public class SupervisorTests
{
    private sealed class FakeAgent(int id) : IAgentProcess
    {
        public int Id => id;
        public bool HasExited { get; set; }
        public int ExitCode => -1; // stays readable after exit, like the real handle-backed agent
        public bool Killed;
        public void Kill() { Killed = true; HasExited = true; }
        public void Dispose() { }
    }

    private sealed class Rig
    {
        public uint Session = 1;
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public List<FakeAgent> Started = new();
        public Supervisor Sup;
        public Rig() => Sup = new Supervisor(() => Session, _ => { var a = new FakeAgent(Started.Count + 1); Started.Add(a); return a; },
            _ => { }, new RestartBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)), () => Now);
    }

    [Fact]
    public void AgentExit_IsRestartedAfterBackoff_Repeatedly()
    {
        var r = new Rig();
        r.Sup.Tick();
        Assert.Single(r.Started);

        for (var round = 2; round <= 3; round++)
        {
            r.Started[^1].HasExited = true;
            r.Sup.Tick();                        // notices the exit, schedules the restart
            Assert.Equal(round - 1, r.Started.Count);
            r.Now += TimeSpan.FromSeconds(0.5);
            r.Sup.Tick();                        // backoff not elapsed yet
            Assert.Equal(round - 1, r.Started.Count);
            r.Now += TimeSpan.FromSeconds(120);
            r.Sup.Tick();
            Assert.Equal(round, r.Started.Count);
        }
    }

    [Fact]
    public void ConsoleSessionChange_MovesAgentImmediately()
    {
        var r = new Rig();
        r.Sup.Tick();
        r.Session = 3;
        r.Sup.Tick();
        Assert.True(r.Started[0].Killed);
        Assert.Equal(2, r.Started.Count);
    }

    [Fact]
    public void NoConsoleSession_StopsAgent()
    {
        var r = new Rig();
        r.Sup.Tick();
        r.Session = SessionPolicy.NoSession;
        r.Sup.Tick();
        Assert.True(r.Started[0].Killed);
    }

    [Fact]
    public void StartFailure_IsSwallowedAndRetried()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var calls = 0;
        var s = new Supervisor(() => 1, _ => calls++ == 0 ? throw new InvalidOperationException("boom") : new FakeAgent(9), _ => { },
            new RestartBackoff(TimeSpan.FromSeconds(1)), () => now);
        s.Tick();
        now += TimeSpan.FromSeconds(5);
        s.Tick();
        Assert.Equal(2, calls);
    }
}
