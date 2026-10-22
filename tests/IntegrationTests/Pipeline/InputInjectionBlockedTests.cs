using System.Runtime.InteropServices;
using DeskShare.Core.Models;
using DeskShare.Core.Platforms.Windows;
using Serilog;

namespace DeskShare.IntegrationTests.Pipeline;

/// <summary>
/// The viewer's input must keep working while the local user's is blocked (BlockInput). Moves the real cursor, so it
/// runs only on an interactive Windows desktop; the blocked part additionally needs an elevated process
/// (BlockInput fails otherwise). Without those it returns without asserting.
/// </summary>
public sealed class InputInjectionBlockedTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [Fact]
    public async Task ControllerInjection_WorksWhileLocalInputIsBlocked()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            return;

        using var controller = new WindowsInputController(new LoggerConfiguration().CreateLogger());
        controller.AuthorizationRequested = _ => true;
        Assert.True(await controller.RequestAuthorizationAsync("test"));

        var blocked = controller.SetLocalInputBlocked(true);
        try
        {
            // Two targets far apart, so a lost move cannot pass by luck. Coordinates are normalized over the primary monitor.
            foreach (var (x, y) in new[] { (0.25, 0.25), (0.75, 0.75) })
            {
                SetCursorPos(0, 0);
                Assert.True(controller.ApplyInput(new InputMessage { Type = InputMessageType.MouseMove, X = x, Y = y, Timestamp = DateTime.UtcNow }));
                await Task.Delay(100);
                GetCursorPos(out var p);
                // The controller maps to the primary monitor (no VIRTUALDESK), so compare with its size.
                var width = GetSystemMetrics(0); // SM_CXSCREEN
                Assert.True(p.X > width * (x - 0.1), $"cursor did not move (blocked={blocked}, x={p.X})");
            }
        }
        finally
        {
            controller.SetLocalInputBlocked(false);
        }
    }
}
