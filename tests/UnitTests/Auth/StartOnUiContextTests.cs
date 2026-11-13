using System.Net;
using System.Net.Http.Json;
using DeskShare.Core;
using DeskShare.Core.Auth;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// The desktop app starts the sender from its UI thread. Start-up after the first registration (capture probe,
/// WebRTC set-up) blocks, so it must not continue on the caller's SynchronizationContext: on Linux that froze the
/// Avalonia window, and with it the passkey rotation.
/// </summary>
public sealed class StartOnUiContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskshare-uictx-" + Guid.NewGuid().ToString("N"));

    /// <summary>Stands in for a busy UI thread: records posted work and never runs it.</summary>
    private sealed class BlockedContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
    }

    /// <summary>Holds the registration until released, so the test can stop the service before capture starts.</summary>
    private sealed class HeldRegisterHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // ConfigureAwait(false): this runs inside the service's call, so a plain await would itself post to the blocked context.
            var registration = (await request.Content!.ReadFromJsonAsync<ServerRegistrationMessage>(ct).ConfigureAwait(false))!;
            Requested.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ServerRegistrationResponse { Success = true, ServerId = registration.ServerId })
            };
        }
    }

    [Fact]
    public async Task StartAsync_FromUiContext_RaisesFirstPasskeySynchronously_AndNeverPostsBackToTheCaller()
    {
        Directory.CreateDirectory(_dir);
        var handler = new HeldRegisterHandler();
        var config = new ScreenSenderConfiguration { ServerId = "server-123456789", SignalingServerUrl = "ws://127.0.0.1:9/signal" };
        using var service = new ScreenSenderService(
            Substitute.For<ILogger<ScreenSenderService>>(), config, new HttpClient(handler),
            Path.Combine(_dir, "server-id"), new ServerOwnerSecretStore(Path.Combine(_dir, "owner.key")));

        string? raised = null;
        service.PasskeyChanged += (_, e) => raised = e.Passkey;

        var ui = new BlockedContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            await service.StartAsync(CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        // ServerManager.CurrentPasskey relies on the first passkey being announced before StartAsync returns.
        Assert.NotNull(raised);
        Assert.Equal(service.CurrentPasskey, raised);

        // Stop while the registration is in flight, then let it finish: the code after it must run without the caller's
        // context and must not go on to start screen capture.
        await handler.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stop = service.StopAsync(timeout.Token);
        handler.Release.TrySetResult();
        await stop;

        Assert.Equal(0, ui.Posts);
        Assert.True(service.Execution.IsCompleted, "the service must stop even though the caller's context never runs posted work");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
