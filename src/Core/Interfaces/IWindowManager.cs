namespace DeskShare.Core.Interfaces;

/// <summary>
/// Defines the contract for platform-specific window management implementations.
/// Handles window enumeration and window-specific capture.
/// Platform-specific implementations: Windows (EnumWindows), Linux (X11 windows), macOS (CGWindow).
/// </summary>
public interface IWindowManager : IDisposable
{
    /// <summary>
    /// Enumerates all visible windows on the desktop.
    /// </summary>
    /// <returns>List of window information for all visible windows.</returns>
    List<WindowInfo> EnumerateWindows();

    /// <summary>
    /// Gets the title of a specific window.
    /// </summary>
    /// <param name="windowHandle">Platform-specific window handle.</param>
    /// <returns>Window title, or null if window is invalid.</returns>
    string? GetWindowTitle(IntPtr windowHandle);

    /// <summary>
    /// Checks if a window is valid and visible.
    /// </summary>
    /// <param name="windowHandle">Platform-specific window handle.</param>
    /// <returns>True if window is valid and visible, false otherwise.</returns>
    bool IsWindowValid(IntPtr windowHandle);

    /// <summary>
    /// Gets the bounds (position and size) of a window.
    /// </summary>
    /// <param name="windowHandle">Platform-specific window handle.</param>
    /// <returns>Window bounds, or null if window is invalid.</returns>
    WindowBounds? GetWindowBounds(IntPtr windowHandle);
}

/// <summary>
/// Information about a window.
/// </summary>
public class WindowInfo
{
    /// <summary>Platform-specific window handle.</summary>
    public IntPtr Handle { get; set; }

    /// <summary>Window title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Window bounds.</summary>
    public WindowBounds Bounds { get; set; } = new();

    /// <summary>Whether the window is visible.</summary>
    public bool IsVisible { get; set; }
}

/// <summary>
/// Window position and size.
/// </summary>
public class WindowBounds
{
    /// <summary>X coordinate of top-left corner.</summary>
    public int X { get; set; }

    /// <summary>Y coordinate of top-left corner.</summary>
    public int Y { get; set; }

    /// <summary>Window width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Window height in pixels.</summary>
    public int Height { get; set; }
}
