#!/usr/bin/env bash
# Linux checks that go beyond the unit tests (run scripts/wsl-dotnet-test.sh first; it prepares ~/oo_linux):
#   1. the Avalonia headless UI tests on Linux
#   2. the bundled chrome-headless-shell: shared libraries present, starts, prints its version
set -u
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export OPENOUTLOOK_TEST_PST=$HOME/rm2.pst OO_HEADLESS_OUT=$HOME/oo_shots
mkdir -p "$OO_HEADLESS_OUT"
root="$(cd "$(dirname "$0")/.." && pwd)"

echo "== headless UI tests =="
cd ~/oo_linux && dotnet test tests/OpenOutlook.HeadlessTests 2>&1 | grep -E " error |  Failed |Passed!|Failed!" | head -20

echo "== bundled browser =="
shell=$(find "$root/third_party/chromium/linux64" -name chrome-headless-shell -type f | head -1)
echo "binary: ${shell:-none}"
if [ -n "$shell" ]; then
  echo "missing libraries:"; ldd "$shell" | grep "not found" || echo "  none"
  # the executable bit is lost on a Windows drive: run a copy from the Linux side
  cp -r "$(dirname "$shell")" ~/chrome-headless-test && chmod +x ~/chrome-headless-test/chrome-headless-shell
  ~/chrome-headless-test/chrome-headless-shell --version 2>&1 | head -2
  ~/chrome-headless-test/chrome-headless-shell --no-sandbox --screenshot=$HOME/oo_shots/chrome-test.png --window-size=400,200 "data:text/html,<h1>hello</h1>" 2>&1 | tail -2
  ls -la $HOME/oo_shots/chrome-test.png 2>&1
  rm -rf ~/chrome-headless-test
fi
