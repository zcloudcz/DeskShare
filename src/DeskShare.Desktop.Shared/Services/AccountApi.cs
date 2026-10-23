using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>What the signaling server says about its optional accounts (GET /accounts/config).</summary>
public sealed record AccountConfig(string Authority, string[] KnownAuthorities, string ClientIdWeb, string ClientIdDesktop, string Scope);

/// <summary>One entry of "My devices".</summary>
public sealed record AccountDevice(string ServerId, string Name, bool Online, DateTime LastSeen, bool Unattended);

/// <summary>GET /me/devices: the account key is what claim proofs must be made for.</summary>
public sealed record AccountDevices(string AccountKey, List<AccountDevice> Devices);

/// <summary>
/// HTTP calls of the "My devices" feature (see SignalingServer AccountEndpoints). Sign-in itself is not here: the
/// caller passes the access token, so this class has no dependency on MSAL and can be tested against the real server.
/// </summary>
public sealed class AccountApi
{
    private readonly HttpClient _http;

    /// <param name="http">Client whose BaseAddress is the signaling server's HTTP base (see SignalingUrl).</param>
    public AccountApi(HttpClient http) => _http = http;

    /// <summary>The server's account settings, or null when accounts are off (404) or the server is unreachable:
    /// then the app shows no account UI at all.</summary>
    public async Task<AccountConfig?> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("/accounts/config", cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<AccountConfig>(cancellationToken)
                : null;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; } // timeout
    }

    /// <summary>The signed-in account's devices.</summary>
    public async Task<AccountDevices> GetDevicesAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "/me/devices", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<AccountDevices>(cancellationToken))!;
    }

    /// <summary>
    /// Adds a device and returns its Server ID. <paramref name="prove"/> gets (account key, unix seconds, nonce) and
    /// returns the Server ID with its proof: made with the app's own owner secret, or by the elevated helper for the
    /// unattended agent (only the helper knows that agent's Server ID and secret). The account key is asked from the
    /// server first, so the proof is made for exactly the value the server checks.
    /// </summary>
    public async Task<string> ClaimAsync(string accessToken, string name, bool unattended,
        Func<string, long, string, Task<(string ServerId, string Proof)>> prove, CancellationToken cancellationToken = default)
    {
        var accountKey = (await GetDevicesAsync(accessToken, cancellationToken)).AccountKey;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = NewNonce();
        var (serverId, proof) = await prove(accountKey, timestamp, nonce);

        using var request = Request(HttpMethod.Post, "/me/devices/claim", accessToken);
        request.Content = JsonContent.Create(new { serverId, name, timestamp, nonce, proof, unattended });
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return serverId;
    }

    /// <summary>Removes a device from the account.</summary>
    public async Task RemoveAsync(string accessToken, string serverId, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Delete, "/me/devices/" + Uri.EscapeDataString(serverId), accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound) // already gone: nothing to do
            await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>22 random base64url characters (the server accepts 16-64 of [A-Za-z0-9_-]).</summary>
    public static string NewNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpRequestMessage Request(HttpMethod method, string path, string accessToken) =>
        new(method, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) } };

    // The server answers errors as { "error": "..." }; show that text instead of a bare status code.
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try { message = (await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken))?.Error; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { }
        throw new InvalidOperationException(message ?? $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private sealed record ErrorBody(string? Error);
}
