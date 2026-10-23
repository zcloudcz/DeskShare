using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DeskShare.Desktop.Shared.Services.Recording;

/// <summary>One .webm file at one resolution. Seam so the recorder's failure handling is testable without FFmpeg.</summary>
internal interface ISegmentWriter : IDisposable
{
    void WriteFrame(byte[] bgra, long pts);
    void RepeatLast(long pts);
    /// <summary>Closes the file properly; throws when that fails (the file is then not usable).</summary>
    void Finish();
}

/// <summary>
/// Records the decoded frames of the shared screen to WebM (VP8) files at a constant frame rate.
/// </summary>
/// <remarks>
/// Threading: <see cref="TryAddFrame"/> is called by the video decode thread and must never wait, so it only puts the frame
/// into a small <see cref="DropWhenFullQueue{T}"/> (dropping it when the encoder is behind). One background thread takes the
/// frames, applies the <see cref="FrameRateNormalizer"/> and encodes.
///
/// Resolution changes (the host switched display, or the window of a shared app was resized): the file is closed and a NEW
/// file with the new size is started (name ends in -2, -3, ...). Scaling everything to one size would blur the text, and a
/// WebM file cannot change its size midstream.
///
/// This recording is the viewer's own copy; the host is only told about it (see the "recording_state" message) and cannot
/// prevent it.
/// </remarks>
public sealed class SessionRecorder : IDisposable
{
    public const int Fps = 15;
    private const int QueueCapacity = 6; // 6 frames of 1080p BGRA is about 50 MB; more would only add delay

    private readonly string _folder;
    private readonly string _serverId;
    private readonly ILogger _logger;
    private readonly Func<string, int, int, int, ISegmentWriter> _writerFactory;
    private readonly DropWhenFullQueue<Frame> _queue = new(QueueCapacity);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTime _startedLocal = DateTime.Now;
    private readonly List<string> _files = new();
    private Thread? _thread;
    private long _stopMs;
    private int _started, _stopped;

    private readonly record struct Frame(byte[] Bgra, int Width, int Height, long TimestampMs);

    public SessionRecorder(string folder, string serverId, ILogger logger)
        : this(folder, serverId, logger, (path, w, h, fps) => new WebmSegmentWriter(path, w, h, fps)) { }

    /// <summary>Test seam: any writer instead of the FFmpeg one.</summary>
    internal SessionRecorder(string folder, string serverId, ILogger logger, Func<string, int, int, int, ISegmentWriter> writerFactory)
    {
        _folder = folder;
        _serverId = serverId;
        _logger = logger;
        _writerFactory = writerFactory;
    }

    /// <summary>Raised on the recorder's thread when writing failed; the recording then stops by itself (the file so far is kept).</summary>
    public event Action<Exception>? Failed;

    /// <summary>
    /// The first thing that went wrong (writing a frame, or closing a file); null when all files were closed properly.
    /// When set, the files must not be reported as saved.
    /// </summary>
    public Exception? Error { get; private set; }

    /// <summary>Frames dropped because the encoder was behind.</summary>
    public long DroppedFrames => _queue.Dropped;

    /// <summary>Files written so far (the finished ones, plus the one in progress).</summary>
    public IReadOnlyList<string> Files
    {
        get { lock (_files) return _files.ToArray(); }
    }

    /// <summary>Creates the folder and starts the encoder thread. Throws when the folder cannot be created.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Directory.CreateDirectory(_folder);
        // A foreground thread on purpose: if the app is closed while the file is being finished, the process waits for the
        // trailer to be written instead of leaving a file that cannot be played. The work is bounded (a few queued frames
        // and the capped gap padding).
        _thread = new Thread(Run) { IsBackground = false, Name = "DeskShare recorder" };
        _thread.Start();
    }

    /// <summary>
    /// Offers a tightly packed BGRA frame. Never blocks. The array is read later by the encoder thread, so the caller must not
    /// change it afterwards (the video sink creates a new array per frame).
    /// </summary>
    public bool TryAddFrame(byte[] bgra, int width, int height) =>
        Volatile.Read(ref _stopped) == 0 && _queue.TryEnqueue(new Frame(bgra, width, height, _clock.ElapsedMilliseconds));

    /// <summary>
    /// Stops, writes what is queued and closes the file(s). Returns the files; check <see cref="Error"/> before calling them
    /// saved. Waits for the encoder thread (at most <paramref name="timeout"/> when given), so call it off the UI thread.
    /// </summary>
    public IReadOnlyList<string> Stop(TimeSpan? timeout = null)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _stopMs = _clock.ElapsedMilliseconds;
            _queue.CompleteAdding();
        }
        if (timeout is { } limit) _thread?.Join(limit);
        else _thread?.Join();
        return Files;
    }

    private void Run()
    {
        ISegmentWriter? writer = null;
        var normalizer = new FrameRateNormalizer(Fps);
        var segment = 0;
        var width = 0;
        var height = 0;
        try
        {
            while (_queue.TryDequeue(out var frame, CancellationToken.None))
            {
                if (writer == null || frame.Width != width || frame.Height != height)
                {
                    // Close the old file up to this frame's time, then start the next one (the new one's clock starts here).
                    var old = writer;
                    writer = null;
                    FinishSegment(old, normalizer, frame.TimestampMs);
                    width = frame.Width;
                    height = frame.Height;
                    // The file is reserved first: an existing recording is never truncated (see ReserveUnique).
                    var path = RecordingFiles.ReserveUnique(_folder, RecordingFiles.FileName(_serverId, _startedLocal, ++segment));
                    lock (_files) _files.Add(path);
                    writer = _writerFactory(path, width, height, Fps);
                    normalizer = new FrameRateNormalizer(Fps);
                    _logger.LogInformation("Recording segment {Segment} started: {Path} ({Width}x{Height})", segment, path, width, height);
                }

                var plan = normalizer.Plan(frame.TimestampMs);
                if (!plan.Accept) continue; // same time slot as the previous frame
                for (var i = 0; i < plan.RepeatPrevious; i++) writer.RepeatLast(plan.FirstRepeatSlot + i);
                writer.WriteFrame(frame.Bgra, plan.Slot);
            }
            var last = writer;
            writer = null;
            FinishSegment(last, normalizer, _stopMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording failed");
            Error ??= ex; // the UI says "could not be saved" instead of "Saved"
            try { writer?.Dispose(); } catch (Exception) { /* already failing */ }
            _queue.CompleteAdding(); // nothing consumes any more: stop accepting frames
            Volatile.Write(ref _stopped, 1);
            Failed?.Invoke(ex);
        }
    }

    // Pads the file to the given time with the last picture (a still screen sends no frames; the padding is capped, see
    // FrameRateNormalizer.MaxGapSeconds, so closing is quick) and closes it. A failure to close is NOT swallowed: it
    // propagates and ends up in Error.
    private static void FinishSegment(ISegmentWriter? writer, FrameRateNormalizer normalizer, long untilMs)
    {
        if (writer == null) return;
        try
        {
            var (count, firstSlot) = normalizer.Finish(untilMs);
            for (var i = 0; i < count; i++) writer.RepeatLast(firstSlot + i);
            writer.Finish();
        }
        catch
        {
            writer.Dispose(); // release the native objects; Finish does this itself, a failed RepeatLast does not
            throw;
        }
    }

    public void Dispose()
    {
        Stop();
        _queue.Dispose();
    }
}
