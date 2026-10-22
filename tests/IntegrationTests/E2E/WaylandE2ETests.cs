using DeskShare.Desktop.Shared.Services;

namespace DeskShare.IntegrationTests.E2E;

/// <summary>
/// Marks a Wayland E2E test. Runs only in docker/e2e-wayland (DESKSHARE_E2E_WAYLAND=1); the X11 E2E container and
/// normal `dotnet test` runs report it as skipped.
/// </summary>
public sealed class WaylandE2EFactAttribute : FactAttribute
{
    public WaylandE2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DESKSHARE_E2E_WAYLAND") != "1")
            Skip = "Wayland E2E: run docker-compose.e2e-wayland.yml";
    }
}

/// <summary>
/// End-to-end on Wayland: the sender runs in a Wayland-only session (headless sway, no DISPLAY) and must capture
/// through the ScreenCast portal (xdg-desktop-portal-wlr) and PipeWire. The output is painted RGB(0x30,0x70,0xB0)
/// by docker/e2e-wayland/sway.config.
/// </summary>
[Collection("WaylandE2E")]
public class WaylandE2ETests
{
    private const int ScreenWidth = 1280, ScreenHeight = 720;
    private const int PaintedR = 0x30, PaintedG = 0x70, PaintedB = 0xB0;

    private static string RestoreTokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskShare", "wayland-restore-token");

    [WaylandE2EFact]
    public async Task Viewer_ReceivesDecodedFrames_CapturedThroughThePortal()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: false);
        await AssertPaintedFrameArrivesAsync(sender);
        Assert.Contains("PipeWireScreenCapturer", sender.Output);
    }

    [WaylandE2EFact]
    public async Task SecondStart_ReusesTheStoredRestoreToken()
    {
        File.Delete(RestoreTokenPath);
        using (var first = await SenderProcess.StartAsync(remoteControl: false))
        {
            Assert.True(File.Exists(RestoreTokenPath), "The portal returned no restore token or it was not stored\n" + first.Output);
        }

        using var second = await SenderProcess.StartAsync(remoteControl: false);
        Assert.Contains("restore token present", second.Output);
        await AssertPaintedFrameArrivesAsync(second);
    }

    [WaylandE2EFact]
    public async Task RemoteControlEnabled_WithoutRemoteDesktopPortal_StillStreams()
    {
        // xdg-desktop-portal-wlr has no RemoteDesktop interface: input is refused with a clear log line, video works.
        using var sender = await SenderProcess.StartAsync(remoteControl: true);
        await AssertPaintedFrameArrivesAsync(sender);
        Assert.Contains("no RemoteDesktop", sender.Output);
    }

    [Fact(Skip = "No headless compositor in the image offers the RemoteDesktop portal: xdg-desktop-portal-wlr implements " +
                 "ScreenCast only, and GNOME/KDE need a consent dialog nobody can click in a container. The input mapping " +
                 "is unit tested (WaylandPortalTests); verify on a real GNOME/KDE session.")]
    public void RemoteControlEnabled_MouseMove_MovesTheWaylandPointer()
    {
    }

    private static async Task AssertPaintedFrameArrivesAsync(SenderProcess sender)
    {
        VideoFrameReceivedEventArgs? match = null, last = null;
        using var client = await Viewer.ConnectAsync(sender, (_, f) =>
        {
            last = f;
            if (IsPainted(f)) match = f;
        });

        var ok = await XServer.WaitUntilAsync(() => match != null, TimeSpan.FromSeconds(45));
        Assert.True(ok, $"No frame with the painted colour. Last frame: {(last == null ? "none" : $"{last.Width}x{last.Height}")}\n{sender.Output}\n{Viewer.LogText()}");
    }

    private static bool IsPainted(VideoFrameReceivedEventArgs f)
    {
        if (f.Width != ScreenWidth || f.Height != ScreenHeight) return false;
        var i = (200 * f.Width + 1000) * 4; // BGRA32, tightly packed; away from where a cursor could be
        const int tolerance = 16; // VP8 is lossy and goes through YUV
        return Math.Abs(f.FrameData[i] - PaintedB) <= tolerance
            && Math.Abs(f.FrameData[i + 1] - PaintedG) <= tolerance
            && Math.Abs(f.FrameData[i + 2] - PaintedR) <= tolerance;
    }
}
