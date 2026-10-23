using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using DeskShare.Core.Auth;
using DeskShare.SignalingServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DeskShare.IntegrationTests.Signaling;

/// <summary>
/// Hosts the server with Accounts:Enabled and a test token issuer: an RSA key made here signs the tokens, and
/// JwtBearer is given that key instead of downloading the tenant's OIDC metadata (nothing leaves the process).
/// </summary>
public sealed class AccountsServerFactory : WebApplicationFactory<Program>
{
    public const string TenantId = "11111111-2222-3333-4444-555555555555";
    public const string Audience = "test-api-client-id";
    // The GUID-host issuer form, which External ID uses for issued tokens (the configured authority uses another host).
    public const string Issuer = $"https://{TenantId}.ciamlogin.com/{TenantId}/v2.0";

    private static readonly Dictionary<string, string> Settings = new()
    {
        ["Accounts__Enabled"] = "true",
        ["Accounts__Authority"] = $"https://test.ciamlogin.com/{TenantId}",
        ["Accounts__Audience"] = Audience,
        ["Accounts__Scope"] = "api://deskshare/access_as_user",
        ["Accounts__ClientIdWeb"] = "web-client-id",
        ["Accounts__ClientIdDesktop"] = "desktop-client-id",
        ["RateLimiting__Accounts__PermitLimit"] = "1000",
    };

    public RsaSecurityKey SigningKey { get; } = new(RSA.Create(2048)) { KeyId = "test-key" };

    public AccountsServerFactory()
    {
        // Program.cs reads Accounts:* while it builds the services, before any WebApplicationFactory hook runs, so the
        // settings must be environment variables. They are set only while this host is built and then restored: the
        // shared ServerFactory (same test collection, so never built concurrently) must keep running with accounts off.
        var previous = Settings.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (k, v) in Settings) Environment.SetEnvironmentVariable(k, v);
        try { _ = Server; }
        finally { foreach (var (k, v) in previous) Environment.SetEnvironmentVariable(k, v); }
    }

    /// <summary>The claim store the server uses: the real in-memory one, with a pause point for race tests.</summary>
    public GatedClaimStore Store { get; } = new(new InMemoryDeviceClaimStore());

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDeviceClaimStore>(Store);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                config.SigningKeys.Add(SigningKey);
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
            });
        });

    public string Token(string oid, string audience = Audience, string scope = "access_as_user", DateTime? expires = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("oid", oid), new Claim("sub", "pairwise-" + oid), new Claim("scp", scope)]),
            NotBefore = (expires ?? DateTime.UtcNow.AddHours(1)).AddHours(-2),
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
        });

    public HttpClient ClientFor(string token)
    {
        var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
}

/// <summary>
/// Decorator over a real claim store. When armed, the next ListAsync (GET /me/devices) returns its snapshot and then
/// waits until the test releases it, so other requests can run in that exact gap.
/// </summary>
public sealed class GatedClaimStore(IDeviceClaimStore inner) : IDeviceClaimStore
{
    private TaskCompletionSource? _reached, _release;

    /// <summary>Arms the gate; the returned tasks: "a list call is paused" and the release switch.</summary>
    public (Task Reached, TaskCompletionSource Release) Arm()
    {
        _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (_reached.Task, _release);
    }

    public async Task<IReadOnlyList<DeviceClaim>> ListAsync(string accountKey)
    {
        var snapshot = await inner.ListAsync(accountKey);
        if (Interlocked.Exchange(ref _reached, null) is { } reached)
        {
            reached.SetResult();
            await _release!.Task;
        }
        return snapshot;
    }

    public Task<DeviceClaim?> GetAsync(string serverId) => inner.GetAsync(serverId);
    public Task SaveAsync(DeviceClaim claim) => inner.SaveAsync(claim);
    public Task DeleteAsync(string serverId) => inner.DeleteAsync(serverId);
    public Task TouchLastSeenAsync(DeviceClaim seen, DateTime lastSeenUtc) => inner.TouchLastSeenAsync(seen, lastSeenUtc);
}

[Collection(SignalingServerCollection.Name)]
public class AccountEndpointTests : IClassFixture<AccountsServerFactory>
{
    private readonly AccountsServerFactory _accounts;
    private readonly SignalingEndpointTests.ServerFactory _accountsOff;

    public AccountEndpointTests(AccountsServerFactory accounts, SignalingEndpointTests.ServerFactory accountsOff)
    {
        _accounts = accounts;
        _accountsOff = accountsOff;
    }

    private static string NewServerId() => "server-" + Random.Shared.Next(100_000_000, 1_000_000_000);
    private static string NewOid() => Guid.NewGuid().ToString();
    private static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    private async Task RegisterAsync(string serverId, string ownerSecret)
    {
        var response = await _accounts.CreateClient().PostAsJsonAsync("/register", new ServerRegistrationMessage
        {
            ServerId = serverId,
            Passkey = "PASSKEYAUTH000000000000000000000000000000000",
            ValidTo = DateTime.UtcNow.AddMinutes(1),
            RemoteControlEnabled = false,
            TrustClientPermanent = false,
            OwnerSecret = ownerSecret
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static object ClaimBody(string serverId, string accountKey, string ownerSecret, long? timestamp = null, string? nonce = null)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        nonce ??= NewNonce();
        return new
        {
            serverId,
            name = "Office PC",
            timestamp = ts,
            nonce,
            proof = DeviceClaimProof.Compute(DeviceClaimProof.ClaimKey(ownerSecret), serverId, accountKey, ts, nonce),
            unattended = false
        };
    }

    private static async Task<JsonElement> DevicesAsync(HttpClient http)
    {
        var response = await http.GetAsync("/me/devices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task FeatureOff_RoutesDoNotExist()
    {
        var http = _accountsOff.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accounts.Token(NewOid()));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/me/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/accounts/config")).StatusCode);
    }

    [Fact]
    public async Task Config_IsPublic_AndNamesBothSignInHosts()
    {
        var config = await _accounts.CreateClient().GetFromJsonAsync<JsonElement>("/accounts/config");
        Assert.Equal("web-client-id", config.GetProperty("clientIdWeb").GetString());
        Assert.Equal("api://deskshare/access_as_user", config.GetProperty("scope").GetString());
        var hosts = config.GetProperty("knownAuthorities").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("test.ciamlogin.com", hosts);
        Assert.Contains($"{AccountsServerFactory.TenantId}.ciamlogin.com", hosts);
    }

    [Fact]
    public async Task Devices_RequireAValidTokenForThisApiAndScope()
    {
        var http = _accounts.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/me/devices")).StatusCode);

        var oid = NewOid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _accounts.ClientFor(_accounts.Token(oid, audience: "other-api")).GetAsync("/me/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _accounts.ClientFor(_accounts.Token(oid, expires: DateTime.UtcNow.AddMinutes(-10))).GetAsync("/me/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _accounts.ClientFor(_accounts.Token(oid, scope: "something_else")).GetAsync("/me/devices")).StatusCode);

        var devices = await DevicesAsync(_accounts.ClientFor(_accounts.Token(oid)));
        Assert.Equal(oid, devices.GetProperty("accountKey").GetString());
        Assert.Equal(0, devices.GetProperty("devices").GetArrayLength());
    }

    [Fact]
    public async Task Claim_WithValidProof_ListsTheDeviceOnline_AndUnclaimRemovesIt()
    {
        var oid = NewOid();
        var http = _accounts.ClientFor(_accounts.Token(oid));
        var serverId = NewServerId();
        await RegisterAsync(serverId, "owner-secret-1");

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/me/devices/claim", ClaimBody(serverId, oid, "owner-secret-1"))).StatusCode);

        var device = Assert.Single((await DevicesAsync(http)).GetProperty("devices").EnumerateArray());
        Assert.Equal(serverId, device.GetProperty("serverId").GetString());
        Assert.Equal("Office PC", device.GetProperty("name").GetString());
        Assert.True(device.GetProperty("online").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/me/devices/{serverId}")).StatusCode);
        Assert.Equal(0, (await DevicesAsync(http)).GetProperty("devices").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/me/devices/{serverId}")).StatusCode);
    }

    [Fact]
    public async Task Claim_IsRejected_ForWrongExpiredOrReplayedProof()
    {
        var oid = NewOid();
        var http = _accounts.ClientFor(_accounts.Token(oid));
        var serverId = NewServerId();
        await RegisterAsync(serverId, "owner-secret-1");

        // Someone who knows the Server ID but not the owner secret.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/me/devices/claim", ClaimBody(serverId, oid, "guessed-secret"))).StatusCode);
        // A proof made for another account is worthless for this one.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/me/devices/claim", ClaimBody(serverId, NewOid(), "owner-secret-1"))).StatusCode);
        // Outside the 5 minute window.
        var old = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds();
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/me/devices/claim", ClaimBody(serverId, oid, "owner-secret-1", old))).StatusCode);

        var body = ClaimBody(serverId, oid, "owner-secret-1");
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/me/devices/claim", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/me/devices/claim", body)).StatusCode);
    }

    [Fact]
    public async Task Claim_OfADeviceOfAnotherAccount_IsRejected_AndItCannotUnclaimIt()
    {
        var owner = NewOid();
        var stranger = NewOid();
        var serverId = NewServerId();
        await RegisterAsync(serverId, "owner-secret-1");
        Assert.Equal(HttpStatusCode.OK, (await _accounts.ClientFor(_accounts.Token(owner))
            .PostAsJsonAsync("/me/devices/claim", ClaimBody(serverId, owner, "owner-secret-1"))).StatusCode);

        // Even with a valid proof (e.g. someone else with access to that computer): first the owner has to remove it.
        var strangerHttp = _accounts.ClientFor(_accounts.Token(stranger));
        Assert.Equal(HttpStatusCode.Conflict, (await strangerHttp.PostAsJsonAsync("/me/devices/claim", ClaimBody(serverId, stranger, "owner-secret-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerHttp.DeleteAsync($"/me/devices/{serverId}")).StatusCode);
        Assert.Single((await DevicesAsync(_accounts.ClientFor(_accounts.Token(owner)))).GetProperty("devices").EnumerateArray());
    }

    [Fact]
    public async Task Claim_OfADeviceThatIsNotRunning_IsRejected()
    {
        var oid = NewOid();
        var response = await _accounts.ClientFor(_accounts.Token(oid))
            .PostAsJsonAsync("/me/devices/claim", ClaimBody(NewServerId(), oid, "owner-secret-1"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DesktopAccountApi_ClaimsListsAndRemoves_AgainstTheRealServer()
    {
        var api = new DeskShare.Desktop.Shared.Services.AccountApi(_accounts.CreateClient());
        Assert.Equal("desktop-client-id", (await api.GetConfigAsync())!.ClientIdDesktop);
        Assert.Null(await new DeskShare.Desktop.Shared.Services.AccountApi(_accountsOff.CreateClient()).GetConfigAsync());

        var oid = NewOid();
        var token = _accounts.Token(oid);
        var serverId = NewServerId();
        await RegisterAsync(serverId, "owner-secret-1");
        var key = DeviceClaimProof.ClaimKey("owner-secret-1");

        Assert.Equal(serverId, await api.ClaimAsync(token, "Laptop", unattended: true,
            (accountKey, ts, nonce) => Task.FromResult((serverId, DeviceClaimProof.Compute(key, serverId, accountKey, ts, nonce)))));
        var device = Assert.Single((await api.GetDevicesAsync(token)).Devices);
        Assert.Equal(("Laptop", true, true), (device.Name, device.Online, device.Unattended));

        // A server error surfaces as its message, e.g. a wrong proof.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => api.ClaimAsync(token, "x", false,
            (_, _, _) => Task.FromResult((serverId, Convert.ToBase64String(new byte[32])))));
        Assert.Equal("Invalid proof", ex.Message);

        await api.RemoveAsync(token, serverId);
        Assert.Empty((await api.GetDevicesAsync(token)).Devices);
    }

    [Fact]
    public async Task ListThatWaitedDuringRemoveAndReclaim_DoesNotRestoreTheOldOwner()
    {
        // Account A's claim with an old LastSeen and the device online, so the list call wants to update LastSeen.
        var a = NewOid();
        var b = NewOid();
        var serverId = NewServerId();
        await RegisterAsync(serverId, "owner-secret-1");
        var fingerprint = AccountEndpoints.Fingerprint(DeviceClaimProof.ClaimKey("owner-secret-1"));
        var old = DateTime.UtcNow.AddHours(-1);
        await _accounts.Store.SaveAsync(new DeviceClaim(serverId, a, "A's PC", false, fingerprint, old, old));

        // A's list call reads its snapshot and pauses; meanwhile A removes the device and B claims it with a proof.
        var (reached, release) = _accounts.Store.Arm();
        var aHttp = _accounts.ClientFor(_accounts.Token(a));
        var list = aHttp.GetAsync("/me/devices");
        await reached.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.NoContent, (await aHttp.DeleteAsync($"/me/devices/{serverId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _accounts.ClientFor(_accounts.Token(b))
            .PostAsJsonAsync("/me/devices/claim", ClaimBody(serverId, b, "owner-secret-1"))).StatusCode);
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await list.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);

        // The stale snapshot must not have been written back: B still owns the device.
        Assert.Equal(b, (await _accounts.Store.GetAsync(serverId))!.AccountKey);
    }

    [Fact]
    public async Task AnonymousFlow_IsUnchanged_WithAccountsOn()
    {
        var serverId = NewServerId();
        await RegisterAsync(serverId, "owner-secret-1");
        var now = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();
        const string passkey = "PASSKEYAUTH000000000000000000000000000000000";
        var response = await _accounts.CreateClient().PostAsJsonAsync("/authenticate", new ClientAuthenticationMessage
        {
            ServerId = serverId,
            Passkey = passkey,
            ClientId = "client-" + Guid.NewGuid().ToString("N"),
            Timestamp = now,
            Nonce = nonce,
            Signature = RequestSigningService.SignRequest(serverId, passkey, now, nonce)
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>The claim stores: Azure Table round-trip (opt-in, like the other Azurite tests) and TouchLastSeen rules.</summary>
public class AzureTableDeviceClaimStoreTests
{
    // A stale "seen online" snapshot (read before a remove and another account's claim) must never re-create or
    // re-assign the claim; a current one only moves LastSeen.
    private static async Task TouchLastSeen_ChangesOnlyLastSeenOfTheSameClaim(IDeviceClaimStore store)
    {
        var serverId = "server-" + Random.Shared.Next(100_000_000, 1_000_000_000);
        var t0 = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var snapshotA = new DeviceClaim(serverId, "acct-a", "A", false, "fp", t0, t0);
        await store.SaveAsync(snapshotA);

        await store.TouchLastSeenAsync(snapshotA, t0.AddHours(1));
        Assert.Equal(snapshotA with { LastSeenUtc = t0.AddHours(1) }, await store.GetAsync(serverId));

        await store.DeleteAsync(serverId);
        await store.TouchLastSeenAsync(snapshotA, t0.AddHours(2));
        Assert.Null(await store.GetAsync(serverId)); // not re-created

        var claimB = new DeviceClaim(serverId, "acct-b", "B", false, "fp", t0, t0);
        await store.SaveAsync(claimB);
        await store.TouchLastSeenAsync(snapshotA, t0.AddHours(3));
        Assert.Equal(claimB, await store.GetAsync(serverId)); // owner and LastSeen untouched
        await store.DeleteAsync(serverId);
    }

    [Fact]
    public Task InMemory_TouchLastSeen_ChangesOnlyLastSeenOfTheSameClaim() =>
        TouchLastSeen_ChangesOnlyLastSeenOfTheSameClaim(new InMemoryDeviceClaimStore());

    [AzuriteFact]
    public Task Azure_TouchLastSeen_ChangesOnlyLastSeenOfTheSameClaim() =>
        TouchLastSeen_ChangesOnlyLastSeenOfTheSameClaim(new AzureTableDeviceClaimStore(Environment.GetEnvironmentVariable("DESKSHARE_AZURITE")!));

    [AzuriteFact]
    public async Task Claim_RoundTrips_ListsPerAccount_AndDeletes()
    {
        var store = new AzureTableDeviceClaimStore(Environment.GetEnvironmentVariable("DESKSHARE_AZURITE")!);
        var account = "acct-" + Guid.NewGuid().ToString("N");
        var serverId = "server-" + Random.Shared.Next(100_000_000, 1_000_000_000);
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var claim = new DeviceClaim(serverId, account, "Office PC", true, "fp", now, now);

        await store.SaveAsync(claim);
        Assert.Equal(claim, await store.GetAsync(serverId));
        Assert.Equal(claim, Assert.Single(await store.ListAsync(account)));
        Assert.Empty(await store.ListAsync("someone-else"));

        await store.DeleteAsync(serverId);
        Assert.Null(await store.GetAsync(serverId));
    }
}
