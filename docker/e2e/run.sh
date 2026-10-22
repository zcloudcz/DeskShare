#!/usr/bin/env bash
# Starts the virtual display, waits for the signaling server, then runs the E2E tests.
set -euo pipefail

# A restarted container keeps /tmp, and a stale lock makes Xvfb refuse to start.
rm -f /tmp/.X99-lock
Xvfb "$DISPLAY" -screen 0 1280x720x24 -nolisten tcp &

# xdpyinfo fails until the X server accepts connections.
timeout 30 bash -c "until xdpyinfo >/dev/null 2>&1; do sleep 0.2; done"

# Paint the screen a known colour: a borderless full-screen window. (xsetroot -solid does not work here: Xvfb
# keeps the root window black in screen grabs, so the sender would capture black.) Text colour equals the
# background so no label is visible; the video test looks for this colour in the received frames.
xmessage -geometry 1280x720+0+0 -bg '#3070b0' -fg '#3070b0' -buttons '' x &
timeout 30 bash -c "until xdotool search --class xmessage >/dev/null 2>&1; do sleep 0.2; done"

timeout 120 bash -c "until curl -sf http://signaling:5151/health >/dev/null; do sleep 1; done"

exec dotnet test /opt/e2e/DeskShare.IntegrationTests.dll --filter "${E2E_FILTER:-FullyQualifiedName~E2E}" --logger "console;verbosity=detailed"
