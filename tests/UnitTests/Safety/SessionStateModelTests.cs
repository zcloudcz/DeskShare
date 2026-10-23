using DeskShare.Core.Safety;

namespace DeskShare.UnitTests.Safety;

/// <summary>Banner state (none / viewing / controlling) driven by session events.</summary>
public sealed class SessionStateModelTests
{
    private static (SessionStateModel Model, List<SessionBannerState> Seen) Create()
    {
        var model = new SessionStateModel();
        var seen = new List<SessionBannerState>();
        model.Changed += seen.Add;
        return (model, seen);
    }

    [Fact]
    public void NoViewer_ShowsNothing_EvenIfControlIsAuthorized()
    {
        var (model, seen) = Create();

        model.ControlChanged(true); // control "armed" before anybody connected

        Assert.Equal(SessionBannerState.None, model.State);
        Assert.Empty(seen);
    }

    [Fact]
    public void ViewerConnects_ThenGetsControl_ThenLosesItAndLeaves()
    {
        var (model, seen) = Create();

        model.ViewerStarted();
        model.ControlChanged(true);
        model.ControlChanged(false);
        model.ViewerEnded();

        Assert.Equal(
            new[] { SessionBannerState.Viewing, SessionBannerState.Controlling, SessionBannerState.Viewing, SessionBannerState.None },
            seen);
    }

    [Fact]
    public void ViewerArrivesWhileControlIsAlreadyAuthorized_ShowsControlling()
    {
        var (model, _) = Create();
        model.ControlChanged(true);

        model.ViewerStarted();

        Assert.Equal(SessionBannerState.Controlling, model.State);
    }

    [Fact]
    public void Recording_ChangesTheBannerTextAndEndsWithTheViewer()
    {
        var (model, seen) = Create();

        model.RecordingChanged(true); // nobody connected: nothing to show
        Assert.False(model.Recording);
        Assert.Empty(seen);

        model.ViewerStarted();
        model.RecordingChanged(true);
        Assert.True(model.Recording);
        Assert.Equal(SessionBannerState.Viewing, model.State);
        Assert.Equal(DeskShare.Core.Platforms.Windows.SessionBanner.ViewingText + " (recording)",
            DeskShare.Core.Platforms.Windows.SessionBanner.TextFor(model.State, model.Recording));
        Assert.Equal(DeskShare.Core.Platforms.Windows.SessionBanner.ControllingText + " (recording)",
            DeskShare.Core.Platforms.Windows.SessionBanner.TextFor(SessionBannerState.Controlling, true));

        model.ViewerEnded();
        Assert.False(model.Recording);
        Assert.Equal(3, seen.Count); // viewing, viewing+recording, none
    }

    [Fact]
    public void Recording_DelayedOlderDecision_CannotOverwriteANewerOne()
    {
        var (model, _) = Create();
        model.ViewerStarted();

        // Decision 1 = on, decision 2 = off; their publication happens in the opposite order.
        model.RecordingChanged(false, sequence: 2);
        model.RecordingChanged(true, sequence: 1);

        Assert.False(model.Recording);
    }

    [Fact]
    public void Snapshot_VersionGrowsWithEveryChange_AndStateIsConsistent()
    {
        var (model, _) = Create();
        var v0 = model.Snapshot.Version;

        model.ViewerStarted();
        model.RecordingChanged(true, 1);
        var s = model.Snapshot;

        Assert.True(s.Version > v0 + 1);
        Assert.Equal((SessionBannerState.Viewing, true), (s.State, s.Recording));

        model.ViewerEnded();
        Assert.Equal((SessionBannerState.None, false), (model.Snapshot.State, model.Snapshot.Recording));
        Assert.True(model.Snapshot.Version > s.Version);
    }

    [Fact]
    public void RepeatedEvents_DoNotRaiseChangedAgain()
    {
        var (model, seen) = Create();

        model.ViewerStarted();
        model.ViewerStarted();
        model.ControlChanged(false);

        Assert.Single(seen);
    }
}
