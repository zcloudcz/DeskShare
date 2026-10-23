using System.Text.Json;
using DeskShare.Desktop.Shared.Services;

namespace DeskShare.UnitTests.Safety;

/// <summary>How the desktop viewer turns the host's messages into the state of its "Request control" button.</summary>
public sealed class ControlRequestStatesTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("""{"authorized":true}""", ControlRequestState.Granted)]
    [InlineData("""{"authorized":false,"requestable":true}""", ControlRequestState.Requestable)]
    [InlineData("""{"authorized":false,"requestable":false}""", ControlRequestState.Unavailable)]
    [InlineData("""{"authorized":false,"requestable":true,"reason":"denied"}""", ControlRequestState.Denied)]
    [InlineData("""{"authorized":false,"reason":"timeout"}""", ControlRequestState.TimedOut)]
    [InlineData("""{"authorized":false,"reason":"client_update_required"}""", ControlRequestState.UpdateRequired)]
    public void AuthorizationResponse_MapsToState(string json, ControlRequestState expected) =>
        Assert.Equal(expected, ControlRequestStates.FromAuthorizationResponse(Json(json)));

    [Fact]
    public void HostStopsControl_ViewerCanAskAgain_WhenTheHostTakesRequests()
    {
        // granted earlier, with a host that asks its user ("requestable" was announced by the probe answer)
        var requestable = ControlRequestStates.IsRequestable(Json("""{"authorized":false,"requestable":true}"""));

        Assert.Equal(ControlRequestState.Requestable, ControlRequestStates.AfterRevoked(requestable));
    }

    [Fact]
    public void HostStopsControl_WithoutRequests_NothingIsOffered() =>
        Assert.Equal(ControlRequestState.Unavailable, ControlRequestStates.AfterRevoked(false));

    [Fact]
    public void ProbeThenGrantThenRevoke_KeepsTheRequestButton()
    {
        static System.Text.Json.JsonElement J(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;
        var allows = ControlRequestStates.NextHostAllowsRequests(J("""{"authorized":false,"requestable":true}"""), false);
        allows = ControlRequestStates.NextHostAllowsRequests(J("""{"authorized":true,"requestable":false}"""), allows);

        Assert.True(allows);
        Assert.Equal(ControlRequestState.Requestable, ControlRequestStates.AfterRevoked(allows));
    }
}
