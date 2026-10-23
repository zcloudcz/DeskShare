using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>An H.264 decoder the sink can swap for another one at runtime (and tests can replace with a fake).</summary>
internal interface IH264FrameDecoder : IDisposable
{
    /// <summary>Feeds one Annex B access unit; calls <paramref name="onFrame"/> with (bgra, width, height) per picture. Throws when the decoder is broken.</summary>
    int Decode(byte[] annexB, Action<byte[], int, int> onFrame);
}


/// <summary>
/// H.264 (Annex B) to BGRA decoder on top of the Windows built-in Media Foundation decoder MFT, so the viewer does
/// not need FFmpeg for H.264. PROTOTYPE: opt-in only (see <see cref="MediaFoundationVideoSink"/>).
///
/// COM lifetime: every Vortice object here (IMFTransform, IMFSample, IMFMediaBuffer, IMFMediaType ...) is a COM
/// wrapper that holds a reference until Dispose(). Forgetting a Dispose leaks native memory (a decoded 1080p sample is
/// 3 MB), so every object created per frame is wrapped in "using".
///
/// Not thread safe: call <see cref="Decode"/> from one thread at a time (the sink serialises with a lock).
/// </summary>
internal sealed class MediaFoundationH264Decoder : IH264FrameDecoder
{
    // CLSID_CMSH264DecoderMFT: the software H.264 decoder that ships with Windows (mfh264dec.dll).
    private static readonly Guid ClsidMsH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");

    // MF_LOW_LATENCY: tells the decoder to output a frame as soon as it is decoded instead of buffering for reordering.
    // This is the MFT-attribute twin of CODECAPI_AVLowLatencyMode (which needs the ICodecAPI interface that Vortice
    // does not wrap). A remote desktop has no B-frames, so waiting would only add delay.
    private static readonly Guid MfLowLatency = new("9C27891A-ED7A-40e1-88E8-B22727A024EE");


    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfENotAccepting = unchecked((int)0xC00D36B5);

    // Output type renegotiations allowed within one Decode call. A real stream needs one (first SPS) or two (a resolution
    // change in the same access unit); more means the decoder keeps asking for a new format and would spin forever.
    private const int MaxStreamChangesPerDecode = 4;
    private const int MaxOutputTypesToTry = 64;

    // Media Foundation must be started once per process before any MF call; it is cheap to leave running.
    private static readonly Lazy<bool> Started = new(() =>
    {
        MediaFactory.MFStartup();
        return true;
    });

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr obj);

    private IMFTransform? _mft;
    private ID3D11Device? _device;                  // only with hardware decoding
    private IMFDXGIDeviceManager? _deviceManager;   // hands that device to the decoder
    private bool _outputTypeSet;
    private int _width, _height;       // visible size
    private int _bufferHeight;         // rows in the coded buffer (1088 for 1080p); the UV plane starts after them
    private int _stride;
    private long _time;                // running timestamp in 100 ns units
    private bool _providesSamples;
    private int _outputSampleSize;

    /// <summary>
    /// True when the decoder MFT can be created. False on Windows N / KN editions without the Media Feature Pack
    /// (mfplat.dll or the decoder is missing) and on every non-Windows OS. Callers then keep using FFmpeg.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var probe = new MediaFoundationH264Decoder();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or SharpGenException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <param name="hardware">Decode on the GPU (DXVA through Direct3D 11). The decoder then outputs GPU textures, which MF copies to system memory when we lock the buffer.</param>
    public MediaFoundationH264Decoder(bool hardware = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Media Foundation is Windows only.");
        _ = Started.Value;

        var hr = CoCreateInstance(ClsidMsH264Decoder, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, typeof(IMFTransform).GUID, out var ptr);
        if (hr < 0 || ptr == IntPtr.Zero)
            throw new InvalidOperationException($"H.264 decoder MFT not available (HRESULT 0x{hr:X8}).");
        _mft = new IMFTransform(ptr);
        try
        {
            Configure(hardware);
        }
        catch
        {
            // SharpGen does not release COM objects on finalization, so a failed setup must free the MFT, the device
            // manager and the D3D device here; otherwise every failed GPU attempt (one per connection) leaks them.
            Dispose(); // releases all three (each only if it was created)
            throw;
        }
    }

    /// <summary>
    /// Test seam: kills the running GPU decoder the way a lost device does, without touching the driver or the system.
    /// The DXGI device manager is pointed at a different D3D11 device, so the device the decoder works with is gone for it.
    /// On its own that makes the MFT go silent (it keeps accepting input and never outputs, which the sink cannot tell
    /// from "waiting for more input"). A removed device normally makes calls fail, so the output type is dropped as well:
    /// the next frame then fails with a real Media Foundation error (MF_E_TRANSFORM_TYPE_NOT_SET) that the sink must handle.
    /// Pass <paramref name="failLoudly"/> = false for the silent variant only (the sink's stall watchdog has to catch it).
    /// </summary>
    internal void SimulateDeviceLoss(bool failLoudly = true)
    {
        if (_mft == null || _deviceManager == null) throw new InvalidOperationException("Only a running GPU decoder has a device to lose.");
        D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport, null, out var replacement).CheckError();
        using (replacement) // the manager keeps its own reference
            _deviceManager.ResetDevice(replacement);
        if (failLoudly) _mft.SetOutputType(0, null, 0);
    }

    private void Configure(bool hardware)
    {
        using (var attrs = _mft!.Attributes)
        {
            attrs.Set(MfLowLatency, 1u);
            if (hardware)
            {
                if (attrs.GetUInt32(TransformAttributeKeys.D3D11Aware) == 0)
                    throw new InvalidOperationException("The H.264 decoder MFT is not Direct3D 11 aware.");
                D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware,
                    DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport, null, out _device).CheckError();
                // MF and our code touch the device from different threads; without this the driver may corrupt state.
                using (var multithread = _device!.QueryInterface<ID3D11Multithread>())
                    multithread.SetMultithreadProtected(true);
                _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                _deviceManager!.ResetDevice(_device);
                _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)_deviceManager.NativePointer);
            }
        }

        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        _mft.SetInputType(0, input, 0);

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>
    /// Feeds one access unit (Annex B, with start codes; SPS/PPS in front of keyframes) and calls
    /// <paramref name="onFrame"/> with (bgra, width, height) for every picture that comes out. Returns the number of pictures.
    /// A hardware encoder with no frame delay gives exactly one picture per access unit once the stream has started.
    /// </summary>
    public int Decode(byte[] annexB, Action<byte[], int, int> onFrame)
    {
        var mft = _mft ?? throw new ObjectDisposedException(nameof(MediaFoundationH264Decoder));

        using (var sample = MediaFactory.MFCreateSample())
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(annexB.Length))
        {
            buffer.Lock(out var dst, out _, out _);
            Marshal.Copy(annexB, 0, dst, annexB.Length);
            buffer.Unlock();
            buffer.CurrentLength = annexB.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = _time;
            sample.SampleDuration = 333_333;
            _time += 333_333;

            try
            {
                mft.ProcessInput(0, sample, 0);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MfENotAccepting)
            {
                // The decoder refuses input while it holds undelivered output: drain, then offer the data again.
                var drained = Drain(onFrame);
                mft.ProcessInput(0, sample, 0);
                return drained + Drain(onFrame);
            }
        }
        return Drain(onFrame);
    }

    private int Drain(Action<byte[], int, int> onFrame)
    {
        var mft = _mft!;
        var frames = 0;
        var streamChanges = 0;
        var emptyOutputs = 0;
        while (true)
        {
            if (!_outputTypeSet && !TryNegotiateOutputType())
                return frames; // no SPS seen yet: the decoder cannot say what it will produce

            var output = new OutputDataBuffer { StreamID = 0 };
            IMFSample? own = null;
            if (!_providesSamples)
            {
                // Software decoder: we supply the sample the picture is written into.
                own = MediaFactory.MFCreateSample();
                using var buf = MediaFactory.MFCreateMemoryBuffer(_outputSampleSize);
                own.AddBuffer(buf);
                output.Sample = own;
            }

            try
            {
                var rc = mft.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
                if (rc.Code == MfETransformNeedMoreInput)
                    return frames;
                if (rc.Code == MfETransformStreamChange)
                {
                    // New SPS (first frame, or the host changed resolution): ask for the new output format.
                    // Bounded: throwing lets the sink count a failure and step down instead of hanging the receive thread.
                    if (++streamChanges > MaxStreamChangesPerDecode)
                        throw new InvalidOperationException("The H.264 decoder keeps requesting a new output format.");
                    _outputTypeSet = false;
                    continue;
                }
                rc.CheckError();

                // Our own sample comes back in output.Sample too: it is released once, in the finally below,
                // so only a sample the decoder allocated itself is released here (a double Release crashes the process).
                var result = output.Sample;
                if (result == null)
                {
                    // "Success" without a picture. One is harmless, but a decoder that answers like this forever would keep this loop spinning in managed code, so it is bounded like the format changes.
                    if (++emptyOutputs > MaxStreamChangesPerDecode)
                        throw new InvalidOperationException("The H.264 decoder keeps returning empty output.");
                }
                else
                {
                    try
                    {
                        onFrame(ToBgra(result, out var w, out var h), w, h);
                        frames++;
                    }
                    finally
                    {
                        // Also when the conversion throws (e.g. Lock2D after a GPU device loss): SharpGen never
                        // releases COM objects by itself, so a skipped Dispose leaks the decoder's sample and its buffer.
                        if (own == null) result.Dispose();
                    }
                }
            }
            finally
            {
                own?.Dispose();
                output.Events?.Dispose();
            }
        }
    }

    // Chooses NV12 as output and reads the geometry. NV12 is the decoder's native layout, so no conversion happens inside the MFT.
    private bool TryNegotiateOutputType()
    {
        var mft = _mft!;
        for (var i = 0; ; i++)
        {
            // A decoder lists a handful of output types. Never ending without NV12 would be a broken decoder: fail, do not loop.
            if (i >= MaxOutputTypesToTry)
                throw new InvalidOperationException("The H.264 decoder offers no NV12 output type.");
            IMFMediaType type;
            try
            {
                type = mft.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code is MfETransformNeedMoreInput or unchecked((int)0xC00D6D5E) /* MF_E_NO_MORE_TYPES */ or unchecked((int)0xC00D6D5F) /* MF_E_TRANSFORM_TYPE_NOT_SET */)
            {
                return false;
            }

            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                    continue;
                mft.SetOutputType(0, type, 0);

                var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                var codedWidth = (int)(size >> 32);
                _bufferHeight = (int)(size & 0xFFFFFFFF);
                _width = codedWidth;
                _height = _bufferHeight;
                // MFVideoArea: MFOffset x (fract u16 + value i16), MFOffset y, SIZE (cx, cy as i32). Size is at bytes 8..15.
                // The visible rectangle inside the (16-aligned) coded frame, e.g. 1920x1080 in 1920x1088. The minimum
                // display aperture is the one MS says must be honoured; the geometric aperture is the older fallback.
                // Clamped to the coded size, so a bogus value can never make us read past the buffer.
                foreach (var key in new[] { MediaTypeAttributeKeys.MinimumDisplayAperture, MediaTypeAttributeKeys.GeometricAperture })
                {
                    if (!type.GetBlobSize(key, out var areaSize).Success || areaSize < 16) continue;
                    var area = type.GetBlob(key);
                    var w = BitConverter.ToInt32(area, 8);
                    var h = BitConverter.ToInt32(area, 12);
                    if (w <= 0 || h <= 0 || w > codedWidth || h > _bufferHeight) continue;
                    _width = w;
                    _height = h;
                    break;
                }
                _stride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var stride).Success && stride != 0 ? unchecked((int)stride) : codedWidth;
                if (_stride < 0) _stride = -_stride; // bottom-up never happens for NV12, but a negative stride must not index backwards

                var info = mft.GetOutputStreamInfo(0);
                _providesSamples = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
                _outputSampleSize = Math.Max(info.Size, _stride * _bufferHeight * 3 / 2);
                _outputTypeSet = true;
                return true;
            }
        }
    }

    private byte[] ToBgra(IMFSample sample, out int width, out int height)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        // Lock2D reports the real row pitch of this particular buffer; for GPU-backed buffers it can differ from the media type's stride.
        using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        IntPtr ptr;
        int stride;
        if (buffer2D != null)
            buffer2D.Lock2D(out ptr, out stride);
        else
        {
            buffer.Lock(out ptr, out _, out _);
            stride = _stride;
        }
        try
        {
            width = _width;
            height = _height;
            var bgra = new byte[width * height * 4];
            Nv12ToBgra.Convert(ptr, Math.Abs(stride), _bufferHeight, width, height, bgra);
            return bgra;
        }
        finally
        {
            if (buffer2D != null) buffer2D.Unlock2D(); else buffer.Unlock();
        }
    }

    public void Dispose()
    {
        var mft = Interlocked.Exchange(ref _mft, null);
        if (mft == null) return;
        try
        {
            mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
            // Tearing down: a failing notification changes nothing, the Dispose below releases the COM object anyway.
        }
        mft.Dispose();
        _deviceManager?.Dispose();
        _device?.Dispose();
    }
}

/// <summary>
/// NV12 to BGRA, BT.601 limited range. That is the matrix the pipeline already uses end to end: the host converts
/// RGB to I420 with BT.601 (see the colour test in HybridVideoEncoderTests) and the FFmpeg viewer path decodes with
/// swscale's default, which is also BT.601 limited. Using 709 here would shift reds and greens visibly.
/// Integer fixed point (8 fractional bits): R = 1.164 (Y-16) + 1.596 (V-128), and so on.
/// </summary>
internal static unsafe class Nv12ToBgra
{
    public static void Convert(IntPtr nv12, int stride, int bufferHeight, int width, int height, byte[] bgra)
    {
        var yPlane = (byte*)nv12;
        var uvPlane = yPlane + (long)stride * bufferHeight; // the UV plane follows ALL buffer rows, also the padding ones
        fixed (byte* dst = bgra)
        {
            for (var row = 0; row < height; row++)
                ConvertRow(yPlane + (long)row * stride, uvPlane + (long)(row / 2) * stride, width, (uint*)(dst + (long)row * width * 4));
        }
    }

    /// <summary>Planar I420 (libvpx output) to BGRA with the same maths. Each plane has its own stride; chroma is half size.</summary>
    public static void ConvertI420(byte* yPlane, int yStride, byte* uPlane, int uStride, byte* vPlane, int vStride, int width, int height, byte[] bgra)
    {
        fixed (byte* dst = bgra)
        {
            for (var row = 0; row < height; row++)
                ConvertRowI420(yPlane + (long)row * yStride, uPlane + (long)(row / 2) * uStride, vPlane + (long)(row / 2) * vStride, width, (uint*)(dst + (long)row * width * 4));
        }
    }

    // 8 pixels: widen bytes to 32-bit lanes so the fixed-point products cannot overflow.
    private static Vector256<int> Pack(Vector256<int> y, Vector256<int> u, Vector256<int> v)
    {
        var c128 = Vector256.Create(128);
        var luma = (y - Vector256.Create(16)) * Vector256.Create(298) + c128;
        u -= c128;
        v -= c128;
        var zero = Vector256<int>.Zero; var max = Vector256.Create(255);
        var b = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(luma + Vector256.Create(516) * u, 8), zero), max);
        var g = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(luma - Vector256.Create(100) * u - Vector256.Create(208) * v, 8), zero), max);
        var r = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(luma + Vector256.Create(409) * v, 8), zero), max);
        return b | Vector256.ShiftLeft(g, 8) | Vector256.ShiftLeft(r, 16) | Vector256.Create(unchecked((int)0xFF000000));
    }

    private static uint Pixel(int y, int u, int v)
    {
        var c = (y - 16) * 298 + 128;
        var d = u - 128;
        var e = v - 128;
        return Clamp((c + 516 * d) >> 8) | (uint)Clamp((c - 100 * d - 208 * e) >> 8) << 8 | (uint)Clamp((c + 409 * e) >> 8) << 16 | 0xFF000000;
    }

    private static void ConvertRow(byte* y, byte* uv, int width, uint* o)
    {
        var x = 0;
        if (Avx2.IsSupported)
        {
            var evenIdx = Vector256.Create(0, 0, 2, 2, 4, 4, 6, 6); // [u0 v0 u1 v1 ..] -> u0 u0 u1 u1 ..
            var oddIdx = Vector256.Create(1, 1, 3, 3, 5, 5, 7, 7);  //                  -> v0 v0 v1 v1 ..
            for (; x + 8 <= width; x += 8)
            {
                var uvPairs = Avx2.ConvertToVector256Int32(uv + x);
                Pack(Avx2.ConvertToVector256Int32(y + x), Avx2.PermuteVar8x32(uvPairs, evenIdx), Avx2.PermuteVar8x32(uvPairs, oddIdx)).Store((int*)(o + x));
            }
        }
        for (; x < width; x++) // scalar tail, and the whole row without AVX2
            o[x] = Pixel(y[x], uv[x & ~1], uv[(x & ~1) + 1]);
    }

    private static void ConvertRowI420(byte* y, byte* u, byte* v, int width, uint* o)
    {
        var x = 0;
        if (Avx2.IsSupported)
        {
            var dup = Vector256.Create(0, 0, 1, 1, 2, 2, 3, 3); // 4 chroma samples cover 8 pixels
            // Loads 8 chroma bytes but uses 4, so stop 16 pixels early: the read can never pass the end of a chroma row.
            for (; x + 16 <= width; x += 8)
                Pack(Avx2.ConvertToVector256Int32(y + x), Avx2.PermuteVar8x32(Avx2.ConvertToVector256Int32(u + x / 2), dup),
                    Avx2.PermuteVar8x32(Avx2.ConvertToVector256Int32(v + x / 2), dup)).Store((int*)(o + x));
        }
        for (; x < width; x++)
            o[x] = Pixel(y[x], u[x / 2], v[x / 2]);
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
