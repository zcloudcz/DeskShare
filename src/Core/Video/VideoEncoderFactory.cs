using Serilog;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.FFmpeg;

namespace DeskShare.Core.Video;

/// <summary>Which codec the sender prefers. VP8 is software and the default; H.264 needs a GPU encoder.</summary>
public enum VideoCodecPreference
{
    /// <summary>VP8 only (default).</summary>
    Vp8,
    /// <summary>Hardware H.264 when an encoder initialises (warns if not), VP8 stays advertised as the fallback.</summary>
    H264,
    /// <summary>Same as <see cref="H264"/> but a missing hardware encoder is only an informational message.</summary>
    Auto
}

/// <summary>
/// Creates the VP8 encoder used for WebRTC, optionally combined with a hardware H.264 encoder. SIPSorceryMedia.Encoders ships its native libvpx wrapper (vpxmd.dll)
/// for Windows only, so macOS and Linux encode through FFmpeg instead (the app bundles FFmpeg with libvpx
/// next to the executable on every platform).
/// </summary>
public static class VideoEncoderFactory
{
    /// <summary>
    /// Codec preference for every encoder created from now on. Set once at startup by ScreenSenderService from
    /// "Capture:Codec"; a static because the encoders are created deep inside the WebRTC sessions.
    /// </summary>
    public static VideoCodecPreference Preference { get; set; } = VideoCodecPreference.Vp8;

    /// <summary>Parses "vp8" | "h264" | "auto" (case-insensitive); anything else, including null, means VP8.</summary>
    public static VideoCodecPreference ParsePreference(string? value) =>
        Enum.TryParse<VideoCodecPreference>(value, ignoreCase: true, out var p) ? p : VideoCodecPreference.Vp8;

    /// <summary>
    /// Creates one encoder. Callers own it and must dispose it (all implementations are <see cref="IDisposable"/>).
    /// </summary>
    /// <param name="useFfmpeg">Null picks by platform (FFmpeg everywhere except Windows); true forces FFmpeg, which tests use on Windows.</param>
    /// <param name="ffmpegFolder">Folder with the FFmpeg shared libraries; defaults to "ffmpeg" next to the executable.</param>
    /// <param name="codec">Null uses <see cref="Preference"/>.</param>
    /// <param name="probeHardware">Test seam: returns the hardware H.264 encoder name or null. Defaults to a real probe.</param>
    public static IVideoSource Create(bool? useFfmpeg = null, string? ffmpegFolder = null,
        VideoCodecPreference? codec = null, Func<string?>? probeHardware = null)
    {
        var vp8 = CreateVp8(useFfmpeg, ffmpegFolder);
        var preference = codec ?? Preference;
        if (preference == VideoCodecPreference.Vp8)
            return vp8;

        // The hardware encoder always runs through FFmpeg, also on Windows where VP8 does not.
        InitFfmpeg(ffmpegFolder);
        var hardware = (probeHardware ?? HardwareH264Encoder.FindAvailable)();
        if (hardware == null)
        {
            Log.Write(preference == VideoCodecPreference.H264 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Information,
                "No hardware H.264 encoder available (Capture:Codec={Codec}), using VP8", preference);
            return vp8;
        }
        return new HybridVideoEncoder(vp8, new HardwareH264Encoder(hardware));
    }

    // Safe to call repeatedly. Without the bundled folder FFmpeg falls back to its own library discovery.
    private static void InitFfmpeg(string? ffmpegFolder)
    {
        var folder = ffmpegFolder ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Directory.Exists(folder))
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, folder);
    }

    private static IVideoSource CreateVp8(bool? useFfmpeg, string? ffmpegFolder)
    {
        if (!(useFfmpeg ?? !OperatingSystem.IsWindows()))
            return new VideoEncoderEndPoint();

        InitFfmpeg(ffmpegFolder);

        var encoder = new FFmpegVideoEndPoint();
        // FFmpegVideoEndPoint also offers VP9, H264, H265 and AV1. We only want VP8: the browser would otherwise
        // be free to pick a codec the LGPL FFmpeg build cannot encode (no libx264/libx265).
        encoder.RestrictFormats(f => f.Codec == VideoCodecsEnum.VP8);
        return encoder;
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
