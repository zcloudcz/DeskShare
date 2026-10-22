using DeskShare.Core.Displays;
using DeskShare.Core.Interfaces;
using DeskShare.Core.WebRTC;
using NSubstitute;
using Serilog;

namespace DeskShare.UnitTests.Displays;

/// <summary>"switch_display" on the sender's data channel: only a session with Control reaches the switch.</summary>
public sealed class DisplaySwitchManagerTests
{
    private static readonly DisplayInfo[] TwoDisplays =
    {
        new(0, "Display 1 - 2880x1800 (primary)", 2880, 1800, true),
        new(1, "Display 2 - 1920x1080", 1920, 1080, false, Left: 2880, Top: 0, OutputIndex: 1),
    };

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Manager_SwitchesOnlyWithControl(bool controlEnabled, int expectedCalls)
    {
        var controller = Substitute.For<IInputController>();
        controller.IsEnabled.Returns(controlEnabled);
        var manager = new DataChannelManager(Substitute.For<ILogger>(), controller);
        var calls = new TaskCompletionSource<DisplayInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        manager.DisplaySwitcher = new DisplaySwitchService(TwoDisplays, 0, d => { Interlocked.Increment(ref count); calls.TrySetResult(d); return Task.FromResult(true); });

        manager.ProcessInputMessage("""{"type":"switch_display","index":1}""");

        if (expectedCalls > 0)
            Assert.Equal(1, (await calls.Task.WaitAsync(TimeSpan.FromSeconds(5))).Index);
        else
            await Task.Delay(100); // give a wrongly started switch the chance to show up
        Assert.Equal(expectedCalls, count);
    }

    [Fact]
    public async Task Manager_IgnoresAnInvalidIndex()
    {
        var controller = Substitute.For<IInputController>();
        controller.IsEnabled.Returns(true);
        var manager = new DataChannelManager(Substitute.For<ILogger>(), controller);
        var count = 0;
        manager.DisplaySwitcher = new DisplaySwitchService(TwoDisplays, 0, _ => { Interlocked.Increment(ref count); return Task.FromResult(true); });

        manager.ProcessInputMessage("""{"type":"switch_display","index":7}""");
        manager.ProcessInputMessage("""{"type":"switch_display","index":"1"}""");
        manager.ProcessInputMessage("""{"type":"switch_display"}""");
        await Task.Delay(100);

        Assert.Equal(0, count);
    }
}
