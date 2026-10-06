#!/usr/bin/env bash
# Opens a PST the way the application does (editable when possible) from the Linux side, once from the Linux disk and once from a Windows drive,
# to show whether the Windows drives (drvfs) can be used as data files. Needs ~/oo_pack from scripts/wsl-package.sh.
#   MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/wsl-open-test.sh [file.pst]
set -u
export PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
src=${1:-/mnt/f/Claude/OpenOutlook/rmarrash_2.pst}
rm -rf ~/oo_pack/tools && cp -r /mnt/f/Claude/OpenOutlook/tools ~/oo_pack/tools
cd ~/oo_pack || exit 1
cp "$src" ~/t_local.pst
winside=$(dirname "$src")/.t_copy.pst
cp "$src" "$winside"
echo "== Linux disk ==";   dotnet run --project tools/MailSmoke -- openeditable ~/t_local.pst 2>&1 | grep -E "opened|failed|rror" | head -5
echo "== Windows drive =="; dotnet run --project tools/MailSmoke -- openeditable "$winside" 2>&1 | grep -E "opened|failed|rror" | head -5
rm -f ~/t_local.pst "$winside" "$winside".* 2>/dev/null
ls -la "$(dirname "$src")"/.t_copy* 2>&1 | head -3
