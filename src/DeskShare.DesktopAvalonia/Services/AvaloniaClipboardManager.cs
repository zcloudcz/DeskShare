using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using DeskShare.Core.Interfaces;
using Serilog;

namespace DeskShare.DesktopAvalonia.Services;

/// <summary>
/// Cross-platform clipboard manager using Avalonia's clipboard API.
/// Replaces WPF's WindowsClipboardManager which used System.Windows.Clipboard.
///
/// Key differences from WPF version:
/// - WPF used: Clipboard.GetText() / Clipboard.SetText() (STA thread required)
/// - Avalonia uses: TopLevel.GetTopLevel(view)?.Clipboard (async API, cross-platform)
/// - Polling approach remains the same (check clipboard every 500ms)
/// - No image support yet (same limitation as WPF version)
/// </summary>
public sealed class AvaloniaClipboardManager : IClipboardManager
{
    private readonly ILogger _logger;
    private readonly object _lockObject = new();
    private bool _isMonitoring;
    private bool _isSyncEnabled = true;
    private bool _disposed;

    // Statistics
    private long _totalChangesDetected;
    private long _totalMessagesSent;
    private long _totalMessagesReceived;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private DateTime? _lastSyncTime;

    // Flag to prevent infinite loop when we set clipboard ourselves
    private bool _suppressNextChange;

    /// <inheritdoc />
    public bool IsMonitoring => _isMonitoring;

    /// <inheritdoc />
    public bool IsSyncEnabled
    {
        get => _isSyncEnabled;
        set => _isSyncEnabled = value;
    }

    /// <inheritdoc />
    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public AvaloniaClipboardManager(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.Information("AvaloniaClipboardManager initialized (cross-platform)");
    }

    /// <summary>
    /// Gets the Avalonia clipboard from the current application's main window.
    /// In Avalonia 11+, clipboard is accessed via TopLevel (the top-level window).
    /// </summary>
    private IClipboard? GetClipboard()
    {
        // Access clipboard through the application's main window
        // TopLevel.GetTopLevel() requires a visual element, so we use the main window
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                return TopLevel.GetTopLevel(mainWindow)?.Clipboard;
            }
        }
        return null;
    }

    /// <inheritdoc />
    public void StartMonitoring()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AvaloniaClipboardManager));

        lock (_lockObject)
        {
            if (_isMonitoring)
            {
                _logger.Warning("Clipboard monitoring already started");
                return;
            }

            // Start background polling (same approach as WPF version)
            Task.Run(async () => await MonitorClipboardAsync());

            _isMonitoring = true;
            _logger.Information("Clipboard monitoring started");
        }
    }

    /// <inheritdoc />
    public void StopMonitoring()
    {
        lock (_lockObject)
        {
            if (!_isMonitoring) return;
            _isMonitoring = false;
            _logger.Information("Clipboard monitoring stopped");
        }
    }

    /// <summary>
    /// Background task that polls clipboard for changes.
    /// Uses Avalonia's async clipboard API instead of WPF's synchronous Clipboard class.
    /// </summary>
    private async Task MonitorClipboardAsync()
    {
        string? lastText = null;

        while (_isMonitoring && !_disposed)
        {
            try
            {
                await Task.Delay(500); // Poll every 500ms

                if (!_isSyncEnabled || _suppressNextChange)
                {
                    _suppressNextChange = false;
                    continue;
                }

                // Read clipboard on UI thread (Avalonia clipboard requires it)
                string? currentText = null;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        var clipboard = GetClipboard();
                        if (clipboard != null)
                        {
                            currentText = await clipboard.GetTextAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error accessing clipboard on UI thread");
                    }
                });

                // Check if text changed
                if (currentText != lastText && !string.IsNullOrEmpty(currentText))
                {
                    lastText = currentText;
                    OnClipboardContentChanged(currentText);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error monitoring clipboard");
            }
        }
    }

    /// <summary>
    /// Called when clipboard content changes.
    /// </summary>
    private void OnClipboardContentChanged(string textContent)
    {
        try
        {
            Interlocked.Increment(ref _totalChangesDetected);

            var sizeBytes = Encoding.UTF8.GetByteCount(textContent);
            Interlocked.Add(ref _totalBytesSent, sizeBytes);
            Interlocked.Increment(ref _totalMessagesSent);
            _lastSyncTime = DateTime.UtcNow;

            _logger.Information("Clipboard changed: Size={Size} bytes", sizeBytes);

            var eventArgs = new ClipboardChangedEventArgs
            {
                Text = textContent,
                Timestamp = DateTime.UtcNow
            };

            ClipboardChanged?.Invoke(this, eventArgs);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing clipboard change");
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AvaloniaClipboardManager));

        string? result = null;

        // Must access clipboard on UI thread in Avalonia
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var clipboard = GetClipboard();
                if (clipboard != null)
                {
                    result = await clipboard.GetTextAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting clipboard text");
            }
        });

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_disposed)
            throw new ObjectDisposedException(nameof(AvaloniaClipboardManager));

        bool success = false;

        // Must access clipboard on UI thread in Avalonia
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                // Suppress next change event to prevent echo
                _suppressNextChange = true;

                var clipboard = GetClipboard();
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(text);

                    var sizeBytes = Encoding.UTF8.GetByteCount(text);
                    Interlocked.Add(ref _totalBytesReceived, sizeBytes);
                    Interlocked.Increment(ref _totalMessagesReceived);
                    _lastSyncTime = DateTime.UtcNow;

                    _logger.Information("Set clipboard text: {Size} bytes", sizeBytes);
                    success = true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error setting clipboard text");
            }
        });

        return success;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        StopMonitoring();

        _logger.Information("AvaloniaClipboardManager disposed. Stats: Sent={Sent}, Received={Received}",
            Interlocked.Read(ref _totalMessagesSent), Interlocked.Read(ref _totalMessagesReceived));

        _disposed = true;
    }
}
