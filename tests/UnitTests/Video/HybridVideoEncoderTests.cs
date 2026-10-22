using DeskShare.Core.Video;
using SIPSorceryMedia.Abstractions;

namespace DeskShare.UnitTests.Video;

/// <summary>
/// Codec selection (Capture:Codec), the SDP format list and the VP8 fallback. The VP8 half runs through FFmpeg
/// so it works on any OS; the DLLs are gitignored, hence the skip when the folder is missing.
/// </summary>
public sealed class HybridVideoEncoderTests
{
    private static string? FindFfmpegFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DeskShare.DesktopAvalonia", "ffmpeg");
            if (Directory.Exists(candidate))
                return Directory.EnumerateFiles(candidate, "avcodec*").Any() ? candidate : null;
        }
        return null;
    }

    private sealed class RequiresFfmpegFactAttribute : FactAttribute
    {
        public RequiresFfmpegFactAttribute()
        {
            if (FindFfmpegFolder() == null)
                Skip = "FFmpeg libraries not found in src/DeskShare.DesktopAvalonia/ffmpeg (download them first).";
        }
    }

    [Theory]
    [InlineData("vp8", VideoCodecPreference.Vp8)]
    [InlineData("h264", VideoCodecPreference.H264)]
    [InlineData("H264", VideoCodecPreference.H264)]
    [InlineData("auto", VideoCodecPreference.Auto)]
    [InlineData("", VideoCodecPreference.Vp8)]
    [InlineData(null, VideoCodecPreference.Vp8)]
    [InlineData("av1", VideoCodecPreference.Vp8)] // unknown value: keep the safe default
    public void ParsePreference_MapsConfigValue(string? value, VideoCodecPreference expected) =>
        Assert.Equal(expected, VideoEncoderFactory.ParsePreference(value));

    [RequiresFfmpegFact]
    public void Vp8Preference_NeverProbesHardware_AndAdvertisesOnlyVp8()
    {
        var probed = false;
        var encoder = VideoEncoderFactory.Create(true, FindFfmpegFolder(), VideoCodecPreference.Vp8, () => { probed = true; return "h264_amf"; });
        using var _ = (IDisposable)encoder;

        Assert.False(probed);
        Assert.Equal(VideoCodecsEnum.VP8, Assert.Single(VideoEncoderFactory.AdvertisedFormats(encoder)).Codec);
    }

    [RequiresFfmpegFact]
    public void NoHardwareEncoder_FallsBackToVp8Only()
    {
        foreach (var preference in new[] { VideoCodecPreference.H264, VideoCodecPreference.Auto })
        {
            var encoder = VideoEncoderFactory.Create(true, FindFfmpegFolder(), preference, () => null);
            using var _ = (IDisposable)encoder;

            Assert.Equal(VideoCodecsEnum.VP8, Assert.Single(VideoEncoderFactory.AdvertisedFormats(encoder)).Codec);
        }
    }

    [RequiresFfmpegFact]
    public void HardwareEncoder_AdvertisesH264BeforeVp8_AndFollowsTheNegotiatedCodec()
    {
        var encoder = VideoEncoderFactory.Create(true, FindFfmpegFolder(), VideoCodecPreference.H264, () => "h264_amf");
        using var _ = (IDisposable)encoder;
        var hybrid = Assert.IsType<HybridVideoEncoder>(encoder);

        var formats = VideoEncoderFactory.AdvertisedFormats(encoder);
        Assert.Equal(new[] { VideoCodecsEnum.H264, VideoCodecsEnum.VP8 }, formats.Select(f => f.Codec));
        // Browsers only decode H.264 packetised as FU-A/STAP-A (mode 1) and need a profile-level-id.
        Assert.Contains("packetization-mode=1", formats[0].Parameters);
        Assert.Contains("profile-level-id=42e01f", formats[0].Parameters);

        // Whatever the viewer picks in its answer, the encoder has to follow.
        encoder.SetVideoSourceFormat(VideoEncoderFactory.ToEncoder(encoder, formats[0]));
        Assert.Equal(VideoCodecsEnum.H264, hybrid.ActiveCodec);
        encoder.SetVideoSourceFormat(VideoEncoderFactory.ToEncoder(encoder, formats[1]));
        Assert.Equal(VideoCodecsEnum.VP8, hybrid.ActiveCodec);
    }

    [RequiresFfmpegFact]
    public void HardwareEncoder_WhenViewerPicksVp8_StillEncodesVp8()
    {
        var encoder = VideoEncoderFactory.Create(true, FindFfmpegFolder(), VideoCodecPreference.H264, () => "h264_amf");
        using var _ = (IDisposable)encoder;
        var formats = VideoEncoderFactory.AdvertisedFormats(encoder);
        encoder.SetVideoSourceFormat(formats[1]);
        var samples = new List<byte[]>();
        encoder.OnVideoSourceEncodedSample += (_, data) => samples.Add(data);

        encoder.ForceKeyFrame();
        encoder.ExternalVideoSourceRawSample(33, 640, 480, new byte[640 * 480 * 3 / 2], VideoPixelFormatsEnum.I420);

        // VP8 key frame start code, same check as the plain VP8 test.
        Assert.Equal(new byte[] { 0x9D, 0x01, 0x2A }, Assert.Single(samples).Skip(3).Take(3));
    }

    /// <summary>Real GPU encode; only meaningful on a machine with AMD/NVIDIA/Intel encoder, otherwise returns early.</summary>
    [RequiresFfmpegFact]
    public void RealHardwareEncoder_ProducesAnnexBStreamWithKeyframeOnDemand()
    {
        var encoder = VideoEncoderFactory.Create(true, FindFfmpegFolder(), VideoCodecPreference.Auto);
        using var _ = (IDisposable)encoder;
        if (encoder is not HybridVideoEncoder hybrid)
            return; // no GPU encoder here (CI runner, container): nothing to verify

        hybrid.SetVideoSourceFormat(VideoEncoderFactory.AdvertisedFormats(hybrid)[0]);
        var samples = new List<byte[]>();
        hybrid.OnVideoSourceEncodedSample += (_, data) => samples.Add(data);

        const int width = 640, height = 480;
        var i420 = new byte[width * height * 3 / 2];
        for (var frame = 0; frame < 8; frame++)
        {
            for (var i = 0; i < width * height; i++)
                i420[i] = (byte)(i % width / 4 + frame * 9);
            Array.Fill(i420, (byte)128, width * height, i420.Length - width * height);
            if (frame == 5)
                hybrid.ForceKeyFrame();
            hybrid.ExternalVideoSourceRawSample(33, width, height, i420, VideoPixelFormatsEnum.I420);
        }

        // Hardware encoders may swallow the first frame (internal pipeline), so not exactly 8 samples.
        Assert.True(samples.Count >= 6, $"only {samples.Count} samples");
        Assert.True(HasNal(samples[0], 5) && HasNal(samples[0], 7) && HasNal(samples[0], 8), "first sample must be SPS+PPS+IDR");
        Assert.Contains(samples.Skip(1), s => HasNal(s, 5)); // the forced keyframe, again with SPS/PPS in front
    }

    [RequiresFfmpegFact]
    public void RealHardwareEncoder_ColoursSurviveEncodeAndDecode()
    {
        (VideoEncoderFactory.Create(true, FindFfmpegFolder(), VideoCodecPreference.Vp8) as IDisposable)?.Dispose(); // initialises FFmpeg
        var name = HardwareH264Encoder.FindAvailable();
        if (name == null)
            return; // no GPU encoder here
        Assert.True(NativeH264Context.EncodesColoursCorrectly(name, HardwareH264Encoder.OptionsFor(name), out var detail), detail);
    }

    private sealed class FakeContext(Func<byte[]?> encode) : IH264Context
    {
        public bool Disposed;
        public byte[]? Encode(byte[] i420, bool forceKeyFrame) => encode();
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void PersistentFailure_RebuildsContextOnce_ThenGivesUp()
    {
        var contexts = new List<FakeContext>();
        var broken = false;
        using var encoder = new HardwareH264Encoder("fake", (_, _) =>
        {
            var c = new FakeContext(() => broken ? throw new InvalidOperationException("gpu gone") : new byte[] { 1 });
            contexts.Add(c);
            return c;
        });
        var failedRaised = 0;
        encoder.HardwareFailed += () => failedRaised++;
        var sent = 0;
        encoder.OnVideoSourceEncodedSample += (_, _) => sent++;
        void Push() => encoder.ExternalVideoSourceRawSample(33, 64, 64, new byte[64 * 64 * 3 / 2], VideoPixelFormatsEnum.I420);

        Push();
        Assert.Equal(1, sent);

        broken = true;
        for (var i = 0; i < HardwareH264Encoder.FailuresBeforeRecreate; i++) Push();
        Assert.False(encoder.Failed);          // first persistent failure: only rebuilds
        Assert.True(contexts[0].Disposed);

        for (var i = 0; i < HardwareH264Encoder.FailuresBeforeRecreate; i++) Push();
        Assert.True(encoder.Failed);           // the rebuilt context failed as well
        Assert.Equal(1, failedRaised);
        Assert.Equal(2, contexts.Count);

        var before = sent;
        Push();                                // after giving up nothing is encoded or retried
        Assert.Equal(before, sent);
        Assert.Equal(2, contexts.Count);
        HardwareH264Encoder.HardwareDisabled = false; // process-wide flag, do not leak into other tests
    }

    [Fact]
    public void NullOutputAfterRebuild_IsNotRecovery_SoTheEncoderEventuallyGivesUp()
    {
        // Every new context buffers one frame (null) and then keeps throwing.
        using var encoder = new HardwareH264Encoder("fake", (_, _) =>
        {
            var first = true;
            return new FakeContext(() =>
            {
                if (!first) throw new InvalidOperationException("gpu gone");
                first = false;
                return null;
            });
        });

        for (var i = 0; i < HardwareH264Encoder.FailuresBeforeRecreate * 3 + 5; i++)
            encoder.ExternalVideoSourceRawSample(33, 64, 64, new byte[64 * 64 * 3 / 2], VideoPixelFormatsEnum.I420);

        try { Assert.True(encoder.Failed); }
        finally { HardwareH264Encoder.HardwareDisabled = false; }
    }

    [Fact]
    public void TruncatedI420Buffer_IsDroppedWithoutTouchingTheEncoder()
    {
        var created = 0;
        using var encoder = new HardwareH264Encoder("fake", (_, _) => { created++; return new FakeContext(() => new byte[] { 1 }); });
        var sent = 0;
        encoder.OnVideoSourceEncodedSample += (_, _) => sent++;

        encoder.ExternalVideoSourceRawSample(33, 64, 64, new byte[64 * 64], VideoPixelFormatsEnum.I420);   // Y plane only
        encoder.ExternalVideoSourceRawSample(33, 63, 64, new byte[64 * 64 * 2], VideoPixelFormatsEnum.I420); // odd width
        encoder.ExternalVideoSourceRawSample(33, 0, 0, Array.Empty<byte>(), VideoPixelFormatsEnum.I420);

        Assert.Equal(0, sent);
        Assert.Equal(0, created);
    }

    [RequiresFfmpegFact]
    public void NativeContext_RejectsTruncatedBuffer_InsteadOfReadingPastIt()
    {
        VideoEncoderFactory.Create(true, FindFfmpegFolder(), VideoCodecPreference.Vp8); // initialises FFmpeg
        var name = HardwareH264Encoder.FindAvailable();
        if (name == null)
            return;
        using var ctx = new NativeH264Context(name, HardwareH264Encoder.OptionsFor(name), 320, 192, 1_000_000);
        Assert.Throws<ArgumentException>(() => ctx.Encode(new byte[320 * 192], false));
    }

    [RequiresFfmpegFact]
    public void AfterPermanentGpuFailure_NewEncodersAdvertiseVp8Only()
    {
        try
        {
            using var broken = new HardwareH264Encoder("fake", (_, _) => new FakeContext(() => throw new InvalidOperationException("gpu gone")));
            for (var i = 0; i < HardwareH264Encoder.FailuresBeforeRecreate * 2; i++)
                broken.ExternalVideoSourceRawSample(33, 64, 64, new byte[64 * 64 * 3 / 2], VideoPixelFormatsEnum.I420);
            Assert.True(broken.Failed);

            // Default probe: must not hand out the GPU encoder again, whatever the cached probe result was.
            var next = VideoEncoderFactory.Create(true, FindFfmpegFolder(), VideoCodecPreference.H264);
            using var _ = (IDisposable)next;
            Assert.Equal(VideoCodecsEnum.VP8, Assert.Single(VideoEncoderFactory.AdvertisedFormats(next)).Codec);

            // An encoder that already exists (second client of MultiClientVideoSource) stops offering H.264 too.
            var existing = new HybridVideoEncoder(VideoEncoderFactory.Create(true, FindFfmpegFolder(), VideoCodecPreference.Vp8), new HardwareH264Encoder("h264_amf"));
            using var __ = existing;
            Assert.Equal(VideoCodecsEnum.VP8, Assert.Single(existing.GetVideoSourceFormats()).Codec);
        }
        finally { HardwareH264Encoder.HardwareDisabled = false; }
    }

    [Fact]
    public void RecoveryAfterRebuild_ResetsTheFailureState()
    {
        var calls = 0;
        using var encoder = new HardwareH264Encoder("fake", (_, _) =>
            new FakeContext(() => ++calls <= HardwareH264Encoder.FailuresBeforeRecreate ? throw new InvalidOperationException("glitch") : new byte[] { 1 }));
        var sent = 0;
        encoder.OnVideoSourceEncodedSample += (_, _) => sent++;

        for (var i = 0; i < HardwareH264Encoder.FailuresBeforeRecreate + 5; i++)
            encoder.ExternalVideoSourceRawSample(33, 64, 64, new byte[64 * 64 * 3 / 2], VideoPixelFormatsEnum.I420);

        Assert.False(encoder.Failed);
        Assert.Equal(5, sent);
    }

    private static bool HasNal(byte[] annexB, int nalType)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1 && (annexB[i + 3] & 0x1F) == nalType)
                return true;
        return false;
    }
}
