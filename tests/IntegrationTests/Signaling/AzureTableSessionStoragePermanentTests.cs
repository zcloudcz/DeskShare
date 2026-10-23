using DeskShare.Core.Auth;
using DeskShare.SignalingServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskShare.IntegrationTests.Signaling;

/// <summary>Runs only against a real Table endpoint: DESKSHARE_AZURITE = connection string of an Azurite instance.</summary>
public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKSHARE_AZURITE")))
            Skip = "Set DESKSHARE_AZURITE to an Azurite connection string (docker run mcr.microsoft.com/azure-storage/azurite)";
    }
}

/// <summary>The Azure Table backend stores the permanent password's authKey (hashed) and accepts it like the passkey.</summary>
public class AzureTableSessionStoragePermanentTests
{
    private const string PasskeyAuth = "PASSKEYAUTH000000000000000000000000000000000";
    private const string PermanentAuth = "Perm-Auth_000000000000000000000000000000000";

    [AzuriteFact]
    public async Task PermanentKey_RoundTrips_AndIsClearedByARegistrationWithoutIt()
    {
        var storage = new AzureTableSessionStorage(Environment.GetEnvironmentVariable("DESKSHARE_AZURITE")!,
            NullLogger<AzureTableSessionStorage>.Instance);
        var serverId = "server-" + Random.Shared.Next(100_000_000, 1_000_000_000);
        const string clientId = "client-azurite"; // one viewer throughout: the backend reserves the session for it

        Task Register(string? permanent) => storage.RegisterServerAsync(new ServerRegistrationMessage
        {
            ServerId = serverId, Passkey = PasskeyAuth, PermanentAuthKey = permanent, OwnerSecret = "secret-1",
            ValidTo = DateTime.UtcNow.AddMinutes(1), RemoteControlEnabled = false, TrustClientPermanent = false
        });
        async Task<bool> Auth(string credential) => (await storage.AuthenticateClientAsync(new ClientAuthenticationMessage
        {
            ServerId = serverId, ClientId = clientId, Passkey = credential
        })).Success;

        await Register(PermanentAuth);
        Assert.True(await Auth(PermanentAuth));
        Assert.True(await Auth(PasskeyAuth));
        Assert.False(await Auth("Wrong-Auth_00000000000000000000000000000000"));

        // The owner removed the password: the next registration must clear the stored hash (the upsert merges).
        await Register(null);
        Assert.False(await Auth(PermanentAuth));
        Assert.True(await Auth(PasskeyAuth));
    }
}
