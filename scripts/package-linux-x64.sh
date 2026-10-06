#!/usr/bin/env bash
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_root"

publish_root="$project_root/publish"
build_dir="$publish_root/linux-x64"
package_dir="$publish_root/OpenOutlook-linux-x64"
oauth_source="$project_root/src/OpenOutlook.Desktop/openoutlook-oauth.json"

dotnet publish src/OpenOutlook.Desktop/OpenOutlook.Desktop.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false -p:NuGetAudit=false --ignore-failed-sources \
  -m:1 -p:UseSharedCompilation=false \
  -o "$build_dir"

mkdir -p "$package_dir"
install -m 755 "$build_dir/OpenOutlook.Desktop" "$publish_root/OpenOutlook.Desktop"
install -m 755 "$build_dir/OpenOutlook.Desktop" "$package_dir/OpenOutlook.Desktop"
install -m 644 packaging/README-linux-x64.txt "$publish_root/README.txt"
install -m 644 packaging/README-linux-x64.txt "$build_dir/README.txt"
install -m 644 packaging/README-linux-x64.txt "$package_dir/README.txt"
install -m 644 LICENSE "$package_dir/LICENSE"

package_files=(OpenOutlook.Desktop README.txt LICENSE)
# the bundled browser (scripts/fetch_chromium.py linux64) and the desktop integration files travel with the program
if [[ -d "$build_dir/chromium" ]]; then
  find "$build_dir/chromium" -name chrome-headless-shell -type f -exec chmod 755 {} +
  rm -rf "$package_dir/chromium" && cp -r "$build_dir/chromium" "$package_dir/chromium"
  package_files+=(chromium)
else
  echo "Note: no bundled browser (run python3 scripts/fetch_chromium.py linux64 before publishing); HTML mail falls back to an installed Chrome or Chromium."
fi
install -m 644 packaging/openoutlook.desktop "$package_dir/openoutlook.desktop"
install -m 644 src/OpenOutlook.Desktop/Assets/openoutlook-512.png "$package_dir/openoutlook.png"
package_files+=(openoutlook.desktop openoutlook.png)
if [[ -f "$oauth_source" ]]; then
  install -m 644 "$oauth_source" "$build_dir/openoutlook-oauth.json"
  install -m 644 "$oauth_source" "$publish_root/openoutlook-oauth.json"
  install -m 644 "$oauth_source" "$package_dir/openoutlook-oauth.json"
  package_files+=(openoutlook-oauth.json)
fi

tar -czf "$publish_root/OpenOutlook-linux-x64.tar.gz" -C "$package_dir" "${package_files[@]}"
printf 'Updated executable: %s\nPortable archive: %s\n' \
  "$publish_root/OpenOutlook.Desktop" "$publish_root/OpenOutlook-linux-x64.tar.gz"
