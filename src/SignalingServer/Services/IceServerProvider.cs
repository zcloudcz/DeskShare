using DeskShare.Core.Auth;

namespace DeskShare.SignalingServer.Services;

/// <summary>
/// Single place that builds the ICE server list returned to clients and senders:
/// static <c>IceServers</c> from configuration plus Cloudflare TURN when it is configured.
/// </summary>
public sealed class IceServerProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<IceServerProvider> _logger;
    private readonly CloudflareTurnCredentialService? _cloudflare;

    /// <summary>Cloudflare is only registered when KeyId and ApiToken are configured, hence optional.</summary>
    public IceServerProvider(
        IConfiguration configuration,
        ILogger<IceServerProvider> logger,
        CloudflareTurnCredentialService? cloudflare = null)
    {
        _configuration = configuration;
        _logger = logger;
        _cloudflare = cloudflare;
    }

    /// <summary>Static configured servers plus Cloudflare TURN (if configured and reachable).</summary>
    public async Task<List<IceServerInfo>> GetIceServersAsync(CancellationToken cancellationToken)
    {
        var servers = _configuration.GetSection("IceServers")
            .GetChildren()
            .Where(s => !string.IsNullOrEmpty(s["Urls"]))
            .Select(s => new IceServerInfo
            {
                Urls = [s["Urls"]!],
                Username = string.IsNullOrEmpty(s["Username"]) ? null : s["Username"],
                Credential = string.IsNullOrEmpty(s["Username"]) ? null : s["Credential"]
            })
            .ToList();

        if (_cloudflare != null)
        {
            try
            {
                servers.Add(await _cloudflare.GetCredentialsAsync(cancellationToken));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Exception type only: messages/URLs may reveal the key id. P2P/STUN still works without TURN.
                _logger.LogWarning("Cloudflare TURN credentials unavailable ({ExceptionType}), using static ICE servers only",
                    ex.GetType().Name);
            }
        }

        return servers;
    }
}
