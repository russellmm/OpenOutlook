#!/usr/bin/env bash
# Copies the repo into the WSL filesystem, builds the native lib + .NET solution there and runs the tests on Linux.
set -e
root="$(cd "$(dirname "$0")/.." && pwd)"
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
rm -rf ~/oo_linux && mkdir ~/oo_linux
cd "$root"
tar --exclude=bin --exclude=obj --exclude='native/openpst/build-*' --exclude=.git --exclude=publish --exclude=runtimes --exclude='*.png' --exclude='*.zip' -cf - . | tar -xf - -C ~/oo_linux
cd ~/oo_linux
bash scripts/build-native.sh 2>&1 | tail -6
cp "$root"/rmarrash_2.pst ~/rm2.pst
export OPENOUTLOOK_TEST_PST=$HOME/rm2.pst
dotnet test tests/OpenOutlook.Tests 2>&1 | grep -E "error|^\s+Failed |Failed!|Passed!" | head -30
