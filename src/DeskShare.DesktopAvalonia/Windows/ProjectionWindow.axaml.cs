using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
// Alias to avoid conflict with Avalonia.Controls.WindowIcon
using MsBoxIcon = MsBox.Avalonia.Enums.Icon;
using DeskShare.DesktopAvalonia.Helpers;
// Models and services now come from the shared library
using DeskShare.Desktop.Shared.Models;
using DeskShare.Desktop.Shared.Services;
using DeskShare.Core.Models;
using AvaloniaMouseButton = DeskShare.Core.Models.MouseButton;

namespace DeskShare.DesktopAvalonia.Windows;

/// <summary>
/// Fullscreen projection window for remote desktop viewing with input forwarding.
/// Avalonia equivalent of WPF's ProjectionWindow.xaml.cs.
///
/// Key migration changes from WPF:
/// - WriteableBitmap: new WriteableBitmap(w,h,96,96,PixelFormats.Bgra32,null) → new WriteableBitmap(new PixelSize(w,h), new Vector(96,96), PixelFormat.Bgra8888)
/// - Frame rendering: Lock/Unlock/AddDirtyRect → using(var framebuffer = bitmap.Lock()) pattern
/// - Mouse events: MouseMove/MouseDown/MouseUp → PointerMoved/PointerPressed/PointerReleased
/// - e.ChangedButton → e.GetCurrentPoint(element).Properties.PointerUpdateKind
/// - MouseWheel → PointerWheelChanged
/// - KeyInterop.VirtualKeyFromKey(key) → KeyMapper.ToVirtualKey(key)
/// - Keyboard.IsKeyDown(Key.LeftShift) → e.KeyModifiers.HasFlag(KeyModifiers.Shift)
/// - WindowStyle = WindowStyle.None → SystemDecorations = SystemDecorations.None
/// - Visibility.Visible/Collapsed → IsVisible = true/false
/// - MessageBox.Show → MessageBoxManager.GetMessageBoxStandard().ShowAsync()
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
    private SystemDecorations _previousSystemDecorations;
    private WriteableBitmap? _frameBuffer;
    private bool _isInputEnabled = true;
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

        // Start connection when window loads
        Opened += async (s, e) => await ConnectToServerAsync();

        // Auto-hide toolbar on pointer move (PointerMoved replaces WPF's MouseMove)
        PointerMoved += ProjectionWindow_PointerMoved;

        // Capture input events on the video display area
        // Avalonia uses Pointer* events instead of WPF's Mouse* events
        VideoImage.PointerMoved += VideoImage_PointerMoved;
        VideoImage.PointerPressed += VideoImage_PointerPressed;
        VideoImage.PointerReleased += VideoImage_PointerReleased;
        VideoImage.PointerWheelChanged += VideoImage_PointerWheelChanged;

        // Make video image focusable for keyboard events
        VideoImage.Focusable = true;
    }

    /// <summary>
    /// Default constructor required by Avalonia XAML designer.
    /// </summary>
    public ProjectionWindow()
    {
        InitializeComponent();
        _logger = null!;
        _clientManager = null!;
        _serverId = string.Empty;
    }

    private async Task ConnectToServerAsync()
    {
        try
        {
            StatusText.Text = "Connecting to server...";
            ConnectionStatusOverlay.IsVisible = true;

            _clientManager.FrameReceived += OnFrameReceived;
            _clientManager.StatsUpdated += OnStatsUpdated;
            _clientManager.Connected += OnConnected;
            _clientManager.Disconnected += OnDisconnected;

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
        Dispatcher.UIThread.Invoke(() =>
        {
            _isConnected = true;
            ConnectionStatusOverlay.IsVisible = false;
            TopToolbar.IsVisible = true;

            // Avalonia WriteableBitmap uses PixelSize, Vector, and PixelFormat.Bgra8888
            // WPF used: new WriteableBitmap(1920, 1080, 96, 96, PixelFormats.Bgra32, null)
            _frameBuffer = new WriteableBitmap(
                new PixelSize(1920, 1080),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888);
            VideoImage.Source = _frameBuffer;

            VideoImage.Focus();

            _logger.LogInformation("Connection established, video rendering and input forwarding enabled");
        });
    }

    private void OnDisconnected(object? sender, string reason)
    {
        Dispatcher.UIThread.Invoke(async () =>
        {
            _logger.LogInformation("Disconnected: {Reason}", reason);
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Connection Lost",
                $"Disconnected: {reason}",
                ButtonEnum.Ok,
                MsBoxIcon.Info);
            await msgBox.ShowWindowDialogAsync(this);
            Close();
        });
    }

    #region Input Event Handlers

    /// <summary>
    /// Handles pointer movement on the video display. Replaces WPF's MouseMove event.
    /// </summary>
    private async void VideoImage_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled) return;

        try
        {
            // e.GetPosition() works the same in Avalonia as WPF
            var position = e.GetPosition(VideoImage);

            double normalizedX = position.X / VideoImage.Bounds.Width;
            double normalizedY = position.Y / VideoImage.Bounds.Height;

            normalizedX = Math.Clamp(normalizedX, 0.0, 1.0);
            normalizedY = Math.Clamp(normalizedY, 0.0, 1.0);

            // Only send if position changed significantly
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
    /// Handles pointer button press. Replaces WPF's MouseDown event.
    /// In Avalonia, PointerPressed replaces MouseDown and uses PointerUpdateKind.
    /// </summary>
    private async void VideoImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled) return;

        try
        {
            // Avalonia: e.GetCurrentPoint(element).Properties.PointerUpdateKind
            // WPF used: e.ChangedButton
            var point = e.GetCurrentPoint(VideoImage);
            var button = ConvertPointerButton(point.Properties.PointerUpdateKind);

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
    /// Handles pointer button release. Replaces WPF's MouseUp event.
    /// </summary>
    private async void VideoImage_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled) return;

        try
        {
            var point = e.GetCurrentPoint(VideoImage);
            var button = ConvertPointerButton(point.Properties.PointerUpdateKind);

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
    /// Handles pointer wheel scrolling. Replaces WPF's MouseWheel event.
    /// Avalonia's Delta is a Vector, not an int. Delta.Y corresponds to WPF's Delta.
    /// </summary>
    private async void VideoImage_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled) return;

        try
        {
            // Avalonia: e.Delta.Y is the vertical scroll amount
            // WPF used: e.Delta (int, multiples of 120)
            // Avalonia Delta is normalized, multiply by 120 for compatibility
            var wheelDelta = (int)(e.Delta.Y * 120);

            var inputMessage = new InputMessage
            {
                Type = InputMessageType.MouseWheel,
                WheelDelta = wheelDelta,
                Timestamp = DateTime.UtcNow
            };

            await _clientManager.SendInputAsync(inputMessage);
            _logger.LogDebug("Mouse wheel: {Delta}", wheelDelta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending mouse wheel");
        }
    }

    /// <summary>
    /// Converts Avalonia PointerUpdateKind to our MouseButton enum.
    /// Replaces WPF's ConvertMouseButton(System.Windows.Input.MouseButton).
    /// </summary>
    private AvaloniaMouseButton ConvertPointerButton(PointerUpdateKind updateKind)
    {
        return updateKind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => AvaloniaMouseButton.Left,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => AvaloniaMouseButton.Right,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => AvaloniaMouseButton.Middle,
            PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => AvaloniaMouseButton.Extra1,
            PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => AvaloniaMouseButton.Extra2,
            _ => AvaloniaMouseButton.Left
        };
    }

    /// <summary>
    /// Handles keyboard key press. Replaces WPF's OnKeyDown override.
    /// Key differences:
    /// - KeyInterop.VirtualKeyFromKey(e.Key) → KeyMapper.ToVirtualKey(e.Key)
    /// - Keyboard.IsKeyDown(Key.LeftShift) → e.KeyModifiers.HasFlag(KeyModifiers.Shift)
    /// </summary>
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled)
        {
            base.OnKeyDown(e);
            return;
        }

        // Don't forward local control keys
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
                // KeyMapper.ToVirtualKey replaces WPF's KeyInterop.VirtualKeyFromKey
                KeyCode = KeyMapper.ToVirtualKey(e.Key),
                // e.KeyModifiers replaces Keyboard.IsKeyDown() checks
                Shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                Control = e.KeyModifiers.HasFlag(KeyModifiers.Control),
                Alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt),
                Timestamp = DateTime.UtcNow
            };

            await _clientManager.SendInputAsync(inputMessage);
            e.Handled = true;

            _logger.LogDebug("Key down: {Key} (VK={VK})", e.Key, inputMessage.KeyCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending key down");
        }
    }

    /// <summary>
    /// Handles keyboard key release. Same migration pattern as OnKeyDown.
    /// </summary>
    protected override async void OnKeyUp(KeyEventArgs e)
    {
        if (!_isConnected || !_isInputEnabled)
        {
            base.OnKeyUp(e);
            return;
        }

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
                KeyCode = KeyMapper.ToVirtualKey(e.Key),
                Shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                Control = e.KeyModifiers.HasFlag(KeyModifiers.Control),
                Alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt),
                Timestamp = DateTime.UtcNow
            };

            await _clientManager.SendInputAsync(inputMessage);
            e.Handled = true;

            _logger.LogDebug("Key up: {Key} (VK={VK})", e.Key, inputMessage.KeyCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending key up");
        }
    }

    #endregion

    private void ToggleFullscreen_Click(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    /// <summary>
    /// Toggles fullscreen mode.
    /// WindowStyle.None → SystemDecorations.None (Avalonia equivalent)
    /// </summary>
    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            // Enter fullscreen
            _previousWindowState = WindowState;
            _previousSystemDecorations = SystemDecorations;

            // SystemDecorations.None replaces WindowStyle.None
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;

            FullscreenButton.Content = "Exit Fullscreen";
            FullscreenHint.IsVisible = true;

            _ = Task.Delay(3000).ContinueWith(_ =>
                Dispatcher.UIThread.Invoke(() => FullscreenHint.IsVisible = false));

            _logger.LogInformation("Entered fullscreen mode");
        }
        else
        {
            // Exit fullscreen
            SystemDecorations = _previousSystemDecorations;
            WindowState = _previousWindowState;
            _isFullscreen = false;

            FullscreenButton.Content = "Fullscreen";
            FullscreenHint.IsVisible = false;

            _logger.LogInformation("Exited fullscreen mode");
        }
    }

    private async void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        var msgBox = MessageBoxManager.GetMessageBoxStandard(
            "Confirm Disconnect",
            "Disconnect from remote server?",
            ButtonEnum.YesNo,
            MsBoxIcon.Question);
        var result = await msgBox.ShowWindowDialogAsync(this);

        if (result == ButtonResult.Yes)
        {
            Close();
        }
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
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

    /// <summary>
    /// Shows/hides toolbar based on pointer position. Replaces WPF's MouseMove handler.
    /// </summary>
    private void ProjectionWindow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isConnected) return;

        var position = e.GetPosition(this);
        if (position.Y < 100)
        {
            TopToolbar.IsVisible = true;
        }
        else if (position.Y > 150)
        {
            TopToolbar.IsVisible = false;
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

    /// <summary>
    /// Renders received video frames to the WriteableBitmap.
    ///
    /// Avalonia WriteableBitmap rendering differences from WPF:
    /// - WPF: bitmap.Lock() → Marshal.Copy → bitmap.AddDirtyRect() → bitmap.Unlock()
    /// - Avalonia: using (var fb = bitmap.Lock()) { Marshal.Copy(data, 0, fb.Address, size) }
    ///   The Lock() returns an ILockedFramebuffer that auto-unlocks on Dispose.
    /// </summary>
    private void OnFrameReceived(object? sender, byte[] frameData)
    {
        _logger.LogInformation("[ProjectionWindow] OnFrameReceived called - frame size: {Size} bytes", frameData.Length);

        Dispatcher.UIThread.Invoke(() =>
        {
            try
            {
                if (_frameBuffer == null)
                {
                    _logger.LogWarning("[ProjectionWindow] Frame buffer is null, cannot render frame");
                    return;
                }

                _logger.LogInformation("[ProjectionWindow] Locking frame buffer for rendering...");

                // Avalonia uses ILockedFramebuffer (using pattern) instead of WPF's Lock/Unlock
                using (var framebuffer = _frameBuffer.Lock())
                {
                    int expectedSize = framebuffer.RowBytes * framebuffer.Size.Height;

                    _logger.LogInformation("[ProjectionWindow] Frame buffer: {Width}x{Height}, rowBytes: {RowBytes}, expected size: {ExpectedSize}, actual size: {ActualSize}",
                        framebuffer.Size.Width, framebuffer.Size.Height, framebuffer.RowBytes, expectedSize, frameData.Length);

                    if (frameData.Length >= expectedSize)
                    {
                        _logger.LogInformation("[ProjectionWindow] Copying frame data to framebuffer...");
                        Marshal.Copy(frameData, 0, framebuffer.Address,
                            Math.Min(frameData.Length, expectedSize));
                        _logger.LogInformation("[ProjectionWindow] Frame data copied successfully");
                    }
                    else
                    {
                        _logger.LogWarning("[ProjectionWindow] Frame data too small: {ActualSize} < {ExpectedSize}",
                            frameData.Length, expectedSize);
                    }
                }
                // Lock is auto-released via Dispose (no manual Unlock needed)

                _logger.LogInformation("[ProjectionWindow] Frame rendered successfully to VideoImage");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rendering frame");
            }
        });
    }

    /// <summary>
    /// Updates connection stats in the toolbar.
    /// </summary>
    private void OnStatsUpdated(object? sender, ConnectionStats stats)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            FpsText.Text = stats.Fps.ToString();
            LatencyText.Text = $"{stats.LatencyMs}ms";
            QualityText.Text = stats.Resolution;
        });
    }
}
