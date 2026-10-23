namespace DeskShare.Desktop.Shared.Services;

/// <summary>Where this viewer stands with its request for control of the host (from the host's authorization_response / approval_pending).</summary>
public enum ControlRequestState
{
    /// <summary>The host does not offer control.</summary>
    Unavailable,
    /// <summary>The host's user has to approve; the viewer may ask.</summary>
    Requestable,
    /// <summary>The question is shown to the host's user.</summary>
    Pending,
    /// <summary>Control was granted.</summary>
    Granted,
    /// <summary>The host's user clicked Deny.</summary>
    Denied,
    /// <summary>Nobody answered in time.</summary>
    TimedOut,
    /// <summary>The host needs a newer viewer to be asked for control.</summary>
    UpdateRequired,
}
