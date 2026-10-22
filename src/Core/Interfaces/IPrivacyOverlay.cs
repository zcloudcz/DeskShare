namespace DeskShare.Core.Interfaces;

/// <summary>Outcome of <see cref="IPrivacyOverlay.TryShow"/>.</summary>
/// <param name="Shown">True when the local monitors are now black.</param>
/// <param name="Reason">Why it was not shown (<c>Shown</c> false), or a warning when it was (for example <c>input_block_failed</c>).</param>
public readonly record struct PrivacyShowResult(bool Shown, string? Reason = null);

/// <summary>
/// Blacks out the local monitors while the remote viewer keeps seeing the real desktop.
/// Kept as an interface so the on/off state machine (<c>PrivacyScreenController</c>) is testable without a display.
/// </summary>
public interface IPrivacyOverlay : IDisposable
{
    /// <summary>Shows the overlay; with <paramref name="blockInput"/> the local mouse and keyboard are blocked too.</summary>
    PrivacyShowResult TryShow(bool blockInput);

    /// <summary>Removes the overlay and unblocks local input. Safe to call when nothing is shown.</summary>
    void Hide();
}
