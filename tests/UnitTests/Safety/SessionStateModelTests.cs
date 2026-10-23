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
    public void RepeatedEvents_DoNotRaiseChangedAgain()
    {
        var (model, seen) = Create();

        model.ViewerStarted();
        model.ViewerStarted();
        model.ControlChanged(false);

        Assert.Single(seen);
    }
}
