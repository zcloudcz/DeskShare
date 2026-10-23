using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace DeskShare.Desktop.Shared.Services.Recording;

/// <summary>
/// One .webm file at ONE resolution: BGRA frames in, VP8 (libvpx, part of the LGPL FFmpeg build; no GPL encoder) in a WebM
/// container out. Written by hand against FFmpeg.AutoGen like Core's NativeH264Context.
/// </summary>
/// <remarks>
/// Not thread safe: one writer thread owns it. The frame number is the presentation time, so the caller must write exactly
/// one frame per time slot (see <see cref="FrameRateNormalizer"/>); that is what makes the frame rate constant.
/// </remarks>
internal sealed unsafe class WebmSegmentWriter : ISegmentWriter
{
    private readonly int _srcWidth, _srcHeight; // size of the frames handed in
    private readonly int _width, _height;       // encoded size: rounded DOWN to even (4:2:0 chroma needs it)
    private const int SwsBilinear = 2; // libswscale SWS_BILINEAR (AutoGen 8 has no constant for it)
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVStream* _stream;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _sws;
    private bool _headerWritten, _finished;

    public string Path { get; }

    public WebmSegmentWriter(string path, int width, int height, int fps)
    {
        if (width < 2 || height < 2) throw new ArgumentException("The frame is too small to record.");
        Path = path;
        _srcWidth = width;
        _srcHeight = height;
        _width = width & ~1;
        _height = height & ~1;

        try
        {
            var encoder = ffmpeg.avcodec_find_encoder_by_name("libvpx");
            if (encoder == null)
                throw new InvalidOperationException("This FFmpeg build has no VP8 encoder (libvpx).");

            AVFormatContext* format = null;
            Check(ffmpeg.avformat_alloc_output_context2(&format, null, "webm", path), "avformat_alloc_output_context2");
            _format = format;

            _codec = ffmpeg.avcodec_alloc_context3(encoder);
            _codec->width = _width;
            _codec->height = _height;
            _codec->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            _codec->time_base = new AVRational { num = 1, den = fps };
            _codec->framerate = new AVRational { num = fps, den = 1 };
            _codec->gop_size = fps * 2;
            _codec->max_b_frames = 0;
            // About 0.1 bit per pixel and frame is plenty for screen content; clamped so tiny and huge screens stay sane.
            _codec->bit_rate = Math.Clamp((long)_width * _height * fps / 10, 500_000L, 8_000_000L);
            // WebM keeps the codec setup in the file header, not in every packet.
            if ((_format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                _codec->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "deadline", "realtime", 0); // the encoder must keep up with live frames
            ffmpeg.av_dict_set(&options, "cpu-used", "8", 0);
            ffmpeg.av_dict_set(&options, "lag-in-frames", "0", 0);
            var rc = ffmpeg.avcodec_open2(_codec, encoder, &options);
            ffmpeg.av_dict_free(&options);
            Check(rc, "avcodec_open2");

            _stream = ffmpeg.avformat_new_stream(_format, null);
            Check(ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codec), "avcodec_parameters_from_context");
            _stream->time_base = _codec->time_base;

            Check(ffmpeg.avio_open(&_format->pb, path, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
            Check(ffmpeg.avformat_write_header(_format, null), "avformat_write_header");
            _headerWritten = true;

            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            _frame->width = _width;
            _frame->height = _height;
            Check(ffmpeg.av_frame_get_buffer(_frame, 32), "av_frame_get_buffer");
            _packet = ffmpeg.av_packet_alloc();

            _sws = ffmpeg.sws_getContext(_width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                _width, _height, AVPixelFormat.AV_PIX_FMT_YUV420P, SwsBilinear, null, null, null);
            if (_sws == null) throw new InvalidOperationException("sws_getContext failed.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Converts one tightly packed BGRA frame (srcWidth * srcHeight * 4 bytes) and encodes it as the next slot.</summary>
    public void WriteFrame(byte[] bgra, long pts)
    {
        if (bgra.Length < (long)_srcWidth * _srcHeight * 4)
            throw new ArgumentException("The pixel buffer is shorter than width x height x 4.", nameof(bgra));
        Check(ffmpeg.av_frame_make_writable(_frame), "av_frame_make_writable");
        fixed (byte* pixels = bgra)
        {
            // The source rows are srcWidth wide; when the width is odd the last column is simply not encoded.
            var src = new byte_ptrArray4 { [0] = pixels };
            var srcStride = new int_array4 { [0] = _srcWidth * 4 };
            ffmpeg.sws_scale(_sws, src, srcStride, 0, _height, _frame->data, _frame->linesize);
        }
        Encode(pts);
    }

    /// <summary>Encodes the previous picture again as the next slot (nothing new arrived in that time).</summary>
    public void RepeatLast(long pts) => Encode(pts);

    private void Encode(long pts)
    {
        _frame->pts = pts;
        Check(ffmpeg.avcodec_send_frame(_codec, _frame), "avcodec_send_frame");
        DrainPackets();
    }

    private void DrainPackets()
    {
        while (true)
        {
            var rc = ffmpeg.avcodec_receive_packet(_codec, _packet);
            if (rc == ffmpeg.AVERROR(ffmpeg.EAGAIN) || rc == ffmpeg.AVERROR_EOF) return;
            Check(rc, "avcodec_receive_packet");
            ffmpeg.av_packet_rescale_ts(_packet, _codec->time_base, _stream->time_base);
            _packet->stream_index = _stream->index;
            Check(ffmpeg.av_interleaved_write_frame(_format, _packet), "av_interleaved_write_frame");
        }
    }

    /// <summary>
    /// Flushes the encoder, writes the file trailer (without it a player cannot seek) and closes the file. Throws when ANY of
    /// these steps fails (full disk, ...): the caller must then not report the file as saved. The native resources are
    /// released in every case, so no <see cref="Dispose"/> is needed afterwards.
    /// </summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        try
        {
            if (_codec == null || !_headerWritten) throw new InvalidOperationException("The recording file was never started.");
            Check(ffmpeg.avcodec_send_frame(_codec, null), "avcodec_send_frame(flush)"); // null = end of stream
            DrainPackets();
            Check(ffmpeg.av_write_trailer(_format), "av_write_trailer");
            Check(ffmpeg.avio_closep(&_format->pb), "avio_closep"); // the data is flushed to disk here: errors show up here
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Frees the native objects. Does NOT finish the file: use <see cref="Finish"/> for that (Dispose alone is for the error path).</summary>
    public void Dispose()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p);
        fixed (AVFrame** f = &_frame) ffmpeg.av_frame_free(f);
        fixed (AVCodecContext** c = &_codec) ffmpeg.avcodec_free_context(c);
        if (_format != null)
        {
            if (_format->pb != null) ffmpeg.avio_closep(&_format->pb);
            ffmpeg.avformat_free_context(_format);
            _format = null;
        }
    }

    private static void Check(int rc, string what)
    {
        if (rc >= 0) return;
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(rc, buf, 256);
        throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringAnsi((IntPtr)buf)} ({rc})");
    }
}
