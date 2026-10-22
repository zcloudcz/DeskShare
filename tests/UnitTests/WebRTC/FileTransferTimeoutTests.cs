using System.Text.Json;
using DeskShare.Core.WebRTC;
using DeskShare.Desktop.Shared.Services;
using NSubstitute;
using Serilog;

namespace DeskShare.UnitTests.WebRTC;

/// <summary>Timeout behaviour of the file transfer, driven by a manual clock instead of real waiting.</summary>
public sealed class FileTransferTimeoutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deskshare-timeout-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _hostReplies = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>Time moves only through <see cref="Advance"/>; supports the timers Task.Delay and CancellationTokenSource use.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by)
        {
            Interlocked.Add(ref _ticks, by.Ticks);
            ManualTimer[] due;
            lock (_timers) due = _timers.Where(t => t.Due <= GetTimestamp()).ToArray();
            foreach (var t in due) t.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due = long.MaxValue;
            private long _period;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetTimestamp() + dueTime.Ticks;
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }

            public void Fire()
            {
                Due = _period > 0 ? owner.GetTimestamp() + _period : long.MaxValue;
                callback(state);
            }

            public void Dispose() { Due = long.MaxValue; lock (owner._timers) owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    // ---- FileReceiver idle timer ----

    private FileReceiver CreateReceiver(ManualTime time) =>
        new(_folder, 1_000_000, json => { lock (_hostReplies) _hostReplies.Add(json); return true; },
            Substitute.For<ILogger>(), TimeSpan.FromSeconds(60), time);

    private static void Send(FileReceiver r, object message)
    {
        var el = JsonSerializer.SerializeToElement(message);
        r.Handle(el.GetProperty("type").GetString()!, el);
    }

    private static string NewId() => Guid.NewGuid().ToString("D");

    private string[] Parts() => Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*.part") : [];

    [Fact]
    public void StaleIdleCallback_AfterAChunk_DoesNotFailTheTransfer()
    {
        var time = new ManualTime();
        using var r = CreateReceiver(time);
        var id = NewId();
        Send(r, new { type = "file_offer", id, name = "a.bin", size = 10 });

        time.Advance(TimeSpan.FromSeconds(59));
        Send(r, new { type = "file_chunk", id, seq = 0, data = Convert.ToBase64String([1]) }); // refreshes the timeout
        time.Advance(TimeSpan.FromSeconds(2));
        r.OnIdle(); // the callback that was already queued for the old deadline

        Assert.Single(Parts());
        Assert.DoesNotContain(_hostReplies, m => m.Contains("file_error"));
    }

    [Fact]
    public void StaleIdleCallback_AfterCancelAndNewOffer_DoesNotFailTheNewTransfer()
    {
        var time = new ManualTime();
        using var r = CreateReceiver(time);
        var first = NewId();
        Send(r, new { type = "file_offer", id = first, name = "a.bin", size = 10 });
        time.Advance(TimeSpan.FromSeconds(59));
        Send(r, new { type = "file_cancel", id = first });
        Send(r, new { type = "file_offer", id = NewId(), name = "b.bin", size = 10 });
        time.Advance(TimeSpan.FromSeconds(2));

        r.OnIdle();

        Assert.Single(Parts());
        Assert.DoesNotContain(_hostReplies, m => m.Contains("file_error"));
    }

    [Fact]
    public void GenuinelyIdleTransfer_TimesOut()
    {
        var time = new ManualTime();
        using var r = CreateReceiver(time);
        Send(r, new { type = "file_offer", id = NewId(), name = "a.bin", size = 10 });
        time.Advance(TimeSpan.FromSeconds(60));

        r.OnIdle();

        Assert.Empty(Parts());
        Assert.Contains(_hostReplies, m => m.Contains("\"reason\":\"timeout\""));
    }

    // ---- FileUploader final drain ----

    private sealed class UploaderRig : IDisposable
    {
        public readonly ManualTime Time = new();
        public readonly List<string> Sent = new();
        public readonly string Source = Path.Combine(Path.GetTempPath(), "deskshare-up-" + Guid.NewGuid().ToString("N") + ".bin");
        public readonly FileUploader Uploader;
        public string Id = "";

        public UploaderRig(Func<ManualTime, ulong> buffered)
        {
            File.WriteAllBytes(Source, [1, 2, 3]);
            Uploader = new FileUploader(json =>
            {
                Sent.Add(json);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.GetProperty("type").GetString() == "file_offer")
                {
                    Id = doc.RootElement.GetProperty("id").GetString()!;
                    Reply(new { type = "file_accept", id = Id });
                }

                return true;
            }, () => buffered(Time), Time);
        }

        public void Reply(object message) => Uploader.OnHostMessage(JsonSerializer.SerializeToElement(message));

        /// <summary>Advances the manual clock a second at a time, letting the uploader's continuations run in between.</summary>
        public async Task RunAsync(Task task, int seconds)
        {
            for (var i = 0; i < seconds && !task.IsCompleted; i++)
            {
                await Task.Delay(3);
                Time.Advance(TimeSpan.FromSeconds(1));
            }

            await Task.Delay(20);
        }

        public void Dispose() => File.Delete(Source);
    }

    [Fact]
    public async Task Uploader_SlowDrainLongerThanReplyTimeout_StillWaitsForTheHost()
    {
        // 900 kB shrinking by 10 kB/s: empty only after 90 s, three times the reply timeout.
        using var rig = new UploaderRig(t => (ulong)Math.Max(0, 900_000 - 10_000 * (t.GetTimestamp() / TimeSpan.TicksPerSecond)));
        var send = rig.Uploader.SendAsync(rig.Source, null, CancellationToken.None);

        await rig.RunAsync(send, 100);
        Assert.False(send.IsCompleted); // drained, now waiting for the host with a fresh 30 s window

        rig.Reply(new { type = "file_done", id = rig.Id, savedAs = "x.bin" });
        Assert.Equal("x.bin", await send);
    }

    [Fact]
    public async Task Uploader_StalledBuffer_FailsAfterTheNoProgressDeadline()
    {
        using var rig = new UploaderRig(_ => 500_000);
        var send = rig.Uploader.SendAsync(rig.Source, null, CancellationToken.None);

        await rig.RunAsync(send, 25);
        Assert.False(send.IsCompleted);

        await rig.RunAsync(send, 10);
        var ex = await Assert.ThrowsAsync<IOException>(() => send);
        Assert.Contains("stalled", ex.Message);
    }

    [Fact]
    public async Task Uploader_CancelDuringDrain_StopsTheWait()
    {
        using var rig = new UploaderRig(_ => 500_000);
        using var cts = new CancellationTokenSource();
        var send = rig.Uploader.SendAsync(rig.Source, null, cts.Token);
        await rig.RunAsync(send, 5);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Contains(rig.Sent, m => m.Contains("file_cancel"));
    }
}
