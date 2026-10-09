using DeskShare.Core.Video;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace DeskShare.UnitTests.Video;

/// <summary>
/// Runs the non-Windows encoder path (FFmpeg + libvpx) on any OS. The FFmpeg DLLs are gitignored (downloaded per machine)
/// so the test is skipped on a runner where the Avalonia app's ffmpeg folder is missing.
/// </summary>
public sealed class VideoEncoderFactoryTests
{
    private static string? FindFfmpegFolder()
    {
        // Walk up from bin/Release/net8.0 to the repo root.
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

    [RequiresFfmpegFact]
    public void FfmpegEncoder_AdvertisesOnlyVp8_AndEncodesI420Frames()
    {
        using var encoder = (FFmpegVideoEndPoint)VideoEncoderFactory.Create(useFfmpeg: true, FindFfmpegFolder());

        // The advertised format must already be VP8 in the current enum numbering: no shim for this path.
        var formats = VideoEncoderFactory.AdvertisedFormats(encoder);
        var vp8 = Assert.Single(formats);
        Assert.Equal(VideoCodecsEnum.VP8, vp8.Codec);
        Assert.Equal(VideoEncoderFactory.ToEncoder(encoder, vp8), vp8);
        encoder.SetVideoSourceFormat(vp8);

        var samples = new List<byte[]>();
        encoder.OnVideoSourceEncodedSample += (_, data) => samples.Add(data);

        const int width = 640, height = 480;
        var i420 = new byte[width * height * 3 / 2];
        for (var frame = 0; frame < 5; frame++)
        {
            // Moving gradient so the encoder has something to code in the delta frames too.
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    i420[y * width + x] = (byte)(x + y + frame * 8);
            Array.Fill(i420, (byte)128, width * height, i420.Length - width * height);

            if (frame == 0)
                encoder.ForceKeyFrame();
            encoder.ExternalVideoSourceRawSample(33, width, height, i420, VideoPixelFormatsEnum.I420);
        }

        Assert.Equal(5, samples.Count);
        Assert.All(samples, s => Assert.NotEmpty(s));
        // VP8 key frame start code (bytes 3..5 = 9D 01 2A) proves this really is a VP8 bitstream.
        Assert.Equal(new byte[] { 0x9D, 0x01, 0x2A }, samples[0].Skip(3).Take(3));
    }
}
