using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace DeskShare.Core.Video;

/// <summary>One open H.264 encoder at one resolution. Seam for <see cref="HardwareH264Encoder"/> so its failure handling is testable.</summary>
internal interface IH264Context : IDisposable
{
    /// <summary>Annex B bytes for one I420 frame, or null while the encoder still buffers (hardware encoders lag a frame).</summary>
    byte[]? Encode(byte[] i420, bool forceKeyFrame);
}

/// <summary>
/// Minimal FFmpeg encode loop for a named encoder. Written by hand instead of using SIPSorceryMedia.FFmpeg's
/// FFmpegVideoEncoder because that one (10.0.16) hands YUV420P planes to a context whose pix_fmt is the encoder's
/// first format (NV12 for AMF), so chroma comes out wrong, and it allocates an AVPacket per frame it never frees.
/// We always feed NV12, which every hardware H.264 encoder takes, and convert from I420 ourselves.
/// </summary>
internal sealed unsafe class NativeH264Context : IH264Context
{
    private const int Fps = 30;

    private readonly int _width, _height;
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _packet; // reused for every frame, freed in Dispose
    private long _pts;

    public NativeH264Context(string encoderName, Dictionary<string, string> options, int width, int height, long bitrate)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("Width and height must be positive and even.");
        _width = width;
        _height = height;

        var codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        if (codec == null)
            throw new InvalidOperationException($"FFmpeg has no encoder '{encoderName}'.");

        try
        {
            _ctx = ffmpeg.avcodec_alloc_context3(codec);
            _ctx->width = width;
            _ctx->height = height;
            _ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            _ctx->time_base = new AVRational { num = 1, den = Fps };
            _ctx->framerate = new AVRational { num = Fps, den = 1 };
            _ctx->gop_size = Fps * 2; // a periodic IDR lets a viewer that missed the first one recover
            _ctx->max_b_frames = 0;   // B-frames add latency
            _ctx->bit_rate = bitrate;

            AVDictionary* dict = null;
            foreach (var (key, value) in options)
                ffmpeg.av_dict_set(&dict, key, value, 0);
            var rc = ffmpeg.avcodec_open2(_ctx, codec, &dict);
            ffmpeg.av_dict_free(&dict);
            Check(rc, "avcodec_open2");

            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            _frame->width = width;
            _frame->height = height;
            Check(ffmpeg.av_frame_get_buffer(_frame, 32), "av_frame_get_buffer");
            _packet = ffmpeg.av_packet_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public byte[]? Encode(byte[] i420, bool forceKeyFrame)
    {
        if (i420.Length < (long)_width * _height * 3 / 2)
            throw new ArgumentException("I420 buffer is shorter than width x height x 1.5.", nameof(i420));
        Check(ffmpeg.av_frame_make_writable(_frame), "av_frame_make_writable");
        FillNv12(i420);
        _frame->pts = _pts++;
        _frame->pict_type = forceKeyFrame ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        _frame->flags = forceKeyFrame ? _frame->flags | ffmpeg.AV_FRAME_FLAG_KEY : _frame->flags & ~ffmpeg.AV_FRAME_FLAG_KEY;

        Check(ffmpeg.avcodec_send_frame(_ctx, _frame), "avcodec_send_frame");

        var rc = ffmpeg.avcodec_receive_packet(_ctx, _packet);
        if (rc == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            return null;
        Check(rc, "avcodec_receive_packet");

        var bytes = new byte[_packet->size];
        Marshal.Copy((IntPtr)_packet->data, bytes, 0, bytes.Length);
        ffmpeg.av_packet_unref(_packet);
        return bytes;
    }

    // I420 = Y plane, U plane, V plane. NV12 = Y plane, then U and V interleaved in one plane.
    private void FillNv12(byte[] i420)
    {
        var chromaW = _width / 2;
        var chromaH = _height / 2;
        fixed (byte* src = i420)
        {
            for (var y = 0; y < _height; y++)
                Buffer.MemoryCopy(src + (long)y * _width, _frame->data[0] + (long)y * _frame->linesize[0], _width, _width);

            var u = src + (long)_width * _height;
            var v = u + (long)chromaW * chromaH;
            for (var y = 0; y < chromaH; y++)
            {
                var dst = _frame->data[1] + (long)y * _frame->linesize[1];
                var us = u + (long)y * chromaW;
                var vs = v + (long)y * chromaW;
                for (var x = 0; x < chromaW; x++)
                {
                    dst[2 * x] = us[x];
                    dst[2 * x + 1] = vs[x];
                }
            }
        }
    }

    /// <summary>
    /// Encodes four known colour blocks, decodes the result with FFmpeg's H.264 decoder and compares the colours.
    /// Catches what "no exception" does not: swapped or shifted chroma (wrong pixel format) and garbage output.
    /// </summary>
    internal static bool EncodesColoursCorrectly(string encoderName, Dictionary<string, string> options, out string detail)
    {
        const int w = 320, h = 192, tolerance = 16;
        // (Y, U, V) of red, green, blue, grey in BT.601 limited range.
        var colours = new (byte Y, byte U, byte V)[] { (81, 90, 240), (145, 54, 34), (41, 240, 110), (128, 128, 128) };
        var i420 = new byte[w * h * 3 / 2];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                i420[y * w + x] = colours[(y / (h / 2)) * 2 + x / (w / 2)].Y;
        for (var y = 0; y < h / 2; y++)
            for (var x = 0; x < w / 2; x++)
            {
                var c = colours[(y / (h / 4)) * 2 + x / (w / 4)];
                i420[w * h + y * (w / 2) + x] = c.U;
                i420[w * h * 5 / 4 + y * (w / 2) + x] = c.V;
            }

        using var enc = new NativeH264Context(encoderName, options, w, h, 1_000_000);
        var decoder = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        var dctx = ffmpeg.avcodec_alloc_context3(decoder);
        var frame = ffmpeg.av_frame_alloc();
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            Check(ffmpeg.avcodec_open2(dctx, decoder, null), "decoder open");
            AVFrame* decoded = null;
            for (var i = 0; i < 6 && decoded == null; i++)
            {
                var data = enc.Encode(i420, forceKeyFrame: i == 0);
                if (data == null) continue;
                Check(ffmpeg.av_new_packet(pkt, data.Length), "av_new_packet");
                Marshal.Copy(data, 0, (IntPtr)pkt->data, data.Length);
                var rc = ffmpeg.avcodec_send_packet(dctx, pkt);
                ffmpeg.av_packet_unref(pkt);
                Check(rc, "avcodec_send_packet");
                if (ffmpeg.avcodec_receive_frame(dctx, frame) == 0) decoded = frame;
            }
            if (decoded == null)
            {
                detail = "no decodable frame";
                return false;
            }
            if (decoded->format != (int)AVPixelFormat.AV_PIX_FMT_YUV420P)
            {
                detail = $"decoder gave pix_fmt {decoded->format}";
                return false;
            }

            for (var block = 0; block < 4; block++)
            {
                var cx = (block % 2) * (w / 2) + w / 4;
                var cy = (block / 2) * (h / 2) + h / 4;
                var y = decoded->data[0][cy * decoded->linesize[0] + cx];
                var u = decoded->data[1][(cy / 2) * decoded->linesize[1] + cx / 2];
                var v = decoded->data[2][(cy / 2) * decoded->linesize[2] + cx / 2];
                var expected = colours[block];
                if (Math.Abs(y - expected.Y) > tolerance || Math.Abs(u - expected.U) > tolerance || Math.Abs(v - expected.V) > tolerance)
                {
                    detail = $"block {block}: got YUV {y},{u},{v} expected {expected.Y},{expected.U},{expected.V}";
                    return false;
                }
            }
            detail = "ok";
            return true;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&dctx);
        }
    }

    private static void Check(int rc, string what)
    {
        if (rc >= 0) return;
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(rc, buf, 256);
        throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringAnsi((IntPtr)buf)} ({rc})");
    }

    public void Dispose()
    {
        fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p);
        fixed (AVFrame** f = &_frame) ffmpeg.av_frame_free(f);
        fixed (AVCodecContext** c = &_ctx) ffmpeg.avcodec_free_context(c);
    }
}
