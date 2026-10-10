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

    private readonly IScreenCapturer _capturer;
    private readonly IFrameConverter _converter;
    private readonly IVideoSource _videoSource;
    private readonly int _maxOutputWidth;

    // Resolution actually sent; equals the capture size unless downscaling is active. Set in Start.
    private int _outputWidth;
    private int _outputHeight;

    private readonly BlockingCollection<Frame> _captureQueue;
    private readonly BlockingCollection<VideoFrame> _conversionQueue;

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

        _captureQueue = new BlockingCollection<Frame>(MaxQueueSize);
        _conversionQueue = new BlockingCollection<VideoFrame>(MaxQueueSize);
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

        (_outputWidth, _outputHeight) = OutputSize.Compute(_capturer.Width, _capturer.Height, _maxOutputWidth);

        // Initialize video source only if not already initialized (e.g., StubVideoSource)
        // For WebRTC scenarios, VideoSource is pre-initialized by WebRTCSession
        if (!_videoSource.IsInitialized)
        {
            if (!_videoSource.Initialize(_outputWidth, _outputHeight, targetFps))
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
            _capturer.Width, _capturer.Height, _outputWidth, _outputHeight, targetFps);

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
                    if (!_captureQueue.TryAdd(frame!, 0, cancellationToken))
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
            _captureQueue.CompleteAdding();
            Log.Information("Capture thread stopped");
        }
    }

    /// <summary>
    /// Conversion loop: Converts frames from BGRA to I420 format.
    /// </summary>
    private Task ConversionLoopAsync(CancellationToken cancellationToken)
    {
        Log.Information("Conversion thread started");

        try
        {
            foreach (var frame in _captureQueue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    var convertStart = Stopwatch.GetTimestamp();
                    var videoFrame = frame.Width == _outputWidth && frame.Height == _outputHeight
                        ? _converter.Convert(frame)
                        : _converter.ConvertAndScale(frame, _outputWidth, _outputHeight);
                    _convertTimings.Record(Stopwatch.GetTimestamp() - convertStart);
                    _lastOutputWidth = videoFrame.Width;
                    _lastOutputHeight = videoFrame.Height;
                    Interlocked.Increment(ref _framesConverted);

                    // Try to add to transmission queue
                    if (!_conversionQueue.TryAdd(videoFrame, 0, cancellationToken))
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
            foreach (var videoFrame in _conversionQueue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    // PushFrame encodes (software VP8) and sends synchronously, so this is the encode cost.
                    var pushStart = Stopwatch.GetTimestamp();
                    var pushed = _videoSource.PushFrame(videoFrame);
                    if (pushed)
                    {
                        // Rejected pushes (no viewer, frame-rate limit) return almost instantly and would
                        // drag the encode average down, so only real pushes are timed.
                        _pushTimings.Record(Stopwatch.GetTimestamp() - pushStart);
                        Interlocked.Increment(ref _framesTransmitted);
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
