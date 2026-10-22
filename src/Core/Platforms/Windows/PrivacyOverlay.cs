using System.Runtime.InteropServices;
using DeskShare.Core.Interfaces;
using Serilog;

namespace DeskShare.Core.Platforms.Windows;

/// <summary>
/// Privacy screen for Windows: one black, topmost window per monitor that the remote viewer does not see.
/// </summary>
/// <remarks>
/// Why it works this way (for junior developers):
/// <list type="bullet">
/// <item>The windows are excluded from screen capture (WDA_EXCLUDEFROMCAPTURE), so the video still shows the real
/// desktop. That flag exists since Windows 10 2004; on older builds the overlay would black out the viewer too, so
/// we refuse to show it (and also when the call fails).</item>
/// <item>The windows are click-through (WS_EX_LAYERED + WS_EX_TRANSPARENT, never activated). The viewer's injected
/// mouse input therefore lands on the real windows below, which the viewer sees but the local user does not.</item>
/// <item>Keeping the local user out is the job of <c>BlockInput</c> (optional). It needs an elevated process and
/// Windows ends it on Ctrl+Alt+Del by design; that is the local user's escape hatch and is intended.</item>
/// <item>Windows need a message loop and are bound to the thread that created them, so everything runs on one
/// dedicated STA thread. Callers talk to it with a synchronous SendMessage to a hidden message-only window.</item>
/// <item>BlockInput stays tied to the thread that called it, so it is called from that same thread (hide, desktop
/// switch and exit unblock from there too).</item>
/// <item>On the lock screen / UAC (Winlogon desktop) the overlay hides itself and comes back with the Default desktop.</item>
/// </list>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PrivacyOverlay : IPrivacyOverlay
{
    /// <summary>First Windows 10 build (2004) that supports WDA_EXCLUDEFROMCAPTURE.</summary>
    public const int MinBuildForCaptureExclusion = 19041;

    public const string NoticeText = "This computer is being used remotely.";

    private const uint WdaExcludeFromCapture = 0x11;
    private const uint WmApp = 0x8000;
    private const uint MsgShow = WmApp + 1;
    private const uint MsgHide = WmApp + 2;
    private const uint MsgQuit = WmApp + 3;
    private const int TimerId = 1;
    private const uint TimerMs = 500;

    private readonly string _className = "DeskSharePrivacy" + Guid.NewGuid().ToString("N");
    private readonly WndProc _wndProc; // must stay referenced: native code holds only the pointer
    private readonly object _lock = new();
    private readonly List<IntPtr> _windows = new();
    private readonly List<Rect> _rects = new();
    private InputDesktop? _desktop;
    private Thread? _thread;
    private IntPtr _controller;
    private IntPtr _font;
    private bool _visible;       // overlay requested (windows exist)
    private bool _blockRequested;
    private bool _blocking;      // BlockInput(true) currently in effect
    private bool _hiddenForDesktop;
    private string? _reason;
    private bool _disposed;

    private readonly Func<bool, bool>? _setInputBlocked;

    /// <param name="setInputBlocked">
    /// Blocks/unblocks local input; pass <see cref="WindowsInputController.SetLocalInputBlocked"/> so it runs on the
    /// input injection thread. Without it the overlay thread calls BlockInput itself, and the viewer's injected
    /// input is blocked too.
    /// </param>
    public PrivacyOverlay(Func<bool, bool>? setInputBlocked = null)
    {
        _setInputBlocked = setInputBlocked;
        _wndProc = HandleMessage;
        // Last line of defense when the process is torn down without Dispose; Windows also releases BlockInput itself.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>Handles of the overlay windows (one per monitor); empty while hidden. For tests.</summary>
    public IReadOnlyList<IntPtr> WindowHandles
    {
        get { lock (_lock) return _windows.ToArray(); }
    }

    /// <summary>Reads the display affinity of a window (0x11 = excluded from capture).</summary>
    public static bool TryGetDisplayAffinity(IntPtr hwnd, out uint affinity) => GetWindowDisplayAffinity(hwnd, out affinity);

    /// <summary>Reads the extended window style (tests check click-through).</summary>
    public static bool TryGetExStyle(IntPtr hwnd, out uint style)
    {
        style = (uint)GetWindowLongW(hwnd, -20);
        return style != 0;
    }

    /// <summary>Pure rule so the version gate is testable on any build.</summary>
    public static bool IsCaptureExclusionSupported(Version osVersion) =>
        osVersion.Major > 10 || (osVersion.Major == 10 && osVersion.Build >= MinBuildForCaptureExclusion);

    public PrivacyShowResult TryShow(bool blockInput)
    {
        if (!OperatingSystem.IsWindows())
            return new PrivacyShowResult(false, "unsupported_platform");
        if (!IsCaptureExclusionSupported(Environment.OSVersion.Version))
            return new PrivacyShowResult(false, "unsupported_windows_version");

        lock (_lock)
        {
            if (_disposed)
                return new PrivacyShowResult(false, "disposed");
            if (!EnsureThread())
                return new PrivacyShowResult(false, "overlay_thread_failed");
        }

        // Synchronous: returns once the UI thread handled it. 0 = failed, 1 = shown, 2 = shown but BlockInput failed.
        var result = (int)SendMessageW(_controller, MsgShow, blockInput ? 1 : 0, IntPtr.Zero);
        return result switch
        {
            1 => new PrivacyShowResult(true, null, Volatile.Read(ref _shownGeneration)),
            2 => new PrivacyShowResult(true, "input_block_failed", Volatile.Read(ref _shownGeneration)),
            _ => new PrivacyShowResult(false, _reason ?? "overlay_failed")
        };
    }

    /// <inheritdoc />
    public event Action<int, string>? Failed;

    private int _generation; // bumped by every show and hide; a late Failed of an older generation is ignored
    private int _shownGeneration; // generation of the last Show, written on the UI thread before SendMessage returns

    public void Hide()
    {
        IntPtr controller;
        lock (_lock) controller = _controller;
        if (controller != IntPtr.Zero)
            SendMessageW(controller, MsgHide, 0, IntPtr.Zero);
    }

    public void Dispose()
    {
        IntPtr controller;
        Thread? thread;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            controller = _controller;
            thread = _thread;
        }
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        if (controller != IntPtr.Zero)
            SendMessageW(controller, MsgQuit, 0, IntPtr.Zero);
        thread?.Join(2000);
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        try { Dispose(); } catch { /* process is exiting anyway */ }
    }

    // ---- UI thread ----

    private bool EnsureThread()
    {
        if (_thread != null)
            return _controller != IntPtr.Zero;

        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "PrivacyOverlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        return _controller != IntPtr.Zero;
    }

    private void Run(ManualResetEventSlim ready)
    {
        var instance = GetModuleHandleW(null);
        var wc = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = instance,
            ClassName = _className
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            ready.Set();
            return;
        }

        _desktop = new InputDesktop();
        _font = CreateFontW(-44, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        // HWND_MESSAGE parent (-3): an invisible window that only receives our commands and the timer.
        var controller = CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
        _controller = controller;
        ready.Set();

        if (controller != IntPtr.Zero)
        {
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        DestroyOverlayWindows();
        if (_font != IntPtr.Zero)
            DeleteObject(_font);
        _desktop?.Dispose();
        UnregisterClassW(_className, instance);
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case MsgShow:
                return new IntPtr(Show(wParam != IntPtr.Zero));
            case MsgHide:
                HideAll();
                return IntPtr.Zero;
            case MsgQuit:
                HideAll();
                KillTimer(hwnd, TimerId);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                if (hwnd == _controller)
                    PostQuitMessage(0);
                return IntPtr.Zero;
            case 0x0113: // WM_TIMER
                if (hwnd == _controller)
                    Tick();
                return IntPtr.Zero;
            case 0x0014: // WM_ERASEBKGND: we paint everything ourselves
                return new IntPtr(1);
            case 0x0084: // WM_NCHITTEST: HTTRANSPARENT, so even a hit test never lands on the overlay
                return hwnd == _controller ? DefWindowProcW(hwnd, msg, wParam, lParam) : new IntPtr(-1);
            case 0x000F: // WM_PAINT
                if (hwnd != _controller)
                {
                    Paint(hwnd);
                    return IntPtr.Zero;
                }
                break;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private int Show(bool blockInput)
    {
        _reason = null;
        if (_visible)
            HideAll();
        // Captured here, on the UI thread, for TryShow: a Tick failure right after this show bumps _generation, and
        // TryShow must still report THIS show's generation so that failure is matched to it.
        _shownGeneration = Interlocked.Increment(ref _generation);

        _desktop?.Refresh();
        _blockRequested = blockInput;
        _rects.Clear();
        _rects.AddRange(EnumerateMonitors());
        if (!CreateOverlayWindows())
            return 0;

        _visible = true;
        _hiddenForDesktop = !IsDefaultDesktop();
        var blockFailed = false;
        if (!_hiddenForDesktop)
        {
            ShowAllWindows(true);
            if (blockInput)
                blockFailed = !SetBlocking(true);
        }
        SetTimer(_controller, TimerId, TimerMs, IntPtr.Zero);
        return blockFailed ? 2 : 1;
    }

    private void HideAll()
    {
        Interlocked.Increment(ref _generation);
        KillTimer(_controller, TimerId);
        SetBlocking(false);
        DestroyOverlayWindows();
        _visible = false;
        _hiddenForDesktop = false;
    }

    /// <summary>Every 500 ms: follow monitor changes and the input desktop, and stay above topmost windows of other apps.</summary>
    private void Tick()
    {
        if (!_visible)
            return;

        _desktop?.Refresh();
        var onDefault = IsDefaultDesktop();
        if (onDefault != !_hiddenForDesktop)
        {
            _hiddenForDesktop = !onDefault;
            ShowAllWindows(onDefault);
            if (_blockRequested)
                SetBlocking(onDefault); // unblock on the lock screen, re-block when the user's desktop is back
        }
        if (_hiddenForDesktop)
            return;

        var rects = EnumerateMonitors();
        if (!rects.SequenceEqual(_rects))
        {
            _rects.Clear();
            _rects.AddRange(rects);
            DestroyOverlayWindows();
            if (!CreateOverlayWindows())
            {
                var failedGeneration = _generation;
                HideAll(); // cannot cover the new layout safely; fail open and tell the controller
                var afterHide = _generation;
                // Queued, not called here: the handler takes the controller's lock, which a thread blocked in TryShow
                // (waiting for this very thread) may hold. If a newer show/hide happened meanwhile, drop it.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    if (afterHide == Volatile.Read(ref _generation))
                        Failed?.Invoke(failedGeneration, "overlay_failed");
                });
                return;
            }
            ShowAllWindows(true);
            return;
        }

        foreach (var w in _windows)
            SetWindowPos(w, new IntPtr(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private bool IsDefaultDesktop() =>
        _desktop?.Name is null || string.Equals(_desktop.Name, DesktopRules.DefaultDesktop, StringComparison.OrdinalIgnoreCase);

    private bool SetBlocking(bool block)
    {
        if (block == _blocking)
            return true;
        if (!(_setInputBlocked?.Invoke(block) ?? BlockInput(block)))
        {
            Log.Warning("BlockInput({Block}) failed, Win32 error {Error}; it needs an elevated process", block, Marshal.GetLastWin32Error());
            return false;
        }
        _blocking = block;
        return true;
    }

    private bool CreateOverlayWindows()
    {
        var instance = GetModuleHandleW(null);
        foreach (var r in _rects)
        {
            // TOPMOST + TOOLWINDOW (no taskbar button) + NOACTIVATE (never steals focus) + LAYERED|TRANSPARENT (click-through).
            var hwnd = CreateWindowExW(0x00080000 | 0x00000020 | 0x00000080 | 0x08000000 | 0x00000008,
                _className, "DeskShare privacy screen", 0x80000000 /* WS_POPUP */,
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                _reason = "window_create_failed";
                DestroyOverlayWindows();
                return false;
            }
            lock (_lock) _windows.Add(hwnd);
            SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /* LWA_ALPHA */); // a layered window shows nothing without this call

            // The overlay may only exist if the viewer cannot see it. Otherwise the viewer gets a black screen too.
            if (!SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
            {
                Log.Warning("SetWindowDisplayAffinity failed, Win32 error {Error}; privacy screen not shown", Marshal.GetLastWin32Error());
                _reason = "capture_exclusion_failed";
                DestroyOverlayWindows();
                return false;
            }
        }
        return true;
    }

    private void ShowAllWindows(bool show)
    {
        foreach (var w in _windows)
        {
            if (show)
            {
                SetWindowPos(w, new IntPtr(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | 0x0040 /* SWP_SHOWWINDOW */);
                UpdateWindow(w);
            }
            else
            {
                ShowWindow(w, 0 /* SW_HIDE */);
            }
        }
    }

    private void DestroyOverlayWindows()
    {
        IntPtr[] windows;
        lock (_lock)
        {
            windows = _windows.ToArray();
            _windows.Clear();
        }
        foreach (var w in windows)
            DestroyWindow(w);
    }

    private void Paint(IntPtr hwnd)
    {
        var dc = BeginPaint(hwnd, out var ps);
        GetClientRect(hwnd, out var rc);
        FillRect(dc, ref rc, GetStockObject(4 /* BLACK_BRUSH */));
        SetBkMode(dc, 1 /* TRANSPARENT */);
        SetTextColor(dc, 0x00FFFFFF);
        var old = SelectObject(dc, _font);
        DrawTextW(dc, NoticeText, -1, ref rc, 0x1 | 0x4 | 0x20 /* CENTER | VCENTER | SINGLELINE */);
        SelectObject(dc, old);
        EndPaint(hwnd, ref ps);
    }

    private static List<Rect> EnumerateMonitors()
    {
        var list = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref Rect r, IntPtr _) =>
        {
            list.Add(r);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---- Win32 ----

    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect : IEquatable<Rect>
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Equals(Rect o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;
        public override readonly bool Equals(object? obj) => obj is Rect r && Equals(r);
        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, int wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, int id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool BlockInput(bool block);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr dc, string text, int count, ref Rect rect, uint format);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
}
