#!/usr/bin/env bash
# Minimal FFmpeg 8.1 LGPL shared build for the DeskShare Windows desktop app (cross-compiled with mingw-w64).
# Contains ONLY what DeskShare calls (see README.md next to this file). Runs inside ubuntu:24.04:
#   docker run --rm -v "$PWD:/src" -v "$PWD/out:/out" ubuntu:24.04 bash /src/scripts/ffmpeg-win/build.sh
# Result: /out/ffmpeg-win64-min.zip (flat: DLLs + LICENSE/README files).
set -euo pipefail

FFMPEG_VER=8.1.2          # same upstream version the macOS job builds
LIBVPX_VER=v1.17.0        # VP8 encoder (WebM recording, tests)
LIBVPL_VER=v2.15.0        # Intel QSV dispatcher (h264_qsv)
NVCODEC_VER=n13.0.19.0    # ffnvcodec headers (h264_nvenc loads the driver at runtime, nothing is linked)
AMF_VER=v1.4.36           # AMD AMF headers (h264_amf loads amfrt64.dll at runtime)

OUT=${OUT:-/out}
W=/build
PREFIX=$W/prefix
HOST=x86_64-w64-mingw32
mkdir -p "$W" "$OUT" "$PREFIX"

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends build-essential gcc-mingw-w64-x86-64-posix g++-mingw-w64-x86-64-posix \
  binutils-mingw-w64-x86-64 nasm pkg-config cmake ninja-build git curl ca-certificates xz-utils zip python3 >/dev/null
update-alternatives --set $HOST-gcc /usr/bin/$HOST-gcc-posix >/dev/null
update-alternatives --set $HOST-g++ /usr/bin/$HOST-g++-posix >/dev/null

export PKG_CONFIG_PATH=$PREFIX/lib/pkgconfig PKG_CONFIG_LIBDIR=$PREFIX/lib/pkgconfig
cd "$W"
GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=advice.detachedHead GIT_CONFIG_VALUE_0=false; export GIT_CONFIG_COUNT GIT_CONFIG_KEY_0 GIT_CONFIG_VALUE_0

# --- header-only deps ---
git clone -q --depth 1 -b $NVCODEC_VER https://github.com/FFmpeg/nv-codec-headers
make -C nv-codec-headers PREFIX=$PREFIX install >/dev/null
git clone -q --depth 1 -b $AMF_VER https://github.com/GPUOpen-LibrariesAndSDKs/AMF
mkdir -p $PREFIX/include && cp -r AMF/amf/public/include $PREFIX/include/AMF

# --- libvpx: VP8 only, static ---
git clone -q --depth 1 -b $LIBVPX_VER https://github.com/webmproject/libvpx
(cd libvpx && CROSS=$HOST- ./configure --target=x86_64-win64-gcc --prefix=$PREFIX --disable-shared --enable-static \
  --disable-vp9 --disable-examples --disable-tools --disable-docs --disable-unit-tests --disable-install-bins \
  --enable-runtime-cpu-detect >/dev/null && make -j"$(nproc)" >/dev/null && make install >/dev/null)

# --- libvpl dispatcher: static ---
git clone -q --depth 1 -b $LIBVPL_VER https://github.com/intel/libvpl
cat > toolchain.cmake <<TC
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_C_COMPILER $HOST-gcc)
set(CMAKE_CXX_COMPILER $HOST-g++)
set(CMAKE_RC_COMPILER $HOST-windres)
set(CMAKE_FIND_ROOT_PATH /usr/$HOST)
TC
cmake -S libvpl -B libvpl/b -G Ninja -DCMAKE_TOOLCHAIN_FILE=$W/toolchain.cmake -DCMAKE_INSTALL_PREFIX=$PREFIX \
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTS=OFF -DBUILD_EXAMPLES=OFF -DBUILD_PREVIEW=OFF \
  -DINSTALL_EXAMPLE_CODE=OFF -DBUILD_DISPATCHER_ONEVPL_EXPERIMENTAL=OFF -DENABLE_WARNINGS_AS_ERRORS=OFF >/dev/null
cmake --build libvpl/b >/dev/null && cmake --install libvpl/b >/dev/null
# The dispatcher is C++: make sure libstdc++ is linked (statically, see the ldflags below).
sed -i 's/^Libs:.*/& -lstdc++/' $PREFIX/lib/pkgconfig/vpl.pc

# winpthread must come from the static libwinpthread.a (extra-libs below), not the -lpthread import library, so no extra DLL is needed.
sed -i "s/-lpthread//g" $PREFIX/lib/pkgconfig/*.pc

# --- FFmpeg ---
curl -sSfL --retry 5 -o ffmpeg.tar.xz https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VER.tar.xz
tar -xJf ffmpeg.tar.xz && cd ffmpeg-$FFMPEG_VER

# Component list = what DeskShare uses (grep of `ffmpeg.` calls and codec names):
#  decode  : h264, vp8 (viewer, FFmpegVideoEndPoint), rawvideo (gdigrab in FfmpegScreenCapturer)
#  encode  : libvpx VP8 (WebM recording, tests), h264_amf/nvenc/qsv/mf (hardware)
#  mux     : webm over the file protocol
#  avdevice: FFmpegInit.Initialise always calls avdevice_register_all(); only gdigrab is registered
#  no avfilter/network/programs; swscale (pixel format conversion) and swresample (loaded by FFmpeg.AutoGen) stay.
./configure --prefix=$PREFIX/ff --arch=x86_64 --target-os=mingw32 --cross-prefix=$HOST- \
  --pkg-config=pkg-config --pkg-config-flags=--static \
  --enable-shared --disable-static --disable-programs --disable-doc --disable-debug --disable-network \
  --disable-autodetect --disable-everything --disable-avfilter --disable-pthreads --enable-w32threads \
  --enable-avcodec --enable-avformat --enable-avutil --enable-swscale --enable-swresample --enable-avdevice \
  --enable-decoder=h264,vp8,rawvideo \
  --enable-parser=h264,vp8 \
  --enable-encoder=libvpx_vp8,h264_amf,h264_nvenc,h264_qsv,h264_mf \
  --enable-muxer=webm --enable-protocol=file --enable-indev=gdigrab \
  --enable-libvpx --enable-amf --enable-ffnvcodec --enable-nvenc --enable-libvpl \
  --enable-mediafoundation --enable-d3d11va --enable-dxva2 \
  --extra-ldflags="-static-libgcc -static-libstdc++" --extra-libs="-Wl,-Bstatic -lwinpthread -Wl,-Bdynamic" \
  > /tmp/configure.log 2>&1 || { tail -40 /tmp/configure.log; tail -60 ffbuild/config.log; exit 1; }
grep -E "^(License|Enabled (encoders|decoders|muxers|indevs|parsers|bsfs|protocols|hwaccels):)" -A3 /tmp/configure.log | head -60 || true
if grep -q -E "^#define CONFIG_(GPL|NONFREE) 1" config.h; then echo "GPL/nonfree enabled!"; exit 1; fi
make -j"$(nproc)" >/tmp/make.log 2>&1 && make install >/dev/null 2>&1 || { tail -40 /tmp/make.log; exit 1; }

# --- package ---
P=$PREFIX/ff
$HOST-strip --strip-unneeded $P/bin/*.dll
mkdir -p $W/pkg/licenses
cp $P/bin/*.dll $W/pkg/
# Fallback: if a DLL still imports winpthread (dynamic), ship it next to the others.
if $HOST-objdump -p $W/pkg/*.dll | grep -q libwinpthread-1.dll; then cp "$(find /usr -name libwinpthread-1.dll | head -1)" $W/pkg/; fi
cp COPYING.LGPLv2.1 $W/pkg/LICENSE-FFmpeg-LGPLv2.1.txt
cp ../libvpx/LICENSE $W/pkg/licenses/libvpx-LICENSE.txt
cp ../libvpl/LICENSE $W/pkg/licenses/libvpl-LICENSE.txt
cp ../AMF/LICENSE.txt $W/pkg/licenses/AMF-LICENSE.txt
{
  echo "FFmpeg $FFMPEG_VER, minimal LGPL shared build for DeskShare (Windows x64)."
  echo "Source: https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VER.tar.xz (unmodified). Built by scripts/ffmpeg-win/build.sh in the DeskShare repo."
  echo "Statically linked: libvpx $LIBVPX_VER, libvpl $LIBVPL_VER; headers only: nv-codec-headers $NVCODEC_VER, AMF $AMF_VER."
  echo "The DLLs are shared libraries and can be replaced by any compatible FFmpeg 8.1 build (LGPL relinking right)."
  echo; echo "configure:"; grep -E "^configuration:|^License:" /tmp/configure.log || head -3 ffbuild/config.log
} > $W/pkg/README.txt
rm -f $OUT/ffmpeg-win64-min.zip
(cd $W/pkg && zip -qr9 $OUT/ffmpeg-win64-min.zip .)

echo "== DLL imports (must be Windows system DLLs only)"
for f in $W/pkg/*.dll; do echo "$(basename $f): $($HOST-objdump -p $f | awk '/DLL Name/{printf "%s ", $3}')"; done
ls -l $W/pkg/*.dll $OUT/ffmpeg-win64-min.zip
