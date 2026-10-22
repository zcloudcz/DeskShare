# Linux E2E image: real headless sender + real .NET viewer (ClientManager) on a virtual X display.
# Build context is the repo root:  docker build -f docker/e2e/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0

# xvfb              - virtual X display the sender captures (x11grab) and injects input into (XTest)
# xdotool           - lets the test read the real pointer position
# x11-utils         - xdpyinfo (readiness probe) and xmessage (full-screen window in a known colour:
#                     Xvfb grabs the root window as black, so xsetroot cannot be used)
# xclip             - lets the clipboard tests set and read the sender display's real X clipboard
# libxtst6, libx11-6, libxcb*, libasound2 - native libs the sender P/Invokes (libX11, libXtst)
#                     and that FFmpeg's x11grab device links against
RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb xdotool xclip x11-utils libxtst6 libx11-6 \
        libxcb1 libxcb-shm0 libxcb-xfixes0 libxcb-shape0 libasound2 \
        curl xz-utils ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# FFmpeg 8.1 LGPL shared libs, same build the release workflow ships. Both the sender (VP8 encode,
# x11grab) and the viewer (VP8 decode) load them from <app>/ffmpeg.
RUN curl -sSL -o /tmp/ffmpeg.tar.xz "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linux64-lgpl-shared-8.1.tar.xz" \
    && mkdir -p /tmp/ffmpeg-src /opt/ffmpeg \
    && tar -xJf /tmp/ffmpeg.tar.xz -C /tmp/ffmpeg-src --strip-components=1 \
    && cp -a /tmp/ffmpeg-src/lib/*.so* /opt/ffmpeg/ \
    && rm -rf /tmp/ffmpeg.tar.xz /tmp/ffmpeg-src

WORKDIR /src
COPY . .

# Sender and test assembly each get their own copy of the FFmpeg libs next to the binaries.
RUN dotnet publish src/ScreenSenderApp/DeskShare.ScreenSenderApp.csproj -c Release -o /opt/sender \
    && cp -a /opt/ffmpeg /opt/sender/ffmpeg \
    && dotnet build tests/IntegrationTests/DeskShare.IntegrationTests.csproj -c Release -o /opt/e2e \
    && cp -a /opt/ffmpeg /opt/e2e/ffmpeg

ENV DISPLAY=:99 \
    DESKSHARE_E2E=1 \
    DESKSHARE_E2E_SENDER=/opt/sender/DeskShare.ScreenSenderApp.dll

COPY docker/e2e/run.sh /usr/local/bin/run-e2e.sh
RUN sed -i 's/\r$//' /usr/local/bin/run-e2e.sh && chmod +x /usr/local/bin/run-e2e.sh
ENTRYPOINT ["/usr/local/bin/run-e2e.sh"]
