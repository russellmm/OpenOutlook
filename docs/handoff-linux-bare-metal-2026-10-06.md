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
- **Shared display-to data tree (design rule 39)**: a long PidTagDisplayTo/DisplayCc is stored once and referenced by both the message and the contents-table cell, otherwise SCANPST reports MINOR. Linux check: `scripts/wsl-share-matrix.sh` builds 9 PSTs (60/90/100/200/400 recipients, display-to 100-3000 characters, plus after flag/read/move) with the Linux engine, and SCANPST on Windows said NO_ERRORS for all 9.
- Details and the investigation: `docs/native-engine-status.md` ("SCANPST minor ... found and fixed"), `docs/scanpst-hotmail-root-cause-2026-10-06.md`, `docs/OpenOutlook_Design_Document.md` rule 39.

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
- **Engine fix: message index past 8 heap blocks** (design rule 40). A Hotmail mirror stopped syncing with "the heap needs a block kind that is not supported yet" once node 0xE01 outgrew 8 blocks; `heap_append_item` now writes the HN bitmap blocks. Verified with the Linux engine: `MailSmoke mkmany <file> 6000` gives a PST that SCANPST (Windows) reports NO_ERRORS. If a mailbox copy on Linux already shows "Sync problem", update and click Sync now.
- **Junk Cleaner keyword Export / Import** (Account Settings > Junk Cleaner). Settings are per machine, so the keyword list built on Linux must be exported there (`Export keywords...`, save under `/mnt/SOLIDIGM/Claude/`) and imported on Windows (`Import keywords...`). Linux's `~/.config/OpenOutlook/junk-cleaner.json` is not reachable from Windows.
- Re-check item 10 of section 3 with the new package: copy the Linux mailbox copy to Windows and scan it with SCANPST (the hotmail mirror built on Windows was NO_ERRORS).

## 10. Current package (2026-10-15)

- **Install 0.1.32** (supersedes 0.1.23 - 0.1.31): `sudo apt install /mnt/SOLIDIGM/Claude/OpenOutlook/publish/openoutlook_0.1.32_amd64.deb`. Built from `6f4846f` (adds Hotmail message times fixed -- they showed 7 hours early --, message colours as written, faster message pictures -- shown as they arrive, tracking pixels and style-sheet beacons skipped, missing pictures logged -- the reader picture, formatting and large-message fixes, the picture-memory option, People / Hotmail contacts and **character-level text selection in the snapshot reader** of 2026-10-15 to `093b112`). On this Wayland desktop the reading pane stays on the snapshot reader, which now selects like a browser: drag by character, double-click a word, triple-click a paragraph, Shift+click, Ctrl+A / Ctrl+C, right-click Copy; please try it on a few HTML messages (= GitHub and the Windows build): everything in section 9 plus Data Files > New, copy/move between stores, the Hotmail calendar, reading-pane changes, Folder and Message Properties (raw headers), the junk keyword file with its edit box, Outlook-style rail icons, the rebuilt appointment window and the Trust Center Automatic Download page.
- **Junk keywords:** the list now lives in `~/.config/OpenOutlook/junk-keywords.json` (one comma-separated line, shared by all accounts). Copy the Windows file (`C:\Users\russell\.config\OpenOutlook\junk-keywords.json`, 145 keywords) there, or edit the list in Account Settings > Junk Cleaner and press Save keywords. The Export button of section 9 is gone (the file is the export).
- Not yet exercised on Linux at all: Folder/Message Properties, the calendar and appointment window, copy/move, Trust Center, People (contacts list, contact window, picture upload through the GTK file dialog). Add them to the section 3 walk-through.

## 11. Walk-through of 0.1.32 on bare metal (2026-10-15, Ubuntu 26.04, GNOME Wayland, kernel 7.3, two 4K monitors at 1.25x)

Driven by Claude through `ydotool` (real input via `/dev/uinput`; a udev rule `/etc/udev/rules.d/60-uinput.rules` with `TAG+="uaccess"` grants it to the desktop user, remove it when done) and X11 screenshots of the XWayland windows (`import -window <id>`). `xdotool` clicks are ignored by Xwayland 24 on GNOME, the screenshot portal refuses non-interactive captures, and GNOME-native windows (file chooser, Chrome) cannot be seen this way: the owner looked at those. The pointer is positioned in a feedback loop (move, read `xdotool getmouselocation`, correct) because GNOME accelerates relative motion; if another window covers OpenOutlook, `wmctrl -i -a <id>` brings it back. Keys are sent only after checking `_NET_ACTIVE_WINDOW` (one blind type once landed in another app).

Passed:
- Install of 0.1.32 (AppArmor profile file present); start from the desktop entry and from a terminal (window in ~3 s, 128 px icon, `WM_CLASS` matches `StartupWMClass`); no `[pst-open]` errors.
- Window size/position and the maximised state are restored after a restart. Context menus draw in front; Message Properties opens on top and active.
- HiDPI: text sharp at 1.25x (Avalonia scale 2.42 on the XWayland surface).
- GTK file chooser (portal) for File > Open & Export > Open Outlook Data File: a copy of `blank.pst` opened editable and was attached.
- Reading: Hotmail HTML (PayPal, Amazon, Nabu Casa) with pictures; an old Rich Text archive message with inline pictures; folder pane wheel scrolling.
- Snapshot reader selection: drag by character + Ctrl+C copied exactly the range; double-click a word; triple-click a paragraph; right-click Copy; Ctrl+A copies the whole message (checked through the X clipboard).
- Message Properties: fields and raw headers; Received shown in local time (23:17 UTC as 16:17).
- People: 20 Hotmail contacts with pictures, detail pane, contact window opens and closes.
- Calendar: after the owner repaired the Hotmail sign-in (`accounts.json` now has `Calendars.ReadWrite`) the month view shows the account's events, the calendar choices and busy days in the mini months; the new appointment window opens (closed without saving).
- Compose and send from both accounts: Hotmail to Gmail and Gmail to Hotmail delivered; both Sent folders and inboxes in the mailbox copies show them (the Gmail copy took a few minutes; it syncs on its timer).
- Hotmail times match local time (the 0.1.32 fix).

Bugs / findings:
1. Dark theme: the Options page keeps a white background while its text turns light, so the title, "General", "Archive files" and the checkbox label are unreadable. The main window, list and reader are fine in dark. (Theme set back to Light.)
2. The app does not follow GNOME's dark preference (`color-scheme 'prefer-dark'`); dark is only the manual option.
3. The Open PST chooser was titled "Open a PST archive read-only" although files open editable (owner: editable is the intended default). Title changed in source to "Open Outlook Data File" (`MainWindow.axaml.cs`, `OpenPstClicked`); not yet built into a package.
4. File > Open & Export says every first write makes a backup copy, while Options > General has the ".bak copy" box unticked: one of the two texts is wrong.
5. Seen once: after a sign-in repair the Calendar kept "Calendar access is not saved" until the view was switched away and back (the ribbon Refresh did not re-read the account). The owner could not reproduce it by hand; may be a test artefact. The repair button gives no feedback while the browser sign-in is open.
6. People list is not alphabetical (Brett, Michael, Anthony, ...); the status bar can stay on "Loading contacts..." after leaving People.
7. Coming back from File > Options left no folder selected ("Select a folder to begin").
8. Window width shrinks by under 1 logical px per restart (1982.90 -> 1982.07); harmless drift.
9. The log reports "OpenOutlook 1.0.0.0" instead of the package version.
10. One `mark-read` PATCH got Graph 404 at the first start (message probably gone from the server; the copy had not synced since 2026-10-06); it fell back to the local overlay as designed.
11. The contact window's ribbon is clipped at the right edge at its default width.
12. (Withdrawn: File > Info "Mailbox Settings" does list the Gmail copy once it is open.)

### 11.1 Second round (same evening, package 0.1.33)

Fixed and built as **0.1.33** (`publish/openoutlook_0.1.33_amd64.deb`, also copied to `publish/openoutlook_current_amd64.deb`; not committed yet):
- **Inline pictures listed as attachments** (owner: Outlook does not do that). Hotmail/Graph and Gmail messages showed every picture the HTML body displays by `cid:` as an attachment chip (the Proton newsletter had 11). Now one rule for all three paths, `PstMessageHeader.IsInlinePicture(contentId, html)` (the rule the PST reader already used): chips, the ribbon Save Attachment button and the Save chooser leave those out; the full list is still used to draw the body's pictures. Gmail now reads each part's `Content-ID` (`GmailAttachmentInfo.ContentId`). The PST Save chooser also uses `VisibleAttachments`. Checked in the app: the Proton message has no chips, its pictures show, Save Attachment is disabled. Tests: `PstMessageHeaderTests.A_picture_is_inline_only_when_the_body_shows_it_by_cid` (6 cases), `GmailMailboxTests.Reads_the_content_id_of_an_inline_picture`.
- **Builds with the .NET 8 SDK** (this machine has only 8.0.131): three `Split([',', '\n', '\r', ';'], ...)` calls were ambiguous for the C# 12 compiler (CS0121); now `new[] { ... }` (`JunkKeywordFile.cs`, `AccountSettingsWindow.Junk.cs`).
- Open PST chooser title (finding 3).
- Bare-metal build notes: `native/openpst/build-linux-x64/CMakeCache.txt` pointed at a `ninja` in an old Claude scratch directory; deleting the cache fixed `build-native.sh` (ctest 3/3). The unit tests had failed with `EntryPointNotFoundException: opst_create_ex` until the native library was rebuilt. Unit tests: **621/621** on this machine.
- Installing: a sudoers rule `/etc/sudoers.d/openoutlook-dev` lets the owner's account run exactly `/usr/bin/apt install /mnt/SOLIDIGM/Claude/OpenOutlook/publish/openoutlook_current_amd64.deb` without a password (sudo-rs rejects wildcards in arguments; `-y` is not allowed by the rule). Copy a new build to that name, then `sudo -n` that exact command.

Passed in this round: attachment **Open** (chip menu > Open: Loupe opened the picture through `xdg-open`, status "Opened ..."); Data Files **Open File Location** (Nautilus received the folder; owner to confirm the window), **Remove** ("closed; the file was not deleted"), **Add by path...** (attached and opened editable); Junk Cleaner tab (keywords, log) and the **Clean Junk** preview (100 of 185 match, reasons shown; cancelled, nothing moved); column header context menu.

More findings:
13. After installing 0.1.33 the app opened small (900 x 560) on the left monitor; the owner moved it. The saved layout before had been maximised on the right monitor (the 19:37 exit may have saved it un-maximised). Re-check: maximise, close, reopen.
14. After that small start the message list's Subject column kept all the width: Received is cut to "10/15" and Size is gone. Star-column widths are not rebalanced when the window grows.
15. Add by path hint text is Windows-only ("X:\email\archive.pst or \\server\share\...") on Linux. The dialog widens to fit a long path, moving its buttons.
16. Attachment chips open their menu on left-click only; right-click does nothing.
17. Linux keyword list has 12 keywords; the Windows list (145) has not been copied (section 10).

### 11.2 Third round

Passed: attachment **Save as...** (GNOME folder picker, picked by the owner: `~/Downloads/smoke-101600.txt`, 40 bytes, status "Attachment saved to a new file"; real attachments still get chips); **sort by Received** both ways keeps the date groups; **drag and drop** of a Hotmail message from Inbox to archive (owner's real mouse; a drag injected with ydotool selects the row but never starts `DoDragDrop`, so drag tests need a person) and **Move to Folder...** back (picker greys the current folder; message back in place).

18. **Fixed in 0.1.34.** Moves in the Hotmail mailbox copy took ~3 s to show, by drag and by Move to Folder: `MoveOnlineAsync` asked Graph first (token, one move per message) and only then refreshed the list, and moved ids were not in the local overlay. Now the rows leave the list at once through the same overlay as delete / archive (`ApplyCompletedMailAction(..., "archive")`), each id is stamped again when Graph confirms (so a sync that began in between cannot bring it back), and ids the server refuses are put back (`UndoLocalChange`). Checked in the app: the row is gone 0.3 s after OK, in both directions, and the message is in the target folder after the next sync. Tests: `MainWindowHeadlessTests.A_Microsoft_move_leaves_the_list_before_the_server_answers` (fails on the old code), `.A_Microsoft_move_the_server_refuses_comes_back`. Unit 621/621, headless 44/44 on this machine. **Gmail too, in 0.1.35:** rows leave the list at once when the move takes the folder's label off (`GmailMoveLeavesSource`; moves out of Sent / Drafts / Starred / Important only add a label and stay, as before), are stamped again when Gmail confirms, and come back with a counts refresh if Gmail refuses. Checked in the app on the owner's test mail (Inbox to the Travel label and back): gone 0.3 s after OK both ways, in the target folder after the next copy sync (the Gmail copy syncs ~20 s after a change, so the target folder lags that long; it did before as well). Test: `MainWindowHeadlessTests.A_Gmail_move_leaves_the_list_before_Gmail_answers_and_a_refused_one_comes_back` (fails on the old code; `FakeGmail.HoldModify` holds or refuses label changes). Unit 621/621, headless 45/45.
19. **Fixed in 0.1.36.** The Move / Copy picker built its own order (data files A-Z first, then accounts A-Z, Gmail labels A-Z) and opened at the top. `BuildTransferRoots` now walks the folder pane itself, so data files, accounts and folders appear as arranged there (`folder-order.json`), and `FolderPickerWindow` opens with the data file or mailbox the messages are in scrolled to the top (`TransferStart`). Also: a confirmed move or copy now syncs the mailbox copy at once (`SyncMirrorNow`; copies used to wait for the 15-minute sync, moves for the 4 s / 20 s batching delay), including cross-store transfers into or out of a mailbox; while a sync is running it falls back to the short delay. Owner tested: picker order and start position, moved and copied messages in the target folder right away (Hotmail and Gmail). Test: `TransferHeadlessTests.The_picker_follows_the_folder_pane_order_and_opens_at_the_data_file_the_messages_are_in` (fails with A-Z order). Unit 621/621, headless 46/46.

### 11.3 Check and Repair for every data file (0.1.37)

Owner: our own SCANPST replacement should be usable on Linux. It already was the engine behind File > Check & Repair (`opst_check` in `native/openpst/src/op_check.c`, the C port of `tools/python/pstcheck.py`; repair `opst_fix` in `op_fix.c`), but the window only checked the archive whose folder was selected, so the mailbox copies (not in the folder list) could not be checked. `ArchiveRepairWindow` now has a **Data file** drop-down with every open data file, the mailbox copies first ("address (mailbox copy)"), preselecting the archive or the mailbox copy behind the account on screen; choosing another file scans it. Repairing a mailbox copy holds the copy's sync off (`_mirrorBusy`) and is refused while a sync runs. On Linux the "cannot be repaired automatically" advice says to run SCANPST on a Windows PC. Test: `MainWindowHeadlessTests.Check_and_repair_offers_every_data_file_and_scans_the_one_chosen`; the fixture test uses the new constructor. Unit 621/621, headless 47/47.

Owner tested on Linux: both Linux-written mailbox copies (Hotmail, Gmail) scan clean; a PST created outside OpenOutlook scans clean; the two attached archives (`rmarrash_1.pst` = rmarrash_hotmail2 and `test-archive.pst` = rmarrash_outlook, under `/mnt/SOLIDIGM/deepseek-linux/projects/OpenOutlook/`) had problems, were repaired by the owner with this tool and then scanned clean. This covers item 10 with our checker; a SCANPST.EXE run on Windows against the same files is still the independent confirmation.

**Windows build cross-published from Linux (2026-10-15, 20:52) from `2be93b8`** into `F:\Claude\OpenOutlook_win` (`dotnet publish src/OpenOutlook.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`; only `OpenOutlook.Desktop.exe` and the `.pdb` files copied; `openpst.dll`, `chromium\` and `openoutlook-oauth.json` unchanged, no native change since `6f4846f`). The previous exe is kept as `OpenOutlook.Desktop.exe.6f4846f.bak` (rename it back to roll back). Valid x64 PE, **not yet started on Windows**: start it once and check an HTML message with inline pictures (no picture chips), a move (row leaves at once), the Move/Copy picker order, and Check & Repair's Data file list.

Still open: SCANPST.EXE on Windows against the Linux-written copies and the two repaired archives (independent check); the backstage left column overlaps itself in a short window ("Save Attachments" struck by a separator, "Office Account" on top of "Print").
