using DeskShare.Desktop.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;

namespace DeskShare.DesktopAvalonia.Services;

/// <summary>
/// Optional "ZCLOUD Login" sign-in of the desktop app, for "My devices". Nothing here is needed to share or connect:
/// a connection still needs the passkey or permanent password.
/// </summary>
/// <remarks>
/// Why MSAL.NET (Microsoft.Identity.Client): it is Microsoft's client for Entra / External ID. It runs the
/// authorization-code flow with PKCE in the user's own browser and receives the answer on a loopback port
/// (http://localhost:&lt;random port&gt;, the redirect URI registered for public clients), then caches and silently
/// refreshes the tokens. Doing that by hand means an HTTP listener, PKCE, token refresh and their edge cases.
/// The token cache is in memory only, so the user signs in again after an app restart.
/// ponytail: add Microsoft.Identity.Client.Extensions.Msal for a persistent (DPAPI/Keychain/libsecret) cache if
/// signing in once per run turns out to be annoying.
/// </remarks>
public sealed class AccountSession
{
    private readonly AccountApi _api;
    private IPublicClientApplication? _msal;
    private string[] _scopes = [];

    /// <summary>Raised (on any thread) after sign-in or sign-out.</summary>
    public event EventHandler? Changed;

    /// <summary>The server's account settings, null until <see cref="InitializeAsync"/> found accounts enabled.</summary>
    public AccountConfig? Config { get; private set; }

    /// <summary>True when the server offers accounts; otherwise the app shows no account UI.</summary>
    public bool Available => _msal != null;

    /// <summary>Display name of the signed-in account (usually the e-mail), null when signed out.</summary>
    public string? UserName { get; private set; }

    public bool IsSignedIn => UserName != null;

    public AccountApi Api => _api;

    public AccountSession(IConfiguration configuration)
    {
        _api = new AccountApi(new HttpClient
        {
            BaseAddress = new Uri(SignalingUrl.HttpBaseFromConfig(configuration)),
            Timeout = TimeSpan.FromSeconds(10)
        });
    }

    /// <summary>Asks the server whether accounts are on. Safe to call more than once; never throws.</summary>
    public async Task InitializeAsync()
    {
        if (_msal != null) return;
        var config = await _api.GetConfigAsync();
        if (config is null || string.IsNullOrEmpty(config.ClientIdDesktop)) return;

        Config = config;
        _scopes = [config.Scope];
        _msal = PublicClientApplicationBuilder.Create(config.ClientIdDesktop)
            .WithAuthority(config.Authority)
            // Loopback redirect: MSAL picks a free port; Entra accepts any port for a registered "http://localhost".
            .WithRedirectUri("http://localhost")
            .Build();
    }

    /// <summary>Opens the system browser for ZCLOUD Login (MFA, if configured, happens there).</summary>
    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        if (_msal is null) return;
        var result = await _msal.AcquireTokenInteractive(_scopes)
            .WithUseEmbeddedWebView(false) // the user's browser: password managers, passkeys and MFA apps work there
            .ExecuteAsync(cancellationToken);
        UserName = result.Account.Username;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the cached tokens (the browser session at ZCLOUD Login stays as it is).</summary>
    public async Task SignOutAsync()
    {
        if (_msal is null) return;
        foreach (var account in await _msal.GetAccountsAsync())
            await _msal.RemoveAsync(account);
        UserName = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A valid access token for the signaling server's API, refreshed silently when needed.</summary>
    /// <exception cref="InvalidOperationException">Not signed in, or the session ended (sign in again).</exception>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var account = _msal is null ? null : (await _msal.GetAccountsAsync()).FirstOrDefault();
        if (account is null) throw new InvalidOperationException("Not signed in.");
        try
        {
            return (await _msal!.AcquireTokenSilent(_scopes, account).ExecuteAsync(cancellationToken)).AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            await SignOutAsync();
            throw new InvalidOperationException("Your sign-in has expired, please sign in again.");
        }
    }
}
