using System.Runtime.InteropServices;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// VP8 decoder that talks to the libvpx DLL shipped with SIPSorceryMedia.Encoders (vpxmd.dll, Windows) directly.
/// Why not VpxVideoEncoder.DecodeVideo: it converts to BGR itself with a full-range formula (greys and saturated
/// colours come out wrong compared to the FFmpeg path) and cannot return the raw planes. Here we take the I420 planes
/// and convert them with the same BT.601 limited-range code as the H.264 path.
///
/// Not thread safe; the sink calls it under its lock.
/// </summary>
internal sealed unsafe class VpxDecoder : IH264FrameDecoder // same Decode shape, so the sink handles both alike
{
    // VPX_DECODER_ABI_VERSION of the shipped vpxmd.dll (libvpx v1.9.0): 3 + (4 + VPX_IMAGE_ABI_VERSION 5) = 12.
    // vpx_codec_dec_init_ver refuses a different value with VPX_CODEC_ABI_MISMATCH, so a wrong constant fails loudly.
    private const int DecoderAbiVersion = 12;

    // sizeof(vpx_codec_ctx_t) is 56 bytes on x64; the extra room guards against a layout change in another libvpx build.
    private const int CtxBytes = 128;

    [DllImport("vpxmd")] private static extern IntPtr vpx_codec_vp8_dx();
    [DllImport("vpxmd")] private static extern int vpx_codec_dec_init_ver(IntPtr ctx, IntPtr iface, IntPtr cfg, int flags, int ver);
    [DllImport("vpxmd")] private static extern int vpx_codec_decode(IntPtr ctx, byte* data, uint size, IntPtr userPriv, int deadline);
    [DllImport("vpxmd")] private static extern IntPtr vpx_codec_get_frame(IntPtr ctx, ref IntPtr iter);
    [DllImport("vpxmd")] private static extern int vpx_codec_destroy(IntPtr ctx);

    private IntPtr _ctx;

    public VpxDecoder()
    {
        _ctx = Marshal.AllocHGlobal(CtxBytes);
        new Span<byte>((void*)_ctx, CtxBytes).Clear();
        var rc = vpx_codec_dec_init_ver(_ctx, vpx_codec_vp8_dx(), IntPtr.Zero, 0, DecoderAbiVersion);
        if (rc != 0)
        {
            Marshal.FreeHGlobal(_ctx);
            _ctx = IntPtr.Zero;
            throw new InvalidOperationException($"vpx_codec_dec_init_ver failed with {rc} (5 = ABI mismatch).");
        }
    }

    /// <summary>Decodes one VP8 frame; calls <paramref name="onFrame"/> with (bgra, width, height) for each picture (normally one).</summary>
    public int Decode(byte[] vp8, Action<byte[], int, int> onFrame)
    {
        if (_ctx == IntPtr.Zero) throw new ObjectDisposedException(nameof(VpxDecoder));
        int rc;
        fixed (byte* p = vp8)
            rc = vpx_codec_decode(_ctx, p, (uint)vp8.Length, IntPtr.Zero, 0);
        if (rc != 0)
            throw new InvalidOperationException($"vpx_codec_decode failed with {rc}.");

        var frames = 0;
        var iter = IntPtr.Zero;
        IntPtr image;
        while ((image = vpx_codec_get_frame(_ctx, ref iter)) != IntPtr.Zero)
        {
            // vpx_image_t (libvpx 1.9): d_w at byte 24, d_h at 28; planes[4] pointers at 48; stride[4] ints at 80.
            // The image memory belongs to the decoder and stays valid until the next decode call, so convert right now.
            var b = (byte*)image;
            // fmt at byte 0. VP8 is always 8-bit I420 (VPX_IMG_FMT_I420 = 0x102); anything else would have other plane
            // sizes, so refuse it instead of reading past the planes.
            if (*(int*)b != 0x102) throw new InvalidOperationException($"Unexpected vpx image format 0x{*(int*)b:X}.");
            var w = *(int*)(b + 24);
            var h = *(int*)(b + 28);
            var planes = (byte**)(b + 48);
            var strides = (int*)(b + 80);
            var bgra = new byte[w * h * 4];
            Nv12ToBgra.ConvertI420(planes[0], strides[0], planes[1], strides[1], planes[2], strides[2], w, h, bgra);
            onFrame(bgra, w, h);
            frames++;
        }
        return frames;
    }

    public void Dispose()
    {
        var ctx = Interlocked.Exchange(ref _ctx, IntPtr.Zero);
        if (ctx == IntPtr.Zero) return;
        vpx_codec_destroy(ctx);
        Marshal.FreeHGlobal(ctx);
    }
}
