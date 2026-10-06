#!/usr/bin/env bash
# Builds the Linux release inside WSL (a copy of the repo on the Linux filesystem, so the Windows obj folders are never touched):
# native library, self-contained publish, portable tar.gz and .deb. The results are copied back to <repo>/publish.
#   MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/wsl-package.sh [version]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version=${1:-0.1.0}
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
work=$HOME/oo_pack
rm -rf "$work" && mkdir -p "$work"
cd "$root"
tar --exclude=bin --exclude=obj --exclude='native/openpst/build-*' --exclude=.git --exclude=publish --exclude=runtimes \
    --exclude='*.pst' --exclude='*.zip' --exclude=.local --exclude=third_party --exclude=.secrets --exclude='./*.png' -cf - . | tar -xf - -C "$work"
cp "$root"/src/OpenOutlook.Desktop/Assets/*.png "$work"/src/OpenOutlook.Desktop/Assets/     # the tar above skips png files (root screenshots); the app icon is needed
mkdir -p "$work/third_party/chromium"
if [[ -d "$root/third_party/chromium/linux64" ]]; then cp -r "$root/third_party/chromium/linux64" "$work/third_party/chromium/linux64"; fi
cd "$work"
bash scripts/build-native.sh 2>&1 | tail -3
bash scripts/package-linux-x64.sh 2>&1 | tail -4
bash scripts/build-deb.sh "$version" 2>&1 | tail -3
mkdir -p "$root/publish"
cp publish/OpenOutlook-linux-x64.tar.gz "$root/publish/"
cp publish/openoutlook_"$version"_amd64.deb "$root/publish/"
ls -la "$root/publish"/OpenOutlook-linux-x64.tar.gz "$root/publish"/openoutlook_"$version"_amd64.deb
