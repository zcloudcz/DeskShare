using System.Text.Json;
using DeskShare.Core.Displays;
using DeskShare.Core.WebRTC;

namespace DeskShare.UnitTests.Displays;

/// <summary>Display list message, switch policy/validation/rate limit and the coordinate mapping for non-primary displays.</summary>
public sealed class DisplaySwitchTests
{
    private static readonly DisplayInfo[] TwoDisplays =
    {
        new(0, "Display 1 - 2880x1800 (primary)", 2880, 1800, true),
        new(1, "Display 2 - 1920x1080", 1920, 1080, false, Left: 2880, Top: 0, OutputIndex: 1),
    };

    private sealed class Clock
    {
        public long Now;
        public long Read() => Now;
    }

    private static DisplaySwitchService Create(Func<DisplayInfo, Task<bool>> apply, Clock? clock = null, int current = 0) =>
        new(TwoDisplays, current, apply, (clock ?? new Clock()).Read);

    // ---- message ----

    [Fact]
    public void BuildMessage_ListsDisplaysAndCurrent()
    {
        var service = Create(_ => Task.FromResult(true), current: 1);

        using var doc = JsonDocument.Parse(service.BuildMessage());
        var root = doc.RootElement;

        Assert.Equal("displays", root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("current").GetInt32());
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(0, items[0].GetProperty("index").GetInt32());
        Assert.Equal("Display 1 - 2880x1800 (primary)", items[0].GetProperty("name").GetString());
        Assert.Equal(2880, items[0].GetProperty("width").GetInt32());
        Assert.Equal(1800, items[0].GetProperty("height").GetInt32());
        Assert.True(items[0].GetProperty("primary").GetBoolean());
        Assert.False(items[1].GetProperty("primary").GetBoolean());
        // Host-only fields must not leak to the viewer.
        Assert.False(items[1].TryGetProperty("left", out _));
    }

    [Fact]
    public void FormatName_NumbersFromOne_AndMarksPrimary()
    {
        Assert.Equal("Display 1 - 2880x1800 (primary)", DisplayInfo.FormatName(0, 2880, 1800, true));
        Assert.Equal("Display 3 - 1920x1080", DisplayInfo.FormatName(2, 1920, 1080, false));
    }

    // ---- policy ----

    [Theory]
    [InlineData("""{"type":"switch_display","index":1}""", true)]
    [InlineData("""{"type":"switch_displays","index":1}""", false)]
    public void Policy_SwitchDisplay_NeedsControl(string json, bool known)
    {
        using var doc = JsonDocument.Parse(json);

        var found = DataChannelPolicy.TryGetRequiredCapability(doc.RootElement, out _, out var required);

        Assert.Equal(known, found);
        if (known)
            Assert.Equal(DeskShare.Core.Models.SessionCapabilities.Control, required);
    }

    // ---- validation ----

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public async Task Switch_RejectsIndexOutsideTheList(int index)
    {
        var called = false;
        var service = Create(_ => { called = true; return Task.FromResult(true); });

        Assert.Equal(DisplaySwitchResult.InvalidIndex, await service.SwitchAsync(index));
        Assert.False(called);
        Assert.Equal(0, service.Current);
    }

    [Theory]
    [InlineData("""{"index":1}""", true, 1)]
    [InlineData("""{"index":1.5}""", false, 0)]
    [InlineData("""{"index":"1"}""", false, 0)]
    [InlineData("""{"index":99999999999}""", false, 0)]
    [InlineData("""{}""", false, 0)]
    public void TryReadIndex_AcceptsIntegersOnly(string json, bool ok, int expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(ok, DisplaySwitchService.TryReadIndex(doc.RootElement, out var index));
        if (ok)
            Assert.Equal(expected, index);
    }

    [Fact]
    public async Task Switch_ToTheCurrentDisplay_DoesNothing()
    {
        var called = false;
        var service = Create(_ => { called = true; return Task.FromResult(true); });

        Assert.Equal(DisplaySwitchResult.Unchanged, await service.SwitchAsync(0));
        Assert.False(called);
    }

    // ---- rate limit ----

    [Fact]
    public async Task Switch_IsLimitedToOnePerSecond()
    {
        var clock = new Clock { Now = 10_000 };
        var service = Create(_ => Task.FromResult(true), clock);

        Assert.Equal(DisplaySwitchResult.Switched, await service.SwitchAsync(1));
        Assert.Equal(1, service.Current);

        clock.Now += DisplaySwitchService.MinSwitchIntervalMs - 1;
        Assert.Equal(DisplaySwitchResult.RateLimited, await service.SwitchAsync(0));
        Assert.Equal(1, service.Current);

        clock.Now += 1;
        Assert.Equal(DisplaySwitchResult.Switched, await service.SwitchAsync(0));
        Assert.Equal(0, service.Current);
    }

    [Fact]
    public async Task Switch_FailedAttempt_KeepsCurrent_AndStillCountsForTheLimit()
    {
        var clock = new Clock { Now = 5_000 };
        var service = Create(_ => Task.FromResult(false), clock);

        Assert.Equal(DisplaySwitchResult.Failed, await service.SwitchAsync(1));
        Assert.Equal(0, service.Current);
        Assert.Equal(DisplaySwitchResult.RateLimited, await service.SwitchAsync(1));
    }

    [Fact]
    public async Task Switch_WhileAnotherIsRunning_IsDropped()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Create(_ => release.Task);

        var first = service.SwitchAsync(1);
        Assert.Equal(DisplaySwitchResult.RateLimited, await service.SwitchAsync(1));

        release.SetResult(true);
        Assert.Equal(DisplaySwitchResult.Switched, await first);
    }

    // ---- coordinate mapping ----

    private static readonly ScreenRect Primary = new(0, 0, 1920, 1080);

    [Fact]
    public void Mapping_PrimaryOnly_SpansTheWholeRange()
    {
        Assert.Equal((0, 0), DisplayMapping.ToVirtualDesk(0, 0, Primary, Primary));
        Assert.Equal((65535, 65535), DisplayMapping.ToVirtualDesk(1, 1, Primary, Primary));
        var (x, y) = DisplayMapping.ToVirtualDesk(0.5, 0.5, Primary, Primary);
        Assert.InRange(x, 32767 - 40, 32768 + 40); // within one pixel of the middle
        Assert.InRange(y, 32767 - 70, 32768 + 70);
    }

    [Fact]
    public void Mapping_SecondaryRightOfPrimary_LandsOnItsOwnPixels()
    {
        var secondary = new ScreenRect(1920, 0, 1280, 1024);
        var virtualScreen = new ScreenRect(0, 0, 3200, 1080);

        // Left edge of the secondary = pixel 1920 of 3200.
        Assert.Equal((Expected(1920, 3200), 0), DisplayMapping.ToVirtualDesk(0, 0, secondary, virtualScreen));
        // Right edge = its last pixel (3199), the end of the virtual screen.
        Assert.Equal((65535, Expected(1023, 1080)), DisplayMapping.ToVirtualDesk(1, 1, secondary, virtualScreen));
    }

    [Fact]
    public void Mapping_SecondaryLeftOfPrimary_NegativeCoordinates()
    {
        var secondary = new ScreenRect(-1280, 0, 1280, 1024);
        var virtualScreen = new ScreenRect(-1280, 0, 3200, 1080);

        Assert.Equal((0, 0), DisplayMapping.ToVirtualDesk(0, 0, secondary, virtualScreen));
        // Its last pixel is x = -1, i.e. offset 1279 inside the virtual screen.
        Assert.Equal((Expected(1279, 3200), Expected(1023, 1080)), DisplayMapping.ToVirtualDesk(1, 1, secondary, virtualScreen));
    }

    [Fact]
    public void Mapping_SecondaryAbovePrimary_NegativeTop()
    {
        var secondary = new ScreenRect(0, -1080, 1920, 1080);
        var virtualScreen = new ScreenRect(0, -1080, 1920, 2160);

        Assert.Equal((0, 0), DisplayMapping.ToVirtualDesk(0, 0, secondary, virtualScreen));
        Assert.Equal((65535, Expected(1079, 2160)), DisplayMapping.ToVirtualDesk(1, 1, secondary, virtualScreen));
    }

    [Fact]
    public void Mapping_ClampsOutOfRangeInput()
    {
        var secondary = new ScreenRect(1920, 0, 1280, 1024);
        var virtualScreen = new ScreenRect(0, 0, 3200, 1080);

        Assert.Equal(DisplayMapping.ToVirtualDesk(0, 0, secondary, virtualScreen), DisplayMapping.ToVirtualDesk(-3, -3, secondary, virtualScreen));
        Assert.Equal(DisplayMapping.ToVirtualDesk(1, 1, secondary, virtualScreen), DisplayMapping.ToVirtualDesk(7, 7, secondary, virtualScreen));
    }

    // The documented SendInput mapping: pixel offset scaled so that the last pixel of the virtual screen is 65535.
    private static int Expected(int offset, int extent) => (int)Math.Round(offset * 65535.0 / (extent - 1));

    // ---- configured default ----

    [Fact]
    public void FindConfigured_MatchesAdapterAndOutput_ElseFirst()
    {
        var displays = new[]
        {
            new DisplayInfo(0, "a", 1, 1, true, AdapterIndex: 0, OutputIndex: 0),
            new DisplayInfo(1, "b", 1, 1, false, AdapterIndex: 0, OutputIndex: 1),
            new DisplayInfo(2, "c", 1, 1, false, AdapterIndex: 1, OutputIndex: 0),
        };

        Assert.Equal(2, DisplayCatalog.FindConfigured(displays, 1, 0));
        Assert.Equal(1, DisplayCatalog.FindConfigured(displays, 0, 1));
        Assert.Equal(0, DisplayCatalog.FindConfigured(displays, 5, 5));
    }

    [Fact]
    public void Enumerate_NeverReturnsEmpty()
    {
        var displays = DisplayCatalog.Enumerate(1280, 720);

        Assert.NotEmpty(displays);
        Assert.All(displays.Select((d, i) => (d, i)), x => Assert.Equal(x.i, x.d.Index));
    }
}
