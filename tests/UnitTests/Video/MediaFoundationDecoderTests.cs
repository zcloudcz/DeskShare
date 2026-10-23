using System.Diagnostics;
using System.Net;
using DeskShare.Core.Video;
using DeskShare.Desktop.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using Xunit.Abstractions;

namespace DeskShare.UnitTests.Video;

/// <summary>
/// Media Foundation H.264 decoder prototype. Streams come from the real hardware encoder (HardwareH264Encoder), so
/// these run on a PC with a GPU encoder and the FFmpeg DLLs, and return early / skip anywhere else (CI, containers, non-Windows).
/// The benchmark only runs with DESKSHARE_BENCH=1 (it takes about a minute and prints a table).
/// </summary>
public sealed class MediaFoundationDecoderTests(ITestOutputHelper output)
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

    private sealed class WindowsMfFactAttribute : FactAttribute
    {
        public WindowsMfFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || !MediaFoundationH264Decoder.IsAvailable())
                Skip = "Needs Windows with Media Foundation H.264.";
            else if (FindFfmpegFolder() == null)
                Skip = "FFmpeg libraries not found in src/DeskShare.DesktopAvalonia/ffmpeg (download them first).";
        }
    }

    private sealed class BenchFactAttribute : FactAttribute
    {
        public BenchFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DESKSHARE_BENCH") != "1")
                Skip = "Set DESKSHARE_BENCH=1 to run the decode benchmark.";
        }
    }

    // BT.601 limited range (Y, U, V) of red, green, blue, grey: the pattern of RealHardwareEncoder_ColoursSurviveEncodeAndDecode.
    private static readonly (byte Y, byte U, byte V)[] Colours = { (81, 90, 240), (145, 54, 34), (41, 240, 110), (128, 128, 128) };
    // The same four colours as BGR, what a correct decoder must return.
    private static readonly (int B, int G, int R)[] ExpectedBgr = { (0, 0, 255), (0, 255, 0), (255, 0, 0), (130, 130, 130) };
    private const int Tolerance = 16; // same tolerance as the FFmpeg decode test

    /// <summary>Four colour blocks (2x2), as I420.</summary>
    private static byte[] ColourBlocks(int w, int h)
    {
        var i420 = new byte[w * h * 3 / 2];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                i420[y * w + x] = Colours[(y / (h / 2)) * 2 + x / (w / 2)].Y;
        for (var y = 0; y < h / 2; y++)
            for (var x = 0; x < w / 2; x++)
            {
                var c = Colours[(y / (h / 4)) * 2 + x / (w / 4)];
                i420[w * h + y * (w / 2) + x] = c.U;
                i420[w * h * 5 / 4 + y * (w / 2) + x] = c.V;
            }
        return i420;
    }

    private static HardwareH264Encoder? NewEncoder()
    {
        (VideoEncoderFactory.Create(true, FindFfmpegFolder(), VideoCodecPreference.Vp8) as IDisposable)?.Dispose(); // initialises FFmpeg
        var name = HardwareH264Encoder.FindAvailable();
        return name == null ? null : new HardwareH264Encoder(name);
    }

    /// <summary>Encodes <paramref name="count"/> frames; the encoder lags a frame, so a few more are pushed and the list may be shorter.</summary>
    private static List<byte[]> Encode(HardwareH264Encoder encoder, int w, int h, int count, Func<int, byte[]> frame)
    {
        var samples = new List<byte[]>();
        encoder.OnVideoSourceEncodedSample += (_, data) => samples.Add(data);
        for (var i = 0; i < count; i++)
            encoder.ExternalVideoSourceRawSample(33, w, h, frame(i), VideoPixelFormatsEnum.I420);
        return samples;
    }

    private static void AssertColours(byte[] bgra, int w, int h)
    {
        for (var block = 0; block < 4; block++)
        {
            var cx = block % 2 * (w / 2) + w / 4;
            var cy = block / 2 * (h / 2) + h / 4;
            var o = (cy * w + cx) * 4;
            var e = ExpectedBgr[block];
            Assert.True(Math.Abs(bgra[o] - e.B) <= Tolerance && Math.Abs(bgra[o + 1] - e.G) <= Tolerance && Math.Abs(bgra[o + 2] - e.R) <= Tolerance,
                $"block {block}: got BGR {bgra[o]},{bgra[o + 1]},{bgra[o + 2]} expected {e.B},{e.G},{e.R}");
            Assert.Equal(255, bgra[o + 3]);
        }
        // The corner pixel exercises the last row/column: wrong stride or crop shows up here as garbage.
        var last = ((h - 1) * w + (w - 1)) * 4;
        var g = ExpectedBgr[3];
        Assert.True(Math.Abs(bgra[last] - g.B) <= Tolerance && Math.Abs(bgra[last + 1] - g.G) <= Tolerance && Math.Abs(bgra[last + 2] - g.R) <= Tolerance,
            $"last pixel: got BGR {bgra[last]},{bgra[last + 1]},{bgra[last + 2]}");
    }

    [WindowsMfFact]
    public void MediaFoundation_DecodesHardwareStream_WithCorrectColoursAndSize() => DecodesWithCorrectColoursAndSize(hardware: false);

    [WindowsMfFact]
    public void MediaFoundationGpu_DecodesHardwareStream_WithCorrectColoursAndSize() => DecodesWithCorrectColoursAndSize(hardware: true);

    private static bool TryCreate(bool hardware, out MediaFoundationH264Decoder? decoder)
    {
        try
        {
            decoder = new MediaFoundationH264Decoder(hardware);
            return true;
        }
        catch (Exception) when (hardware)
        {
            decoder = null; // no D3D11-capable decoder on this machine: nothing to verify
            return false;
        }
    }

    private static void DecodesWithCorrectColoursAndSize(bool hardware)
    {
        using var encoder = NewEncoder();
        if (encoder == null) return; // no GPU encoder here

        // 1920x1080 on purpose: the coded picture is 1088 rows high, so this also covers the crop and the UV plane offset.
        foreach (var (w, h) in new[] { (640, 480), (1920, 1080) })
        {
            var stream = Encode(encoder, w, h, 6, _ => ColourBlocks(w, h));
            Assert.NotEmpty(stream);
            if (!TryCreate(hardware, out var decoder)) return;
            using var _ = decoder;
            var frames = new List<(byte[] Bgra, int W, int H)>();
            foreach (var au in stream)
                decoder!.Decode(au, (bgra, fw, fh) => frames.Add((bgra, fw, fh)));

            Assert.NotEmpty(frames);
            var last = frames[^1];
            Assert.Equal((w, h), (last.W, last.H));
            Assert.Equal(w * h * 4, last.Bgra.Length);
            AssertColours(last.Bgra, w, h);
        }
    }

    [WindowsMfFact]
    public void MediaFoundation_FollowsAResolutionChange_OnTheSameDecoder()
    {
        using var encoder = NewEncoder();
        if (encoder == null) return;

        // The encoder rebuilds its context per resolution and the new one starts with SPS+PPS+IDR, like the host does after a display switch.
        var stream = Encode(encoder, 640, 480, 5, _ => ColourBlocks(640, 480));
        var firstPart = stream.Count;
        stream.AddRange(Encode(encoder, 320, 192, 5, _ => ColourBlocks(320, 192)).Skip(firstPart));

        using var decoder = new MediaFoundationH264Decoder();
        var sizes = new List<(int W, int H)>();
        byte[]? lastFrame = null;
        foreach (var au in stream)
            decoder.Decode(au, (bgra, w, h) => { sizes.Add((w, h)); lastFrame = bgra; });

        Assert.Contains((640, 480), sizes);
        Assert.Equal((320, 192), sizes[^1]);
        AssertColours(lastFrame!, 320, 192);
    }

    [WindowsMfFact]
    public void MediaFoundationSink_RaisesBgraFramesThroughVideoSink()
    {
        using var encoder = NewEncoder();
        if (encoder == null) return;
        var stream = Encode(encoder, 640, 480, 6, _ => ColourBlocks(640, 480));

        using var sink = new VideoSink(NullLogger<VideoSink>.Instance, new MediaFoundationVideoSink(NullLogger.Instance));
        VideoFrameReceivedEventArgs? received = null;
        sink.FrameReceived += (_, e) => received = e;
        var format = new VideoFormat(VideoCodecsEnum.H264, 102);
        foreach (var au in stream)
            sink.GotVideoFrame(new IPEndPoint(IPAddress.Loopback, 1), 0, au, format);

        Assert.NotNull(received);
        Assert.Equal((640, 480), (received!.Width, received.Height));
        AssertColours(received.FrameData, 640, 480);
    }

    /// <summary>
    /// VP8 decodes and has the right size, but SIPSorcery's libvpx wrapper converts YUV to BGR itself with a full-range
    /// formula (grey 128 stays 128, FFmpeg gives 130; saturated red comes out as 208/30/0 instead of 253/0/0), and there is
    /// no way to get the I420 planes out of it. Known deviation of the prototype, see the report: the checks are deliberately loose.
    /// </summary>
    [WindowsMfFact]
    public void MediaFoundationSink_DecodesVp8_ThroughLibvpx_WithKnownColourDeviation()
    {
        const int w = 320, h = 192;
        byte[] vp8;
        using (var vpx = new SIPSorceryMedia.Encoders.VpxVideoEncoder())
            vp8 = vpx.EncodeVideo(w, h, ColourBlocks(w, h), VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8);

        using var sink = new VideoSink(NullLogger<VideoSink>.Instance, new MediaFoundationVideoSink(NullLogger.Instance));
        VideoFrameReceivedEventArgs? received = null;
        sink.FrameReceived += (_, e) => received = e;
        sink.GotVideoFrame(new IPEndPoint(IPAddress.Loopback, 1), 0, vp8, new VideoFormat(VideoCodecsEnum.VP8, 96));

        Assert.NotNull(received);
        Assert.Equal((w, h), (received!.Width, received.Height));
        var grey = ((h * 3 / 4) * w + w * 3 / 4) * 4;
        Assert.InRange(received.FrameData[grey], 120, 136);
        var red = ((h / 4) * w + w / 4) * 4;
        Assert.True(received.FrameData[red + 2] > 180 && received.FrameData[red] < 40, "block 0 must still be clearly red");
    }

    [WindowsMfFact]
    public void MediaFoundation_PicturesMatchTheFfmpegDecode()
    {
        using var encoder = NewEncoder();
        if (encoder == null) return;
        const int w = 1920, h = 1080;
        var stream = Encode(encoder, w, h, 12, MovingFrame(w, h));

        byte[]? mf = null, ff = null;
        using (var decoder = new MediaFoundationH264Decoder())
            foreach (var au in stream)
                decoder.Decode(au, (bgra, _, _) => mf = bgra);

        FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, FindFfmpegFolder());
        var endpoint = new FFmpegVideoEndPoint();
        endpoint.RestrictFormats(f => f.Codec == VideoCodecsEnum.H264);
        endpoint.SetVideoSinkFormat(new VideoFormat(VideoCodecsEnum.H264, 102));
        using (var sink = new VideoSink(NullLogger<VideoSink>.Instance, endpoint))
        {
            sink.FrameReceived += (_, e) => ff = e.FrameData;
            foreach (var au in stream)
                sink.GotVideoFrame(new IPEndPoint(IPAddress.Loopback, 1), 0, au, new VideoFormat(VideoCodecsEnum.H264, 102));
        }

        Assert.NotNull(mf);
        Assert.NotNull(ff);
        Assert.Equal(ff!.Length, mf!.Length);
        // Same matrix and range, so only chroma upsampling differs (we repeat chroma, swscale interpolates): small on average.
        long sum = 0;
        for (var i = 0; i < mf.Length; i++)
            sum += Math.Abs(mf[i] - ff[i]);
        var mean = (double)sum / mf.Length;
        output.WriteLine($"mean abs difference MF vs FFmpeg: {mean:F2} levels per channel");
        Assert.True(mean < 2.5, $"mean abs difference {mean:F2}");
    }

    [BenchFact]
    public void Benchmark_1080p30_MediaFoundationVersusFfmpeg()
    {
        using var encoder = NewEncoder();
        if (encoder == null) { output.WriteLine("no GPU encoder"); return; }

        const int w = 1920, h = 1080, frames = 300;
        var stream = Encode(encoder, w, h, frames, MovingFrame(w, h));
        output.WriteLine($"stream: {stream.Count} access units, {stream.Sum(s => (long)s.Length) / 1024} KiB, {stream.Count(s => s.Length > 0 && ContainsIdr(s))} IDR");

        var rows = new List<string>();
        // Warm-up run first for each, then the measured run: JIT and driver start-up are not part of steady-state cost.
        foreach (var name in new[] { "MF software (decoder only)", "MF GPU (decoder only)", "MF software via VideoSink", "FFmpeg via VideoSink (today)" })
        {
            for (var round = 0; round < 2; round++)
            {
                var (avg, p95, cpuPerFrame, outFrames, firstFrame) = Measure(name, stream);
                if (round == 1)
                    rows.Add($"{name,-30} avg {avg,6:F2} ms  p95 {p95,6:F2} ms  cpu {cpuPerFrame,6:F2} ms/frame  out {outFrames}/{stream.Count}  first frame {firstFrame:F1} ms");
            }
        }
        rows.Add(MeasureConversion(w, h));
        foreach (var r in rows) output.WriteLine(r);
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "deskshare-mf-bench.txt"), rows);
    }

    // NV12 -> BGRA alone, on a 1080p buffer with the 1088-row padding the decoder uses.
    private static string MeasureConversion(int w, int h)
    {
        var nv12 = System.Runtime.InteropServices.Marshal.AllocHGlobal(w * 1088 * 3 / 2);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(Enumerable.Repeat((byte)100, w * 1088 * 3 / 2).ToArray(), 0, nv12, w * 1088 * 3 / 2);
            var bgra = new byte[w * h * 4];
            for (var i = 0; i < 20; i++) Nv12ToBgra.Convert(nv12, w, 1088, w, h, bgra); // warm-up
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < 200; i++) Nv12ToBgra.Convert(nv12, w, 1088, w, h, bgra);
            return $"NV12->BGRA convert only        avg {Stopwatch.GetElapsedTime(t).TotalMilliseconds / 200,6:F2} ms (single thread, so also the cpu cost)";
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(nv12); }
    }

    private static (double Avg, double P95, double CpuPerFrame, int Out, double FirstFrameMs) Measure(string name, List<byte[]> stream)
    {
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "mf-progress.txt"), name + "\n");
        var times = new List<double>(stream.Count);
        var outFrames = 0;
        double firstFrame = -1;
        Action<byte[]> decode;
        IDisposable owner;
        var ep = new IPEndPoint(IPAddress.Loopback, 1);
        var format = new VideoFormat(VideoCodecsEnum.H264, 102);

        var sw = Stopwatch.StartNew();
        if (name.Contains("(decoder only)"))
        {
            var d = new MediaFoundationH264Decoder(hardware: name.StartsWith("MF GPU"));
            owner = d;
            decode = au => outFrames += d.Decode(au, (_, _, _) => { });
        }
        else
        {
            VideoSink sink;
            if (name.StartsWith("MF"))
                sink = new VideoSink(NullLogger<VideoSink>.Instance, new MediaFoundationVideoSink(NullLogger.Instance));
            else
            {
                FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, FindFfmpegFolder());
                var endpoint = new FFmpegVideoEndPoint();
                endpoint.RestrictFormats(f => f.Codec == VideoCodecsEnum.H264);
                endpoint.SetVideoSinkFormat(format);
                sink = new VideoSink(NullLogger<VideoSink>.Instance, endpoint);
            }
            sink.FrameReceived += (_, _) => outFrames++;
            owner = sink;
            decode = au => sink.GotVideoFrame(ep, 0, au, format);
        }

        using (owner)
        {
            // First frame latency: keyframe in, picture out (a fresh decoder, so it includes creating it).
            var before = outFrames;
            var i = 0;
            while (outFrames == before && i < stream.Count)
                decode(stream[i++]);
            firstFrame = sw.Elapsed.TotalMilliseconds;

            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            for (; i < stream.Count; i++)
            {
                var t = Stopwatch.GetTimestamp();
                decode(stream[i]);
                times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
            var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
            times.Sort();
            return (times.Average(), times[(int)(times.Count * 0.95)], cpu / Math.Max(1, times.Count), outFrames, firstFrame);
        }
    }

    // Scrolling gradient with a few moving blocks: not a real desktop, but enough motion for the encoder to produce P-frames of a realistic size.
    private static Func<int, byte[]> MovingFrame(int w, int h) => frame =>
    {
        var i420 = new byte[w * h * 3 / 2];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                i420[y * w + x] = (byte)(16 + ((x + frame * 4) / 8 + y / 16) % 200);
        for (var b = 0; b < 8; b++)
            for (var y = 0; y < 120; y++)
                Array.Fill(i420, (byte)(40 + b * 25), (y + b * 100) % (h - 120) * w + (frame * (b + 3) * 5) % (w - 200), 200);
        // A noisy patch (video, animation on a real desktop) so the stream reaches the encoder's target bitrate instead of being almost empty.
        var noise = new Random(frame);
        for (var y = 200; y < 700; y++)
            noise.NextBytes(i420.AsSpan(y * w + 300, 900));
        Array.Fill(i420, (byte)128, w * h, i420.Length - w * h);
        return i420;
    };

    private static bool ContainsIdr(byte[] annexB)
    {
        for (var i = 0; i + 4 < annexB.Length; i++)
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1 && (annexB[i + 3] & 0x1F) == 5)
                return true;
        return false;
    }
}
