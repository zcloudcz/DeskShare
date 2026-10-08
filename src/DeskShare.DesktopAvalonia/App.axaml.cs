using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
// Shared library services and models (replaces DeskShare.DesktopAvalonia.Services/Models)
using DeskShare.Desktop.Shared.Services;
// Platform-specific services that remain in this project
using DeskShare.DesktopAvalonia.Services;
// Core interfaces for DI registration
using DeskShare.Core.Interfaces;

namespace DeskShare.DesktopAvalonia;

/// <summary>
/// Main application class with dependency injection setup.
/// Avalonia equivalent of WPF's App.xaml.cs.
///
/// Key differences from WPF:
/// - OnStartup(StartupEventArgs) -> OnFrameworkInitializationCompleted()
/// - OnExit(ExitEventArgs) -> subscribe to lifetime.Exit event
/// - StartupUri removed; main window created manually in OnFrameworkInitializationCompleted
/// - Application.Current.Shutdown() -> (lifetime as IClassicDesktopStyleApplicationLifetime).Shutdown()
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Global DI container - accessible from all windows/services.
    /// Same pattern as WPF project.
    /// </summary>
    public static IServiceProvider? ServiceProvider { get; private set; }

    /// <summary>
    /// Application configuration loaded from appsettings.json.
    /// </summary>
    public static IConfiguration? Configuration { get; private set; }

    /// <summary>
    /// In-memory log messages collection for the Log tab.
    /// Bound to the ListBox in MainWindow's Log tab.
    /// </summary>
    public static ObservableCollection<string> LogMessages { get; } = new();

    /// <summary>
    /// Whether to show the Log tab in the UI.
    /// Set to true when --log argument is passed or in DEBUG mode.
    /// </summary>
    public static bool ShowLogTab { get; private set; }

    public override void Initialize()
    {
        // Load the AXAML markup (equivalent to WPF's InitializeComponent)
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called when the Avalonia framework is fully initialized.
    /// This replaces WPF's OnStartup method.
    /// Here we setup DI, logging, and create the main window.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Check for --log command line argument (same as WPF project)
            ShowLogTab = desktop.Args?.Contains("--log") ?? false;
#if DEBUG
            ShowLogTab = true; // Always show log tab in DEBUG mode
#endif

            // Load configuration from appsettings.json (same as WPF project)
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            Configuration = configBuilder.Build();

            // Create Avalonia UI dispatcher for shared services that need UI thread access
            var uiDispatcher = new AvaloniaUIDispatcher();

            // Setup Serilog logging (same as WPF project)
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RemoteDesktop.NET",
                "logs",
                "desktop-avalonia-.log");

            var logConfig = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

            // Add in-memory sink for Log tab display
            // Now uses shared InMemoryLogSink with injected IUIDispatcher
            if (ShowLogTab)
            {
                logConfig = logConfig.WriteTo.Sink(new InMemoryLogSink(LogMessages, uiDispatcher, maxMessages: 500));
            }

            Log.Logger = logConfig.CreateLogger();

            // Redirect Console.WriteLine to Serilog (captures Core library output)
            var consoleWriter = new ConsoleToLoggerWriter();
            Console.SetOut(consoleWriter);
            Console.SetError(consoleWriter);

            // Setup dependency injection (same services as WPF project)
            var services = new ServiceCollection();

            // Add configuration
            services.AddSingleton<IConfiguration>(Configuration);

            // Add logging with Serilog
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(dispose: true);
            });

            // Register platform-specific services via interfaces
            // IUIDispatcher: Avalonia implementation wraps Dispatcher.UIThread.InvokeAsync
            services.AddSingleton<IUIDispatcher>(uiDispatcher);
            // IClipboardManager: Avalonia implementation for cross-platform clipboard
            services.AddSingleton<IClipboardManager>(sp =>
            {
                var serilogLogger = Serilog.Log.ForContext<AvaloniaClipboardManager>();
                return new AvaloniaClipboardManager(serilogLogger);
            });

            // Add shared application services (from DeskShare.Desktop.Shared)
            services.AddSingleton<ServerManager>();
            services.AddTransient<ClientManager>();
            services.AddSingleton<ConnectionManager>();
            services.AddSingleton<OnlineStatusMonitor>();

            // Build DI container
            ServiceProvider = services.BuildServiceProvider();

            var logger = ServiceProvider.GetRequiredService<ILogger<App>>();
            logger.LogInformation("DeskShare Avalonia application started");
            _ = UpdateService.CheckAndStageAsync(logger);

            // Create and show the main window (replaces WPF's StartupUri)
            desktop.MainWindow = new MainWindow();

            // Subscribe to application exit for cleanup (replaces WPF's OnExit)
            desktop.Exit += (sender, args) =>
            {
                Log.Information("DeskShare Avalonia application exiting");
                Log.CloseAndFlush();
                (ServiceProvider as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
