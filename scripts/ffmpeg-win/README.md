# Minimal FFmpeg for the Windows desktop app

`build.sh` cross-compiles (mingw-w64, in `ubuntu:24.04`) FFmpeg 8.1 as **LGPL shared** DLLs with only what DeskShare calls,
about 9 MB instead of the 159 MB generic BtbN build. Windows only: Linux and macOS packaging still uses the full libraries
(they need avfilter for the crop filter of the screen capturer).

| Part | Why |
|---|---|
| decoders `h264`, `vp8` | viewer (`VideoSink` via `FFmpegVideoEndPoint`), `NativeH264Context` colour probe |
| decoder `rawvideo`, indev `gdigrab` | `FfmpegScreenCapturer` on Windows (test only; production uses DXGI/GDI) |
| encoders `h264_amf`, `h264_nvenc`, `h264_qsv`, `h264_mf` | `HardwareH264Encoder` (software NV12 frames) |
| encoder `libvpx_vp8`, muxer `webm`, protocol `file` | `WebmSegmentWriter` (recording), FFmpeg VP8 path in tests |
| parsers `h264`, `vp8`, swscale, swresample, avdevice | `sws_scale`; `FFmpegInit.Initialise` always calls `avdevice_register_all` (needs `avdevice-62.dll`) |

No avfilter, no network, no programs. libvpx (VP8 only) and the Intel libvpl dispatcher are linked statically; the AMF and
nv-codec headers are header-only (the vendor driver DLLs are loaded at runtime). Component list changes belong in `build.sh`
next to the `./configure` call; if you start using another FFmpeg codec/format/function in the app, add it there.

Rebuild: Actions > `ffmpeg-windows` > Run workflow (also runs on pushes touching this folder and monthly), or locally

    docker run --rm -v "$PWD:/src" -v "$PWD/out:/out" ubuntu:24.04 bash /src/scripts/ffmpeg-win/build.sh

The result is the artifact `ffmpeg-win64-min` (`ffmpeg-win64-min.zip` with DLLs, LICENSE and README). It is fetched by
`src/DeskShare.DesktopAvalonia/download-ffmpeg.ps1` (default; `-Full` falls back to the BtbN build).
