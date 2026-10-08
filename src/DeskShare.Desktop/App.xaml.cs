using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
// Shared library services (replaces DeskShare.Desktop.Services for moved types)
using DeskShare.Desktop.Shared.Services;
// Platform-specific services that remain in this project
using DeskShare.Desktop.Services;
// Core interfaces for DI registration
using DeskShare.Core.Interfaces;
using Velopack;

namespace DeskShare.Desktop;

/// <summary>
/// Main application with dependency injection setup
/// </summary>
public partial class App : Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }
    public static IConfiguration? Configuration { get; private set; }
    public static ObservableCollection<string> LogMessages { get; } = new();
    public static bool ShowLogTab { get; private set; }

    /// <summary>
    /// Custom entry point (see StartupObject in csproj). Velopack must run first: on install/update/uninstall
    /// it creates shortcuts or exits early before any WPF window is shown.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Check for --log command line argument
        ShowLogTab = e.Args.Contains("--log");
#if DEBUG
        ShowLogTab = true; // Always show in DEBUG mode
#endif

        // Load configuration
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

        Configuration = configBuilder.Build();

        // Create WPF UI dispatcher for shared services that need UI thread access
        var uiDispatcher = new WpfUIDispatcher();

        // Setup Serilog
        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemoteDesktop.NET",
            "logs",
            "desktop-.log");

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        // Add in-memory sink if Log tab should be shown
        // Now uses shared InMemoryLogSink with injected IUIDispatcher
        if (ShowLogTab)
        {
            logConfig = logConfig.WriteTo.Sink(new InMemoryLogSink(LogMessages, uiDispatcher, maxMessages: 500));
        }

        Log.Logger = logConfig.CreateLogger();

        // Redirect Console.WriteLine to Serilog (for Core library Console output)
        var consoleWriter = new ConsoleToLoggerWriter();
        Console.SetOut(consoleWriter);
        Console.SetError(consoleWriter);

        // Setup dependency injection
        var services = new ServiceCollection();

        // Add configuration
        services.AddSingleton<IConfiguration>(Configuration);

        // Add logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        // Register platform-specific services via interfaces
        // IUIDispatcher: WPF implementation wraps Application.Current.Dispatcher.BeginInvoke
        services.AddSingleton<IUIDispatcher>(uiDispatcher);
        // IClipboardManager: Windows-specific clipboard implementation
        services.AddSingleton<IClipboardManager>(sp =>
        {
            var serilogLogger = Serilog.Log.ForContext<WindowsClipboardManager>();
            return new WindowsClipboardManager(serilogLogger);
        });

        // Add shared application services (from DeskShare.Desktop.Shared)
        services.AddSingleton<ServerManager>();
        services.AddTransient<ClientManager>();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<OnlineStatusMonitor>();

        // Build service provider
        ServiceProvider = services.BuildServiceProvider();

        var logger = ServiceProvider.GetRequiredService<ILogger<App>>();
        logger.LogInformation("RemoteDesktop.NET Desktop application started");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        Log.Information("RemoteDesktop.NET Desktop application exiting");
        Log.CloseAndFlush();

        (ServiceProvider as IDisposable)?.Dispose();
    }
}
