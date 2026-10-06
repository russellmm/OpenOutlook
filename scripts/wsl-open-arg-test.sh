#!/usr/bin/env bash
# Starts the installed program under a virtual display with a PST path on the command line (isolated settings folders) and shows what the log says.
#   MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/wsl-open-arg-test.sh [file.pst]
src=${1:-/mnt/f/Claude/OpenOutlook/rmarrash_2.pst}
rm -rf ~/arg-test && mkdir -p ~/arg-test/cfg ~/arg-test/data
export XDG_CONFIG_HOME=$HOME/arg-test/cfg XDG_DATA_HOME=$HOME/arg-test/data OPENOUTLOOK_NO_MIRROR=1
timeout 25 xvfb-run -a /opt/openoutlook/OpenOutlook.Desktop "$src" > ~/arg-test/run.log 2>&1 &
sleep 14
echo "--- log ---"; grep -vE "WebKit|libwebkit" ~/arg-test/data/OpenOutlook/logs/openoutlook.log | cut -c1-260 | tail -8
echo "--- saved PST list ---"; cat ~/arg-test/cfg/OpenOutlook/attached-psts.json 2>&1
echo; wait
pkill -f chrome-headless-shell 2>/dev/null; true
