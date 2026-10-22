# Wayland E2E image: headless sway (wlroots) + xdg-desktop-portal-wlr + PipeWire, real sender + real .NET viewer.
# Build context is the repo root:  docker build -f docker/e2e-wayland/Dockerfile .
# Arch Linux rather than Debian/Ubuntu: xdg-desktop-portal-wlr 0.7 (Ubuntu 24.04, Debian 13) refuses to start a
# screencast when the compositor cannot offer DMA-BUF, which is always the case without a GPU (pixman renderer).
# xdg-desktop-portal-wlr >= 0.8 with sway >= 1.11 uses ext-image-copy-capture and works with SHM buffers.
FROM archlinux:latest

# Arch is rolling: pin the package set to one Arch Linux Archive snapshot so the test does not change under us.
# Bump the date deliberately (and re-run the test) to move to newer sway/portal/PipeWire versions.
RUN echo 'Server = https://archive.archlinux.org/repos/2026/10/21/$repo/os/$arch' > /etc/pacman.d/mirrorlist

# dotnet-sdk-8.0, aspnet-runtime-8.0 - build and run the sender and the test assembly (it references ASP.NET)
# sway, swaybg                    - headless wlroots compositor and a solid-colour background the test looks for
# xdg-desktop-portal(-wlr)        - the ScreenCast portal the sender talks to over D-Bus (wlr has no RemoteDesktop)
# pipewire, wireplumber           - the video stream; wireplumber links the sender's stream to the portal's node
# gst-plugin-pipewire, gst-plugins-base - what PipeWireScreenCapturer loads (pipewiresrc, videoconvert, appsink)
# libxcb, alsa-lib                - FFmpeg (VP8 encode/decode) links against them even when x11grab is unused
RUN pacman -Syyuu --noconfirm --needed \
        dotnet-sdk-8.0 aspnet-runtime-8.0 sway swaybg xdg-desktop-portal xdg-desktop-portal-wlr pipewire wireplumber \
        gst-plugin-pipewire gst-plugins-base dbus libxcb alsa-lib curl xz ca-certificates \
    && pacman -Scc --noconfirm \
    # Arch gives sway cap_sys_nice; a container without that capability then refuses to exec it.
    && setcap -r /usr/bin/sway \
    # Every desktop has a machine id; the image does not, and the D-Bus client reads it.
    && mkdir -p /var/lib/dbus && dbus-uuidgen > /etc/machine-id && ln -sf /etc/machine-id /var/lib/dbus/machine-id

# FFmpeg 8.1 LGPL shared libs, same build as the X11 E2E image and the release.
RUN curl -sSL -o /tmp/ffmpeg.tar.xz "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linux64-lgpl-shared-8.1.tar.xz" \
    && mkdir -p /tmp/ffmpeg-src /opt/ffmpeg \
    && tar -xJf /tmp/ffmpeg.tar.xz -C /tmp/ffmpeg-src --strip-components=1 \
    && cp -a /tmp/ffmpeg-src/lib/*.so* /opt/ffmpeg/ \
    && rm -rf /tmp/ffmpeg.tar.xz /tmp/ffmpeg-src

WORKDIR /src
COPY . .

RUN dotnet publish src/ScreenSenderApp/DeskShare.ScreenSenderApp.csproj -c Release -o /opt/sender \
    && cp -a /opt/ffmpeg /opt/sender/ffmpeg \
    && dotnet build tests/IntegrationTests/DeskShare.IntegrationTests.csproj -c Release -o /opt/e2e \
    && cp -a /opt/ffmpeg /opt/e2e/ffmpeg \
    # Unit tests for the Linux-only parts (file modes, symlinks) that the Windows CI job skips.
    && dotnet build tests/UnitTests/DeskShare.UnitTests.csproj -c Release -o /opt/unit

# A Wayland-only session: no DISPLAY, so the sender must pick the portal path on its own.
ENV XDG_SESSION_TYPE=wayland \
    XDG_CURRENT_DESKTOP=sway \
    XDG_RUNTIME_DIR=/tmp/xdg \
    WLR_BACKENDS=headless \
    WLR_RENDERER=pixman \
    WLR_LIBINPUT_NO_DEVICES=1 \
    DESKSHARE_E2E_WAYLAND=1 \
    DESKSHARE_E2E_SENDER=/opt/sender/DeskShare.ScreenSenderApp.dll

COPY docker/e2e-wayland/sway.config /etc/deskshare-e2e/sway.config
COPY docker/e2e-wayland/xdpw.config /root/.config/xdg-desktop-portal-wlr/config
COPY docker/e2e-wayland/portals.conf /root/.config/xdg-desktop-portal/portals.conf
COPY docker/e2e-wayland/run.sh /usr/local/bin/run-e2e.sh
RUN sed -i 's/\r$//' /usr/local/bin/run-e2e.sh /etc/deskshare-e2e/sway.config /root/.config/xdg-desktop-portal-wlr/config /root/.config/xdg-desktop-portal/portals.conf \
    && chmod +x /usr/local/bin/run-e2e.sh
ENTRYPOINT ["dbus-run-session", "--", "/usr/local/bin/run-e2e.sh"]
