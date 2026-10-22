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
    private static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6")]
    private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, byte[] data, int nelements);

    // format 32 data is an array of C longs (8 bytes on 64-bit Linux), not ints
    [DllImport("libX11.so.6", EntryPoint = "XChangeProperty")]
    private static extern int XChangePropertyLongs(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, long[] data, int nelements);

    [DllImport("libX11.so.6")]
    private static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, long eventMask, ref XEvent sendEvent);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property,
        long long_offset, long long_length, bool delete, IntPtr req_type,
        out IntPtr actual_type_return, out int actual_format_return,
        out ulong nitems_return, out ulong bytes_after_return, out IntPtr prop_return);

    [StructLayout(LayoutKind.Explicit, Size = 192)]
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
    private const int PropModeReplace = 0;
    private const int MaxReadLongs = 32768; // 128 KB of property data; larger texts need the INCR protocol (not supported)

    // Xlib is not thread safe without XInitThreads: the monitor thread and callers share one display, so
    // every X call that touches the event queue happens under this lock.
    private readonly object _xLock = new();
    private IntPtr _atomAtom;
    private IntPtr _atomString;

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
            _atomAtom = XInternAtom(_display, "ATOM", false);
            _atomString = XInternAtom(_display, "STRING", false);

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

    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            lock (_xLock)
            {
                return ReadSelection();
            }
        }, cancellationToken);
    }

    // Asks the current clipboard owner for UTF8_STRING and waits for the answer. Caller holds _xLock.
    private string? ReadSelection()
    {
        if (_display == IntPtr.Zero)
            return null;

        try
        {
            XConvertSelection(_display, _atomClipboard, _atomUtf8String,
                _atomClipboard, _window, IntPtr.Zero);
            XFlush(_display);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (XPending(_display) == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                XNextEvent(_display, out XEvent xEvent);

                if (xEvent.type == SelectionRequest)
                {
                    HandleSelectionRequest(xEvent.xselectionrequest);
                }
                else if (xEvent.type == SelectionNotify)
                {
                    if (xEvent.xselection.property == IntPtr.Zero)
                        return null; // owner could not convert to UTF8_STRING

                    // delete = true: reading removes the property, so it does not pile up on our window
                    XGetWindowProperty(_display, _window, _atomClipboard,
                        0, MaxReadLongs, true, _atomUtf8String,
                        out _, out _, out ulong nItems, out _, out IntPtr data);

                    if (data == IntPtr.Zero)
                        return null;

                    try
                    {
                        if (nItems == 0)
                            return null;
                        var buffer = new byte[nItems];
                        Marshal.Copy(data, buffer, 0, (int)nItems);
                        return Encoding.UTF8.GetString(buffer);
                    }
                    finally
                    {
                        XFree(data); // memory owned by Xlib, not Marshal.AllocHGlobal
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetTextAsync failed");
            return null;
        }
    }

    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || _display == IntPtr.Zero)
            return Task.FromResult(false);

        try
        {
            lock (_xLock)
            {
                // Store the text locally (X11 clipboard is pull-based, not push-based): other apps ask us for it
                // through SelectionRequest events, answered by HandleSelectionRequest.
                _lastClipboardText = text;

                XSetSelectionOwner(_display, _atomClipboard, _window, IntPtr.Zero);
                XFlush(_display);

                return Task.FromResult(XGetSelectionOwner(_display, _atomClipboard) == _window);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SetTextAsync failed");
            return Task.FromResult(false);
        }
    }

    private void MonitorClipboardLoop()
    {
        IntPtr lastOwner = IntPtr.Zero;

        try
        {
            while (_monitorCts?.Token.IsCancellationRequested == false)
            {
                string? newText = null;

                lock (_xLock)
                {
                    // Poll for clipboard owner changes
                    IntPtr currentOwner = XGetSelectionOwner(_display, _atomClipboard);

                    if (currentOwner != lastOwner && currentOwner != _window)
                    {
                        lastOwner = currentOwner;
                        newText = ReadSelection();
                    }

                    // Answer selection requests (other apps pasting what we put on the clipboard)
                    while (XPending(_display) > 0)
                    {
                        XNextEvent(_display, out XEvent xEvent);

                        if (xEvent.type == SelectionRequest)
                        {
                            HandleSelectionRequest(xEvent.xselectionrequest);
                        }
                    }
                }

                // Outside the lock: handlers may call SetTextAsync.
                if (newText != null && newText != _lastClipboardText)
                {
                    _lastClipboardText = newText;
                    ClipboardChanged?.Invoke(this, new ClipboardChangedEventArgs { Text = newText, Timestamp = DateTime.UtcNow });
                }

                Thread.Sleep(200); // Poll every 200ms
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Clipboard monitor loop error");
        }
    }

    private void HandleSelectionRequest(XSelectionRequestEvent request)
    {
        // Another application asks for our clipboard: write the answer into a property on its window,
        // then tell it with a SelectionNotify event (property = None means "cannot convert").
        var reply = new XEvent();
        reply.xselection.type = SelectionNotify;
        reply.xselection.display = request.display;
        reply.xselection.requestor = request.requestor;
        reply.xselection.selection = request.selection;
        reply.xselection.target = request.target;
        reply.xselection.time = request.time;
        reply.xselection.property = IntPtr.Zero;

        try
        {
            // Old clients send no property: the convention is to use the target atom.
            var property = request.property != IntPtr.Zero ? request.property : request.target;

            if (request.target == _atomTargets)
            {
                var targets = new[] { (long)_atomTargets, (long)_atomUtf8String, (long)_atomString, (long)_atomText };
                XChangePropertyLongs(_display, request.requestor, property, _atomAtom, 32, PropModeReplace, targets, targets.Length);
                reply.xselection.property = property;
            }
            else if (_lastClipboardText != null &&
                     (request.target == _atomUtf8String || request.target == _atomString || request.target == _atomText))
            {
                var data = Encoding.UTF8.GetBytes(_lastClipboardText);
                XChangeProperty(_display, request.requestor, property, _atomUtf8String, 8, PropModeReplace, data, data.Length);
                reply.xselection.property = property;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HandleSelectionRequest failed");
        }

        XSendEvent(_display, request.requestor, false, 0, ref reply);
        XFlush(_display);
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
