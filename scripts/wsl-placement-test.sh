#!/usr/bin/env bash
# Starts the newest built package (unpacked, not installed, with scratch settings) on the real WSLg display for a few seconds and shows where the window opened:
# what the program asked for, what it reports after two seconds, and where the X server says the window is. The window closes by itself.
deb=$(ls -t /mnt/f/Claude/OpenOutlook/publish/openoutlook_*_amd64.deb | head -1)
rm -rf ~/place-test && mkdir -p ~/place-test/cfg ~/place-test/data && dpkg -x "$deb" ~/place-test/pkg
# COPY_LAYOUT=1 starts with the real saved window layout (size, columns) instead of the defaults
if [ "${COPY_LAYOUT:-0}" = 1 ]; then mkdir -p ~/place-test/cfg/OpenOutlook && cp ~/.config/OpenOutlook/view-layout.json ~/place-test/cfg/OpenOutlook/; fi
# SIZE=WxH starts with a layout of just that window size
if [ -n "${SIZE:-}" ]; then mkdir -p ~/place-test/cfg/OpenOutlook; printf '{"WindowWidth":%s,"WindowHeight":%s}' "${SIZE%x*}" "${SIZE#*x}" > ~/place-test/cfg/OpenOutlook/view-layout.json; fi
export XDG_CONFIG_HOME=$HOME/place-test/cfg XDG_DATA_HOME=$HOME/place-test/data OPENOUTLOOK_NO_MIRROR=1 EGL_LOG_LEVEL=fatal
timeout 14 ~/place-test/pkg/opt/openoutlook/OpenOutlook.Desktop > ~/place-test/run.log 2>&1 &
sleep 8
echo "== X display =="; xrandr --listmonitors | tail -2
echo "== the window as the X server sees it =="
xwininfo -root -tree 2>/dev/null | grep -iE "openoutlook|OpenOutlook" | head -4
for id in $(xwininfo -root -tree 2>/dev/null | grep -iE "openoutlook" | awk '{print $1}' | head -2); do xwininfo -id "$id" 2>/dev/null | grep -E "Absolute|Width:|Height:|Relative"; done
echo "== program log =="; grep placement ~/place-test/data/OpenOutlook/logs/openoutlook.log | cut -c1-400
wait
