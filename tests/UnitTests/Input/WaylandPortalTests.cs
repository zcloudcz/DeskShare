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

    [Theory]
    [InlineData('A', 38u)]  // X keycode = evdev + 8 (KEY_A 30)
    [InlineData('B', 56u)]  // the old offset mapping sent 39 ('s') here
    [InlineData('H', 43u)]
    [InlineData('1', 10u)]
    [InlineData(0x10, 50u)] // Shift -> Shift_L
    [InlineData(0x0D, 36u)] // Return
    public void XTest_ConvertVirtualKeyToXKeycode_UsesEvdevPlus8(int virtualKey, uint expected)
    {
        Assert.Equal(expected, XTestInputController.ConvertVirtualKeyToXKeycode(virtualKey));
    }

    [Fact]
    public void XTest_ConvertVirtualKeyToXKeycode_UnknownKey_IsNull()
    {
        Assert.Null(XTestInputController.ConvertVirtualKeyToXKeycode(0xFF));
    }

    /// <summary>Linux/macOS only: symlinks and file modes are what these tests are about.</summary>
    public sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Unix file modes and symlinks (runs in the Wayland Docker E2E image)";
        }
    }

    private static string NewTokenDir() => Path.Combine(Path.GetTempPath(), "deskshare-token-" + Guid.NewGuid().ToString("N"), "DeskShare");

    [UnixFact]
    public void RestoreToken_IsWrittenAs0600_InA0700Directory()
    {
        var dir = NewTokenDir();
        var path = Path.Combine(dir, "wayland-restore-token");
        try
        {
            Directory.CreateDirectory(dir); // default 0755, like the folder other sender files create
            WaylandPortalSession.WriteRestoreToken(path, "token-1");

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
            Assert.Equal("token-1", WaylandPortalSession.ReadRestoreToken(path));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [UnixFact]
    public void RestoreToken_SymlinkAtTheTokenPath_IsReplacedNotFollowed_AndNotRead()
    {
        var dir = NewTokenDir();
        var path = Path.Combine(dir, "wayland-restore-token");
        var victim = Path.Combine(Path.GetDirectoryName(dir)!, "victim");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(victim, "precious");
            File.CreateSymbolicLink(path, victim);

            Assert.Null(WaylandPortalSession.ReadRestoreToken(path)); // a planted link is not trusted

            WaylandPortalSession.WriteRestoreToken(path, "token-2");
            Assert.Equal("precious", File.ReadAllText(victim));      // target untouched
            Assert.Null(new FileInfo(path).LinkTarget);               // the link itself was replaced
            Assert.Equal("token-2", WaylandPortalSession.ReadRestoreToken(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [UnixFact]
    public void RestoreToken_ReadableByOthers_IsIgnored()
    {
        var dir = NewTokenDir();
        var path = Path.Combine(dir, "wayland-restore-token");
        try
        {
            WaylandPortalSession.WriteRestoreToken(path, "token-3");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            Assert.Null(WaylandPortalSession.ReadRestoreToken(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [UnixFact]
    public void RestoreToken_DirectoryIsASymlink_NothingIsWritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "deskshare-token-" + Guid.NewGuid().ToString("N"));
        var elsewhere = Path.Combine(root, "elsewhere");
        var dir = Path.Combine(root, "DeskShare");
        try
        {
            Directory.CreateDirectory(elsewhere);
            Directory.CreateSymbolicLink(dir, elsewhere);

            WaylandPortalSession.WriteRestoreToken(Path.Combine(dir, "wayland-restore-token"), "token-4");

            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
