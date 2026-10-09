using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskShare.Core.Platforms.macOS;

/// <summary>
/// Accessibility permission check for macOS. CGEvent posting (remote control) is silently ignored
/// until the user grants the app Accessibility access in System Settings > Privacy & Security.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacAccessibility
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(ApplicationServices)]
    private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    /// <summary>
    /// Returns whether the process is trusted for Accessibility. When it is not, macOS shows its
    /// "wants to control this computer" prompt (it appears once per request; the user then has to
    /// tick the app in System Settings).
    /// </summary>
    public static bool IsTrustedPromptingUser()
    {
        IntPtr cf = NativeLibrary.Load(CoreFoundation);
        IntPtr key = CFStringCreateWithCString(IntPtr.Zero, "AXTrustedCheckOptionPrompt", 0x08000100 /* kCFStringEncodingUTF8 */);
        // kCFBooleanTrue and the dictionary callbacks are exported data symbols, so they are looked up by name.
        IntPtr yes = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
        IntPtr options = CFDictionaryCreate(IntPtr.Zero, new[] { key }, new[] { yes }, 1,
            NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
        try
        {
            return AXIsProcessTrustedWithOptions(options);
        }
        finally
        {
            CFRelease(options);
            CFRelease(key);
        }
    }
}
