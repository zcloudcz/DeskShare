using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskShare.Common.Interfaces;
using DeskShare.Common.Models;

namespace DeskShare.ScreenSenderApp.Capture;

/// <summary>
/// Captures frames from a specific window using Windows PrintWindow API.
/// Alternative to DesktopDuplicator for window-specific capture.
/// </summary>
/// <remarks>
/// This capturer uses GDI (Graphics Device Interface) to capture a specific window.
/// Unlike DXGI Desktop Duplication which captures entire displays, this can capture
/// individual application windows.
///
/// Limitations:
/// - Slower than DXGI (no GPU acceleration)
/// - Some windows may not render correctly (DRM-protected content)
/// - Window must be at least partially visible
///
/// Best for:
/// - Capturing specific applications (browser, notepad, etc.)
/// - When you need to share only one window, not entire screen
/// - Scenarios where window privacy is important
/// </remarks>
[SupportedOSPlatform("windows6.1")]
public sealed class WindowCapturer : ICapturer, IDisposable
{
    private IntPtr _windowHandle;
    private string _windowTitle = string.Empty;
    private int _targetWidth;
    private int _targetHeight;
    private bool _initialized;
    private bool _disposed;

    // Win32 API imports
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// Initializes window capturer for a specific window.
    /// </summary>
    /// <param name="windowHandle">Handle (HWND) of window to capture.</param>
    /// <param name="windowTitle">Title of window (for logging/display).</param>
    /// <returns>True if initialization succeeded, false if window is invalid.</returns>
    public bool Initialize(IntPtr windowHandle, string windowTitle)
    {
        if (_initialized)
            throw new InvalidOperationException("WindowCapturer already initialized");

        // Validate window exists
        if (!IsWindow(windowHandle))
            return false;

        // Get window dimensions
        if (!GetWindowRect(windowHandle, out RECT rect))
            return false;

        _windowHandle = windowHandle;
        _windowTitle = windowTitle;
        _targetWidth = rect.Width;
        _targetHeight = rect.Height;
        _initialized = true;

        return true;
    }

    /// <summary>
    /// Initializes with default parameters.
    /// Required by ICapturer interface but not used for WindowCapturer.
    /// Use Initialize(IntPtr, string) instead.
    /// </summary>
    public bool Initialize()
    {
        throw new NotSupportedException(
            "WindowCapturer requires window handle. Use Initialize(IntPtr windowHandle, string windowTitle) instead.");
    }

    /// <summary>
    /// Attempts to capture the current window frame.
    /// Uses PrintWindow API to render window content to bitmap.
    /// </summary>
    /// <param name="frame">Captured frame data if successful.</param>
    /// <returns>True if frame was captured, false if window closed or capture failed.</returns>
    public bool TryAcquireFrame(out Frame? frame)
    {
        frame = null;

        if (!_initialized || _disposed)
            return false;

        // Check if window still exists
        if (!IsWindow(_windowHandle))
            return false;

        try
        {
            // Create bitmap for window capture
            using var bitmap = new Bitmap(_targetWidth, _targetHeight, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);

            // Get device context from graphics object
            IntPtr hdc = graphics.GetHdc();

            try
            {
                // Capture window content using PrintWindow API
                // PW_RENDERFULLCONTENT (0x00000002) = Render full window including non-client area
                if (!PrintWindow(_windowHandle, hdc, 0x00000002))
                {
                    return false; // Window capture failed
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            // Lock bitmap data for reading
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                // Calculate buffer size (4 bytes per pixel for BGRA)
                int bufferSize = bitmapData.Stride * bitmapData.Height;
                byte[] buffer = new byte[bufferSize];

                // Copy bitmap data to buffer
                Marshal.Copy(bitmapData.Scan0, buffer, 0, bufferSize);

                // Create frame using constructor
                frame = new Frame(
                    width: bitmap.Width,
                    height: bitmap.Height,
                    data: buffer,
                    stride: bitmapData.Stride,
                    timestamp: DateTime.UtcNow);

                return true;
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }
        catch
        {
            // Capture failed (window may have closed or became inaccessible)
            return false;
        }
    }

    /// <summary>
    /// Releases the current frame.
    /// Not needed for WindowCapturer (no frame locking like DXGI),
    /// but required by ICapturer interface.
    /// </summary>
    public void ReleaseFrame()
    {
        // No-op for WindowCapturer - we copy bitmap data immediately in TryAcquireFrame
        // This is different from DesktopDuplicator which holds onto DXGI resources
    }

    /// <summary>
    /// Gets the window title being captured.
    /// </summary>
    public string WindowTitle => _windowTitle;

    /// <summary>
    /// Gets the current width of the captured window.
    /// </summary>
    public int Width => _targetWidth;

    /// <summary>
    /// Gets the current height of the captured window.
    /// </summary>
    public int Height => _targetHeight;

    /// <summary>
    /// Updates window dimensions if window was resized.
    /// Call this periodically to handle window resize events.
    /// </summary>
    /// <returns>True if dimensions were updated, false if window is invalid.</returns>
    public bool UpdateDimensions()
    {
        if (!IsWindow(_windowHandle))
            return false;

        if (!GetWindowRect(_windowHandle, out RECT rect))
            return false;

        _targetWidth = rect.Width;
        _targetHeight = rect.Height;
        return true;
    }

    /// <summary>
    /// Checks if the window is still valid and can be captured.
    /// </summary>
    public bool IsWindowValid()
    {
        return IsWindow(_windowHandle);
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _initialized = false;
        _disposed = true;
    }
}
