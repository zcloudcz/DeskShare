using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DeskShare.Desktop.Shared.Services.Recording;

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
    private readonly DropWhenFullQueue<Frame> _queue = new(QueueCapacity);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTime _startedLocal = DateTime.Now;
    private readonly List<string> _files = new();
    private Thread? _thread;
    private long _stopMs;
    private int _started, _stopped;

    private readonly record struct Frame(byte[] Bgra, int Width, int Height, long TimestampMs);

    public SessionRecorder(string folder, string serverId, ILogger logger)
    {
        _folder = folder;
        _serverId = serverId;
        _logger = logger;
    }

    /// <summary>Raised on the recorder's thread when writing failed; the recording then stops by itself (the file so far is kept).</summary>
    public event Action<Exception>? Failed;

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
        _thread = new Thread(Run) { IsBackground = true, Name = "DeskShare recorder" };
        _thread.Start();
    }

    /// <summary>
    /// Offers a tightly packed BGRA frame. Never blocks. The array is read later by the encoder thread, so the caller must not
    /// change it afterwards (the video sink creates a new array per frame).
    /// </summary>
    public bool TryAddFrame(byte[] bgra, int width, int height) =>
        Volatile.Read(ref _stopped) == 0 && _queue.TryEnqueue(new Frame(bgra, width, height, _clock.ElapsedMilliseconds));

    /// <summary>Stops, writes what is queued and closes the file(s). Returns the files. Blocks briefly: call it off the UI thread.</summary>
    public IReadOnlyList<string> Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _stopMs = _clock.ElapsedMilliseconds;
            _queue.CompleteAdding();
        }
        _thread?.Join();
        return Files;
    }

    private void Run()
    {
        WebmSegmentWriter? writer = null;
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
                    FinishSegment(writer, normalizer, frame.TimestampMs);
                    width = frame.Width;
                    height = frame.Height;
                    var path = Path.Combine(_folder, RecordingFiles.FileName(_serverId, _startedLocal, ++segment));
                    writer = new WebmSegmentWriter(path, width, height, Fps);
                    normalizer = new FrameRateNormalizer(Fps);
                    lock (_files) _files.Add(path);
                    _logger.LogInformation("Recording segment {Segment} started: {Path} ({Width}x{Height})", segment, path, width, height);
                }

                var plan = normalizer.Plan(frame.TimestampMs);
                if (!plan.Accept) continue; // same time slot as the previous frame
                for (var i = 0; i < plan.RepeatPrevious; i++) writer.RepeatLast();
                writer.WriteFrame(frame.Bgra);
            }
            FinishSegment(writer, normalizer, _stopMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording failed");
            try { writer?.Dispose(); } catch (Exception) { /* already failing */ }
            _queue.CompleteAdding(); // nothing consumes any more: stop accepting frames
            Volatile.Write(ref _stopped, 1);
            Failed?.Invoke(ex);
        }
    }

    // Pads the file to the given time with the last picture (a still screen sends no frames) and closes it.
    private static void FinishSegment(WebmSegmentWriter? writer, FrameRateNormalizer normalizer, long untilMs)
    {
        if (writer == null) return;
        for (var i = normalizer.Finish(untilMs); i > 0; i--) writer.RepeatLast();
        writer.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _queue.Dispose();
    }
}
