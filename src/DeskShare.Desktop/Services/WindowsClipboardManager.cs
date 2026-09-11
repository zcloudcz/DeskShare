using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Desktop.Services;

/// <summary>
/// Windows implementation of clipboard manager using WPF Clipboard API.
/// Monitors clipboard changes and provides synchronization with remote peer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardManager : IClipboardManager
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

    /// <summary>
    /// Initializes a new instance of the WindowsClipboardManager class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public WindowsClipboardManager(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.Information("WindowsClipboardManager initialized");
    }

    /// <inheritdoc />
    public void StartMonitoring()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsClipboardManager));
        }

        lock (_lockObject)
        {
            if (_isMonitoring)
            {
                _logger.Warning("Clipboard monitoring already started");
                return;
            }

            // Start a background thread to monitor clipboard using polling
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
            if (!_isMonitoring)
            {
                return;
            }

            _isMonitoring = false;
            _logger.Information("Clipboard monitoring stopped");
        }
    }

    /// <summary>
    /// Background task that polls clipboard for changes.
    /// </summary>
    private async Task MonitorClipboardAsync()
    {
        string? lastText = null;
        byte[]? lastImageHash = null;

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

                // Access clipboard on STA thread
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // Check if clipboard has text
                        if (Clipboard.ContainsText())
                        {
                            var currentText = Clipboard.GetText();

                            if (currentText != lastText && !string.IsNullOrEmpty(currentText))
                            {
                                lastText = currentText;
                                lastImageHash = null;
                                OnClipboardContentChanged(ClipboardContentType.Text, currentText, null);
                            }
                        }
                        // Check if clipboard has image
                        else if (Clipboard.ContainsImage())
                        {
                            var image = Clipboard.GetImage();

                            if (image != null)
                            {
                                // Convert to hash to detect if image changed
                                var imageHash = ComputeImageHash(image);

                                if (!CompareByteArrays(imageHash, lastImageHash))
                                {
                                    lastImageHash = imageHash;
                                    lastText = null;
                                    OnClipboardContentChanged(ClipboardContentType.Image, null, image);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error accessing clipboard in dispatcher");
                    }
                });
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
    private void OnClipboardContentChanged(ClipboardContentType contentType, string? textContent, BitmapSource? imageSource)
    {
        try
        {
            Interlocked.Increment(ref _totalChangesDetected);

            // For now, only support text clipboard sync in the new interface
            if (contentType == ClipboardContentType.Text && textContent != null)
            {
                var sizeBytes = Encoding.UTF8.GetByteCount(textContent);
                Interlocked.Add(ref _totalBytesSent, sizeBytes);
                Interlocked.Increment(ref _totalMessagesSent);
                _lastSyncTime = DateTime.UtcNow;

                _logger.Information("Clipboard changed: Type={Type}, Size={Size} bytes", contentType, sizeBytes);

                var eventArgs = new ClipboardChangedEventArgs
                {
                    Text = textContent,
                    Timestamp = DateTime.UtcNow
                };

                ClipboardChanged?.Invoke(this, eventArgs);
            }
            // TODO: Image support to be added later when interface is extended
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing clipboard change");
        }
    }

    /// <inheritdoc />
    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsClipboardManager));
        }

        var tcs = new TaskCompletionSource<string?>();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    tcs.SetResult(text);
                }
                else
                {
                    tcs.SetResult(null);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting clipboard text");
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <inheritdoc />
    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsClipboardManager));
        }

        var tcs = new TaskCompletionSource<bool>();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                // Suppress next change event to prevent echo
                _suppressNextChange = true;

                Clipboard.SetText(text);

                var sizeBytes = Encoding.UTF8.GetByteCount(text);
                Interlocked.Add(ref _totalBytesReceived, sizeBytes);
                Interlocked.Increment(ref _totalMessagesReceived);
                _lastSyncTime = DateTime.UtcNow;

                _logger.Information("Set clipboard text: {Size} bytes", sizeBytes);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error setting clipboard text");
                tcs.SetResult(false);
            }
        });

        return tcs.Task;
    }

    // Statistics removed from interface - kept internally for logging
    private InternalClipboardStatistics GetStatisticsInternal()
    {
        return new InternalClipboardStatistics
        {
            TotalChangesDetected = Interlocked.Read(ref _totalChangesDetected),
            TotalMessagesSent = Interlocked.Read(ref _totalMessagesSent),
            TotalMessagesReceived = Interlocked.Read(ref _totalMessagesReceived),
            TotalBytesSent = Interlocked.Read(ref _totalBytesSent),
            TotalBytesReceived = Interlocked.Read(ref _totalBytesReceived),
            LastSyncTime = _lastSyncTime
        };
    }

    private class InternalClipboardStatistics
    {
        public long TotalChangesDetected { get; set; }
        public long TotalMessagesSent { get; set; }
        public long TotalMessagesReceived { get; set; }
        public long TotalBytesSent { get; set; }
        public long TotalBytesReceived { get; set; }
        public DateTime? LastSyncTime { get; set; }
    }

    #region Helper Methods

    /// <summary>
    /// Converts BitmapSource to PNG base64 string.
    /// </summary>
    private string? ConvertBitmapSourceToBase64(BitmapSource bitmapSource)
    {
        try
        {
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error converting bitmap to base64");
            return null;
        }
    }

    /// <summary>
    /// Converts base64 string to BitmapSource.
    /// </summary>
    private BitmapSource? ConvertBase64ToBitmapSource(string base64)
    {
        try
        {
            var imageBytes = Convert.FromBase64String(base64);
            using var stream = new MemoryStream(imageBytes);
            var decoder = new PngBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error converting base64 to bitmap");
            return null;
        }
    }

    /// <summary>
    /// Computes a simple hash of image for change detection.
    /// </summary>
    private byte[] ComputeImageHash(BitmapSource image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);

        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(stream.ToArray());
    }

    /// <summary>
    /// Compares two byte arrays.
    /// </summary>
    private bool CompareByteArrays(byte[]? a, byte[]? b)
    {
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        return a.SequenceEqual(b);
    }

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopMonitoring();

        var stats = GetStatisticsInternal();
        _logger.Information("WindowsClipboardManager disposed. Stats: Sent={Sent}, Received={Received}",
            stats.TotalMessagesSent, stats.TotalMessagesReceived);

        _disposed = true;
    }
}
