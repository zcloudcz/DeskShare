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

    /// <summary>Answers the registration asynchronously and signals when it has.</summary>
    private sealed class RegisterHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // ConfigureAwait(false): this runs inside the service's call, so a plain await would itself post to the blocked context.
            var registration = (await request.Content!.ReadFromJsonAsync<ServerRegistrationMessage>(ct).ConfigureAwait(false))!;
            await Task.Delay(50, ct).ConfigureAwait(false); // answer asynchronously, like a real server
            Registered.TrySetResult();
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
        var handler = new RegisterHandler();
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

        await handler.Registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300); // let the continuation after the registration be scheduled

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StopAsync(stop.Token);

        Assert.Equal(0, ui.Posts);
        Assert.True(service.Execution.IsCompleted, "the service must stop even though the caller's context never runs posted work");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
