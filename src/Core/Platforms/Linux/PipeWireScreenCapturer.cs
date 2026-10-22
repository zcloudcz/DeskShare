using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using Serilog;

namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// Wayland screen capturer: the desktop portal grants a PipeWire stream (see <see cref="WaylandPortalSession"/>)
/// and GStreamer's pipewiresrc turns it into BGRA frames.
/// </summary>
/// <remarks>
/// Why GStreamer: the bundled FFmpeg build has no PipeWire input, and libpipewire's C API negotiates formats
/// through inline SPA helpers that cannot be P/Invoked sensibly. GStreamer is called in-process (a helper
/// process would not inherit the portal's PipeWire fd) through a dozen plain C functions. It is a system
/// dependency, not bundled: the pipewiresrc plugin must match the user's PipeWire daemon. Packages:
/// gstreamer1.0-pipewire + gstreamer1.0-plugins-base (Debian/Ubuntu), gstreamer1-plugins-base + pipewire-gstreamer
/// (Fedora), gst-plugins-base + gst-plugin-pipewire (Arch). GNOME and KDE desktops usually have them already.
///
/// Like <see cref="FfmpegScreenCapturer"/>, a background thread fills a single "latest frame" slot that
/// <see cref="TryAcquireFrame"/> takes, so a slow consumer never queues frames.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PipeWireScreenCapturer : IScreenCapturer
{
    private static readonly ILogger Logger = Log.ForContext<PipeWireScreenCapturer>();
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private WaylandPortalSession? _session;
    private SafeHandle? _pipeWireFd;
    private IntPtr _pipeline;
    private IntPtr _appSink;
    private Thread? _pullThread;
    private volatile bool _stopping;
    private Frame? _latest; // guarded by _lock
    private bool _disposed; // guarded by _lock
    private bool _sizeMismatchLogged;

    /// <inheritdoc />
    public int Width { get; private set; }

    /// <inheritdoc />
    public int Height { get; private set; }

    /// <inheritdoc />
    public bool Initialize()
    {
        if (_session != null)
            throw new InvalidOperationException("Capturer is already initialized.");

        try
        {
            _session = WaylandPortalSession.Acquire();
            _pipeWireFd = _session.OpenPipeWireRemote();

            Gst.gst_init(IntPtr.Zero, IntPtr.Zero);
            // BGRA (not BGRx): videoconvert then writes an opaque alpha, which is what the pipeline expects.
            // appsink keeps only the newest buffer and never syncs to the clock (live capture); the capture
            //   pipeline's own timer then sets the frame rate, as with x11grab.
            var description = $"pipewiresrc fd={_pipeWireFd.DangerousGetHandle()} path={_session.NodeId} always-copy=true " +
                              "! videoconvert ! video/x-raw,format=BGRA " +
                              "! appsink name=sink max-buffers=1 drop=true sync=false";
            _pipeline = Gst.gst_parse_launch(description, out var error);
            if (error != IntPtr.Zero)
            {
                // A recoverable error (e.g. a property older plugins lack) still returns a pipeline.
                Logger.Warning("[PipeWireScreenCapturer] GStreamer: {Error}", Gst.TakeErrorMessage(error));
            }
            if (_pipeline == IntPtr.Zero)
            {
                Logger.Error("[PipeWireScreenCapturer] Could not build the GStreamer pipeline; install the GStreamer PipeWire plugin (gstreamer1.0-pipewire)");
                return false;
            }

            _appSink = Gst.gst_bin_get_by_name(_pipeline, "sink");
            if (Gst.gst_element_set_state(_pipeline, Gst.StatePlaying) == Gst.StateChangeFailure)
            {
                Logger.Error("[PipeWireScreenCapturer] Pipeline did not start: {Error}", Gst.PopBusError(_pipeline));
                return false;
            }

            _pullThread = new Thread(PullLoop) { IsBackground = true, Name = "PipeWire capture" };
            _pullThread.Start();

            if (!_firstFrame.Wait(FirstFrameTimeout))
            {
                Logger.Error("[PipeWireScreenCapturer] No frame within {Timeout}: {Error}", FirstFrameTimeout, Gst.PopBusError(_pipeline));
                return false;
            }

            Logger.Information("[PipeWireScreenCapturer] Capturing PipeWire node {Node} at {Width}x{Height}", _session.NodeId, Width, Height);
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Logger.Error(ex, "[PipeWireScreenCapturer] GStreamer is not installed (libgstreamer-1.0 / libgstapp-1.0)");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[PipeWireScreenCapturer] Initialization failed");
            return false;
        }
    }

    private void PullLoop()
    {
        const ulong pullTimeoutNs = 100_000_000; // 100 ms: bounds how long Dispose waits for this thread
        // Compositors only send a frame when the screen changes. A viewer that joins while the screen is idle
        // would wait for the next change, so the newest sample is kept and delivered again once per second.
        // (pipewiresrc's keepalive-time does not fire for these streams.) The sample is a copy
        // (always-copy=true), so holding it does not starve the compositor of PipeWire buffers.
        var repeatInterval = TimeSpan.FromSeconds(1);
        var lastSample = IntPtr.Zero;
        var lastDelivered = DateTime.UtcNow;
        try
        {
            while (!_stopping)
            {
                var sample = Gst.gst_app_sink_try_pull_sample(_appSink, pullTimeoutNs);
                if (sample == IntPtr.Zero)
                {
                    if (Gst.gst_app_sink_is_eos(_appSink))
                    {
                        Logger.Error("[PipeWireScreenCapturer] Stream ended: {Error}", Gst.PopBusError(_pipeline));
                        return;
                    }
                    if (lastSample == IntPtr.Zero || DateTime.UtcNow - lastDelivered < repeatInterval)
                        continue;
                    sample = Gst.gst_mini_object_ref(lastSample); // repeat the idle screen
                }

                try
                {
                    OnSample(sample);
                    lastDelivered = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[PipeWireScreenCapturer] Failed to read a frame");
                }

                if (lastSample != IntPtr.Zero) Gst.gst_mini_object_unref(lastSample);
                lastSample = sample; // takes over this loop's reference
            }
        }
        finally
        {
            if (lastSample != IntPtr.Zero) Gst.gst_mini_object_unref(lastSample);
        }
    }

    private unsafe void OnSample(IntPtr sample)
    {
        var structure = Gst.gst_caps_get_structure(Gst.gst_sample_get_caps(sample), 0);
        if (!Gst.gst_structure_get_int(structure, "width", out var width) || !Gst.gst_structure_get_int(structure, "height", out var height)
            || width <= 0 || height <= 0)
            return;

        var buffer = Gst.gst_sample_get_buffer(sample);
        if (!Gst.gst_buffer_map(buffer, out var map, Gst.MapRead))
            return;
        try
        {
            // The first frame fixes the size. Even dimensions because the I420 converter needs them.
            if (Width == 0)
            {
                Width = width & ~1;
                Height = height & ~1;
            }

            // videoconvert writes default (tightly packed) rows, but derive the stride from the buffer size so a
            // padded passthrough buffer is still read correctly.
            var srcStride = (int)((long)map.Size / height);
            if (width < Width || height < Height || srcStride < Width * 4)
            {
                if (!_sizeMismatchLogged)
                {
                    _sizeMismatchLogged = true;
                    Logger.Warning("[PipeWireScreenCapturer] Frame {W}x{H} does not fit {Width}x{Height} (resolution change?); dropping",
                        width, height, Width, Height);
                }
                return;
            }

            var src = new ReadOnlySpan<byte>((void*)map.Data, (int)map.Size);
            int dstStride = Width * 4;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(dstStride * Height);
            for (int y = 0; y < Height; y++)
                src.Slice(y * srcStride, dstStride).CopyTo(pixels.AsSpan(y * dstStride, dstStride));

            var frame = Frame.FromPooled(Width, Height, pixels, dstStride, DateTime.UtcNow);
            Frame? replaced;
            lock (_lock)
            {
                if (_disposed)
                {
                    frame.Dispose();
                    return;
                }
                replaced = _latest;
                _latest = frame;
            }
            replaced?.Dispose(); // never consumed: give the buffer back to the pool
            _firstFrame.Set();
        }
        finally
        {
            Gst.gst_buffer_unmap(buffer, ref map);
        }
    }

    /// <inheritdoc />
    public bool TryAcquireFrame(out Frame? frame)
    {
        lock (_lock)
        {
            frame = _latest;
            _latest = null;
        }
        return frame != null;
    }

    /// <inheritdoc />
    public void ReleaseFrame()
    {
        // Nothing to release: ownership of the frame passed to the caller in TryAcquireFrame.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Frame? pending;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            pending = _latest;
            _latest = null;
        }
        pending?.Dispose();

        _stopping = true;
        _pullThread?.Join();

        if (_pipeline != IntPtr.Zero)
        {
            Gst.gst_element_set_state(_pipeline, Gst.StateNull);
            if (_appSink != IntPtr.Zero) Gst.gst_object_unref(_appSink);
            Gst.gst_object_unref(_pipeline);
        }

        _pipeWireFd?.Dispose(); // pipewiresrc works on its own dup of the fd
        if (_session != null) WaylandPortalSession.Release();
        _firstFrame.Dispose();
    }

    /// <summary>The handful of GStreamer C functions the capturer needs.</summary>
    private static class Gst
    {
        private const string Lib = "libgstreamer-1.0.so.0";
        private const string AppLib = "libgstapp-1.0.so.0";
        private const string GLib = "libglib-2.0.so.0";

        public const int StateNull = 1, StatePlaying = 4, StateChangeFailure = 0;
        public const int MapRead = 1;
        private const int MessageError = 1 << 1;

        /// <summary>GstMapInfo; only Data and Size are read, the rest keeps the native layout.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct MapInfo
        {
            public IntPtr Memory;
            public int Flags;
            public IntPtr Data;
            public nuint Size;
            public nuint MaxSize;
            private IntPtr _user0, _user1, _user2, _user3;
            private IntPtr _reserved0, _reserved1, _reserved2, _reserved3;
        }

        [DllImport(Lib)] public static extern void gst_init(IntPtr argc, IntPtr argv);
        [DllImport(Lib)] public static extern IntPtr gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string description, out IntPtr error);
        [DllImport(Lib)] public static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Lib)] public static extern int gst_element_set_state(IntPtr element, int state);
        [DllImport(Lib)] public static extern IntPtr gst_element_get_bus(IntPtr element);
        [DllImport(Lib)] public static extern IntPtr gst_bus_pop_filtered(IntPtr bus, int types);
        [DllImport(Lib)] public static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);
        [DllImport(Lib)] public static extern void gst_object_unref(IntPtr obj);
        [DllImport(Lib)] public static extern void gst_mini_object_unref(IntPtr obj);
        [DllImport(Lib)] public static extern IntPtr gst_mini_object_ref(IntPtr obj);
        [DllImport(Lib)] public static extern IntPtr gst_sample_get_buffer(IntPtr sample);
        [DllImport(Lib)] public static extern IntPtr gst_sample_get_caps(IntPtr sample);
        [DllImport(Lib)] public static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);
        [DllImport(Lib)][return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool gst_structure_get_int(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int value);
        [DllImport(Lib)][return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool gst_buffer_map(IntPtr buffer, out MapInfo info, int flags);
        [DllImport(Lib)] public static extern void gst_buffer_unmap(IntPtr buffer, ref MapInfo info);
        [DllImport(AppLib)] public static extern IntPtr gst_app_sink_try_pull_sample(IntPtr appSink, ulong timeoutNs);
        [DllImport(AppLib)][return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool gst_app_sink_is_eos(IntPtr appSink);
        [DllImport(GLib)] private static extern void g_error_free(IntPtr error);
        [DllImport(GLib)] private static extern void g_free(IntPtr mem);

        /// <summary>Reads GError.message (after the 4-byte domain and 4-byte code) and frees the error.</summary>
        public static string TakeErrorMessage(IntPtr error)
        {
            var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "";
            g_error_free(error);
            return message;
        }

        /// <summary>The first error posted on the pipeline bus, for log messages ("none" when there is none).</summary>
        public static string PopBusError(IntPtr pipeline)
        {
            var bus = gst_element_get_bus(pipeline);
            var message = gst_bus_pop_filtered(bus, MessageError);
            gst_object_unref(bus);
            if (message == IntPtr.Zero)
                return "none";

            gst_message_parse_error(message, out var error, out var debug);
            var text = TakeErrorMessage(error) + (debug != IntPtr.Zero ? $" ({Marshal.PtrToStringUTF8(debug)})" : "");
            g_free(debug);
            gst_mini_object_unref(message);
            return text;
        }
    }
}
