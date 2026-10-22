using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskShare.Desktop.Shared.Services;
using Xunit.Abstractions;

namespace DeskShare.IntegrationTests.E2E;

/// <summary>
/// Performance measurements on top of the Linux E2E harness: received FPS, glass-to-glass latency and the
/// sender's own stage timings, at 1280x720 (Xvfb :99) and 1920x1080 (Xvfb :100). See docker/e2e/run.sh.
/// Results go to $DESKSHARE_E2E_RESULTS (default /results) as perf.md and perf.json and to the test output.
/// </summary>
// One collection: the tests share the X displays, the pointer and the signaling rate limits, so they must not overlap.
[Collection("LinuxE2E")]
public class LinuxE2EPerfTests(ITestOutputHelper output)
{
    // Deliberately loose: CI runners are shared and have no GPU, so these only catch "the pipeline is broken
    // or an order of magnitude slower", not small regressions. The numbers in the report are the real signal.
    private const double MinFps720 = 10;
    private const double MaxMedianLatencyMs720 = 1000;
    // 1920x1080 is reported only (no threshold).

    private const int WarmupSeconds = 5, FpsWindowSeconds = 15, LatencySamples = 10;

    // Two full-screen windows per display (docker/e2e/run.sh); raising one switches the visible colour.
    private static readonly Paint Base = new("base", 0x30, 0x70, 0xB0);
    private static readonly Paint Alt = new("alt", 0xB0, 0x50, 0x30);

    // Results of all perf tests in this run; the report is rewritten after each one.
    private static readonly List<PerfResult> Results = new();

    [E2EFact]
    public Task Perf_1280x720() => MeasureAsync(":99", 1280, 720);

    [E2EFact]
    public Task Perf_1920x1080() => MeasureAsync(":100", 1920, 1080);

    private async Task MeasureAsync(string display, int width, int height)
    {
        using var sender = await SenderProcess.StartAsync(remoteControl: false, display);
        var baseId = FindWindow(display, Base.Window);
        var altId = FindWindow(display, Alt.Window);
        Raise(display, baseId);

        var frames = 0;
        var counting = false;
        int frameW = 0, frameH = 0;
        var wanted = Base;
        TaskCompletionSource<long>? hit = null;
        using var client = await Viewer.ConnectAsync(sender, (_, f) =>
        {
            frameW = f.Width; frameH = f.Height;
            if (counting) Interlocked.Increment(ref frames);
            var h = hit;
            if (h != null && Matches(f, wanted)) h.TrySetResult(Stopwatch.GetTimestamp());
        });

        async Task<bool> WaitForColourAsync(Paint colour, TimeSpan timeout)
        {
            var t = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            wanted = colour;
            hit = t;
            try { await t.Task.WaitAsync(timeout); return true; }
            catch (TimeoutException) { return false; }
            finally { hit = null; }
        }

        // Warm-up: the first frames can be black until the encoder is running, so wait for the base colour.
        Assert.True(await WaitForColourAsync(Base, TimeSpan.FromSeconds(45)), $"No frame with the base colour\n{sender.Output}\n{Viewer.LogText()}");
        await Task.Delay(TimeSpan.FromSeconds(WarmupSeconds));

        // Sender stage timings are logged every 10 s; read the lines that appear from here to the end of the run.
        var logLinesBefore = sender.Output.Split('\n').Length;

        counting = true;
        await Task.Delay(TimeSpan.FromSeconds(FpsWindowSeconds));
        counting = false;
        var fps = frames / (double)FpsWindowSeconds;

        // Glass-to-glass: switch the screen colour from here and time the first frame that shows it.
        var latencies = new List<double>();
        for (var i = 0; i < LatencySamples; i++)
        {
            var (target, targetId, current) = i % 2 == 0 ? (Alt, altId, Base) : (Base, baseId, Alt);
            Assert.True(await WaitForColourAsync(current, TimeSpan.FromSeconds(10)), "Screen was not stable before the switch");
            await Task.Delay(300); // let the encoder settle after the previous change (test code, not product code)

            var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            wanted = target;
            hit = tcs;
            var start = Stopwatch.GetTimestamp();
            Raise(display, targetId);
            var end = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            hit = null;
            latencies.Add(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds);
        }
        Raise(display, baseId); // leave the screen in its original state

        var timings = ParseTimings(string.Join('\n', sender.Output.Split('\n').Skip(logLinesBefore)));
        latencies.Sort();
        var result = new PerfResult(
            $"{width}x{height}", $"{frameW}x{frameH}", Math.Round(fps, 1),
            Math.Round(latencies[latencies.Count / 2]), Math.Round(latencies[(int)Math.Ceiling(0.95 * latencies.Count) - 1]),
            latencies.Select(x => Math.Round(x)).ToArray(), timings);
        WriteReport(result);

        if (width == 1280)
        {
            Assert.True(result.Fps >= MinFps720, $"FPS {result.Fps} < {MinFps720}");
            Assert.True(result.LatencyMedianMs <= MaxMedianLatencyMs720, $"median latency {result.LatencyMedianMs} ms > {MaxMedianLatencyMs720}");
        }
    }

    // Sample away from the centre (the pointer rests there and x11grab draws it); same relative pixel at any size.
    private static bool Matches(VideoFrameReceivedEventArgs f, Paint c)
    {
        var i = ((int)(f.Height * 0.28) * f.Width + (int)(f.Width * 0.78)) * 4; // BGRA32, tightly packed
        const int tolerance = 16; // VP8 is lossy and goes through YUV
        return Math.Abs(f.FrameData[i] - c.B) <= tolerance
            && Math.Abs(f.FrameData[i + 1] - c.G) <= tolerance
            && Math.Abs(f.FrameData[i + 2] - c.R) <= tolerance;
    }

    private static readonly Regex TimingRx = new(
        @"output=(\d+x\d+), viewers=-?\d+, acquire avg/max=([\d.]+)/([\d.]+)ms, convert avg/max=([\d.]+)/([\d.]+)ms, push\(encode\+send\) avg/max=([\d.]+)/([\d.]+)ms",
        RegexOptions.Compiled);

    private static StageTimings? ParseTimings(string log)
    {
        var m = TimingRx.Matches(log).ToList();
        if (m.Count == 0) return null;
        static double D(Match x, int g) => double.Parse(x.Groups[g].Value, CultureInfo.InvariantCulture);
        double Avg(int g) => Math.Round(m.Average(x => D(x, g)), 1);
        double Max(int g) => Math.Round(m.Max(x => D(x, g)), 1);
        return new StageTimings(m.Count, Avg(2), Max(3), Avg(4), Max(5), Avg(6), Max(7));
    }

    private void WriteReport(PerfResult result)
    {
        string md;
        string json;
        lock (Results)
        {
            Results.RemoveAll(r => r.Screen == result.Screen);
            Results.Add(result);
            Results.Sort((a, b) => string.CompareOrdinal(a.Screen, b.Screen));
            md = ToMarkdown(Results);
            json = JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true });
        }
        output.WriteLine(md);
        try
        {
            var dir = Environment.GetEnvironmentVariable("DESKSHARE_E2E_RESULTS") ?? "/results";
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "perf.md"), md);
            File.WriteAllText(Path.Combine(dir, "perf.json"), json);
        }
        catch (Exception e) { output.WriteLine($"(report not saved: {e.Message})"); }
    }

    private static string ToMarkdown(List<PerfResult> rs)
    {
        var sb = new StringBuilder("## DeskShare Linux E2E performance\n\n");
        sb.AppendLine("| Screen | Decoded | FPS | Latency median / p95 (ms) | acquire avg/max | convert avg/max | push avg/max (ms) |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rs)
        {
            var t = r.Sender;
            sb.AppendLine($"| {r.Screen} | {r.Decoded} | {r.Fps.ToString(CultureInfo.InvariantCulture)} | {r.LatencyMedianMs} / {r.LatencyP95Ms} | " +
                (t == null ? "n/a | n/a | n/a |" : $"{t.AcquireAvg}/{t.AcquireMax} | {t.ConvertAvg}/{t.ConvertMax} | {t.PushAvg}/{t.PushMax} |"));
        }
        sb.AppendLine();
        sb.AppendLine($"Latency = colour switch command to first decoded viewer frame showing it ({LatencySamples} samples; includes xdotool spawn and up to one capture interval). Thresholds apply to 1280x720 only: FPS >= {MinFps720}, median latency <= {MaxMedianLatencyMs720} ms.");
        foreach (var r in rs) sb.AppendLine($"- {r.Screen} samples (ms): {string.Join(", ", r.LatencySamplesMs)}");
        return sb.ToString();
    }

    private static string FindWindow(string display, string name) =>
        RunXdotool(display, $"search --name ^{name}$").Split('\n', StringSplitOptions.RemoveEmptyEntries).First().Trim();

    private static void Raise(string display, string windowId) => RunXdotool(display, $"windowraise {windowId}");

    private static string RunXdotool(string display, string args)
    {
        var psi = new ProcessStartInfo("xdotool", args) { RedirectStandardOutput = true };
        psi.Environment["DISPLAY"] = display;
        using var p = Process.Start(psi)!;
        var result = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return result;
    }

    private sealed record Paint(string Window, int R, int G, int B);

    private sealed record StageTimings(int Reports, double AcquireAvg, double AcquireMax, double ConvertAvg, double ConvertMax, double PushAvg, double PushMax);

    private sealed record PerfResult(string Screen, string Decoded, double Fps, double LatencyMedianMs, double LatencyP95Ms, double[] LatencySamplesMs, StageTimings? Sender);
}
