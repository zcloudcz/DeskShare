using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.Platforms.Windows;
using DeskShare.Core.WebRTC;
using NSubstitute;
using Serilog;

namespace DeskShare.UnitTests.WebRTC;

public sealed class PrivacyScreenTests
{
    private const string On = """{"type":"privacy_screen","enabled":true}""";
    private const string Off = """{"type":"privacy_screen","enabled":false}""";

    private sealed class FakeOverlay : IPrivacyOverlay
    {
        public bool Visible;
        public bool BlockRequested;
        public int HideCalls;
        public PrivacyShowResult NextResult = new(true);
        public bool Disposed;
        public event Action<int, string>? Failed;
        public int Generation;
        public void Fail(string reason) { Visible = false; Failed?.Invoke(Generation, reason); }
        public void FailOld(int generation) => Failed?.Invoke(generation, "overlay_failed");
        public Action? OnShow; // runs inside TryShow, to interleave events deterministically

        public PrivacyShowResult TryShow(bool blockInput)
        {
            OnShow?.Invoke();
            Visible = NextResult.Shown;
            Generation++;
            BlockRequested = blockInput;
            return NextResult with { Generation = Generation };
        }

        public void Hide() { Visible = false; HideCalls++; }
        public void Dispose() => Disposed = true;
    }

    private static (DataChannelManager Manager, FakeOverlay Overlay, PrivacyScreenController Controller) Create(
        bool control, bool grantPrivacy = true, bool blockInput = false)
    {
        var input = Substitute.For<IInputController>();
        input.IsEnabled.Returns(control);
        var manager = new DataChannelManager(Substitute.For<ILogger>(), input);
        var overlay = new FakeOverlay();
        var controller = new PrivacyScreenController(overlay, blockInput);
        manager.PrivacyScreen = controller;
        if (grantPrivacy)
            manager.HostGrantedCapabilities |= SessionCapabilities.PrivacyScreen;
        return (manager, overlay, controller);
    }

    [Fact]
    public void Message_NeedsPrivacyScreenAndControl()
    {
        foreach (var (control, privacy) in new[] { (true, false), (false, true), (false, false) })
        {
            var (manager, overlay, _) = Create(control, privacy);
            manager.ProcessInputMessage(On);
            Assert.False(overlay.Visible);
        }

        var (ok, okOverlay, controller) = Create(control: true);
        ok.ProcessInputMessage(On);
        Assert.True(okOverlay.Visible);
        Assert.True(controller.Enabled);
    }

    [Fact]
    public void Capabilities_AreGrantedOnlyWhenHostEnabled()
    {
        var (off, _, _) = Create(control: true, grantPrivacy: false);
        var (on, _, _) = Create(control: true);

        Assert.Equal(SessionCapabilities.None, off.GrantedCapabilities & SessionCapabilities.PrivacyScreen);
        Assert.Equal(SessionCapabilities.PrivacyScreen, on.GrantedCapabilities & SessionCapabilities.PrivacyScreen);
    }

    [Fact]
    public void Viewer_TurnsItOffAgain()
    {
        var (manager, overlay, controller) = Create(control: true);
        manager.ProcessInputMessage(On);

        manager.ProcessInputMessage(Off);

        Assert.False(overlay.Visible);
        Assert.False(controller.Enabled);
    }

    [Fact]
    public void BlockInput_FollowsTheHostOption()
    {
        var (a, overlayA, _) = Create(control: true, blockInput: true);
        var (b, overlayB, _) = Create(control: true, blockInput: false);

        a.ProcessInputMessage(On);
        b.ProcessInputMessage(On);

        Assert.True(overlayA.BlockRequested);
        Assert.False(overlayB.BlockRequested);
    }

    [Fact]
    public void FailedShow_StaysOff_AndReportsReason()
    {
        var overlay = new FakeOverlay { NextResult = new PrivacyShowResult(false, "unsupported_windows_version") };
        var controller = new PrivacyScreenController(overlay, false);
        (bool Enabled, string? Reason)? reported = null;
        controller.StateChanged += (e, r) => reported = (e, r);

        controller.Set(true, controller.Epoch);

        Assert.False(controller.Enabled);
        Assert.Equal((false, "unsupported_windows_version"), reported);
    }

    [Fact]
    public void Disable_IsAutomaticOffAndNotifiesOnce()
    {
        var overlay = new FakeOverlay();
        var controller = new PrivacyScreenController(overlay, true);
        var states = new List<(bool, string?)>();
        controller.StateChanged += (e, r) => states.Add((e, r));
        controller.Set(true, controller.Epoch);

        controller.Disable("channel_closed");
        controller.Disable("channel_closed"); // already off: no second hide, no second message

        Assert.False(overlay.Visible);
        Assert.Equal(new (bool, string?)[] { (true, null), (false, "channel_closed") }, states);
    }

    [Fact]
    public void Dispose_TurnsOffAndReleasesTheOverlay()
    {
        var overlay = new FakeOverlay();
        var controller = new PrivacyScreenController(overlay, false);
        controller.Set(true, controller.Epoch);

        controller.Dispose();

        Assert.False(overlay.Visible);
        Assert.True(overlay.Disposed);
    }

    [Fact]
    public void RequestStale_AfterDisable_DoesNotTurnItOn()
    {
        var overlay = new FakeOverlay();
        var controller = new PrivacyScreenController(overlay, false);
        var captured = controller.Epoch;   // request accepted ...
        controller.Disable("control_revoked"); // ... then the session ends

        controller.Set(true, captured);

        Assert.False(overlay.Visible);
        Assert.False(controller.Enabled);
    }

    [Fact]
    public void Request_IsDropped_WhenCapabilityIsGoneUnderTheLock()
    {
        var overlay = new FakeOverlay();
        var controller = new PrivacyScreenController(overlay, false);

        controller.Set(true, controller.Epoch, () => false);

        Assert.False(overlay.Visible);
    }

    [Fact]
    public void RevokeDuringShow_EndsUpOff()
    {
        var input = Substitute.For<IInputController>();
        input.IsEnabled.Returns(true);
        var manager = new DataChannelManager(Substitute.For<ILogger>(), input);
        var overlay = new FakeOverlay();
        var controller = new PrivacyScreenController(overlay, false);
        manager.PrivacyScreen = controller;
        manager.HostGrantedCapabilities |= SessionCapabilities.PrivacyScreen;

        // The request is accepted, then control is revoked while it is being applied.
        var epoch = controller.Epoch;
        input.IsEnabled.Returns(false);
        controller.Disable("control_revoked");
        manager.ProcessInputMessage(Off); // later "off" is rejected by policy, so nothing may stay on
        controller.Set(true, epoch);

        Assert.False(overlay.Visible);
    }

    [Fact]
    public void OverlayFailure_TurnsStateOff_AndTellsViewer()
    {
        var overlay = new FakeOverlay();
        var controller = new PrivacyScreenController(overlay, true);
        var states = new List<(bool, string?)>();
        controller.StateChanged += (e, r) => states.Add((e, r));
        controller.Set(true, controller.Epoch);

        overlay.Fail("overlay_failed");

        Assert.False(controller.Enabled);
        Assert.Equal((false, "overlay_failed"), states[^1]);
    }

    [Fact]
    public void LateFailureOfOlderShow_DoesNotHideTheCurrentOne()
    {
        var overlay = new FakeOverlay();
        var controller = new PrivacyScreenController(overlay, false);
        controller.Set(true, controller.Epoch); // generation 1
        var old = overlay.Generation;
        controller.Set(true, controller.Epoch); // generation 2 replaces it

        overlay.FailOld(old);

        Assert.True(controller.Enabled);
        Assert.True(overlay.Visible);
    }

    [Theory]
    [InlineData(10, 0, 19040, false)]
    [InlineData(10, 0, 19041, true)]
    [InlineData(10, 0, 22631, true)]
    [InlineData(6, 3, 9600, false)]
    public void CaptureExclusion_NeedsWindows10_2004(int major, int minor, int build, bool expected) =>
        Assert.Equal(expected, PrivacyOverlay.IsCaptureExclusionSupported(new Version(major, minor, build)));
}
