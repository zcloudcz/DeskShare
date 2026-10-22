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
/// The GStreamer stream is shared and kept for a short time after it was opened (<see cref="SharedSessionCache{T}"/>):
/// the sender probes the screen size with one capturer and then creates the real one, and some portals
/// (xdg-desktop-portal-wlr) end a stream once its consumer disconnects, so a second consumer could not reattach.
///
/// Like <see cref="FfmpegScreenCapturer"/>, frames land in a single "latest frame" slot that
/// <see cref="TryAcquireFrame"/> takes, so a slow consumer never queues frames.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PipeWireScreenCapturer : IScreenCapturer
{
    private static readonly ILogger Logger = Log.ForContext<PipeWireScreenCapturer>();
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly SharedSessionCache<Stream> Streams = new(TimeSpan.FromSeconds(15));

    private readonly object _lock = new();
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private Stream? _stream;
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
        if (_stream != null)
            throw new InvalidOperationException("Capturer is already initialized.");

        try
        {
            // Two tries: a portal can hand out a stream that is already gone (xdg-desktop-portal-wlr when the
            // previous sender was stopped a moment ago: "target not found"); a fresh portal session then works.
            for (int attempt = 1; ; attempt++)
            {
                var stream = Streams.Acquire(Stream.Open);
                _stream = stream;
                // A kept stream may have ended or failed meanwhile (e.g. sharing stopped from the desktop); then
                // Subscribe refuses it and the retry below opens a new one.
                var error = stream.Subscribe(OnSample) ? WaitForFirstFrame(stream) : "the kept stream has ended";
                if (error == null)
                {
                    Logger.Information("[PipeWireScreenCapturer] Capturing PipeWire node {Node} at {Width}x{Height}", stream.NodeId, Width, Height);
                    return true;
                }

                stream.Unsubscribe(OnSample); // after this no callback of that stream runs any more
                stream.Discard(); // never hand a stream that delivers nothing to the next capturer
                _stream = null;
                ResetFrameState(); // a late frame of the failed attempt must not satisfy the next one
                if (attempt == 2)
                {
                    Logger.Error("[PipeWireScreenCapturer] No frame from the PipeWire stream: {Error}", error);
                    return false;
                }
                Logger.Warning("[PipeWireScreenCapturer] No frame from the PipeWire stream ({Error}); retrying with a new portal session", error);
            }
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

    /// <summary>Null once the first frame arrived, otherwise why not (fails fast when the stream fails).</summary>
    private string? WaitForFirstFrame(Stream stream)
    {
        var deadline = DateTime.UtcNow + FirstFrameTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_firstFrame.Wait(TimeSpan.FromMilliseconds(100)))
                return null;
            if (stream.Ended)
                return stream.Error ?? "stream ended";
        }
        return $"no frame within {FirstFrameTimeout.TotalSeconds:0} s";
    }

    private void ResetFrameState()
    {
        Frame? stale;
        lock (_lock)
        {
            stale = _latest;
            _latest = null;
        }
        stale?.Dispose();
        _firstFrame.Reset();
        Width = Height = 0;
        _sizeMismatchLogged = false;
    }

    /// <summary>
    /// One portal session's PipeWire stream read through GStreamer, shared by consecutive capturers.
    /// </summary>
    private sealed class Stream : IDisposable
    {
        private readonly WaylandPortalSession _session;
        private readonly SafeHandle _pipeWireFd;
        private readonly IntPtr _pipeline;
        private readonly IntPtr _appSink;
        private readonly Thread _pullThread;
        private readonly object _subscribersLock = new();
        private readonly List<Action<IntPtr>> _subscribers = new(); // guarded by _subscribersLock
        private IntPtr _lastSample;                                 // guarded by _subscribersLock
        private volatile bool _stopping;
        private int _disposed;

        public uint NodeId => _session.NodeId;

        /// <summary>True once the stream stopped delivering: end of stream or a pipeline error (set under the subscribers lock).</summary>
        public volatile bool Ended;

        /// <summary>The pipeline error that ended the stream, if any.</summary>
        public volatile string? Error;

        private Stream(WaylandPortalSession session, SafeHandle pipeWireFd, IntPtr pipeline)
        {
            _session = session;
            _pipeWireFd = pipeWireFd;
            _pipeline = pipeline;
            _appSink = Gst.gst_bin_get_by_name(pipeline, "sink");
            _pullThread = new Thread(PullLoop) { IsBackground = true, Name = "PipeWire capture" };
        }

        public static Stream Open()
        {
            var session = WaylandPortalSession.Acquire();
            SafeHandle? fd = null;
            Stream? stream = null;
            try
            {
                fd = session.OpenPipeWireRemote();
                Gst.gst_init(IntPtr.Zero, IntPtr.Zero);
                // BGRA (not BGRx): videoconvert then writes an opaque alpha, which is what the pipeline expects.
                // appsink keeps only the newest buffer and never syncs to the clock (live capture); the capture
                //   pipeline's own timer then sets the frame rate, as with x11grab.
                var description = $"pipewiresrc fd={fd.DangerousGetHandle()} path={session.NodeId} always-copy=true " +
                                  "! videoconvert ! video/x-raw,format=BGRA " +
                                  "! appsink name=sink max-buffers=1 drop=true sync=false";
                var pipeline = Gst.gst_parse_launch(description, out var error);
                if (error != IntPtr.Zero)
                {
                    // A recoverable error (e.g. a property older plugins lack) still returns a pipeline.
                    Logger.Warning("[PipeWireScreenCapturer] GStreamer: {Error}", Gst.TakeErrorMessage(error));
                }
                if (pipeline == IntPtr.Zero)
                    throw new InvalidOperationException("Could not build the GStreamer pipeline; install the GStreamer PipeWire plugin (gstreamer1.0-pipewire)");

                stream = new Stream(session, fd, pipeline); // owns session reference, fd and pipeline from here
                if (Gst.gst_element_set_state(pipeline, Gst.StatePlaying) == Gst.StateChangeFailure)
                    throw new InvalidOperationException("GStreamer pipeline did not start: " + stream.PopError());
                stream._pullThread.Start();
                return stream;
            }
            catch
            {
                if (stream != null)
                {
                    stream.Dispose();
                }
                else
                {
                    fd?.Dispose();
                    WaylandPortalSession.Release(session);
                }
                throw;
            }
        }

        /// <summary>
        /// Adds a frame consumer; it gets the newest frame at once, so it never waits for screen damage. Returns
        /// false (and adds nothing) when the stream has ended or failed or the portal closed its session: the
        /// cached frame would then be the last picture of a dead stream.
        /// </summary>
        public bool Subscribe(Action<IntPtr> onSample)
        {
            lock (_subscribersLock)
            {
                if (Ended || _session.IsClosed)
                    return false;
                _subscribers.Add(onSample);
                if (_lastSample != IntPtr.Zero) Deliver(onSample, _lastSample);
                return true;
            }
        }

        public void Unsubscribe(Action<IntPtr> onSample)
        {
            lock (_subscribersLock) _subscribers.Remove(onSample);
        }

        public string PopError() => Gst.PopBusError(_pipeline);

        /// <summary>Drops this stream and its portal session from the caches, so the next capturer starts fresh.</summary>
        public void Discard()
        {
            Streams.Invalidate(this);
            WaylandPortalSession.Invalidate(_session);
        }

        private static void Deliver(Action<IntPtr> onSample, IntPtr sample)
        {
            try
            {
                onSample(sample);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[PipeWireScreenCapturer] Failed to read a frame");
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
            var lastDelivered = DateTime.UtcNow;
            while (!_stopping)
            {
                var sample = Gst.gst_app_sink_try_pull_sample(_appSink, pullTimeoutNs);
                lock (_subscribersLock)
                {
                    if (sample == IntPtr.Zero)
                    {
                        // A pipeline error (e.g. "target not found") does not always end in EOS, so check both.
                        var error = PopError();
                        if (error != "none" || Gst.gst_app_sink_is_eos(_appSink))
                        {
                            Logger.Error("[PipeWireScreenCapturer] Stream ended: {Error}", error);
                            Error = error != "none" ? error : null;
                            Ended = true;
                            return;
                        }
                        if (_lastSample == IntPtr.Zero || DateTime.UtcNow - lastDelivered < repeatInterval)
                            continue;
                        sample = Gst.gst_mini_object_ref(_lastSample); // repeat the idle screen
                    }

                    foreach (var subscriber in _subscribers)
                        Deliver(subscriber, sample);
                    lastDelivered = DateTime.UtcNow;

                    if (_lastSample != IntPtr.Zero) Gst.gst_mini_object_unref(_lastSample);
                    _lastSample = sample; // takes over this loop's reference
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _stopping = true;
            if (_pullThread.IsAlive) _pullThread.Join();

            Gst.gst_element_set_state(_pipeline, Gst.StateNull);
            if (_appSink != IntPtr.Zero) Gst.gst_object_unref(_appSink);
            Gst.gst_object_unref(_pipeline);
            lock (_subscribersLock)
            {
                if (_lastSample != IntPtr.Zero) Gst.gst_mini_object_unref(_lastSample);
                _lastSample = IntPtr.Zero;
            }

            _pipeWireFd.Dispose(); // pipewiresrc works on its own dup of the fd
            WaylandPortalSession.Release(_session);
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

        if (_stream != null)
        {
            _stream.Unsubscribe(OnSample);
            Streams.Release(_stream);
        }
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
