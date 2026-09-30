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
if [[ -f "$oauth_source" ]]; then
  install -m 644 "$oauth_source" "$build_dir/openoutlook-oauth.json"
  install -m 644 "$oauth_source" "$publish_root/openoutlook-oauth.json"
  install -m 644 "$oauth_source" "$package_dir/openoutlook-oauth.json"
  package_files+=(openoutlook-oauth.json)
fi

tar -czf "$publish_root/OpenOutlook-linux-x64.tar.gz" -C "$package_dir" "${package_files[@]}"
printf 'Updated executable: %s\nPortable archive: %s\n' \
  "$publish_root/OpenOutlook.Desktop" "$publish_root/OpenOutlook-linux-x64.tar.gz"
