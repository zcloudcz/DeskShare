using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
// Shared library services and models (moved from DeskShare.Desktop.Services/Models)
using DeskShare.Desktop.Shared.Services;
using DeskShare.Desktop.Shared.Models;
using DeskShare.Core;

namespace DeskShare.Desktop;

/// <summary>
/// Main window with dual-mode (Server/Client) functionality
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
    private int _activeClientConnections = 0; // Track number of active client connections
    private string? _currentConnectionHistoryId; // Track current connection for history update

    public ObservableCollection<string> LogMessages => App.LogMessages;

    public MainWindow()
    {
        InitializeComponent();

        // Setup logging and services
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

        ConnectionsListView.ItemsSource = _savedConnections;
        HistoryItemsControl.ItemsSource = _connectionHistory;

        // Load saved connections and history
        LoadSavedConnections();
        LoadConnectionHistory();

        // Subscribe to online status changes
        _onlineStatusMonitor.OnlineStatusChanged += OnOnlineStatusChanged;

        // Start online status monitoring
        _onlineStatusMonitor.Start();

        // Setup event handlers
        SaveConnectionCheckBox.Checked += (s, e) => ConnectionNamePanel.Visibility = Visibility.Visible;
        SaveConnectionCheckBox.Unchecked += (s, e) => ConnectionNamePanel.Visibility = Visibility.Collapsed;

        // Handle window closing - shutdown everything
        Closed += async (s, e) =>
        {
            try
            {
                // Stop online monitoring
                _onlineStatusMonitor.Stop();

                // Stop server if running
                if (_isServerRunning)
                {
                    await _serverManager.StopAsync();
                }

                // Close all projection windows
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is ProjectionWindow && window != this)
                    {
                        window.Close();
                    }
                }

                // Shutdown application
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during application shutdown");
            }
        };

        // Setup log auto-scroll
        App.LogMessages.CollectionChanged += (s, e) =>
        {
            Dispatcher.BeginInvoke(() => LogScrollViewer.ScrollToBottom());
        };

        // Set DataContext for log binding
        DataContext = this;

        // Show log tab based on App.ShowLogTab (DEBUG mode or --log argument)
        LogTab.Visibility = App.ShowLogTab ? Visibility.Visible : Visibility.Collapsed;

        _logger.LogInformation("MainWindow initialized");
    }

    #region Server Mode Event Handlers

    private async void StartServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogInformation("Starting server...");

            // Disable start button
            StartServerButton.IsEnabled = false;
            StartServerButton.Content = "Starting...";

            // Generate server ID
            string serverId = GenerateServerId();
            ServerIdText.Text = serverId;

            // Generate password (optional extra security layer)
            string? password = null;
            // Password functionality removed - using passkey authentication only

            // Get remote control setting
            bool enableRemoteControl = EnableRemoteControlCheckBox.IsChecked == true;

            // Get trust client permanent setting
            bool trustClientPermanent = TrustClientPermanentCheckBox.IsChecked == true;

            // Start actual server components (ScreenSenderApp, SignalingServer)
            await _serverManager.StartAsync(serverId, password, enableRemoteControl, trustClientPermanent);

            _logger.LogInformation("Server started with remote control {Status}",
                enableRemoteControl ? "ENABLED" : "DISABLED");

            // Subscribe to server events
            _serverManager.ConnectionCountChanged += (s, count) =>
            {
                Dispatcher.Invoke(() => ActiveConnectionsText.Text = count.ToString());
            };

            _serverManager.StatsUpdated += (s, stats) =>
            {
                Dispatcher.Invoke(() =>
                {
                    // Could update additional stats here if UI supports it
                    ActiveConnectionsText.Text = stats.ActiveConnections.ToString();
                });
            };

            _serverManager.PasskeyChanged += OnPasskeyChanged;

            // Update UI to running state
            _isServerRunning = true;
            _serverStartTime = DateTime.Now;

            UpdateServerStatus(ServerStatus.Running);
            ServerIdPanel.Visibility = Visibility.Visible;
            PasskeyPanel.Visibility = Visibility.Visible; // Show passkey panel
            ConnectionInfoPanel.Visibility = Visibility.Visible;

            StartServerButton.IsEnabled = false;
            StopServerButton.IsEnabled = true;

            // Disable server settings - can't change while running
            EnableRemoteControlCheckBox.IsEnabled = false;
            TrustClientPermanentCheckBox.IsEnabled = false;

            // Subscribe to passkey changes
            // TODO: Get access to ScreenSenderService passkey events through ServerManager

            // Update tab availability - disable Client tab
            UpdateTabAvailability();

            // Start uptime timer
            _uptimeTimer = new System.Timers.Timer(1000);
            _uptimeTimer.Elapsed += (s, e) => Dispatcher.Invoke(UpdateUptime);
            _uptimeTimer.Start();

            _logger.LogInformation("Server started successfully with ID: {ServerId}", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server");
            MessageBox.Show($"Failed to start server: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);

            StartServerButton.IsEnabled = true;
            StartServerButton.Content = "Start Server";

            // Re-enable server settings on error
            EnableRemoteControlCheckBox.IsEnabled = true;
            TrustClientPermanentCheckBox.IsEnabled = true;
        }
    }

    private async void StopServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogInformation("Stopping server...");

            StopServerButton.IsEnabled = false;
            StopServerButton.Content = "Stopping...";

            // Stop actual server components
            await _serverManager.StopAsync();

            // Stop uptime timer
            _uptimeTimer?.Stop();
            _uptimeTimer?.Dispose();
            _uptimeTimer = null;

            // Stop passkey countdown timer
            _passkeyCountdownTimer?.Stop();
            _passkeyCountdownTimer?.Dispose();
            _passkeyCountdownTimer = null;

            // Update UI to stopped state
            _isServerRunning = false;
            UpdateServerStatus(ServerStatus.Stopped);
            ServerIdPanel.Visibility = Visibility.Collapsed;
            PasskeyPanel.Visibility = Visibility.Collapsed;
            ConnectionInfoPanel.Visibility = Visibility.Collapsed;

            StartServerButton.IsEnabled = true;
            StartServerButton.Content = "Start Server";
            StopServerButton.Content = "Stop Server";

            // Re-enable server settings
            EnableRemoteControlCheckBox.IsEnabled = true;
            TrustClientPermanentCheckBox.IsEnabled = true;

            // Update tab availability - enable Client tab
            UpdateTabAvailability();

            _logger.LogInformation("Server stopped successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop server");
            MessageBox.Show($"Failed to stop server: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);

            StopServerButton.IsEnabled = true;
            StopServerButton.Content = "Stop Server";
        }
    }

    private void CopyServerId_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ServerIdText.Text);
            _logger.LogInformation("Server ID copied to clipboard");

            // Visual feedback
            var button = (Button)sender;
            var originalContent = button.Content;
            button.Content = "Copied!";
            Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() => button.Content = originalContent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy server ID");
        }
    }

    private void CopyPasskey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Copy passkey without dashes for easier entry
            var passkeyText = PasskeyText.Text.Replace("-", "");
            Clipboard.SetText(passkeyText);
            _logger.LogInformation("Passkey copied to clipboard");

            // Visual feedback
            var button = (Button)sender;
            var originalContent = button.Content;
            button.Content = "Copied!";
            Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() => button.Content = originalContent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy passkey");
        }
    }

    private void OnPasskeyChanged(object? sender, PasskeyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Update passkey display with formatted version (XXX-XXX-XXX)
            PasskeyText.Text = DeskShare.Core.Auth.AuthenticationService.FormatPasskeyForDisplay(e.Passkey);
            _currentPasskeyValidTo = e.ValidTo;

            _logger.LogInformation("Passkey updated in UI: {Passkey}", PasskeyText.Text);

            // Start/restart countdown timer
            _passkeyCountdownTimer?.Stop();
            _passkeyCountdownTimer = new System.Timers.Timer(500); // Update every 500ms
            _passkeyCountdownTimer.Elapsed += (s, args) => Dispatcher.Invoke(UpdatePasskeyCountdown);
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

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ServerPasswordText.Text != "Not Set")
            {
                Clipboard.SetText(ServerPasswordText.Text);
                _logger.LogInformation("Password copied to clipboard");

                // Visual feedback
                var button = (Button)sender;
                var originalContent = button.Content;
                button.Content = "Copied!";
                Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() => button.Content = originalContent));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy password");
        }
    }

    private void UpdateServerStatus(ServerStatus status)
    {
        switch (status)
        {
            case ServerStatus.Running:
                ServerStatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xD4, 0xED, 0xDA));
                ServerStatusText.Text = "Running";
                ServerStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x15, 0x5D, 0x27));
                break;

            case ServerStatus.Stopped:
                ServerStatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xF3, 0xCD));
                ServerStatusText.Text = "Stopped";
                ServerStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x85, 0x64, 0x04));
                break;

            case ServerStatus.Error:
                ServerStatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF8, 0xD7, 0xDA));
                ServerStatusText.Text = "Error";
                ServerStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x84, 0x2C, 0x30));
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
        // Generate a persistent server ID based on MAC address
        // This ID will remain the same for this machine
        return ServerIdGenerator.GenerateServerId();
    }

    private string GeneratePassword()
    {
        // Generate a secure but readable password (8 characters)
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = new Random();
        return new string(Enumerable.Range(0, 8).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    #endregion

    #region Client Mode Event Handlers

    private async void ManualConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string serverId = ManualServerIdTextBox.Text.Trim();
            string passkey = DeskShare.Core.Auth.AuthenticationService.UnformatPasskey(ManualPasskeyTextBox.Text.Trim());
            string password = ManualPasswordBox.Password;

            if (string.IsNullOrEmpty(serverId))
            {
                MessageBox.Show("Please enter a Server ID", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(passkey) || passkey.Length != 9)
            {
                MessageBox.Show("Please enter a valid 9-character passkey", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _logger.LogInformation("Attempting manual connection to server: {ServerId}", serverId);

            // Save connection if requested
            if (SaveConnectionCheckBox.IsChecked == true)
            {
                string connectionName = ConnectionNameTextBox.Text.Trim();
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
                    LastConnected = DateTime.Now.ToString("Last used: yyyy-MM-dd HH:mm")
                };

                _savedConnections.Add(savedConnection);
                await _connectionManager.SaveConnectionAsync(savedConnection);

                UpdateConnectionsListVisibility();
            }

            // Authenticate first, then establish connection
            await ConnectToServerAsync(serverId, passkey, password);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to server");
            MessageBox.Show($"Failed to connect: {ex.Message}", "Connection Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ConnectToSaved_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender;
            var connection = (SavedConnection)button.Tag;

            _logger.LogInformation("Connecting to saved connection: {Name}", connection.Name);

            // Always prompt for passkey (it changes every 45 seconds)
            var passkeyPrompt = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter the current 9-character passkey from the server:",
                "Passkey Required",
                "",
                -1, -1);

            if (string.IsNullOrWhiteSpace(passkeyPrompt))
            {
                return; // User cancelled
            }

            string passkey = DeskShare.Core.Auth.AuthenticationService.UnformatPasskey(passkeyPrompt.Trim());

            if (passkey.Length != 9)
            {
                MessageBox.Show("Invalid passkey. Must be 9 characters.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? password = null;
            if (connection.HasPassword)
            {
                // Prompt for password
                var passwordDialog = new PasswordDialog(connection.Name);
                if (passwordDialog.ShowDialog() == true)
                {
                    password = passwordDialog.Password;
                }
                else
                {
                    return; // User cancelled
                }
            }

            // Update last connected time
            connection.LastConnected = $"Last used: {DateTime.Now:yyyy-MM-dd HH:mm}";
            await _connectionManager.SaveConnectionAsync(connection);

            await ConnectToServerAsync(connection.ServerId, passkey, password);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to saved server");
            MessageBox.Show($"Failed to connect: {ex.Message}", "Connection Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConnectionsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click is not supported anymore - user must use Connect button which prompts for passkey
        // Passkey changes every 45 seconds so can't be stored
    }

    private async void RemoveConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender;
            var connection = (SavedConnection)button.Tag;

            var result = MessageBox.Show(
                $"Remove connection '{connection.Name}'?",
                "Confirm Removal",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
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

        // Create history entry for this connection attempt
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
            // Authenticate with SignalingServer first
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var signalingUrl = SignalingUrl.HttpBaseFromConfig(App.Configuration!);

            // Generate HMAC signature for secure authentication
            var clientId = DeskShare.Core.ServerIdGenerator.GenerateServerId(); // MAC-based static client ID
            var nonce = Guid.NewGuid().ToString(); // Unique request ID
            var timestamp = DateTime.UtcNow;

            var signature = DeskShare.Core.Auth.RequestSigningService.SignRequest(
                serverId,
                passkey,
                timestamp,
                nonce);

            _logger.LogInformation(
                "Signing authentication request | ServerId: {ServerId} | Nonce: {Nonce} | Timestamp: {Timestamp}",
                serverId, nonce, timestamp);

            var authRequest = new DeskShare.Core.Auth.ClientAuthenticationMessage
            {
                ServerId = serverId,
                Passkey = passkey,
                ClientId = clientId,
                Timestamp = timestamp,      // HMAC field
                Nonce = nonce,              // HMAC field
                Signature = signature       // HMAC field
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

            // Mark authentication as successful
            historyEntry.AuthenticationSuccessful = true;
            await _connectionManager.AddHistoryEntryAsync(historyEntry);
            _currentConnectionHistoryId = historyEntry.Id;

            // Add to UI history
            _connectionHistory.Insert(0, historyEntry);
            UpdateHistoryVisibility();

            // Create client manager for this connection
            var clientManager = App.ServiceProvider?.GetService<ClientManager>()
                ?? throw new InvalidOperationException("ClientManager not available");

            // Open projection window and pass the client manager
            var projectionWindow = new ProjectionWindow(serverId, password, clientManager);

            // Track connection lifecycle
            _activeClientConnections++;
            UpdateTabAvailability();

            // Subscribe to window closed event to track when connection ends
            projectionWindow.Closed += async (s, e) =>
            {
                _activeClientConnections--;
                UpdateTabAvailability();
                _logger.LogInformation("Client connection closed for server {ServerId}", serverId);

                // Update history entry with disconnection time
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

            // Record failed connection in history
            historyEntry.AuthenticationSuccessful = false;
            historyEntry.DisconnectedAt = DateTime.Now;
            await _connectionManager.AddHistoryEntryAsync(historyEntry);

            // Add to UI history
            _connectionHistory.Insert(0, historyEntry);
            UpdateHistoryVisibility();

            MessageBox.Show($"Failed to connect: {ex.Message}", "Connection Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
        EmptyConnectionsPanel.Visibility = _savedConnections.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        ConnectionsListView.Visibility = _savedConnections.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void LoadConnectionHistory()
    {
        try
        {
            var history = await _connectionManager.LoadHistoryAsync();
            _connectionHistory.Clear();

            // Take only last 20 entries for display
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
        EmptyHistoryPanel.Visibility = _connectionHistory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        HistoryItemsControl.Visibility = _connectionHistory.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void TrustConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender;
            var entry = (ConnectionHistoryEntry)button.Tag;

            // Toggle trust status
            if (entry.IsTrusted)
            {
                var result = MessageBox.Show(
                    $"Remove trust from this connection?\nYou will need to enter passkey for future connections.",
                    "Remove Trust",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    await _connectionManager.RemoveTrustAsync(entry.ServerId);
                    entry.IsTrusted = false;
                    _logger.LogInformation("Trust removed from server {ServerId}", entry.ServerId);
                }
            }
            else
            {
                var result = MessageBox.Show(
                    $"Trust this connection?\nNo passkey will be required for future connections to this server.",
                    "Trust Connection",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    await _connectionManager.MarkAsTrustedAsync(entry.ServerId);
                    entry.IsTrusted = true;
                    _logger.LogInformation("Server {ServerId} marked as trusted", entry.ServerId);
                }
            }

            // Refresh display
            LoadConnectionHistory();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update trust status");
            MessageBox.Show($"Failed to update trust status: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RemoveHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender;
            var entry = (ConnectionHistoryEntry)button.Tag;

            var history = await _connectionManager.LoadHistoryAsync();
            history.RemoveAll(h => h.Id == entry.Id);

            // Save updated history
            await _connectionManager.CleanupOldHistoryAsync(30); // This will trigger a save

            _connectionHistory.Remove(entry);
            UpdateHistoryVisibility();

            _logger.LogInformation("Removed history entry for {ServerId}", entry.ServerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove history entry");
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = MessageBox.Show(
                "Clear all connection history? Trusted connections will be preserved.",
                "Clear History",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // Keep trusted connections, remove everything else
                await _connectionManager.CleanupOldHistoryAsync(0);
                LoadConnectionHistory();
                _logger.LogInformation("Connection history cleared");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear history");
            MessageBox.Show($"Failed to clear history: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshOnlineStatus_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender;
            var originalContent = button.Content;

            button.Content = "⏳ Checking...";
            button.IsEnabled = false;

            _logger.LogInformation("Manually refreshing online status for trusted servers");
            await _onlineStatusMonitor.CheckNowAsync();

            // Refresh UI
            LoadConnectionHistory();

            button.Content = originalContent;
            button.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh online status");
            MessageBox.Show($"Failed to refresh status: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOnlineStatusChanged(object? sender, OnlineStatusChangedEventArgs e)
    {
        // Update UI on dispatcher thread
        Dispatcher.Invoke(() =>
        {
            _logger.LogDebug("Online status changed for {ServerId}: {Status}",
                e.ServerId, e.IsOnline?.ToString() ?? "Unknown");

            // Find entry in UI collection and update it
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
    /// Updates tab availability based on current mode (Server/Client).
    /// When server is running, disable Client tab.
    /// When client is connected, disable Server tab.
    /// </summary>
    private void UpdateTabAvailability()
    {
        Dispatcher.Invoke(() =>
        {
            if (_isServerRunning)
            {
                // Server is running - disable Client tab
                ClientTab.IsEnabled = false;
                ServerTab.IsEnabled = true;
                _logger.LogDebug("Client tab disabled (server running)");
            }
            else if (_activeClientConnections > 0)
            {
                // Client is connected - disable Server tab
                ServerTab.IsEnabled = false;
                ClientTab.IsEnabled = true;
                _logger.LogDebug("Server tab disabled ({Count} client connection(s) active)", _activeClientConnections);
            }
            else
            {
                // Nothing active - enable both tabs
                ServerTab.IsEnabled = true;
                ClientTab.IsEnabled = true;
                _logger.LogDebug("Both tabs enabled (no active connections)");
            }
        });
    }

    #endregion
}

// SavedConnection and ServerStatus are now in DeskShare.Desktop.Shared.Models

partial class MainWindow
{
    #region Log Tab Event Handlers

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        App.LogMessages.Clear();
        _logger.LogInformation("Log cleared by user");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logText = string.Join(Environment.NewLine, App.LogMessages);
            Clipboard.SetText(logText);
            _logger.LogInformation("Log copied to clipboard ({Lines} lines)", App.LogMessages.Count);
            MessageBox.Show($"Log copied to clipboard ({App.LogMessages.Count} lines)",
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy log to clipboard");
            MessageBox.Show("Failed to copy log to clipboard: " + ex.Message,
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion
}
