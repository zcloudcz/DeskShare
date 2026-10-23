using System.Runtime.Versioning;

namespace DeskShare.Core.Platforms.Windows;

/// <summary>
/// A modal-style "Deny / Allow" window on the user's desktop, for the unattended agent (which has no UI framework).
/// Closing the window or cancelling the token counts as Deny.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConfirmationPrompt
{
    private const int DenyId = 1, AllowId = 2;

    /// <summary>True only when the person clicked Allow.</summary>
    public static async Task<bool> AskAsync(string text, CancellationToken cancellationToken)
    {
        using var panel = new Win32Panel(new PanelOptions(
            "DeskShare", text,
            new[] { new PanelButton(DenyId, "Deny"), new PanelButton(AllowId, "Allow") },
            Banner: false, HideOnSecureDesktop: false, Width: 480, Height: 190));

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.ButtonClicked += id => { answer.TrySetResult(id == AllowId); panel.Close(); };
        _ = panel.Closed.ContinueWith(_ => answer.TrySetResult(false), TaskScheduler.Default); // X button: Deny
        using var registration = cancellationToken.Register(() => { answer.TrySetResult(false); panel.Close(); });

        if (!panel.Start())
            return false; // cannot show a question: never connect unseen

        return await answer.Task.ConfigureAwait(false);
    }
}
