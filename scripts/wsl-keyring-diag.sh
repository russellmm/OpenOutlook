#!/usr/bin/env bash
# Shows why sign-in or the keyring might not work in WSL: the program's log, the session bus, the Secret Service and the default keyring. Nothing secret is printed.
echo "== program log (last lines, without the WebKit noise) =="
grep -vE "libwebkit|cannot open shared|^/home|^\)|^$" ~/.local/share/OpenOutlook/logs/openoutlook.log | tail -12 | cut -c1-420
echo "== session =="
echo "DBUS_SESSION_BUS_ADDRESS=${DBUS_SESSION_BUS_ADDRESS:-<unset>}  XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR:-<unset>}  DISPLAY=${DISPLAY:-<unset>}"
systemctl --user is-active dbus.service gnome-keyring-daemon.socket gnome-keyring-daemon.service 2>&1 | paste -sd' '
echo "== Secret Service on the bus =="
timeout 8 busctl --user list 2>&1 | grep -iE "secrets|keyring|NAME" | head -4
echo "== default keyring alias / collections =="
timeout 8 gdbus call --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets --method org.freedesktop.Secret.Service.ReadAlias default 2>&1 | head -2
timeout 8 busctl --user get-property org.freedesktop.secrets /org/freedesktop/secrets org.freedesktop.Secret.Service Collections 2>&1 | head -2
ls -la ~/.local/share/keyrings 2>&1 | head -6
echo "== can an item be read back (non-interactive, 8 s limit) =="
timeout 8 secret-tool lookup app oo-test < /dev/null > /dev/null 2>&1; echo "lookup exit code: $? (0 = found, 1 = not found, 124 = waited for a prompt)"
echo "== sign-in browser helper =="
echo "BROWSER=${BROWSER:-<unset>}"; command -v xdg-open wsl-open explorer.exe
