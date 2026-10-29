#!/usr/bin/env bash
# Builds the public Linux release files from the tested builds in publish/, without openoutlook-oauth.json (owner 2026-10-29). Usage: MSYS_NO_PATHCONV=1 wsl bash scripts/release-pack-linux.sh (edit v= first).
set -euo pipefail
v=0.1.84
repo=/mnt/f/Claude/OpenOutlook
win=/mnt/f/Claude/OpenOutlook_win
example=$repo/src/OpenOutlook.Desktop/openoutlook-oauth.example.json
work=$HOME/oo_release
out=$repo/publish/release-$v
rm -rf "$work" && mkdir -p "$work" "$out"

# Linux tar.gz: unpack, drop the oauth file, add the template, pack again
t=$work/tar && mkdir -p "$t"
tar -xzf "$repo/publish/OpenOutlook-linux-x64.tar.gz" -C "$t"
find "$t" -name openoutlook-oauth.json -print -delete
cp "$example" "$t/"                                   # the archive has no top folder: files sit at its root
tar -czf "$out/OpenOutlook-$v-linux-x64.tar.gz" -C "$t" .

# Linux .deb: unpack with control files, drop the oauth file (and its md5sums line), add the template, rebuild
d=$work/deb && mkdir -p "$d"
dpkg-deb -R "$repo/publish/openoutlook_${v}_amd64.deb" "$d"
rm -f "$d/opt/openoutlook/openoutlook-oauth.json"
[[ -f "$d/DEBIAN/md5sums" ]] && sed -i '/openoutlook-oauth.json/d' "$d/DEBIAN/md5sums"
[[ -f "$d/DEBIAN/conffiles" ]] && sed -i '/openoutlook-oauth.json/d' "$d/DEBIAN/conffiles"
install -m 644 "$example" "$d/opt/openoutlook/openoutlook-oauth.example.json"
dpkg-deb --root-owner-group -Zxz --build "$d" "$out/openoutlook_${v}_amd64.deb" >/dev/null

# checks: no oauth file anywhere, sizes, checksums
! tar -tzf "$out/OpenOutlook-$v-linux-x64.tar.gz" | grep -q 'openoutlook-oauth.json$'
! dpkg-deb -c "$out/openoutlook_${v}_amd64.deb" | grep -q 'openoutlook-oauth.json$'
dpkg-deb -f "$out/openoutlook_${v}_amd64.deb" Package Version
ls -la "$out"
echo "no oauth file in any package: OK"
