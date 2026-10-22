using DeskShare.Core.Platforms.Windows;
using DeskShare.Unattended;

namespace DeskShare.UnitTests.Input;

public class SecureDesktopLogicTests
{
    [Fact]
    public void Tracker_ReportsOnlyRealChanges_AndIgnoresUnreadableDesktop()
    {
        var t = new DesktopChangeTracker();

        Assert.False(t.Observe(null));           // not readable (normal app): never a change
        Assert.True(t.Observe("Default"));       // first known desktop
        Assert.False(t.Observe("default"));      // same, names are case-insensitive
        Assert.False(t.Observe(null));           // transient failure keeps the last known desktop
        Assert.True(t.Observe("Winlogon"));      // UAC / lock screen
        Assert.Equal("Winlogon", t.Current);
        Assert.True(t.Observe("Default"));       // back after unlock
    }

    [Theory]
    [InlineData("Winlogon", "Default", true)]
    [InlineData("Default", "Default", false)]
    [InlineData("DEFAULT", "Default", false)]
    [InlineData(null, "Default", false)]
    public void NeedsAttach_OnlyWhenInputDesktopDiffersFromHome(string? input, string home, bool expected) =>
        Assert.Equal(expected, DesktopRules.NeedsAttach(input, home));

    [Theory]
    [InlineData("Winlogon", true, true)]
    [InlineData("Screen-saver", true, true)]
    [InlineData("Default", true, false)]   // normal desktop keeps retrying DXGI as before
    [InlineData(null, true, false)]        // unknown desktop (non-SYSTEM app): unchanged behaviour
    [InlineData("Winlogon", false, false)] // duplication works, no fallback needed
    public void GdiFallback_OnlyWhenDuplicationFailedOnSecureDesktop(string? desktop, bool failed, bool expected) =>
        Assert.Equal(expected, DesktopRules.ShouldUseGdiFallback(desktop, failed));

    [Theory]
    [InlineData(1, true, null)]
    [InlineData(3, true, null)]
    public void Sas_Available_ForSystemWithServicesPolicy(int? policy, bool system, string? why) =>
        Assert.Equal(why, SasAvailability.WhyUnavailable(policy, system));

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void Sas_Unavailable_WithoutPolicyOrWithoutSystem(int? policy, bool system) =>
        Assert.NotNull(SasAvailability.WhyUnavailable(policy, system));
}

public class SasPolicyTests
{
    private sealed class FakeStore : ISasPolicyStore
    {
        public int? Value;
        public bool InstallerSetIt { get; set; }
        public int? ReadSoftwareSasGeneration() => Value;
        public void WriteSoftwareSasGeneration(int value) => Value = value;
        public void DeleteSoftwareSasGeneration() => Value = null;
    }

    [Fact]
    public void Install_SetsValueAndRecordsIt_WhenAbsent()
    {
        var s = new FakeStore();

        Assert.True(SasPolicy.EnableForServices(s));

        Assert.Equal(1, s.Value);
        Assert.True(s.InstallerSetIt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void Install_LeavesExistingValueAlone(int existing)
    {
        var s = new FakeStore { Value = existing };

        Assert.False(SasPolicy.EnableForServices(s));

        Assert.Equal(existing, s.Value);
        Assert.False(s.InstallerSetIt);
    }

    [Fact]
    public void Reinstall_KeepsOurClaim()
    {
        var s = new FakeStore();
        SasPolicy.EnableForServices(s);

        Assert.False(SasPolicy.EnableForServices(s));

        Assert.True(s.InstallerSetIt);
        Assert.Equal(1, s.Value);
    }

    [Fact]
    public void Uninstall_RemovesValue_OnlyWhenInstallerSetIt()
    {
        var ours = new FakeStore();
        SasPolicy.EnableForServices(ours);
        var theirs = new FakeStore { Value = 1 }; // admin configured 1 themselves

        Assert.True(SasPolicy.RestoreOnUninstall(ours));
        Assert.False(SasPolicy.RestoreOnUninstall(theirs));

        Assert.Null(ours.Value);
        Assert.False(ours.InstallerSetIt);
        Assert.Equal(1, theirs.Value);
    }

    [Fact]
    public void Uninstall_KeepsValueTheAdminChangedSince()
    {
        var s = new FakeStore();
        SasPolicy.EnableForServices(s);
        s.Value = 3;

        Assert.False(SasPolicy.RestoreOnUninstall(s));

        Assert.Equal(3, s.Value);
        Assert.False(s.InstallerSetIt); // claim dropped, value is theirs now
    }
}
