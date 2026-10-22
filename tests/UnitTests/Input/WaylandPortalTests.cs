#pragma warning disable CA1416 // Only pure helpers are called; they run on any OS.
using DeskShare.Core.Models;
using DeskShare.Core.Platforms.Linux;

namespace DeskShare.UnitTests.Input;

/// <summary>
/// Pure parts of the Wayland support: session detection, portal request paths, restore-token persistence and
/// the input mappings. The D-Bus/PipeWire path itself runs in the Wayland Docker E2E (docker-compose.e2e-wayland.yml).
/// </summary>
public sealed class WaylandPortalTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Theory]
    [InlineData("wayland", ":0", "wayland-0", true)]   // GNOME/KDE Wayland session with XWayland
    [InlineData("x11", ":0", "wayland-0", false)]      // explicit X11 session wins
    [InlineData("X11", null, null, false)]
    [InlineData(null, null, "wayland-1", true)]         // bare compositor without logind session type
    [InlineData(null, ":1", "wayland-1", false)]        // usable DISPLAY and no session type: keep X11
    [InlineData(null, ":0", null, false)]
    [InlineData("tty", null, null, false)]
    public void IsWaylandSession_FollowsSessionTypeThenDisplays(string? sessionType, string? display, string? waylandDisplay, bool expected)
    {
        var env = new List<(string, string)>();
        if (sessionType != null) env.Add(("XDG_SESSION_TYPE", sessionType));
        if (display != null) env.Add(("DISPLAY", display));
        if (waylandDisplay != null) env.Add(("WAYLAND_DISPLAY", waylandDisplay));

        Assert.Equal(expected, WaylandPortalSession.IsWaylandSession(Env(env.ToArray())));
    }

    [Fact]
    public void RequestPath_UsesSenderNameWithoutColonAndDots()
    {
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42/deskshare7_1",
            WaylandPortalSession.RequestPath(":1.42", "deskshare7_1"));
    }

    [Fact]
    public void RestoreToken_RoundTripsAndIsRemovedWhenThePortalReturnsNone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskshare-wayland-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "wayland-restore-token");
        try
        {
            Assert.Null(WaylandPortalSession.ReadRestoreToken(path)); // first run: nothing stored

            WaylandPortalSession.WriteRestoreToken(path, "3f1e-token");
            Assert.Equal("3f1e-token", WaylandPortalSession.ReadRestoreToken(path));

            // Tokens are single-use: no new token must not leave the consumed one behind.
            WaylandPortalSession.WriteRestoreToken(path, null);
            Assert.False(File.Exists(path));
            Assert.Null(WaylandPortalSession.ReadRestoreToken(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, 0.0)]
    [InlineData(0.25, 0.75, 320.0, 540.0)]
    [InlineData(1.0, 1.0, 1279.0, 719.0)] // clamped onto the last pixel, still inside the stream
    public void ToStreamCoordinates_ScalesNormalizedToStreamSize(double x, double y, double expectedX, double expectedY)
    {
        Assert.Equal((expectedX, expectedY), WaylandPortalInputController.ToStreamCoordinates(x, y, 1280, 720));
    }

    [Theory]
    [InlineData(120, -1)]  // wheel up = negative discrete step
    [InlineData(-240, 2)]
    [InlineData(60, 0)]    // less than a notch
    public void ToScrollSteps_ConvertsWheelNotches(int delta, int expected)
    {
        Assert.Equal(expected, WaylandPortalInputController.ToScrollSteps(delta));
    }

    [Theory]
    [InlineData(MouseButton.Left, 0x110)]
    [InlineData(MouseButton.Right, 0x111)]
    [InlineData(MouseButton.Middle, 0x112)]
    [InlineData(MouseButton.Extra1, 0x113)]
    [InlineData(MouseButton.Extra2, 0x114)]
    public void ToEvdevButton_UsesLinuxButtonCodes(MouseButton button, int expected)
    {
        Assert.Equal(expected, WaylandPortalInputController.ToEvdevButton(button));
    }

    [Theory]
    [InlineData('A', 30)]   // KEY_A
    [InlineData('Q', 16)]   // KEY_Q
    [InlineData('Z', 44)]   // KEY_Z
    [InlineData('M', 50)]   // KEY_M
    [InlineData('1', 2)]    // KEY_1
    [InlineData('9', 10)]   // KEY_9
    [InlineData('0', 11)]   // KEY_0
    [InlineData(0x0D, 28)]  // Enter
    [InlineData(0x1B, 1)]   // Escape
    [InlineData(0x25, 105)] // Left arrow
    [InlineData(0x70, 59)]  // F1
    [InlineData(0x79, 68)]  // F10
    [InlineData(0x7B, 88)]  // F12
    [InlineData(0x87, 194)] // F24
    [InlineData(0xA5, 100)] // Right Alt
    [InlineData(0xBA, 39)]  // ; on US layout
    [InlineData(0x60, 82)]  // Numpad 0
    [InlineData(0x67, 71)]  // Numpad 7
    public void EvdevKeyMap_MapsVirtualKeys(int virtualKey, int expected)
    {
        Assert.Equal(expected, EvdevKeyMap.FromVirtualKey(virtualKey));
    }

    [Fact]
    public void EvdevKeyMap_UnknownKey_IsNull()
    {
        Assert.Null(EvdevKeyMap.FromVirtualKey(0xFF));
    }

    [Fact]
    public void EvdevKeyMap_EveryLetterHasADistinctCode()
    {
        var codes = Enumerable.Range('A', 26).Select(vk => EvdevKeyMap.FromVirtualKey(vk)).ToList();
        Assert.DoesNotContain(null, codes);
        Assert.Equal(26, codes.Distinct().Count());
    }
}
