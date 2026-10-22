using DeskShare.Unattended;

namespace DeskShare.UnitTests.Unattended;

public class SessionPolicyTests
{
    [Theory]
    [InlineData(null, 1u, SupervisorAction.Start)]      // logon: agent not running yet
    [InlineData(1u, 1u, SupervisorAction.None)]         // already in the right session
    [InlineData(1u, 2u, SupervisorAction.Restart)]      // fast user switch / console moved
    [InlineData(2u, SessionPolicy.NoSession, SupervisorAction.Stop)]   // console disconnected
    [InlineData(null, SessionPolicy.NoSession, SupervisorAction.None)]
    [InlineData(1u, 0u, SupervisorAction.Stop)]         // session 0 is never a target
    [InlineData(null, 0u, SupervisorAction.None)]
    public void Decide_FollowsConsoleSession(uint? running, uint active, SupervisorAction expected)
        => Assert.Equal(expected, SessionPolicy.Decide(running, active));
}

public class RestartBackoffTests
{
    [Fact]
    public void Delay_DoublesUpToMax()
    {
        var b = new RestartBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        var delays = Enumerable.Range(0, 6).Select(_ => b.NextDelay(TimeSpan.Zero).TotalSeconds).ToArray();
        Assert.Equal(new double[] { 1, 2, 4, 8, 10, 10 }, delays);
    }

    [Fact]
    public void LongRun_ResetsBackoff()
    {
        var b = new RestartBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
        b.NextDelay(TimeSpan.Zero);
        b.NextDelay(TimeSpan.Zero);
        Assert.Equal(1, b.NextDelay(TimeSpan.FromMinutes(5)).TotalSeconds);
    }

    [Fact]
    public void Reset_StartsOver()
    {
        var b = new RestartBackoff();
        b.NextDelay(TimeSpan.Zero);
        b.NextDelay(TimeSpan.Zero);
        b.Reset();
        Assert.Equal(1, b.NextDelay(TimeSpan.Zero).TotalSeconds);
    }

    [Fact]
    public void HugeAttemptCount_DoesNotOverflow()
    {
        var b = new RestartBackoff();
        for (var i = 0; i < 100; i++) b.NextDelay(TimeSpan.Zero);
        Assert.Equal(60, b.NextDelay(TimeSpan.Zero).TotalSeconds);
    }
}

public class ServiceVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", true)]
    [InlineData("1.2.3.0", "1.2.3", false)]
    [InlineData("1.2.3", "1.10.0", true)]            // numeric, not lexical
    [InlineData("1.2.3+abc", "1.2.3-beta.1", false)] // suffixes ignored
    [InlineData("2.0.0", "1.9.9", false)]            // never "update" to an older app
    [InlineData("garbage", "1.0.0", false)]
    [InlineData(null, "1.0.0", false)]
    public void UpdateAvailable(string? installed, string? app, bool expected)
        => Assert.Equal(expected, ServiceVersion.UpdateAvailable(installed, app));
}

public class UnattendedPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "svc");

    [Fact]
    public void ResolveAgentPath_IsInsideServiceFolder()
    {
        var agent = UnattendedPaths.ResolveAgentPath(Root);
        Assert.True(UnattendedPaths.IsInside(Root, agent));
        Assert.EndsWith(UnattendedPaths.AgentExeName, agent);
    }

    [Theory]
    [InlineData("svc2/agent.exe")]          // shared prefix is not "inside"
    [InlineData("svc/../evil.exe")]
    [InlineData("other/svc/x.exe")]
    public void IsInside_RejectsOutsidePaths(string relative)
        => Assert.False(UnattendedPaths.IsInside(Root, Path.Combine(Path.GetTempPath(), relative)));

    [Fact]
    public void IsInside_AcceptsNestedAndIgnoresCaseAndTrailingSeparator()
    {
        Assert.True(UnattendedPaths.IsInside(Root + Path.DirectorySeparatorChar, Path.Combine(Root.ToUpperInvariant(), "a", "b.exe")));
        Assert.False(UnattendedPaths.IsInside(Root, Root)); // the folder itself is not a file inside it
    }
}

public class AclSpecTests
{
    [Fact]
    public void DataDirSddl_GrantsOnlySystemAndAdministrators_AndBlocksInheritance()
    {
        Assert.StartsWith("D:P", AclSpec.DataDirSddl);          // protected: no parent ACEs (ProgramData grants Users read)
        Assert.Contains(";;;SY)", AclSpec.DataDirSddl);
        Assert.Contains(";;;BA)", AclSpec.DataDirSddl);
        Assert.Equal(2, AclSpec.DataDirSddl.Count(c => c == '(') );
        Assert.DoesNotContain(";;;BU)", AclSpec.DataDirSddl);   // Users
        Assert.DoesNotContain(";;;WD)", AclSpec.DataDirSddl);   // Everyone
    }
}
