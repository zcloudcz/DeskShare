namespace DeskShare.Core.Interfaces;

/// <summary>Outcome of <see cref="IPrivacyOverlay.TryShow"/>.</summary>
/// <param name="Shown">True when the local monitors are now black.</param>
/// <param name="Generation">Identifies this show; <see cref="IPrivacyOverlay.Failed"/> repeats it so a late failure of an older show can be ignored.</param>
/// <param name="Reason">Why it was not shown (<c>Shown</c> false), or a warning when it was (for example <c>input_block_failed</c>).</param>
public readonly record struct PrivacyShowResult(bool Shown, string? Reason = null, int Generation = 0);

/// <summary>
/// Blacks out the local monitors while the remote viewer keeps seeing the real desktop.
/// Kept as an interface so the on/off state machine (<c>PrivacyScreenController</c>) is testable without a display.
/// </summary>
public interface IPrivacyOverlay : IDisposable
{
    /// <summary>Shows the overlay; with <paramref name="blockInput"/> the local mouse and keyboard are blocked too.</summary>
    PrivacyShowResult TryShow(bool blockInput);

    /// <summary>Raised (on any thread) when the overlay had to remove itself, for example after a monitor change it could not cover. Input is unblocked by then.</summary>
    event Action<int, string>? Failed;

    /// <summary>Removes the overlay and unblocks local input. Safe to call when nothing is shown.</summary>
    void Hide();
}
