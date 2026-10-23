using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskShare.Core.Platforms.Windows;

/// <summary>One push button of a <see cref="Win32Panel"/>.</summary>
internal sealed record PanelButton(int Id, string Text);

/// <summary>Look and placement of a <see cref="Win32Panel"/>.</summary>
/// <param name="Banner">True: a thin bar at the top-centre of the primary monitor that never takes focus. False: a dialog in the middle of the screen.</param>
/// <param name="HideOnSecureDesktop">Hide while the input desktop is not the user's (lock screen, UAC).</param>
internal sealed record PanelOptions(string Title, string Text, IReadOnlyList<PanelButton> Buttons, bool Banner, bool HideOnSecureDesktop,
    int Width, int Height, uint Background = 0x00F0F0F0, uint Foreground = 0x00000000);

/// <summary>
/// A tiny Win32 window with a text and push buttons: the session banner and the "allow this connection?" prompt both use it
/// (no UI framework is available in the headless agent).
/// </summary>
/// <remarks>
/// For junior developers:
/// <list type="bullet">
/// <item>A window belongs to the thread that created it and needs that thread's message loop, so the panel runs its own
/// STA thread (same approach as <see cref="PrivacyOverlay"/>). Other threads talk to it with PostMessage only, so a
/// caller can never deadlock against the loop.</item>
/// <item>Banner mode uses WS_EX_NOACTIVATE: clicking a button works but the window never takes keyboard focus from
/// what the user is typing in. A timer re-asserts "topmost" because other topmost windows can push it down.</item>
/// <item>The window is deliberately NOT excluded from screen capture: the viewer sees the banner too.</item>
/// <item>Button handlers run on the thread pool, not on the UI thread, so a slow handler cannot freeze the window.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class Win32Panel : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint MsgClose = WmApp + 1;
    private const uint MsgSetText = WmApp + 2;
    private const uint MsgSetButton = WmApp + 3;
    private const uint MsgSetVisible = WmApp + 4;
    private const int TimerId = 1;
    private const uint TimerMs = 500;

    private readonly PanelOptions _options;
    private readonly string _className = "DeskSharePanel" + Guid.NewGuid().ToString("N");
    private readonly WndProc _wndProc; // must stay referenced: native code holds only the pointer
    private readonly object _lock = new();
    private readonly Dictionary<int, IntPtr> _buttons = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private InputDesktop? _desktop;
    private IntPtr _hwnd, _text, _font, _brush;
    private string _pendingText;
    private bool _wantVisible = true;
    private bool _shown;
    private bool _disposed;

    public Win32Panel(PanelOptions options)
    {
        _options = options;
        _pendingText = options.Text;
        _wndProc = HandleMessage;
    }

    /// <summary>Raised with the button id when a button is clicked (thread-pool thread).</summary>
    public event Action<int>? ButtonClicked;

    /// <summary>Completes when the window is gone (closed by <see cref="Close"/>, the user, or a failed start).</summary>
    public Task Closed => _closed.Task;

    /// <summary>The window handle (zero before <see cref="Start"/> or after it closed). Tests inspect styles with it.</summary>
    public IntPtr Handle
    {
        get { lock (_lock) return _hwnd; }
    }

    /// <summary>Text currently shown (what the window itself was last told).</summary>
    public string Text
    {
        get { lock (_lock) return _pendingText; }
    }

    /// <summary>Creates and shows the window. Returns false when Windows refused (no desktop access, for example).</summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (_disposed || _thread != null) return _hwnd != IntPtr.Zero;
            _thread = new Thread(Run) { IsBackground = true, Name = "Win32Panel" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        _ready.Wait();
        return Handle != IntPtr.Zero;
    }

    public void SetText(string text)
    {
        IntPtr h;
        lock (_lock) { _pendingText = text; h = _hwnd; }
        if (h != IntPtr.Zero) PostMessageW(h, MsgSetText, IntPtr.Zero, IntPtr.Zero);
    }

    public void SetButtonVisible(int id, bool visible)
    {
        var h = Handle;
        if (h != IntPtr.Zero) PostMessageW(h, MsgSetButton, new IntPtr(id), new IntPtr(visible ? 1 : 0));
    }

    public void SetVisible(bool visible)
    {
        lock (_lock) _wantVisible = visible;
        var h = Handle;
        if (h != IntPtr.Zero) PostMessageW(h, MsgSetVisible, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Asks the window to close; <see cref="Closed"/> completes when it did.</summary>
    public void Close()
    {
        var h = Handle;
        if (h != IntPtr.Zero) PostMessageW(h, MsgClose, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
        }
        Close();
        thread?.Join(2000);
    }

    // ---- UI thread ----

    private void Run()
    {
        var instance = GetModuleHandleW(null);
        var created = false;
        try
        {
            created = Setup(instance);
        }
        finally
        {
            _ready.Set(); // after the handle is published (or the start failed)
        }

        if (created)
        {
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        lock (_lock) _hwnd = IntPtr.Zero;
        _desktop?.Dispose();
        if (_font != IntPtr.Zero) DeleteObject(_font);
        if (_brush != IntPtr.Zero) DeleteObject(_brush);
        UnregisterClassW(_className, instance);
        _closed.TrySetResult();
    }

    private bool Setup(IntPtr instance)
    {
        _font = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _brush = CreateSolidBrush(_options.Background);
        var wc = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = instance,
            ClassName = _className,
            Background = _brush
        };
        if (RegisterClassExW(ref wc) == 0) return false;

        if (_options.HideOnSecureDesktop) _desktop = new InputDesktop();

        var screenW = GetSystemMetrics(0 /* SM_CXSCREEN: primary monitor */);
        var screenH = GetSystemMetrics(1);
        var x = (screenW - _options.Width) / 2;
        var y = _options.Banner ? 0 : (screenH - _options.Height) / 2;
        // Banner: TOPMOST | TOOLWINDOW (no taskbar button) | NOACTIVATE (never takes focus), thin border.
        // Prompt: TOPMOST so it cannot hide behind the user's apps, a caption with a close button (= Deny).
        var exStyle = _options.Banner ? 0x00000008u | 0x00000080u | 0x08000000u : 0x00000008u;
        var style = _options.Banner ? 0x80000000u | 0x00800000u /* POPUP|BORDER */ : 0x80000000u | 0x00C00000u | 0x00080000u /* POPUP|CAPTION|SYSMENU */;
        var hwnd = CreateWindowExW(exStyle, _className, _options.Title, style, x, y, _options.Width, _options.Height,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return false;

        lock (_lock) _hwnd = hwnd;
        CreateChildren(hwnd, instance);
        ApplyVisibility();
        SetTimer(hwnd, TimerId, TimerMs, IntPtr.Zero);
        return true;
    }

    private void CreateChildren(IntPtr parent, IntPtr instance)
    {
        GetClientRect(parent, out var rc);
        const int btnW = 100, gap = 8;
        var btnH = _options.Banner ? 26 : 30;
        var count = _options.Buttons.Count;
        var buttonsWidth = count * btnW + (count - 1) * gap;
        int textX, textY, textW, textH, btnX, btnY;
        if (_options.Banner)
        {
            textX = 10; textY = 0; textW = rc.Right - 20 - buttonsWidth - gap; textH = rc.Bottom;
            btnX = rc.Right - 10 - buttonsWidth; btnY = (rc.Bottom - btnH) / 2;
        }
        else
        {
            textX = 16; textY = 16; textW = rc.Right - 32; textH = rc.Bottom - btnH - 48;
            btnX = rc.Right - 16 - buttonsWidth; btnY = rc.Bottom - btnH - 16;
        }

        // SS_CENTERIMAGE centres the single banner line vertically; the prompt text wraps (default SS_LEFT).
        var textStyle = 0x40000000u | 0x10000000u | (_options.Banner ? 0x0000000Cu /* SS_LEFTNOWORDWRAP */ | 0x00000200u : 0u);
        _text = CreateWindowExW(0, "STATIC", _pendingText, textStyle, textX, textY, textW, textH, parent, IntPtr.Zero, instance, IntPtr.Zero);
        SendMessageW(_text, 0x0030 /* WM_SETFONT */, _font, new IntPtr(1));

        for (var i = 0; i < count; i++)
        {
            var b = _options.Buttons[i];
            var h = CreateWindowExW(0, "BUTTON", b.Text, 0x40000000u | 0x10000000u /* CHILD|VISIBLE */,
                btnX + i * (btnW + gap), btnY, btnW, btnH, parent, new IntPtr(b.Id), instance, IntPtr.Zero);
            SendMessageW(h, 0x0030, _font, new IntPtr(1));
            _buttons[b.Id] = h;
        }
    }

    private bool OnUsersDesktop()
    {
        if (_desktop == null) return true;
        _desktop.Refresh();
        return _desktop.Name is null || string.Equals(_desktop.Name, DesktopRules.DefaultDesktop, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyVisibility()
    {
        bool want;
        lock (_lock) want = _wantVisible;
        var show = want && OnUsersDesktop();
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        if (show)
        {
            // HWND_TOPMOST (-1) again and again: another topmost window opened later would otherwise cover us.
            // The banner never activates; the prompt may (it needs the person's attention).
            var flags = SwpNoMove | SwpNoSize | 0x0040u /* SWP_SHOWWINDOW */ | (_options.Banner ? SwpNoActivate : 0u);
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, flags);
            if (!_shown && !_options.Banner) SetForegroundWindow(hwnd); // best effort; topmost already keeps it visible
            _shown = true;
        }
        else if (_shown)
        {
            ShowWindow(hwnd, 0 /* SW_HIDE */);
            _shown = false;
        }
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case MsgClose:
            case 0x0010: // WM_CLOSE (the caption's X button of the prompt)
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                KillTimer(hwnd, TimerId);
                PostQuitMessage(0);
                return IntPtr.Zero;
            case MsgSetText:
                string text;
                lock (_lock) text = _pendingText;
                SetWindowTextW(_text, text);
                return IntPtr.Zero;
            case MsgSetButton:
                if (_buttons.TryGetValue(wParam.ToInt32(), out var button))
                    ShowWindow(button, lParam != IntPtr.Zero ? 5 /* SW_SHOW */ : 0);
                return IntPtr.Zero;
            case MsgSetVisible:
                ApplyVisibility();
                return IntPtr.Zero;
            case 0x0113: // WM_TIMER
                ApplyVisibility();
                return IntPtr.Zero;
            case 0x0111: // WM_COMMAND: low word = control id, high word 0 = BN_CLICKED
                if (((wParam.ToInt64() >> 16) & 0xFFFF) == 0)
                {
                    var id = (int)(wParam.ToInt64() & 0xFFFF);
                    if (_buttons.ContainsKey(id))
                        ThreadPool.QueueUserWorkItem(_ => ButtonClicked?.Invoke(id));
                }
                return IntPtr.Zero;
            case 0x0138: // WM_CTLCOLORSTATIC: the text sits on the panel's own colour
                SetBkColor(wParam, _options.Background);
                SetTextColor(wParam, _options.Foreground);
                return _brush;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---- Win32 ----

    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, int id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hwnd, string text);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
}
