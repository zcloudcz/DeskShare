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

    // A single decode (or decoder creation) call longer than this is a hang. A 1080p software decode takes ~10-30 ms and
    // creating a GPU decoder on a cold driver up to about a second, so 2 s is far above anything normal and still
    // short enough for the viewer to recover before the user gives up on a frozen picture.
    internal TimeSpan HangLimit { get; init; } = TimeSpan.FromSeconds(2);

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
    private VpxDecoder? _vp8;
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

    private readonly record struct Item(IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format, bool Silent);

    /// <summary>One decoder thread with its own queue.</summary>
    private sealed class Worker
    {
        public readonly object Gate = new();
        public readonly Queue<Item> Queue = new();
        public bool Busy, Stop;
        public volatile bool Abandoned;
        public long DecodeStartedAt;            // Stopwatch ticks while inside a decode/creation call, else 0
        public VideoCodecsEnum Codec;           // what that call decodes
        public bool IsStuck(TimeSpan limit)
        {
            var started = Volatile.Read(ref DecodeStartedAt);
            return started != 0 && Stopwatch.GetElapsedTime(started) > limit;
        }
    }

    // Every H.264 access unit since the last keyframe. The viewer cannot ask the host for a new keyframe (there is no
    // feedback channel), and a new decoder needs one to start, so on a switch we replay these instead.
    private readonly List<(IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)> _gop = new();
    private long _gopBytes;
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
                finally
                {
                    lock (w.Gate) { w.Busy = false; System.Threading.Monitor.PulseAll(w.Gate); }
                }
                if (w.Abandoned) return;
            }
        }
        finally
        {
            // Only a thread that left normally cleans up, and it does so itself: nobody else may touch a decoder that can be mid-call.
            if (!w.Abandoned)
            {
                _h264?.Dispose();
                _vp8?.Dispose();
                (_ffmpeg as IDisposable)?.Dispose();
                lock (_gate) _gop.Clear();
            }
        }
    }

    // Runs a native decoder call under the hang clock. After a call that returns on an abandoned thread, nothing may be touched.
    private T Timed<T>(VideoCodecsEnum codec, Func<T> call)
    {
        var w = t_worker!;
        w.Codec = codec;
        Volatile.Write(ref w.DecodeStartedAt, Stopwatch.GetTimestamp());
        try { return call(); }
        finally
        {
            Volatile.Write(ref w.DecodeStartedAt, 0);
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
            // Hang detection piggybacks on the frames arriving anyway (no timer thread). A static screen sends nothing,
            // but then there is no moving picture to lose; the next frame notices the hang.
            if (_worker.IsStuck(HangLimit)) Abandon(_worker);
            Enqueue(_worker, new Item(remoteEndPoint, timestamp, frame, format, false));
        }
    }

    private void Enqueue(Worker w, Item item)
    {
        lock (w.Gate)
        {
            if (w.Queue.Count >= QueueCapacity)
            {
                // Nothing before a keyframe is needed to show the picture again, so a keyframe empties the queue;
                // any other frame is dropped (the picture may glitch until the next keyframe, the receive thread must not wait).
                if (!IsKeyframe(item))
                {
                    if (++_dropped % 30 == 1) _logger.LogWarning("Video decoder queue full, dropped {Count} frames so far", _dropped);
                    return;
                }
                w.Queue.Clear();
            }
            w.Queue.Enqueue(item);
            System.Threading.Monitor.PulseAll(w.Gate);
        }
    }

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
        var cappedOut = _abandoned >= MaxAbandonedDecoders;
        if (stuck.Codec == VideoCodecsEnum.H264)
        {
            if (_path != H264Path.FFmpeg) _path = cappedOut ? H264Path.FFmpeg : _path + 1;
        }
        else _vp8OnFfmpeg = true;
        if (cappedOut) { _path = H264Path.FFmpeg; _vp8OnFfmpeg = true; }

        Item[] leftovers;
        lock (stuck.Gate) { leftovers = stuck.Queue.ToArray(); stuck.Queue.Clear(); }
        _worker = StartWorker();

        // Replay the frames since the last keyframe into the next Media Foundation decoder (silently: only the newest
        // picture is shown). For FFmpeg the first queued frame makes DecodeH264 replay by itself.
        if (stuck.Codec == VideoCodecsEnum.H264 && _path != H264Path.FFmpeg)
            for (var i = 0; i < _gop.Count; i++)
                Enqueue(_worker, new Item(_gop[i].Ep, _gop[i].Ts, _gop[i].Data, _gop[i].Format, Silent: i < _gop.Count - 1 || leftovers.Length > 0));
        foreach (var item in leftovers) Enqueue(_worker, item);
    }

    // Runs on the decoder thread.
    private void Process(Item item)
    {
        _suppressEvents = item.Silent;
        try
        {
            if (item.Format.Codec == VideoCodecsEnum.H264)
                DecodeH264(item.Ep, item.Ts, item.Data, item.Format);
            else if (item.Format.Codec == VideoCodecsEnum.VP8)
                DecodeVp8(item.Ep, item.Ts, item.Data, item.Format);
        }
        catch (WorkerAbandonedException) { }
        catch (Exception ex) when (!IsStale)
        {
            // Last line of defence: whatever goes wrong in here must never kill the decoder thread.
            _logger.LogError(ex, "Video decode failed for {Codec} frame of {Size} bytes", item.Format.Codec, item.Data.Length);
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
                // (this frame included). Throws again if it still fails; the caller logs it and the next frame retries.
                Remember(ep, ts, frame, format);
                EnsureFfmpeg();
                Replay(f => _ffmpeg!.GotVideoFrame(f.Ep, f.Ts, f.Data, f.Format));
                return;
            }
            _ffmpeg.GotVideoFrame(ep, ts, frame, format);
            return;
        }

        Remember(ep, ts, frame, format);
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
                StepDown(); // replays the current frame too: it is the newest one remembered
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
        if (pictures > 0) _h264SilentUnits = 0;
        else if (_h264SawIdr && ++_h264SilentUnits >= SilentUnitsBeforeStall) throw new DecoderStalledException();
    }

    // Moves to the next decoder and feeds it everything since the last keyframe so the picture continues without a new keyframe.
    private void StepDown()
    {
        _h264?.Dispose();
        _h264 = null;
        _h264Failures = 0;
        _h264SawIdr = false;
        _h264SilentUnits = 0;
        _path++;
        _logger.LogWarning("Switching H.264 decoding to {Path}", _path);

        if (_path == H264Path.FFmpeg)
        {
            EnsureFfmpeg();
            Replay(frame => _ffmpeg!.GotVideoFrame(frame.Ep, frame.Ts, frame.Data, frame.Format));
            return;
        }
        Replay(frame =>
        {
            _h264 ??= Timed(VideoCodecsEnum.H264, () => _createH264(_path));
            FeedH264(frame.Data); // a replay into a decoder that is silent as well steps down again
        });
    }

    private void Replay(Action<(IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)> decode)
    {
        if (_gopOverflow)
            _logger.LogWarning("No replay possible (too long since the last keyframe): the picture resumes at the next keyframe");
        (IPEndPoint Ep, uint Ts, byte[] Data, VideoFormat Format)[] frames;
        lock (_gate) frames = _gop.ToArray();
        _suppressEvents = true;
        try
        {
            for (var i = 0; i < frames.Length; i++)
            {
                _suppressEvents = i < frames.Length - 1; // show only the newest picture, not a burst of old ones
                decode(frames[i]);
            }
        }
        catch (Exception ex) when (_path != H264Path.FFmpeg && !IsStale)
        {
            // The next decoder is broken as well: keep stepping down (ends at FFmpeg, which is not guarded here on purpose).
            _suppressEvents = false;
            _logger.LogWarning("Media Foundation H.264 ({Path}) failed during replay: {Message}", _path, ex.Message);
            StepDown();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void Remember(IPEndPoint ep, uint ts, byte[] frame, VideoFormat format)
    {
        lock (_gate) RememberLocked(ep, ts, frame, format);
    }

    private void RememberLocked(IPEndPoint ep, uint ts, byte[] frame, VideoFormat format)
    {
        if (ContainsIdr(frame))
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
                var vp8 = _vp8 ??= Timed(VideoCodecsEnum.VP8, () => new VpxDecoder());
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
        // Never waits: the decoder thread frees the decoders itself when it leaves (see Run). If it is hung it stays hung
        // and leaks, but Dispose still returns at once (it may run on the UI thread).
        lock (w.Gate) { w.Stop = true; System.Threading.Monitor.PulseAll(w.Gate); }
    }
}
