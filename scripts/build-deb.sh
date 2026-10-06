#!/usr/bin/env bash
# Builds publish/openoutlook_<version>_amd64.deb from the output of scripts/package-linux-x64.sh (run that first, on Linux).
# Layout: /opt/openoutlook (program + bundled browser), /usr/bin/openoutlook (launcher), desktop entry and icon under /usr/share.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
version=${1:-0.1.0}
build="$root/publish/linux-x64"
stage="${OO_DEB_STAGE:-$root/publish/deb-stage}"   # set OO_DEB_STAGE to a native Linux path when the repo sits on NTFS (dpkg-deb rejects 777 control dir)
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
# Ubuntu 23.10+ blocks unprivileged user namespaces unless AppArmor allows them; without this the bundled browser crashes (SIGTRAP)
install -d "$stage/etc/apparmor.d"
install -m 644 packaging/openoutlook-chromium.apparmor "$stage/etc/apparmor.d/openoutlook-chromium"
cat > "$stage/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if [ -f /etc/apparmor.d/openoutlook-chromium ] && command -v apparmor_parser >/dev/null 2>&1; then
  apparmor_parser -r /etc/apparmor.d/openoutlook-chromium 2>/dev/null || true
fi
exit 0
POSTINST
cat > "$stage/DEBIAN/postrm" <<'POSTRM'
#!/bin/sh
set -e
if [ "$1" = remove ] || [ "$1" = purge ]; then
  command -v apparmor_parser >/dev/null 2>&1 && apparmor_parser -R /etc/apparmor.d/openoutlook-chromium 2>/dev/null || true
fi
exit 0
POSTRM
chmod 755 "$stage/DEBIAN/postinst" "$stage/DEBIAN/postrm"
# EGL_LOG_LEVEL=fatal: WSLg and some VMs have no DRI3, and Mesa then prints two harmless warnings on every start
cat > "$stage/usr/bin/openoutlook" <<'LAUNCHER'
#!/bin/sh
export EGL_LOG_LEVEL=fatal
# Under WSL there is no Linux browser: sign-in pages and links open in the default Windows browser
if [ -z "$BROWSER" ] && grep -qi microsoft /proc/version 2>/dev/null && command -v explorer.exe >/dev/null 2>&1; then
  export BROWSER=/opt/openoutlook/wsl-open
fi
exec /opt/openoutlook/OpenOutlook.Desktop "$@"
LAUNCHER
printf '#!/bin/sh\n# opens a link in the default Windows browser (WSL)\nexec explorer.exe "$1"\n' > "$stage/opt/openoutlook/wsl-open"
chmod 755 "$stage/opt/openoutlook/wsl-open"
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
