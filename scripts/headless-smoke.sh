#!/usr/bin/env bash
# Headless smoke run: launch the real desktop app on a private Xvfb display,
# verify it actually renders, optionally click through the UI, save screenshots.
#
#   scripts/headless-smoke.sh
#   OO_SMOKE_PST="$PWD/rmarrash_2.pst" scripts/headless-smoke.sh
#   OO_SMOKE_LIVE=1 OO_SMOKE_CLICKS="135,361:7 500,412:15" scripts/headless-smoke.sh
#
# OO_BINARY        app binary (default: src/.../bin/Debug/net8.0/OpenOutlook.Desktop)
# OO_SMOKE_PST     space separated archive paths to open on launch
# OO_SMOKE_CLICKS  "x,y:seconds ..." executed in order; a screenshot is taken after
#                  each wait, so the whole flow is recorded
# OO_SMOKE_LIVE    1 = copy the account registry into the scratch HOME, so the run
#                  signs in for real. The refresh token comes from the keyring over
#                  the session bus, which is inherited; nothing is written back.
# OO_SMOKE_CRASH_GUARD  1 = throw an unhandled exception after startup and require that the app
#                  survives it with an "Unexpected error" window and a log entry
# OO_SMOKE_HOME    HOME to run under (default: a fresh scratch directory)
# OO_SMOKE_SIZE    window size WxH forced after launch (default: the 1440x900 screen)
# OO_SMOKE_OUT     output directory (default: .local/smoke/<timestamp>)
#
# Three properties this script exists to guarantee, each because it went wrong once:
#
# 1. Never touch the owner's session. The app is launched with DISPLAY explicitly set
#    to the private Xvfb display; a run that inherited DISPLAY=:0 opened a live window
#    on the owner's monitor mid-work. Launching is refused if no private display exists.
# 2. Never overwrite the owner's settings. The app persists window geometry, appearance
#    and its archive list under HOME, so runs default to a scratch HOME that is deleted
#    afterwards. Restored geometry also made scripted coordinates depend on whatever
#    the owner did last; geometry is therefore forced after launch.
# 3. Be self-contained in one process tree. Background processes may not outlive the
#    calling shell, so Xvfb, the app, capture and cleanup all happen in one invocation.
#
# Other host quirks handled here: Xvfb will not create /tmp/.X11-unix when euid != 0
# (so xvfb-run fails), this build of Xvfb crashes with GLX enabled, and scrot needs an
# absolute output path.
#
# Screenshots contain real mail: keep the output directory out of version control.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 2

binary="${OO_BINARY:-$root/src/OpenOutlook.Desktop/bin/Debug/net8.0/OpenOutlook.Desktop}"
outdir="${OO_SMOKE_OUT:-$root/.local/smoke/$(date +%Y%m%d-%H%M%S)}"
screens="${OO_SMOKE_SIZE:-1440x900}"
screen_w="${screens%x*}"; screen_h="${screens#*x}"

[[ -x "$binary" ]] || { echo "binary not found: $binary (run scripts/dev-check.sh first)"; exit 2; }
for tool in Xvfb scrot xdotool identify; do
    command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 2; }
done
mkdir -p "$outdir" || exit 2

# Pick an unused display, and refuse to proceed without one rather than falling back.
display=""
for n in 97 98 99 96 95 94 93; do
    [[ -e "/tmp/.X$n-lock" ]] || { display=":$n"; break; }
done
[[ -n "$display" && "$display" != ":0" ]] || { echo "FAIL: no private X display free"; exit 2; }

# Scratch HOME so a test run cannot rewrite the owner's window geometry or preferences.
scratch_home=""
if [[ -n "${OO_SMOKE_HOME:-}" ]]; then
    run_home="$OO_SMOKE_HOME"
else
    scratch_home="$(mktemp -d "$root/.local/smoke-home.XXXXXX")" || exit 2
    run_home="$scratch_home"
    mkdir -p "$run_home/.local/share/OpenOutlook"
    # Deterministic pane weights. Without them the layout comes from whatever was last used, which
    # moves reader buttons between rows and makes scripted coordinates meaningless run to run.
    panes="${OO_SMOKE_PANES:-1.4,3,5.6}"
    IFS=',' read -r pane_folders pane_messages pane_reader <<< "$panes"
    mkdir -p "$run_home/.config/OpenOutlook"
    printf '{"FolderPaneWeight":%s,"MessagePaneWeight":%s,"ReaderPaneWeight":%s,"WindowMaximized":false}\n' \
        "$pane_folders" "$pane_messages" "$pane_reader" > "$run_home/.config/OpenOutlook/view-layout.json"
    if [[ "${OO_SMOKE_LIVE:-0}" == "1" && -f "$HOME/.local/share/OpenOutlook/accounts.json" ]]; then
        cp "$HOME/.local/share/OpenOutlook/accounts.json" "$run_home/.local/share/OpenOutlook/"
        echo "live account run: registry copied into scratch HOME (keyring via session bus)"
    fi
fi

mkdir -p /tmp/.X11-unix 2>/dev/null && chmod 1777 /tmp/.X11-unix 2>/dev/null
rm -f "/tmp${display}-lock"

Xvfb "$display" -screen 0 "${screens}x24" -extension GLX >"$outdir/xvfb.log" 2>&1 &
xvfb_pid=$!
app_pid=""
cleanup() {
    [[ -n "$app_pid" ]] && kill "$app_pid" 2>/dev/null
    kill "$xvfb_pid" 2>/dev/null
    wait 2>/dev/null
    [[ -n "$scratch_home" ]] && rm -rf "$scratch_home"
}
trap cleanup EXIT

for _ in $(seq 1 40); do DISPLAY="$display" xwininfo -root >/dev/null 2>&1 && break; sleep 0.5; done
if ! DISPLAY="$display" xwininfo -root >/dev/null 2>&1; then
    echo "FAIL: Xvfb did not come up on $display"; tail -5 "$outdir/xvfb.log"; exit 1
fi

# shellcheck disable=SC2206
archives=(${OO_SMOKE_PST:-})
# OO_SMOKE_CRASH_GUARD=1 makes the app throw from an async void continuation after startup, so the
# unhandled-exception hook can be verified from outside instead of assumed. Checked below.
crash_env=()
if [[ "${OO_SMOKE_CRASH_GUARD:-0}" == "1" ]]; then
    crash_env=(OPENOUTLOOK_TEST_THROW_UNHANDLED=1)
    echo "crash-guard mode: the app will throw an unhandled exception ~1.5s after startup"
fi
env -u DISPLAY HOME="$run_home" DISPLAY="$display" "${crash_env[@]}" setsid nohup "$binary" "${archives[@]}" \
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

# Force a known position and size so scripted coordinates mean the same thing every run.
DISPLAY="$display" xdotool windowmove "$window" 0 0 >/dev/null 2>&1
DISPLAY="$display" xdotool windowsize "$window" "$screen_w" "$screen_h" >/dev/null 2>&1
sleep 2
geometry=$(DISPLAY="$display" xdotool getwindowgeometry "$window" 2>/dev/null | tr '\n' ' ')
echo "window $window up on $display ${geometry}"

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
    case "$step" in
        # wheel:<x>,<y>:<clicks> scrolls a pane, for content that starts below the fold.
        wheel:*)
            coords="${step#wheel:}"; coords="${coords%%:*}"; n="${step##*:}"
            for _ in $(seq 1 "$n"); do
                DISPLAY="$display" xdotool mousemove "${coords%,*}" "${coords#*,}" click 5
            done
            sleep 3
            ;;
        # key:<xdotool-key> presses one key (e.g. key:Delete) into whatever has focus.
        key:*)
            DISPLAY="$display" xdotool key "${step#key:}"
            sleep 2
            ;;
        # type:<text> types into whatever has focus; '+' stands for a space, since steps are
        # word-split and a literal space would end the step.
        type:*)
            text="${step#type:}"; text="${text//+/ }"
            DISPLAY="$display" xdotool type --clearmodifiers --delay 25 "$text"
            sleep 2
            ;;
        # clip:<name> saves the clipboard selection to <outdir>/<name>.txt, so a flow can prove
        # that text really is selectable and copyable (Xvfb has its own clipboard).
        clip:*)
            name="${step#clip:}"
            if command -v xclip >/dev/null; then
                DISPLAY="$display" xclip -selection clipboard -o > "$outdir/$name.txt" 2>/dev/null
            elif command -v xsel >/dev/null; then
                DISPLAY="$display" xsel -b > "$outdir/$name.txt" 2>/dev/null
            else
                echo "  (no xclip/xsel; cannot read clipboard)"
            fi
            echo "  clip $name -> $(wc -c < "$outdir/$name.txt" 2>/dev/null || echo 0) bytes"
            ;;
        # drag:<x1>,<y1>,<x2>,<y2> presses, moves and releases -- the way text is really selected.
        drag:*)
            spec="${step#drag:}"; IFS=',' read -r x1 y1 x2 y2 <<< "$spec"
            DISPLAY="$display" xdotool mousemove "$x1" "$y1" mousedown 1
            DISPLAY="$display" xdotool mousemove_relative -- 12 6
            DISPLAY="$display" xdotool mousemove "$x2" "$y2" mouseup 1
            sleep 1
            ;;
        # key:<combo> sends a key combination, e.g. key:ctrl+b.
        key:*)
            DISPLAY="$display" xdotool key "${step#key:}"
            sleep 1
            ;;
        # rclick:x,y:secs right-clicks -- opens context menus.
        rclick:*)
            coords="${step#rclick:}"; wait_s="${coords##*:}"; coords="${coords%%:*}"
            [[ "$wait_s" == "$coords" ]] && wait_s=2
            DISPLAY="$display" xdotool mousemove "${coords%,*}" "${coords#*,}" click 3
            sleep "$wait_s"
            ;;
        *)
            coords="${step%%:*}"; wait_s="${step##*:}"; [[ "$wait_s" == "$step" ]] && wait_s=5
            DISPLAY="$display" xdotool mousemove "${coords%,*}" "${coords#*,}" click 1
            sleep "$wait_s"
            ;;
    esac
    index=$((index + 1))
    capture "click$index" || exit 1
done

# Crash-guard verification: the escape must be reported and survived. This is the only way to know the
# hook works in the shipped binary rather than only in a synthetic probe.
if [[ "${OO_SMOKE_CRASH_GUARD:-0}" == "1" ]]; then
    sleep 4
    guard_fail=""
    kill -0 "$app_pid" 2>/dev/null || guard_fail="process died after the unhandled exception"
    notice=$(DISPLAY="$display" xdotool search --name ".*Unexpected error.*" 2>/dev/null | head -1)
    [[ -z "$notice" ]] && guard_fail="${guard_fail:-no 'Unexpected error' window appeared}"
    grep -q "test escape from an async void continuation" "$run_home/.local/share/OpenOutlook/logs/openoutlook.log" 2>/dev/null \
        || guard_fail="${guard_fail:-the escape was not written to the application log}"
    if [[ -z "$guard_fail" ]]; then
        DISPLAY="$display" scrot "$outdir/crash-guard.png" 2>/dev/null
        echo "CRASH GUARD PASSED: survived the escape, notice window $notice, logged"
    else
        echo "FAIL: crash guard: $guard_fail"
        tail -20 "$outdir/app.log"
        exit 1
    fi
fi

if grep -qiE "Unhandled exception|Fatal error|Aborted" "$outdir/app.log"; then
    echo "FAIL: fatal error in app log"; tail -20 "$outdir/app.log"; exit 1
fi

echo "HEADLESS SMOKE PASSED — screenshots in $outdir"
