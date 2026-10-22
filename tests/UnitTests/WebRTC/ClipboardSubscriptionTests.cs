using DeskShare.Core.Models;
using DeskShare.Core.WebRTC;
using NSubstitute;
using Serilog;
using SIPSorcery.Net;

namespace DeskShare.UnitTests.WebRTC;

/// <summary>
/// The host grants the clipboard, but only sends its own text to a viewer that subscribed on the current channel.
/// </summary>
public sealed class ClipboardSubscriptionTests
{
    private const string Subscribe = """{"type":"clipboard_subscribe","enabled":true}""";
    private const string Unsubscribe = """{"type":"clipboard_subscribe","enabled":false}""";

    private static DataChannelManager Create(bool granted) =>
        new(Substitute.For<ILogger>()) { HostGrantedCapabilities = granted ? SessionCapabilities.Clipboard : SessionCapabilities.None };

    [Fact]
    public void NothingIsSent_BeforeTheViewerSubscribes()
    {
        var manager = Create(granted: true);

        Assert.False(manager.ClipboardSubscribed);
        Assert.False(manager.TrySendClipboard("{}"));
    }

    [Fact]
    public void Subscribe_ThenUnsubscribe_TogglesTheState()
    {
        var manager = Create(granted: true);

        manager.ProcessInputMessage(Subscribe);
        Assert.True(manager.ClipboardSubscribed);

        manager.ProcessInputMessage(Unsubscribe);
        Assert.False(manager.ClipboardSubscribed);
    }

    [Fact]
    public void Subscribe_IsIgnored_WhenHostDidNotGrantTheClipboard()
    {
        var manager = Create(granted: false);

        manager.ProcessInputMessage(Subscribe);

        Assert.False(manager.ClipboardSubscribed);
    }

    [Fact]
    public void Subscription_IsReset_WhenTheChannelIsReplaced()
    {
        var manager = Create(granted: true);
        manager.AttachDataChannel(new RTCDataChannel(null!, new RTCDataChannelInit()));
        manager.ProcessInputMessage(Subscribe);
        Assert.True(manager.ClipboardSubscribed);

        manager.AttachDataChannel(new RTCDataChannel(null!, new RTCDataChannelInit())); // next viewer

        Assert.False(manager.ClipboardSubscribed);
        Assert.False(manager.TrySendClipboard("{}"));
    }
}
