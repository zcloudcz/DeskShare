using DeskShare.Core.Platforms.Windows;

namespace DeskShare.Core.Safety;

/// <summary>
/// Unattended agent: should a new viewer be confirmed by the person at the computer first?
/// Yes while a user is logged on and the screen is unlocked (the input desktop is "Default"); no on the lock screen
/// or without a logged-on user ("Winlogon"), because nobody could answer there.
/// </summary>
public static class AdmissionPolicy
{
    /// <param name="requireConfirmation">The <c>RequireConfirmationWhenUserLoggedOn</c> setting of unattended.json.</param>
    /// <param name="inputDesktopName">Name of the desktop that receives input now; null = could not be read.</param>
    public static bool ShouldAsk(bool requireConfirmation, string? inputDesktopName) =>
        requireConfirmation
        // Unknown counts as "user's desktop": asking needlessly is safer than letting a viewer in unseen.
        && (inputDesktopName is null || string.Equals(inputDesktopName, DesktopRules.DefaultDesktop, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds the admission delegate the data channel calls before a viewer gets video or control:
    /// asks (with <paramref name="showPrompt"/>) when <see cref="ShouldAsk"/> says so, otherwise admits at once.
    /// </summary>
    public static Func<CancellationToken, Task<bool>> Create(
        bool requireConfirmation,
        Func<string?> inputDesktopName,
        Func<CancellationToken, Task<bool>> showPrompt) =>
        async ct => !ShouldAsk(requireConfirmation, inputDesktopName()) || await showPrompt(ct).ConfigureAwait(false);
}

/// <summary>The real (Windows) wiring of <see cref="AdmissionPolicy"/> for the unattended agent.</summary>
public static class UnattendedAdmission
{
    public const string PromptText = "Someone wants to connect to this computer remotely.\r\n\r\nOnly allow it if you expect this and know who it is.";

    /// <summary>Null on platforms without the confirmation window (the caller then admits everyone, as before).</summary>
    public static Func<CancellationToken, Task<bool>>? Create(bool requireConfirmation)
    {
        if (!requireConfirmation || !OperatingSystem.IsWindows())
            return null;
        return AdmissionPolicy.Create(true, ReadInputDesktopName, Ask);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Task<bool> Ask(CancellationToken ct) => ConfirmationPrompt.AskAsync(PromptText, ct);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadInputDesktopName()
    {
        using var desktop = new InputDesktop();
        desktop.Refresh();
        return desktop.Name;
    }
}
