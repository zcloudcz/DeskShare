using System.Diagnostics;
using DeskShare.Common.Interfaces;
using DeskShare.Common.Models;

namespace DeskShare.ScreenSenderApp.Video;

/// <summary>
/// Stub implementation of IVideoSource for testing capture pipeline without WebRTC.
/// Logs frame statistics instead of actually transmitting over network.
/// </summary>
/// <remarks>
/// This is a placeholder implementation. Replace with actual WebRTC ExternalVideoTrackSource
/// when integrating with Microsoft.MixedReality.WebRTC or SIPSorcery.WebRTC.
/// </remarks>
public sealed class StubVideoSource : IVideoSource
{
    private readonly object _statsLock = new();
    private bool _initialized;
    private bool _disposed;

    private int _width;
    private int _height;
    private int _targetFrameRate;

    private long _framesPushed;
    private long _framesDropped;
    private readonly Stopwatch _statsTimer = Stopwatch.StartNew();
    private DateTime _lastFrameTime = DateTime.MinValue;

    /// <inheritdoc/>
    public bool IsActive => _initialized && !_disposed;

    /// <inheritdoc/>
    public bool IsInitialized => _initialized;

    /// <inheritdoc/>
    public bool Initialize(int width, int height, int frameRate)
    {
        if (_initialized)
            throw new InvalidOperationException("VideoSource is already initialized.");

        if (width <= 0 || width % 2 != 0)
            throw new ArgumentException("Width must be positive and even.", nameof(width));

        if (height <= 0 || height % 2 != 0)
            throw new ArgumentException("Height must be positive and even.", nameof(height));

        if (frameRate <= 0 || frameRate > 120)
            throw new ArgumentException("Frame rate must be between 1 and 120.", nameof(frameRate));

        _width = width;
        _height = height;
        _targetFrameRate = frameRate;
        _initialized = true;

        Console.WriteLine($"[StubVideoSource] Initialized: {_width}x{_height} @ {_targetFrameRate}fps");
        Console.WriteLine($"[StubVideoSource] NOTE: This is a stub. Frames will be logged, not transmitted.");
        Console.WriteLine($"[StubVideoSource] Replace with actual WebRTC implementation for real streaming.");

        return true;
    }

    /// <inheritdoc/>
    public bool PushFrame(VideoFrame frame)
    {
        if (!_initialized)
            throw new InvalidOperationException("VideoSource is not initialized.");

        if (_disposed)
            throw new ObjectDisposedException(nameof(StubVideoSource));

        if (frame == null)
            throw new ArgumentNullException(nameof(frame));

        // Simulate backpressure - drop frames if pushing too fast
        var now = DateTime.UtcNow;
        var minFrameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFrameRate);

        if (_lastFrameTime != DateTime.MinValue)
        {
            var timeSinceLastFrame = now - _lastFrameTime;
            if (timeSinceLastFrame < minFrameInterval * 0.8) // Allow 20% tolerance
            {
                // Simulating backpressure - too fast
                lock (_statsLock)
                {
                    _framesDropped++;
                }
                return false;
            }
        }

        _lastFrameTime = now;

        lock (_statsLock)
        {
            _framesPushed++;

            // Log statistics every 30 frames
            if (_framesPushed % 30 == 0)
            {
                var elapsed = _statsTimer.Elapsed.TotalSeconds;
                var actualFps = _framesPushed / elapsed;

                Console.WriteLine(
                    $"[StubVideoSource] Stats: {_framesPushed} frames pushed, " +
                    $"{_framesDropped} dropped, " +
                    $"FPS: {actualFps:F2}, " +
                    $"Timestamp: {frame.Timestamp:HH:mm:ss.fff}");
            }
        }

        // Simulate some processing time (WebRTC encoding overhead)
        Thread.Sleep(2);

        return true;
    }

    /// <inheritdoc/>
    public VideoSourceStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            var elapsed = _statsTimer.Elapsed.TotalSeconds;
            var currentFps = elapsed > 0 ? _framesPushed / elapsed : 0;

            return new VideoSourceStatistics
            {
                FramesPushed = _framesPushed,
                FramesDropped = _framesDropped,
                CurrentFps = currentFps,
                BitrateKbps = 0, // Not applicable for stub
                AverageFrameTimeMs = currentFps > 0 ? 1000.0 / currentFps : 0
            };
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        var stats = GetStatistics();
        Console.WriteLine($"[StubVideoSource] Disposing...");
        Console.WriteLine($"[StubVideoSource] Final stats: {stats.FramesPushed} frames, " +
                         $"{stats.FramesDropped} dropped, " +
                         $"Avg FPS: {stats.CurrentFps:F2}");

        _disposed = true;
    }
}
