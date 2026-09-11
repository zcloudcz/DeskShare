using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DeskShare.Core.Interfaces;

namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// Linux implementation of window manager using X11 API.
/// Provides window enumeration and window-specific operations for X11-based systems.
/// </summary>
/// <remarks>
/// This implementation uses X11 window management APIs to enumerate and query windows.
/// For Wayland systems, a different implementation would be needed.
///
/// X11 window hierarchy:
/// - Root window: The entire screen
/// - Top-level windows: Application windows
/// - Child windows: UI elements within applications
///
/// Dependencies:
/// - libX11.so (X11 library)
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class X11WindowManager : IWindowManager
{
    private IntPtr _display = IntPtr.Zero;
    private bool _disposed;

    // X11 native methods
    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryTree(IntPtr display, IntPtr window,
        out IntPtr root_return, out IntPtr parent_return,
        out IntPtr children_return, out uint nchildren_return);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);

    [DllImport("libX11.so.6")]
    private static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr window_name_return);

    [DllImport("libX11.so.6")]
    private static extern int XGetGeometry(IntPtr display, IntPtr drawable,
        out IntPtr root_return, out int x_return, out int y_return,
        out uint width_return, out uint height_return,
        out uint border_width_return, out uint depth_return);

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int x, y;
        public int width, height;
        public int border_width;
        public int depth;
        public IntPtr visual;
        public IntPtr root;
        public int c_class;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public ulong backing_planes;
        public ulong backing_pixel;
        public bool save_under;
        public IntPtr colormap;
        public bool map_installed;
        public int map_state;
        public long all_event_masks;
        public long your_event_mask;
        public long do_not_propagate_mask;
        public bool override_redirect;
        public IntPtr screen;
    }

    // X11 map state constants
    private const int IsUnmapped = 0;
    private const int IsUnviewable = 1;
    private const int IsViewable = 2;

    public X11WindowManager()
    {
        _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to open X display");
        }
    }

    public List<Interfaces.WindowInfo> EnumerateWindows()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(X11WindowManager));

        var windows = new List<Interfaces.WindowInfo>();
        IntPtr rootWindow = XDefaultRootWindow(_display);

        EnumerateWindowsRecursive(rootWindow, windows, 0);

        return windows;
    }

    private void EnumerateWindowsRecursive(IntPtr window, List<Interfaces.WindowInfo> windows, int depth)
    {
        // Don't go too deep (avoid infinite recursion)
        if (depth > 10)
            return;

        // Query window children
        int result = XQueryTree(_display, window,
            out IntPtr root, out IntPtr parent,
            out IntPtr childrenPtr, out uint nChildren);

        if (result == 0 || childrenPtr == IntPtr.Zero)
            return;

        try
        {
            // Get array of child windows
            IntPtr[] children = new IntPtr[nChildren];
            for (int i = 0; i < nChildren; i++)
            {
                children[i] = Marshal.ReadIntPtr(childrenPtr, i * IntPtr.Size);
            }

            // Process each child
            foreach (var child in children)
            {
                // Check if window is viewable (visible)
                if (XGetWindowAttributes(_display, child, out var attrs) != 0)
                {
                    // Only include viewable top-level windows
                    if (attrs.map_state == IsViewable && depth <= 1)
                    {
                        string? title = GetWindowTitle(child);

                        // Skip windows without titles (usually not user windows)
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            var bounds = GetWindowBounds(child);
                            if (bounds != null)
                            {
                                windows.Add(new Interfaces.WindowInfo
                                {
                                    Handle = child,
                                    Title = title,
                                    Bounds = bounds,
                                    IsVisible = true
                                });
                            }
                        }
                    }
                }

                // Recurse to children
                EnumerateWindowsRecursive(child, windows, depth + 1);
            }
        }
        finally
        {
            XFree(childrenPtr);
        }
    }

    public string? GetWindowTitle(IntPtr windowHandle)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(X11WindowManager));

        if (windowHandle == IntPtr.Zero)
            return null;

        if (XFetchName(_display, windowHandle, out IntPtr namePtr) == 0 || namePtr == IntPtr.Zero)
            return null;

        try
        {
            string? name = Marshal.PtrToStringAnsi(namePtr);
            return name;
        }
        finally
        {
            XFree(namePtr);
        }
    }

    public bool IsWindowValid(IntPtr windowHandle)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(X11WindowManager));

        if (windowHandle == IntPtr.Zero)
            return false;

        if (XGetWindowAttributes(_display, windowHandle, out var attrs) == 0)
            return false;

        return attrs.map_state == IsViewable;
    }

    public Interfaces.WindowBounds? GetWindowBounds(IntPtr windowHandle)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(X11WindowManager));

        if (windowHandle == IntPtr.Zero)
            return null;

        int result = XGetGeometry(_display, windowHandle,
            out IntPtr root, out int x, out int y,
            out uint width, out uint height,
            out uint borderWidth, out uint depth);

        if (result == 0)
            return null;

        return new Interfaces.WindowBounds
        {
            X = x,
            Y = y,
            Width = (int)width,
            Height = (int)height
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_display != IntPtr.Zero)
        {
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }

        _disposed = true;
    }
}
