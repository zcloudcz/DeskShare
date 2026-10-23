using DeskShare.Desktop.Shared.Services.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace DeskShare.UnitTests.Video;

/// <summary>Frame-rate normalization and queue-drop logic (pure), plus one real encode through the bundled FFmpeg.</summary>
public sealed class SessionRecorderTests
{
    // ---- FrameRateNormalizer: 10 fps = one slot per 100 ms ----

    [Fact]
    public void FirstFrame_IsAccepted_AndStartsTheTimeline()
    {
        var n = new FrameRateNormalizer(10);

        Assert.Equal(new FramePlan(true, 0, 0, 0), n.Plan(5000)); // the origin is the first frame, not time zero
        Assert.Equal(1, n.WrittenSlots);
    }

    [Fact]
    public void FrameInAFilledSlot_IsDropped()
    {
        var n = new FrameRateNormalizer(10);
        n.Plan(0);

        Assert.False(n.Plan(30).Accept);
        Assert.False(n.Plan(99).Accept);
        Assert.Equal(new FramePlan(true, 0, 1, 1), n.Plan(100));
    }

    [Fact]
    public void Gap_IsFilledByRepeatingThePreviousFrame()
    {
        var n = new FrameRateNormalizer(10);
        n.Plan(0);

        var plan = n.Plan(450); // slot 4: slots 1, 2, 3 are empty

        Assert.Equal(new FramePlan(true, 3, 1, 4), plan);
        Assert.Equal(5, n.WrittenSlots);
    }

    [Fact]
    public void SteadyThirtyFpsInput_BecomesOneFramePerSlot()
    {
        var n = new FrameRateNormalizer(15);
        long written = 0;
        for (var ms = 0; ms < 2000; ms += 33) // ~30 fps for two seconds
        {
            var p = n.Plan(ms);
            if (p.Accept) written += 1 + p.RepeatPrevious;
        }

        Assert.InRange(written, 29, 31); // 2 s x 15 fps
        Assert.Equal(written, n.WrittenSlots);
    }

    [Fact]
    public void Finish_PadsTheTailAndNeverGoesBackwards()
    {
        var n = new FrameRateNormalizer(10);
        n.Plan(0);

        Assert.Equal((9, 1L, (long?)null), n.Finish(1000)); // slot 10 is the end; slots 1..9 still empty
        Assert.Equal(0, n.Finish(500).Count); // a time in the past adds nothing
        Assert.Equal(0, new FrameRateNormalizer(10).Finish(1000).Count); // no frame was ever written
    }

    [Fact]
    public void LongGap_IsCapped_AndTheNextFrameKeepsItsTrueTime()
    {
        var n = new FrameRateNormalizer(15);
        n.Plan(0);

        var plan = n.Plan(3_600_000); // an hour of a still screen

        Assert.Equal(30, plan.RepeatPrevious); // 2 s of copies, not 54,000
        Assert.Equal(1, plan.FirstRepeatSlot);
        Assert.Equal(54_000, plan.Slot);       // the frame keeps its real presentation time (the hole is in the timestamps)
        Assert.Equal(54_001, n.WrittenSlots);
    }

    [Fact]
    public void LongTail_IsCappedToo()
    {
        var n = new FrameRateNormalizer(15);
        n.Plan(0);

        // 2 s of copies, then one copy at the last slot so the file still lasts an hour.
        Assert.Equal((30, 1L, (long?)(3_600 * 15 - 1)), n.Finish(3_600_000));
    }

    // ---- File names: never truncate ----

    [Fact]
    public void ReserveUnique_NeverReusesAnExistingFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deskshare-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var existing = Path.Combine(folder, "a.webm");
            File.WriteAllText(existing, "precious");

            var second = RecordingFiles.ReserveUnique(folder, "a.webm");
            var third = RecordingFiles.ReserveUnique(folder, "a.webm");

            Assert.Equal(Path.Combine(folder, "a-2.webm"), second);
            Assert.Equal(Path.Combine(folder, "a-3.webm"), third);
            Assert.Equal("precious", File.ReadAllText(existing)); // untouched
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TwoRecordersInTheSameSecond_GetDifferentFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deskshare-two-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Record()
            {
                using var recorder = new SessionRecorder(folder, "server-x", NullLogger.Instance, (_, _, _, _) => new FakeWriter());
                recorder.Start();
                recorder.TryAddFrame(new byte[16], 2, 2);
                return Assert.Single(recorder.Stop());
            }

            // Same server id; names are only to the second, so quick restarts can collide.
            var names = new HashSet<string> { Record(), Record(), Record() };

            Assert.Equal(3, names.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // ---- Finishing a file can fail ----

    private sealed class FakeWriter : ISegmentWriter
    {
        public Exception? FinishError;
        public bool Disposed;
        public void WriteFrame(byte[] bgra, long pts) { }
        public void RepeatLast(long pts) { }
        public void Finish() { if (FinishError != null) throw FinishError; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void FailedFinish_IsReportedAsError_NotSwallowed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deskshare-fail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new FakeWriter { FinishError = new IOException("disk full") };
            Exception? failedEvent = null;
            using var recorder = new SessionRecorder(folder, "server-x", NullLogger.Instance, (_, _, _, _) => writer);
            recorder.Failed += ex => failedEvent = ex;
            recorder.Start();
            recorder.TryAddFrame(new byte[16], 2, 2);

            recorder.Stop();

            Assert.Equal("disk full", recorder.Error?.Message);
            Assert.Same(recorder.Error, failedEvent);
            Assert.True(writer.Disposed); // native resources are released even when closing failed
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void SuccessfulFinish_HasNoError()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deskshare-ok-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var recorder = new SessionRecorder(folder, "server-x", NullLogger.Instance, (_, _, _, _) => new FakeWriter());
            recorder.Start();
            recorder.TryAddFrame(new byte[16], 2, 2);

            recorder.Stop();

            Assert.Null(recorder.Error);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // ---- DropWhenFullQueue ----

    [Fact]
    public void FullQueue_DropsInsteadOfBlocking_AndCountsTheDrops()
    {
        using var q = new DropWhenFullQueue<int>(2);

        Assert.True(q.TryEnqueue(1));
        Assert.True(q.TryEnqueue(2));
        Assert.False(q.TryEnqueue(3)); // returns at once
        Assert.Equal(1, q.Dropped);

        Assert.True(q.TryDequeue(out var first, CancellationToken.None));
        Assert.Equal(1, first);
        Assert.True(q.TryEnqueue(4)); // room again
    }

    [Fact]
    public void CompletedQueue_DrainsThenEnds_AndRefusesNewItems()
    {
        using var q = new DropWhenFullQueue<int>(2);
        q.TryEnqueue(1);
        q.CompleteAdding();

        Assert.False(q.TryEnqueue(2));
        Assert.True(q.TryDequeue(out _, CancellationToken.None));
        Assert.False(q.TryDequeue(out _, CancellationToken.None)); // empty and completed: no waiting
    }

    [Theory]
    [InlineData("server-123456789", "deskshare-server-123456789-20260102-030405.webm", 1)]
    [InlineData("server-123456789", "deskshare-server-123456789-20260102-030405-2.webm", 2)]
    [InlineData("..\\evil/x", "deskshare-evilx-20260102-030405.webm", 1)]
    public void FileName_IsSafeAndNumbersSegments(string id, string expected, int segment) =>
        Assert.Equal(expected, RecordingFiles.FileName(id, new DateTime(2026, 1, 2, 3, 4, 5), segment));

    // ---- Real encode through the bundled FFmpeg ----

    private static string? FindFfmpegFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DeskShare.DesktopAvalonia", "ffmpeg");
            if (Directory.Exists(candidate))
                return Directory.EnumerateFiles(candidate, "avcodec*").Any() ? candidate : null;
        }
        return null;
    }

    private sealed class RequiresFfmpegFactAttribute : FactAttribute
    {
        public RequiresFfmpegFactAttribute()
        {
            if (FindFfmpegFolder() == null)
                Skip = "FFmpeg libraries not found in src/DeskShare.DesktopAvalonia/ffmpeg (download them first).";
        }
    }

    [RequiresFfmpegFact]
    public void SyntheticFrames_AreEncodedToAWebmFile_AndAResolutionChangeStartsANewFile()
    {
        FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, FindFfmpegFolder());
        var folder = Path.Combine(Path.GetTempPath(), "deskshare-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var recorder = new SessionRecorder(folder, "server-test", NullLogger.Instance);
            recorder.Start();

            AddGradientFrames(recorder, 320, 240, count: 30);
            AddGradientFrames(recorder, 400, 300, count: 5); // the host switched display

            var files = recorder.Stop();

            Assert.Equal(2, files.Count);
            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file);
                Assert.True(bytes.Length > 100);
                Assert.Equal(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, bytes.Take(4)); // EBML header = Matroska/WebM
            }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    // The recorder reads real time, so the frames are paced (about 30 fps) like a live stream.
    private static void AddGradientFrames(SessionRecorder recorder, int w, int h, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var bgra = new byte[w * h * 4];
            for (var p = 0; p < w * h; p++)
            {
                bgra[p * 4] = (byte)(p % w + i * 5);
                bgra[p * 4 + 1] = (byte)(p / w);
                bgra[p * 4 + 2] = (byte)(i * 8);
                bgra[p * 4 + 3] = 255;
            }
            recorder.TryAddFrame(bgra, w, h);
            Thread.Sleep(33);
        }
    }
}
