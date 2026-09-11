using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DeskShare.Core.Interfaces;

namespace DeskShare.Core.Platforms.Windows;

/// <summary>
/// Windows implementation of window manager using Win32 API.
/// Provides window enumeration and window-specific operations.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32WindowManager : IWindowManager
{
    private bool _disposed;

    // Win32 API imports
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public List<Interfaces.WindowInfo> EnumerateWindows()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Win32WindowManager));

        var windows = new List<Interfaces.WindowInfo>();

        bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
        {
            // Skip invisible windows
            if (!IsWindowVisible(hWnd))
                return true;

            // Skip minimized windows
            if (IsIconic(hWnd))
                return true;

            // Get window title
            string? title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            // Get window bounds
            var bounds = GetWindowBounds(hWnd);
            if (bounds == null)
                return true;

            windows.Add(new Interfaces.WindowInfo
            {
                Handle = hWnd,
                Title = title,
                Bounds = bounds,
                IsVisible = true
            });

            return true;
        }

        EnumWindows(EnumWindowCallback, IntPtr.Zero);
        return windows;
    }

    public string? GetWindowTitle(IntPtr windowHandle)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Win32WindowManager));

        if (windowHandle == IntPtr.Zero)
            return null;

        int length = GetWindowTextLength(windowHandle);
        if (length == 0)
            return null;

        var builder = new StringBuilder(length + 1);
        int result = GetWindowText(windowHandle, builder, builder.Capacity);

        return result > 0 ? builder.ToString() : null;
    }

    public bool IsWindowValid(IntPtr windowHandle)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Win32WindowManager));

        if (windowHandle == IntPtr.Zero)
            return false;

        return IsWindowVisible(windowHandle) && !IsIconic(windowHandle);
    }

    public Interfaces.WindowBounds? GetWindowBounds(IntPtr windowHandle)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Win32WindowManager));

        if (windowHandle == IntPtr.Zero)
            return null;

        if (!GetWindowRect(windowHandle, out RECT rect))
            return null;

        return new Interfaces.WindowBounds
        {
            X = rect.Left,
            Y = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}
