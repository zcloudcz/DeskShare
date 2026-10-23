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

/// <summary>Pure rules that turn the host's messages into a <see cref="ControlRequestState"/> (kept out of ClientManager so they can be tested).</summary>
public static class ControlRequestStates
{
    public static bool IsRequestable(System.Text.Json.JsonElement response) =>
        response.TryGetProperty("requestable", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

    public static ControlRequestState FromAuthorizationResponse(System.Text.Json.JsonElement response)
    {
        var granted = response.TryGetProperty("authorized", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True;
        var reason = response.TryGetProperty("reason", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String ? r.GetString() : null;
        return granted ? ControlRequestState.Granted
            : reason == "client_update_required" ? ControlRequestState.UpdateRequired
            : reason == "timeout" ? ControlRequestState.TimedOut
            : reason == "denied" ? ControlRequestState.Denied
            : IsRequestable(response) ? ControlRequestState.Requestable
            : ControlRequestState.Unavailable;
    }

    /// <summary>After the host revoked control: ask again if the host takes requests, otherwise nothing is offered.</summary>
    /// <summary>Whether the host accepts control requests, kept across a grant (which answers requestable:false).</summary>
    public static bool NextHostAllowsRequests(System.Text.Json.JsonElement response, bool previous) =>
        IsRequestable(response)
        || (previous && response.TryGetProperty("authorized", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True);

    public static ControlRequestState AfterRevoked(bool hostAllowsRequests) =>
        hostAllowsRequests ? ControlRequestState.Requestable : ControlRequestState.Unavailable;
}
