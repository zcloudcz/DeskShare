using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.ServiceProcess;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskShare.Unattended;

namespace DeskShare.DesktopAvalonia.Controls;

/// <summary>
/// Enable / disable / update the unattended-access Windows service. The actual work (copy to Program Files, ACLs,
/// service registration) is done by DeskShare.Service.exe re-launched elevated (UAC), so this app never needs admin rights.
/// Note: until the permanent password lands (U3) the agent still uses the rotating passkey, so a remote viewer cannot
/// connect to an unattended machine unless someone reads the current passkey.
/// </summary>
public partial class UnattendedAccessPanel : UserControl
{
    // Folder shipped inside the app package (release.yml publishes the service + agent there).
    private static string SourceDir => Path.Combine(AppContext.BaseDirectory, "service");
    private static string SourceExe => Path.Combine(SourceDir, UnattendedPaths.ServiceExeName);

    public UnattendedAccessPanel()
    {
        InitializeComponent();
        IsVisible = OperatingSystem.IsWindows();
        if (IsVisible)
            Refresh();
    }

    private void Refresh(string? note = null)
    {
        var installed = InstalledVersion();
        var running = OperatingSystem.IsWindows() && IsRunning();
        var appVersion = (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        var update = installed != null && ServiceVersion.UpdateAvailable(installed, appVersion);
        var packaged = File.Exists(SourceExe);

        StatusText.Text = "Status: " + (installed == null ? "Not installed"
            : update ? "Update available"
            : running ? "Running" : "Stopped") + (note is null ? "" : " - " + note);
        EnableButton.IsVisible = installed == null;
        EnableButton.IsEnabled = packaged;
        UpdateButton.IsVisible = update;
        UpdateButton.IsEnabled = packaged;
        DisableButton.IsVisible = installed != null;
        if (!packaged && installed == null)
            StatusText.Text += " (service files are not part of this build)";
    }

    private static string? InstalledVersion()
    {
        var exe = Path.Combine(UnattendedPaths.InstallDir, UnattendedPaths.ServiceExeName);
        return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(UnattendedPaths.ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException) { return false; } // not registered
    }

    private void OnEnable(object? sender, RoutedEventArgs e) => RunElevated("install");
    private void OnDisable(object? sender, RoutedEventArgs e) => RunElevated("uninstall");

    private async void RunElevated(string verb)
    {
        EnableButton.IsEnabled = UpdateButton.IsEnabled = DisableButton.IsEnabled = false;
        string? note = null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(SourceExe, verb)
            {
                UseShellExecute = true, // required for "runas"
                Verb = "runas",
                WorkingDirectory = SourceDir,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (p != null)
            {
                await p.WaitForExitAsync();
                if (p.ExitCode != 0) note = "failed, see %ProgramData%\\DeskShare\\logs\\service.log";
            }
        }
        catch (Win32Exception) { note = "cancelled"; } // user declined the UAC prompt
        Refresh(note);
    }
}
