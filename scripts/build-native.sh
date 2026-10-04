#!/usr/bin/env bash
# Builds native/openpst and copies libopenpst.so into src/OpenOutlook.Desktop/runtimes/<rid>/native/
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-linux-x64}"
bld="$root/native/openpst/build-$rid"
cmake -S "$root/native/openpst" -B "$bld" -G Ninja -DCMAKE_BUILD_TYPE=Release -DOPST_BUILD_TOOLS=OFF
cmake --build "$bld"
dest="$root/src/OpenOutlook.Desktop/runtimes/$rid/native"
mkdir -p "$dest"
cp -f "$bld"/libopenpst.so* "$dest/"
(cd "$bld" && ctest --output-on-failure) || true
echo "native library -> $dest"
