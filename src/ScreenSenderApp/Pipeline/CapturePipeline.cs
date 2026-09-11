using System.Collections.Concurrent;
using System.Diagnostics;
using DeskShare.Common.Interfaces;
using DeskShare.Common.Models;

namespace DeskShare.ScreenSenderApp.Pipeline;

/// <summary>
/// Orchestrates the screen capture pipeline: Capture -> Convert -> Push to WebRTC.
/// Uses producer-consumer pattern with bounded queues for backpressure handling.
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private const int MaxQueueSize = 3; // Limit buffering to prevent memory issues
    private const int CaptureLoopDelayMs = 10; // Small delay between capture attempts

    private readonly ICapturer _capturer;
    private readonly IFrameConverter _converter;
    private readonly IVideoSource _videoSource;

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
    public CapturePipeline(ICapturer capturer, IFrameConverter converter, IVideoSource videoSource)
    {
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
            Console.WriteLine("[Pipeline] Failed to initialize capturer.");
            return false;
        }

        // Initialize video source only if not already initialized (e.g., StubVideoSource)
        // For WebRTC scenarios, VideoSource is pre-initialized by WebRTCSession
        if (!_videoSource.IsInitialized)
        {
            if (!_videoSource.Initialize(_capturer.Width, _capturer.Height, targetFps))
            {
                Console.WriteLine("[Pipeline] Failed to initialize video source.");
                return false;
            }
        }
        else
        {
            Console.WriteLine("[Pipeline] Video source already initialized, skipping initialization.");
        }

        Console.WriteLine($"[Pipeline] Starting pipeline: {_capturer.Width}x{_capturer.Height} @ {targetFps}fps");

        _cts = new CancellationTokenSource();
        _isRunning = true;

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

        Console.WriteLine("[Pipeline] Stopping pipeline...");

        _cts?.Cancel();

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
            Console.WriteLine($"[Pipeline] Error during shutdown: {ex.Message}");
        }

        _isRunning = false;

        PrintStatistics();
    }

    /// <summary>
    /// Capture loop: Acquires frames from screen and queues them for conversion.
    /// </summary>
    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Pipeline] Capture thread started.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Try to acquire frame
                if (_capturer.TryAcquireFrame(out var frame))
                {
                    Interlocked.Increment(ref _framesCaptured);

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
            Console.WriteLine("[Pipeline] Capture thread stopped.");
        }
    }

    /// <summary>
    /// Conversion loop: Converts frames from BGRA to I420 format.
    /// </summary>
    private Task ConversionLoopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Pipeline] Conversion thread started.");

        try
        {
            foreach (var frame in _captureQueue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    var videoFrame = _converter.Convert(frame);
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
                    Console.WriteLine($"[Pipeline] Conversion error: {ex.Message}");
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
            Console.WriteLine("[Pipeline] Conversion thread stopped.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Transmission loop: Pushes converted frames to WebRTC video source.
    /// </summary>
    private Task TransmissionLoopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Pipeline] Transmission thread started.");

        try
        {
            foreach (var videoFrame in _conversionQueue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    if (_videoSource.PushFrame(videoFrame))
                    {
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
                    Console.WriteLine($"[Pipeline] Transmission error: {ex.Message}");
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
            Console.WriteLine("[Pipeline] Transmission thread stopped.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Prints pipeline statistics to console.
    /// </summary>
    private void PrintStatistics()
    {
        Console.WriteLine("\n[Pipeline] === Final Statistics ===");
        Console.WriteLine($"  Frames captured:         {_framesCaptured}");
        Console.WriteLine($"  Frames skipped (queue):  {_framesCaptureSkipped}");
        Console.WriteLine($"  Frames converted:        {_framesConverted}");
        Console.WriteLine($"  Frames transmitted:      {_framesTransmitted}");
        Console.WriteLine($"  Frames dropped (bp):     {_framesDroppedBackpressure}");

        if (_framesCaptured > 0)
        {
            var successRate = (_framesTransmitted * 100.0) / _framesCaptured;
            Console.WriteLine($"  Success rate:            {successRate:F2}%");
        }

        var videoStats = _videoSource.GetStatistics();
        Console.WriteLine($"  VideoSource FPS:         {videoStats.CurrentFps:F2}");
        Console.WriteLine("=====================================\n");
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

        _cts?.Dispose();
        _captureQueue?.Dispose();
        _conversionQueue?.Dispose();

        _capturer?.Dispose();
        _videoSource?.Dispose();

        _disposed = true;
    }
}
