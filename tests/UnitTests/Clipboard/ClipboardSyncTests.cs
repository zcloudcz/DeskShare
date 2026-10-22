using System.Text.Json;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.WebRTC;
using NSubstitute;

namespace DeskShare.UnitTests.Clipboard;

/// <summary>
/// Text clipboard sync: nothing moves unless enabled, our own writes are not echoed back, size is limited.
/// </summary>
public sealed class ClipboardSyncTests
{
    private readonly IClipboardManager _clipboard = Substitute.For<IClipboardManager>();
    private readonly List<string> _sent = new();

    private ClipboardSync Create(bool enabled)
    {
        _clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return new ClipboardSync(_clipboard, json => { _sent.Add(json); return true; }) { Enabled = enabled };
    }

    private void LocalCopy(string text) =>
        _clipboard.ClipboardChanged += Raise.Event<EventHandler<ClipboardChangedEventArgs>>(_clipboard, new ClipboardChangedEventArgs { Text = text });

    private static ClipboardMessage Text(string text) =>
        new() { ContentType = ClipboardContentType.Text, TextContent = text, SizeBytes = text.Length };

    [Fact]
    public void LocalChange_IsNotSent_WhenDisabled()
    {
        using var sync = Create(enabled: false);

        LocalCopy("hello");

        Assert.Empty(_sent);
    }

    [Fact]
    public void LocalChange_IsSent_WhenEnabled_InTheUnifiedFormat()
    {
        using var sync = Create(enabled: true);

        LocalCopy("héllo \"x\"\n");

        using var doc = JsonDocument.Parse(Assert.Single(_sent));
        Assert.Equal("clipboard", doc.RootElement.GetProperty("type").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal((int)ClipboardContentType.Text, data.GetProperty("ContentType").GetInt32());
        Assert.Equal("héllo \"x\"\n", data.GetProperty("TextContent").GetString());
    }

    [Fact]
    public async Task RemoteText_IsNotApplied_WhenDisabled()
    {
        using var sync = Create(enabled: false);

        var applied = await sync.ApplyAsync(Text("secret"));

        Assert.False(applied);
        await _clipboard.DidNotReceive().SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoteText_IsApplied_WhenEnabled()
    {
        using var sync = Create(enabled: true);

        var applied = await sync.ApplyAsync(Text("from host"));

        Assert.True(applied);
        await _clipboard.Received(1).SetTextAsync("from host", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppliedRemoteText_IsNotEchoedBack()
    {
        using var sync = Create(enabled: true);

        await sync.ApplyAsync(Text("from host"));
        LocalCopy("from host"); // the change event our own write causes

        Assert.Empty(_sent);

        LocalCopy("typed locally"); // a real local copy still goes out
        Assert.Single(_sent);
    }

    [Fact]
    public void TooLargeLocalText_IsRejectedWithMessage_NotTruncated()
    {
        using var sync = Create(enabled: true);
        string? reason = null;
        sync.Rejected += (_, r) => reason = r;

        LocalCopy(new string('a', ClipboardSync.MaxTextBytes + 1));

        Assert.Empty(_sent);
        Assert.Contains("too large", reason);
    }

    [Fact]
    public void TextAtTheLimit_IsSent_AndFitsTheMessageLimit()
    {
        using var sync = Create(enabled: true);

        LocalCopy(new string('a', ClipboardSync.MaxTextBytes));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Assert.Single(_sent)) <= 64 * 1024);
    }

    [Fact]
    public void Limit_IsInUtf8Bytes_NotChars()
    {
        using var sync = Create(enabled: true);

        LocalCopy(new string('é', ClipboardSync.MaxTextBytes / 2 + 1)); // 2 bytes each

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task TooLargeRemoteText_IsRejected()
    {
        using var sync = Create(enabled: true);
        string? reason = null;
        sync.Rejected += (_, r) => reason = r;

        var applied = await sync.ApplyAsync(Text(new string('a', ClipboardSync.MaxTextBytes + 1)));

        Assert.False(applied);
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task NonTextMessages_AreIgnored()
    {
        using var sync = Create(enabled: true);

        var applied = await sync.ApplyAsync(new ClipboardMessage { ContentType = ClipboardContentType.Image, ImageDataBase64 = "AAAA" });

        Assert.False(applied);
    }
}
