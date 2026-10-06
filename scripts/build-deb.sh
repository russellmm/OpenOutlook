#!/usr/bin/env bash
# Builds publish/openoutlook_<version>_amd64.deb from the output of scripts/package-linux-x64.sh (run that first, on Linux).
# Layout: /opt/openoutlook (program + bundled browser), /usr/bin/openoutlook (launcher), desktop entry and icon under /usr/share.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
version=${1:-0.1.0}
build="$root/publish/linux-x64"
stage="$root/publish/deb-stage"
[[ -x "$build/OpenOutlook.Desktop" ]] || { echo "Run scripts/package-linux-x64.sh first."; exit 1; }

rm -rf "$stage"
install -d "$stage/opt/openoutlook" "$stage/usr/bin" "$stage/usr/share/applications" "$stage/usr/share/icons/hicolor/512x512/apps" "$stage/usr/share/icons/hicolor/256x256/apps" "$stage/DEBIAN"
install -m 755 "$build/OpenOutlook.Desktop" "$stage/opt/openoutlook/OpenOutlook.Desktop"
[[ -f "$build/libopenpst.so" ]] && install -m 755 "$build/libopenpst.so" "$stage/opt/openoutlook/libopenpst.so"
if [[ -d "$build/chromium" ]]; then
  cp -r "$build/chromium" "$stage/opt/openoutlook/chromium"
  find "$stage/opt/openoutlook/chromium" -name chrome-headless-shell -type f -exec chmod 755 {} +
fi
[[ -f "$build/openoutlook-oauth.json" ]] && install -m 644 "$build/openoutlook-oauth.json" "$stage/opt/openoutlook/openoutlook-oauth.json"
install -m 644 LICENSE "$stage/opt/openoutlook/LICENSE"
install -m 644 src/OpenOutlook.Desktop/Assets/openoutlook-512.png "$stage/usr/share/icons/hicolor/512x512/apps/openoutlook.png"
install -m 644 src/OpenOutlook.Desktop/Assets/openoutlook.png "$stage/usr/share/icons/hicolor/256x256/apps/openoutlook.png"
install -m 644 packaging/openoutlook.desktop "$stage/usr/share/applications/openoutlook.desktop"
# EGL_LOG_LEVEL=fatal: WSLg and some VMs have no DRI3, and Mesa then prints two harmless warnings on every start
# XCURSOR_*: under WSLg the default X cursor is tiny and nearly invisible on a high-resolution screen; a larger Adwaita cursor (override with your own XCURSOR_SIZE / XCURSOR_THEME)
printf '#!/bin/sh\nexport EGL_LOG_LEVEL=fatal\nexport XCURSOR_THEME="${XCURSOR_THEME:-Adwaita}"\nexport XCURSOR_SIZE="${XCURSOR_SIZE:-48}"\nexec /opt/openoutlook/OpenOutlook.Desktop "$@"\n' > "$stage/usr/bin/openoutlook"
chmod 755 "$stage/usr/bin/openoutlook"

# Depends: what the bundled browser needs (its own list, from Chrome for Testing) plus the keyring and file-opening helpers
deps="libsecret-1-0, xdg-utils"
if [[ -f third_party/chromium/linux64/deb.deps ]]; then
  chromium_deps=$(grep -v '^[[:space:]]*#' third_party/chromium/linux64/deb.deps | grep -v '^[[:space:]]*$' | grep -v '^wget' | paste -sd, - | sed 's/,/, /g')
  deps="$deps, $chromium_deps"
fi
size=$(du -sk "$stage/opt" "$stage/usr" | awk '{s+=$1} END {print s}')
cat > "$stage/DEBIAN/control" <<CONTROL
Package: openoutlook
Version: $version
Section: mail
Priority: optional
Architecture: amd64
Maintainer: OpenOutlook <russellmarr2012@gmail.com>
Installed-Size: $size
Depends: $deps
Recommends: gnome-keyring | kwalletmanager, libicu74 | libicu72 | libicu70 | libicu67
Description: OpenOutlook mail client
 Reads and edits Outlook PST files, connects Microsoft and Gmail accounts, and keeps
 local mailbox copies so folders and messages open instantly. Includes a bundled
 headless browser for laying out HTML mail.
CONTROL
dpkg-deb --build --root-owner-group "$stage" "$root/publish/openoutlook_${version}_amd64.deb"
echo "Built: $root/publish/openoutlook_${version}_amd64.deb"
