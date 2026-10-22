using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DeskShare.Core;
using DeskShare.Core.Auth;
using DeskShare.Core.Interfaces;
using DeskShare.Desktop.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeskShare.IntegrationTests.E2E;

/// <summary>
/// Marks a test as Linux/Docker E2E. It only runs when DESKSHARE_E2E=1 (set by docker/e2e), so the normal
/// `dotnet test` on a developer machine or the Windows CI job reports it as skipped instead of failing.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DESKSHARE_E2E") != "1")
            Skip = "Linux E2E: set DESKSHARE_E2E=1 (see docker-compose.e2e.yml)";
    }
}

/// <summary>
/// Runs the real headless sender (src/ScreenSenderApp) as a child process. The sender prints its Server ID and
/// passkey to stdout, so the test needs no product change to learn the credentials.
/// </summary>
public sealed class SenderProcess : IDisposable
{
    public static string SignalingWsUrl =>
        Environment.GetEnvironmentVariable("DESKSHARE_E2E_SIGNALING") ?? "ws://signaling:5151/signal";

    private static string SenderDll =>
        Environment.GetEnvironmentVariable("DESKSHARE_E2E_SENDER") ?? "/opt/sender/DeskShare.ScreenSenderApp.dll";

    private static readonly Regex ServerIdRx = new(@"^Server ID: (\S+)", RegexOptions.Compiled);
    private static readonly Regex PasskeyRx = new(@"^Passkey: ([A-Z0-9-]+) ", RegexOptions.Compiled);

    private readonly Process _process;
    private readonly TaskCompletionSource _streaming = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _output = new();
    private volatile string? _serverId;
    private volatile string? _passkey;

    private SenderProcess(bool remoteControl, string? display, bool clipboard)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (display != null) psi.Environment["DISPLAY"] = display;
        psi.ArgumentList.Add(SenderDll);
        psi.ArgumentList.Add($"--Signaling:ServerUrl={SignalingWsUrl}");
        psi.ArgumentList.Add($"--Security:EnableRemoteControl={(remoteControl ? "true" : "false")}");
        psi.ArgumentList.Add($"--Security:EnableClipboard={(clipboard ? "true" : "false")}");

        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public string ServerId => _serverId ?? throw new InvalidOperationException("Sender has not printed its Server ID");

    /// <summary>Latest passkey without dashes. It rotates every 45 s, so read it right before use.</summary>
    public string Passkey => AuthenticationService.UnformatPasskey(_passkey ?? throw new InvalidOperationException("No passkey yet"));

    public string Output { get { lock (_output) return string.Join('\n', _output); } }

    /// <summary>Starts the sender and waits until it has registered and is streaming.</summary>
    public static async Task<SenderProcess> StartAsync(bool remoteControl, string? display = null, bool clipboard = false)
    {
        var sender = new SenderProcess(remoteControl, display, clipboard);
        try
        {
            // The service starts in the background (the console "Streaming." line comes too early), so wait for its own
            // log line: it is written after /register succeeded and the capture pipeline runs.
            await sender._streaming.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return sender;
        }
        catch
        {
            var output = sender.Output;
            sender.Dispose();
            throw new TimeoutException($"Sender did not start streaming. Output:\n{output}");
        }
    }

    private void OnLine(string? line)
    {
        if (line is null) return;
        lock (_output) _output.Add(line);

        if (ServerIdRx.Match(line) is { Success: true } id) _serverId = id.Groups[1].Value;
        else if (PasskeyRx.Match(line) is { Success: true } key) _passkey = key.Groups[1].Value;
        else if (line.Contains("is now streaming", StringComparison.Ordinal)) _streaming.TrySetResult();
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _process.Dispose();
    }
}

/// <summary>Authenticates like the Avalonia app does (signed POST /authenticate) and builds the real ClientManager.</summary>
public static class Viewer
{
    /// <returns>The HTTP status, so callers can tell a real rejection (401) from a server error or rate limit,
    /// and the parsed response when the call succeeded.</returns>
    public static async Task<(HttpStatusCode Status, ClientAuthenticationResponse? Response)> AuthenticateAsync(string serverId, string passkey, string clientId)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();
        var response = await http.PostAsJsonAsync($"{SignalingUrl.ToHttpBase(SenderProcess.SignalingWsUrl)}/authenticate",
            new ClientAuthenticationMessage
            {
                ServerId = serverId,
                Passkey = passkey,
                ClientId = clientId,
                Timestamp = timestamp,
                Nonce = nonce,
                Signature = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce)
            });
        return (response.StatusCode, response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ClientAuthenticationResponse>()
            : null);
    }

    /// <summary>Viewer log lines, appended to failure messages so a failed run explains itself.</summary>
    public static readonly List<string> Log = new();

    public static async Task<ClientManager> ConnectAsync(SenderProcess sender, EventHandler<VideoFrameReceivedEventArgs>? onFrame = null, IClipboardManager? clipboard = null)
    {
        lock (Log) Log.Clear();
        var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new ListLoggerProvider(Log)));
        var clientId = ServerIdGenerator.GenerateClientId();
        var (_, auth) = await AuthenticateAsync(sender.ServerId, sender.Passkey, clientId);
        Assert.True(auth?.Success, $"authenticate failed (passkey used: {sender.Passkey})\n{sender.Output}");

        var client = new ClientManager(
            loggers.CreateLogger<ClientManager>(),
            loggers.CreateLogger<VideoSink>(),
            new ConfigurationBuilder().Build(),
            clipboard ?? new FakeClipboard());
        if (onFrame != null) client.FrameReceived += onFrame;

        await client.ConnectAsync(sender.ServerId, null, SenderProcess.SignalingWsUrl,
            auth!.WebSocketToken, clientId, auth.IceServers, auth.ResumeToken);
        return client;
    }

    public static string LogText() { lock (Log) return $"viewer log lines: {Log.Count}\n" + string.Join('\n', Log); }

    private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(sink, categoryName);
        public void Dispose() { }
    }

    private sealed class ListLogger(List<string> sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (sink) sink.Add($"[viewer {logLevel}] {category.Split('.').Last()}: {formatter(state, exception)} {exception?.Message}");
        }
    }

    /// <summary>In-memory viewer clipboard: the test "copies" with <see cref="Copy"/> and reads what the viewer applied.</summary>
    public sealed class FakeClipboard : IClipboardManager
    {
        public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;
        public string? Text { get; private set; }
        public bool IsMonitoring => false;
        public void Copy(string text) => ClipboardChanged?.Invoke(this, new ClipboardChangedEventArgs { Text = text });
        public Task<string?> GetTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Text);
        public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default) { Text = text; return Task.FromResult(true); }
        public void StartMonitoring() { }
        public void StopMonitoring() { }
        public void Dispose() { }
    }
}

/// <summary>Reads and sets the real X11 pointer through xdotool, independent of the code under test.</summary>
public static class XServer
{
    private static readonly Regex Pos = new(@"x:(-?\d+) y:(-?\d+)", RegexOptions.Compiled);

    public static (int X, int Y) PointerLocation()
    {
        var match = Pos.Match(Run("getmouselocation"));
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    public static void MovePointer(int x, int y) => Run($"mousemove {x} {y}");

    /// <summary>What a real X client sees on the CLIPBOARD selection of the sender display (empty when nobody owns it).</summary>
    public static string GetClipboard()
    {
        using var p = Process.Start(new ProcessStartInfo("xclip", "-o -selection clipboard") { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : "";
    }

    /// <summary>Takes ownership of the clipboard like another application would (xclip stays alive to serve it).</summary>
    public static void SetClipboard(string text)
    {
        // Only stdin is redirected: xclip forks a daemon that keeps stdout/stderr open, so reading them would hang.
        using var p = Process.Start(new ProcessStartInfo("xclip", "-selection clipboard -i") { RedirectStandardInput = true })!;
        p.StandardInput.Write(text);
        p.StandardInput.Close();
        p.WaitForExit();
    }

    private static string Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("xdotool", args) { RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    /// <summary>Polls instead of sleeping a fixed time: returns as soon as the condition holds.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<Task>? onEachTry = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (onEachTry != null) await onEachTry();
            if (condition()) return true;
            await Task.Delay(250);
        }
        return condition();
    }
}
