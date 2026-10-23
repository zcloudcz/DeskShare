using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>Where H.264 is decoded right now. The order is the fallback order: each step down is slower or more compatible.</summary>
internal enum H264Path { MediaFoundationGpu, MediaFoundationSoftware, FFmpeg }

/// <summary>
/// Decoder endpoint that replaces FFmpegVideoEndPoint inside <see cref="VideoSink"/> (through its test seam
/// constructor): H.264 goes to the Windows Media Foundation decoder (GPU first, then software), VP8 to libvpx
/// (<see cref="VpxDecoder"/>). It raises OnVideoSinkDecodedSampleFaster with a BGRA <see cref="RawImage"/>, exactly like
/// FFmpegVideoEndPoint, so VideoSink and everything behind it stay unchanged. (The RawImage costs one extra copy in
/// VideoSink; removing it would need VideoSink changes beyond a few lines, so it is left alone.)
///
/// If a decoder breaks mid-session (GPU reset, driver update) the sink steps down GPU, then software, then FFmpeg
/// instead of leaving the viewer with a frozen picture.
///
/// Threading: decoding runs on ONE background thread per decoder generation, fed by a bounded queue, so the WebRTC
/// receive thread (GotVideoFrame) only enqueues and never waits. Why: a Media Foundation call can hang inside native
/// code (seen when the D3D manager of a running decoder changes) and a native call cannot be interrupted. If a decode
/// call runs longer than <see cref="HangLimit"/> the thread and its decoder are abandoned (not disposed from another
/// thread: that could crash, so they leak), a new thread starts on the next path and the picture is replayed from the
/// last keyframe. Events are raised on the decoder thread, as the FFmpeg endpoint raised them on the caller thread:
/// VideoSink consumers already cannot assume the UI thread.
/// </summary>
internal sealed class MediaFoundationVideoSink : IVideoSink, IDisposable
{
    /// <summary>Consecutive failed frames before a decoder is given up. One bad packet must not cost the GPU path, a dead device fails every frame.</summary>
    internal const int FailuresBeforeFallback = 3;

    // Access units a decoder may swallow without a single picture before it counts as stalled. A decoder with a device
    // that silently died keeps accepting input and never outputs, so no exception ever reaches us. Real decoders here
    // (low latency, no B-frames) delay by 0-1 frames; 45 AU is 1.5 s at 30 fps, long enough to never fire on a slow
    // start or a dropped burst, short enough that the viewer does not sit on a frozen picture.
    internal const int SilentUnitsBeforeStall = 45;

    // The same stall measured in time, for a screen that changes rarely: after the last changes no further frame comes,
    // so counting units alone would wait forever. Same 1.5 s as 45 units at 30 fps.
    internal static readonly TimeSpan SilenceBeforeStall = TimeSpan.FromSeconds(1.5);

    // A single decode (or decoder creation) call longer than this is a hang. A 1080p software decode takes ~10-30 ms and
    // creating a GPU decoder on a cold driver up to about a second, so 2 s is far above anything normal and still
    // short enough for the viewer to recover before the user gives up on a frozen picture.
    internal TimeSpan HangLimit { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Test seam: runs after a replacement decoder thread started and before its recovery replay is queued.</summary>
    internal Action? AfterReplacementStarted { get; init; }

    /// <summary>Test seam: the VP8 decoder to use instead of libvpx.</summary>
    internal Func<IH264FrameDecoder>? CreateVp8 { get; init; }

    // After this many abandoned decoders the machine clearly cannot be trusted with Media Foundation: go to FFmpeg for good
    // (each abandoned decoder keeps a thread and native memory alive).
    private const int MaxAbandonedDecoders = 2;

    // Access units waiting for the decoder thread. 90 = 3 s at 30 fps, more than HangLimit, so a hang can be detected and
    // replayed without losing frames that arrived meanwhile. Normal depth is 0-1.
    private const int QueueCapacity = 90;

    // Cap for the frames kept for replay (see _gop). A 6 Mbit/s stream is about 8 MB per 10 s.
    private const long MaxReplayBytes = 32 * 1024 * 1024;

    // Same format the host's HardwareH264Encoder advertises (constrained baseline, packetization-mode=1).
    private static readonly VideoFormat H264Format = new(
        VideoCodecsEnum.H264, 102, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");
    private static readonly VideoFormat Vp8Format = new(VideoCodecsEnum.VP8, 96);

    private readonly ILogger _logger;
    private readonly Func<H264Path, IH264FrameDecoder> _createH264;
    private readonly Func<IVideoSink> _createFfmpeg;
    private readonly object _gate = new(); // guards _worker, _disposed, _gop and the abandon decision; never held while decoding

    private H264Path _path;
    private IH264FrameDecoder? _h264;
    private int _h264Failures;
    private bool _h264SawIdr;      // silence only counts once the decoder got a keyframe: before it, no picture is expected
    private int _h264SilentUnits;  // access units fed since the last picture came out
    private long _h264SilentSince; // Stopwatch ticks of the first of those units (0 = none); read by the hang timer
    private IH264FrameDecoder? _vp8; // a VpxDecoder (same Decode shape), replaceable in tests via CreateVp8
    private int _vp8Failures;
    private bool _vp8OnFfmpeg;
    private IVideoSink? _ffmpeg;
    private bool _suppressEvents; // true while old frames are replayed into a new decoder: only the last picture is shown
    private bool _disposed;
    private Worker _worker;
    private int _abandoned;
    private long _dropped;

    // The worker thread that is running the current code. A thread that was abandoned and later wakes up from its hung
    // native call finds Abandoned set here and must leave without touching the (now shared) sink state.
    [ThreadStatic] private static Worker? t_worker;

    private sealed class WorkerAbandonedException : Exception { }

    // Replay is set only on the one recovery item that Abandon hands to a new worker: a snapshot of the whole GOP. It sits
    // outside the capacity rules on purpose (a GOP can be hundreds of frames, the queue holds 90) and is fed first.
    private readonly record struct Item(IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format,
        (IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)[]? Replay = null, bool StallCheck = false);

    /// <summary>One decoder thread with its own queue.</summary>
    private sealed class Worker
    {
        public readonly object Gate = new();
        public readonly Queue<Item> Queue = new();
        public bool Busy, Stop;
        public volatile bool Abandoned;
        public long DecodeStartedAt;            // Stopwatch ticks while inside a decode/creation call, 0 when idle, Claimed once abandoned
        public VideoCodecsEnum Codec;           // what that call decodes
        public const long Claimed = -1;

        /// <summary>
        /// Takes over a call that has run longer than <paramref name="limit"/>. Atomic with the end of the call (see Timed):
        /// exactly one side wins the CompareExchange on the same start value, so either the call finished normally and the
        /// worker carries on, or the watchdog owns it and the worker can never publish anything from it. Without this, a
        /// call ending just after the watchdog read the old timestamp could still write decoder state for the new worker.
        /// </summary>
        public bool TryClaimStuck(TimeSpan limit)
        {
            var started = Volatile.Read(ref DecodeStartedAt);
            if (started <= 0 || Stopwatch.GetElapsedTime(started) <= limit) return false;
            return Interlocked.CompareExchange(ref DecodeStartedAt, Claimed, started) == started;
        }
    }

    // Every video frame (H.264 access unit or VP8 frame) RECEIVED since the last keyframe, written by the receive thread only (under _gate), so it is
    // always complete whatever the decoder threads are doing. The viewer cannot ask the host for a new keyframe (there is
    // no feedback channel), and a new decoder needs one to start, so on a switch we replay these instead. A replay takes
    // a snapshot and removes the queued H.264 frames (the snapshot already contains them), so nothing is decoded twice.
    private readonly List<(IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)> _gop = new();
    private long _gopBytes;
    private int _replayGeneration; // decoder thread only, see Replay
    private bool _gopOverflow; // too long without a keyframe: replay is not possible, the new decoder waits for the next one

    internal MediaFoundationVideoSink(ILogger logger, Func<H264Path, IH264FrameDecoder> createH264, Func<IVideoSink> createFfmpeg,
        H264Path start, IH264FrameDecoder? firstDecoder = null)
    {
        _logger = logger;
        _createH264 = createH264;
        _createFfmpeg = createFfmpeg;
        _path = start;
        _h264 = firstDecoder;
        _worker = StartWorker();
        // Hang checks also run on a timer: after the last change of a static screen no further frame arrives, so a
        // decoder stuck on that frame would otherwise never be noticed and the viewer would keep showing old content.
        _hangTimer = new Timer(_ => CheckForHang(), null, HangCheckInterval, HangCheckInterval);
    }

    private static readonly TimeSpan HangCheckInterval = TimeSpan.FromMilliseconds(250);
    private readonly Timer _hangTimer;

    private void CheckForHang()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_worker.TryClaimStuck(HangLimit)) Abandon(_worker);
                // A decoder that took input but shows nothing, and no frame is coming to notice it: ask its thread to check.
                else if (SilentTooLong()) Enqueue(_worker, new Item(null!, 0, Array.Empty<byte>(), default, StallCheck: true));
            }
        }
        catch (Exception ex)
        {
            // A timer callback must never throw (it would crash the process).
            _logger.LogError(ex, "Decoder hang check failed");
        }
    }

    private Worker StartWorker()
    {
        var worker = new Worker();
        // Background: an abandoned thread stuck in native code must never keep the process alive.
        new Thread(() => Run(worker)) { IsBackground = true, Name = "H264/VP8 decoder" }.Start();
        return worker;
    }

    private void Run(Worker w)
    {
        t_worker = w;
        // Nothing may escape this method: on a raw thread an unhandled exception would take the whole process down.
        try
        {
            while (true)
            {
                Item item;
                lock (w.Gate)
                {
                    while (w.Queue.Count == 0 && !w.Stop) System.Threading.Monitor.Wait(w.Gate);
                    if (w.Stop) break; // Dispose: queued frames are not worth decoding any more
                    item = w.Queue.Dequeue();
                    w.Busy = true;
                }
                try
                {
                    Process(item);
                }
                catch (Exception ex)
                {
                    // An abandoned thread that wakes up late fails in arbitrary ways (and may do so before it learns it is
                    // abandoned): it leaves silently. A healthy one logs and takes the next frame.
                    if (w.Abandoned) return;
                    _logger.LogError(ex, "Video decode failed for {Codec} frame of {Size} bytes", item.Format.Codec, item.Data.Length);
                }
                finally
                {
                    lock (w.Gate) { w.Busy = false; System.Threading.Monitor.PulseAll(w.Gate); }
                }
                if (w.Abandoned) return;
            }
        }
        catch (Exception ex)
        {
            if (!w.Abandoned) _logger.LogError(ex, "Video decoder thread stopped by an unexpected error");
        }
        finally
        {
            // Only a thread that left normally cleans up, and it does so itself: nobody else may touch a decoder that can be mid-call.
            if (!w.Abandoned)
            {
                try
                {
                    _h264?.Dispose();
                    _vp8?.Dispose();
                    (_ffmpeg as IDisposable)?.Dispose();
                }
                catch (Exception ex) { _logger.LogWarning("Disposing the decoders failed: {Message}", ex.Message); }
                lock (_gate) _gop.Clear();
            }
        }
    }
    // Runs a native decoder call under the hang clock. After a call that returns on an abandoned thread, nothing may be touched.
    private T Timed<T>(VideoCodecsEnum codec, Func<T> call)
    {
        var w = t_worker!;
        if (w.Abandoned) throw new WorkerAbandonedException(); // do not start a native call (or touch shared state) once abandoned
        w.Codec = codec;
        var start = Math.Max(1, Stopwatch.GetTimestamp());
        Volatile.Write(ref w.DecodeStartedAt, start);
        try { return call(); }
        finally
        {
            // Lost the race to the watchdog (it claimed this call): the result belongs to nobody, leave without using it.
            if (Interlocked.CompareExchange(ref w.DecodeStartedAt, 0, start) != start) throw new WorkerAbandonedException();
            if (w.Abandoned) throw new WorkerAbandonedException();
        }
    }

    private static bool IsStale => t_worker?.Abandoned == true;

    /// <summary>Test seam: waits until everything queued so far has been decoded.</summary>
    internal bool WaitIdle(TimeSpan timeout)
    {
        Worker w;
        lock (_gate) w = _worker;
        var end = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        lock (w.Gate)
        {
            while (w.Queue.Count > 0 || w.Busy)
            {
                var left = TimeSpan.FromSeconds((double)(end - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
                if (left <= TimeSpan.Zero) return false;
                System.Threading.Monitor.Wait(w.Gate, left);
            }
            return true;
        }
    }

    /// <summary>The decoder in use right now (tests assert the fallback step; also handy in logs).</summary>
    internal H264Path CurrentPath => _path;

#pragma warning disable CS0067 // part of IVideoSink, never raised by this sink (only the "faster" variant is used)
    public event VideoSinkSampleDecodedDelegate? OnVideoSinkDecodedSample;
#pragma warning restore CS0067
    public event VideoSinkSampleDecodedFasterDelegate? OnVideoSinkDecodedSampleFaster;

    /// <summary>
    /// Picks the decoder from "Video:Decoder" (or env DESKSHARE_VIDEO_DECODER): Auto (default) | MediaFoundation |
    /// MediaFoundationSoftware | FFmpeg. Returns null when FFmpeg should be used, either by choice or because Media
    /// Foundation cannot be created here (Windows N editions without the Media Feature Pack). Logs the outcome once.
    /// Callers must check OperatingSystem.IsWindows() first so that no Media Foundation type is touched elsewhere.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static MediaFoundationVideoSink? TryCreate(IConfiguration configuration, ILogger logger)
    {
        var requested = Environment.GetEnvironmentVariable("DESKSHARE_VIDEO_DECODER") ?? configuration["Video:Decoder"];
        if (string.Equals(requested, "FFmpeg", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Video decoder: FFmpeg (Video:Decoder=FFmpeg)");
            return null;
        }
        var software = string.Equals(requested, "MediaFoundationSoftware", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(requested) && !software &&
            !string.Equals(requested, "Auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(requested, "MediaFoundation", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("Unknown Video:Decoder '{Value}', using Auto", requested);

        // Creating the real decoder is the availability probe: it fails on N editions and when D3D11/DXVA is unusable.
        for (var path = software ? H264Path.MediaFoundationSoftware : H264Path.MediaFoundationGpu; path != H264Path.FFmpeg; path++)
        {
            try
            {
                var first = CreateDecoder(path);
                logger.LogInformation("Video decoder: Media Foundation ({Path}) for H.264, libvpx for VP8", path);
                return new MediaFoundationVideoSink(logger, CreateDecoder, CreateFfmpegEndpoint, path, first);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Media Foundation H.264 ({Path}) unavailable: {Message}", path, ex.Message);
            }
        }
        logger.LogWarning("Video decoder: FFmpeg (Media Foundation H.264 is not available, e.g. a Windows N edition without the Media Feature Pack)");
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static IH264FrameDecoder CreateDecoder(H264Path path) =>
        new MediaFoundationH264Decoder(hardware: path == H264Path.MediaFoundationGpu);

    // Same setup as the VideoSink constructor does for its own endpoint (FFmpeg itself is initialised from <app>/ffmpeg there).
    private static IVideoSink CreateFfmpegEndpoint()
    {
        var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
        if (Directory.Exists(folder))
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, folder);
        var endpoint = new FFmpegVideoEndPoint();
        endpoint.RestrictFormats(format => format.Codec == VideoCodecsEnum.VP8 || format.Codec == VideoCodecsEnum.H264);
        endpoint.SetVideoSinkFormat(Vp8Format);
        return endpoint;
    }

    /// <summary>Called on the WebRTC receive thread: queues the frame for the decoder thread and returns at once.</summary>
    public void GotVideoFrame(IPEndPoint remoteEndPoint, uint timestamp, byte[] frame, VideoFormat format)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Checked here too (not only by the timer), so a hang is handled before this frame is queued behind it.
            if (_worker.TryClaimStuck(HangLimit)) Abandon(_worker);
            if (format.Codec is VideoCodecsEnum.H264 or VideoCodecsEnum.VP8) RememberLocked(remoteEndPoint, timestamp, frame, format);
            Enqueue(_worker, new Item(remoteEndPoint, timestamp, frame, format));
        }
    }

    // Called under _gate (it reads _gop).
    private void Enqueue(Worker w, Item item)
    {
        lock (w.Gate)
        {
            if (item.Replay == null && IsKeyframe(item) && w.Queue.Count > 0)
            {
                // A keyframe resets the decoder: queued frames and catch-up replays before it would only be decoded in vain.
                var keep = w.Queue.Where(i => !IsVideoWork(i)).ToArray();
                w.Queue.Clear();
                foreach (var other in keep) w.Queue.Enqueue(other);
            }
            if (w.Queue.Count >= QueueCapacity)
            {
                // Nothing before a keyframe is needed to show the picture again, so a keyframe empties the queue.
                if (IsKeyframe(item)) w.Queue.Clear();
                else if (IsVideoWork(item) && !_gopOverflow && _gop.Count > 0)
                {
                    // A dropped delta (H.264 or VP8) would break the reference chain and, if it was the last change of a
                    // static screen, would never be shown. Instead the queued frames become ONE catch-up item: the whole GOP
                    // (it already contains this frame) decoded again from its keyframe, which resets the decoder.
                    var keep = w.Queue.Where(i => !IsVideoWork(i)).ToArray();
                    w.Queue.Clear();
                    foreach (var other in keep) w.Queue.Enqueue(other);
                    item = new Item(null!, 0, Array.Empty<byte>(), default, _gop.ToArray());
                    if (++_dropped % 30 == 1) _logger.LogWarning("Video decoder queue full, catching up from the last keyframe ({Count} times so far)", _dropped);
                }
                else
                {
                    // Too long since the keyframe to replay: dropped, the picture recovers at the next keyframe.
                    if (++_dropped % 30 == 1) _logger.LogWarning("Video decoder queue full, dropped {Count} frames so far", _dropped);
                    return;
                }
            }
            w.Queue.Enqueue(item);
            System.Threading.Monitor.PulseAll(w.Gate);
        }
    }

    // Video frames and catch-up snapshots: everything a newer GOP snapshot or a keyframe makes obsolete.
    private static bool IsVideoWork(Item i) => i.Replay != null || i.Format.Codec is VideoCodecsEnum.H264 or VideoCodecsEnum.VP8;

    private static bool IsKeyframe(Item item) =>
        item.Format.Codec == VideoCodecsEnum.H264 ? ContainsIdr(item.Data) : item.Data.Length > 0 && (item.Data[0] & 1) == 0; // VP8: bit 0 of the frame tag is 0 for keyframes

    // Called under _gate when the worker sits in one native call for too long.
    private void Abandon(Worker stuck)
    {
        stuck.Abandoned = true;
        _abandoned++;
        _logger.LogError("Video decoder thread hung for more than {Limit} s in {Codec} decoding; abandoning it (the thread and its decoder leak, they cannot be stopped safely). Abandoned so far: {Count}",
            HangLimit.TotalSeconds, stuck.Codec, _abandoned);

        // The hung thread owned these. Dropping the references without Dispose is the leak mentioned above.
        _h264 = null;
        _vp8 = null;
        _h264Failures = 0;
        _h264SawIdr = false;
        _h264SilentUnits = 0;
        Volatile.Write(ref _h264SilentSince, 0);
        var cappedOut = _abandoned >= MaxAbandonedDecoders;
        if (stuck.Codec == VideoCodecsEnum.H264)
        {
            if (_path != H264Path.FFmpeg) _path = cappedOut ? H264Path.FFmpeg : _path + 1;
        }
        else _vp8OnFfmpeg = true;
        if (cappedOut) { _path = H264Path.FFmpeg; _vp8OnFfmpeg = true; }

        // Everything the hung thread had queued (frames and older catch-up snapshots) is inside the current GOP snapshot,
        // and its decoders are gone with the thread, so the new worker starts with that snapshot; if it hangs too, the
        // next Abandon takes a fresh one.
        Item[] leftovers;
        lock (stuck.Gate)
        {
            leftovers = stuck.Queue.Where(i => !IsVideoWork(i) && !i.StallCheck).ToArray();
            stuck.Queue.Clear();
        }
        _worker = StartWorker();
        AfterReplacementStarted?.Invoke();
        if (_gop.Count > 0)
            lock (_worker.Gate)
            {
                _worker.Queue.Enqueue(new Item(null!, 0, Array.Empty<byte>(), default, _gop.ToArray()));
                // The new thread may already be waiting on an empty queue; on a static screen no later frame wakes it.
                System.Threading.Monitor.PulseAll(_worker.Gate);
            }
        foreach (var item in leftovers) Enqueue(_worker, item);
    }

    // Takes the GOP snapshot for a replay and drops the H.264 frames still queued for this worker: the snapshot contains them.
    private (IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)[] TakeSnapshot()
    {
        var w = t_worker!;
        lock (_gate)
        {
            lock (w.Gate)
            {
                var keep = w.Queue.Where(i => !IsVideoWork(i)).ToArray();
                w.Queue.Clear();
                foreach (var item in keep) w.Queue.Enqueue(item);
            }
            return _gop.ToArray();
        }
    }

    // Feeds one replayed frame to the current decoder (Media Foundation or FFmpeg) without touching the GOP.
    private void FeedReplay((IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format) frame)
    {
        if (frame.Format.Codec == VideoCodecsEnum.VP8)
        {
            DecodeVp8(frame.Ep, frame.Ts, frame.Data, frame.Format);
            return;
        }
        if (_path == H264Path.FFmpeg)
        {
            EnsureFfmpeg();
            _ffmpeg!.GotVideoFrame(frame.Ep, frame.Ts, frame.Data, frame.Format);
            return;
        }
        _h264 ??= Timed(VideoCodecsEnum.H264, () => _createH264(_path));
        FeedH264(frame.Data); // a replay into a decoder that is silent as well steps down again
    }
    // Runs on the decoder thread.
    private void Process(Item item)
    {
        try
        {
            if (item.StallCheck)
                CheckSilence();
            else if (item.Replay != null)
                Replay(FeedReplay, item.Replay);
            else if (item.Format.Codec == VideoCodecsEnum.H264)
                DecodeH264(item.Ep, item.Ts, item.Data, item.Format);
            else if (item.Format.Codec == VideoCodecsEnum.VP8)
                DecodeVp8(item.Ep, item.Ts, item.Data, item.Format);
        }
        finally
        {
            if (!IsStale) _suppressEvents = false;
        }
    }

    private void DecodeH264(IPEndPoint ep, uint ts, byte[] frame, VideoFormat format)
    {
        if (_path == H264Path.FFmpeg)
        {
            if (_ffmpeg == null)
            {
                // Creating the FFmpeg endpoint failed when we switched to it: retry, then catch up from the last keyframe
                // (this frame included, it is already in the GOP). Throws again if it still fails; the caller logs it and the next frame retries.
                EnsureFfmpeg();
                Replay(FeedReplay);
                return;
            }
            _ffmpeg.GotVideoFrame(ep, ts, frame, format);
            return;
        }

        try
        {
            _h264 ??= Timed(VideoCodecsEnum.H264, () => _createH264(_path));
            FeedH264(frame);
            _h264Failures = 0;
        }
        catch (Exception ex) when (!IsStale)
        {
            // A decoder that cannot even be created, or that went silent, is given up at once; a running one only after repeated failures.
            _h264Failures = _h264 == null || ex is DecoderStalledException ? FailuresBeforeFallback : _h264Failures + 1;
            _logger.LogWarning("Media Foundation H.264 ({Path}) failed ({Count}/{Max}): {Message}", _path, _h264Failures, FailuresBeforeFallback, ex.Message);
            if (_h264Failures >= FailuresBeforeFallback)
                StepDown(); // replays the current frame too: the receive thread put it into the GOP already
            // Below the limit the frame is dropped; the next one may work.
        }
    }

    private sealed class DecoderStalledException() : Exception($"No picture for {SilentUnitsBeforeStall} access units after a keyframe");

    // Decodes one access unit and watches for a decoder that accepts input but never outputs (see SilentUnitsBeforeStall).
    private void FeedH264(byte[] frame)
    {
        var decoder = _h264!;
        var pictures = Timed(VideoCodecsEnum.H264, () => decoder.Decode(frame, Raise));
        _h264SawIdr |= ContainsIdr(frame);
        if (pictures > 0)
        {
            _h264SilentUnits = 0;
            Volatile.Write(ref _h264SilentSince, 0);
        }
        else if (_h264SawIdr)
        {
            if (++_h264SilentUnits == 1) Volatile.Write(ref _h264SilentSince, Stopwatch.GetTimestamp());
            if (_h264SilentUnits >= SilentUnitsBeforeStall || SilentTooLong()) throw new DecoderStalledException();
        }
    }

    private bool SilentTooLong()
    {
        var since = Volatile.Read(ref _h264SilentSince);
        return since != 0 && Stopwatch.GetElapsedTime(since) > SilenceBeforeStall;
    }

    // Runs on the decoder thread when the timer saw a silence that no further frame will end (static screen).
    private void CheckSilence()
    {
        if (_path == H264Path.FFmpeg || _h264 == null || !SilentTooLong()) return;
        _logger.LogWarning("Media Foundation H.264 ({Path}) gave no picture for {Seconds} s after input; switching", _path, SilenceBeforeStall.TotalSeconds);
        StepDown();
    }

    // Moves to the next decoder and feeds it everything since the last keyframe so the picture continues without a new keyframe.
    private void StepDown()
    {
        IH264FrameDecoder? old;
        lock (_gate) // atomic with Abandon: a thread that was abandoned in the meantime must not touch the new worker's state
        {
            if (IsStale) return;
            old = _h264;
            _h264 = null;
            _h264Failures = 0;
            _h264SawIdr = false;
            _h264SilentUnits = 0;
            Volatile.Write(ref _h264SilentSince, 0);
            _path++;
        }
        // Disposing a broken decoder runs native calls too (MFT messages, COM/D3D release) and can hang like a decode,
        // so it is timed the same way: a hang here is abandoned and the next decoder takes over.
        if (old != null) Timed(VideoCodecsEnum.H264, () => { old.Dispose(); return 0; });
        _logger.LogWarning("Switching H.264 decoding to {Path}", _path);

        if (_path == H264Path.FFmpeg) EnsureFfmpeg();
        Replay(FeedReplay);
    }

    private void Replay(Action<(IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)> decode,
        (IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)[]? frames = null)
    {
        if (_gopOverflow)
            _logger.LogWarning("No replay possible (too long since the last keyframe): the picture resumes at the next keyframe");
        frames ??= TakeSnapshot();
        // A decoder switch inside this replay (VP8 falling back to FFmpeg) starts its own, newer replay; this one must
        // then stop, or the rest of its frames would reach the new decoder twice.
        var generation = ++_replayGeneration;
        _suppressEvents = true;
        try
        {
            for (var i = 0; i < frames.Length && generation == _replayGeneration; i++)
            {
                _suppressEvents = i < frames.Length - 1; // show only the newest picture, not a burst of old ones
                decode(frames[i]);
            }
        }
        catch (Exception ex) when (frames.Length > 0 && frames[0].Format.Codec == VideoCodecsEnum.H264 && _path != H264Path.FFmpeg && !IsStale)
        {
            // The next decoder is broken as well: keep stepping down (ends at FFmpeg, which is not guarded here on purpose).
            _suppressEvents = false;
            _logger.LogWarning("Media Foundation H.264 ({Path}) failed during replay: {Message}", _path, ex.Message);
            StepDown(); // takes a fresh snapshot, a superset of this one
        }
        finally
        {
            _suppressEvents = false;
        }
    }
    private void RememberLocked(IPEndPoint ep, uint ts, byte[] frame, VideoFormat format)
    {
        // A keyframe starts a new GOP; so does a codec change (the sender switched between VP8 and H.264).
        if (IsKeyframe(new Item(ep, ts, frame, format)) || (_gop.Count > 0 && _gop[0].Format.Codec != format.Codec))
        {
            _gop.Clear();
            _gopBytes = 0;
            _gopOverflow = false;
        }
        if (_gopOverflow) return;
        _gop.Add((ep, ts, frame, format));
        _gopBytes += frame.Length;
        if (_gopBytes > MaxReplayBytes)
        {
            _gop.Clear();
            _gopBytes = 0;
            _gopOverflow = true;
        }
    }

    // NAL unit type 5 (IDR slice) behind a 00 00 01 start code.
    internal static bool ContainsIdr(byte[] annexB)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1 && (annexB[i + 3] & 0x1F) == 5)
                return true;
        return false;
    }

    private void DecodeVp8(IPEndPoint ep, uint ts, byte[] frame, VideoFormat format)
    {
        if (!_vp8OnFfmpeg)
        {
            try
            {
                var vp8 = _vp8 ??= Timed(VideoCodecsEnum.VP8, () => CreateVp8?.Invoke() ?? new VpxDecoder());
                Timed(VideoCodecsEnum.VP8, () => { vp8.Decode(frame, Raise); return 0; });
                _vp8Failures = 0;
                return;
            }
            catch (Exception ex) when (!IsStale)
            {
                _vp8Failures = _vp8 == null ? FailuresBeforeFallback : _vp8Failures + 1;
                _logger.LogWarning("libvpx VP8 decode failed ({Count}/{Max}): {Message}", _vp8Failures, FailuresBeforeFallback, ex.Message);
                if (_vp8Failures < FailuresBeforeFallback) return;
                _vp8?.Dispose();
                _vp8 = null;
                _vp8OnFfmpeg = true;
                _logger.LogWarning("Switching VP8 decoding to FFmpeg");
                // Catch FFmpeg up from the last keyframe (the snapshot contains this frame too) so the picture continues.
                EnsureFfmpeg();
                Replay(FeedReplay);
                return;
            }
        }
        EnsureFfmpeg();
        _ffmpeg!.GotVideoFrame(ep, ts, frame, format);
    }

    private void EnsureFfmpeg()
    {
        if (_ffmpeg != null) return;
        _ffmpeg = _createFfmpeg();
        _ffmpeg.OnVideoSinkDecodedSampleFaster += image =>
        {
            if (!_suppressEvents && !_disposed) OnVideoSinkDecodedSampleFaster?.Invoke(image);
        };
    }

    // Pins the pixels only for the duration of the event: VideoSink copies them straight away.
    private void Raise(byte[] bgra, int width, int height)
    {
        if (IsStale) throw new WorkerAbandonedException(); // a hung decoder woke up late: its pictures are old news
        if (_suppressEvents || _disposed) return;
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            OnVideoSinkDecodedSampleFaster?.Invoke(new RawImage
            {
                Width = width, Height = height, Stride = width * 4,
                Sample = handle.AddrOfPinnedObject(), PixelFormat = VideoPixelFormatsEnum.Bgra
            });
        }
        finally { handle.Free(); }
    }

    // The WebRTC stack reassembles RTP packets into whole frames itself and calls GotVideoFrame, so raw RTP is not used.
    public void GotVideoRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload) { }

    public List<VideoFormat> GetVideoSinkFormats() => new() { Vp8Format, H264Format };
    public void SetVideoSinkFormat(VideoFormat videoFormat) { }
    public void RestrictFormats(Func<VideoFormat, bool> filter) { }
    public Task PauseVideoSink() => Task.CompletedTask;
    public Task ResumeVideoSink() => Task.CompletedTask;
    public Task StartVideoSink() => Task.CompletedTask;
    public Task CloseVideoSink() { Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        Worker w;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            w = _worker;
        }
        _hangTimer.Dispose();
        // Never waits: the decoder thread frees the decoders itself when it leaves (see Run). If it is hung it stays hung
        // and leaks, but Dispose still returns at once (it may run on the UI thread).
        lock (w.Gate) { w.Stop = true; System.Threading.Monitor.PulseAll(w.Gate); }
    }
}
