#!/usr/bin/env bash
# Asks the keyring service to save one test item. With no default keyring yet, GNOME Keyring opens its "Choose password for new keyring" window; the person at the
# computer fills it in (this script never sees or sets the password). The test item is deleted again once it was stored.
timeout 900 bash -c 'echo "oo-keyring-test" | secret-tool store --label="OpenOutlook keyring test" app oo-test'
code=$?
echo "store finished with exit code $code"
if [ "$code" = 0 ]; then
  echo "default keyring now: $(gdbus call --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets --method org.freedesktop.Secret.Service.ReadAlias default 2>&1 | head -1)"
  ls ~/.local/share/keyrings
  secret-tool clear app oo-test
  echo "test item removed"
fi
