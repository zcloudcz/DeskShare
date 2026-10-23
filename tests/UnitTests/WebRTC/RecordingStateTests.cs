using DeskShare.Core.Auth;
using DeskShare.Core.Safety;
using DeskShare.Core.WebRTC;
using NSubstitute;
using Serilog;

namespace DeskShare.UnitTests.WebRTC;

/// <summary>
/// "recording_state" is the viewer's honest statement that it records the session. It needs View, is ignored before the
/// e2e verification, and its flag belongs to one peer connection.
/// </summary>
public sealed class RecordingStateTests
{
    private const string On = """{"type":"recording_state","on":true}""";
    private const string Off = """{"type":"recording_state","on":false}""";

    private static DataChannelManager Create(bool requireBinding) =>
        new(Substitute.For<ILogger>()) { VerifyViewerBind = requireBinding ? (_, _) => null : null };

    [Fact]
    public void Policy_RecordingState_NeedsView()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(On);

        Assert.True(DataChannelPolicy.TryGetRequiredCapability(doc.RootElement, out _, out var required));
        Assert.Equal(DeskShare.Core.Models.SessionCapabilities.View, required);
    }

    [Fact]
    public void RejectedBeforeVerification()
    {
        var manager = Create(requireBinding: true);
        var peer = new PeerContext(); // never verified
        manager.BeginPeer(peer);
        var raised = 0;
        manager.ViewerRecordingChanged += (_, _) => raised++;

        manager.ProcessInputMessage(peer, On);

        Assert.False(peer.Recording);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Verified_OnOff_RaisesOncePerChange()
    {
        var manager = Create(requireBinding: true);
        var peer = new PeerContext();
        manager.BeginPeer(peer);
        peer.TryVerify();
        var seen = new List<bool>();
        manager.ViewerRecordingChanged += (_, p) => seen.Add(p.Recording);

        manager.ProcessInputMessage(peer, On);
        manager.ProcessInputMessage(peer, On); // repeat: no second event
        manager.ProcessInputMessage(peer, Off);

        Assert.Equal(new[] { true, false }, seen);
    }

    [Fact]
    public void ReplacingThePeer_ClearsItsRecordingFlag()
    {
        var manager = Create(requireBinding: true);
        var first = new PeerContext();
        manager.BeginPeer(first);
        first.TryVerify();
        manager.ProcessInputMessage(first, On);
        Assert.True(first.Recording);

        var second = new PeerContext();
        manager.BeginPeer(second); // retires the first

        Assert.False(first.Recording);
        Assert.False(second.Recording);
    }

    [Fact]
    public void AuditKinds_AreOnePerState()
    {
        Assert.Equal(AuditKind.RecordingStarted, AuditEvents.Recording(true).Kind);
        Assert.Equal(AuditKind.RecordingStopped, AuditEvents.Recording(false).Kind);
        Assert.NotEqual(AuditEvents.Recording(true).EventId, AuditEvents.Recording(false).EventId);
    }
}
