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

    private static bool HasNal(byte[] annexB, int nalType)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1 && (annexB[i + 3] & 0x1F) == nalType)
                return true;
        return false;
    }
}
