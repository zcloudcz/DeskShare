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
    public void RepeatedEvents_DoNotRaiseChangedAgain()
    {
        var (model, seen) = Create();

        model.ViewerStarted();
        model.ViewerStarted();
        model.ControlChanged(false);

        Assert.Single(seen);
    }
}
