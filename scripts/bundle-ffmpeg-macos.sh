#!/usr/bin/env bash
# Collects Homebrew ffmpeg@8 shared libraries into "$1" (e.g. publish/ffmpeg) under their major-version
# names (libavcodec.62.dylib — the names FFmpeg.AutoGen dlopen()s) and rewrites install names so the folder
# is relocatable. Third-party dependencies (libvpx, opus, openssl, …) are bundled by dylibbundler.
# Used by release.yml on the macOS runner; needs: brew install ffmpeg@8 dylibbundler
set -euo pipefail

DEST="$1"
PREFIX="$(brew --prefix ffmpeg@8)"
LIBS="avcodec avdevice avfilter avformat avutil swresample swscale postproc"
mkdir -p "$DEST"

# 1. Copy each library once, dereferencing the major-version symlink (libavcodec.62.dylib → real file).
for lib in $LIBS; do
  src="$PREFIX/lib/lib$lib.$(ls "$PREFIX/lib" | sed -nE "s/^lib$lib\.([0-9]+)\.dylib$/\1/p" | head -1).dylib"
  [ -f "$src" ] || { echo "skip $lib (not found)"; continue; }
  cp -L "$src" "$DEST/$(basename "$src")"
  chmod +w "$DEST/$(basename "$src")"
done

# 2. Intra-FFmpeg references use fully versioned names (libavutil.60.26.102.dylib); point them at the
#    major-named copies next to each other so nothing is pulled in twice.
to_major() { basename "$1" | sed -E 's/^(lib[a-z]+\.[0-9]+)(\.[0-9.]+)?\.dylib$/\1.dylib/'; }
for f in "$DEST"/*.dylib; do
  install_name_tool -id "@loader_path/$(basename "$f")" "$f"
  otool -L "$f" | awk 'NR>1 {print $1}' \
    | grep -E '/lib(avcodec|avdevice|avfilter|avformat|avutil|swresample|swscale|postproc)\.[0-9]' \
    | while read -r dep; do
        install_name_tool -change "$dep" "@loader_path/$(to_major "$dep")" "$f"
      done
done

# 3. Bundle everything else the libraries link against (-of: overwrite files, never the directory).
ARGS=""
for f in "$DEST"/*.dylib; do ARGS="$ARGS -x $f"; done
dylibbundler -of -b $ARGS -d "$DEST" -p @loader_path/ -s "$(brew --prefix)/lib" -s "$PREFIX/lib"

# 4. Ad-hoc re-sign (install_name_tool invalidates signatures); Velopack re-signs with Developer ID later.
for f in "$DEST"/*.dylib; do codesign --force --sign - "$f" >/dev/null 2>&1 || true; done

echo "Bundled $(ls "$DEST" | wc -l | tr -d ' ') libraries into $DEST:"
ls "$DEST"
