#!/usr/bin/env bash
# Builds native/openpst with ASAN+UBSAN in the WSL filesystem, runs its tests and (optionally) reads the given PST files with the CLI.
#   scripts/wsl-native-asan.sh [file.pst ...]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rm -rf ~/opst_asan && mkdir ~/opst_asan
cp -r "$root/native/openpst/." ~/opst_asan/
rm -rf ~/opst_asan/build-*
cmake -S ~/opst_asan -B ~/opst_asan/b -G Ninja -DCMAKE_BUILD_TYPE=Debug -DOPST_SANITIZE=ON >/dev/null
cmake --build ~/opst_asan/b 2>&1 | tail -2
(cd ~/opst_asan/b && ctest --output-on-failure 2>&1 | tail -6)
for f in "$@"; do
  echo "== $f"
  ~/opst_asan/b/openpst "$f" verify 2>&1 | tail -2
  ~/opst_asan/b/openpst "$f" tree 2>&1 | head -3
  ~/opst_asan/b/openpst "$f" dump 2>&1 | wc -l
done
