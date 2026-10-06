# Session handoff (2026-10-05)

Read this first when resuming. Longer background: `native-engine-status.md` (what exists and how it is validated), `OpenOutlook_Design_Document.md` (every PST rule found, rules 1-35), `accounts-setup.md` (Hotmail / Gmail), `OpenOutlook_Avalonia_Integration_Plan.md` (phases, backlog), `Ctools.MD` / `PythonTools.MD` (tool references).

## State
- Repo `github.com/russellmm/OpenOutlook`, work branch `native-engine-phase0`, merged to `main` after every step (both point at the same commit). The app (Avalonia 11.2.3, .NET 8) reads and writes PST files through the vendored C library `native/openpst`, and has Hotmail (Microsoft Graph) and Gmail accounts.
- Everything below is done and pushed; the published Windows build is `F:\Claude\OpenOutlook_win\OpenOutlook.Desktop.exe` (+ `openpst.dll`, `openoutlook-oauth.json`).

## Build, test, publish, push
```
dotnet build OpenOutlook.sln
export OPENOUTLOOK_TEST_PST="F:/Claude/OpenOutlook/rmarrash_2.pst" OO_HEADLESS_OUT="F:/Claude/OpenOutlook/.local/headless"
dotnet test tests/OpenOutlook.Tests          # expected: 5 failures = OfflineMessageCacheTests (unix file permissions, cannot pass on Windows)
dotnet test tests/OpenOutlook.HeadlessTests  # expected: all pass
powershell scripts/build-native.ps1          # C library -> src/OpenOutlook.Desktop/runtimes/win-x64/native (needed after any C change)
powershell scripts/build-native-tools.ps1    # CLI + C tests in native/openpst/build-tools
python scripts/fetch_chromium.py                # once (and when scripts/chromium-version.txt changes): the pinned headless Chromium, 115 MB per platform, into third_party/chromium (git-ignored)
# publish (close the running exe first: taskkill //F //IM OpenOutlook.Desktop.exe)
dotnet publish src/OpenOutlook.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o /f/Claude/OpenOutlook_win
cp src/OpenOutlook.Desktop/runtimes/win-x64/native/openpst.dll /f/Claude/OpenOutlook_win/
```
- Soak test of the PST engine: `tools/PstSoak` (needs two SCANPST-clean files; see the design document), SCANPST driver `tools/python/run_scanpst2.ps1 -Repair`.
- **Push:** this repo has its own credential helper (`.git/config`, `F:\Claude\git-cred-ghtoken.sh`) that reads the GitHub token from `F:\Claude\gh_token`, so Git Credential Manager never opens a sign-in window. Use `GCM_INTERACTIVE=never GIT_TERMINAL_PROMPT=0 git push origin native-engine-phase0:main`. If a push hangs, a stuck `git-credential-manager` process is waiting for a GUI sign-in: kill it. Never commit `gh_token`, `.secrets/`, or `openoutlook-oauth.json` (all ignored).
- Credentials: Google client id/secret were provided in `.secrets/` and merged into `openoutlook-oauth.json` (source tree and next to the exe). The consent screen is in Testing mode: refresh tokens last 7 days, the Gmail address must be a test user.

## Gotchas learned
- The bash tool collapses backslashes in heredocs (`\\n` becomes a newline, `\\0` a NUL): write files with the Write tool, or build escapes with `chr(92)` in Python. Large heredocs also break on quotes: use the Write tool for scripts.
- Python on Windows does not understand Git-Bash `/f/...` paths: use `F:/...`.
- A native `NativeWebView` paints above all Avalonia content: anything that overlays the main window must hide it (see `InitializeWebViewOverlayGuard`). In the headless test host it fails to start; the test host swallows that like the app does, and tests that only need construction do not `Show()` the window.
- Headless input: `MouseMove` needs `RawInputModifiers.LeftMouseButton` to count as a held button; drag events need `DragEnter` before `DragOver`; the window-level handler with `handledEventsToo` shows the final drag effect.
- The folder pane has two drag systems (folder reordering, message drops). Handlers must not touch drags that are not theirs.
- Windows git: SCANPST scans may leave `.log` files next to scanned PSTs; the test PSTs (`rmarrash_*.pst`) are the owner's data: only ever work on copies.

## Open items / ideas
0. **Future feature: offline sending (Outbox)** for the mailbox copies; see section 9e of `offline-mirror-plan.md`. Not needed now.
1. Gmail: editing a saved draft; drag onto Gmail labels works.
2. Hotmail: unread counts in the Move picker only come from the folder list loaded at startup.
3. Google consent screen shows "Home Assistant 13" (shared project): rename it or create a separate project and swap the client id/secret.
4. PST engine: the 3 broken tables of `test-archive.pst` are not rebuilt by any rule; ANSI/4K files are read-only; the soak harness does not cover folder rename/move; Python fixer lacks R10.
5. Offline mailbox mirror (PST per account, user-chosen location): see `offline-mirror-plan.md`.
6. Plan backlog (section 11): Calendar/People/Tasks, categories, undo/redo, address book, rules, Graph write for archive-to-mailbox, packaging for Linux, fuzzing/ASAN CI, 1 GB perf pass.
6. Linux: the app starts under Xvfb in WSL; real sign-in on Linux (libsecret keyring) has not been exercised by the owner yet.

## In progress: Gmail compose / reply / forward / attachments + new compose window (2026-10-05)
Done and committed (builds; 450 .NET tests pass, 13 headless pass):
- Provider (`src/OpenOutlook.Providers.Google`): `GmailMimeBuilder` (RFC 5322/MIME, header-injection checks, RFC 2047, 25 MB limit), `GmailMailbox.SendAsync / SaveDraftAsync / SendDraftAsync / GetAttachmentAsync`, attachment ids and thread id in `GmailContent`. Tests: `GmailSendTests`.
- Scopes: Gmail sign-in now requests `gmail.modify` + `gmail.compose`; `ConnectedAccount.CanSendGmail`. Accounts connected earlier must sign in again to send.
- Compose abstraction (`ComposeBackends.cs`): `IComposeBackend` with `GraphComposeBackend` (server drafts) and `GmailComposeBackend` (MIME); `ComposeAccount` list for the From picker; `ComposeSeed` for reply/forward.
- New `ComposeWindow.cs`: Outlook-like layout (quick access, Message/Insert/Options/Format Text/Review/Help tabs, Send, From account list, To/Cc/Bcc, Subject), "To be implemented" tooltips on unbuilt buttons; From can change until a draft is saved.
- `MainWindow.Compose.cs`: `OpenCompose`, `BuildComposeAccounts`, `BuildGmailSeedAsync` (reply, reply all, forward incl. attachments), quoting; Gmail reading-pane Reply/Reply All/Forward buttons; Gmail attachment saving (`SaveGmailAttachmentAsync`).
Tests added (15 headless pass): Gmail reply / reply all / forward preparation (headers, no self-reply, attachment download), compose window tooltips, From locked once a draft is saved, seed files reach the backend.
Still to do / verify:
1. Real-world check by the owner: sign in to Gmail again (new scope; add `gmail.compose` to the consent screen), send / reply / forward / save attachment; Hotmail compose in the new window (edit existing draft, reply/forward drafts, attachments).
2. Known gaps: editing a saved Gmail draft is not supported (`CanReopenDrafts = false`); Paste icon is a placeholder; the ribbon is not yet scrollable-friendly at narrow widths.

## Bundled headless Chromium (2026-10-05)
The reading pane lays HTML out with a headless browser. Edge 154 stopped starting headless on the owner's PC (every layout fell back to a basic preview), so the application now ships Google's chrome-headless-shell (Chrome for Testing, version pinned in `scripts/chromium-version.txt`, downloaded by `scripts/fetch_chromium.py` to `third_party/chromium/<platform>`). The Desktop project copies it to a `chromium` folder beside the exe at build and publish (about 270 MB unpacked on Windows). `BrowserHtmlRenderer` tries it first, then any installed Edge / Chrome / Chromium (the one that starts is remembered), and `BrowserProcessTracker` closes browsers left behind by earlier runs. Linux uses the `linux64` build the same way (not yet tested on a Linux desktop). Without the folder (a plain `dotnet build` before fetching) the installed browsers are used as before.

## Look and feel (2026-10-05)
- Responsive main ribbon (`RibbonResponsiveLayout.cs`): groups collapse from the right into drop-down buttons as the window narrows.
- Sync indicator in the status bar (`SyncIndicatorModel.cs`, `MainWindow.Mirror.cs`): worst state wins; click opens Account Settings > Data Files.
- Account Settings / Data Files themed like the app (`AccountSettingsWindow.cs`, `ListBox.olList` in `Assets/OutlookStyles.axaml`): icon toolbar, drawn default check mark, status dots.
- Next: other look-and-feel items (compose icons, reading pane buttons, folder icons, dark mode), then the Linux side.

## Performance (2026-10-05)
Measured live (MailSmoke `timing [cache]`): a Graph request is about 150-300 ms. Done: account check (/me) remembered per token (`GraphAccountVerification`, enabled at app start-up only), folder details and message list fetched in parallel, HTTP/2 preferred, folder list shown from the last-seen page while the fresh one loads, unread numbers taken from the page itself and the folder tree re-read at most once a minute, token + connection warm-up at start-up.
Next (bigger): serve live folders and messages from the local mailbox copy (PST), optimistic UI for read/flag/delete, body prefetch.

## Local-first folders (2026-10-05)
Folders of a Microsoft account are now shown from its mailbox copy (`LocalMailboxReader` in OpenOutlook.Mirror, wired in `MainWindow.LocalFolders.cs` and `LoadMicrosoftFolderAsync`): about 10 ms instead of 0.5 s. Message ids stay server ids, so reading, flagging, moving and deleting still go to Graph. Changes made here (`_localRemoved/_localFlags/_localReads`) show at once and are dropped when the next sync has caught up; a sync is requested 4 s after a change, when a folder is opened and the last sync is over 90 s old, and by the Refresh button. After a sync that changed something the open list reloads.
Not yet local: message bodies and attachments (still Graph, about 0.25 s), the folder tree and its counts, Gmail accounts, move/drag between folders (relies on the next sync). List shows the newest 500 of a folder.
Update: message bodies now come from the copy too (`LocalMailboxReader.ReadBody`, 0-3 ms): the reading pane shows the body at once; for messages with attachments the chips (and for Gmail the reply data) follow when the server answers. Gmail folders use the same local-first list (`LoadGmailFolderAsync`), overlay and sync triggers. Still network: attachments, folder tree / label counts, drag-move until next sync.
Gmail actions (2026-10-05): a single Gmail call takes about 0.26 s (MailSmoke `gmailtiming`), but a no-change mailbox copy pass was 4.2 s of API traffic (`gmailsync`; now 2.5 s: unread/starred asked once per pass, not per label) and ran after every change. Now: read/star/archive/trash change the list at once and Gmail is told in the background (queued, undone on refusal); the copy catches up 20 s after the last change (Gmail) and a stale folder triggers a pass only after 5 min; overlay entries survive a pass that began before the change; label counts are adjusted locally and re-read 8 s after the last action.
Microsoft actions (2026-10-05): read/unread, flag/unflag, archive and delete (outside Deleted Items) now change the list at once and are sent in the background in order (`MainWindow.MicrosoftActions.cs`); a refusal restores the list. Deleting inside Deleted Items still asks and waits for Microsoft.
App icon: `python scripts/generate_icon.py` draws `Assets/openoutlook.ico/.png/-512.png` (blue rounded square, white envelope, open amber ring); the exe icon comes from `ApplicationIcon`, the main window from `Icon=`. Other windows (compose, dialogs) do not set an icon yet; the Linux .desktop/deb should use `openoutlook-512.png`.
Message list columns (2026-10-05): paperclip scaled down (icon templates are 32x32 canvases and do not scale by themselves: wrap in a Viewbox); right-click on a column header opens the header menu (Arrange By, Reverse Sort, Field Chooser, Remove This Column, Group By This Field; Group by Box and View Settings are placeholders) instead of the message menu (`MainWindow.ColumnMenu.cs`); Importance and Flag columns exist but start hidden, shown through the Field Chooser or the menu; their visibility is saved in view-layout.json (5-column files from before still load). Importance comes from Graph `importance`, the PST, and the local copy; Gmail messages show normal importance.

## Linux (2026-10-05)
Checked in WSL (Ubuntu, .NET 8, Xvfb): unit tests 531/531 and headless UI tests 21/21 pass on Linux; the bundled chrome-headless-shell 154 starts and all its libraries are present.
Scripts (all run with `MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/<name>`): `wsl-dotnet-test.sh` (native lib + unit tests), `wsl-linux-check.sh` (headless UI tests + browser check), `wsl-package.sh [version]` (self-contained publish, tar.gz, .deb into `publish/`, built from a copy in ~/oo_pack so the Windows obj folders are untouched), `wsl-deb-smoke.sh` (unpacks the .deb, starts it under Xvfb, self-test render through the bundled browser).
Fixed: the chromium files were being embedded in the single-file exe on Linux (306 MB exe, no `chromium/chrome-headless-shell` on disk): the Desktop csproj now marks them `ExcludeFromSingleFile`. The .deb (118 MB) installs to /opt/openoutlook with an /usr/bin/openoutlook launcher, desktop entry and icons; Depends come from the browser's own deb.deps plus libsecret and xdg-utils.
Not done / unknown: real sign-in with the libsecret keyring on a Linux desktop; WebKitGTK interactive reader (not installed in WSL, the app falls back to the browser snapshot, as designed); Linux file-opening helpers (xdg-open) for attachments and "Open file location" are untested; no CI job yet.
CI: `.github/workflows/linux.yml` (pushing it needs a token with the `workflow` scope: `.secrets/gh_classic`) (ubuntu-24.04, on push to main, pull requests and by hand): native build + ctest, unit tests, headless UI tests under xvfb, fetches the Linux browser, builds the tar.gz and .deb, runs `scripts/wsl-deb-smoke.sh` (starts the packaged program under Xvfb and checks the self-test render), uploads the packages as an artifact. The private PST fixtures are not in the repo, so tests that need one skip themselves there.
WebKitGTK: the app probes libwebkit2gtk-4.1 / 4.0 (GTK3, what Avalonia's web view uses). The GTK4 packages (webkitgtk-6.0) do not count. With libwebkit2gtk-4.1-0 installed in WSL the program starts normally and the interactive reader is not disabled; the embedded view cannot be judged under Xvfb (needs a real desktop).
Network drives (2026-10-05): the PST engine opens files on mapped drives (X:\) and UNC paths (\server\share) fine (MailSmoke `openpath`, read-only). A program running as administrator does not see mapped drives made in a normal session (Windows keeps them per token; the usual remedy is the HKLM EnableLinkedConnections registry value, which is not set on this PC): OpenOutlook now says so in the status bar when it is elevated, and Account Settings > Data Files has "Add by path…" for typing a mapped-drive or UNC path when the file dialog does not list the share. The cause of the owner's report has not been confirmed.
Linux launcher sets EGL_LOG_LEVEL=fatal (WSLg has no DRI3; Mesa's two warnings on every start are harmless). .deb 0.1.1.
WSL drives in the file dialog (2026-10-05): the GTK dialog in WSL only lists "Computer /", so the Windows drives under /mnt were hard to find. `WslDriveBookmarks` (start-up, WSL only) adds every single-letter /mnt/<x> to ~/.config/gtk-3.0|gtk-4.0/bookmarks without touching other bookmarks. Network drives V: W: X: Y: must also be mounted in WSL (/etc/fstab drvfs lines, see the wsl-network-drives memory note).
Linux PST open bug (2026-10-05): in the published single-file Linux build the PST engine could not be found ("The native PST library is not available"), so every data file failed to open; Account Settings showed nothing because the failure only went to the (hidden) status bar. Fixed: `package-linux-x64.sh` puts libopenpst.so beside the program (and in the tar.gz/.deb), failed opens are now logged (`[pst-open]`) and reported in Account Settings, the deb smoke test checks the library is in the package. `scripts/wsl-open-arg-test.sh` starts the installed program with a PST path and shows the log. Lesson: the earlier Linux smoke test never opened a PST; it should (a fixture is private, so CI cannot).
Invisible mouse cursor in OpenOutlook under WSLg (2026-10-05): not the app. A running "Remote Desktop" program (killed in Task Manager by the owner) made the cursor vanish over WSLg windows; an XCURSOR launcher override was tried first and removed again (.deb 0.1.6).
WSL sign-in (2026-10-05): the keyring service needs a persistent default keyring ("Default keyring", created through GNOME Keyring's password window, triggered with `scripts/wsl-create-keyring.sh`); until then the Accounts window says "A persistent unlocked default keyring is required". `scripts/wsl-keyring-diag.sh` shows the state. The deb launcher sets BROWSER to /opt/openoutlook/wsl-open (explorer.exe) under WSL so sign-in pages open in the Windows browser. .deb 0.1.7.
WSLg windowing (2026-10-05), owner reports: (1) the window always opened on the second monitor: WSLg's X server places the Windows left monitor at x=0 and the program cannot read its own window position (saved view-layout.json had -22,-22, the top-left of that monitor), so under WSL the saved position is no longer restored (size and maximised state still are) and Windows places the window. (2) menus/dialogs painted under the main window: Linux now draws popups inside the window (`X11PlatformOptions.OverlayPopups`), and under WSL every window except the main one is Topmost while open. Both are untested on the real WSLg desktop (the owner must try). Fallback ideas if dialogs still misbehave: ShowInTaskbar=false on dialogs, or running under Wayland (GDK_BACKEND/AVALONIA backend) instead of X11.
