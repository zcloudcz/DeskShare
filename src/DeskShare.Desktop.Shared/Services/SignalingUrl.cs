using Microsoft.Extensions.Configuration;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Derives the HTTP base URL (used for /authenticate, /servers/.../status) from the
/// configured WebSocket signaling URL, so the hosted server address lives in one config key.
/// </summary>
public static class SignalingUrl
{
    public const string ClientConfigKey = "Client:SignalingServerUrl";

    /// <summary>"wss://host/signal" → "https://host" (and ws → http).</summary>
    public static string ToHttpBase(string webSocketUrl)
    {
        var uri = new Uri(webSocketUrl);
        var scheme = uri.Scheme == "wss" ? "https" : "http";
        return $"{scheme}://{uri.Authority}";
    }

    public static string HttpBaseFromConfig(IConfiguration configuration)
    {
        var url = configuration.GetValue<string>(ClientConfigKey)
            ?? throw new InvalidOperationException($"'{ClientConfigKey}' is not configured in appsettings.json");
        return ToHttpBase(url);
    }
}
