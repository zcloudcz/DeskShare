using System.Collections.Concurrent;
using System.Diagnostics;
using DeskShare.Core.Conversion;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.Video;
using Serilog;

namespace DeskShare.Core.Pipeline;

/// <summary>
/// Orchestrates the screen capture pipeline: Capture -> Convert -> Push to WebRTC.
/// Uses producer-consumer pattern with bounded queues for backpressure handling.
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private const int MaxQueueSize = 3; // Limit buffering to prevent memory issues
    private const int CaptureLoopDelayMs = 10; // Small delay between capture attempts

    // Replaced by SwitchCapturerAsync; the swap happens on the capture thread only, between two frames.
    private IScreenCapturer _capturer;
    private readonly IFrameConverter _converter;
    private readonly IVideoSource _videoSource;
    private readonly int _maxOutputWidth;

    // Resolution actually sent; equals the capture size unless downscaling is active. Set in Start and when the
    // capturer is switched. One immutable object so the conversion thread never sees a new width with an old height.
    private sealed record OutputDims(int Width, int Height);
    private volatile OutputDims _output = new(0, 0);

    // A capturer handed to SwitchCapturerAsync, applied by the capture loop (see ApplyPendingSwitch).
    private readonly object _switchLock = new();
    private IScreenCapturer? _pendingCapturer;
    private TaskCompletionSource<bool>? _pendingSwitch;
    private int _keyFramePending;
    private Func<bool>? _pendingGuard;
    private Action? _pendingOnFirstFrame;

    // Bumped on every capturer switch. Each queued frame carries the generation it was captured in; the later
    // stages drop frames of an older generation, so nothing of the previous display is sent after the switch.
    private volatile int _generation;
    // Callback of the last switch, run by the transmission thread once the first frame of that generation was sent.
    private sealed record FirstFrame(int Generation, Action Callback);
    private volatile FirstFrame? _firstFrameCallback;

    private readonly BlockingCollection<(Frame Frame, int Generation)> _captureQueue;
    private readonly BlockingCollection<(VideoFrame Frame, int Generation)> _conversionQueue;

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Task? _conversionTask;
    private Task? _transmissionTask;

    private bool _isRunning;
    private bool _disposed;

    // Statistics
    private long _framesCaptured;
    private long _framesCaptureSkipped;
    private long _framesConverted;
    private long _framesTransmitted;
    private long _framesDroppedBackpressure;

    // Per-stage timing, reported every StageReportIntervalSeconds (see ReportStageTimings).
    private const int StageReportIntervalSeconds = 10;
    private readonly StageTimings _acquireTimings = new();
    private readonly StageTimings _convertTimings = new();
    private readonly StageTimings _pushTimings = new();
    private Timer? _timingReportTimer;
    private int _lastOutputWidth;
    private int _lastOutputHeight;

    /// <summary>
    /// Gets a value indicating whether the pipeline is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapturePipeline"/> class.
    /// </summary>
    /// <param name="capturer">Screen capturer implementation.</param>
    /// <param name="converter">Pixel format converter.</param>
    /// <param name="videoSource">WebRTC video source.</param>
    /// <param name="maxOutputWidth">Maximum width of the sent video; 0 = native. See <see cref="OutputSize"/>.</param>
    public CapturePipeline(IScreenCapturer capturer, IFrameConverter converter, IVideoSource videoSource, int maxOutputWidth = 0)
    {
        _maxOutputWidth = maxOutputWidth;
        _capturer = capturer ?? throw new ArgumentNullException(nameof(capturer));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _videoSource = videoSource ?? throw new ArgumentNullException(nameof(videoSource));

        _captureQueue = new BlockingCollection<(Frame, int)>(MaxQueueSize);
        _conversionQueue = new BlockingCollection<(VideoFrame, int)>(MaxQueueSize);
    }

    /// <summary>
    /// Starts the capture pipeline.
    /// </summary>
    /// <param name="targetFps">Target frames per second.</param>
    /// <returns>True if started successfully.</returns>
    public bool Start(int targetFps = 30)
    {
        if (_isRunning)
            throw new InvalidOperationException("Pipeline is already running.");

        if (_disposed)
            throw new ObjectDisposedException(nameof(CapturePipeline));

        // Initialize capturer
        if (!_capturer.Initialize())
        {
            Log.Error("Failed to initialize capturer");
            return false;
        }

        var (outputWidth, outputHeight) = OutputSize.Compute(_capturer.Width, _capturer.Height, _maxOutputWidth);
        _output = new OutputDims(outputWidth, outputHeight);

        // Initialize video source only if not already initialized (e.g., StubVideoSource)
        // For WebRTC scenarios, VideoSource is pre-initialized by WebRTCSession
        if (!_videoSource.IsInitialized)
        {
            if (!_videoSource.Initialize(outputWidth, outputHeight, targetFps))
            {
                Log.Error("Failed to initialize video source");
                return false;
            }
        }
        else
        {
            Log.Information("Video source already initialized, skipping initialization");
        }

        Log.Information("Starting pipeline: capture {Width}x{Height} -> output {OutWidth}x{OutHeight} @ {TargetFps}fps",
            _capturer.Width, _capturer.Height, outputWidth, outputHeight, targetFps);

        _cts = new CancellationTokenSource();
        _isRunning = true;

        _timingReportTimer = new Timer(
            _ => ReportStageTimings(), null,
            TimeSpan.FromSeconds(StageReportIntervalSeconds), TimeSpan.FromSeconds(StageReportIntervalSeconds));

        // Start three worker threads
        _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        _conversionTask = Task.Run(() => ConversionLoopAsync(_cts.Token));
        _transmissionTask = Task.Run(() => TransmissionLoopAsync(_cts.Token));

        return true;
    }

    /// <summary>
    /// Stops the capture pipeline gracefully.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        Log.Information("Stopping pipeline...");

        _cts?.Cancel();
        _timingReportTimer?.Dispose();
        _timingReportTimer = null;

        // Wait for all tasks to complete
        try
        {
            await Task.WhenAll(_captureTask!, _conversionTask!, _transmissionTask!);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during shutdown");
        }

        _isRunning = false;

        PrintStatistics();
    }

    /// <summary>
    /// Capture loop: Acquires frames from screen and queues them for conversion.
    /// </summary>
    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        Log.Information("Capture thread started");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ApplyPendingSwitch();

                // Try to acquire frame. Only successful acquisitions are timed: a failed attempt just
                // means "no new frame yet" and would skew the average.
                var acquireStart = Stopwatch.GetTimestamp();
                if (_capturer.TryAcquireFrame(out var frame))
                {
                    _acquireTimings.Record(Stopwatch.GetTimestamp() - acquireStart);

                    if (Interlocked.Increment(ref _framesCaptured) % 100 == 0)
                    {
                        Log.Debug("Captured {Count} frames so far", _framesCaptured);
                    }

                    // Try to add to queue (non-blocking)
                    if (!_captureQueue.TryAdd((frame!, _generation), 0, cancellationToken))
                    {
                        // Queue full, drop frame and release
                        Interlocked.Increment(ref _framesCaptureSkipped);
                        frame!.Dispose();
                        _capturer.ReleaseFrame();
                    }
                    else
                    {
                        _capturer.ReleaseFrame();
                    }
                }

                // Small delay to avoid spinning
                await Task.Delay(CaptureLoopDelayMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        finally
        {
            // A switch requested right before shutdown will never be applied: release its capturer and its caller.
            lock (_switchLock)
            {
                _pendingCapturer?.Dispose();
                _pendingSwitch?.TrySetResult(false);
                (_pendingCapturer, _pendingSwitch) = (null, null);
            }

            _captureQueue.CompleteAdding();
            Log.Information("Capture thread stopped");
        }
    }

    /// <summary>
    /// Replaces the capturer while the pipeline keeps running (display switch): the encoder and the peer
    /// connection stay, the next frames simply come from <paramref name="next"/> and a keyframe is requested.
    /// The pipeline owns <paramref name="next"/> from here on (it is disposed when the switch fails).
    /// </summary>
    /// <param name="next">The capturer for the new display.</param>
    /// <param name="canCommit">Checked on the capture thread right before the swap; when it returns false the switch is cancelled
    /// (the viewer lost the right to switch while the new capturer was starting).</param>
    /// <param name="onFirstFrameSent">Runs on the transmission thread after the first frame of the new display was sent, never before:
    /// this is when pointer input may be mapped to the new display.</param>
    /// <returns>True when <paramref name="next"/> initialised and is now the active capturer; false otherwise (the old one keeps running).</returns>
    public Task<bool> SwitchCapturerAsync(IScreenCapturer next, Func<bool>? canCommit = null, Action? onFirstFrameSent = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_switchLock)
        {
            if (!_isRunning || _disposed || _cts!.IsCancellationRequested || _pendingCapturer != null)
            {
                next.Dispose();
                return Task.FromResult(false);
            }

            _pendingCapturer = next;
            _pendingGuard = canCommit;
            _pendingOnFirstFrame = onFirstFrameSent;
            _pendingSwitch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pendingSwitch.Task;
        }
    }

    /// <summary>
    /// Runs at the top of the capture loop, i.e. when no frame is held, so the old capturer can be disposed safely.
    /// </summary>
    private void ApplyPendingSwitch()
    {
        IScreenCapturer? next;
        TaskCompletionSource<bool>? result;
        Func<bool>? guard;
        Action? onFirstFrame;
        lock (_switchLock)
        {
            (next, result, guard, onFirstFrame) = (_pendingCapturer, _pendingSwitch, _pendingGuard, _pendingOnFirstFrame);
            (_pendingCapturer, _pendingSwitch, _pendingGuard, _pendingOnFirstFrame) = (null, null, null, null);
        }

        if (next == null)
            return;

        var ok = false;
        try
        {
            ok = next.Initialize();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Switching capturer failed");
        }

        if (!ok)
        {
            Log.Warning("New capturer failed to initialize; keeping the current one");
            next.Dispose();
            result!.SetResult(false);
            return;
        }

        if (guard != null && !guard())
        {
            Log.Warning("Display switch cancelled: no longer allowed");
            next.Dispose();
            result!.SetResult(false);
            return;
        }

        var old = _capturer;
        _capturer = next;
        old.Dispose();

        var (width, height) = OutputSize.Compute(next.Width, next.Height, _maxOutputWidth);
        _output = new OutputDims(width, height);
        // Frames of the old display still waiting for conversion would be squeezed into the new size.
        while (_captureQueue.TryTake(out var stale))
            stale.Frame.Dispose();
        // Frames already converted or being converted carry the old generation and are dropped by the later stages.
        var generation = _generation + 1;
        if (onFirstFrame != null)
            _firstFrameCallback = new FirstFrame(generation, onFirstFrame);
        _generation = generation;
        Interlocked.Exchange(ref _keyFramePending, 1);
        Log.Information("Capturer switched: capture {Width}x{Height} -> output {OutWidth}x{OutHeight}",
            next.Width, next.Height, width, height);
        result!.SetResult(true);
    }

    /// <summary>
    /// Conversion loop: Converts frames from BGRA to I420 format.
    /// </summary>
    private Task ConversionLoopAsync(CancellationToken cancellationToken)
    {
        Log.Information("Conversion thread started");

        try
        {
            foreach (var (frame, generation) in _captureQueue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    if (generation != _generation)
                        continue; // captured before a display switch

                    var convertStart = Stopwatch.GetTimestamp();
                    var output = _output;
                    var videoFrame = frame.Width == output.Width && frame.Height == output.Height
                        ? _converter.Convert(frame)
                        : _converter.ConvertAndScale(frame, output.Width, output.Height);
                    _convertTimings.Record(Stopwatch.GetTimestamp() - convertStart);
                    _lastOutputWidth = videoFrame.Width;
                    _lastOutputHeight = videoFrame.Height;
                    Interlocked.Increment(ref _framesConverted);

                    // Try to add to transmission queue
                    if (!_conversionQueue.TryAdd((videoFrame, generation), 0, cancellationToken))
                    {
                        // Queue full, drop frame
                        videoFrame.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Conversion error");
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        finally
        {
            _conversionQueue.CompleteAdding();
            Log.Information("Conversion thread stopped");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Transmission loop: Pushes converted frames to WebRTC video source.
    /// </summary>
    private Task TransmissionLoopAsync(CancellationToken cancellationToken)
    {
        Log.Information("Transmission thread started");

        try
        {
            var lastWidth = 0;
            var lastHeight = 0;
            foreach (var (videoFrame, generation) in _conversionQueue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    if (generation != _generation)
                        continue; // converted before a display switch

                    // After a display switch the picture is unrelated to the previous one, and a size change may
                    // reset the encoder: either way the viewer needs a keyframe to start from.
                    var sizeChanged = lastWidth != 0 && (videoFrame.Width != lastWidth || videoFrame.Height != lastHeight);
                    (lastWidth, lastHeight) = (videoFrame.Width, videoFrame.Height);
                    if (Interlocked.Exchange(ref _keyFramePending, 0) == 1 || sizeChanged)
                        _videoSource.RequestKeyFrame();

                    // PushFrame encodes (software VP8) and sends synchronously, so this is the encode cost.
                    var pushStart = Stopwatch.GetTimestamp();
                    var pushed = _videoSource.PushFrame(videoFrame);
                    if (pushed)
                    {
                        // Rejected pushes (no viewer, frame-rate limit) return almost instantly and would
                        // drag the encode average down, so only real pushes are timed.
                        _pushTimings.Record(Stopwatch.GetTimestamp() - pushStart);
                        Interlocked.Increment(ref _framesTransmitted);
                        if (_firstFrameCallback is { } first && generation >= first.Generation)
                        {
                            _firstFrameCallback = null;
                            first.Callback();
                        }
                    }
                    else
                    {
                        // Backpressure from WebRTC
                        Interlocked.Increment(ref _framesDroppedBackpressure);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Transmission error");
                }
                finally
                {
                    videoFrame.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        finally
        {
            Log.Information("Transmission thread stopped");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Logs average and max time per stage since the previous report, then resets the counters.
    /// </summary>
    private void ReportStageTimings()
    {
        var acquire = _acquireTimings.TakeSnapshot();
        var convert = _convertTimings.TakeSnapshot();
        var push = _pushTimings.TakeSnapshot();
        var viewers = _videoSource is MultiClientVideoSource multi ? multi.ConnectedClientCount : -1;

        Log.Information(
            "Pipeline timing (last {Seconds}s): frames={Frames}, output={Width}x{Height}, viewers={Viewers}, " +
            "acquire avg/max={AcquireAvg:F1}/{AcquireMax:F1}ms, convert avg/max={ConvertAvg:F1}/{ConvertMax:F1}ms, " +
            "push(encode+send) avg/max={PushAvg:F1}/{PushMax:F1}ms",
            StageReportIntervalSeconds, push.Count, _lastOutputWidth, _lastOutputHeight, viewers,
            acquire.AvgMs, acquire.MaxMs, convert.AvgMs, convert.MaxMs, push.AvgMs, push.MaxMs);
    }

    /// <summary>
    /// Accumulator for one stage's timings. Record is called from one pipeline thread and TakeSnapshot from
    /// the timer thread. The lock keeps count, total and max consistent with each other; it is taken once per
    /// frame and almost never contended, so its cost is negligible next to conversion or encoding.
    /// </summary>
    private sealed class StageTimings
    {
        private readonly object _lock = new();
        private long _count;
        private long _totalTicks;
        private long _maxTicks;

        public void Record(long ticks)
        {
            lock (_lock)
            {
                _count++;
                _totalTicks += ticks;
                if (ticks > _maxTicks)
                    _maxTicks = ticks;
            }
        }

        public (long Count, double AvgMs, double MaxMs) TakeSnapshot()
        {
            long count, total, max;
            lock (_lock)
            {
                (count, total, max) = (_count, _totalTicks, _maxTicks);
                _count = _totalTicks = _maxTicks = 0;
            }

            var toMs = 1000.0 / Stopwatch.Frequency;
            return (count, count > 0 ? total * toMs / count : 0, max * toMs);
        }
    }

    /// <summary>
    /// Prints pipeline statistics to console.
    /// </summary>
    private void PrintStatistics()
    {
        var successRate = _framesCaptured > 0 ? (_framesTransmitted * 100.0) / _framesCaptured : 0;
        var videoStats = _videoSource.GetStatistics();

        Log.Information(
            "Final statistics: Captured={FramesCaptured}, Skipped={FramesSkipped}, Converted={FramesConverted}, " +
            "Transmitted={FramesTransmitted}, DroppedBackpressure={FramesDropped}, SuccessRate={SuccessRate:F2}%, " +
            "VideoSourceFps={VideoSourceFps:F2}",
            _framesCaptured, _framesCaptureSkipped, _framesConverted,
            _framesTransmitted, _framesDroppedBackpressure, successRate,
            videoStats.CurrentFps);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_isRunning)
        {
            // Use GetAwaiter().GetResult() instead of Wait() to avoid deadlocks
            // This properly unwraps AggregateException and preserves stack traces
            StopAsync().GetAwaiter().GetResult();
        }

        _timingReportTimer?.Dispose();
        _cts?.Dispose();
        _captureQueue?.Dispose();
        _conversionQueue?.Dispose();

        _capturer?.Dispose();
        _videoSource?.Dispose();

        _disposed = true;
    }
}
