#!/usr/bin/env bash
# Starts the virtual display, waits for the signaling server, then runs the E2E tests.
set -euo pipefail

# A restarted container keeps /tmp, and a stale lock makes Xvfb refuse to start.
rm -f /tmp/.X99-lock /tmp/.X100-lock
Xvfb "$DISPLAY" -screen 0 1280x720x24 -nolisten tcp &

# xdpyinfo fails until the X server accepts connections.
timeout 30 bash -c "until xdpyinfo >/dev/null 2>&1; do sleep 0.2; done"

# Second display for the 1920x1080 perf run (the sender child process is started with DISPLAY=:100). Started only
# after the first is up: two Xvfb racing to create /tmp/.X11-unix make one of them fail.
Xvfb :100 -screen 0 1920x1080x24 -nolisten tcp &
timeout 30 bash -c "until DISPLAY=:100 xdpyinfo >/dev/null 2>&1; do sleep 0.2; done"

# Paint each screen a known colour: a borderless full-screen window. (xsetroot -solid does not work here: Xvfb
# keeps the root window black in screen grabs, so the sender would capture black.) Text colour equals the
# background so no label is visible; the video test looks for this colour in the received frames.
# A second window "alt" (different colour) is mapped first and so sits underneath "base"; the perf tests switch the
# visible colour by raising one window with xdotool (no window manager, so this is a plain restack).
for d in "$DISPLAY" :100; do
  size=1280x720; [ "$d" = ":100" ] && size=1920x1080
  DISPLAY=$d xmessage -title alt -geometry $size+0+0 -bg '#b05030' -fg '#b05030' -buttons '' x &
  timeout 30 bash -c "until DISPLAY=$d xdotool search --name '^alt\$' >/dev/null 2>&1; do sleep 0.2; done"
  DISPLAY=$d xmessage -title base -geometry $size+0+0 -bg '#3070b0' -fg '#3070b0' -buttons '' x &
  timeout 30 bash -c "until DISPLAY=$d xdotool search --name '^base\$' >/dev/null 2>&1; do sleep 0.2; done"
done

timeout 120 bash -c "until curl -sf http://signaling:5151/health >/dev/null; do sleep 1; done"

exec dotnet test /opt/e2e/DeskShare.IntegrationTests.dll --filter "${E2E_FILTER:-FullyQualifiedName~E2E}" --logger "console;verbosity=detailed"
