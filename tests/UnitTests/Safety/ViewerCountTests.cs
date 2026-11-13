using DeskShare.Core;
using DeskShare.Core.Auth;
using DeskShare.Core.Safety;
using DeskShare.Core.WebRTC;
using NSubstitute;

namespace DeskShare.UnitTests.Safety;

/// <summary>
/// "Active Connections" in the desktop app comes from <see cref="SessionSafety.ViewerCountChanged"/>: a viewer counts
/// once its end-to-end binding verified, and stops counting when its session ends.
/// </summary>
public sealed class ViewerCountTests
{
    private const string BindJson = """{"type":"e2e_bind","v":1,"role":"viewer","mac":"abcd"}""";

    private static (WebRTCSessionWithInput Session, SessionSafety Safety, List<int> Counts) Create()
    {
        var session = new WebRTCSessionWithInput(Substitute.For<Serilog.ILogger>());
        session.DataChannel.VerifyViewerBind = (_, _) => "sender-mac";
        var safety = new SessionSafety(session, new ScreenSenderConfiguration { ShowSessionBanner = false },
            Substitute.For<IAuditSink>(), Substitute.For<Microsoft.Extensions.Logging.ILogger>(), null, null);
        var counts = new List<int>();
        safety.ViewerCountChanged += counts.Add;
        return (session, safety, counts);
    }

    [Fact]
    public void VerifiedViewer_CountsOne_AndZeroWhenItsSessionEnds()
    {
        var (session, safety, counts) = Create();
        using var _ = session;
        using var __ = safety;

        session.DataChannel.ProcessInputMessage(BindJson);
        Assert.Equal(new[] { 1 }, counts);

        session.DataChannel.BeginPeer(new PeerContext()); // the viewer left / another connection took over
        Assert.Equal(new[] { 1, 0 }, counts);
    }

    [Fact]
    public void UnverifiedConnection_IsNotCounted()
    {
        var (session, safety, counts) = Create();
        using var _ = session;
        using var __ = safety;

        session.DataChannel.BeginPeer(new PeerContext());

        Assert.Empty(counts);
    }

    [Fact]
    public void NextViewer_AfterTheFirstEnded_CountsAgain()
    {
        var (session, safety, counts) = Create();
        using var _ = session;
        using var __ = safety;

        session.DataChannel.ProcessInputMessage(BindJson);
        session.DataChannel.BeginPeer(new PeerContext());
        session.DataChannel.ProcessInputMessage(BindJson);

        Assert.Equal(new[] { 1, 0, 1 }, counts);
    }
}
