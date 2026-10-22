using DeskShare.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskShare.UnitTests.Unattended;

public class StreamingSignalTests
{
    private static ScreenSenderService NewService() =>
        new(NullLogger<ScreenSenderService>.Instance, new ScreenSenderConfiguration { ServerId = "server-123456789" });

    [Fact]
    public void Streaming_IsPending_UntilSignalled()
    {
        var s = NewService();
        Assert.False(s.Streaming.IsCompleted);
        s.SignalStreaming();
        Assert.True(s.Streaming.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecutionFaultBeforeStreaming_FaultsTheSignal()
    {
        var s = NewService();
        s.WireStreamingSignal(Task.FromException(new InvalidOperationException("capture failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Streaming);
    }

    [Fact]
    public async Task ExecutionEndingWithoutStreaming_CancelsTheSignal()
    {
        var s = NewService();
        s.WireStreamingSignal(Task.CompletedTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Streaming);
    }

    [Fact]
    public async Task ExecutionEndingAfterStreaming_KeepsTheSuccess()
    {
        var s = NewService();
        s.SignalStreaming();
        s.WireStreamingSignal(Task.FromException(new InvalidOperationException("later")));
        await s.Streaming; // first outcome wins
    }
}
