using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using DeskShare.Core.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace DeskShare.SignalingServer.Services;

/// <summary>
/// Optional ZCLOUD accounts ("My devices"): sign-in through Microsoft Entra External ID, then a user can bind the
/// Server IDs of their own computers to the account and see them listed with their online status.
/// </summary>
/// <remarks>
/// Off unless <c>Accounts:Enabled</c> is true: then nothing here is registered or mapped (the routes answer 404) and
/// the server behaves exactly as before. Accounts never replace the passkey or permanent password: connecting still
/// needs the secret, because the end-to-end binding of the WebRTC session is derived from it.
/// Why JwtBearer (Microsoft.AspNetCore.Authentication.JwtBearer): it is the framework's own validator for OIDC access
/// tokens. It downloads the tenant's signing keys from the OIDC metadata (and refreshes them when Entra rotates them)
/// and checks signature, issuer, audience and lifetime; writing that by hand would be easy to get subtly wrong.
/// </remarks>
public static class AccountEndpoints
{
    /// <summary>Name of the authorization policy every /me route requires.</summary>
    public const string PolicyName = "accounts";

    private static readonly Regex ServerIdRx = new(@"^server-[1-9][0-9]{8}$", RegexOptions.CultureInvariant);
    // Nonces are client-chosen; base64url-ish and bounded so they cannot carry the "|" field separator or bloat the cache.
    private static readonly Regex NonceRx = new(@"^[A-Za-z0-9_-]{16,64}$", RegexOptions.CultureInvariant);
    private const int MaxNameLength = 64;

    /// <summary>True when <c>Accounts:Enabled</c> is set.</summary>
    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue("Accounts:Enabled", false);

    /// <summary>
    /// Registers authentication, the "accounts" policy and rate limit, and the claim storage. Call only when
    /// <see cref="IsEnabled"/>. Fails fast on missing settings, so a half-configured deployment does not start.
    /// </summary>
    public static void AddAccounts(WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var authority = Required(config, "Accounts:Authority").TrimEnd('/');
        var audience = Required(config, "Accounts:Audience");
        var scope = Required(config, "Accounts:Scope");
        var tenantId = TenantIdFromAuthority(authority);

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // External ID metadata lives at <authority>/v2.0/.well-known/openid-configuration.
                options.Authority = authority + "/v2.0";
                // Keep the raw JWT claim names ("oid", "sub", "scp") instead of the long WS-Fed URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // Sign-in goes through the branded host (zcloudcz.ciamlogin.com), but External ID may issue the
                    // token from the tenant-GUID host. Both are this tenant; JwtBearer also adds the metadata's issuer.
                    ValidIssuers = ValidIssuers(authority, tenantId),
                    // v2 access tokens carry the API registration's client id as "aud".
                    ValidAudience = audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(2)
                };
            });

        // A token for some other API of the tenant would fail the audience check; the scope check also rejects a
        // token of this API that was not issued for "access_as_user" (e.g. an app-only token).
        var requiredScope = scope[(scope.LastIndexOf('/') + 1)..];
        builder.Services.AddAuthorization(o => o.AddPolicy(PolicyName, p => p
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => AccountKey(ctx.User) != null &&
                (ctx.User.FindFirst("scp")?.Value ?? "").Split(' ').Contains(requiredScope))));

        var permitLimit = config.GetValue("RateLimiting:Accounts:PermitLimit", 30);
        var windowSeconds = config.GetValue("RateLimiting:Accounts:WindowSeconds", 60);
        builder.Services.AddRateLimiter(o => o.AddPolicy(PolicyName, context =>
            // Per account when signed in (several users behind one NAT do not share a budget), else per IP.
            RateLimitPartition.GetFixedWindowLimiter(
                AccountKey(context.User) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromSeconds(windowSeconds),
                    QueueLimit = 0
                })));

        // Same switch as the session storage, so claims survive restarts wherever sessions do.
        if (config.GetValue("Storage:Type", "InMemory")!.Equals("AzureTableStorage", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = Required(config, "Storage:AzureTableStorage:ConnectionString");
            builder.Services.AddSingleton<IDeviceClaimStore>(_ => new AzureTableDeviceClaimStore(connectionString));
        }
        else
        {
            builder.Services.AddSingleton<IDeviceClaimStore, InMemoryDeviceClaimStore>();
        }
    }

    /// <summary>
    /// Content-Security-Policy addition for the web viewer: MSAL.js talks to the sign-in hosts with fetch, which the
    /// default <c>connect-src 'self' wss: ws:</c> would block.
    /// </summary>
    public static string CspConnectSources(IConfiguration configuration)
    {
        var authority = configuration["Accounts:Authority"]!.TrimEnd('/');
        return $"{new Uri(authority).GetLeftPart(UriPartial.Authority)} https://{TenantIdFromAuthority(authority)}.ciamlogin.com";
    }

    /// <summary>
    /// Maps the account routes. Must run after UseCors and before UseRateLimiter: the rate limiter partitions by the
    /// signed-in account, so authentication has to run first.
    /// </summary>
    public static void MapAccounts(WebApplication app)
    {
        var config = app.Configuration;
        var authority = config["Accounts:Authority"]!.TrimEnd('/');

        // Public: what the clients need to start a sign-in. Clients that get 404 here show no account UI at all.
        app.MapGet("/accounts/config", () => Results.Ok(new
        {
            Authority = authority,
            // MSAL.js accepts tokens only from hosts it knows; External ID uses both (see ValidIssuers).
            KnownAuthorities = new[] { new Uri(authority).Host, $"{TenantIdFromAuthority(authority)}.ciamlogin.com" },
            ClientIdWeb = config["Accounts:ClientIdWeb"] ?? "",
            ClientIdDesktop = config["Accounts:ClientIdDesktop"] ?? "",
            Scope = config["Accounts:Scope"]
        })).RequireRateLimiting(PolicyName);

        // Claim, list and unclaim each read a claim and then write it. One lock makes that a single step, so two
        // accounts claiming the same Server ID at once cannot both win. A few calls per user per day: one lock is plenty.
        // ponytail: single-instance lock (like ConnectionManager's owner claims, which live in memory per instance anyway).
        var claimLock = new SemaphoreSlim(1, 1);

        // Used nonces are remembered a little longer than a proof can be fresh (window in both directions), so a
        // captured claim request cannot be replayed while its timestamp is still accepted. Only requests with a valid
        // proof reach the cache, so strangers cannot fill it.
        var claimNonces = new NonceCache(
            nonceTtl: DeviceClaimProof.FreshnessWindow * 2 + TimeSpan.FromMinutes(1),
            cleanupInterval: TimeSpan.FromMinutes(1));
        app.Lifetime.ApplicationStopped.Register(claimNonces.Dispose);

        var me = app.MapGroup("/me").RequireAuthorization(PolicyName).RequireRateLimiting(PolicyName);

        me.MapGet("/devices", async (ClaimsPrincipal user, IDeviceClaimStore store, IServerSessionStorage sessions, ConnectionManager cm) =>
        {
            var accountKey = AccountKey(user)!;
            var devices = new List<object>();
            // ponytail: one status lookup per device (an account has a handful); batch them if accounts grow to hundreds.
            foreach (var claim in await store.ListAsync(accountKey))
            {
                var status = await sessions.GetServerStatusAsync(claim.ServerId);
                // Online only while the SAME installation that proved the claim owns the Server ID. If the ID lapsed and
                // another installation registered it since, this account must not see that stranger's computer online.
                var owner = cm.CaptureOwner(claim.ServerId);
                var online = status is { IsOnline: true } && owner != null && Fingerprint(owner) == claim.OwnerFingerprint;
                var lastSeen = claim.LastSeenUtc;
                if (online)
                {
                    lastSeen = status!.LastActivity.ToUniversalTime();
                    // Remember it for when the device is off (and after a restart); throttled to save writes.
                    // Conditional LastSeen-only update: this runs without the claim lock, after awaits, so the claim
                    // may have been removed or taken over meanwhile and must not be written back (see TouchLastSeenAsync).
                    if (lastSeen - claim.LastSeenUtc > TimeSpan.FromMinutes(5))
                        await store.TouchLastSeenAsync(claim, lastSeen);
                }
                devices.Add(new { claim.ServerId, claim.Name, Online = online, LastSeen = lastSeen, claim.Unattended });
            }
            // The account key is returned so clients use exactly the value the server checks proofs against.
            return Results.Ok(new { AccountKey = accountKey, Devices = devices });
        });

        me.MapPost("/devices/claim", async (ClaimRequest request, ClaimsPrincipal user, IDeviceClaimStore store, ConnectionManager cm) =>
        {
            var accountKey = AccountKey(user)!;
            var name = (request.Name ?? "").Trim();
            if (request.ServerId is not { } serverId || !ServerIdRx.IsMatch(serverId) ||
                request.Nonce is not { } nonce || !NonceRx.IsMatch(nonce) ||
                string.IsNullOrEmpty(request.Proof) ||
                name.Length > MaxNameLength || name.Any(char.IsControl))
                return Results.BadRequest(new { Error = "Invalid claim request" });

            // Compared as numbers: converting an absurd client value to a date would throw.
            var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var window = (long)DeviceClaimProof.FreshnessWindow.TotalSeconds;
            if (request.Timestamp < nowSeconds - window || request.Timestamp > nowSeconds + window)
                return Results.Json(new { Error = "Claim expired, check the computer's clock" }, statusCode: StatusCodes.Status403Forbidden);

            // The proof key is the owner-secret hash the server holds while the device is registered. A device that is
            // off (or whose claim lapsed) cannot be added: there is nothing to check the proof against.
            var owner = cm.CaptureOwner(serverId);
            if (owner == null)
                return Results.Conflict(new { Error = "The device must be running and connected to be added" });

            if (!DeviceClaimProof.Verify(owner, serverId, accountKey, request.Timestamp, nonce, request.Proof) ||
                !claimNonces.TryUseNonce(nonce))
                return Results.Json(new { Error = "Invalid proof" }, statusCode: StatusCodes.Status403Forbidden);

            var fingerprint = Fingerprint(owner);
            await claimLock.WaitAsync();
            try
            {
                var existing = await store.GetAsync(serverId);
                // Another account holds this very installation: it has to remove it first. A claim made by an earlier
                // installation of the same Server ID (reinstall, computer sold) is stale and the proven owner takes it over.
                if (existing != null && existing.AccountKey != accountKey && existing.OwnerFingerprint == fingerprint)
                    return Results.Conflict(new { Error = "The device belongs to another account" });

                var now = DateTime.UtcNow;
                var claim = new DeviceClaim(serverId, accountKey, name.Length > 0 ? name : serverId, request.Unattended,
                    fingerprint, now, now);
                await store.SaveAsync(claim);
                return Results.Ok(new { claim.ServerId, claim.Name, Online = true, LastSeen = now, claim.Unattended });
            }
            finally
            {
                claimLock.Release();
            }
        });

        me.MapDelete("/devices/{serverId}", async (string serverId, ClaimsPrincipal user, IDeviceClaimStore store) =>
        {
            var accountKey = AccountKey(user)!;
            await claimLock.WaitAsync();
            try
            {
                // 404 also for someone else's device: do not tell a caller which Server IDs other accounts hold.
                var existing = await store.GetAsync(serverId);
                if (existing == null || existing.AccountKey != accountKey)
                    return Results.NotFound();
                await store.DeleteAsync(serverId);
                return Results.NoContent();
            }
            finally
            {
                claimLock.Release();
            }
        });
    }

    /// <summary>
    /// The stable account id: "oid" (the user's object id in the tenant), else "sub". Never the e-mail or name, which
    /// users can change. Restricted to characters that cannot break the "|"-joined proof message.
    /// </summary>
    public static string? AccountKey(ClaimsPrincipal user)
    {
        var key = user.FindFirst("oid")?.Value ?? user.FindFirst("sub")?.Value;
        return key is { Length: > 0 and <= 128 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? key : null;
    }

    /// <summary>
    /// Stored instead of the owner-secret hash: the hash is the claim key (whoever has it can make proofs), so a leaked
    /// table must not contain it. A one-way, domain-separated hash of it still tells installations apart.
    /// </summary>
    public static string Fingerprint(byte[] ownerHash) =>
        Convert.ToBase64String(SHA256.HashData([.. Encoding.UTF8.GetBytes("DeskShare-claim-owner-v1|"), .. ownerHash]));

    /// <summary>The tenant id is the last path segment of the authority (https://host/&lt;tenant-guid&gt;).</summary>
    private static string TenantIdFromAuthority(string authority) => new Uri(authority).AbsolutePath.Trim('/').Split('/')[0];

    private static string[] ValidIssuers(string authority, string tenantId) =>
        [$"https://{tenantId}.ciamlogin.com/{tenantId}/v2.0", authority + "/v2.0"];

    private static string Required(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value ? value
            : throw new InvalidOperationException($"{key} must be set when Accounts:Enabled is true");

    /// <summary>Body of POST /me/devices/claim; see <see cref="DeviceClaimProof"/> for proof, timestamp and nonce.</summary>
    /// <param name="ServerId">The Server ID to add.</param>
    /// <param name="Name">Optional display name (max 64 characters).</param>
    /// <param name="Timestamp">Unix seconds.</param>
    /// <param name="Nonce">16-64 base64url characters, used once.</param>
    /// <param name="Proof">Base64 HMAC, see <see cref="DeviceClaimProof.Compute"/>.</param>
    /// <param name="Unattended">Shown in the list; set by the desktop app when it claims the unattended agent's ID.</param>
    public sealed record ClaimRequest(string? ServerId, string? Name, long Timestamp, string? Nonce, string? Proof, bool Unattended);
}
