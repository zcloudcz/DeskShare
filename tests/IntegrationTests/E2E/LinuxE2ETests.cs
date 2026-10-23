using System.Diagnostics;
using System.Net;
using DeskShare.Core;
using DeskShare.Core.Auth;
using DeskShare.Core.Models;
using DeskShare.Desktop.Shared.Services;

namespace DeskShare.IntegrationTests.E2E;

/// <summary>
/// End-to-end on Linux: real signaling server (separate container), real headless sender capturing an Xvfb
/// display, real ClientManager as the viewer. Run through docker-compose.e2e.yml; skipped elsewhere.
/// Xvfb is 1280x720 and its screen is covered by an RGB(0x30,0x70,0xB0) window started in docker/e2e/run.sh.
/// </summary>
// One collection: the tests share the X displays, the pointer and the signaling rate limits, so they must not overlap.
[Collection("LinuxE2E")]
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
    public async Task RemoteControlEnabled_Typing_ArrivesInTheFocusedWindow()
    {
        // A small xterm in the top-left corner writes what it receives to a file once Enter is pressed. Without a
        // window manager X gives the keyboard to the window under the pointer, so the viewer first moves the
        // pointer onto it. Keys are Windows virtual-key codes, as the web client (event.keyCode) and the
        // Avalonia viewer (KeyMapper.ToVirtualKey) send them, including separate Shift down/up events.
        var typedFile = $"/tmp/typed-{Guid.NewGuid():N}";
        var title = "typing-target";
        var psi = new ProcessStartInfo("xterm") { UseShellExecute = false };
        foreach (var arg in new[] { "-T", title, "-geometry", "40x4+0+0", "-e", "sh", "-c", $"cat > {typedFile}" })
            psi.ArgumentList.Add(arg);
        using var xterm = Process.Start(psi)!;
        try
        {
            Assert.True(await XServer.WaitUntilAsync(() => XServer.HasWindow(title), TimeSpan.FromSeconds(10)), "xterm did not start");

            using var sender = await SenderProcess.StartAsync(remoteControl: true);
            var connected = false;
            using var client = await Viewer.ConnectAsync(sender, (_, _) => connected = true);
            Assert.True(await XServer.WaitUntilAsync(() => connected, TimeSpan.FromSeconds(45)), "No video, so no connection\n" + sender.Output + "\n" + Viewer.LogText());

            // Pointer onto the xterm (normalized 0.05/0.03 -> 64/21); repeated until the data channel delivers it.
            var onXterm = await XServer.WaitUntilAsync(
                () => Math.Abs(XServer.PointerLocation().X - 64) <= 3 && Math.Abs(XServer.PointerLocation().Y - 21) <= 3,
                TimeSpan.FromSeconds(20),
                () => client.SendInputAsync(new InputMessage { Type = InputMessageType.MouseMove, X = 0.05, Y = 0.03 }));
            Assert.True(onXterm, $"Pointer at {XServer.PointerLocation()}\n{sender.Output}");

            const int Shift = 0x10, Enter = 0x0D;
            async Task Key(int vk, bool down) =>
                await client.SendInputAsync(new InputMessage { Type = down ? InputMessageType.KeyDown : InputMessageType.KeyUp, KeyCode = vk });
            async Task Tap(int vk) { await Key(vk, true); await Key(vk, false); }

            await Key(Shift, true); await Tap('H'); await Key(Shift, false);
            foreach (var c in "ELLO1") await Tap(c);
            await Key(Shift, true); await Tap('1'); await Key(Shift, false); // '!'
            await Tap(Enter);

            string Typed() => File.Exists(typedFile) ? File.ReadAllText(typedFile) : "";
            Assert.True(await XServer.WaitUntilAsync(() => Typed() == "Hello1!\n", TimeSpan.FromSeconds(10)),
                $"Typed: '{Typed()}'\n{sender.Output}");
        }
        finally
        {
            try { xterm.Kill(entireProcessTree: true); } catch { /* already gone */ }
            File.Delete(typedFile);
        }
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

    private static async Task<(ClientManager Client, Viewer.FakeClipboard Clipboard)> ConnectWithClipboardAsync(SenderProcess sender)
    {
        var clipboard = new Viewer.FakeClipboard();
        var client = await Viewer.ConnectAsync(sender, clipboard: clipboard);
        // The host answers the viewer's authorization_request on the data channel with its clipboard grant.
        return (client, clipboard);
    }

    [E2EFact]
    public async Task ClipboardEnabled_ViewerText_ReachesTheHostsRealClipboard()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: false, clipboard: true);
        var (client, clipboard) = await ConnectWithClipboardAsync(sender);
        using var _ = client;

        Assert.True(await XServer.WaitUntilAsync(() => client.HostAllowsClipboard, TimeSpan.FromSeconds(45)),
            "The host never reported that it allows the clipboard\n" + sender.Output + "\n" + Viewer.LogText());
        client.ShareClipboard = true;

        // The data channel can still be warming up, so copy again until the text arrives.
        var arrived = await XServer.WaitUntilAsync(
            () => XServer.GetClipboard() == "viewer->host \u00e9",
            TimeSpan.FromSeconds(20),
            () => { clipboard.Copy("viewer->host \u00e9"); return Task.CompletedTask; });

        Assert.True(arrived, $"Host clipboard: '{XServer.GetClipboard()}'\n{sender.Output}\n{Viewer.LogText()}");
    }

    [E2EFact]
    public async Task ClipboardDisabled_ViewerText_NeverReachesTheHost()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: false, clipboard: false);
        var (client, clipboard) = await ConnectWithClipboardAsync(sender);
        using var _ = client;
        client.ShareClipboard = true; // the viewer opts in, but the host never granted it

        var arrived = await XServer.WaitUntilAsync(
            () => XServer.GetClipboard().Contains("must-not-arrive"),
            TimeSpan.FromSeconds(8),
            () => { clipboard.Copy("must-not-arrive"); return Task.CompletedTask; });

        Assert.False(client.HostAllowsClipboard);
        Assert.False(arrived, "Clipboard text reached a host that did not allow clipboard sharing");
    }

    [E2EFact]
    public async Task ClipboardEnabled_HostText_ArrivesAtTheViewer_OnlyWhenViewerOptedIn()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: false, clipboard: true);
        var (client, clipboard) = await ConnectWithClipboardAsync(sender);
        using var _ = client;
        string? received = null;
        client.ClipboardReceived += (_, m) => received = m.TextContent;

        Assert.True(await XServer.WaitUntilAsync(() => client.HostAllowsClipboard, TimeSpan.FromSeconds(45)),
            "The host never reported that it allows the clipboard\n" + sender.Output + "\n" + Viewer.LogText());

        // Viewer has not opted in: the host's text must not touch the viewer's clipboard.
        var leaked = await XServer.WaitUntilAsync(() => clipboard.Text != null, TimeSpan.FromSeconds(4),
            () => { XServer.SetClipboard("host-secret-1"); return Task.CompletedTask; });
        Assert.False(leaked, "Host text was applied although the viewer did not opt in");
        // Stronger than "not applied": the host must not even send it before the viewer subscribed.
        Assert.DoesNotContain("Received clipboard message from host", Viewer.LogText());

        client.ShareClipboard = true;
        var got = await XServer.WaitUntilAsync(() => received == "host->viewer", TimeSpan.FromSeconds(20),
            async () => { XServer.SetClipboard("host->viewer"); await Task.Delay(500); });

        Assert.True(got, $"Viewer received: '{received}'\n{sender.Output}\n{Viewer.LogText()}");
        Assert.Equal("host->viewer", clipboard.Text);
    }

    private static string NewReceiveFolder() => Path.Combine(Path.GetTempPath(), "deskshare-e2e-recv-" + Guid.NewGuid().ToString("N"));

    private static async Task<(string Path, byte[] Content)> WriteRandomFileAsync(int bytes)
    {
        var content = System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes);
        var path = Path.Combine(Path.GetTempPath(), "deskshare-e2e-src-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(path, content);
        return (path, content);
    }

    [E2EFact]
    public async Task FileReceiveEnabled_ViewerFile_ArrivesWithIdenticalBytes()
    {
        var folder = NewReceiveFolder();
        using var sender = await SenderProcess.StartAsync(remoteControl: false, receiveFolder: folder);
        using var client = await Viewer.ConnectAsync(sender);
        Assert.True(await XServer.WaitUntilAsync(() => client.HostAllowsFiles, TimeSpan.FromSeconds(45)),
            "The host never reported that it accepts files\n" + sender.Output + "\n" + Viewer.LogText());
        var (path, content) = await WriteRandomFileAsync(5 * 1024 * 1024);
        try
        {
            var savedAs = await client.SendFileAsync(path).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(System.Security.Cryptography.SHA256.HashData(content),
                System.Security.Cryptography.SHA256.HashData(sender.ReadReceived(savedAs)));
            Assert.Equal([savedAs], sender.ReceivedFiles()); // no .part left behind
        }
        finally { File.Delete(path); Directory.Delete(folder, recursive: true); }
    }

    [E2EFact]
    public async Task FileReceiveDisabled_OfferIsRejected_AndNothingIsWritten()
    {
        var folder = NewReceiveFolder();
        // Remote control on only so that the sender has a data channel at all (a pure view-only sender has none and
        // never answers); file receiving itself stays off.
        using var sender = await SenderProcess.StartAsync(remoteControl: true, receiveFolder: null);
        using var client = await Viewer.ConnectAsync(sender);
        // A modified viewer could offer a file anyway; the host must refuse it.
        var (path, _) = await WriteRandomFileAsync(10_000);
        try
        {
            await Task.Delay(2000); // let the data channel open and the authorization_response arrive
            Assert.False(client.HostAllowsFiles);
            var ex = await Assert.ThrowsAsync<IOException>(() => client.SendFileAsync(path).WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Contains("disabled", ex.Message);
            Assert.Empty(sender.ReceivedFiles());
            Assert.False(Directory.Exists(folder));
        }
        finally { File.Delete(path); }
    }

    [E2EFact]
    public async Task FileReceiveEnabled_TraversalName_IsSavedSanitizedInsideTheFolder()
    {
        var folder = NewReceiveFolder();
        using var sender = await SenderProcess.StartAsync(remoteControl: false, receiveFolder: folder);
        using var client = await Viewer.ConnectAsync(sender);
        Assert.True(await XServer.WaitUntilAsync(() => client.HostAllowsFiles, TimeSpan.FromSeconds(45)),
            "The host never reported that it accepts files\n" + sender.Output + "\n" + Viewer.LogText());
        var (path, content) = await WriteRandomFileAsync(1000);
        try
        {
            // The real viewer sends the plain file name; a hostile one would put the path in the offer.
            var realPasswd = File.ReadAllBytes("/etc/passwd");
            var savedAs = await client.SendFileAsync(path, offeredName: "../../etc/passwd").WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal("passwd", savedAs);
            Assert.Equal(["passwd"], sender.ReceivedFiles());
            Assert.Equal(content, sender.ReadReceived("passwd"));
            Assert.Equal(realPasswd, File.ReadAllBytes("/etc/passwd")); // the real system file is untouched
        }
        finally { File.Delete(path); Directory.Delete(folder, recursive: true); }
    }

    [E2EFact]
    public async Task ViewerWithWrongE2EKey_IsDisconnected_AndGetsNoFrames()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: true);
        var wrongKey = E2EBinding.DeriveKeys("WRONGKEY9", sender.ServerId).E2EKey;

        // The signaling server accepts this viewer (the authKey is right), like a server that relays a man in the middle.
        var frames = 0;
        string? reason = null;
        using var client = await Viewer.ConnectAsync(sender, (_, _) => Interlocked.Increment(ref frames), e2eKeyOverride: wrongKey);
        client.Disconnected += (_, r) => reason ??= r;

        // The sender drops the peer whose binding does not verify; the viewer then reports the failed check.
        var dropped = await XServer.WaitUntilAsync(() => !client.IsConnected, TimeSpan.FromSeconds(45));

        Assert.True(dropped, $"Viewer was not disconnected\n{sender.Output}\n{Viewer.LogText()}");
        Assert.Equal(0, Volatile.Read(ref frames));
        Assert.Contains("end-to-end check failed", sender.Output);
    }

    [E2EFact]
    public async Task PermanentPassword_GivesVideo_WrongPasswordIs401_PasskeyStillWorks()
    {
        // What "DeskShare.Service set-password" leaves on an unattended host: keys only, bound to the Server ID.
        const string password = "unattended e2e password";
        var serverId = "server-" + Random.Shared.Next(100_000_000, 1_000_000_000);
        var keys = E2EBinding.FromPermanentPassword(password, serverId);
        var keyFile = Path.Combine(Path.GetTempPath(), $"permanent-{Guid.NewGuid():N}.key");
        PermanentKeyFile.Save(keyFile, serverId, keys.AuthKey, keys.E2EKey);
        try
        {
            using var sender = await SenderProcess.StartAsync(remoteControl: false, serverId: serverId, permanentKeyFile: keyFile);
            Assert.Equal(serverId, sender.ServerId);

            var (wrong, _) = await Viewer.AuthenticateAsync(serverId, "not the right password", ServerIdGenerator.GenerateClientId());
            Assert.Equal(HttpStatusCode.Unauthorized, wrong);

            // The passkey still works next to the password. Same client id for the password connection below,
            // because the server keeps the session reserved for the client that authenticated last.
            var clientId = ServerIdGenerator.GenerateClientId();
            var (passkeyStatus, _) = await Viewer.AuthenticateAsync(serverId, sender.Passkey, clientId);
            Assert.Equal(HttpStatusCode.OK, passkeyStatus);

            // Frames only flow after the end-to-end binding verified (see ViewerWithWrongE2EKey_...), so a frame proves
            // that the sender accepted the binding made with the password's e2e key.
            var frames = 0;
            using var client = await Viewer.ConnectAsync(sender, (_, _) => Interlocked.Increment(ref frames), credential: password, clientId: clientId);
            Assert.True(await XServer.WaitUntilAsync(() => Volatile.Read(ref frames) > 0, TimeSpan.FromSeconds(45)),
                $"No video with the permanent password\n{sender.Output}\n{Viewer.LogText()}");
        }
        finally { File.Delete(keyFile); }
    }

    [E2EFact]
    public async Task WrongPasskey_IsRejected()
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: false);

        var (status, _) = await Viewer.AuthenticateAsync(sender.ServerId, "WRONGKEY9", ServerIdGenerator.GenerateClientId());

        // Only 401 proves the passkey was checked; a 500 or 429 would also "reject" without proving anything.
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }
}
