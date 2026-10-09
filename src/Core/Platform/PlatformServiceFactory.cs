using Microsoft.Extensions.Logging;
using DeskShare.Core.Interfaces;
using Serilog;
using ILogger = Serilog.ILogger;

namespace DeskShare.Core.Platform;

/// <summary>
/// Factory for creating platform-specific service implementations.
/// Detects the current operating system and returns appropriate implementations.
/// </summary>
public static class PlatformServiceFactory
{
    /// <summary>
    /// Creates a platform-specific screen capturer.
    /// </summary>
    /// <param name="adapterIndex">Graphics adapter index (0 = primary GPU).</param>
    /// <param name="outputIndex">Output/monitor index (0 = primary monitor).</param>
    /// <param name="targetFps">Capture frame rate (used by the FFmpeg-based capturer on macOS and Linux).</param>
    /// <returns>Platform-specific screen capturer implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when current platform is not supported.</exception>
    #pragma warning disable CA1416 // Validate platform compatibility
    public static IScreenCapturer CreateScreenCapturer(int adapterIndex = 0, int outputIndex = 0, int targetFps = 30)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: Use DXGI Desktop Duplication API
            return new Platforms.Windows.DesktopDuplicator(adapterIndex, outputIndex);
        }
        else if (OperatingSystem.IsLinux())
        {
            // Linux: FFmpeg x11grab (on Wayland only X11/XWayland content is captured)
            // TODO: Detect Wayland and use PipeWire ScreenCast instead
            return new Platforms.FfmpegScreenCapturer(outputIndex, targetFps);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS: FFmpeg avfoundation (triggers the system Screen Recording prompt)
            return new Platforms.FfmpegScreenCapturer(outputIndex, targetFps);
        }
        else
        {
            throw new PlatformNotSupportedException(
                $"Screen capture is not supported on {Environment.OSVersion.Platform}");
        }
    }
    #pragma warning restore CA1416

    /// <summary>
    /// Creates a platform-specific input controller.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <returns>Platform-specific input controller implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when current platform is not supported.</exception>
    public static IInputController CreateInputController(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: SendInput API
            return new Platforms.Windows.WindowsInputController(logger);
        }
        else if (OperatingSystem.IsLinux())
        {
            // Linux: XTest extension for X11
            // TODO: Detect Wayland and use virtual input devices instead
            return new Platforms.Linux.XTestInputController(logger);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS: CGEvent API (Core Graphics Event Services)
            // Requires Accessibility permissions
            return new Platforms.macOS.CGEventInputController(logger);
        }
        else
        {
            throw new PlatformNotSupportedException(
                $"Input control is not supported on {Environment.OSVersion.Platform}");
        }
    }

    /// <summary>
    /// Creates a platform-specific clipboard manager.
    /// </summary>
    /// <returns>Platform-specific clipboard manager implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when current platform is not supported.</exception>
    public static IClipboardManager CreateClipboardManager()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: Win32 Clipboard API
            return new Platforms.Windows.Win32ClipboardManager();
        }
        else if (OperatingSystem.IsLinux())
        {
            // Linux: X11 clipboard selection
            // TODO: Detect Wayland and use wl-clipboard instead
            return new Platforms.Linux.X11ClipboardManager();
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS: NSPasteboard (using pbcopy/pbpaste tools)
            // TODO: Consider full Objective-C runtime implementation
            return new Platforms.macOS.NSPasteboardClipboardManager();
        }
        else
        {
            throw new PlatformNotSupportedException(
                $"Clipboard is not supported on {Environment.OSVersion.Platform}");
        }
    }

    /// <summary>
    /// Creates a platform-specific window manager.
    /// </summary>
    /// <returns>Platform-specific window manager implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when current platform is not supported.</exception>
    public static IWindowManager CreateWindowManager()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: EnumWindows API
            return new Platforms.Windows.Win32WindowManager();
        }
        else if (OperatingSystem.IsLinux())
        {
            // Linux: X11 window enumeration
            // TODO: Detect Wayland and use wlroots/KDE window list
            return new Platforms.Linux.X11WindowManager();
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS: CGWindow API (basic stub implementation)
            // TODO: Complete implementation with Objective-C runtime bridge
            return new Platforms.macOS.CGWindowManager();
        }
        else
        {
            throw new PlatformNotSupportedException(
                $"Window management is not supported on {Environment.OSVersion.Platform}");
        }
    }

    /// <summary>
    /// Gets a human-readable description of the current platform.
    /// </summary>
    public static string GetPlatformDescription()
    {
        if (OperatingSystem.IsWindows())
            return $"Windows {Environment.OSVersion.Version}";
        else if (OperatingSystem.IsLinux())
            return "Linux (X11/Wayland detection needed)";
        else if (OperatingSystem.IsMacOS())
            return $"macOS {Environment.OSVersion.Version}";
        else
            return $"Unknown: {Environment.OSVersion.Platform}";
    }

    /// <summary>
    /// Checks if the current platform is supported for screen sharing (server mode).
    /// </summary>
    public static bool IsServerSupported()
    {
        // Windows: Full support (DXGI + SendInput + Clipboard API)
        // Linux: Partial support (X11 capture implemented, input/clipboard TBD)
        // macOS: Partial support (Framework stubs implemented, needs completion)
        return OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
    }

    /// <summary>
    /// Checks if the current platform is supported for viewing (client mode).
    /// </summary>
    public static bool IsClientSupported()
    {
        // Client (viewer) can work on any platform that supports WebRTC decoding
        // This is mostly platform-independent (handled by SIPSorcery + FFmpeg)
        return true;
    }
}
