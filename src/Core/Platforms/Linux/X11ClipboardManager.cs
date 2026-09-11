using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// Linux implementation of clipboard manager using X11 clipboard selection.
/// Provides clipboard monitoring and synchronization for X11-based systems.
/// </summary>
/// <remarks>
/// X11 has multiple clipboard selections:
/// - PRIMARY: Text selected with mouse (middle-click paste)
/// - CLIPBOARD: Ctrl+C/Ctrl+V clipboard (what most users expect)
/// - SECONDARY: Rarely used
///
/// This implementation focuses on CLIPBOARD selection for Windows-like behavior.
///
/// X11 clipboard works differently than Windows:
/// - Clipboard content is owned by an application window
/// - When app closes, clipboard content may be lost
/// - We need to monitor for selection owner changes
///
/// Dependencies:
/// - libX11.so (X11 library)
///
/// Note: For Wayland, a different implementation using wl-clipboard would be needed.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class X11ClipboardManager : IClipboardManager
{
    private IntPtr _display = IntPtr.Zero;
    private IntPtr _window = IntPtr.Zero;
    private bool _isMonitoring;
    private bool _disposed;
    private Thread? _monitorThread;
    private CancellationTokenSource? _monitorCts;
    private string? _lastClipboardText;

    // X11 Atoms (interned string identifiers)
    private IntPtr _atomClipboard;
    private IntPtr _atomUtf8String;
    private IntPtr _atomTargets;
    private IntPtr _atomText;

    // X11 native methods
    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent,
        int x, int y, uint width, uint height, uint border_width,
        ulong border, ulong background);

    [DllImport("libX11.so.6")]
    private static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XInternAtom(IntPtr display, string atom_name, bool only_if_exists);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr selection);

    [DllImport("libX11.so.6")]
    private static extern int XConvertSelection(IntPtr display, IntPtr selection,
        IntPtr target, IntPtr property, IntPtr requestor, IntPtr time);

    [DllImport("libX11.so.6")]
    private static extern int XSetSelectionOwner(IntPtr display, IntPtr selection,
        IntPtr owner, IntPtr time);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XSync(IntPtr display, bool discard);

    [DllImport("libX11.so.6")]
    private static extern int XNextEvent(IntPtr display, out XEvent event_return);

    [DllImport("libX11.so.6")]
    private static extern int XPending(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property,
        long long_offset, long long_length, bool delete, IntPtr req_type,
        out IntPtr actual_type_return, out int actual_format_return,
        out ulong nitems_return, out ulong bytes_after_return, out IntPtr prop_return);

    [StructLayout(LayoutKind.Explicit)]
    private struct XEvent
    {
        [FieldOffset(0)]
        public int type;

        [FieldOffset(0)]
        public XSelectionEvent xselection;

        [FieldOffset(0)]
        public XSelectionRequestEvent xselectionrequest;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XSelectionEvent
    {
        public int type;
        public ulong serial;
        public bool send_event;
        public IntPtr display;
        public IntPtr requestor;
        public IntPtr selection;
        public IntPtr target;
        public IntPtr property;
        public IntPtr time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XSelectionRequestEvent
    {
        public int type;
        public ulong serial;
        public bool send_event;
        public IntPtr display;
        public IntPtr owner;
        public IntPtr requestor;
        public IntPtr selection;
        public IntPtr target;
        public IntPtr property;
        public IntPtr time;
    }

    // X11 event types
    private const int SelectionNotify = 31;
    private const int SelectionRequest = 30;

    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public bool IsMonitoring => _isMonitoring;

    public void StartMonitoring()
    {
        if (_isMonitoring)
            return;

        try
        {
            // Open X display
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
            {
                Log.Error("Failed to open X display");
                return;
            }

            // Create a simple window (required for clipboard operations)
            IntPtr rootWindow = XDefaultRootWindow(_display);
            _window = XCreateSimpleWindow(_display, rootWindow, 0, 0, 1, 1, 0, 0, 0);

            // Intern atoms (get identifiers for clipboard-related strings)
            _atomClipboard = XInternAtom(_display, "CLIPBOARD", false);
            _atomUtf8String = XInternAtom(_display, "UTF8_STRING", false);
            _atomTargets = XInternAtom(_display, "TARGETS", false);
            _atomText = XInternAtom(_display, "TEXT", false);

            // Start monitoring thread
            _monitorCts = new CancellationTokenSource();
            _monitorThread = new Thread(MonitorClipboardLoop)
            {
                IsBackground = true,
                Name = "X11ClipboardMonitor"
            };
            _monitorThread.Start();

            _isMonitoring = true;
            Log.Information("Clipboard monitoring started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start clipboard monitoring");
        }
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring)
            return;

        _monitorCts?.Cancel();
        _monitorThread?.Join(TimeSpan.FromSeconds(2));

        _isMonitoring = false;
        Log.Information("Clipboard monitoring stopped");
    }

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        if (_display == IntPtr.Zero)
            return null;

        try
        {
            // Request clipboard content
            XConvertSelection(_display, _atomClipboard, _atomUtf8String,
                _atomClipboard, _window, IntPtr.Zero);
            XFlush(_display);

            // Wait for SelectionNotify event (with timeout)
            var startTime = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(2);

            while (DateTime.UtcNow - startTime < timeout)
            {
                if (XPending(_display) > 0)
                {
                    XNextEvent(_display, out XEvent xEvent);

                    if (xEvent.type == SelectionNotify)
                    {
                        var selEvent = xEvent.xselection;

                        if (selEvent.property != IntPtr.Zero)
                        {
                            // Read the property containing clipboard data
                            XGetWindowProperty(_display, _window, _atomClipboard,
                                0, 1024, false, _atomUtf8String,
                                out IntPtr actualType, out int actualFormat,
                                out ulong nItems, out ulong bytesAfter, out IntPtr data);

                            if (data != IntPtr.Zero && nItems > 0)
                            {
                                try
                                {
                                    byte[] buffer = new byte[nItems];
                                    Marshal.Copy(data, buffer, 0, (int)nItems);
                                    string text = Encoding.UTF8.GetString(buffer);
                                    return text;
                                }
                                finally
                                {
                                    // Free the property data
                                    Marshal.FreeHGlobal(data);
                                }
                            }
                        }

                        break;
                    }
                }

                await Task.Delay(10, cancellationToken);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetTextAsync failed");
            return null;
        }
    }

    public async Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || _display == IntPtr.Zero)
            return false;

        try
        {
            // Store the text locally (X11 clipboard is pull-based, not push-based)
            _lastClipboardText = text;

            // Become the clipboard owner
            XSetSelectionOwner(_display, _atomClipboard, _window, IntPtr.Zero);
            XFlush(_display);

            // Verify we own the selection
            IntPtr owner = XGetSelectionOwner(_display, _atomClipboard);
            bool success = owner == _window;

            if (success)
            {
                Log.Debug("Clipboard text set successfully");
            }

            return await Task.FromResult(success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SetTextAsync failed");
            return false;
        }
    }

    private void MonitorClipboardLoop()
    {
        IntPtr lastOwner = IntPtr.Zero;

        try
        {
            while (_monitorCts?.Token.IsCancellationRequested == false)
            {
                // Poll for clipboard owner changes
                IntPtr currentOwner = XGetSelectionOwner(_display, _atomClipboard);

                if (currentOwner != lastOwner && currentOwner != _window)
                {
                    lastOwner = currentOwner;

                    // Clipboard owner changed, read new content
                    Task.Run(async () =>
                    {
                        string? newText = await GetTextAsync();
                        if (newText != null && newText != _lastClipboardText)
                        {
                            _lastClipboardText = newText;

                            var eventArgs = new ClipboardChangedEventArgs
                            {
                                Text = newText,
                                Timestamp = DateTime.UtcNow
                            };

                            ClipboardChanged?.Invoke(this, eventArgs);
                        }
                    });
                }

                // Check for selection request events (when other apps request our clipboard)
                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, out XEvent xEvent);

                    if (xEvent.type == SelectionRequest)
                    {
                        HandleSelectionRequest(xEvent.xselectionrequest);
                    }
                }

                Thread.Sleep(200); // Poll every 200ms
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Clipboard monitor loop error");
        }
    }

    private void HandleSelectionRequest(XSelectionRequestEvent requestEvent)
    {
        // Another application is requesting our clipboard data
        // We need to provide the data we stored in _lastClipboardText

        if (_lastClipboardText == null)
            return;

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(_lastClipboardText);
            IntPtr dataPtr = Marshal.AllocHGlobal(data.Length);

            try
            {
                Marshal.Copy(data, 0, dataPtr, data.Length);

                // Set the property with our clipboard data
                // Note: This is a simplified implementation
                // Full implementation would use XChangeProperty

                XFlush(_display);
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HandleSelectionRequest failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        StopMonitoring();

        if (_window != IntPtr.Zero)
        {
            XDestroyWindow(_display, _window);
            _window = IntPtr.Zero;
        }

        if (_display != IntPtr.Zero)
        {
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }

        _monitorCts?.Dispose();
        _disposed = true;

        Log.Information("Clipboard manager disposed");
    }
}
