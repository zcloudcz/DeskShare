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
        public SasMarker Marker { get; set; }
        public bool FailMarkerWrite, FailValueWrite;
        public int? ReadSoftwareSasGeneration() => Value;
        public void WriteSoftwareSasGeneration(int value) { if (FailValueWrite) throw new IOException(); Value = value; }
        public void DeleteSoftwareSasGeneration() => Value = null;
    }

    // Fails the n-th marker write (1 = the Pending write, 2 = the Done write); reverting to None is always allowed.
    private sealed class FlakyMarkerStore : ISasPolicyStore
    {
        private int _writes;
        private SasMarker _marker;
        public int FailOnWrite;
        public int? Value;
        public SasMarker Marker
        {
            get => _marker;
            set { if (value != SasMarker.None && ++_writes == FailOnWrite) throw new IOException(); _marker = value; }
        }
        public int? ReadSoftwareSasGeneration() => Value;
        public void WriteSoftwareSasGeneration(int value) => Value = value;
        public void DeleteSoftwareSasGeneration() => Value = null;
    }

    [Fact]
    public void Install_PendingMarkerWriteFails_WritesNothing()
    {
        var s = new FlakyMarkerStore { FailOnWrite = 1 };

        Assert.Throws<IOException>(() => SasPolicy.EnableForServices(s));

        Assert.Null(s.Value);
        Assert.Equal(SasMarker.None, s.Marker);
    }

    [Fact]
    public void Install_PolicyWriteFails_RevertsMarker()
    {
        var s = new FakeStore { FailValueWrite = true };

        Assert.Throws<IOException>(() => SasPolicy.EnableForServices(s));

        Assert.Null(s.Value);
        Assert.Equal(SasMarker.None, s.Marker);
    }

    // Done marker write fails AND the value cannot be deleted during revert: the claim must survive as Pending.
    private sealed class StuckValueStore : ISasPolicyStore
    {
        private int _writes;
        public SasMarker Marker { get; set; }
        public int? Value;
        SasMarker ISasPolicyStore.Marker
        {
            get => Marker;
            set { if (value == SasMarker.Done && ++_writes == 1) throw new IOException(); Marker = value; }
        }
        public int? ReadSoftwareSasGeneration() => Value;
        public void WriteSoftwareSasGeneration(int value) => Value = value;
        public void DeleteSoftwareSasGeneration() => throw new IOException();
    }

    [Fact]
    public void Install_RevertCannotRemoveTheValue_KeepsThePendingClaim()
    {
        var s = new StuckValueStore();

        Assert.Throws<IOException>(() => SasPolicy.EnableForServices(s));

        Assert.Equal(1, s.Value);
        Assert.Equal(SasMarker.Pending, s.Marker); // uninstall/next install still know the value is ours
    }

    [Fact]
    public void Install_DoneMarkerWriteFails_RemovesThePolicyAgain()
    {
        var s = new FlakyMarkerStore { FailOnWrite = 2 };

        Assert.Throws<IOException>(() => SasPolicy.EnableForServices(s));

        Assert.Null(s.Value);
        Assert.Equal(SasMarker.None, s.Marker);
    }

    [Fact]
    public void Install_CompletesOrDropsAPendingMarkerFromAnInterruptedRun()
    {
        var wrote = new FakeStore { Marker = SasMarker.Pending, Value = 1 };
        var didNot = new FakeStore { Marker = SasMarker.Pending };

        Assert.False(SasPolicy.EnableForServices(wrote));
        Assert.True(SasPolicy.EnableForServices(didNot));

        Assert.Equal(SasMarker.Done, wrote.Marker); // uninstall will now remove it
        Assert.Equal(1, didNot.Value);
        Assert.Equal(SasMarker.Done, didNot.Marker);
    }

    [Theory]
    [InlineData(40L, 10)]
    [InlineData(50L, 0)]
    [InlineData(500L, 0)]
    [InlineData(-5000L, 50)] // clock oddity: never longer than the interval
    public void GdiWait_IsClamped(long elapsed, int expected) =>
        Assert.Equal(expected, DesktopRules.GdiWaitMs(1000 + elapsed, 1000, 50));

    [Fact]
    public void GdiWait_FirstFrameNeverWaits_EvenAfterMonthsOfUptime()
    {
        var uptime = 30L * 24 * 3600 * 1000; // > int.MaxValue ms

        Assert.Equal(0, DesktopRules.GdiWaitMs(uptime, null, 50));
        Assert.Equal(50, DesktopRules.GdiWaitMs(uptime, uptime, 50));
        Assert.Equal(0, DesktopRules.GdiWaitMs(uptime + 80, uptime, 50));
    }

    [Fact]
    public void Install_SetsValueAndRecordsIt_WhenAbsent()
    {
        var s = new FakeStore();

        Assert.True(SasPolicy.EnableForServices(s));

        Assert.Equal(1, s.Value);
        Assert.Equal(SasMarker.Done, s.Marker);
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
        Assert.Equal(SasMarker.None, s.Marker);
    }

    [Fact]
    public void Reinstall_KeepsOurClaim()
    {
        var s = new FakeStore();
        SasPolicy.EnableForServices(s);

        Assert.False(SasPolicy.EnableForServices(s));

        Assert.Equal(SasMarker.Done, s.Marker);
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
        Assert.Equal(SasMarker.None, ours.Marker);
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
        Assert.Equal(SasMarker.None, s.Marker); // claim dropped, value is theirs now
    }
}
