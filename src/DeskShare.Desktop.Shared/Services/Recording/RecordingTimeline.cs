using System.Collections.Concurrent;

namespace DeskShare.Desktop.Shared.Services.Recording;

/// <summary>What <see cref="FrameRateNormalizer.Plan"/> decided for one incoming frame.</summary>
/// <param name="Accept">False: this frame arrived too soon after the previous one (its time slot is taken), drop it.</param>
/// <param name="RepeatPrevious">How many extra copies of the PREVIOUS frame to write first, to cover a gap in the stream.</param>
/// <param name="FirstRepeatSlot">Time slot (= presentation time) of the first of those copies; the others follow one slot apart.</param>
/// <param name="Slot">Time slot of the new frame itself. Can be later than the copies end: a long gap is NOT filled completely.</param>
public readonly record struct FramePlan(bool Accept, int RepeatPrevious, long FirstRepeatSlot, long Slot);

/// <summary>
/// Turns frames that arrive at irregular times into a constant frame rate: the video timeline is a row of slots of
/// 1/fps seconds. A frame goes into the slot it arrived in; a second frame for an already filled slot is dropped; slots
/// nobody filled (the screen did not change, the network stalled) are filled by repeating the previous frame.
/// Pure arithmetic on millisecond timestamps, so it is unit tested without FFmpeg or a clock.
/// </summary>
public sealed class FrameRateNormalizer
{
    private readonly int _fps;
    private long _originMs = -1;
    private long _nextSlot; // first slot that is still empty

    public FrameRateNormalizer(int fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        _fps = fps;
        MaxGapCopies = fps * MaxGapSeconds;
    }

    /// <summary>
    /// A still screen sends no frames, so gaps can last an hour. Encoding a copy for every empty slot (54,000 for an hour)
    /// would keep the encoder busy for no benefit. At most this many seconds of copies are written per gap; the next
    /// frame then gets its true (later) presentation time. WebM allows timestamps with holes, a player just keeps showing
    /// the last picture across the hole.
    /// </summary>
    public const int MaxGapSeconds = 2;

    /// <summary>The same limit in frames.</summary>
    public int MaxGapCopies { get; }

    /// <summary>Slots already written (= frames in the output so far).</summary>
    public long WrittenSlots => _nextSlot;

    /// <summary>Decides what to do with a frame that arrived <paramref name="timestampMs"/> (any monotonic clock).</summary>
    public FramePlan Plan(long timestampMs)
    {
        if (_originMs < 0) _originMs = timestampMs; // the first frame starts the video
        var slot = SlotOf(timestampMs);
        if (slot < _nextSlot) return new FramePlan(false, 0, 0, 0);
        var first = _nextSlot;
        var repeat = (int)Math.Min(slot - _nextSlot, MaxGapCopies); // empty slots between the previous frame and this one, capped
        _nextSlot = slot + 1;
        return new FramePlan(true, repeat, first, slot);
    }

    /// <summary>
    /// At the end of the recording: copies of the last frame so the video lasts until <paramref name="timestampMs"/>
    /// (capped like a gap, see <see cref="MaxGapSeconds"/>). <c>FirstSlot</c> is the presentation slot of the first copy.
    /// </summary>
    public (int Count, long FirstSlot) Finish(long timestampMs)
    {
        if (_originMs < 0) return (0, 0);
        var first = _nextSlot;
        var gap = Math.Max(0, SlotOf(timestampMs) - _nextSlot);
        _nextSlot += gap;
        return ((int)Math.Min(gap, MaxGapCopies), first);
    }

    private long SlotOf(long timestampMs) => Math.Max(0, timestampMs - _originMs) * _fps / 1000;
}

/// <summary>
/// A queue with a fixed capacity that NEVER blocks the producer: when the consumer (the encoder) is too slow the new item
/// is dropped. The producer is the video decode thread; making it wait would stall the live picture on screen.
/// </summary>
public sealed class DropWhenFullQueue<T> : IDisposable
{
    private readonly BlockingCollection<T> _items;
    private long _dropped;

    public DropWhenFullQueue(int capacity) => _items = new BlockingCollection<T>(new ConcurrentQueue<T>(), capacity);

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>False (and counted as dropped) when the queue is full or already completed.</summary>
    public bool TryEnqueue(T item)
    {
        bool added;
        try { added = _items.TryAdd(item); }
        catch (InvalidOperationException) { added = false; } // CompleteAdding was called: recording is stopping
        if (!added) Interlocked.Increment(ref _dropped);
        return added;
    }

    /// <summary>No more items will come; the consumer finishes what is queued.</summary>
    public void CompleteAdding() => _items.CompleteAdding();

    /// <summary>Waits for the next item. False when completed and empty, or when cancelled.</summary>
    public bool TryDequeue(out T item, CancellationToken cancel)
    {
        try { return _items.TryTake(out item!, Timeout.Infinite, cancel); }
        catch (OperationCanceledException) { item = default!; return false; }
    }

    public void Dispose() => _items.Dispose();
}

/// <summary>Where recordings go and how they are called.</summary>
public static class RecordingFiles
{
    /// <summary>%USERPROFILE%\Videos\DeskShare (the Videos folder of the current user on any OS).</summary>
    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is { Length: > 0 } videos
            ? videos
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos"), "DeskShare");

    /// <summary>
    /// Creates the (empty) file for <paramref name="fileName"/> and returns its path. If the name is taken (two recordings
    /// in the same second: quick restart, two windows) the next free "-2", "-3", ... name is used. The file is created with
    /// <see cref="FileMode.CreateNew"/>, which fails instead of truncating when the file exists, so another recording is never
    /// overwritten and two recorders can never claim the same name (the check and the creation are one atomic call).
    /// </summary>
    public static string ReserveUnique(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, n == 1 ? fileName : $"{stem}-{n}{ext}");
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // taken: try the next number
            }
        }
    }

    /// <summary>deskshare-&lt;server&gt;-yyyyMMdd-HHmmss[-N].webm. N is the segment number, written from the 2nd segment on.</summary>
    public static string FileName(string serverId, DateTime startedLocal, int segment)
    {
        // The id is shown by the user, so keep only characters that are safe in a file name.
        var safe = new string(serverId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) safe = "host";
        return $"deskshare-{safe}-{startedLocal:yyyyMMdd-HHmmss}{(segment > 1 ? "-" + segment : "")}.webm";
    }
}
