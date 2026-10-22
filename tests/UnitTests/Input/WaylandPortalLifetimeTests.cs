#pragma warning disable CA1416 // The tested logic is plain .NET; only the real portal session needs Linux.
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.Platforms.Linux;
using Serilog;

namespace DeskShare.UnitTests.Input;

/// <summary>
/// Lifetime of the shared portal session (<see cref="SharedSessionCache{T}"/>) and the authorization races of
/// <see cref="WaylandPortalInputController"/>, both against fakes instead of D-Bus.
/// </summary>
public sealed class WaylandPortalLifetimeTests
{
    private sealed class FakeSession : IDisposable
    {
        public int DisposeCount;
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void Cache_StartStopRestart_LastReleaseClosesAndRestartOpensANewSession()
    {
        var cache = new SharedSessionCache<FakeSession>(TimeSpan.Zero);
        var opened = 0;
        FakeSession Open() { opened++; return new FakeSession(); }

        // Start: capturer and input controller share one session.
        var capture = cache.Acquire(Open);
        var input = cache.Acquire(Open);
        Assert.Same(capture, input);
        Assert.Equal(1, opened);

        // Stop: the first release keeps it, the final one closes it.
        cache.Release(capture);
        Assert.Equal(0, capture.DisposeCount);
        cache.Release(input);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Null(cache.Current);

        // Restart: a fresh session.
        var again = cache.Acquire(Open);
        Assert.NotSame(capture, again);
        Assert.Equal(2, opened);
    }

    [Fact]
    public void Cache_ReleaseRightAfterOpening_KeepsTheSessionForTheNextAcquire()
    {
        // The sender's size probe releases the session moments before the real capturer asks for it again;
        // reusing it avoids a second consent dialog.
        var cache = new SharedSessionCache<FakeSession>(TimeSpan.FromMinutes(1));
        var opened = 0;
        FakeSession Open() { opened++; return new FakeSession(); }

        var probe = cache.Acquire(Open);
        cache.Release(probe);
        Assert.Equal(0, probe.DisposeCount);

        var real = cache.Acquire(Open);
        Assert.Same(probe, real);
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task Cache_KeptSession_IsClosedOnceTheKeepTimeRunsOut()
    {
        var cache = new SharedSessionCache<FakeSession>(TimeSpan.FromMilliseconds(100));
        var session = cache.Acquire(() => new FakeSession());
        cache.Release(session);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.DisposeCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(1, session.DisposeCount);
        Assert.Null(cache.Current);
    }

    [Fact]
    public void Cache_SessionClosedByThePortal_IsNotHandedOutAgain_AndLateReleasesAreIgnored()
    {
        var cache = new SharedSessionCache<FakeSession>(TimeSpan.Zero);
        var closed = cache.Acquire(() => new FakeSession());

        cache.Invalidate(closed); // Session.Closed: the user stopped sharing from the desktop
        Assert.Equal(1, closed.DisposeCount);

        var fresh = cache.Acquire(() => new FakeSession());
        Assert.NotSame(closed, fresh);

        cache.Release(closed); // the old holder lets go later
        Assert.Equal(1, closed.DisposeCount);
        Assert.Equal(0, fresh.DisposeCount);
        Assert.Same(fresh, cache.Current);
    }

    private sealed class FakePortal : IRemoteDesktopPortal
    {
        public bool CanInjectInput { get; init; } = true;
        public bool IsClosed { get; set; }
        public int StreamWidth => 1280;
        public int StreamHeight => 720;
        public readonly List<string> Calls = new();
        public bool NotifyPointerMotionAbsolute(double x, double y) { Calls.Add($"move {x} {y}"); return true; }
        public bool NotifyPointerButton(int button, bool pressed) { Calls.Add($"button {button} {pressed}"); return true; }
        public bool NotifyPointerAxisDiscrete(int steps) { Calls.Add($"axis {steps}"); return true; }
        public bool NotifyKeyboardKeycode(int keycode, bool pressed) { Calls.Add($"key {keycode} {pressed}"); return true; }
    }

    /// <summary>A fake shared session whose acquire blocks until the test opens the gate (the consent dialog).</summary>
    private sealed class FakePortalSource
    {
        public readonly ManualResetEventSlim Gate = new(true);
        public readonly ManualResetEventSlim Entered = new(false);
        public FakePortal Portal = new();
        public int Acquired, Released;
        public readonly List<IRemoteDesktopPortal> ReleasedPortals = new();

        public IRemoteDesktopPortal Acquire()
        {
            Entered.Set();
            Gate.Wait();
            Interlocked.Increment(ref Acquired);
            return Portal;
        }

        public void Release(IRemoteDesktopPortal portal)
        {
            lock (ReleasedPortals) ReleasedPortals.Add(portal);
            Interlocked.Increment(ref Released);
        }

        public WaylandPortalInputController CreateController() =>
            new(new LoggerConfiguration().CreateLogger(), Acquire, Release) { AuthorizationRequested = _ => true };
    }

    [Fact]
    public async Task Authorize_Granted_InjectsMappedInput_AndDisposeReleasesTheSession()
    {
        var source = new FakePortalSource();
        var controller = source.CreateController();

        Assert.True(await controller.RequestAuthorizationAsync("viewer"));
        Assert.True(controller.ApplyInput(new InputMessage { Type = InputMessageType.MouseMove, X = 0.25, Y = 0.75 }));
        Assert.True(controller.ApplyInput(new InputMessage { Type = InputMessageType.KeyDown, KeyCode = 'A' }));
        Assert.Equal(new[] { "move 320 540", "key 30 True" }, source.Portal.Calls);

        controller.Dispose();
        Assert.Equal(1, source.Acquired);
        Assert.Equal(1, source.Released);
    }

    [Fact]
    public async Task Revoke_WhileThePortalDialogIsOpen_WinsOverTheLateGrant()
    {
        var source = new FakePortalSource();
        source.Gate.Reset();
        var controller = source.CreateController();

        var pending = controller.RequestAuthorizationAsync("viewer");
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(5)));
        controller.RevokeAuthorization();
        source.Gate.Set(); // the user finally answers the dialog

        Assert.False(await pending);
        Assert.Equal(InputAuthorizationState.Revoked, controller.AuthorizationState);
        Assert.False(controller.ApplyInput(new InputMessage { Type = InputMessageType.MouseMove, X = 0.5, Y = 0.5 }));
        Assert.Equal(1, source.Released); // the abandoned attempt gave its reference back
        Assert.Empty(source.Portal.Calls);
    }

    [Fact]
    public async Task Cancel_WhileThePortalDialogIsOpen_ReturnsAtOnce_AndReleasesTheLateSession()
    {
        var source = new FakePortalSource();
        source.Gate.Reset();
        var controller = source.CreateController();
        using var cts = new CancellationTokenSource();

        var pending = controller.RequestAuthorizationAsync("viewer", cts.Token);
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(5)));
        cts.Cancel();

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(InputAuthorizationState.Authorized, controller.AuthorizationState);

        source.Gate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (source.Released == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(1, source.Released);
    }

    [Fact]
    public async Task Dispose_WhileThePortalDialogIsOpen_ReleasesTheLateSession()
    {
        var source = new FakePortalSource();
        source.Gate.Reset();
        var controller = source.CreateController();

        var pending = controller.RequestAuthorizationAsync("viewer");
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(5)));
        controller.Dispose();
        source.Gate.Set();

        Assert.False(await pending);
        Assert.Equal(1, source.Acquired);
        Assert.Equal(1, source.Released);
    }

    [Fact]
    public async Task Reauthorize_AfterThePortalClosedTheSession_OpensANewOne()
    {
        var source = new FakePortalSource();
        var controller = source.CreateController();
        Assert.True(await controller.RequestAuthorizationAsync("viewer"));
        var closed = source.Portal;

        closed.IsClosed = true; // the user stopped sharing from the desktop
        controller.RevokeAuthorization();
        source.Portal = new FakePortal();

        Assert.True(await controller.RequestAuthorizationAsync("viewer"));
        Assert.True(controller.ApplyInput(new InputMessage { Type = InputMessageType.MouseMove, X = 0, Y = 0 }));

        Assert.Equal(new IRemoteDesktopPortal[] { closed }, source.ReleasedPortals);
        Assert.Empty(closed.Calls);
        Assert.Single(source.Portal.Calls);
    }

    private sealed class NullInput : IInputController
    {
        public bool IsEnabled => false;
        public InputAuthorizationState AuthorizationState => InputAuthorizationState.NotAuthorized;
        public event EventHandler<InputAuthorizationState>? AuthorizationStateChanged { add { } remove { } }
        public Func<string, bool>? AuthorizationRequested { get; set; }
        public Task<bool> RequestAuthorizationAsync(string clientId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void RevokeAuthorization() { }
        public bool ApplyInput(InputMessage message) => false;
        public InputStatistics GetStatistics() => new();
        public void Dispose() => Disposed++;
        public int Disposed;
    }

    [Fact]
    public void WebRTCSessionWithInput_Dispose_DisposesTheInputController()
    {
        // The sender hands the controller to the session and never disposes it itself.
        var input = new NullInput();
        var session = new DeskShare.Core.WebRTC.WebRTCSessionWithInput(new LoggerConfiguration().CreateLogger(), input);

        session.Dispose();

        Assert.Equal(1, input.Disposed);
    }
}
