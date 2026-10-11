using System.Net;
using System.Net.Http.Json;
using DeskShare.Core;
using DeskShare.Core.Auth;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// Server IDs are only 9 digits, so the signaling server can answer /register with 403 because another
/// installation owns the ID. The sender must then pick a new ID and register again.
/// </summary>
public sealed class ServerIdCollisionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskshare-collision-" + Guid.NewGuid().ToString("N"));

    /// <summary>Answers /register with 403 for the first <c>forbiddenCount</c> calls, then with success.</summary>
    private sealed class FakeSignalingHandler(int forbiddenCount) : HttpMessageHandler
    {
        public List<string> RegisteredServerIds { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var registration = (await request.Content!.ReadFromJsonAsync<ServerRegistrationMessage>(ct))!;
            RegisteredServerIds.Add(registration.ServerId);

            return RegisteredServerIds.Count <= forbiddenCount
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ServerRegistrationResponse { Success = true, ServerId = registration.ServerId })
                };
        }
    }

    private (ScreenSenderService Service, ScreenSenderConfiguration Config, string IdPath) CreateSender(FakeSignalingHandler handler)
    {
        Directory.CreateDirectory(_dir);
        var idPath = Path.Combine(_dir, "server-id");
        var config = new ScreenSenderConfiguration
        {
            ServerId = ServerIdGenerator.GetOrCreateServerId(idPath),
            SignalingServerUrl = "ws://localhost:5151/signal"
        };
        var service = new ScreenSenderService(
            Substitute.For<ILogger<ScreenSenderService>>(), config, new HttpClient(handler), idPath,
            new ServerOwnerSecretStore(Path.Combine(_dir, "owner.key")));
        return (service, config, idPath);
    }

    [Fact]
    public async Task Register_WhenTheServerIdIsTaken_UsesAndPersistsANewId_AndRaisesServerIdChanged()
    {
        var handler = new FakeSignalingHandler(forbiddenCount: 2);
        var (service, config, idPath) = CreateSender(handler);
        var original = config.ServerId;
        var announced = new List<string>();
        service.ServerIdChanged += (_, id) => announced.Add(id);

        await service.GenerateNewPasskeyAsync(allowServerIdChange: true);

        Assert.Equal(3, handler.RegisteredServerIds.Count);                  // 2 refusals + 1 success
        Assert.Equal(handler.RegisteredServerIds[^1], service.ServerId);     // the accepted ID is the one in use
        Assert.Equal(service.ServerId, File.ReadAllText(idPath));            // ... and survives a restart
        Assert.Equal(2, announced.Count);
        Assert.Equal(original, handler.RegisteredServerIds[0]);
    }

    [Fact]
    public async Task Register_GivesUpAfterFiveAttempts()
    {
        var handler = new FakeSignalingHandler(forbiddenCount: int.MaxValue);
        var (service, _, _) = CreateSender(handler);

        await service.GenerateNewPasskeyAsync(allowServerIdChange: true);

        Assert.Equal(5, handler.RegisteredServerIds.Count);
    }

    [Fact]
    public async Task Register_AfterTheFirstRegistration_NeverChangesTheServerId()
    {
        // The running WebRTC session is bound to the ID, so a later 403 must not swap it.
        var handler = new FakeSignalingHandler(forbiddenCount: int.MaxValue);
        var (service, config, _) = CreateSender(handler);
        var original = config.ServerId;

        await service.GenerateNewPasskeyAsync();

        Assert.Single(handler.RegisteredServerIds);
        Assert.Equal(original, service.ServerId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
