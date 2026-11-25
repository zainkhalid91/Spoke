#!/usr/bin/env bash
# Renders every wireframe/flow HTML in this folder to png/<name>.png with headless Edge.
# Usage: bash docs/wireframes/render.sh [name-filter]
set -e
here="$(cd "$(dirname "$0")" && pwd -W 2>/dev/null || pwd)"
edge="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
for f in "$here"/*.html; do
  n="$(basename "$f" .html)"
  [[ -n "$1" && "$n" != *$1* ]] && continue
  h=800; [[ "$n" == flows-* ]] && h=1100; [[ "$n" == flows-07 ]] && h=1450
  "$edge" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
    --window-size=1280,$h --virtual-time-budget=4000 \
    --screenshot="$here/png/$n.png" "file:///$f" >/dev/null 2>&1
  echo "rendered $n"
done
