using System.Net;
using System.Net.Http.Json;
using DeskShare.Core;
using DeskShare.Core.Auth;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// Rotation and reconnect may register at the same time. The server must end up with the newest passkey,
/// which an older request finishing last would otherwise overwrite.
/// </summary>
public sealed class ConcurrentRegistrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskshare-reg-" + Guid.NewGuid().ToString("N"));

    /// <summary>The first request is held back until released; records order and overlap of requests.</summary>
    private sealed class SlowFirstRequestHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _calls;
        public int MaxInFlight;
        public List<string> Passkeys { get; } = new();

        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var registration = (await request.Content!.ReadFromJsonAsync<ServerRegistrationMessage>(ct))!;
            var overlap = Interlocked.Increment(ref _inFlight);
            MaxInFlight = Math.Max(MaxInFlight, overlap);
            lock (Passkeys) Passkeys.Add(registration.Passkey);

            if (Interlocked.Increment(ref _calls) == 1)
                await _releaseFirst.Task; // the older request is slow...

            Interlocked.Decrement(ref _inFlight);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ServerRegistrationResponse { Success = true, ServerId = registration.ServerId })
            };
        }
    }

    [Fact]
    public async Task OverlappingRegistrations_RunOneAtATime_AndTheLastOneSendsTheNewestPasskey()
    {
        Directory.CreateDirectory(_dir);
        var handler = new SlowFirstRequestHandler();
        var config = new ScreenSenderConfiguration { ServerId = "server-123456789", SignalingServerUrl = "ws://localhost:5151/signal" };
        var service = new ScreenSenderService(
            Substitute.For<ILogger<ScreenSenderService>>(), config, new HttpClient(handler),
            Path.Combine(_dir, "server-id"), new ServerOwnerSecretStore(Path.Combine(_dir, "owner.key")));

        var older = service.GenerateNewPasskeyAsync();           // blocks inside the first request
        await Task.Delay(100);
        var newer = service.GenerateNewPasskeyAsync();           // new passkey while the old request is in flight
        await Task.Delay(100);
        handler.ReleaseFirst();
        await Task.WhenAll(older, newer);

        Assert.Equal(1, handler.MaxInFlight);                    // never two requests at once
        Assert.Equal(2, handler.Passkeys.Count);
        // The server's last word is the current passkey, as the authKey derived from it (the server never sees the passkey).
        Assert.Equal(E2EBinding.DeriveKeys(service.CurrentPasskey!, config.ServerId).AuthKey, handler.Passkeys[^1]);
        Assert.DoesNotContain(service.CurrentPasskey!, handler.Passkeys);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
