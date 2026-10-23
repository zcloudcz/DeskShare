using DeskShare.Core.Displays;
using DeskShare.Core.Platforms.Windows;

namespace DeskShare.IntegrationTests.Pipeline;

/// <summary>
/// Lists the real DXGI outputs and opens a duplicator on each. Windows only and meaningful with two or more monitors
/// on an interactive desktop; otherwise the test returns without asserting (xunit 2.5 has no runtime skip).
/// </summary>
public sealed class DisplayEnumerationWindowsTests
{
    [Fact]
    public void EveryOutput_IsListedWithItsRectangle_AndCanBeCaptured()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            return;

        var monitors = DesktopDuplicator.EnumerateMonitors();
        if (monitors.Count < 2)
            return; // one monitor (or none, e.g. a service session): nothing to switch between

        var displays = DisplayCatalog.Enumerate(0, 0);
        Assert.Equal(monitors.Count, displays.Count);
        Assert.Single(displays, d => d.Primary); // the primary monitor sits at 0,0 and there is only one
        Assert.Equal(displays.Count, displays.Select(d => (d.AdapterIndex, d.OutputIndex)).Distinct().Count());

        foreach (var display in displays)
        {
            Assert.Equal(DisplayInfo.FormatName(display.Index, display.Width, display.Height, display.Primary), display.Name);

            using var capturer = new DesktopDuplicator(display.AdapterIndex, display.OutputIndex);
            if (!capturer.Initialize())
                continue; // duplication can be refused (secure desktop, another app duplicating the output)

            Assert.Equal(display.Width / 2 * 2, capturer.Width); // the capturer rounds to even sizes
            Assert.Equal(display.Height / 2 * 2, capturer.Height);
        }
    }
}
