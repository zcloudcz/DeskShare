using System.Collections.Concurrent;

namespace DeskShare.Desktop.Shared.Services.Recording;

/// <summary>What <see cref="FrameRateNormalizer.Plan"/> decided for one incoming frame.</summary>
/// <param name="Accept">False: this frame arrived too soon after the previous one (its time slot is taken), drop it.</param>
/// <param name="RepeatPrevious">How many extra copies of the PREVIOUS frame to write first, to cover a gap in the stream.</param>
public readonly record struct FramePlan(bool Accept, int RepeatPrevious);

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
    }

    /// <summary>Slots already written (= frames in the output so far).</summary>
    public long WrittenSlots => _nextSlot;

    /// <summary>Decides what to do with a frame that arrived <paramref name="timestampMs"/> (any monotonic clock).</summary>
    public FramePlan Plan(long timestampMs)
    {
        if (_originMs < 0) _originMs = timestampMs; // the first frame starts the video
        var slot = SlotOf(timestampMs);
        if (slot < _nextSlot) return new FramePlan(false, 0);
        var repeat = (int)(slot - _nextSlot); // empty slots between the previous frame and this one
        _nextSlot = slot + 1;
        return new FramePlan(true, repeat);
    }

    /// <summary>
    /// At the end of the recording: how many copies of the last frame are needed so the video lasts until
    /// <paramref name="timestampMs"/> (a static screen sends no frames, the recording should still be that long).
    /// </summary>
    public int Finish(long timestampMs)
    {
        if (_originMs < 0) return 0;
        var repeat = (int)Math.Max(0, SlotOf(timestampMs) - _nextSlot);
        _nextSlot += repeat;
        return repeat;
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

    /// <summary>deskshare-&lt;server&gt;-yyyyMMdd-HHmmss[-N].webm. N is the segment number, written from the 2nd segment on.</summary>
    public static string FileName(string serverId, DateTime startedLocal, int segment)
    {
        // The id is shown by the user, so keep only characters that are safe in a file name.
        var safe = new string(serverId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) safe = "host";
        return $"deskshare-{safe}-{startedLocal:yyyyMMdd-HHmmss}{(segment > 1 ? "-" + segment : "")}.webm";
    }
}
