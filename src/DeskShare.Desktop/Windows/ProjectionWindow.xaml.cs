using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
// Shared library services and models (moved from DeskShare.Desktop.Services)
using DeskShare.Desktop.Shared.Services;
using DeskShare.Desktop.Shared.Models;
using DeskShare.Core.Models;
using MouseButton = DeskShare.Core.Models.MouseButton;
using System.Runtime.InteropServices;

namespace DeskShare.Desktop;

/// <summary>
/// Fullscreen projection window for remote desktop viewing with input forwarding
/// </summary>
public partial class ProjectionWindow : Window
{
    private readonly ILogger<ProjectionWindow> _logger;
    private readonly ClientManager _clientManager;
    private readonly string _serverId;
    private readonly string? _password;
    private readonly string? _webSocketToken;
    private readonly string? _clientId;
    private readonly IReadOnlyList<DeskShare.Core.Auth.IceServerInfo>? _iceServers;
    private bool _isFullscreen;
    private bool _isConnected;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private WriteableBitmap? _frameBuffer;
    private bool _isInputEnabled = true;  // Enable remote control by default
    private Point _lastMousePosition;

    public ProjectionWindow(
        string serverId,
        string? password,
        ClientManager clientManager,
        string? webSocketToken = null,
        string? clientId = null,
        IReadOnlyList<DeskShare.Core.Auth.IceServerInfo>? iceServers = null)
    {
        InitializeComponent();

        _logger = (App.ServiceProvider?.GetService(typeof(ILogger<ProjectionWindow>)) as ILogger<ProjectionWindow>)
                  ?? throw new InvalidOperationException("Logger not available");

        _clientManager = clientManager;
        _serverId = serverId;
        _password = password;
        _webSocketToken = webSocketToken;
        _clientId = clientId;
        _iceServers = iceServers;

        ServerIdText.Text = $"Server ID: {serverId}";
        ConnectedServerIdText.Text = serverId;

        _logger.LogInformation("ProjectionWindow initialized for server {ServerId}", serverId);

        // Start connection process
        Loaded += async (s, e) => await ConnectToServerAsync();

        // Auto-hide toolbar on mouse move
        MouseMove += ProjectionWindow_MouseMove;

        // Capture input events on the video display area
        VideoImage.MouseMove += VideoImage_MouseMove;
        VideoImage.MouseDown += VideoImage_MouseDown;
        VideoImage.MouseUp += VideoImage_MouseUp;
        VideoImage.MouseWheel += VideoImage_MouseWheel;

        // Make video image focusable for keyboard events
        VideoImage.Focusable = true;
    }

    private async Task ConnectToServerAsync()
    {
        try
        {
            StatusText.Text = "Connecting to server...";
            ConnectionStatusOverlay.Visibility = Visibility.Visible;

            // Subscribe to client events
            _clientManager.FrameReceived += OnFrameReceived;
            _clientManager.StatsUpdated += OnStatsUpdated;
            _clientManager.Connected += OnConnected;
            _clientManager.Disconnected += OnDisconnected;

            // Connect to server via WebRTC
            await _clientManager.ConnectAsync(_serverId, _password, null, _webSocketToken, _clientId, _iceServers);

            _logger.LogInformation("Successfully connected to server {ServerId}", _serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to server {ServerId}", _serverId);
            StatusText.Text = $"Connection failed: {ex.Message}";

            await Task.Delay(3000);
            Close();
        }
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _isConnected = true;
            ConnectionStatusOverlay.Visibility = Visibility.Collapsed;
            TopToolbar.Visibility = Visibility.Visible;

            // Initialize frame buffer (will be resized when first frame arrives)
            _frameBuffer = new WriteableBitmap(1920, 1080, 96, 96, PixelFormats.Bgra32, null);
            VideoImage.Source = _frameBuffer;

            // Focus on video image to receive keyboard events
            VideoImage.Focus();

            _logger.LogInformation("Connection established, video rendering and input forwarding enabled");
        });
    }

    private void OnDisconnected(object? sender, string reason)
    {
        Dispatcher.Invoke(() =>
        {
            _logger.LogInformation("Disconnected: {Reason}", reason);
            MessageBox.Show($"Disconnected: {reason}", "Connection Lost",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        });
    }

    #region Input Event Handlers

    /// <summary>
    /// Handles mouse movement on the video display and forwards to remote server
    /// </summary>
    private async void VideoImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled) return;

        try
        {
            // Get mouse position relative to video image
            var position = e.GetPosition(VideoImage);

            // Normalize coordinates to 0.0 - 1.0 range
            double normalizedX = position.X / VideoImage.ActualWidth;
            double normalizedY = position.Y / VideoImage.ActualHeight;

            // Clamp to valid range
            normalizedX = Math.Clamp(normalizedX, 0.0, 1.0);
            normalizedY = Math.Clamp(normalizedY, 0.0, 1.0);

            // Only send if position changed significantly (reduce bandwidth)
            if (Math.Abs(position.X - _lastMousePosition.X) < 2 &&
                Math.Abs(position.Y - _lastMousePosition.Y) < 2)
            {
                return;
            }

            _lastMousePosition = position;

            var inputMessage = new InputMessage
            {
                Type = InputMessageType.MouseMove,
                X = normalizedX,
                Y = normalizedY,
                Timestamp = DateTime.UtcNow
            };

            await _clientManager.SendInputAsync(inputMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending mouse move");
        }
    }

    /// <summary>
    /// Handles mouse button press events
    /// </summary>
    private async void VideoImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled) return;

        try
        {
            var button = ConvertMouseButton(e.ChangedButton);

            var inputMessage = new InputMessage
            {
                Type = InputMessageType.MouseDown,
                Button = button,
                Timestamp = DateTime.UtcNow
            };

            await _clientManager.SendInputAsync(inputMessage);

            _logger.LogDebug("Mouse button down: {Button}", button);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending mouse down");
        }
    }

    /// <summary>
    /// Handles mouse button release events
    /// </summary>
    private async void VideoImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled) return;

        try
        {
            var button = ConvertMouseButton(e.ChangedButton);

            var inputMessage = new InputMessage
            {
                Type = InputMessageType.MouseUp,
                Button = button,
                Timestamp = DateTime.UtcNow
            };

            await _clientManager.SendInputAsync(inputMessage);

            _logger.LogDebug("Mouse button up: {Button}", button);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending mouse up");
        }
    }

    /// <summary>
    /// Handles mouse wheel scrolling
    /// </summary>
    private async void VideoImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled) return;

        try
        {
            var inputMessage = new InputMessage
            {
                Type = InputMessageType.MouseWheel,
                WheelDelta = e.Delta,
                Timestamp = DateTime.UtcNow
            };

            await _clientManager.SendInputAsync(inputMessage);

            _logger.LogDebug("Mouse wheel: {Delta}", e.Delta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending mouse wheel");
        }
    }

    /// <summary>
    /// Converts WPF mouse button to our MouseButton enum
    /// </summary>
    private MouseButton ConvertMouseButton(System.Windows.Input.MouseButton wpfButton)
    {
        return wpfButton switch
        {
            System.Windows.Input.MouseButton.Left => MouseButton.Left,
            System.Windows.Input.MouseButton.Right => MouseButton.Right,
            System.Windows.Input.MouseButton.Middle => MouseButton.Middle,
            System.Windows.Input.MouseButton.XButton1 => MouseButton.Extra1,
            System.Windows.Input.MouseButton.XButton2 => MouseButton.Extra2,
            _ => MouseButton.Left
        };
    }

    /// <summary>
    /// Handles keyboard key press events (override to capture all keys)
    /// </summary>
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled)
        {
            base.OnKeyDown(e);
            return;
        }

        // Don't forward special keys used for local control
        if (e.Key == Key.F11 || (e.Key == Key.Escape && _isFullscreen))
        {
            base.OnKeyDown(e);
            return;
        }

        try
        {
            var inputMessage = new InputMessage
            {
                Type = InputMessageType.KeyDown,
                KeyCode = KeyInterop.VirtualKeyFromKey(e.Key),
                Shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift),
                Control = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl),
                Alt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt),
                Timestamp = DateTime.UtcNow
            };

            await _clientManager.SendInputAsync(inputMessage);

            // Mark as handled to prevent local processing
            e.Handled = true;

            _logger.LogDebug("Key down: {Key} (VK={VK})", e.Key, inputMessage.KeyCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending key down");
        }
    }

    /// <summary>
    /// Handles keyboard key release events (override to capture all keys)
    /// </summary>
    protected override async void OnKeyUp(KeyEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled)
        {
            base.OnKeyUp(e);
            return;
        }

        // Don't forward special keys used for local control
        if (e.Key == Key.F11 || (e.Key == Key.Escape && _isFullscreen))
        {
            base.OnKeyUp(e);
            return;
        }

        try
        {
            var inputMessage = new InputMessage
            {
                Type = InputMessageType.KeyUp,
                KeyCode = KeyInterop.VirtualKeyFromKey(e.Key),
                Shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift),
                Control = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl),
                Alt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt),
                Timestamp = DateTime.UtcNow
            };

            await _clientManager.SendInputAsync(inputMessage);

            // Mark as handled to prevent local processing
            e.Handled = true;

            _logger.LogDebug("Key up: {Key} (VK={VK})", e.Key, inputMessage.KeyCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending key up");
        }
    }

    #endregion

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            // Enter fullscreen
            _previousWindowState = WindowState;
            _previousWindowStyle = WindowStyle;

            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;

            FullscreenButton.Content = "⛶ Exit Fullscreen";
            FullscreenHint.Visibility = Visibility.Visible;

            // Auto-hide hint after 3 seconds
            Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => FullscreenHint.Visibility = Visibility.Collapsed));

            _logger.LogInformation("Entered fullscreen mode");
        }
        else
        {
            // Exit fullscreen
            WindowStyle = _previousWindowStyle;
            WindowState = _previousWindowState;
            _isFullscreen = false;

            FullscreenButton.Content = "⛶ Fullscreen";
            FullscreenHint.Visibility = Visibility.Collapsed;

            _logger.LogInformation("Exited fullscreen mode");
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Disconnect from remote server?",
            "Confirm Disconnect",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            Close();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                break;

            case Key.Escape:
                if (_isFullscreen)
                {
                    ToggleFullscreen();
                }
                else
                {
                    Disconnect_Click(sender, e);
                }
                break;
        }
    }

    private void ProjectionWindow_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isConnected) return;

        // Show toolbar when mouse is near top
        var position = e.GetPosition(this);
        if (position.Y < 100)
        {
            TopToolbar.Visibility = Visibility.Visible;
        }
        else if (position.Y > 150)
        {
            // Auto-hide toolbar
            TopToolbar.Visibility = Visibility.Collapsed;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Cleanup WebRTC connection
        _clientManager.FrameReceived -= OnFrameReceived;
        _clientManager.StatsUpdated -= OnStatsUpdated;
        _clientManager.Connected -= OnConnected;
        _clientManager.Disconnected -= OnDisconnected;

        _clientManager.DisconnectAsync().GetAwaiter().GetResult();

        _logger.LogInformation("ProjectionWindow closed, disconnected from {ServerId}", _serverId);
    }

    private void OnFrameReceived(object? sender, byte[] frameData)
    {
        _logger.LogInformation("[ProjectionWindow] OnFrameReceived called - frame size: {Size} bytes", frameData.Length);

        // Update VideoImage with received frame
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_frameBuffer == null)
                {
                    _logger.LogWarning("[ProjectionWindow] Frame buffer is null, cannot render frame");
                    return;
                }

                _logger.LogInformation("[ProjectionWindow] Locking frame buffer for rendering...");

                // Lock the bitmap for writing
                _frameBuffer.Lock();

                // Copy frame data to bitmap
                unsafe
                {
                    IntPtr pBackBuffer = _frameBuffer.BackBuffer;
                    int stride = _frameBuffer.BackBufferStride;
                    int expectedSize = _frameBuffer.PixelWidth * _frameBuffer.PixelHeight * 4;

                    _logger.LogInformation("[ProjectionWindow] Frame buffer: {Width}x{Height}, stride: {Stride}, expected size: {ExpectedSize}, actual size: {ActualSize}",
                        _frameBuffer.PixelWidth, _frameBuffer.PixelHeight, stride, expectedSize, frameData.Length);

                    // Copy frame data (assuming BGRA32 format)
                    if (frameData.Length >= expectedSize)
                    {
                        _logger.LogInformation("[ProjectionWindow] Copying frame data to back buffer...");
                        Marshal.Copy(
                            frameData, 0, pBackBuffer,
                            Math.Min(frameData.Length, stride * _frameBuffer.PixelHeight));
                        _logger.LogInformation("[ProjectionWindow] Frame data copied successfully");
                    }
                    else
                    {
                        _logger.LogWarning("[ProjectionWindow] Frame data too small: {ActualSize} < {ExpectedSize}", frameData.Length, expectedSize);
                    }
                }

                // Mark the entire bitmap as dirty
                _frameBuffer.AddDirtyRect(new System.Windows.Int32Rect(
                    0, 0, _frameBuffer.PixelWidth, _frameBuffer.PixelHeight));

                _frameBuffer.Unlock();

                _logger.LogInformation("[ProjectionWindow] Frame rendered successfully to VideoImage");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rendering frame");
            }
        });
    }

    private void OnStatsUpdated(object? sender, ConnectionStats stats)
    {
        Dispatcher.Invoke(() =>
        {
            FpsText.Text = stats.Fps.ToString();
            LatencyText.Text = $"{stats.LatencyMs}ms";
            QualityText.Text = stats.Resolution;
        });
    }
}
