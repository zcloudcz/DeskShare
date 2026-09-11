using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DeskShare.ScreenSenderApp.Capture;

/// <summary>
/// Enumerates all visible windows in the system.
/// Uses Windows API (EnumWindows, GetWindowText, IsWindowVisible) to find capturable windows.
/// </summary>
/// <remarks>
/// This class provides a way to list all windows that can be captured.
/// Useful for allowing users to select which window to share instead of entire screen.
///
/// Usage:
/// <code>
/// var enumerator = new WindowEnumerator();
/// var windows = enumerator.EnumerateWindows();
/// foreach (var window in windows)
/// {
///     Console.WriteLine($"{window.Handle}: {window.Title}");
/// }
/// </code>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowEnumerator
{
    // Win32 API imports for window enumeration
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
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Delegate for EnumWindows callback
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Enumerates all visible windows in the system.
    /// Filters out invisible, minimized, and empty-title windows.
    /// </summary>
    /// <returns>List of window information (handle, title, process ID).</returns>
    public List<WindowInfo> EnumerateWindows()
    {
        var windows = new List<WindowInfo>();

        // Callback for EnumWindows - called for each top-level window
        bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
        {
            // Skip invisible windows (hidden windows, background processes)
            if (!IsWindowVisible(hWnd))
                return true;

            // Skip minimized windows (iconified)
            if (IsIconic(hWnd))
                return true;

            // Get window title length
            int length = GetWindowTextLength(hWnd);
            if (length == 0)
                return true; // Skip windows without title (typically not user windows)

            // Get window title
            var builder = new StringBuilder(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            string title = builder.ToString();

            if (string.IsNullOrWhiteSpace(title))
                return true; // Skip empty titles

            // Get process ID for the window
            GetWindowThreadProcessId(hWnd, out uint processId);

            // Add to list
            windows.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                ProcessId = processId
            });

            return true; // Continue enumeration
        }

        // Start enumeration
        EnumWindows(EnumWindowCallback, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// Gets the title of a specific window by its handle.
    /// Useful for refreshing window title or validating window still exists.
    /// </summary>
    /// <param name="handle">Window handle (HWND).</param>
    /// <returns>Window title, or empty string if window doesn't exist or has no title.</returns>
    public string GetWindowTitle(IntPtr handle)
    {
        int length = GetWindowTextLength(handle);
        if (length == 0)
            return string.Empty;

        var builder = new StringBuilder(length + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    /// <summary>
    /// Checks if a window is still valid and visible.
    /// Useful for validating that selected window still exists before capture.
    /// </summary>
    /// <param name="handle">Window handle (HWND).</param>
    /// <returns>True if window is valid and visible, false otherwise.</returns>
    public bool IsWindowValid(IntPtr handle)
    {
        return IsWindowVisible(handle) && GetWindowTextLength(handle) > 0;
    }
}

/// <summary>
/// Information about a capturable window.
/// Contains window handle, title, and process ID.
/// </summary>
public sealed class WindowInfo
{
    /// <summary>
    /// Window handle (HWND) - unique identifier for the window.
    /// Use this to capture the window.
    /// </summary>
    public IntPtr Handle { get; set; }

    /// <summary>
    /// Window title text (caption).
    /// Displayed in window's title bar.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Process ID that owns this window.
    /// Can be used to identify application (e.g., chrome.exe, notepad.exe).
    /// </summary>
    public uint ProcessId { get; set; }

    /// <summary>
    /// Returns a formatted string representation of the window.
    /// Format: "Title (PID: ProcessId, HWND: Handle)"
    /// </summary>
    public override string ToString()
    {
        return $"{Title} (PID: {ProcessId}, HWND: 0x{Handle:X})";
    }
}
