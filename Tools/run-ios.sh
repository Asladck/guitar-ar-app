#!/bin/zsh
# Launches the installed app on a connected iPhone and streams its log to this terminal
# (lines from the app start with [GuitarAR]). Ctrl+C stops the log and the app.
# Usage: Tools/run-ios.sh [device-udid]   (defaults to the first connected iPhone)
set -euo pipefail

BUNDLE_ID="com.asladck.guitarar"
DEVICE="${1:-}"
if [[ -z "$DEVICE" ]]; then
  DEVICE="$(xcrun devicectl list devices 2>/dev/null | awk '/iPhone/ && /connected|available/ {for (i = 1; i <= NF; i++) if ($i ~ /^[0-9A-F]{8}-/) {print $i; exit}}')"
fi
[[ -n "$DEVICE" ]] || { echo "No iPhone found. Connect it and check: xcrun devicectl list devices"; exit 1; }

echo "== Launching $BUNDLE_ID on $DEVICE"
xcrun devicectl device process launch --console --terminate-existing --device "$DEVICE" "$BUNDLE_ID"
