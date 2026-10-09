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
        using var encoder = (FfmpegVp8Encoder)VideoEncoderFactory.Create(useFfmpeg: true, FindFfmpegFolder());

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

    [Theory]
    [InlineData(1920, 1080, 4_000_000)]
    [InlineData(3440, 1440, 9_600_000)]
    [InlineData(320, 240, 1_000_000)] // clamped up
    [InlineData(7680, 4320, 10_000_000)] // clamped down
    public void TargetBitrate_ScalesWithFrameSizeWithinClamp(int width, int height, int expected) =>
        Assert.InRange(FfmpegVp8Encoder.TargetBitrate(width, height), expected * 0.98, expected * 1.02);

    [RequiresFfmpegFact]
    public void FfmpegEncoder_EncodesHighDetailScreenContent_AtScreenSharingBitrate()
    {
        using var encoder = (FfmpegVp8Encoder)VideoEncoderFactory.Create(useFfmpeg: true, FindFfmpegFolder());
        var samples = new List<byte[]>();
        encoder.OnVideoSourceEncodedSample += (_, data) => samples.Add(data);

        // Text-like content: a grid of 8x16 "glyph" cells, each a black/white stripe pattern, with ~5% of the
        // cells changing per frame (typing). Rich in sharp edges; a 256 kbit/s budget (~1 KB per
        // frame at 30 fps) would smear, while the ~4 Mbit/s target (~16 KB per frame) keeps them.
        const int width = 1920, height = 1080, frames = 30, cellW = 8, cellH = 16;
        var i420 = new byte[width * height * 3 / 2];
        Array.Fill(i420, (byte)128, width * height, i420.Length - width * height);
        var rnd = new Random(1);
        var glyphs = new byte[(width / cellW) * (height / cellH)][];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var i = 0; i < glyphs.Length; i++)
            {
                if (glyphs[i] != null && rnd.Next(20) != 0)
                    continue;
                glyphs[i] = new byte[cellW * cellH];
                // Stripes of random orientation and width: sharp edges like strokes of text, but not pure noise.
                var vertical = rnd.Next(2) == 0;
                var stripe = rnd.Next(1, 4);
                for (var p = 0; p < glyphs[i].Length; p++)
                    glyphs[i][p] = (byte)((((vertical ? p % cellW : p / cellW) / stripe) & 1) == 0 ? 16 : 235);
                var cx = i % (width / cellW) * cellW;
                var cy = i / (width / cellW) * cellH;
                for (var y = 0; y < cellH; y++)
                    Array.Copy(glyphs[i], y * cellW, i420, (cy + y) * width + cx, cellW);
            }
            encoder.ExternalVideoSourceRawSample(33, width, height, i420, VideoPixelFormatsEnum.I420);
        }

        Assert.Equal(frames, samples.Count);
        Assert.Equal(new byte[] { 0x9D, 0x01, 0x2A }, samples[0].Skip(3).Take(3));
        var averageBytes = samples.Average(s => s.Length);
        Assert.True(averageBytes > 1100, $"Average frame was only {averageBytes:F0} bytes; bitrate looks like the 256 kbit/s default.");
    }
}
