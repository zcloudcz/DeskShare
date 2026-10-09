#!/usr/bin/env bash
# Collects Homebrew ffmpeg@8 shared libraries into "$1" (e.g. publish/ffmpeg) under their major-version
# names (libavcodec.62.dylib — the names FFmpeg.AutoGen dlopen()s) and rewrites install names so the folder
# is relocatable. Third-party dependencies (libvpx, opus, openssl, …) are copied in recursively.
# Used by release.yml on the macOS runner; needs: brew install ffmpeg@8
set -euo pipefail

DEST="$1"
# FFMPEG_PREFIX: an FFmpeg install prefix (source build, used on Intel); otherwise the Homebrew formula
# FFMPEG_FORMULA (default ffmpeg@8). Either way it must be FFmpeg 8, checked below.
PREFIX="${FFMPEG_PREFIX:-$(brew --prefix "${FFMPEG_FORMULA:-ffmpeg@8}")}"
LIBS="avcodec avdevice avfilter avformat avutil swresample swscale postproc"
mkdir -p "$DEST"

# Homebrew libraries reference each other by fully versioned names (libavutil.60.26.102.dylib);
# we ship one file per library under its major name, so every reference is mapped through this.
to_major() { basename "$1" | sed -E 's/^(lib[A-Za-z0-9_+-]+\.[0-9]+)(\.[0-9.]+)?\.dylib$/\1.dylib/'; }

# 1. Copy the FFmpeg libraries, dereferencing the major-version symlink.
for lib in $LIBS; do
  major="$(ls "$PREFIX/lib" | sed -nE "s/^(lib$lib\.[0-9]+\.dylib)$/\1/p" | head -1)"
  [ -n "$major" ] || { echo "skip $lib (not found)"; continue; }
  cp -L "$PREFIX/lib/$major" "$DEST/$major"
done

# FFmpeg.AutoGen 8.1 (used by SIPSorceryMedia.FFmpeg) loads libavcodec.62; any other major version would
# build fine but fail at runtime on the user's Mac, so stop here instead.
if [ ! -f "$DEST/libavcodec.62.dylib" ]; then
  echo "ERROR: FFmpeg 8 required (libavcodec.62.dylib); got: $(ls "$DEST")" >&2; exit 1
fi

# 2. Breadth-first: for every library in DEST, copy its Homebrew dependencies in (by major name) and
#    rewrite the reference to @loader_path. System libraries (/usr/lib, /System) stay as they are.
queue=("$DEST"/*.dylib)
while [ ${#queue[@]} -gt 0 ]; do
  f="${queue[0]}"; queue=("${queue[@]:1}")
  chmod +w "$f"
  install_name_tool -id "@loader_path/$(basename "$f")" "$f"
  for dep in $(otool -L "$f" | awk 'NR>1 {print $1}' | grep -E '^/opt/homebrew|^/usr/local' || true); do
    target="$DEST/$(to_major "$dep")"
    if [ ! -f "$target" ]; then
      cp -L "$dep" "$target"
      queue+=("$target")
    fi
    install_name_tool -change "$dep" "@loader_path/$(basename "$target")" "$f"
  done
done

# 3. Ad-hoc re-sign (install_name_tool invalidates signatures); Velopack re-signs with Developer ID later.
for f in "$DEST"/*.dylib; do codesign --force --sign - "$f" >/dev/null 2>&1 || true; done

echo "Bundled $(ls "$DEST" | wc -l | tr -d ' ') libraries into $DEST:"
ls "$DEST"
# Sanity: nothing may still point into Homebrew.
if otool -L "$DEST"/*.dylib | grep -E '^\s+/opt/homebrew|^\s+/usr/local'; then
  echo "ERROR: unresolved Homebrew references remain" >&2; exit 1
fi
