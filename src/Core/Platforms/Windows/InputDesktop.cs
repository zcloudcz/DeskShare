using System.Runtime.InteropServices;
using Serilog;

namespace DeskShare.Core.Platforms.Windows;

/// <summary>
/// Follows the desktop that currently receives input. The UAC prompt, lock screen and sign-in screen live on the
/// separate "Winlogon" desktop; a SYSTEM process in the user's session may open it, a normal app may not
/// (OpenInputDesktop then fails and everything stays as it was).
/// </summary>
/// <remarks>
/// Desktops are per thread, and the capture/input code runs on arbitrary thread-pool threads. So instead of moving a
/// thread for good, callers wrap each unit of work in <see cref="Enter"/> and the thread goes back to its own desktop afterwards.
/// </remarks>
internal sealed class InputDesktop : IDisposable
{
    private static readonly string? Home = NameOf(DesktopNative.GetThreadDesktop(DesktopNative.GetCurrentThreadId()));

    private readonly DesktopChangeTracker _tracker = new();
    private IntPtr _handle; // desktop to attach to; zero = the process's own desktop is already the input desktop
    private bool _attachFailureLogged;

    public string? Name => _tracker.Current;

    /// <summary>Re-reads the input desktop. Cheap (open, read name, close); returns true when it changed.</summary>
    public bool Refresh()
    {
        var h = DesktopNative.OpenInputDesktop(0, false, DesktopNative.GenericAll);
        if (h == IntPtr.Zero)
            return false; // off limits (normal app on Winlogon) or mid-switch: keep what we have, no log spam

        var name = NameOf(h);
        if (!_tracker.Observe(name))
        {
            DesktopNative.CloseDesktop(h);
            return false;
        }

        var old = _handle;
        if (DesktopRules.NeedsAttach(name, Home))
        {
            _handle = h;
        }
        else
        {
            _handle = IntPtr.Zero;
            DesktopNative.CloseDesktop(h);
        }
        // Fails (ignored) while some thread is still attached to it; threads restore in Scope.Dispose so it normally works.
        if (old != IntPtr.Zero)
            DesktopNative.CloseDesktop(old);

        Log.Information("Input desktop is now {Desktop}", name);
        return true;
    }

    /// <summary>Attaches the calling thread to the input desktop until the returned scope is disposed.</summary>
    public Scope Enter()
    {
        if (_handle == IntPtr.Zero)
            return default;

        var previous = DesktopNative.GetThreadDesktop(DesktopNative.GetCurrentThreadId());
        if (previous == _handle)
            return default;

        if (!DesktopNative.SetThreadDesktop(_handle))
        {
            // Typically the thread owns a window or hook. Log once, then carry on on the old desktop.
            if (!_attachFailureLogged)
            {
                _attachFailureLogged = true;
                Log.Warning("SetThreadDesktop({Desktop}) failed, Win32 error {Error}; capture/input stay on the previous desktop",
                    Name, Marshal.GetLastWin32Error());
            }
            return default;
        }
        return new Scope(previous);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            DesktopNative.CloseDesktop(_handle);
        _handle = IntPtr.Zero;
    }

    public readonly struct Scope(IntPtr previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous != IntPtr.Zero)
                DesktopNative.SetThreadDesktop(previous);
        }
    }

    private static unsafe string? NameOf(IntPtr desktop)
    {
        if (desktop == IntPtr.Zero)
            return null;
        char* buffer = stackalloc char[256];
        return DesktopNative.GetUserObjectInformationW(desktop, DesktopNative.UoiName, buffer, 256 * sizeof(char), out _)
            ? new string(buffer)
            : null;
    }
}

internal static unsafe class DesktopNative
{
    public const uint GenericAll = 0x10000000;
    public const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetUserObjectInformationW(IntPtr obj, int index, char* info, uint length, out uint needed);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // GDI fallback capture
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hDc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GdiFlush();
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int srcX, int srcY, uint rop);

    public const uint SrcCopy = 0x00CC0020;
    public const uint CaptureBlt = 0x40000000; // also capture layered windows

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }
}
