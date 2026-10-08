using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace DeskShare.Core.Video;

/// <summary>
/// Works around a binary mismatch in SIPSorceryMedia.Encoders 10.0.4: it is compiled against
/// SIPSorceryMedia.Abstractions 8.0.12, whose <see cref="VideoCodecsEnum"/> has no leading
/// <c>Unknown</c> member. In Abstractions 10.x every codec value is shifted by one, so the encoder's
/// "VP8" (7) is seen by us, the SDP writer and the browser as H263, and the offer is rejected with
/// "VideoIncompatible". The encoder itself still only understands its own numbering.
///
/// ponytail: enum arithmetic; delete this class once SIPSorceryMedia.Encoders ships a build against
/// Abstractions 10 (the shift then computes to 0 and the shim becomes a no-op).
/// </summary>
public static class EncoderFormatShim
{
    // SupportedFormats is meant to be exactly [VP8]; whatever codec value it reports tells us the offset.
    private static readonly int Shift =
        (int)VideoCodecsEnum.VP8 - (int)VideoEncoderEndPoint.SupportedFormats[0].Codec;

    /// <summary>Formats to advertise in SDP: the encoder's formats expressed in our enum numbering.</summary>
    public static List<VideoFormat> AdvertisedFormats(VideoEncoderEndPoint encoder) =>
        encoder.GetVideoSourceFormats().Select(f => Remap(f, Shift)).ToList();

    /// <summary>A negotiated format (our numbering) expressed in the encoder's numbering.</summary>
    public static VideoFormat ToEncoder(VideoFormat negotiated) => Remap(negotiated, -Shift);

    private static VideoFormat Remap(VideoFormat f, int by) =>
        by == 0 ? f : new VideoFormat((VideoCodecsEnum)((int)f.Codec + by), f.FormatID, f.ClockRate, f.Parameters);
}
