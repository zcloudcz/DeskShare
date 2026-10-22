#!/usr/bin/env bash
# Runs inside dbus-run-session: starts PipeWire, headless sway and (via D-Bus activation) the portal, then the
# Wayland E2E tests.
set -euo pipefail

rm -rf "$XDG_RUNTIME_DIR" && mkdir -m 700 -p "$XDG_RUNTIME_DIR"

pipewire &
timeout 30 bash -c "until [ -S $XDG_RUNTIME_DIR/pipewire-0 ]; do sleep 0.2; done"
wireplumber &

sway -c /etc/deskshare-e2e/sway.config &
timeout 30 bash -c "until ls $XDG_RUNTIME_DIR/wayland-* 2>/dev/null | grep -qv lock; do sleep 0.2; done"
export WAYLAND_DISPLAY
WAYLAND_DISPLAY=$(basename "$(ls $XDG_RUNTIME_DIR/wayland-* | grep -v lock | head -1)")

# xdg-desktop-portal-wlr only captures on damage and shares one capture per output between sessions, so a
# sender that joins while the screen is perfectly still may never get a first frame. A real desktop always
# has some movement; here the pointer nudges back and forth near the centre (the test samples elsewhere).
(while true; do
  swaymsg -q seat - cursor move 1 1 || true; sleep 0.3
  swaymsg -q seat - cursor move -- -1 -1 || true; sleep 0.3
done) &

# The portals are started by D-Bus activation and need to know where the compositor is.
dbus-update-activation-environment WAYLAND_DISPLAY XDG_CURRENT_DESKTOP XDG_SESSION_TYPE XDG_RUNTIME_DIR

timeout 120 bash -c "until curl -sf http://signaling:5151/health >/dev/null; do sleep 1; done"

# Linux-only unit tests first (restore-token file security); set -e stops the run when they fail.
dotnet test /opt/unit/DeskShare.UnitTests.dll --filter "FullyQualifiedName~WaylandPortal"

exec dotnet test /opt/e2e/DeskShare.IntegrationTests.dll --filter "${E2E_FILTER:-FullyQualifiedName~WaylandE2E}" --logger "console;verbosity=detailed"
