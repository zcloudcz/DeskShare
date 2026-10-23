using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.ServiceProcess;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskShare.Core;
using DeskShare.Core.Auth;
using DeskShare.DesktopAvalonia.Dialogs;
using DeskShare.Unattended;

namespace DeskShare.DesktopAvalonia.Controls;

/// <summary>
/// Enable / disable / update the unattended-access Windows service. The actual work (copy to Program Files, ACLs,
/// service registration) is done by DeskShare.Service.exe re-launched elevated (UAC), so this app never needs admin rights.
/// "Set password..." gives the unattended agent a permanent password, so a remote viewer can connect with the Server ID
/// and that password instead of the rotating passkey nobody is there to read (see PasswordSetup for the exchange).
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
        PasswordPanel.IsVisible = installed != null;
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
    // The password commands run the INSTALLED (admin-protected) helper, not the app's user-writable copy.
    private async void OnClearPassword(object? sender, RoutedEventArgs e) => await RunElevated(PasswordSetup.ClearVerb, InstalledExe);

    private static string InstalledExe => Path.Combine(UnattendedPaths.InstallDir, UnattendedPaths.ServiceExeName);

    private static ProcessStartInfo Elevated(string exe, string arguments) => new(exe, arguments)
    {
        UseShellExecute = true, // required for "runas"
        Verb = "runas",
        WorkingDirectory = Environment.SystemDirectory, // never the user-writable app folder for an elevated process
        WindowStyle = ProcessWindowStyle.Hidden
    };

    [System.Runtime.Versioning.SupportedOSPlatform("windows")] // the panel is only shown on Windows
    private async void OnSetPassword(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var password = await new SetPasswordDialog().ShowDialog<string?>(owner);
        if (password is null) return;

        SetPasswordButton.IsEnabled = ClearPasswordButton.IsEnabled = false;
        string note;
        try
        {
            // Random name, one instance, only this user can open it. The helper checks that this process serves it
            // (--owner-pid), and we accept only the helper process we started (see PasswordSetup for the why).
            var pipeName = "DeskShare-set-password-" + Guid.NewGuid().ToString("N");
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var p = Process.Start(Elevated(InstalledExe, $"{PasswordSetup.SetVerb} --pipe {pipeName} --owner-pid {Environment.ProcessId}"))
                ?? throw new InvalidOperationException("the service helper did not start");

            // The helper may fail before it connects (then it just exits), so wait for whichever comes first.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var accept = PasswordSetup.AcceptHelperAsync(pipe, p.Id, PipePeer.ClientProcessId, cts.Token);
            if (await Task.WhenAny(accept, p.WaitForExitAsync(cts.Token)) != accept)
                throw new InvalidOperationException("the service helper ended early, see %ProgramData%\\DeskShare\\logs\\service.log");
            await accept; // rethrows a cancellation (timeout)

            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true);
            // PBKDF2 takes about a second: off the UI thread.
            var serverId = await PasswordSetup.RunAppAsync(reader, writer, id => Task.Run(() =>
            {
                var keys = E2EBinding.FromPermanentPassword(password, id);
                return (keys.AuthKey, keys.E2EKey);
            }), cts.Token);
            await p.WaitForExitAsync(cts.Token);
            note = $"password set, connect with Server ID {ServerIdGenerator.FormatServerIdForDisplay(serverId)} and the password";
        }
        catch (Win32Exception) { note = "cancelled"; } // user declined the UAC prompt
        catch (OperationCanceledException) { note = "password not set (timed out)"; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { note = "password not set: " + ex.Message; }
        SetPasswordButton.IsEnabled = ClearPasswordButton.IsEnabled = true;
        Refresh(note);
    }

    private async void RunElevated(string verb) => await RunElevated(verb, SourceExe);

    private async Task RunElevated(string verb, string exe)
    {
        EnableButton.IsEnabled = UpdateButton.IsEnabled = DisableButton.IsEnabled = false;
        string? note = null;
        try
        {
            using var p = Process.Start(Elevated(exe, verb));
            if (p != null)
            {
                await p.WaitForExitAsync();
                if (p.ExitCode != 0) note = $"failed (exit code {p.ExitCode}), see %ProgramData%\\DeskShare\\logs\\service.log if present";
            }
        }
        catch (Win32Exception) { note = "cancelled"; } // user declined the UAC prompt
        Refresh(note);
    }
}
