using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using DeskShare.Core.Auth;

namespace DeskShare.SignalingServer.Services;

/// <summary>
/// Mints TURN credentials from Cloudflare Realtime TURN (same Cloudflare TURN key as our messenger "Jay").
/// </summary>
/// <remarks>
/// POST {baseUrl}/v1/turn/keys/{keyId}/credentials/generate with a Bearer API token and body {"ttl": N}.
/// The API token never leaves the server; clients only get the short-lived username/credential pair.
/// Never log the token or response bodies: Cloudflare error payloads can echo request details.
/// </remarks>
public sealed class CloudflareTurnCredentialService
{
    /// <summary>Named HttpClient so tests can plug in a fake handler.</summary>
    public const string HttpClientName = "cloudflare-turn";

    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _time;
    private readonly string _apiToken;
    private readonly int _ttlSeconds;
    private readonly Uri _endpoint;

    // ponytail: ONE shared credential set is handed to every client and sender until half its TTL has passed.
    // Cloudflare bills/limits per key, not per credential, so this is fine for a small user base. Mint per-session
    // credentials instead if abuse of a leaked credential (shared by everyone) ever matters.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile CachedCredential? _cached;

    private sealed record CachedCredential(IceServerInfo Server, DateTimeOffset RefreshAfter);

    /// <summary>Reads Turn:Cloudflare:* settings; throws if KeyId or ApiToken is missing.</summary>
    public CloudflareTurnCredentialService(
        IHttpClientFactory httpFactory, IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        _httpFactory = httpFactory;
        _time = timeProvider ?? TimeProvider.System;

        var keyId = configuration["Turn:Cloudflare:KeyId"]
            ?? throw new InvalidOperationException("Turn:Cloudflare:KeyId is not configured.");
        _apiToken = configuration["Turn:Cloudflare:ApiToken"]
            ?? throw new InvalidOperationException("Turn:Cloudflare:ApiToken is not configured.");
        _ttlSeconds = configuration.GetValue("Turn:Cloudflare:TtlSeconds", 86400);

        // Overridable so tests (and a future regional endpoint) need no code change.
        var baseUrl = configuration["Turn:Cloudflare:BaseUrl"] ?? "https://rtc.live.cloudflare.com";
        _endpoint = new Uri($"{baseUrl.TrimEnd('/')}/v1/turn/keys/{Uri.EscapeDataString(keyId)}/credentials/generate");
    }

    /// <summary>
    /// Returns TURN credentials, reusing the last issued set while more than half of its TTL remains
    /// (senders re-register every 45 s; one Cloudflare call per request would be wasteful).
    /// Throws if Cloudflare is unreachable or answers with something unexpected.
    /// </summary>
    public async Task<IceServerInfo> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        var cached = _cached;
        if (cached != null && _time.GetUtcNow() < cached.RefreshAfter)
            return cached.Server;

        // Only one caller refreshes; the others wait and then reuse the fresh result.
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            cached = _cached;
            if (cached != null && _time.GetUtcNow() < cached.RefreshAfter)
                return cached.Server;

            var server = await RequestCredentialsAsync(cancellationToken);
            _cached = new CachedCredential(server, _time.GetUtcNow().AddSeconds(_ttlSeconds / 2.0));
            return server;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IceServerInfo> RequestCredentialsAsync(CancellationToken cancellationToken)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        request.Content = JsonContent.Create(new { ttl = _ttlSeconds });

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Cloudflare TURN returned {(int)response.StatusCode}");

        var payload = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken: cancellationToken);
        var ice = payload?.IceServers;
        if (ice is null || ice.Urls is not { Length: > 0 } ||
            string.IsNullOrEmpty(ice.Username) || string.IsNullOrEmpty(ice.Credential))
            throw new InvalidOperationException("Cloudflare TURN returned an unexpected response.");

        return new IceServerInfo { Urls = ice.Urls, Username = ice.Username, Credential = ice.Credential };
    }

    // `iceServers` is a single object on the /credentials/generate route (an array only on generate-ice-servers).
    private sealed record GenerateResponse([property: JsonPropertyName("iceServers")] CloudflareIceServers? IceServers);

    private sealed record CloudflareIceServers(
        [property: JsonPropertyName("urls")] string[]? Urls,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("credential")] string? Credential);
}
