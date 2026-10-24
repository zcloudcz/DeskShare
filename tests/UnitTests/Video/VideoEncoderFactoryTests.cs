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
        using var encoder = (LibvpxVp8Encoder)VideoEncoderFactory.Create(useFfmpeg: true, FindFfmpegFolder());

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

    [RequiresFfmpegFact]
    public void FfmpegVp8_KeepsDesktopTextSharp()
    {
        // A 1600x900 "desktop": flat background with lines of 1-2 px strokes, like text. With FFmpeg's default
        // 256 kbit/s the strokes smear (the blurry picture seen on Linux hosts); the realtime settings keep them.
        const int width = 1600, height = 900;
        var i420 = TextScreen(width, height);

        using var encoder = (LibvpxVp8Encoder)VideoEncoderFactory.Create(useFfmpeg: true, FindFfmpegFolder(), VideoCodecPreference.Vp8);
        encoder.SetVideoSourceFormat(VideoEncoderFactory.AdvertisedFormats(encoder)[0]);
        var samples = new List<byte[]>();
        encoder.OnVideoSourceEncodedSample += (_, data) => samples.Add(data);
        encoder.ForceKeyFrame();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var frame = 0; frame < 15; frame++)
            encoder.ExternalVideoSourceRawSample(33, width, height, i420, VideoPixelFormatsEnum.I420);
        var encodeMs = clock.Elapsed.TotalMilliseconds / 15;

        // Every frame is sent at once (no look-ahead holding frames back), and only the requested first frame is a key
        // frame (VP8: bit 0 of the first byte is 0 for key frames); libvpx must not add its own.
        Assert.Equal(15, samples.Count);
        Assert.Equal(1, samples.Count(x => (x[0] & 1) == 0));

        // SIPSorcery's decoder ignores the requested format and returns 24-bit RGB/BGR rows (grey here, so the channel
        // order does not matter). Compare with the source luma converted the same way (BT.601 limited range).
        using var decoder = new FFmpegVideoEncoder();
        byte[]? rgb = null;
        var stride = 0;
        foreach (var sample in samples)
            foreach (var picture in decoder.DecodeVideoFaster(sample, VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8))
            {
                rgb = picture.GetBuffer();
                stride = picture.Stride;
            }
        Assert.NotNull(rgb);

        double sse = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var expected = Math.Clamp((i420[y * width + x] - 16) * 255.0 / 219.0, 0, 255);
                var d = rgb![y * stride + x * 3 + 1] - expected;
                sse += d * d;
            }
        var psnr = 10 * Math.Log10(255.0 * 255.0 / (sse / (width * height)));
        Console.WriteLine($"VP8 text test: PSNR {psnr:F1} dB, {samples.Sum(x => x.Length) / samples.Count} bytes/frame avg, key {samples[0].Length} bytes, encode {encodeMs:F1} ms/frame");
        Assert.True(psnr > 38, $"luma PSNR {psnr:F1} dB: the text is smeared");
    }

    // Grey luma plane of a terminal-like screen: lines of real 12 px text (System.Drawing, Windows only), chroma neutral.
    private static byte[] TextScreen(int width, int height)
    {
        var i420 = new byte[width * height * 3 / 2];
        Array.Fill(i420, (byte)128, width * height, i420.Length - width * height);
        if (!OperatingSystem.IsWindows())
        {
            for (var i = 0; i < width * height; i++) i420[i] = (byte)(i % width % 7 == 0 ? 230 : 40);
            return i420;
        }
        using var bmp = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(30, 30, 30));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new System.Drawing.Font("Consolas", 12);
            for (var y = 4; y < height - 16; y += 18)
                g.DrawString($"{y:D4}  drwxr-xr-x 2 ubuntu ubuntu 4096 Oct 24 09:{y % 60:D2} DeskShare-Linux-demo/src/Core/Video  OK", font, System.Drawing.Brushes.Gainsboro, 6, y);
        }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = bmp.GetPixel(x, y);
                i420[y * width + x] = (byte)(16 + ((c.R * 66 + c.G * 129 + c.B * 25 + 128) >> 8)); // BT.601 limited range
            }
        return i420;
    }

}
