using System.Runtime.Versioning;
using DeskShare.Core.Platforms.Windows;
using DeskShare.Core.Safety;

namespace DeskShare.UnitTests.Safety;

/// <summary>
/// Windows smoke test: creates the real banner window for a moment. It needs an interactive desktop, so on other systems
/// and in non-interactive sessions (a service, some CI agents) each test returns early instead of failing.
/// </summary>
public sealed class SessionBannerSmokeTests
{
    private const uint ExTopmost = 0x8, ExToolWindow = 0x80, ExNoActivate = 0x08000000;

    private static bool CanShowWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Banner_IsTopmostNoFocus_AndStaysVisibleToTheViewer()
    {
        if (!CanShowWindows) return;
        using var banner = new SessionBanner(() => { }, () => { });

        banner.Update(SessionBannerState.Viewing);
        var handle = banner.WindowHandle;
        if (handle == IntPtr.Zero) return; // no desktop to create a window on (headless session)

        Assert.True(PrivacyOverlay.TryGetExStyle(handle, out var ex));
        Assert.Equal(ExTopmost | ExToolWindow | ExNoActivate, ex & (ExTopmost | ExToolWindow | ExNoActivate));
        // Not excluded from capture: the viewer sees the banner as well.
        Assert.True(PrivacyOverlay.TryGetDisplayAffinity(handle, out var affinity));
        Assert.Equal(0u, affinity);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Banner_FollowsTheState_AndDisappearsWhenTheSessionEnds()
    {
        if (!CanShowWindows) return;
        using var banner = new SessionBanner(() => { }, () => { });

        banner.Update(SessionBannerState.Viewing);
        if (banner.WindowHandle == IntPtr.Zero) return;
        banner.Update(SessionBannerState.Controlling);
        Assert.NotEqual(IntPtr.Zero, banner.WindowHandle);

        banner.Update(SessionBannerState.None);
        Assert.Equal(IntPtr.Zero, banner.WindowHandle);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ConfirmationPrompt_UnansweredPromptIsDeniedWhenTheTimeoutCancelsIt()
    {
        if (!CanShowWindows) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var allowed = await ConfirmationPrompt.AskAsync("Smoke test: do not click.", timeout.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(allowed);
    }
}
