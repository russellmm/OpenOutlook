# Handoff: testing OpenOutlook on bare-metal Linux (2026-10-06)

Everything below is committed on `native-engine-phase0` and `main` (same commit). Until now Linux has only been run under **WSL2 / WSLg** on the owner's Windows PC (Ubuntu), never on a real Linux desktop. This list is what is known, what is untested, and how to build, run, log and report.

> **Update (2026-10-06, evening):** the first bare-metal session has happened. Sections 1-7 are the original handoff and are still accurate (current package: `openoutlook_0.1.22_amd64.deb`); **section 8 at the end is the result of that session** (fixes, gotchas, what is still untested, known issues). Start there.

## 1. What to install

Packages are built by `scripts/wsl-package.sh <version>` into `F:\Claude\OpenOutlook\publish\`:

| File | Notes |
|---|---|
| `openoutlook_0.1.13_amd64.deb` (118 MB) | installs to `/opt/openoutlook`, launcher `/usr/bin/openoutlook`, desktop entry, icons. 0.1.13 has the SCANPST display-to fix in `libopenpst.so`. |
| `OpenOutlook-linux-x64.tar.gz` (153 MB) | portable: run `./OpenOutlook.Desktop` from the unpacked folder. |

Get them onto the Linux machine with a USB stick or by mounting the Windows NTFS drive (F:) read-only. They are **not** in git (too large; `publish/` is git-ignored). The CI workflow (`.github/workflows/linux.yml`) also builds both on every push to `main` and uploads them as an artifact.

```bash
sudo apt install ./openoutlook_0.1.13_amd64.deb      # pulls libsecret, xdg-utils and the browser's own libraries
openoutlook                                           # or the "OpenOutlook" desktop entry
```

Needed on the desktop (the deb depends on most of it, check anyway): a running **Secret Service** (GNOME Keyring or KWallet, with an unlocked *default* keyring - sign-in tokens live there), `xdg-open`, and for the interactive message reader **libwebkit2gtk-4.1-0** (GTK3 WebKit; the GTK4 `webkitgtk-6.0` packages do not count). Without WebKit the reader falls back to a browser-rendered snapshot, by design.

Sign-in needs `openoutlook-oauth.json` beside the program (the package already includes it, git-ignored in the repo - never commit or paste it). You will have to **sign in again** on the new machine (Account Settings > Email > add Hotmail / Gmail); tokens are per user and per machine.

## 2. Where things live on Linux

| What | Path |
|---|---|
| program | `/opt/openoutlook` (deb) |
| log | `~/.local/share/OpenOutlook/logs/openoutlook.log` (2 MB, rotated); tags like `[pst-open]`, `[mirror]`, `[placement]` |
| mailbox copies (PST behind each connected account) | `~/.local/share/openoutlook/mail/<address>.pst` (+ `.sync`, `.log`) |
| window / pane layout | `~/.config/OpenOutlook/view-layout.json` |
| opened data files | `~/.config/OpenOutlook/attached-psts.json` |
| mirror settings | `~/.config/OpenOutlook/mirror-settings.json` (under `$XDG_CONFIG_HOME` when set) |

## 3. Test checklist (never run on real hardware)

Mark each in the log of your session; anything failing: attach the end of `openoutlook.log`.

1. **Start** from the desktop entry and from a terminal. Icon in the launcher/taskbar; window title; no `[pst-open]` errors in the log.
2. **Window behaviour** on the real X11/Wayland session (all of this was WSLg-specific workarounds before; they are gated on WSL and should be inert now): window opens where expected, remembers size/position/maximised, menus and dialogs appear **in front** of the main window (Linux draws popups inside the window: `X11PlatformOptions.OverlayPopups`), mouse cursor visible, high-DPI/scaling looks right, dark/light theme.
3. **File dialogs** (GTK): File > Open data file, Account Settings > Data Files > Add... and Add by path...
4. **Open a PST**: copy one of the PSTs next to the machine (for example `rmarrash_*.pst`), open it read-only, browse folders, read messages (HTML in the interactive WebKit reader, plain text), attachments (save, open with `xdg-open`), "Open file location".
5. **Sign-in**: add the Hotmail account and the Gmail account (browser opens through `xdg-open`; the keyring must store the token). Restart the app: still signed in.
6. **Mailbox copy**: after sign-in the copy is built under `~/.local/share/openoutlook/mail`. Check Data Files shows it with a status, "Sync now" works, turning it off and on again starts a sync (fixed 2026-10-06). Folders open from the local copy, actions (read/unread, flag, move, delete) are instant and reach the server.
7. **Junk Cleaner** (Hotmail only): preview, clean, silent auto-clean.
8. **Compose**: new mail, reply, forward with attachment, send from both accounts; second compose window.
9. **Drag and drop**, column header menu, Importance/Flag columns, sorting by Received keeps date groups.
10. **Scan the files the Linux build writes.** SCANPST exists only on Windows: copy `~/.local/share/openoutlook/mail/<address>.pst` (close the app first) back to the Windows PC and scan it with SCANPST (`tools/python/run_scanpst2.ps1 <file>` prints NO_ERRORS / MINOR / ERRORS). The Hotmail mirror produced by the Windows build scanned **NO_ERRORS** after the 2026-10-06 fix; the Linux engine was verified with synthetic files only (9 of 9 NO_ERRORS, see section 5). On Linux itself: `native/openpst` builds `openpst` (CLI: `openpst FILE check`) and `tools/python/pstcheck.py refs FILE`.

## 4. Building and testing on Linux itself

```bash
sudo apt install build-essential cmake ninja-build python3        # native engine
# .NET 8 SDK from Microsoft (dotnet-install.sh) in ~/.dotnet
bash scripts/build-native.sh                                      # libopenpst.so + ctest (3/3 expected)
dotnet test tests/OpenOutlook.Tests                               # 533 pass on Linux; tests that need private PST fixtures skip themselves
bash scripts/package-linux-x64.sh && bash scripts/build-deb.sh 0.1.14
```

Scripts named `wsl-*.sh` are WSL helpers (run as `MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/<name>`): `wsl-dotnet-test.sh`, `wsl-linux-check.sh` (headless UI tests), `wsl-package.sh`, `wsl-deb-smoke.sh`, `wsl-share-matrix.sh` (new: builds the SCANPST synthetic PSTs with the Linux engine). On bare metal use `scripts/package-linux-x64.sh` / `build-deb.sh` directly.

Headless tests (21 UI tests) run under Xvfb: `scripts/headless-smoke.sh`. The `chrome-headless-shell` (Chrome for Testing 154, `scripts/fetch_chromium.py`) ships beside the program and renders HTML mail for the fallback and printing paths.

## 5. State of the engine on Linux

- OpenPST (`native/openpst`, plain C) compiles unchanged on Linux; ctest 3/3, unit tests 533/533 in WSL on 2026-10-06 including the new display-to change.
- **Shared display-to data tree (design rule 27)**: a long PidTagDisplayTo/DisplayCc is stored once and referenced by both the message and the contents-table cell, otherwise SCANPST reports MINOR. Linux check: `scripts/wsl-share-matrix.sh` builds 9 PSTs (60/90/100/200/400 recipients, display-to 100-3000 characters, plus after flag/read/move) with the Linux engine, and SCANPST on Windows said NO_ERRORS for all 9.
- Details and the investigation: `docs/native-engine-status.md` ("SCANPST minor ... found and fixed"), `docs/scanpst-hotmail-root-cause-2026-10-06.md`, `docs/OpenOutlook_Design_Document.md` rule 27.

## 6. Known issues / not done

- **Bcc recipients: real SCANPST ERRORS.** A message with Bcc recipients written by our importer makes SCANPST report an error (not just MINOR). Documented, not investigated; reproduce with `MailSmoke mkbigrecips <dir> N:...` (the 1:bccL spec) and run `tools/python/run_scanpst2.ps1`. Suspect: how the recipient table and the display-bcc property (0x0E02) are written; compare with an Outlook-authored message that has Bcc recipients (SCANPST repair on a copy + `tools/python/pstdiff.py` / `pststoragediff.py`). Leave the real mirror alone: Hotmail mail from the server has no Bcc, so mirrors are clean; it matters for sent items.
- No checker/fixer rule for already-written PSTs with the old independent display-to copy (recreate the mirror: close the app, rename its `.pst` and `.sync`, start, turn the copy on).
- A single recipient *name* of 5,000+ characters still gives a SCANPST error (SCANPST rebuilds display-to from it); not a real-world case.
- Linux specifics never exercised outside WSL: libsecret keyring on a real desktop, WebKitGTK reader, `xdg-open`/file-manager helpers, Wayland (XWayland vs native: Avalonia 11.2 uses X11/XWayland), HiDPI.
- Five `OfflineMessageCacheTests` fail on Windows with `PlatformNotSupportedException` in the test setup (a Unix-only API); they pass on Linux. Not investigated.
- The Windows build is `F:\Claude\OpenOutlook_win`; a PST written by one platform opens fine on the other (same engine, same format).

## 7. Standing rules for whoever continues

- The owner's real PSTs (`F:\Claude\rmarrash_*.pst`, `F:\Claude\PST_Files\...`, and anything the app owns under `AppData`/`~/.local/share/openoutlook/mail`) are never modified by experiments: work on copies (`.local/scan/`).
- Never commit or print `gh_token`, `.secrets/`, `openoutlook-oauth.json`.
- Push `native-engine-phase0` to both `native-engine-phase0` and `main` (same commit). Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Publish the Windows build only when a change affects Windows, and never kill the owner's running OpenOutlook.

## 8. Result of the first bare-metal session (2026-10-06, Ubuntu 26.04, GNOME on Wayland, kernel 7.0)

Done on the real machine (everything automated; items 1-9 of section 3 still need a person at the desktop):

- Built the native engine (ctest 3/3), unit tests (536/536), headless UI tests (24/24 with `OPENOUTLOOK_TEST_PST=<small pst>`), headless smoke under Xvfb (source build, published single-file binary and the installed /opt copy all start and render), the self-contained package and the .deb. Installed 0.1.14 ... 0.1.22 with `apt install ./publish/openoutlook_<v>_amd64.deb`; 0.1.22 is current.
- Desktop prerequisites verified present: gnome-keyring on the session bus (`org.freedesktop.secrets`), libsecret, libwebkit2gtk-4.1-0, xdg-utils, default browser Google Chrome.
- **Found and fixed: the bundled `chrome-headless-shell` crashed with SIGTRAP** ("The application OpenOutlook has closed unexpectedly", apport report). Ubuntu 23.10+ sets `kernel.apparmor_restrict_unprivileged_userns=1`; the browser's sandbox creates a user namespace, lands in the `unprivileged_userns` profile and is denied `sys_admin` (kernel log: `apparmor="DENIED" operation="capable" ... capname="sys_admin"`). The app retried twice and fell back (the reader log shows `chrome-headless-shell did not start`). Fix, in the deb: `/etc/apparmor.d/openoutlook-chromium` (`packaging/openoutlook-chromium.apparmor`), a profile with `userns,` for that one binary, loaded by the postinst (`apparmor_parser -r`) and removed by the postrm. The sandbox stays on. `aa-status` lists `openoutlook-chromium` after install. Not done: the portable tar.gz has no profile; on Ubuntu 23.10+ it needs the same file copied to /etc/apparmor.d (with the path edited) or the sandbox disabled.
- **`scripts/build-deb.sh` on an NTFS checkout:** `dpkg-deb` rejects the 777 control directory an NTFS mount produces. Set `OO_DEB_STAGE` to a native path: `OO_DEB_STAGE=/tmp/deb-stage bash scripts/build-deb.sh 0.1.22`. (This repo sits on `/mnt/SOLIDIGM`, a fuseblk NTFS mount; file modes are all 777.)
- **Line endings:** because of that mount about 40 tracked files show as modified in `git status` although only their line endings differ (`git diff --ignore-cr-at-eol` shows the real changes). Commit by explicit file list, not `git add -A`. A `.gitattributes` / `core.autocrlf` setting would stop it.
- Build tools needed that were missing: `ninja-build` (installed with apt; `build-native.sh` uses `-G Ninja`).
- A dotnet gotcha for automation: after `dotnet build/test` the msbuild/VBCS server processes keep the stdout pipe open, so a command that pipes `dotnet test` into `grep` appears to hang. Use `DOTNET_CLI_USE_MSBUILD_SERVER=0`, `-nodeReuse:false`, and redirect output to a file.
- In the Xvfb smoke the reading pane shows "Unable to initialize GTK" (NativeWebView needs a GTK session). Expected under Xvfb; if it ever appears on the real desktop, that is a bug to report. It also hides the lower part of popup menus that overlap the reading pane in screenshots.

Product work done in the same session (all pushed, see `docs/FEATURES.md` and `session-handoff-2026-10.md`): Empty Deleted Items for Microsoft accounts with a permanent-delete warning; stale folder lists and counts after server-side deletes and Junk Cleaner runs; Arrange By drop-down on the "By date" chip; search-icon and zoom-slider fixes; highlight-next after delete/archive/move; the chosen sort survives folder changes.

Still to walk through by hand on this machine (section 3): 1 start from desktop entry and terminal, 2 window behaviour on Wayland (XWayland) incl. popups in front, remembered geometry, HiDPI, theme, 3 GTK file dialogs, 4 open a PST read-only (WebKit reader, attachments, xdg-open), 5 Hotmail and Gmail sign-in through the keyring and a restart, 6 mailbox copy and sync, 7 Junk Cleaner (the Linux session saw it work: nine messages moved by the spam rules), 8 compose/reply/forward from both accounts, 9 drag and drop, 10 scan the Linux-written PSTs with SCANPST on Windows.

Known issues added: (a) the Windows build `F:\Claude\OpenOutlook_win` was republished at the end of the session by cross-publishing from Linux (`dotnet publish src/OpenOutlook.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <scratch>`, then copying `OpenOutlook.Desktop.exe` and the `.pdb` files into the folder; `openpst.dll`, `chromium\` and `openoutlook-oauth.json` were left as they were, the dll is byte-identical to the current native build). It is a valid x64 PE but has NOT been started on Windows: run it once and check the new list features (Arrange By chip, sort persistence, highlight-next, Empty Deleted Items, search icon, zoom slider); (b) two Graph reader unit tests are flaky when the whole suite runs (`GraphJunkMailReaderTests.Unsafe_pagination_does_not_send_followup_request`, `GraphInboxReaderTests.DownloadsTheRawMimeMessageForTheVerifiedAccountOnly`; they pass alone and on rerun; suspected shared static state in `GraphAccountVerification`, which skips the `/v1.0/me` call when another test already verified the same account and token); (c) the ribbon Zoom button's icon is cropped like the search icon was (32px template in a 28px box, wrap in a Viewbox); (d) the Bcc SCANPST error is deliberately deferred until after the first production release; (e) `rmarrash_2.zip` was removed from the repository (still in history: added in 836348d, removed in c929e75); purging history was offered and not done.

Secrets on this machine: the sudo password for the bare-metal box is in `/mnt/SOLIDIGM/Claude/.secrets/sudo_bm` (the older `sudo` file is for WSL), the GitHub classic token in `gh_classic`. Never print or commit them. Pushing: `git -c http.extraHeader="Authorization: Basic $(printf 'x-access-token:%s' "$tok" | base64 -w0)" push origin native-engine-phase0 native-engine-phase0:main` with `tok` read from `gh_classic` (no credential helper is configured on Linux).

## 9. Updates after the first bare-metal session (2026-10-06, evening, from Windows/WSL)

- **Package 0.1.23** (`publish/openoutlook_0.1.23_amd64.deb`, tar.gz) replaces 0.1.22 on the Linux box: `sudo apt install /mnt/SOLIDIGM/Claude/OpenOutlook/publish/openoutlook_0.1.23_amd64.deb`. It has the engine fixes below and the new Junk Cleaner buttons; the AppArmor profile and packaging from section 8 are included (built from the same source in WSL).
- **Engine fix: message index past 8 heap blocks** (design rule 28). A Hotmail mirror stopped syncing with "the heap needs a block kind that is not supported yet" once node 0xE01 outgrew 8 blocks; `heap_append_item` now writes the HN bitmap blocks. Verified with the Linux engine: `MailSmoke mkmany <file> 6000` gives a PST that SCANPST (Windows) reports NO_ERRORS. If a mailbox copy on Linux already shows "Sync problem", update and click Sync now.
- **Junk Cleaner keyword Export / Import** (Account Settings > Junk Cleaner). Settings are per machine, so the keyword list built on Linux must be exported there (`Export keywords...`, save under `/mnt/SOLIDIGM/Claude/`) and imported on Windows (`Import keywords...`). Linux's `~/.config/OpenOutlook/junk-cleaner.json` is not reachable from Windows.
- Re-check item 10 of section 3 with the new package: copy the Linux mailbox copy to Windows and scan it with SCANPST (the hotmail mirror built on Windows was NO_ERRORS).
