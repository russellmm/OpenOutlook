#!/usr/bin/env bash
# Unpacks the built .deb into a scratch folder (nothing is installed) and starts the program under a virtual display for a few seconds:
# it must stay running, find libopenpst and the bundled browser, and render the self-test HTML message through that browser.
set -u
fail=0
root="$(cd "$(dirname "$0")/.." && pwd)"
deb=$(ls -t "$root"/publish/openoutlook_*_amd64.deb | head -1)
echo "package: $deb"
dpkg-deb -I "$deb" | grep -E "Package|Version|Depends" | cut -c1-200
echo "contents (top):"; dpkg-deb -c "$deb" | awk '{print $6}' | grep -vE "chromium/.+" | head -20
dpkg-deb -c "$deb" | grep -q "opt/openoutlook/libopenpst.so" || { echo "FAIL: libopenpst.so is not in the package"; fail=1; }
echo "chromium files: $(dpkg-deb -c "$deb" | grep -c 'opt/openoutlook/chromium/')"
rm -rf ~/deb-smoke && mkdir ~/deb-smoke && dpkg -x "$deb" ~/deb-smoke
exe=~/deb-smoke/opt/openoutlook/OpenOutlook.Desktop
ls -la "$exe" ~/deb-smoke/opt/openoutlook/chromium/chrome-headless-shell 2>&1 | cut -c1-120
export HOME_BAK=$HOME OPENOUTLOOK_NO_MIRROR=1 OPENOUTLOOK_SELFTEST_HTML=1 XDG_CONFIG_HOME=$HOME/deb-smoke/cfg XDG_DATA_HOME=$HOME/deb-smoke/data DOTNET_BUNDLE_EXTRACT_BASE_DIR=$HOME/deb-smoke/extract
mkdir -p "$XDG_CONFIG_HOME" "$XDG_DATA_HOME"
# The self-test starts 8 s after the window and logs "[selftest]" when the message is laid out. A slow CI runner once needed 20 s just to
# start the single-file program (2026-10-15, run 38814882482), so wait for the log line (up to 60 s) instead of a fixed time.
timeout 90 xvfb-run -a "$exe" > ~/deb-smoke/run.log 2>&1 &
pid=$!
for i in $(seq 1 60); do
  sleep 1
  if ! kill -0 $pid 2>/dev/null; then echo "program exited early after $i s:"; tail -15 ~/deb-smoke/run.log; fail=1; break; fi
  if grep -rqs "\[selftest\]" ~/deb-smoke/data; then echo "self-test logged after $i s, program still running: OK"; break; fi
done
kill $pid 2>/dev/null; pkill -f "deb-smoke/opt/openoutlook/OpenOutlook.Desktop" 2>/dev/null
wait $pid 2>/dev/null
echo "--- log files ---"
find ~/deb-smoke -name "*.log" -not -name run.log | head
for f in $(find ~/deb-smoke -name "*.log" -not -name run.log | head -3); do echo "## $f"; grep -iE "reader|browser|chrom|self-test|selftest" "$f" | tail -8; done
echo "--- stdout/stderr tail ---"; tail -5 ~/deb-smoke/run.log
if ! grep -rqs "\[selftest\]" ~/deb-smoke/data; then echo "FAIL: the HTML self-test did not run through the bundled browser"; fail=1; fi
[ -x ~/deb-smoke/opt/openoutlook/chromium/chrome-headless-shell ] || { echo "FAIL: bundled browser missing from the package"; fail=1; }
pkill -f chrome-headless-shell 2>/dev/null
exit $fail
