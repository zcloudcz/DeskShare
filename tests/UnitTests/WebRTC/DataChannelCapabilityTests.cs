using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.WebRTC;
using NSubstitute;
using Serilog;

namespace DeskShare.UnitTests.WebRTC;

/// <summary>
/// Session-level capability policy of the sender's data channel: every message type maps to one
/// capability, and anything unknown or not granted is rejected before a handler runs.
/// </summary>
public sealed class DataChannelCapabilityTests
{
    private const string ClipboardJson =
        """{"type":"clipboard","data":{"ContentType":0,"TextContent":"secret","SizeBytes":6}}""";

    private static (DataChannelManager Manager, IInputController Controller) Create(bool controlEnabled)
    {
        var controller = Substitute.For<IInputController>();
        controller.IsEnabled.Returns(controlEnabled);
        controller.ApplyInput(Arg.Any<InputMessage>()).Returns(true);
        return (new DataChannelManager(Substitute.For<ILogger>(), controller), controller);
    }

    // Every message type the viewers send today, with the capability it needs.
    public static IEnumerable<object[]> InputMessages() =>
        Enum.GetNames<InputMessageType>().Select(n => new object[] { $$"""{"Type":"{{n}}","X":0.5,"Y":0.5}""" });

    [Theory]
    [MemberData(nameof(InputMessages))]
    public void Input_IsApplied_WhenControlGranted(string json)
    {
        var (manager, controller) = Create(controlEnabled: true);

        manager.ProcessInputMessage(json);

        controller.Received(1).ApplyInput(Arg.Any<InputMessage>());
    }

    [Theory]
    [MemberData(nameof(InputMessages))]
    public void Input_IsRejected_WhenControlNotGranted_EvenWithController(string json)
    {
        var (manager, controller) = Create(controlEnabled: false);

        manager.ProcessInputMessage(json);

        controller.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
    }

    [Fact]
    public void Input_WithNumericType_NeedsControlToo()
    {
        var (denied, deniedController) = Create(controlEnabled: false);
        var (allowed, allowedController) = Create(controlEnabled: true);

        denied.ProcessInputMessage("""{"Type":0,"X":0.1,"Y":0.1}""");
        allowed.ProcessInputMessage("""{"Type":0,"X":0.1,"Y":0.1}""");

        deniedController.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
        allowedController.Received(1).ApplyInput(Arg.Any<InputMessage>());
    }

    [Fact]
    public void Clipboard_IsRejected_WhileClipboardNotGranted_EvenWithControl()
    {
        var (manager, _) = Create(controlEnabled: true);
        var raised = false;
        manager.ClipboardMessageReceived += (_, _) => raised = true;

        manager.ProcessInputMessage(ClipboardJson);

        Assert.False(raised);
    }

    [Fact]
    public void Clipboard_IsDelivered_WhenClipboardGranted()
    {
        var (manager, _) = Create(controlEnabled: false);
        manager.HostGrantedCapabilities = SessionCapabilities.Clipboard;
        var raised = false;
        manager.ClipboardMessageReceived += (_, _) => raised = true;

        manager.ProcessInputMessage(ClipboardJson);

        Assert.True(raised);
    }

    [Fact]
    public void Clipboard_WithoutData_NeverFallsThroughToInput()
    {
        // The desktop viewer sends {"type":"clipboard","text":...}: it used to be parsed as an input message.
        var (manager, controller) = Create(controlEnabled: true);
        manager.HostGrantedCapabilities = SessionCapabilities.Clipboard;

        manager.ProcessInputMessage("""{"type":"clipboard","text":"hi"}""");

        controller.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
    }

    [Theory]
    [InlineData("""{"type":"file_transfer"}""")]
    [InlineData("""{"type":"MouseMove","X":0.5,"Y":0.5}""")] // lowercase key is not the input format
    [InlineData("""{"Type":"Nope"}""")]
    [InlineData("""{"Type":"1"}""")]
    [InlineData("""{"Type":99}""")]
    [InlineData("""{"type":5}""")]
    [InlineData("""{"X":0.5}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void UnknownMessages_AreRejected(string json)
    {
        var (manager, controller) = Create(controlEnabled: true);
        manager.HostGrantedCapabilities = SessionCapabilities.Clipboard | SessionCapabilities.FileTransfer;
        var raised = false;
        manager.ClipboardMessageReceived += (_, _) => raised = true;

        manager.ProcessInputMessage(json);

        controller.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
        Assert.False(raised);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    public void MalformedJson_IsRejected_WithoutThrowing(string json)
    {
        var (manager, controller) = Create(controlEnabled: true);

        manager.ProcessInputMessage(json);

        controller.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
    }

    [Fact]
    public void OversizeMessage_IsRejected()
    {
        var (manager, controller) = Create(controlEnabled: true);
        var json = $$"""{"Type":"MouseMove","X":0.5,"Pad":"{{new string('a', DataChannelPolicy.MaxMessageBytes)}}"}""";

        manager.ProcessInputMessage(json);

        controller.DidNotReceive().ApplyInput(Arg.Any<InputMessage>());
    }

    [Theory]
    [InlineData(SessionCapabilities.View, "authorization_request")]
    [InlineData(SessionCapabilities.Clipboard, "clipboard")]
    [InlineData(SessionCapabilities.Control, "MouseMove")]
    public void RequiredCapability_MatchesTable(SessionCapabilities expected, string type)
    {
        var json = type == "MouseMove" ? $$"""{"Type":"{{type}}"}""" : $$"""{"type":"{{type}}"}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(DataChannelPolicy.TryGetRequiredCapability(doc.RootElement, out _, out var required));
        Assert.Equal(expected, required);
    }

    [Fact]
    public void ViewOnlySession_GrantsOnlyView()
    {
        var manager = new DataChannelManager(Substitute.For<ILogger>());

        Assert.Equal(SessionCapabilities.View, manager.GrantedCapabilities);
    }

    [Fact]
    public void RejectionLogging_IsCappedPerType_AndNeverContainsContent()
    {
        var logger = Substitute.For<ILogger>();
        var manager = new DataChannelManager(logger, null);
        manager.ClipboardMessageReceived += (_, _) => { };

        for (var i = 0; i < 100; i++)
        {
            manager.ProcessInputMessage(ClipboardJson);
        }

        // 5 detailed lines + 1 "will not be logged" line, not 100 (Serilog overloads differ, so count by name).
        var warnings = logger.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Warning").ToList();
        Assert.Equal(6, warnings.Count);
        Assert.DoesNotContain(warnings, c => c.GetArguments().Any(a => a?.ToString()?.Contains("secret") == true));
    }
}
