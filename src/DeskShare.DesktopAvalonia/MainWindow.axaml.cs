using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
// Alias to avoid conflict with Avalonia.Controls.WindowIcon
using MsBoxIcon = MsBox.Avalonia.Enums.Icon;
using DeskShare.DesktopAvalonia.Dialogs;
// Models and services now come from the shared library
using DeskShare.Desktop.Shared.Models;
using DeskShare.Desktop.Shared.Services;
using DeskShare.DesktopAvalonia.Windows;
using DeskShare.Core;

namespace DeskShare.DesktopAvalonia;

/// <summary>
/// Main window with dual-mode (Server/Client) functionality.
/// Avalonia equivalent of WPF's MainWindow.xaml.cs.
///
/// Key migration changes from WPF:
/// - Window base class: Avalonia.Controls.Window (not System.Windows.Window)
/// - Dispatcher.Invoke() → Dispatcher.UIThread.Invoke()
/// - Visibility.Visible/Collapsed → IsVisible = true/false
/// - System.Windows.Clipboard.SetText() → TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync()
/// - MessageBox.Show() → MessageBoxManager.GetMessageBoxStandard().ShowAsync()
/// - Microsoft.VisualBasic.Interaction.InputBox → PasskeyDialog
/// - System.Windows.Media.SolidColorBrush → Avalonia.Media.SolidColorBrush
/// - System.Windows.Media.Color.FromRgb → Avalonia.Media.Color.FromRgb
/// - Application.Current.Shutdown() → lifetime.Shutdown()
/// - ListView → ListBox
/// - MouseDoubleClick → DoubleTapped
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly ServerManager _serverManager;
    private readonly ConnectionManager _connectionManager;
    private readonly OnlineStatusMonitor _onlineStatusMonitor;
    private readonly ObservableCollection<SavedConnection> _savedConnections;
    private readonly ObservableCollection<ConnectionHistoryEntry> _connectionHistory;
    private System.Timers.Timer? _uptimeTimer;
    private System.Timers.Timer? _passkeyCountdownTimer;
    private DateTime _serverStartTime;
    private bool _isServerRunning;
    private DateTime _currentPasskeyValidTo;
    private int _activeClientConnections = 0;
    private string? _currentConnectionHistoryId;

    /// <summary>
    /// Log messages collection bound to the Log tab's ItemsControl.
    /// </summary>
    public ObservableCollection<string> LogMessages => App.LogMessages;

    public MainWindow()
    {
        InitializeComponent();

        // Screen capture is implemented for Windows (DXGI) and Linux/X11 only; the macOS capturer is a stub.
        if (OperatingSystem.IsMacOS())
        {
            StartServerButton.IsEnabled = false;
            StartServerButton.Content = "Sharing from macOS is not available yet";
        }

        // Setup logging and services from DI container (same as WPF project)
        _logger = App.ServiceProvider?.GetService<ILogger<MainWindow>>()
                  ?? throw new InvalidOperationException("Logger not available");

        _serverManager = App.ServiceProvider?.GetService<ServerManager>()
                  ?? throw new InvalidOperationException("ServerManager not available");

        _connectionManager = App.ServiceProvider?.GetService<ConnectionManager>()
                  ?? throw new InvalidOperationException("ConnectionManager not available");

        _onlineStatusMonitor = App.ServiceProvider?.GetService<OnlineStatusMonitor>()
                  ?? throw new InvalidOperationException("OnlineStatusMonitor not available");

        _savedConnections = new ObservableCollection<SavedConnection>();
        _connectionHistory = new ObservableCollection<ConnectionHistoryEntry>();

        // Bind data sources (ListBox replaces ListView)
        ConnectionsListBox.ItemsSource = _savedConnections;
        HistoryItemsControl.ItemsSource = _connectionHistory;

        // Load saved connections and history
        LoadSavedConnections();
        LoadConnectionHistory();

        // Subscribe to online status changes
        _onlineStatusMonitor.OnlineStatusChanged += OnOnlineStatusChanged;
        _onlineStatusMonitor.Start();

        // Save checkbox toggle: IsVisible replaces Visibility.Visible/Collapsed
        SaveConnectionCheckBox.IsCheckedChanged += (s, e) =>
        {
            ConnectionNamePanel.IsVisible = SaveConnectionCheckBox.IsChecked == true;
        };

        // Handle window closing - shutdown everything
        Closed += async (s, e) =>
        {
            try
            {
                _onlineStatusMonitor.Stop();

                if (_isServerRunning)
                {
                    await _serverManager.StopAsync();
                }

                // Close all projection windows
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    // Collect windows to close (can't modify during enumeration)
                    var windowsToClose = desktop.Windows
                        .OfType<ProjectionWindow>()
                        .ToList();

                    foreach (var window in windowsToClose)
                    {
                        window.Close();
                    }

                    // Shutdown application (replaces Application.Current.Shutdown())
                    desktop.Shutdown();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during application shutdown");
            }
        };

        // Setup log auto-scroll
        App.LogMessages.CollectionChanged += (s, e) =>
        {
            // Avalonia uses Dispatcher.UIThread instead of WPF's Dispatcher
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                LogScrollViewer.ScrollToEnd();
            });
        };

        // Set DataContext for data binding (log messages)
        DataContext = this;

        // Show log tab based on App.ShowLogTab
        // IsVisible replaces Visibility.Visible/Collapsed
        LogTab.IsVisible = App.ShowLogTab;

        _logger.LogInformation("MainWindow initialized (Avalonia)");
    }

    #region Server Mode Event Handlers

    private async void StartServer_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogInformation("Starting server...");

            StartServerButton.IsEnabled = false;
            StartServerButton.Content = "Starting...";

            string serverId = GenerateServerId();
            ServerIdText.Text = serverId;

            string? password = null;

            bool enableRemoteControl = EnableRemoteControlCheckBox.IsChecked == true;
            bool trustClientPermanent = TrustClientPermanentCheckBox.IsChecked == true;

            await _serverManager.StartAsync(serverId, password, enableRemoteControl, trustClientPermanent);

            _logger.LogInformation("Server started with remote control {Status}",
                enableRemoteControl ? "ENABLED" : "DISABLED");

            // Subscribe to server events
            _serverManager.ConnectionCountChanged += (s, count) =>
            {
                Dispatcher.UIThread.Invoke(() => ActiveConnectionsText.Text = count.ToString());
            };

            _serverManager.StatsUpdated += (s, stats) =>
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    ActiveConnectionsText.Text = stats.ActiveConnections.ToString();
                });
            };

            _serverManager.PasskeyChanged += OnPasskeyChanged;

            _isServerRunning = true;
            _serverStartTime = DateTime.Now;

            UpdateServerStatus(ServerStatus.Running);
            ServerIdPanel.IsVisible = true;
            PasskeyPanel.IsVisible = true;
            ConnectionInfoPanel.IsVisible = true;

            StartServerButton.IsEnabled = false;
            StopServerButton.IsEnabled = true;

            EnableRemoteControlCheckBox.IsEnabled = false;
            TrustClientPermanentCheckBox.IsEnabled = false;

            UpdateTabAvailability();

            _uptimeTimer = new System.Timers.Timer(1000);
            _uptimeTimer.Elapsed += (s, e) => Dispatcher.UIThread.Invoke(UpdateUptime);
            _uptimeTimer.Start();

            _logger.LogInformation("Server started successfully with ID: {ServerId}", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server");

            // Avalonia replacement for WPF's MessageBox.Show()
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Error",
                $"Failed to start server: {ex.Message}",
                ButtonEnum.Ok,
                MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);

            StartServerButton.IsEnabled = true;
            StartServerButton.Content = "Start Server";
            EnableRemoteControlCheckBox.IsEnabled = true;
            TrustClientPermanentCheckBox.IsEnabled = true;
        }
    }

    private async void StopServer_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogInformation("Stopping server...");

            StopServerButton.IsEnabled = false;
            StopServerButton.Content = "Stopping...";

            await _serverManager.StopAsync();

            _uptimeTimer?.Stop();
            _uptimeTimer?.Dispose();
            _uptimeTimer = null;

            _passkeyCountdownTimer?.Stop();
            _passkeyCountdownTimer?.Dispose();
            _passkeyCountdownTimer = null;

            _isServerRunning = false;
            UpdateServerStatus(ServerStatus.Stopped);
            ServerIdPanel.IsVisible = false;
            PasskeyPanel.IsVisible = false;
            ConnectionInfoPanel.IsVisible = false;

            StartServerButton.IsEnabled = true;
            StartServerButton.Content = "Start Server";
            StopServerButton.Content = "Stop Server";

            EnableRemoteControlCheckBox.IsEnabled = true;
            TrustClientPermanentCheckBox.IsEnabled = true;

            UpdateTabAvailability();

            _logger.LogInformation("Server stopped successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop server");

            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Error",
                $"Failed to stop server: {ex.Message}",
                ButtonEnum.Ok,
                MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);

            StopServerButton.IsEnabled = true;
            StopServerButton.Content = "Stop Server";
        }
    }

    private async void CopyServerId_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Avalonia clipboard: TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync()
            // Replaces WPF's System.Windows.Clipboard.SetText()
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(ServerIdText.Text);
            }
            _logger.LogInformation("Server ID copied to clipboard");

            var button = (Button)sender!;
            var originalContent = button.Content;
            button.Content = "Copied!";
            _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.UIThread.Invoke(() => button.Content = originalContent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy server ID");
        }
    }

    private async void CopyPasskey_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var passkeyText = PasskeyText.Text?.Replace("-", "") ?? "";
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(passkeyText);
            }
            _logger.LogInformation("Passkey copied to clipboard");

            var button = (Button)sender!;
            var originalContent = button.Content;
            button.Content = "Copied!";
            _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.UIThread.Invoke(() => button.Content = originalContent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy passkey");
        }
    }

    private void OnPasskeyChanged(object? sender, PasskeyChangedEventArgs e)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            PasskeyText.Text = DeskShare.Core.Auth.AuthenticationService.FormatPasskeyForDisplay(e.Passkey);
            _currentPasskeyValidTo = e.ValidTo;

            _logger.LogInformation("Passkey updated in UI: {Passkey}", PasskeyText.Text);

            _passkeyCountdownTimer?.Stop();
            _passkeyCountdownTimer = new System.Timers.Timer(500);
            _passkeyCountdownTimer.Elapsed += (s, args) => Dispatcher.UIThread.Invoke(UpdatePasskeyCountdown);
            _passkeyCountdownTimer.Start();
        });
    }

    private void UpdatePasskeyCountdown()
    {
        var now = DateTime.UtcNow;
        var timeRemaining = _currentPasskeyValidTo - now;

        if (timeRemaining.TotalSeconds > 0)
        {
            PasskeyExpirationText.Text = $"Expires in: {(int)timeRemaining.TotalSeconds}s";
        }
        else
        {
            PasskeyExpirationText.Text = "Generating new passkey...";
        }
    }

    private async void CopyPassword_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ServerPasswordText.Text != "Not Set")
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(ServerPasswordText.Text ?? "");
                }
                _logger.LogInformation("Password copied to clipboard");

                var button = (Button)sender!;
                var originalContent = button.Content;
                button.Content = "Copied!";
                _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.UIThread.Invoke(() => button.Content = originalContent));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy password");
        }
    }

    /// <summary>
    /// Updates the server status badge colors.
    /// Uses Avalonia.Media.SolidColorBrush instead of System.Windows.Media.SolidColorBrush.
    /// </summary>
    private void UpdateServerStatus(ServerStatus status)
    {
        switch (status)
        {
            case ServerStatus.Running:
                ServerStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xD4, 0xED, 0xDA));
                ServerStatusText.Text = "Running";
                ServerStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x5D, 0x27));
                break;

            case ServerStatus.Stopped:
                ServerStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xCD));
                ServerStatusText.Text = "Stopped";
                ServerStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x64, 0x04));
                break;

            case ServerStatus.Error:
                ServerStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xD7, 0xDA));
                ServerStatusText.Text = "Error";
                ServerStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x84, 0x2C, 0x30));
                break;
        }
    }

    private void UpdateUptime()
    {
        if (_isServerRunning)
        {
            var uptime = DateTime.Now - _serverStartTime;
            UptimeText.Text = $"{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
        }
    }

    private string GenerateServerId()
    {
        return ServerIdGenerator.GenerateServerId();
    }

    #endregion

    #region Client Mode Event Handlers

    private async void ManualConnect_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string serverId = ManualServerIdTextBox.Text?.Trim() ?? "";
            string passkey = DeskShare.Core.Auth.AuthenticationService.UnformatPasskey(ManualPasskeyTextBox.Text?.Trim() ?? "");
            // In Avalonia, TextBox with PasswordChar is used instead of PasswordBox
            string password = ManualPasswordBox.Text ?? "";

            if (string.IsNullOrEmpty(serverId))
            {
                var msgBox = MessageBoxManager.GetMessageBoxStandard(
                    "Validation Error",
                    "Please enter a Server ID",
                    ButtonEnum.Ok,
                    MsBoxIcon.Warning);
                await msgBox.ShowWindowDialogAsync(this);
                return;
            }

            if (string.IsNullOrEmpty(passkey) || passkey.Length != 9)
            {
                var msgBox = MessageBoxManager.GetMessageBoxStandard(
                    "Validation Error",
                    "Please enter a valid 9-character passkey",
                    ButtonEnum.Ok,
                    MsBoxIcon.Warning);
                await msgBox.ShowWindowDialogAsync(this);
                return;
            }

            _logger.LogInformation("Attempting manual connection to server: {ServerId}", serverId);

            if (SaveConnectionCheckBox.IsChecked == true)
            {
                string connectionName = ConnectionNameTextBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(connectionName))
                {
                    connectionName = $"Server {serverId}";
                }

                var savedConnection = new SavedConnection
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = connectionName,
                    ServerId = serverId,
                    HasPassword = !string.IsNullOrEmpty(password),
                    LastConnected = DateTime.Now.ToString("'Last used:' yyyy-MM-dd HH:mm")
                };

                _savedConnections.Add(savedConnection);
                await _connectionManager.SaveConnectionAsync(savedConnection);
                UpdateConnectionsListVisibility();
            }

            await ConnectToServerAsync(serverId, passkey, password);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to server");
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Connection Error",
                $"Failed to connect: {ex.Message}",
                ButtonEnum.Ok,
                MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
    }

    private async void ConnectToSaved_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender!;
            var connection = (SavedConnection)button.Tag!;

            _logger.LogInformation("Connecting to saved connection: {Name}", connection.Name);

            // Show PasskeyDialog instead of Microsoft.VisualBasic.Interaction.InputBox
            var passkeyDialog = new PasskeyDialog();
            var passkeyResult = await passkeyDialog.ShowDialog<string?>(this);

            if (string.IsNullOrWhiteSpace(passkeyResult))
            {
                return; // User cancelled
            }

            string passkey = DeskShare.Core.Auth.AuthenticationService.UnformatPasskey(passkeyResult.Trim());

            if (passkey.Length != 9)
            {
                var msgBox = MessageBoxManager.GetMessageBoxStandard(
                    "Validation Error",
                    "Invalid passkey. Must be 9 characters.",
                    ButtonEnum.Ok,
                    MsBoxIcon.Warning);
                await msgBox.ShowWindowDialogAsync(this);
                return;
            }

            string? password = null;
            if (connection.HasPassword)
            {
                var passwordDialog = new PasswordDialog(connection.Name);
                var passwordResult = await passwordDialog.ShowDialog<bool>(this);
                if (passwordResult)
                {
                    password = passwordDialog.Password;
                }
                else
                {
                    return; // User cancelled
                }
            }

            connection.LastConnected = $"Last used: {DateTime.Now:yyyy-MM-dd HH:mm}";
            await _connectionManager.SaveConnectionAsync(connection);

            await ConnectToServerAsync(connection.ServerId, passkey, password);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to saved server");
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Connection Error",
                $"Failed to connect: {ex.Message}",
                ButtonEnum.Ok,
                MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
    }

    /// <summary>
    /// DoubleTapped replaces WPF's MouseDoubleClick event.
    /// </summary>
    private void ConnectionsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Double-click disabled - user must use Connect button which prompts for passkey
    }

    private async void RemoveConnection_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender!;
            var connection = (SavedConnection)button.Tag!;

            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Confirm Removal",
                $"Remove connection '{connection.Name}'?",
                ButtonEnum.YesNo,
                MsBoxIcon.Question);
            var result = await msgBox.ShowWindowDialogAsync(this);

            if (result == ButtonResult.Yes)
            {
                _savedConnections.Remove(connection);
                await _connectionManager.RemoveConnectionAsync(connection.Id);
                UpdateConnectionsListVisibility();
                _logger.LogInformation("Removed saved connection: {Name}", connection.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove connection");
        }
    }

    private async Task ConnectToServerAsync(string serverId, string passkey, string? password)
    {
        _logger.LogInformation("Authenticating with server {ServerId}", serverId);

        var historyEntry = new ConnectionHistoryEntry
        {
            ServerId = serverId,
            ConnectedAt = DateTime.Now,
            Type = ConnectionType.Client,
            AuthenticationSuccessful = false,
            IsTrusted = await _connectionManager.IsTrustedAsync(serverId)
        };

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var signalingUrl = SignalingUrl.HttpBaseFromConfig(App.Configuration!);

            var clientId = DeskShare.Core.ServerIdGenerator.GenerateServerId();
            var nonce = Guid.NewGuid().ToString();
            var timestamp = DateTime.UtcNow;

            var signature = DeskShare.Core.Auth.RequestSigningService.SignRequest(
                serverId, passkey, timestamp, nonce);

            _logger.LogInformation(
                "Signing authentication request | ServerId: {ServerId} | Nonce: {Nonce} | Timestamp: {Timestamp}",
                serverId, nonce, timestamp);

            var authRequest = new DeskShare.Core.Auth.ClientAuthenticationMessage
            {
                ServerId = serverId,
                Passkey = passkey,
                ClientId = clientId,
                Timestamp = timestamp,
                Nonce = nonce,
                Signature = signature
            };

            var authResponse = await httpClient.PostAsJsonAsync($"{signalingUrl}/authenticate", authRequest);

            if (!authResponse.IsSuccessStatusCode)
            {
                var errorContent = await authResponse.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Authentication failed: {authResponse.StatusCode}. {errorContent}");
            }

            var authResult = await authResponse.Content.ReadFromJsonAsync<DeskShare.Core.Auth.ClientAuthenticationResponse>();

            if (authResult == null || !authResult.Success)
            {
                throw new InvalidOperationException($"Authentication failed: {authResult?.ErrorMessage ?? "Unknown error"}");
            }

            _logger.LogInformation("Authentication successful. Remote control enabled: {RemoteControl}", authResult.RemoteControlEnabled);

            historyEntry.AuthenticationSuccessful = true;
            await _connectionManager.AddHistoryEntryAsync(historyEntry);
            _currentConnectionHistoryId = historyEntry.Id;

            _connectionHistory.Insert(0, historyEntry);
            UpdateHistoryVisibility();

            var clientManager = App.ServiceProvider?.GetService<ClientManager>()
                ?? throw new InvalidOperationException("ClientManager not available");

            var projectionWindow = new ProjectionWindow(serverId, password, clientManager);

            _activeClientConnections++;
            UpdateTabAvailability();

            projectionWindow.Closed += async (s, e) =>
            {
                _activeClientConnections--;
                UpdateTabAvailability();
                _logger.LogInformation("Client connection closed for server {ServerId}", serverId);

                if (_currentConnectionHistoryId != null)
                {
                    await _connectionManager.UpdateHistoryEntryAsync(_currentConnectionHistoryId, entry =>
                    {
                        entry.DisconnectedAt = DateTime.Now;
                    });
                    _currentConnectionHistoryId = null;
                }
            };

            projectionWindow.Show();
            _logger.LogInformation("Projection window opened for server {ServerId}", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open projection window");

            historyEntry.AuthenticationSuccessful = false;
            historyEntry.DisconnectedAt = DateTime.Now;
            await _connectionManager.AddHistoryEntryAsync(historyEntry);

            _connectionHistory.Insert(0, historyEntry);
            UpdateHistoryVisibility();

            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Connection Error",
                $"Failed to connect: {ex.Message}",
                ButtonEnum.Ok,
                MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
    }

    private async void LoadSavedConnections()
    {
        try
        {
            var connections = await _connectionManager.LoadConnectionsAsync();
            foreach (var conn in connections)
            {
                _savedConnections.Add(conn);
            }
            UpdateConnectionsListVisibility();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load saved connections");
        }
    }

    private void UpdateConnectionsListVisibility()
    {
        // IsVisible (bool) replaces Visibility.Visible/Collapsed
        EmptyConnectionsPanel.IsVisible = _savedConnections.Count == 0;
        ConnectionsListBox.IsVisible = _savedConnections.Count > 0;
    }

    private async void LoadConnectionHistory()
    {
        try
        {
            var history = await _connectionManager.LoadHistoryAsync();
            _connectionHistory.Clear();

            foreach (var entry in history.Take(20))
            {
                _connectionHistory.Add(entry);
            }
            UpdateHistoryVisibility();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load connection history");
        }
    }

    private void UpdateHistoryVisibility()
    {
        EmptyHistoryPanel.IsVisible = _connectionHistory.Count == 0;
        HistoryItemsControl.IsVisible = _connectionHistory.Count > 0;
    }

    private async void TrustConnection_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender!;
            var entry = (ConnectionHistoryEntry)button.Tag!;

            if (entry.IsTrusted)
            {
                var msgBox = MessageBoxManager.GetMessageBoxStandard(
                    "Remove Trust",
                    "Remove trust from this connection?\nYou will need to enter passkey for future connections.",
                    ButtonEnum.YesNo,
                    MsBoxIcon.Question);
                var result = await msgBox.ShowWindowDialogAsync(this);

                if (result == ButtonResult.Yes)
                {
                    await _connectionManager.RemoveTrustAsync(entry.ServerId);
                    entry.IsTrusted = false;
                    _logger.LogInformation("Trust removed from server {ServerId}", entry.ServerId);
                }
            }
            else
            {
                var msgBox = MessageBoxManager.GetMessageBoxStandard(
                    "Trust Connection",
                    "Trust this connection?\nNo passkey will be required for future connections to this server.",
                    ButtonEnum.YesNo,
                    MsBoxIcon.Question);
                var result = await msgBox.ShowWindowDialogAsync(this);

                if (result == ButtonResult.Yes)
                {
                    await _connectionManager.MarkAsTrustedAsync(entry.ServerId);
                    entry.IsTrusted = true;
                    _logger.LogInformation("Server {ServerId} marked as trusted", entry.ServerId);
                }
            }

            LoadConnectionHistory();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update trust status");
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Error",
                $"Failed to update trust status: {ex.Message}",
                ButtonEnum.Ok,
                MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
    }

    private async void RemoveHistory_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender!;
            var entry = (ConnectionHistoryEntry)button.Tag!;

            var history = await _connectionManager.LoadHistoryAsync();
            history.RemoveAll(h => h.Id == entry.Id);
            await _connectionManager.CleanupOldHistoryAsync(30);

            _connectionHistory.Remove(entry);
            UpdateHistoryVisibility();

            _logger.LogInformation("Removed history entry for {ServerId}", entry.ServerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove history entry");
        }
    }

    private async void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Clear History",
                "Clear all connection history? Trusted connections will be preserved.",
                ButtonEnum.YesNo,
                MsBoxIcon.Question);
            var result = await msgBox.ShowWindowDialogAsync(this);

            if (result == ButtonResult.Yes)
            {
                await _connectionManager.CleanupOldHistoryAsync(0);
                LoadConnectionHistory();
                _logger.LogInformation("Connection history cleared");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear history");
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Error",
                $"Failed to clear history: {ex.Message}",
                ButtonEnum.Ok,
                MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
    }

    private async void RefreshOnlineStatus_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender!;
            var originalContent = button.Content;

            button.Content = "Checking...";
            button.IsEnabled = false;

            _logger.LogInformation("Manually refreshing online status for trusted servers");
            await _onlineStatusMonitor.CheckNowAsync();

            LoadConnectionHistory();

            button.Content = originalContent;
            button.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh online status");
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Error",
                $"Failed to refresh status: {ex.Message}",
                ButtonEnum.Ok,
                MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
    }

    private void OnOnlineStatusChanged(object? sender, OnlineStatusChangedEventArgs e)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            _logger.LogDebug("Online status changed for {ServerId}: {Status}",
                e.ServerId, e.IsOnline?.ToString() ?? "Unknown");

            var entry = _connectionHistory.FirstOrDefault(h => h.ServerId == e.ServerId);
            if (entry != null)
            {
                entry.IsOnline = e.IsOnline;
                entry.LastOnlineCheck = DateTime.UtcNow;

                // Force UI refresh by removing and re-adding
                var index = _connectionHistory.IndexOf(entry);
                _connectionHistory.RemoveAt(index);
                _connectionHistory.Insert(index, entry);
            }
        });
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _uptimeTimer?.Stop();
        _uptimeTimer?.Dispose();
        _logger.LogInformation("MainWindow closed");
    }

    #region Tab Management

    /// <summary>
    /// Updates tab availability based on current mode.
    /// Uses IsEnabled (same property name as WPF).
    /// </summary>
    private void UpdateTabAvailability()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (_isServerRunning)
            {
                ClientTab.IsEnabled = false;
                ServerTab.IsEnabled = true;
                _logger.LogDebug("Client tab disabled (server running)");
            }
            else if (_activeClientConnections > 0)
            {
                ServerTab.IsEnabled = false;
                ClientTab.IsEnabled = true;
                _logger.LogDebug("Server tab disabled ({Count} client connection(s) active)", _activeClientConnections);
            }
            else
            {
                ServerTab.IsEnabled = true;
                ClientTab.IsEnabled = true;
                _logger.LogDebug("Both tabs enabled (no active connections)");
            }
        });
    }

    #endregion

    #region Log Tab Event Handlers

    private void ClearLog_Click(object? sender, RoutedEventArgs e)
    {
        App.LogMessages.Clear();
        _logger.LogInformation("Log cleared by user");
    }

    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var logText = string.Join(Environment.NewLine, App.LogMessages);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(logText);
            }
            _logger.LogInformation("Log copied to clipboard ({Lines} lines)", App.LogMessages.Count);

            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Success",
                $"Log copied to clipboard ({App.LogMessages.Count} lines)",
                ButtonEnum.Ok,
                MsBoxIcon.Info);
            await msgBox.ShowWindowDialogAsync(this);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy log to clipboard");
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Error",
                "Failed to copy log to clipboard: " + ex.Message,
                ButtonEnum.Ok,
                MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
    }

    #endregion
}
