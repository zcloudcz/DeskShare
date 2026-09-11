using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// Linux implementation of screen capture using X11 XShmGetImage.
/// Provides screen capture functionality for X11-based Linux systems.
/// </summary>
/// <remarks>
/// This implementation uses X11 (X Window System) APIs to capture the screen.
/// For Wayland systems, a different implementation using PipeWire would be needed.
///
/// X11 capture process:
/// 1. Open connection to X display
/// 2. Get root window (entire screen)
/// 3. Use XShmGetImage for efficient shared memory capture
/// 4. Convert from X11 pixel format to BGRA
///
/// Dependencies:
/// - libX11.so (X11 library)
/// - libXext.so (X11 extensions, for XShm)
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class X11ScreenCapturer : IScreenCapturer
{
    private IntPtr _display = IntPtr.Zero;
    private IntPtr _rootWindow = IntPtr.Zero;
    private int _screenWidth;
    private int _screenHeight;
    private bool _initialized;
    private bool _disposed;

    // X11 native methods
    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y,
        uint width, uint height, ulong plane_mask, int format);

    [DllImport("libX11.so.6")]
    private static extern void XDestroyImage(IntPtr ximage);

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int x, y;
        public int width, height;
        public int border_width;
        public int depth;
        // ... other fields omitted for brevity
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int width, height;
        public int xoffset;
        public int format;
        public IntPtr data;
        public int byte_order;
        public int bitmap_unit;
        public int bitmap_bit_order;
        public int bitmap_pad;
        public int depth;
        public int bytes_per_line;
        public int bits_per_pixel;
        // ... other fields
    }

    public int Width => _screenWidth;
    public int Height => _screenHeight;

    public bool Initialize()
    {
        if (_initialized)
            return true;

        try
        {
            // Open connection to X server
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
            {
                Log.Error("Failed to open X display");
                return false;
            }

            // Get root window (entire screen)
            _rootWindow = XDefaultRootWindow(_display);
            if (_rootWindow == IntPtr.Zero)
            {
                Log.Error("Failed to get root window");
                return false;
            }

            // Get screen dimensions
            XGetWindowAttributes(_display, _rootWindow, out var attributes);
            _screenWidth = attributes.width;
            _screenHeight = attributes.height;

            _initialized = true;
            Log.Information("Initialized: {Width}x{Height}", _screenWidth, _screenHeight);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Initialization failed");
            return false;
        }
    }

    public bool TryAcquireFrame(out Frame? frame)
    {
        frame = null;

        if (!_initialized || _disposed)
            return false;

        try
        {
            // Capture screen using XGetImage
            // ZPixmap = 2 (format), AllPlanes = ~0UL
            IntPtr xImage = XGetImage(_display, _rootWindow, 0, 0,
                (uint)_screenWidth, (uint)_screenHeight, ~0UL, 2);

            if (xImage == IntPtr.Zero)
                return false;

            try
            {
                // Marshal XImage structure
                var imageStruct = Marshal.PtrToStructure<XImage>(xImage);

                // Calculate buffer size
                int bufferSize = imageStruct.bytes_per_line * imageStruct.height;
                byte[] buffer = new byte[bufferSize];

                // Copy image data
                Marshal.Copy(imageStruct.data, buffer, 0, bufferSize);

                // TODO: Convert from X11 pixel format (usually BGRA or RGBA) to our standard BGRA
                // For now, assume it's already BGRA

                frame = new Frame(
                    width: imageStruct.width,
                    height: imageStruct.height,
                    data: buffer,
                    stride: imageStruct.bytes_per_line,
                    timestamp: DateTime.UtcNow);

                return true;
            }
            finally
            {
                XDestroyImage(xImage);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Frame capture failed");
            return false;
        }
    }

    public void ReleaseFrame()
    {
        // No-op for X11 - we copy data immediately in TryAcquireFrame
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

        _initialized = false;
        _disposed = true;

        Log.Information("Disposed");
    }
}
