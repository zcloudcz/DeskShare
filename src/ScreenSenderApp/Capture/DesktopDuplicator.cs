using DeskShare.Common.Interfaces;
using DeskShare.Common.Models;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskShare.ScreenSenderApp.Capture;

/// <summary>
/// Implementation of screen capture using DXGI Desktop Duplication API.
/// Provides efficient GPU-based screen capture with minimal CPU overhead.
/// </summary>
/// <remarks>
/// Desktop Duplication API only provides frames when screen content changes.
/// This is more efficient than polling but requires handling timeout scenarios.
/// Uses staging texture for CPU-accessible copy of the screen content.
/// </remarks>
public sealed class DesktopDuplicator : ICapturer
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
            Console.WriteLine($"[DesktopDuplicator] Creating DXGI factory...");
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            // Get the specified adapter
            Console.WriteLine($"[DesktopDuplicator] Enumerating adapter {_adapterIndex}...");
            var enumResult = factory.EnumAdapters((uint)_adapterIndex, out var adapter);
            if (enumResult.Failure || adapter == null)
            {
                Console.WriteLine($"[DesktopDuplicator] ✗ Failed to get adapter {_adapterIndex}. Result: {enumResult}");
                return false;
            }
            Console.WriteLine($"[DesktopDuplicator] ✓ Adapter found: {adapter.Description.Description}");

            // Create D3D11 device for the adapter
            Console.WriteLine($"[DesktopDuplicator] Creating D3D11 device...");
            var deviceResult = D3D11.D3D11CreateDevice(
                adapter,
                Vortice.Direct3D.DriverType.Unknown,
                DeviceCreationFlags.None,
                null,
                out _device);

            if (deviceResult.Failure || _device == null)
            {
                Console.WriteLine($"[DesktopDuplicator] ✗ Failed to create D3D11 device. Result: {deviceResult}");
                adapter.Dispose();
                return false;
            }
            Console.WriteLine($"[DesktopDuplicator] ✓ D3D11 device created");

            _deviceContext = _device.ImmediateContext;

            // Get the specified output (monitor)
            Console.WriteLine($"[DesktopDuplicator] Enumerating output {_outputIndex}...");
            var outputResult = adapter.EnumOutputs((uint)_outputIndex, out var output);
            adapter.Dispose();

            if (outputResult.Failure || output == null)
            {
                Console.WriteLine($"[DesktopDuplicator] ✗ Failed to get output {_outputIndex}. Result: {outputResult}");
                CleanupResources();
                return false;
            }

            // Get output description for dimensions
            var outputDesc = output.Description;
            Console.WriteLine($"[DesktopDuplicator] ✓ Output found: {outputDesc.DeviceName}");
            Width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
            Height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
            Console.WriteLine($"[DesktopDuplicator] Resolution: {Width}x{Height}");

            // Ensure dimensions are even (required for I420 format)
            Width = (Width / 2) * 2;
            Height = (Height / 2) * 2;

            // Query for IDXGIOutput1 interface needed for duplication
            Console.WriteLine($"[DesktopDuplicator] Querying IDXGIOutput1 interface...");
            var output1 = output.QueryInterface<IDXGIOutput1>();
            output.Dispose();

            if (output1 == null)
            {
                Console.WriteLine($"[DesktopDuplicator] ✗ Failed to query IDXGIOutput1");
                CleanupResources();
                return false;
            }

            // Create desktop duplication
            Console.WriteLine($"[DesktopDuplicator] Creating desktop duplication...");
            _duplication = output1.DuplicateOutput(_device);
            output1.Dispose();

            if (_duplication == null)
            {
                Console.WriteLine($"[DesktopDuplicator] ✗ Failed to create desktop duplication");
                CleanupResources();
                return false;
            }
            Console.WriteLine($"[DesktopDuplicator] ✓ Desktop duplication created");

            // Create staging texture for CPU access
            Console.WriteLine($"[DesktopDuplicator] Creating staging texture...");
            CreateStagingTexture();

            _initialized = true;
            Console.WriteLine($"[DesktopDuplicator] ✓ Initialization complete!");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopDuplicator] ✗ Exception during initialization: {ex.Message}");
            Console.WriteLine($"[DesktopDuplicator] Stack trace: {ex.StackTrace}");
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

        try
        {
            // Try to acquire next frame with timeout
            var result = _duplication!.AcquireNextFrame(
                AcquireTimeoutMs,
                out var frameInfo,
                out var desktopResource);

            // No new frame available (no screen changes)
            if (result.Failure)
            {
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
                var data = new byte[dataSize];

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
                frame = new Frame(
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
        catch
        {
            if (_frameAcquired)
            {
                ReleaseFrame();
            }
            return false;
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
            Console.WriteLine($"[DesktopDuplicator] Error enumerating monitors: {ex.Message}");
        }

        return monitors;
    }
}
