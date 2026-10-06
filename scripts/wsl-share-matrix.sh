#!/usr/bin/env bash
# Linux check of the shared display-to data tree: builds the synthetic PSTs with the Linux engine (MailSmoke mkbigrecips / mkmulti / touchall)
# into <repo>/.local/scan/linux_share so they can be scanned with SCANPST on Windows.
#   MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/wsl-share-matrix.sh
set -euo pipefail
export PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/.local/scan/linux_share"
work=$HOME/oo_linux
tmp=$(mktemp -d)
cd "$work"
dotnet build tools/MailSmoke -c Release -v q 2>&1 | tail -1
run() { dotnet tools/MailSmoke/bin/Release/net8.0/MailSmoke.dll "$@"; }
run mkbigrecips "$tmp" 60 90 100 200 400 | tail -5
run mkmulti "$tmp/multi.pst" 100 1000 1023 1024 1100 1500 1700 1800 2000 2500 3000 | tail -1
for n in multi recips_200 recips_60; do cp "$tmp/$n.pst" "$tmp/t_$n.pst"; run touchall "$tmp/t_$n.pst" | tail -1; done
mkdir -p "$out" && cp "$tmp"/*.pst "$out"/
ls -la "$out"
