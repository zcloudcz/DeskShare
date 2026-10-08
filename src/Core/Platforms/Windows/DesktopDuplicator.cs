using System.Buffers;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskShare.Core.Platforms.Windows;

/// <summary>
/// Implementation of screen capture using DXGI Desktop Duplication API.
/// Provides efficient GPU-based screen capture with minimal CPU overhead.
/// </summary>
/// <remarks>
/// Desktop Duplication API only provides frames when screen content changes.
/// This is more efficient than polling but requires handling timeout scenarios.
/// Uses staging texture for CPU-accessible copy of the screen content.
/// </remarks>
public sealed class DesktopDuplicator : IScreenCapturer
{
    private const int AcquireTimeoutMs = 100;
    private const Format TextureFormat = Format.B8G8R8A8_UNorm;

    private readonly int _adapterIndex;
    private readonly int _outputIndex;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private bool _initialized;
    private bool _disposed;
    private bool _frameAcquired;
    private long _failedAcquires;
    private DateTime _lastReinitialize = DateTime.MinValue;

    /// <inheritdoc/>
    public int Width { get; private set; }

    /// <inheritdoc/>
    public int Height { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopDuplicator"/> class.
    /// </summary>
    /// <param name="adapterIndex">Index of the graphics adapter to use (0 for primary).</param>
    /// <param name="outputIndex">Index of the output (monitor) to capture (0 for primary).</param>
    public DesktopDuplicator(int adapterIndex = 0, int outputIndex = 0)
    {
        if (adapterIndex < 0)
            throw new ArgumentException("Adapter index must be non-negative.", nameof(adapterIndex));

        if (outputIndex < 0)
            throw new ArgumentException("Output index must be non-negative.", nameof(outputIndex));

        _adapterIndex = adapterIndex;
        _outputIndex = outputIndex;
    }

    /// <inheritdoc/>
    public bool Initialize()
    {
        if (_initialized)
            throw new InvalidOperationException("Capturer is already initialized.");

        try
        {
            // Create DXGI factory to enumerate adapters and outputs
            Log.Information("Creating DXGI factory");
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            // Get the specified adapter
            Log.Information("Enumerating adapter {AdapterIndex}", _adapterIndex);
            var enumResult = factory.EnumAdapters((uint)_adapterIndex, out var adapter);
            if (enumResult.Failure || adapter == null)
            {
                Log.Error("Failed to get adapter {AdapterIndex}, result: {Result}", _adapterIndex, enumResult);
                return false;
            }
            Log.Information("Adapter found: {AdapterName}", adapter.Description.Description);

            // Create D3D11 device for the adapter
            Log.Information("Creating D3D11 device");
            var deviceResult = D3D11.D3D11CreateDevice(
                adapter,
                Vortice.Direct3D.DriverType.Unknown,
                DeviceCreationFlags.None,
                null,
                out _device);

            if (deviceResult.Failure || _device == null)
            {
                Log.Error("Failed to create D3D11 device, result: {Result}", deviceResult);
                adapter.Dispose();
                return false;
            }
            Log.Information("D3D11 device created");

            _deviceContext = _device.ImmediateContext;

            // Get the specified output (monitor)
            Log.Information("Enumerating output {OutputIndex}", _outputIndex);
            var outputResult = adapter.EnumOutputs((uint)_outputIndex, out var output);
            adapter.Dispose();

            if (outputResult.Failure || output == null)
            {
                Log.Error("Failed to get output {OutputIndex}, result: {Result}", _outputIndex, outputResult);
                CleanupResources();
                return false;
            }

            // Get output description for dimensions
            var outputDesc = output.Description;
            Log.Information("Output found: {DeviceName}", outputDesc.DeviceName);
            Width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
            Height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
            Log.Information("Resolution: {Width}x{Height}", Width, Height);

            // Ensure dimensions are even (required for I420 format)
            Width = (Width / 2) * 2;
            Height = (Height / 2) * 2;

            // Query for IDXGIOutput1 interface needed for duplication
            Log.Information("Querying IDXGIOutput1 interface");
            var output1 = output.QueryInterface<IDXGIOutput1>();
            output.Dispose();

            if (output1 == null)
            {
                Log.Error("Failed to query IDXGIOutput1");
                CleanupResources();
                return false;
            }

            // Create desktop duplication
            Log.Information("Creating desktop duplication");
            _duplication = output1.DuplicateOutput(_device);
            output1.Dispose();

            if (_duplication == null)
            {
                Log.Error("Failed to create desktop duplication");
                CleanupResources();
                return false;
            }
            Log.Information("Desktop duplication created");

            // Create staging texture for CPU access
            Log.Information("Creating staging texture");
            CreateStagingTexture();

            _initialized = true;
            Log.Information("Initialization complete");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception during initialization");
            CleanupResources();
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TryAcquireFrame(out Frame? frame)
    {
        frame = null;

        if (!_initialized)
            throw new InvalidOperationException("Capturer is not initialized.");

        if (_frameAcquired)
            throw new InvalidOperationException("Previous frame has not been released.");

        // After a failed Reinitialize() there is no duplication object yet; try again (throttled inside).
        if (_duplication == null)
        {
            Reinitialize();
            if (_duplication == null)
                return false;
        }

        try
        {
            // Try to acquire next frame with timeout
            var result = _duplication.AcquireNextFrame(
                AcquireTimeoutMs,
                out var frameInfo,
                out var desktopResource);

            // No new frame available (no screen changes) — anything other than a timeout is a real problem
            // (e.g. DXGI_ERROR_ACCESS_LOST after a display mode change) and must not be swallowed silently.
            if (result.Failure)
            {
                if (result.Code == Vortice.DXGI.ResultCode.AccessLost.Code)
                {
                    // The duplication interface is dead for good (desktop switch, mode change, driver reset);
                    // Windows requires a new IDXGIOutputDuplication. Without this the capture silently stops forever.
                    Reinitialize();
                }
                else if (result.Code != Vortice.DXGI.ResultCode.WaitTimeout.Code && _failedAcquires++ % 100 == 0)
                {
                    Log.Warning("AcquireNextFrame failed: {Result} (occurrence {Count})", result, _failedAcquires);
                }
                return false;
            }

            _frameAcquired = true;

            // Query for ID3D11Texture2D interface
            using var acquiredTexture = desktopResource?.QueryInterface<ID3D11Texture2D>();
            desktopResource?.Dispose();

            if (acquiredTexture == null)
            {
                ReleaseFrame();
                return false;
            }

            // Copy acquired texture to staging texture for CPU access
            _deviceContext!.CopyResource(_stagingTexture!, acquiredTexture);

            // Map staging texture to CPU memory
            var mappedResource = _deviceContext.Map(_stagingTexture!, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                // Calculate data size
                var dataSize = (int)mappedResource.RowPitch * Height;
                // Rent instead of allocate: a fresh ~8 MB array per frame at 30 fps lands on the LOH
                // and causes gen2 GC stalls. Frame.Dispose (called by the pipeline) returns it.
                var data = ArrayPool<byte>.Shared.Rent(dataSize);

                // Copy data from mapped memory
                unsafe
                {
                    var sourcePtr = (byte*)mappedResource.DataPointer.ToPointer();
                    fixed (byte* destPtr = data)
                    {
                        Buffer.MemoryCopy(sourcePtr, destPtr, dataSize, dataSize);
                    }
                }

                // Create Frame object with captured data
                frame = Frame.FromPooled(
                    Width,
                    Height,
                    data,
                    (int)mappedResource.RowPitch,
                    DateTime.UtcNow);

                return true;
            }
            finally
            {
                _deviceContext.Unmap(_stagingTexture!, 0u);
            }
        }
        catch (Exception ex)
        {
            if (_failedAcquires++ % 100 == 0)
            {
                Log.Warning(ex, "Frame acquisition threw (occurrence {Count})", _failedAcquires);
            }
            if (_frameAcquired)
            {
                ReleaseFrame();
            }
            return false;
        }
    }

    /// <summary>
    /// Recreates the device and duplication after DXGI_ERROR_ACCESS_LOST. Throttled to once per second:
    /// while the secure desktop (UAC, lock screen) is up, every attempt fails again.
    /// </summary>
    private void Reinitialize()
    {
        var now = DateTime.UtcNow;
        if (now - _lastReinitialize < TimeSpan.FromSeconds(1))
            return;
        _lastReinitialize = now;

        Log.Warning("Desktop duplication access lost, reinitializing capture");
        var (oldWidth, oldHeight) = (Width, Height);
        CleanupResources();
        _initialized = false;
        _frameAcquired = false;

        if (!Initialize())
        {
            Log.Error("Reinitialization failed; will retry on next frame");
            _initialized = true; // keep the pipeline alive so it retries instead of throwing
            return;
        }

        if (Width != oldWidth || Height != oldHeight)
        {
            // ponytail: the pipeline/encoder are sized at start; a resolution change needs a full restart.
            Log.Warning("Display resolution changed {OldW}x{OldH} → {W}x{H}; restart sharing to apply", oldWidth, oldHeight, Width, Height);
        }
    }

    /// <inheritdoc/>
    public void ReleaseFrame()
    {
        if (!_frameAcquired)
            return;

        try
        {
            _duplication?.ReleaseFrame();
        }
        catch
        {
            // Ignore errors during frame release
        }
        finally
        {
            _frameAcquired = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_frameAcquired)
        {
            ReleaseFrame();
        }

        CleanupResources();
        _disposed = true;
    }

    /// <summary>
    /// Creates staging texture for CPU-accessible copy of screen content.
    /// </summary>
    private void CreateStagingTexture()
    {
        var textureDesc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = TextureFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        _stagingTexture = _device!.CreateTexture2D(textureDesc);
    }

    /// <summary>
    /// Cleans up all DirectX resources.
    /// </summary>
    private void CleanupResources()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _duplication?.Dispose();
        _duplication = null;

        _deviceContext?.Dispose();
        _deviceContext = null;

        _device?.Dispose();
        _device = null;

        _initialized = false;
    }

    /// <summary>
    /// Information about a display output (monitor).
    /// </summary>
    /// <param name="AdapterIndex">Index of the graphics adapter.</param>
    /// <param name="OutputIndex">Index of the output on the adapter.</param>
    /// <param name="AdapterName">Name of the graphics adapter.</param>
    /// <param name="OutputName">Name of the output (monitor).</param>
    /// <param name="Width">Width of the display in pixels.</param>
    /// <param name="Height">Height of the display in pixels.</param>
    public record MonitorInfo(int AdapterIndex, int OutputIndex, string AdapterName, string OutputName, int Width, int Height);

    /// <summary>
    /// Enumerates all available monitors (display outputs) in the system.
    /// </summary>
    /// <returns>List of monitor information for all available displays.</returns>
    public static List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            // Enumerate all adapters (GPUs)
            uint adapterIndex = 0;
            while (true)
            {
                var adapterResult = factory.EnumAdapters(adapterIndex, out var adapter);
                if (adapterResult.Failure || adapter == null)
                    break;

                using (adapter)
                {
                    var adapterDesc = adapter.Description;
                    var adapterName = adapterDesc.Description;

                    // Enumerate all outputs (monitors) on this adapter
                    uint outputIndex = 0;
                    while (true)
                    {
                        var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                        if (outputResult.Failure || output == null)
                            break;

                        using (output)
                        {
                            var outputDesc = output.Description;
                            var bounds = outputDesc.DesktopCoordinates;
                            var width = bounds.Right - bounds.Left;
                            var height = bounds.Bottom - bounds.Top;

                            monitors.Add(new MonitorInfo(
                                (int)adapterIndex,
                                (int)outputIndex,
                                adapterName,
                                outputDesc.DeviceName,
                                width,
                                height
                            ));
                        }

                        outputIndex++;
                    }
                }

                adapterIndex++;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error enumerating monitors");
        }

        return monitors;
    }
}
