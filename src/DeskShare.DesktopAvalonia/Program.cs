using Avalonia;
using Velopack;

namespace DeskShare.DesktopAvalonia;

/// <summary>
/// Entry point for the Avalonia application.
/// This replaces WPF's auto-generated Main() method.
/// In WPF, the entry point is auto-generated from App.xaml's StartupUri.
/// In Avalonia, we explicitly create and configure the application builder.
/// </summary>
public class Program
{
    /// <summary>
    /// Application entry point.
    /// Avalonia requires an explicit Main() unlike WPF which auto-generates one.
    /// </summary>
    /// <param name="args">Command line arguments (e.g., --log to show log tab).</param>
    [STAThread]
    public static int Main(string[] args)
    {
        // MSI custom actions (elevated, no UI): write / remove the machine policy and exit.
        if (PolicyCommand.IsPolicyVerb(args)) return PolicyCommand.Run(args);

        // Velopack must run first: on install/update/uninstall it creates shortcuts or exits early.
        // Skipped for the per-machine MSI install, which is updated by deploying a newer MSI, never by Velopack.
        if (!DeskShare.Unattended.MachinePolicy.IsMsiInstall(AppContext.BaseDirectory))
            VelopackApp.Build().Run();

        // BuildAvaloniaApp() creates the Avalonia application with its configuration.
        // StartWithClassicDesktopLifetime() runs it as a standard desktop app with a main window.
        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Configures the Avalonia application builder.
    /// This is also used by the Avalonia visual designer for previews.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()       // Automatically detect Windows/macOS/Linux
            .WithInterFont()           // Use the Inter font family (bundled with Avalonia.Fonts.Inter)
            .LogToTrace();             // Log Avalonia framework messages to Trace output
}
