using DeskShare.Core.Video;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace DeskShare.UnitTests.Video;

/// <summary>
/// Guards the VideoCodecsEnum mismatch between SIPSorceryMedia.Encoders and Abstractions 10.x:
/// whatever the encoder package reports, the SDP must advertise VP8 and the encoder must get its own value back.
/// </summary>
public sealed class EncoderFormatShimTests
{
    [Fact]
    public void AdvertisedFormats_ReportVp8ToSdp()
    {
        using var encoder = new VideoEncoderEndPoint();

        var formats = EncoderFormatShim.AdvertisedFormats(encoder);

        Assert.Single(formats);
        Assert.Equal(VideoCodecsEnum.VP8, formats[0].Codec);
        Assert.Equal(96, formats[0].FormatID);
    }

    [Fact]
    public void ToEncoder_RoundTripsToEncoderNumbering()
    {
        var encoderVp8 = VideoEncoderEndPoint.SupportedFormats[0];
        var negotiated = new VideoFormat(VideoCodecsEnum.VP8, encoderVp8.FormatID, encoderVp8.ClockRate);

        var forEncoder = EncoderFormatShim.ToEncoder(negotiated);

        // Same numeric codec value the encoder was compiled with, regardless of what our enum calls it.
        Assert.Equal((int)encoderVp8.Codec, (int)forEncoder.Codec);
        Assert.Equal(96, forEncoder.FormatID);
    }
}
