using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DeskShare.Core;
using DeskShare.Core.Auth;
using DeskShare.Desktop.Shared.Services;
using DeskShare.DesktopAvalonia.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeskShare.DesktopAvalonia;

/// <summary>
/// Optional ZCLOUD account UI ("Sign in", "My devices"). Kept in its own file so the main window logic stays as it
/// was; everything here stays hidden unless the signaling server reports accounts as enabled.
/// </summary>
public partial class MainWindow
{
    private AccountSession? _account;

    /// <summary>A row of the "My devices" list (public: Avalonia's reflection bindings read its properties).</summary>
    public sealed record MyDeviceItem(string ServerId, string Name, string Detail, string Status, IBrush StatusBrush);

    private async void InitAccounts()
    {
        _account = App.ServiceProvider?.GetService<AccountSession>();
        if (_account is null) return;
        await _account.InitializeAsync();
        if (!_account.Available) return; // accounts off on this server: no account UI at all

        _account.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateAccountUi);
        AccountBar.IsVisible = true;
        UpdateAccountUi();
    }

    private void UpdateAccountUi()
    {
        var signedIn = _account?.IsSignedIn == true;
        AccountNameText.Text = signedIn ? _account!.UserName : "";
        SignInButton.Content = signedIn ? "Sign out" : "Sign in";
        AddDeviceCard.IsVisible = signedIn;
        AddDeviceButton.IsEnabled = signedIn;
        MyDevicesCard.IsVisible = signedIn;
        if (signedIn) _ = LoadMyDevicesAsync();
        else MyDevicesListBox.ItemsSource = null;
    }

    private async void SignIn_Click(object? sender, RoutedEventArgs e)
    {
        if (_account is null) return;
        SignInButton.IsEnabled = false;
        try
        {
            if (_account.IsSignedIn) await _account.SignOutAsync();
            else await _account.SignInAsync();
        }
        catch (Exception ex) when (ex is Microsoft.Identity.Client.MsalException or InvalidOperationException or HttpRequestException)
        {
            // Includes the user closing the browser tab (MsalClientException "authentication_canceled").
            _logger.LogWarning("Sign-in failed: {Message}", ex.Message);
            AccountNameText.Text = "Sign-in failed";
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    // Server Mode: bind this computer's Server ID to the account. The proof is made with the same owner secret the
    // running sender registers with (see DeviceClaimProof), so the server can tell it really is this computer.
    private async void AddThisComputer_Click(object? sender, RoutedEventArgs e)
    {
        if (_account is null) return;
        if (!_serverManager.IsRunning || _serverManager.ServerId is not { } serverId)
        {
            AddDeviceStatusText.Text = "Start the server first: the device must be online to be added.";
            return;
        }

        AddDeviceButton.IsEnabled = false;
        try
        {
            var key = DeviceClaimProof.ClaimKey(new ServerOwnerSecretStore().GetOrCreate());
            await _account.Api.ClaimAsync(await _account.GetTokenAsync(), Environment.MachineName, unattended: false,
                (accountKey, timestamp, nonce) => Task.FromResult((serverId, DeviceClaimProof.Compute(key, serverId, accountKey, timestamp, nonce))));
            AddDeviceStatusText.Text = $"Added {ServerIdGenerator.FormatServerIdForDisplay(serverId)} to your devices.";
            _ = LoadMyDevicesAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            AddDeviceStatusText.Text = "Not added: " + ex.Message;
        }
        finally
        {
            AddDeviceButton.IsEnabled = _account.IsSignedIn;
        }
    }

    private void RefreshMyDevices_Click(object? sender, RoutedEventArgs e) => _ = LoadMyDevicesAsync();

    private async Task LoadMyDevicesAsync()
    {
        if (_account is not { IsSignedIn: true }) return;
        try
        {
            var devices = await _account.Api.GetDevicesAsync(await _account.GetTokenAsync());
            MyDevicesListBox.ItemsSource = devices.Devices
                .OrderByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new MyDeviceItem(
                    d.ServerId,
                    d.Name,
                    ServerIdGenerator.FormatServerIdForDisplay(d.ServerId) + (d.Unattended ? "  (unattended)" : ""),
                    d.Online ? "Online" : "Last seen " + d.LastSeen.ToLocalTime().ToString("g"),
                    d.Online ? Brushes.Green : Brushes.Gray))
                .ToList();
            MyDevicesStatusText.Text = devices.Devices.Count == 0
                ? "No devices yet. Use \"Add this computer to my devices\" in Server Mode on each computer."
                : "Click a device to fill in its Server ID. The passkey or password is still needed.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            MyDevicesStatusText.Text = "Could not load your devices: " + ex.Message;
        }
    }

    // Fills the Server ID only: the passkey / permanent password must still be typed, the account never replaces it.
    private void MyDevices_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var item = MyDevicesListBox.SelectedItem as MyDeviceItem;
        RemoveDeviceButton.IsEnabled = item != null;
        if (item is null) return;
        ManualServerIdTextBox.Text = ServerIdGenerator.FormatServerIdForDisplay(item.ServerId);
        ManualPasskeyTextBox.Focus();
    }

    private async void RemoveMyDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (_account is null || MyDevicesListBox.SelectedItem is not MyDeviceItem item) return;
        try
        {
            await _account.Api.RemoveAsync(await _account.GetTokenAsync(), item.ServerId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            MyDevicesStatusText.Text = "Not removed: " + ex.Message;
            return;
        }
        await LoadMyDevicesAsync();
    }
}
