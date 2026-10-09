using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.FFmpeg;

namespace DeskShare.Core.Video;

/// <summary>
/// Creates the VP8 encoder used for WebRTC. SIPSorceryMedia.Encoders ships its native libvpx wrapper (vpxmd.dll)
/// for Windows only, so macOS and Linux encode through FFmpeg instead (the app bundles FFmpeg with libvpx
/// next to the executable on every platform).
/// </summary>
public static class VideoEncoderFactory
{
    /// <summary>
    /// Creates one encoder. Callers own it and must dispose it (both implementations are <see cref="IDisposable"/>).
    /// </summary>
    /// <param name="useFfmpeg">Null picks by platform (FFmpeg everywhere except Windows); true forces FFmpeg, which tests use on Windows.</param>
    /// <param name="ffmpegFolder">Folder with the FFmpeg shared libraries; defaults to "ffmpeg" next to the executable.</param>
    public static IVideoSource Create(bool? useFfmpeg = null, string? ffmpegFolder = null)
    {
        if (!(useFfmpeg ?? !OperatingSystem.IsWindows()))
            return new VideoEncoderEndPoint();

        // Safe to call repeatedly. Without the bundled folder FFmpeg falls back to its own library discovery.
        var folder = ffmpegFolder ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Directory.Exists(folder))
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, folder);

        // VP8 only: the browser must not pick a codec the LGPL FFmpeg build cannot encode (no libx264/libx265).
        return new FfmpegVp8Encoder();
    }

    /// <summary>Formats to advertise in SDP, in the current <see cref="VideoCodecsEnum"/> numbering.</summary>
    public static List<VideoFormat> AdvertisedFormats(IVideoSource encoder) =>
        // Only VideoEncoderEndPoint is compiled against the old Abstractions enum and needs the remap.
        encoder is VideoEncoderEndPoint legacy
            ? EncoderFormatShim.AdvertisedFormats(legacy)
            : encoder.GetVideoSourceFormats();

    /// <summary>A negotiated format (current numbering) expressed in the numbering the encoder understands.</summary>
    public static VideoFormat ToEncoder(IVideoSource encoder, VideoFormat negotiated) =>
        encoder is VideoEncoderEndPoint ? EncoderFormatShim.ToEncoder(negotiated) : negotiated;
}
