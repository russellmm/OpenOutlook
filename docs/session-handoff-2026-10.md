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
