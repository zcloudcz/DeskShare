using DeskShare.Core;
using DeskShare.Core.Models;
using DeskShare.Desktop.Shared.Services;

namespace DeskShare.IntegrationTests.E2E;

/// <summary>
/// End-to-end on Linux: real signaling server (separate container), real headless sender capturing an Xvfb
/// display, real ClientManager as the viewer. Run through docker-compose.e2e.yml; skipped elsewhere.
/// Xvfb is 1280x720 and its screen is covered by an RGB(0x30,0x70,0xB0) window started in docker/e2e/run.sh.
/// </summary>
public class LinuxE2ETests
{
    private const int ScreenWidth = 1280, ScreenHeight = 720;
    private const int PaintedR = 0x30, PaintedG = 0x70, PaintedB = 0xB0;

    [E2EFact]
    public async Task Viewer_ReceivesDecodedFrames_OfTheSendersScreen()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: false);

        VideoFrameReceivedEventArgs? match = null;
        VideoFrameReceivedEventArgs? last = null;
        using var client = await Viewer.ConnectAsync(sender, (_, f) =>
        {
            last = f;
            if (IsPainted(f)) match = f;
        });

        // The first frames can still be black while the encoder warms up, so wait for one with the right colour.
        var ok = await XServer.WaitUntilAsync(() => match != null, TimeSpan.FromSeconds(45));

        Assert.True(ok, $"No frame with the painted colour. Last frame: {(last == null ? "none" : $"{last.Width}x{last.Height} pixel={Describe(last)}")}\n{sender.Output}\n{Viewer.LogText()}");
        Assert.Equal(ScreenWidth, match!.Width);
        Assert.Equal(ScreenHeight, match.Height);
    }

    private static string Describe(VideoFrameReceivedEventArgs f)
    {
        var i = (200 * f.Width + 1000) * 4;
        return f.Width > 1000 && f.Height > 200 ? $"B{f.FrameData[i]} G{f.FrameData[i + 1]} R{f.FrameData[i + 2]} A{f.FrameData[i + 3]}" : "n/a";
    }

    // Sampled away from the screen centre on purpose: the pointer rests there on a fresh Xvfb and
    // x11grab draws the cursor into the capture.
    private static bool IsPainted(VideoFrameReceivedEventArgs f)
    {
        if (f.Width != ScreenWidth || f.Height != ScreenHeight) return false;
        var i = (200 * f.Width + 1000) * 4; // BGRA32, tightly packed
        const int tolerance = 16; // VP8 is lossy and goes through YUV
        return Math.Abs(f.FrameData[i] - PaintedB) <= tolerance
            && Math.Abs(f.FrameData[i + 1] - PaintedG) <= tolerance
            && Math.Abs(f.FrameData[i + 2] - PaintedR) <= tolerance;
    }

    [E2EFact]
    public async Task RemoteControlEnabled_MouseMove_MovesTheRealPointer()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: true);
        XServer.MovePointer(10, 10);

        var connected = false;
        using var client = await Viewer.ConnectAsync(sender, (_, _) => connected = true);
        Assert.True(await XServer.WaitUntilAsync(() => connected, TimeSpan.FromSeconds(45)), "No video, so no connection\n" + sender.Output + "\n" + Viewer.LogText());

        // The data channel may open a moment after the first frame; input sent before that is dropped,
        // so keep sending until the pointer arrives (normalized 0.25/0.75 -> 320/540).
        var moved = await XServer.WaitUntilAsync(
            () => Math.Abs(XServer.PointerLocation().X - 320) <= 3 && Math.Abs(XServer.PointerLocation().Y - 540) <= 3,
            TimeSpan.FromSeconds(20),
            () => client.SendInputAsync(new InputMessage { Type = InputMessageType.MouseMove, X = 0.25, Y = 0.75 }));

        Assert.True(moved, $"Pointer at {XServer.PointerLocation()}, expected ~(320, 540)\n{sender.Output}\n{Viewer.LogText()}");
    }

    [E2EFact]
    public async Task RemoteControlDisabled_MouseMove_LeavesThePointerAlone()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: false);
        XServer.MovePointer(10, 10);

        var connected = false;
        using var client = await Viewer.ConnectAsync(sender, (_, _) => connected = true);
        Assert.True(await XServer.WaitUntilAsync(() => connected, TimeSpan.FromSeconds(45)), "No video, so no connection\n" + sender.Output + "\n" + Viewer.LogText());

        // Same move as the positive test, repeated for the same kind of window. The condition is "pointer
        // moved", which must stay false for the whole period.
        var moved = await XServer.WaitUntilAsync(
            () => XServer.PointerLocation() != (10, 10),
            TimeSpan.FromSeconds(6),
            () => client.SendInputAsync(new InputMessage { Type = InputMessageType.MouseMove, X = 0.25, Y = 0.75 }));

        Assert.False(moved, $"Pointer moved to {XServer.PointerLocation()} although remote control is disabled");
    }

    [E2EFact]
    public async Task WrongPasskey_IsRejected()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: false);

        var auth = await Viewer.AuthenticateAsync(sender.ServerId, "WRONGKEY9", ServerIdGenerator.GenerateClientId());

        Assert.True(auth == null || !auth.Success);
    }
}
