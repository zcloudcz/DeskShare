using SIPSorceryMedia.Abstractions;

namespace DeskShare.Core.Video;

/// <summary>
/// Advertises hardware H.264 first and VP8 second, then encodes with whichever codec the viewer picked in the
/// SDP answer. The sender must keep VP8 around: old browsers or a Linux viewer may not offer H.264.
/// </summary>
internal sealed class HybridVideoEncoder : IVideoSource, IDisposable
{
    private readonly IVideoSource _vp8;
    private readonly HardwareH264Encoder _h264;
    private IVideoSource _active;

    /// <param name="vp8">The VP8 encoder from <see cref="VideoEncoderFactory"/> (may use the legacy enum numbering).</param>
    public HybridVideoEncoder(IVideoSource vp8, HardwareH264Encoder h264)
    {
        _vp8 = vp8;
        _h264 = h264;
        _active = vp8;
        _vp8.OnVideoSourceEncodedSample += (d, s) => OnVideoSourceEncodedSample?.Invoke(d, s);
        _h264.OnVideoSourceEncodedSample += (d, s) => OnVideoSourceEncodedSample?.Invoke(d, s);
    }

    // Once the GPU encoder has given up, new negotiations must not offer H.264 (we would have nothing to send under it).
    public List<VideoFormat> GetVideoSourceFormats() =>
        (!_h264.CanAdvertise ? new List<VideoFormat>() : new List<VideoFormat> { HardwareH264Encoder.Format })
            .Concat(VideoEncoderFactory.AdvertisedFormats(_vp8)).ToList();

    public void SetVideoSourceFormat(VideoFormat videoFormat)
    {
        if (videoFormat.Codec == VideoCodecsEnum.H264)
        {
            _active = _h264;
            return;
        }
        _active = _vp8;
        _vp8.SetVideoSourceFormat(VideoEncoderFactory.ToEncoder(_vp8, videoFormat));
    }

    /// <summary>The codec currently being encoded (what the viewer negotiated).</summary>
    public VideoCodecsEnum ActiveCodec => ReferenceEquals(_active, _h264) ? VideoCodecsEnum.H264 : VideoCodecsEnum.VP8;

    public void ForceKeyFrame() => _active.ForceKeyFrame();

    /// <summary>Drops frames still buffered inside the encoder (a new viewer starts). Only the GPU encoder buffers.</summary>
    public void DiscardPending() => _h264.DiscardPending();

    public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat) =>
        _active.ExternalVideoSourceRawSample(durationMilliseconds, width, height, sample, pixelFormat);

    public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage) =>
        _active.ExternalVideoSourceRawSampleFaster(durationMilliseconds, rawImage);

    public void RestrictFormats(Func<VideoFormat, bool> filter) { }
    public bool HasEncodedVideoSubscribers() => OnVideoSourceEncodedSample != null;
    public bool IsVideoSourcePaused() => false;
    public Task PauseVideo() => Task.CompletedTask;
    public Task ResumeVideo() => Task.CompletedTask;
    public Task StartVideo() => Task.CompletedTask;
    public Task CloseVideo() => Task.CompletedTask;

    public event EncodedSampleDelegate? OnVideoSourceEncodedSample;
#pragma warning disable CS0067 // part of IVideoSource, never raised by an encoder-only source
    public event RawVideoSampleDelegate? OnVideoSourceRawSample;
    public event RawVideoSampleFasterDelegate? OnVideoSourceRawSampleFaster;
    public event SourceErrorDelegate? OnVideoSourceError;
#pragma warning restore CS0067

    public void Dispose()
    {
        _h264.Dispose();
        (_vp8 as IDisposable)?.Dispose();
    }
}
