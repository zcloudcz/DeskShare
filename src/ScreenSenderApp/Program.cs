using Microsoft.Extensions.Configuration;
using DeskShare.Common.Interfaces;
using DeskShare.ScreenSenderApp.Capture;
using DeskShare.ScreenSenderApp.Conversion;
using DeskShare.ScreenSenderApp.Pipeline;
using DeskShare.ScreenSenderApp.Video;
using DeskShare.ScreenSenderApp.WebRTC;
using Serilog;

namespace DeskShare.ScreenSenderApp;

/// <summary>
/// Main entry point for RemoteDesktop Screen Sender application.
/// Demonstrates screen capture pipeline with DXGI Desktop Duplication and pixel conversion.
/// </summary>
internal class Program
{
    private static CapturePipeline? _pipeline;
    private static WebRTCSession? _webrtcSession;
    private static bool _shouldExit;

    /// <summary>
    /// Application entry point.
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddCommandLine(args)
            .Build();

        // Initialize Serilog
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        try
        {
            Log.Information("RemoteDesktop.NET Screen Sender starting...");

            Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║      RemoteDesktop.NET - Screen Sender Application       ║");
            Console.WriteLine("║                     Version 1.0 (Phase 1)                 ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            // Load configuration
            var config = new AppConfiguration();
            configuration.Bind(config);

            // Override with command line arguments
            config = ParseArguments(args, config);

            // Display configuration
            DisplayConfiguration(config);

            // Setup console cancellation handler
            Console.CancelKeyPress += OnConsoleCancelKeyPress;

            // Interactive menu
            await RunInteractiveMenuAsync(config);

            // Cleanup
            Console.WriteLine("\n[Shutdown] Cleaning up...");
            _pipeline?.Dispose();
            _webrtcSession?.Dispose();
            Log.Information("Application exited gracefully");
            Console.WriteLine("[Shutdown] Application exited.");

            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            Console.WriteLine($"\n[FATAL] {ex.Message}");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Runs interactive menu for controlling the application.
    /// </summary>
    private static async Task RunInteractiveMenuAsync(AppConfiguration config)
    {
        while (!_shouldExit)
        {
            Console.WriteLine("\n═══════════════════════════════════════════════════════════");
            Console.WriteLine("                         MENU");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine("  [1] Start Capture Pipeline (Stub/Test Mode)");
            Console.WriteLine("  [2] Stop Capture Pipeline");
            Console.WriteLine("  [3] Start WebRTC Session (Live Streaming)");
            Console.WriteLine("  [4] Stop WebRTC Session");
            Console.WriteLine("  [5] Show Statistics");
            Console.WriteLine("  [6] Change Settings");
            Console.WriteLine("  [7] List Available Monitors");
            Console.WriteLine("  [8] About");
            Console.WriteLine("  [Q] Quit");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.Write("\nSelect option: ");

            var key = Console.ReadKey();
            Console.WriteLine("\n");

            switch (key.KeyChar)
            {
                case '1':
                    StartPipeline(config);
                    break;

                case '2':
                    await StopPipelineAsync();
                    break;

                case '3':
                    await StartWebRTCSessionAsync(config);
                    break;

                case '4':
                    await StopWebRTCSessionAsync();
                    break;

                case '5':
                    ShowStatistics();
                    break;

                case '6':
                    config = ChangeSettings(config);
                    break;

                case '7':
                    ListAvailableMonitors();
                    break;

                case '8':
                    ShowAbout();
                    break;

                case 'q':
                case 'Q':
                    if (_pipeline?.IsRunning == true)
                    {
                        Console.WriteLine("[Menu] Stopping pipeline before exit...");
                        await StopPipelineAsync();
                    }
                    if (_webrtcSession != null)
                    {
                        Console.WriteLine("[Menu] Stopping WebRTC session before exit...");
                        await StopWebRTCSessionAsync();
                    }
                    _shouldExit = true;
                    break;

                default:
                    Console.WriteLine("[Menu] Invalid option. Please try again.");
                    break;
            }

            if (!_shouldExit && key.KeyChar != '1')
            {
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
            }
        }
    }

    /// <summary>
    /// Creates the appropriate pixel converter based on configuration.
    /// </summary>
    private static IFrameConverter CreateConverter(AppConfiguration config)
    {
        IFrameConverter converter = config.Capture.ConverterType switch
        {
            ConverterType.Basic => new PixelConverter(),
            ConverterType.Simd => new SimdPixelConverter(),
            ConverterType.Pooled => new PooledPixelConverter(),
            _ => new SimdPixelConverter() // Default to SIMD
        };

        Log.Information("Created converter: {ConverterType} ({ConverterClass})",
            config.Capture.ConverterType,
            converter.GetType().Name);

        return converter;
    }

    /// <summary>
    /// Starts the capture pipeline.
    /// </summary>
    private static void StartPipeline(AppConfiguration config)
    {
        if (_pipeline == null)
        {
            // Initialize pipeline on first use
            var capturer = new DesktopDuplicator(config.Capture.AdapterIndex, config.Capture.OutputIndex);
            var converter = CreateConverter(config);
            var videoSource = new StubVideoSource();

            _pipeline = new CapturePipeline(capturer, converter, videoSource);
            Log.Information("Pipeline initialized with {Converter}", converter.GetType().Name);
        }

        if (_pipeline.IsRunning)
        {
            Console.WriteLine("[Pipeline] Pipeline is already running!");
            return;
        }

        Console.WriteLine($"[Pipeline] Starting capture at {config.Capture.TargetFps} FPS...");

        if (_pipeline.Start(config.Capture.TargetFps))
        {
            Console.WriteLine("[Pipeline] ✓ Pipeline started successfully!");
            Console.WriteLine("[Pipeline] Press any key to return to menu and stop...");
            Console.ReadKey();

            StopPipelineAsync().GetAwaiter().GetResult();
        }
        else
        {
            Console.WriteLine("[Pipeline] ✗ Failed to start pipeline.");
            Console.WriteLine("[Pipeline] Make sure you have a compatible display adapter.");
        }
    }

    /// <summary>
    /// Stops the capture pipeline.
    /// </summary>
    private static async Task StopPipelineAsync()
    {
        if (_pipeline?.IsRunning == false)
        {
            Console.WriteLine("[Pipeline] Pipeline is not running.");
            return;
        }

        Console.WriteLine("[Pipeline] Stopping...");
        await _pipeline!.StopAsync();
        Console.WriteLine("[Pipeline] ✓ Pipeline stopped.");
    }

    /// <summary>
    /// Shows current statistics.
    /// </summary>
    private static void ShowStatistics()
    {
        if (_pipeline == null)
        {
            Console.WriteLine("[Stats] Pipeline not initialized.");
            return;
        }

        Console.WriteLine("\n[Stats] Current Status:");
        Console.WriteLine($"  Running: {(_pipeline.IsRunning ? "Yes" : "No")}");

        if (_pipeline.IsRunning)
        {
            Console.WriteLine("\n  Real-time statistics are printed by the pipeline.");
            Console.WriteLine("  Check console output while pipeline is running.");
        }
    }

    /// <summary>
    /// Changes application settings.
    /// </summary>
    private static AppConfiguration ChangeSettings(AppConfiguration current)
    {
        Console.WriteLine("\n[Settings] Current Configuration:");
        DisplayConfiguration(current);

        // FPS setting
        Console.Write("\nEnter new target FPS (10-60) [current: {0}]: ", current.Capture.TargetFps);
        var fpsInput = Console.ReadLine();

        if (int.TryParse(fpsInput, out var newFps) && newFps >= 10 && newFps <= 60)
        {
            current.Capture.TargetFps = newFps;
            Console.WriteLine($"[Settings] ✓ Target FPS set to {newFps}");
            Log.Information("Target FPS changed to {Fps}", newFps);
        }
        else if (!string.IsNullOrWhiteSpace(fpsInput))
        {
            Console.WriteLine("[Settings] ✗ Invalid FPS value. Keeping current setting.");
        }

        // Converter type setting
        Console.WriteLine("\n[Settings] Select pixel converter type:");
        Console.WriteLine("  [1] Basic - Simple implementation (baseline)");
        Console.WriteLine("  [2] SIMD - AVX2 optimized (~3x faster)");
        Console.WriteLine("  [3] Pooled - ArrayPool buffer reuse (low GC pressure)");
        Console.Write($"\nEnter choice (1-3) [current: {(int)current.Capture.ConverterType + 1}]: ");

        var converterInput = Console.ReadLine();
        if (int.TryParse(converterInput, out var choice) && choice >= 1 && choice <= 3)
        {
            current.Capture.ConverterType = (ConverterType)(choice - 1);
            Console.WriteLine($"[Settings] ✓ Converter set to {current.Capture.ConverterType}");
            Log.Information("Converter type changed to {ConverterType}", current.Capture.ConverterType);
        }
        else if (!string.IsNullOrWhiteSpace(converterInput))
        {
            Console.WriteLine("[Settings] ✗ Invalid choice. Keeping current setting.");
        }

        return current;
    }

    /// <summary>
    /// Shows about information.
    /// </summary>
    private static void ShowAbout()
    {
        Console.WriteLine("\n╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                          ABOUT                            ║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════╣");
        Console.WriteLine("║  RemoteDesktop.NET - Screen Streaming Application         ║");
        Console.WriteLine("║                                                           ║");
        Console.WriteLine("║  Technology Stack:                                        ║");
        Console.WriteLine("║    • DXGI Desktop Duplication API (screen capture)        ║");
        Console.WriteLine("║    • Vortice.Windows (DirectX bindings)                   ║");
        Console.WriteLine("║    • ITU-R BT.601 (BGRA -> I420 conversion)               ║");
        Console.WriteLine("║    • WebRTC (peer-to-peer streaming) [STUB]               ║");
        Console.WriteLine("║                                                           ║");
        Console.WriteLine("║  Features:                                                ║");
        Console.WriteLine("║    ✓ GPU-based screen capture                             ║");
        Console.WriteLine("║    ✓ Efficient pixel format conversion                    ║");
        Console.WriteLine("║    ✓ Backpressure handling                                ║");
        Console.WriteLine("║    ✓ Real-time statistics                                 ║");
        Console.WriteLine("║                                                           ║");
        Console.WriteLine("║  NOTE: This is a demonstration version.                   ║");
        Console.WriteLine("║  WebRTC transmission is stubbed (frames are logged).      ║");
        Console.WriteLine("║  Integrate with actual WebRTC library for real streaming. ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
    }

    /// <summary>
    /// Parses command line arguments.
    /// </summary>
    private static AppConfiguration ParseArguments(string[] args, AppConfiguration config)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--adapter" when i + 1 < args.Length:
                    if (int.TryParse(args[i + 1], out var adapter))
                        config.Capture.AdapterIndex = adapter;
                    i++;
                    break;

                case "--output" when i + 1 < args.Length:
                    if (int.TryParse(args[i + 1], out var output))
                        config.Capture.OutputIndex = output;
                    i++;
                    break;

                case "--fps" when i + 1 < args.Length:
                    if (int.TryParse(args[i + 1], out var fps))
                        config.Capture.TargetFps = fps;
                    i++;
                    break;
            }
        }

        return config;
    }

    /// <summary>
    /// Displays current configuration.
    /// </summary>
    private static void DisplayConfiguration(AppConfiguration config)
    {
        Console.WriteLine("[Config] Current Settings:");
        Console.WriteLine($"  Adapter Index:     {config.Capture.AdapterIndex} (0 = primary GPU)");
        Console.WriteLine($"  Output Index:      {config.Capture.OutputIndex} (0 = primary monitor)");
        Console.WriteLine($"  Target FPS:        {config.Capture.TargetFps}");
        Console.WriteLine($"  Converter Type:    {config.Capture.ConverterType}");
        Console.WriteLine($"  Signaling Server:  {config.Signaling.ServerUrl}");
    }

    /// <summary>
    /// Starts WebRTC session with live streaming.
    /// </summary>
    private static async Task StartWebRTCSessionAsync(AppConfiguration config)
    {
        if (_webrtcSession != null)
        {
            Console.WriteLine("[WebRTC] Session already active!");
            return;
        }

        try
        {
            Console.WriteLine($"[WebRTC] Initializing session...");
            Console.WriteLine($"[WebRTC] Signaling server: {config.Signaling.ServerUrl}");

            // Get screen resolution from capturer and dispose immediately
            int width, height;
            {
                var tempCapturer = new DesktopDuplicator(config.Capture.AdapterIndex, config.Capture.OutputIndex);
                if (!tempCapturer.Initialize())
                {
                    Console.WriteLine("[WebRTC] ✗ Failed to initialize screen capturer.");
                    return;
                }

                width = tempCapturer.Width;
                height = tempCapturer.Height;

                // Explicitly dispose to free duplication resource
                tempCapturer.Dispose();
            }

            Console.WriteLine($"[WebRTC] Screen resolution: {width}x{height}");

            // Create WebRTC session
            _webrtcSession = new WebRTCSession();

            // Subscribe to events
            _webrtcSession.ConnectionStateChanged += (sender, state) =>
            {
                Console.WriteLine($"[WebRTC] Connection state: {state}");
            };

            _webrtcSession.ErrorOccurred += (sender, error) =>
            {
                Console.WriteLine($"[WebRTC] Error: {error}");
            };

            // Initialize with configured ServerId
            var initialized = await _webrtcSession.InitializeAsync(
                width,
                height,
                config.Capture.TargetFps,
                config.Signaling.ServerUrl,
                config.Signaling.ServerId);

            if (!initialized)
            {
                Console.WriteLine("[WebRTC] ✗ Failed to initialize session.");
                _webrtcSession.Dispose();
                _webrtcSession = null;
                return;
            }

            Console.WriteLine($"[WebRTC] ✓ Session initialized");
            Console.WriteLine($"[WebRTC] Server ID: {_webrtcSession.ClientId}");
            Console.WriteLine();
            Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  Server ID: {_webrtcSession.ClientId,-45} ║");
            Console.WriteLine("╠════════════════════════════════════════════════════════════╣");
            Console.WriteLine("║  Share this ID with the WebClient to establish connection  ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.WriteLine("[WebRTC] Waiting for WebClient to initiate connection...");

            // Start capture pipeline with WebRTC video source
            var capturer = new DesktopDuplicator(config.Capture.AdapterIndex, config.Capture.OutputIndex);
            var converter = CreateConverter(config);

            _pipeline = new CapturePipeline(capturer, converter, _webrtcSession.VideoSource);
            Log.Information("WebRTC pipeline initialized with {Converter}", converter.GetType().Name);

            if (_pipeline.Start(config.Capture.TargetFps))
            {
                Console.WriteLine("[WebRTC] ✓ Capture pipeline started!");
                Console.WriteLine("[WebRTC] Streaming to connected clients...");
                Console.WriteLine("[WebRTC] Press any key to return to menu...");
                Console.ReadKey();
            }
            else
            {
                Console.WriteLine("[WebRTC] ✗ Failed to start pipeline.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRTC] ✗ Error: {ex.Message}");
            Log.Error(ex, "Failed to start WebRTC session");

            _webrtcSession?.Dispose();
            _webrtcSession = null;
        }
    }

    /// <summary>
    /// Stops WebRTC session.
    /// </summary>
    private static async Task StopWebRTCSessionAsync()
    {
        if (_webrtcSession == null)
        {
            Console.WriteLine("[WebRTC] No active session.");
            return;
        }

        Console.WriteLine("[WebRTC] Stopping session...");

        // Stop pipeline first
        if (_pipeline?.IsRunning == true)
        {
            await _pipeline.StopAsync();
        }

        _pipeline?.Dispose();
        _pipeline = null;

        _webrtcSession.Dispose();
        _webrtcSession = null;

        Log.Information("WebRTC session stopped");
        Console.WriteLine("[WebRTC] ✓ Session stopped.");
    }

    /// <summary>
    /// Lists all available monitors in the system with their details.
    /// </summary>
    private static void ListAvailableMonitors()
    {
        Console.WriteLine("\n[Monitors] Enumerating available displays...\n");

        var monitors = DesktopDuplicator.EnumerateMonitors();

        if (monitors.Count == 0)
        {
            Console.WriteLine("[Monitors] ✗ No monitors found!");
            return;
        }

        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("              AVAILABLE MONITORS");
        Console.WriteLine("═══════════════════════════════════════════════════════════");

        for (int i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            var isPrimary = monitor.AdapterIndex == 0 && monitor.OutputIndex == 0;
            var primaryMarker = isPrimary ? " [PRIMARY]" : "";

            Console.WriteLine($"\n Monitor #{i + 1}{primaryMarker}");
            Console.WriteLine($"  ├─ Adapter:    {monitor.AdapterName}");
            Console.WriteLine($"  ├─ Output:     {monitor.OutputName}");
            Console.WriteLine($"  ├─ Resolution: {monitor.Width}x{monitor.Height}");
            Console.WriteLine($"  └─ Indices:    Adapter={monitor.AdapterIndex}, Output={monitor.OutputIndex}");
        }

        Console.WriteLine("\n═══════════════════════════════════════════════════════════");
        Console.WriteLine($"Total: {monitors.Count} monitor(s) detected");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("\nTo capture a specific monitor, set AdapterIndex and OutputIndex");
        Console.WriteLine("in appsettings.json or use the Change Settings menu option.");
        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey();
    }

    /// <summary>
    /// Handles Ctrl+C graceful shutdown.
    /// </summary>
    private static void OnConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        Console.WriteLine("\n\n[Shutdown] Ctrl+C detected. Shutting down gracefully...");
        e.Cancel = true; // Prevent immediate termination
        _shouldExit = true;

        if (_pipeline?.IsRunning == true)
        {
            _pipeline.StopAsync().GetAwaiter().GetResult();
        }
    }
}

/// <summary>
/// Application configuration.
/// </summary>
internal sealed class AppConfiguration
{
    public CaptureConfiguration Capture { get; set; } = new();
    public WebRTCConfiguration WebRTC { get; set; } = new();
    public SignalingConfiguration Signaling { get; set; } = new();
}

internal sealed class CaptureConfiguration
{
    public int AdapterIndex { get; set; } = 0;
    public int OutputIndex { get; set; } = 0;
    public int TargetFps { get; set; } = 30;
    public bool UseSimdOptimization { get; set; } = true;
    public ConverterType ConverterType { get; set; } = ConverterType.Simd;
}

/// <summary>
/// Enum defining available pixel converter types.
/// Each converter has different performance characteristics.
/// </summary>
internal enum ConverterType
{
    /// <summary>
    /// Basic converter - simple implementation, no optimizations.
    /// Good for debugging and baseline comparison.
    /// </summary>
    Basic,

    /// <summary>
    /// SIMD-optimized converter using AVX2 instructions.
    /// ~3x faster than basic, best for CPU-bound scenarios.
    /// </summary>
    Simd,

    /// <summary>
    /// Pooled converter using ArrayPool for buffer reuse.
    /// Reduces GC pressure, best for high FPS scenarios (60+ fps).
    /// </summary>
    Pooled
}

internal sealed class WebRTCConfiguration
{
    public List<IceServerConfig> IceServers { get; set; } = new()
    {
        new IceServerConfig { Urls = "stun:stun.l.google.com:19302" }
    };
    public int MaxBitrate { get; set; } = 5000;
    public string VideoCodec { get; set; } = "VP8";
    public bool MultiClientMode { get; set; } = true;
}

internal sealed class IceServerConfig
{
    public string Urls { get; set; } = "";
}

internal sealed class SignalingConfiguration
{
    public string ServerUrl { get; set; } = "wss://localhost:7240/signal";
    public string ServerId { get; set; } = "screen-sender-001";
}
