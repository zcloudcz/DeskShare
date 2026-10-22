using DeskShare.Core.Platforms.Windows;

namespace DeskShare.IntegrationTests.Pipeline;

/// <summary>
/// Creates the real overlay for a moment. Windows only and needs an interactive desktop plus Windows 10 2004+;
/// otherwise the test returns without asserting (xunit 2.5 has no runtime skip).
/// </summary>
public sealed class PrivacyOverlayWindowsTests
{
    [Fact]
    public void Overlay_IsExcludedFromCapture_AndRemovedOnHide()
    {
        if (!OperatingSystem.IsWindows() || !PrivacyOverlay.IsCaptureExclusionSupported(Environment.OSVersion.Version) || !Environment.UserInteractive)
            return;

        using var overlay = new PrivacyOverlay();
        var result = overlay.TryShow(blockInput: false);
        if (!result.Shown && result.Reason == "window_create_failed")
            return; // no desktop to create windows on (service session / CI)

        Assert.True(result.Shown, result.Reason);
        Assert.NotEmpty(overlay.WindowHandles);
        foreach (var hwnd in overlay.WindowHandles)
        {
            Assert.True(PrivacyOverlay.TryGetDisplayAffinity(hwnd, out var affinity));
            Assert.Equal(0x11u, affinity); // WDA_EXCLUDEFROMCAPTURE

            Assert.True(PrivacyOverlay.TryGetExStyle(hwnd, out var style));
            Assert.NotEqual(0u, style & 0x20); // WS_EX_TRANSPARENT: click-through
        }

        overlay.Hide();
        Assert.Empty(overlay.WindowHandles);
    }
}
