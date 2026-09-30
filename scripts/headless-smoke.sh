#!/usr/bin/env bash
# Headless smoke run: launch the real desktop app on a private Xvfb display,
# verify it actually renders, optionally click through the UI, save screenshots.
#
#   scripts/headless-smoke.sh                          # start with no archive
#   OO_SMOKE_PST="$PWD/rmarrash_2.pst" scripts/headless-smoke.sh
#   OO_SMOKE_CLICKS="135,402:6 500,410:8" scripts/headless-smoke.sh
#
# OO_SMOKE_PST     newline/space separated archive paths to open on launch
# OO_SMOKE_CLICKS  "x,y:seconds ..." executed in order; a screenshot is taken
#                  after each wait, so the whole flow is recorded
# OO_BINARY        app binary (default: bin/Debug/net8.0/OpenOutlook.Desktop)
# OO_SMOKE_HOME    HOME to run under (default: real HOME, so account registry
#                  and keyring behave exactly as they do for the owner)
# OO_SMOKE_OUT     output directory (default: .local/smoke/<timestamp>)
#
# Why this script exists (all of these bit us):
# - Xvfb refuses to create /tmp/.X11-unix when euid != 0, so xvfb-run fails in a
#   container/sandbox; the directory must exist first.
# - A read-only HOME makes XDG settings writes throw; override with OO_SMOKE_HOME.
# - Background processes may not outlive the calling shell, so this script starts
#   Xvfb, runs the app, captures and cleans up inside one invocation.
# - scrot needs an absolute output path.
# - This host's Xvfb crashes with GLX enabled, so it is disabled here.
# Screenshots contain real mail: keep the output directory out of version control.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 2

binary="${OO_BINARY:-$root/src/OpenOutlook.Desktop/bin/Debug/net8.0/OpenOutlook.Desktop}"
outdir="${OO_SMOKE_OUT:-$root/.local/smoke/$(date +%Y%m%d-%H%M%S)}"
run_home="${OO_SMOKE_HOME:-$HOME}"
screens=1440x900

[[ -x "$binary" ]] || { echo "binary not found: $binary (run scripts/dev-check.sh first)"; exit 2; }
for tool in Xvfb scrot xdotool identify; do
    command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 2; }
done
mkdir -p "$outdir" || exit 2

# Pick an unused display so we never touch the user's session.
display=""
for n in 97 98 99 96 95; do
    [[ -e "/tmp/.X$n-lock" ]] || { display=":$n"; break; }
done
[[ -n "$display" ]] || { echo "no free X display"; exit 2; }

mkdir -p /tmp/.X11-unix 2>/dev/null && chmod 1777 /tmp/.X11-unix 2>/dev/null
rm -f "/tmp${display}-lock"

Xvfb "$display" -screen 0 "${screens}x24" -extension GLX >"$outdir/xvfb.log" 2>&1 &
xvfb_pid=$!
app_pid=""
cleanup() {
    [[ -n "$app_pid" ]] && kill "$app_pid" 2>/dev/null
    kill "$xvfb_pid" 2>/dev/null
    wait 2>/dev/null
}
trap cleanup EXIT

for _ in $(seq 1 40); do DISPLAY="$display" xwininfo -root >/dev/null 2>&1 && break; sleep 0.5; done
if ! DISPLAY="$display" xwininfo -root >/dev/null 2>&1; then
    echo "Xvfb did not come up on $display"; tail -5 "$outdir/xvfb.log"; exit 1
fi

# shellcheck disable=SC2206
archives=(${OO_SMOKE_PST:-})
HOME="$run_home" DISPLAY="$display" setsid nohup "$binary" "${archives[@]}" \
    >"$outdir/app.log" 2>&1 </dev/null &
app_pid=$!

window=""
for _ in $(seq 1 60); do
    window=$(DISPLAY="$display" xdotool search --name ".*OpenOutlook.*" 2>/dev/null | head -1)
    [[ -n "$window" ]] && break
    sleep 0.5
done
if [[ -z "$window" ]]; then
    echo "FAIL: no OpenOutlook window appeared on $display"
    tail -20 "$outdir/app.log"
    exit 1
fi
echo "window $window up on $display"

capture() { # capture <name>
    DISPLAY="$display" scrot "$outdir/$1.png" 2>"$outdir/scrot.err"
    if [[ ! -s "$outdir/$1.png" ]]; then echo "FAIL: screenshot '$1' missing"; return 1; fi
    # A rendered UI has many colours; a blank/black root has one or two.
    colors=$(identify -format "%k" "$outdir/$1.png" 2>/dev/null || echo 0)
    size=$(identify -format "%wx%h" "$outdir/$1.png" 2>/dev/null || echo "?")
    echo "  shot $1.png ${size} colors=${colors}"
    (( colors > 16 )) || { echo "FAIL: '$1' looks blank (colors=$colors)"; return 1; }
}

sleep 5
capture startup || exit 1

index=0
for step in ${OO_SMOKE_CLICKS:-}; do
    coords="${step%%:*}"; wait_s="${step##*:}"; [[ "$wait_s" == "$step" ]] && wait_s=5
    DISPLAY="$display" xdotool mousemove "${coords%,*}" "${coords#*,}" click 1
    sleep "$wait_s"
    index=$((index + 1))
    capture "click$index" || exit 1
done

if grep -qiE "Unhandled exception|Fatal error|Aborted" "$outdir/app.log"; then
    echo "FAIL: fatal error in app log"; tail -20 "$outdir/app.log"; exit 1
fi

echo "HEADLESS SMOKE PASSED — screenshots in $outdir"
