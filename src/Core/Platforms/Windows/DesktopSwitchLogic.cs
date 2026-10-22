namespace DeskShare.Core.Platforms.Windows;

/// <summary>
/// Pure decision logic for following the input desktop (UAC prompt, lock screen, sign-in screen).
/// No Win32 calls here so it is unit-testable on any OS.
/// </summary>
internal sealed class DesktopChangeTracker
{
    /// <summary>Name of the last known input desktop, null until the first successful observation.</summary>
    public string? Current { get; private set; }

    /// <summary>
    /// Feeds the latest input desktop name. A null name means "could not be read" (for example the desktop is
    /// off limits to a non-SYSTEM process) and is ignored, so the normal app never sees a change on the secure desktop.
    /// </summary>
    /// <returns>True when the name differs from the previous known one.</returns>
    public bool Observe(string? name)
    {
        if (name is null || string.Equals(name, Current, StringComparison.OrdinalIgnoreCase))
            return false;
        Current = name;
        return true;
    }
}

internal static class DesktopRules
{
    /// <summary>The user's normal desktop. Everything else (Winlogon, Screen-saver) is a secure/special desktop.</summary>
    public const string DefaultDesktop = "Default";

    /// <summary>A thread only has to be moved when the input desktop is not the one the process started on.</summary>
    public static bool NeedsAttach(string? inputDesktop, string? homeDesktop) =>
        inputDesktop is not null && !string.Equals(inputDesktop, homeDesktop, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// GDI is the fallback only when Desktop Duplication failed on a non-default desktop. On the default desktop a
    /// failure keeps today's behaviour (retry DXGI); with an unknown desktop (null) nothing changes either.
    /// </summary>
    public static bool ShouldUseGdiFallback(string? desktop, bool duplicationFailed) =>
        duplicationFailed && desktop is not null && !string.Equals(desktop, DefaultDesktop, StringComparison.OrdinalIgnoreCase);
}

internal static class SasAvailability
{
    /// <summary>
    /// Why SendSAS(FALSE) cannot work, or null when it can. SoftwareSASGeneration: 0 none, 1 services,
    /// 2 ease-of-access apps, 3 both; the missing value behaves like 0 for services.
    /// </summary>
    public static string? WhyUnavailable(int? softwareSasGeneration, bool isSystem)
    {
        if (!isSystem)
            return "the agent does not run as SYSTEM (install the unattended service)";
        if (softwareSasGeneration is not (1 or 3))
            return @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\SoftwareSASGeneration must be 1 (services)";
        return null;
    }
}
