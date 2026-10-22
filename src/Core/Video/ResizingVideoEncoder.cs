using Serilog;
using SIPSorceryMedia.Abstractions;

namespace DeskShare.Core.Video;

/// <summary>
/// Gives a video encoder that is fixed to the size of its first frame (both VP8 encoders are: they keep encoding at
/// the first size and ignore later frame sizes) the ability to follow a resolution change, which a display switch
/// causes. On a size change the wrapped encoder is replaced by a new one, in the codec the viewer negotiated, and
/// starts with a keyframe. The peer connection is untouched: it only sees the wrapper's encoded-sample event.
/// </summary>
internal sealed class ResizingVideoEncoder : IVideoSource, IDisposable
{
    private readonly Func<IVideoSource> _create;
    private IVideoSource _inner;
    private VideoFormat? _negotiated; // as the caller passed it, i.e. in the current VideoCodecsEnum numbering
    private int _width, _height;
    // One lock for encode, recreate and dispose: a client being removed (Dispose) while a frame is being pushed
    // (possibly in the middle of Recreate) waits for it, then disposes whatever encoder is current. Nothing leaks.
    private readonly object _lock = new();
    private bool _disposed;

    /// <param name="create">Makes a fresh encoder; called again for every size change.</param>
    public ResizingVideoEncoder(Func<IVideoSource> create)
    {
        _create = create;
        _inner = Attach(create());
    }

    private IVideoSource Attach(IVideoSource encoder)
    {
        encoder.OnVideoSourceEncodedSample += Forward;
        return encoder;
    }

    private void Forward(uint durationRtpUnits, byte[] sample) => OnVideoSourceEncodedSample?.Invoke(durationRtpUnits, sample);

    // The legacy-numbered VP8 encoder needs its formats translated (see VideoEncoderFactory); the wrapper does it for its callers.
    public List<VideoFormat> GetVideoSourceFormats() => VideoEncoderFactory.AdvertisedFormats(_inner);

    public void SetVideoSourceFormat(VideoFormat videoFormat)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _negotiated = videoFormat;
            _inner.SetVideoSourceFormat(VideoEncoderFactory.ToEncoder(_inner, videoFormat));
        }
    }

    public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            if (_width != 0 && (width != _width || height != _height))
            {
                // The hardware H.264 encoder rebuilds its own context per size; only the VP8 encoders need replacing.
                if (_inner is not HybridVideoEncoder { ActiveCodec: VideoCodecsEnum.H264 })
                    Recreate(width, height);
            }
            (_width, _height) = (width, height);
            _inner.ExternalVideoSourceRawSample(durationMilliseconds, width, height, sample, pixelFormat);
        }
    }

    private void Recreate(int width, int height)
    {
        Log.Information("Video size changed {OldW}x{OldH} -> {W}x{H}; creating a new encoder", _width, _height, width, height);
        _inner.OnVideoSourceEncodedSample -= Forward;
        (_inner as IDisposable)?.Dispose();
        _inner = Attach(_create());
        if (_negotiated is { } negotiated)
            _inner.SetVideoSourceFormat(VideoEncoderFactory.ToEncoder(_inner, negotiated));
        _inner.ForceKeyFrame();
    }

    public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage) =>
        _inner.ExternalVideoSourceRawSampleFaster(durationMilliseconds, rawImage);

    public void ForceKeyFrame()
    {
        lock (_lock)
        {
            if (!_disposed)
                _inner.ForceKeyFrame();
        }
    }
    public void RestrictFormats(Func<VideoFormat, bool> filter) => _inner.RestrictFormats(filter);
    public bool HasEncodedVideoSubscribers() => OnVideoSourceEncodedSample != null;
    public bool IsVideoSourcePaused() => _inner.IsVideoSourcePaused();
    public Task PauseVideo() => _inner.PauseVideo();
    public Task ResumeVideo() => _inner.ResumeVideo();
    public Task StartVideo() => _inner.StartVideo();
    public Task CloseVideo() => _inner.CloseVideo();

    public event EncodedSampleDelegate? OnVideoSourceEncodedSample;
#pragma warning disable CS0067 // part of IVideoSource, never raised by an encoder-only source
    public event RawVideoSampleDelegate? OnVideoSourceRawSample;
    public event RawVideoSampleFasterDelegate? OnVideoSourceRawSampleFaster;
    public event SourceErrorDelegate? OnVideoSourceError;
#pragma warning restore CS0067

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _inner.OnVideoSourceEncodedSample -= Forward;
            (_inner as IDisposable)?.Dispose();
        }
    }
}
