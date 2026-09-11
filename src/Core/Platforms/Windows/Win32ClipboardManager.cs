using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Core.Platforms.Windows;

/// <summary>
/// Windows implementation of clipboard manager using Win32 API.
/// Provides clipboard monitoring and synchronization without WPF dependencies.
/// </summary>
/// <remarks>
/// This implementation uses raw Win32 clipboard APIs:
/// - OpenClipboard/CloseClipboard: Access clipboard
/// - GetClipboardData: Retrieve data
/// - SetClipboardData: Store data
/// - GetClipboardSequenceNumber: Detect changes
///
/// This is a platform-specific (non-UI) implementation suitable for Core library.
/// For WPF applications, see WindowsClipboardManager in Desktop project.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class Win32ClipboardManager : IClipboardManager
{
    private bool _isMonitoring;
    private bool _disposed;
    private Thread? _monitorThread;
    private CancellationTokenSource? _monitorCts;
    private uint _lastSequenceNumber;
    private string? _lastClipboardText;

    // Win32 API constants
    private const uint CF_TEXT = 1;
    private const uint CF_UNICODETEXT = 13;

    // Win32 API methods
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    private const uint GMEM_MOVEABLE = 0x0002;

    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public bool IsMonitoring => _isMonitoring;

    public void StartMonitoring()
    {
        if (_isMonitoring)
            return;

        try
        {
            // Get initial sequence number
            _lastSequenceNumber = GetClipboardSequenceNumber();

            // Start monitoring thread
            _monitorCts = new CancellationTokenSource();
            _monitorThread = new Thread(MonitorClipboardLoop)
            {
                IsBackground = true,
                Name = "Win32ClipboardMonitor"
            };
            _monitorThread.Start();

            _isMonitoring = true;
            Log.Information("Clipboard monitoring started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start clipboard monitoring");
        }
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring)
            return;

        _monitorCts?.Cancel();
        _monitorThread?.Join(TimeSpan.FromSeconds(2));

        _isMonitoring = false;
        Log.Information("Clipboard monitoring stopped");
    }

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    return null;
                }

                try
                {
                    // Try Unicode text first
                    IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                    if (hData == IntPtr.Zero)
                    {
                        // Fall back to ANSI text
                        hData = GetClipboardData(CF_TEXT);
                        if (hData == IntPtr.Zero)
                        {
                            return null;
                        }
                    }

                    IntPtr pData = GlobalLock(hData);
                    if (pData == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        // Read Unicode string
                        string text = Marshal.PtrToStringUni(pData) ?? string.Empty;
                        return text;
                    }
                    finally
                    {
                        GlobalUnlock(hData);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GetTextAsync failed");
                return null;
            }
        }, cancellationToken);
    }

    public async Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        return await Task.Run(() =>
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    return false;
                }

                try
                {
                    if (!EmptyClipboard())
                    {
                        return false;
                    }

                    // Allocate global memory for text
                    byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);

                    if (hGlobal == IntPtr.Zero)
                    {
                        return false;
                    }

                    try
                    {
                        IntPtr pGlobal = GlobalLock(hGlobal);
                        if (pGlobal == IntPtr.Zero)
                        {
                            GlobalFree(hGlobal);
                            return false;
                        }

                        try
                        {
                            Marshal.Copy(bytes, 0, pGlobal, bytes.Length);
                        }
                        finally
                        {
                            GlobalUnlock(hGlobal);
                        }

                        // Set clipboard data
                        IntPtr hResult = SetClipboardData(CF_UNICODETEXT, hGlobal);
                        if (hResult == IntPtr.Zero)
                        {
                            GlobalFree(hGlobal);
                            return false;
                        }

                        // Memory is now owned by clipboard - don't free it
                        _lastClipboardText = text;
                        Log.Debug("Clipboard text set successfully");
                        return true;
                    }
                    catch
                    {
                        GlobalFree(hGlobal);
                        throw;
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SetTextAsync failed");
                return false;
            }
        }, cancellationToken);
    }

    private void MonitorClipboardLoop()
    {
        try
        {
            while (_monitorCts?.Token.IsCancellationRequested == false)
            {
                // Check if clipboard sequence number changed
                uint currentSequence = GetClipboardSequenceNumber();

                if (currentSequence != _lastSequenceNumber)
                {
                    _lastSequenceNumber = currentSequence;

                    // Clipboard changed, read new content
                    Task.Run(async () =>
                    {
                        string? newText = await GetTextAsync();
                        if (newText != null && newText != _lastClipboardText)
                        {
                            _lastClipboardText = newText;

                            var eventArgs = new ClipboardChangedEventArgs
                            {
                                Text = newText,
                                Timestamp = DateTime.UtcNow
                            };

                            ClipboardChanged?.Invoke(this, eventArgs);
                        }
                    });
                }

                Thread.Sleep(200); // Poll every 200ms
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Clipboard monitor loop error");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        StopMonitoring();

        _monitorCts?.Dispose();
        _disposed = true;

        Log.Information("Clipboard manager disposed");
    }
}
