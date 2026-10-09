using System.Text.Json;
using System.Text.Json.Serialization;
using DeskShare.Core.Models;

namespace DeskShare.UnitTests.WebRTC;

/// <summary>
/// The sender must understand input from both viewers: the browser (remote-control.js) sends enum names,
/// the desktop viewer serializes enums as numbers. Same options as DataChannelManager uses.
/// </summary>
public sealed class InputMessageJsonTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void BrowserMessage_WithEnumNames_Deserializes()
    {
        var json = """{"Type":"MouseDown","X":0.5,"Y":0.25,"Button":"Right","Timestamp":"2026-10-09T18:00:00.000Z"}""";

        var message = JsonSerializer.Deserialize<InputMessage>(json, Options)!;

        Assert.Equal(InputMessageType.MouseDown, message.Type);
        Assert.Equal(MouseButton.Right, message.Button);
        Assert.Equal(0.5, message.X);
    }

    [Fact]
    public void DesktopMessage_WithNumericEnums_StillDeserializes()
    {
        var json = JsonSerializer.Serialize(new InputMessage { Type = InputMessageType.MouseMove, X = 0.1, Y = 0.2 });

        var message = JsonSerializer.Deserialize<InputMessage>(json, Options)!;

        Assert.Equal(InputMessageType.MouseMove, message.Type);
    }
}
