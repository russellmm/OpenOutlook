#!/usr/bin/env bash
# Unpacks the built .deb into a scratch folder (nothing is installed) and starts the program under a virtual display for a few seconds:
# it must stay running, find libopenpst and the bundled browser, and render the self-test HTML message through that browser.
set -u
root="$(cd "$(dirname "$0")/.." && pwd)"
deb=$(ls -t "$root"/publish/openoutlook_*_amd64.deb | head -1)
echo "package: $deb"
dpkg-deb -I "$deb" | grep -E "Package|Version|Depends" | cut -c1-200
echo "contents (top):"; dpkg-deb -c "$deb" | awk '{print $6}' | grep -vE "chromium/.+" | head -20
echo "chromium files: $(dpkg-deb -c "$deb" | grep -c 'opt/openoutlook/chromium/')"
rm -rf ~/deb-smoke && mkdir ~/deb-smoke && dpkg -x "$deb" ~/deb-smoke
exe=~/deb-smoke/opt/openoutlook/OpenOutlook.Desktop
ls -la "$exe" ~/deb-smoke/opt/openoutlook/chromium/chrome-headless-shell 2>&1 | cut -c1-120
export HOME_BAK=$HOME OPENOUTLOOK_NO_MIRROR=1 OPENOUTLOOK_SELFTEST_HTML=1 XDG_CONFIG_HOME=$HOME/deb-smoke/cfg XDG_DATA_HOME=$HOME/deb-smoke/data DOTNET_BUNDLE_EXTRACT_BASE_DIR=$HOME/deb-smoke/extract
mkdir -p "$XDG_CONFIG_HOME" "$XDG_DATA_HOME"
timeout 25 xvfb-run -a "$exe" > ~/deb-smoke/run.log 2>&1 &
pid=$!
sleep 18
if kill -0 $pid 2>/dev/null; then echo "program still running after 18 s: OK"; else echo "program exited early:"; tail -15 ~/deb-smoke/run.log; fi
wait $pid 2>/dev/null
echo "--- log files ---"
find ~/deb-smoke -name "*.log" -not -name run.log | head
for f in $(find ~/deb-smoke -name "*.log" -not -name run.log | head -3); do echo "## $f"; grep -iE "reader|browser|chrom|self-test|selftest" "$f" | tail -8; done
echo "--- stdout/stderr tail ---"; tail -5 ~/deb-smoke/run.log
pkill -f chrome-headless-shell 2>/dev/null; true
