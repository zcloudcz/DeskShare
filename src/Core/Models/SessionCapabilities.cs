namespace DeskShare.Core.Models;

/// <summary>
/// What a connected viewer is allowed to do in a session. The host grants these; every data channel
/// message must map to exactly one of them (see <c>DataChannelPolicy</c>).
/// </summary>
/// <remarks>
/// One session-level policy instead of per-handler flags: a viewer connected "to look" must not be able
/// to reach the input or clipboard handler just because one handler forgot its own check.
/// </remarks>
[Flags]
public enum SessionCapabilities
{
    /// <summary>Nothing granted.</summary>
    None = 0,

    /// <summary>Watch the screen and ask whether remote control is available.</summary>
    View = 1,

    /// <summary>Send mouse and keyboard input.</summary>
    Control = 2,

    /// <summary>Exchange clipboard content.</summary>
    Clipboard = 4,

    /// <summary>Send files to the host (viewer to host only).</summary>
    FileTransfer = 8,

    /// <summary>Black out the host's local monitors while the viewer still sees the desktop (needs Control too).</summary>
    PrivacyScreen = 16
}
