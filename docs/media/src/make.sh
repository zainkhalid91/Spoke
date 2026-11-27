#!/usr/bin/env bash
# Rebuilds docs/media/*.png: renders the wheels with Spoke's offscreen renderer, then lays them out with compose.py.
# Usage: bash docs/media/src/make.sh path/to/Spoke.exe [photo.jpg for the drop wheel]
set -e
here="$(cd "$(dirname "$0")" && pwd)"
exe="$1"; photo="${2:-$here/photo.jpg}"
out="$(mktemp -d)"
cd "$here"
"$exe" --render "$out/hero.png"   --config demo.json   --theme Aurora --hover 2 --transparent --scale 1.5
"$exe" --render "$out/simple.png" --config simple.json --theme Aurora --hover 1 --transparent --scale 1.5
"$exe" --render "$out/ring.png"   --config demo.json   --theme Aurora --ring 4 --ring-hover 2 --transparent --scale 1.5
"$exe" --render "$out/drop.png"   --config simple.json --theme Aurora --drop "$photo" --hover 2 --transparent --scale 1.5
for t in Aurora Ember Paper Graphite Onyx; do
  "$exe" --render "$out/t-$t.png" --config simple.json --theme $t --hover 0 --transparent --scale 1.5
done
python compose.py "$out" "$here/.."
