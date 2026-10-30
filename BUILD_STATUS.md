# OpenOutlook build status

Status date: **2026-10-30**, build **0.1.88** (`5935a8a`). OpenOutlook is a working personal mail client for **Windows 11** and **Linux** (Ubuntu 26.04 on a bare-metal GNOME / Wayland desktop, plus WSL2/WSLg and CI), used daily by its owner on both. It is still not a release candidate: the first-release gates in `PRODUCT_REQUIREMENTS.md` are not all met (see the list at the end).

What exists, per platform and per feature, is in **`docs/FEATURES.md`**. How it is built is in `DESIGN_SPEC.md` (sections 1-10 are the original baseline, section 11 onwards the design as built). The chronological working log is `docs/session-handoff-2026-10.md`; older history is in `docs/history/` (the state through 2026-10-02, when the app was Linux-only and read-only for PSTs, is in `docs/history/BUILD_STATUS-through-2026-10-02.md`).

## Current state

- **Repository:** `github.com/russellmm/OpenOutlook`; work happens on `native-engine-phase0`, which is pushed to `main` after every step (both point at the same commit).
- **Tests (2026-10-29, `e4c2a34`, Windows):** 748 unit tests pass plus the 5 known `OfflineMessageCacheTests` failures (Unix file permissions; they cannot pass on Windows), 80/80 headless UI tests; `python scripts/check-edge-cases.py` passes (every edge case in `docs/EDGE_CASES.md` names an existing test). Last Linux run (2026-10-30, 0.1.87 = 0.1.88 source, bare metal): unit 756/756, headless 82/82 (Windows not run since 0.1.84). C library: `ctest` 3/3 (`test_basic`, `test_formats`, `test_write` with 188 checks and the clean-file guard; last run 2026-10-16, no C changes since). `.github/workflows/linux.yml` runs the Linux build, tests, packaging and a start of the packaged program on every push to `main`.
- **Published builds (0.1.88, 2026-10-30)**
  - Windows: `F:\Claude\OpenOutlook_win` (`OpenOutlook.Desktop.exe` + `openpst.dll`, `openoutlook-oauth.json`, `chromium\`), published locally with `dotnet publish` (see `README.md`); 0.1.88 was cross-published from Linux (exe + pdbs; not yet started on Windows). The version and commit show in File > Account > About.
  - Linux: `publish/openoutlook_0.1.88_amd64.deb` (installed on the bare-metal box) (+ `publish/openoutlook_current_amd64.deb`) and `publish/OpenOutlook-linux-x64.tar.gz`, built by `scripts/wsl-package.sh` and checked with `scripts/wsl-deb-smoke.sh`. Every Windows publish also builds the Linux packages with the same version. Published binaries and the OAuth configuration are not tracked by Git.
- **PST engine:** OpenPST (C, `native/openpst`) reads and writes Unicode PST files, creates new ones and checks and repairs them (fixer rules R1-R13, format rules 1-43). The owner's three main PSTs, repaired by OpenOutlook's Check and Repair, scan clean in SCANPST.EXE (2026-10-18); a clean-file guard proves the fixer leaves SCANPST-clean files byte-identical (`docs/native-engine-status.md`).
- **Accounts:** Hotmail (Microsoft Graph) and Gmail (Gmail API) used live on Windows and on the bare-metal Linux desktop (libsecret). New mail shows within a minute (light check, F9 = Send/Receive All); a red dot (Windows) or dock badge (Linux) marks new mail while the window is in the background.
- **Mailbox copies:** both account types keep a local PST copy; folders, message bodies and most actions work from it (`docs/offline-mirror-plan.md`, `docs/FEATURES.md` section 4). Saved drafts appear in Drafts at once.
- **Reading:** sanitized HTML laid out by the bundled headless Chromium (snapshot tiles with character-level text selection) or the embedded WebView2 on Windows; Trust Center picture downloads, tracking pixels skipped, colours as written. Heavy CSS-picture mailings no longer stall the reading pane (0.1.80). Message pictures are taken at the display scale, so text is sharp on HiDPI screens (0.1.87).
- **Composing:** Outlook-style compose window with HTML edited in place (Format Text tab, pictures as inline parts), recipient suggestions, Select Names, Check Names, Yes / No / Cancel on close. On GNOME / Wayland the same editor runs in the bundled headless Chromium and is drawn into the body, so it is edited in place there too (0.1.85; full resolution since 0.1.87). Sent Items catches up a few seconds after Send (0.1.87).
- **Calendar and People:** Hotmail calendar (four views, create / change / delete events, reminders window); Microsoft contacts in People (create / edit / delete). See `docs/calendar.md`, `docs/people.md`.
- **Junk mail (Microsoft accounts):** Junk Cleaner with keywords and 18 rules (sender not verified, fake address, pretends to be a company, no To, Bcc, crowd, emoji subject, bulk rating, ...) behind an **Other rules...** window, preview, automatic cleaning and log; Outlook's junk lists (Safe / Blocked Senders, Junk Email Options, Outlook's own lists read from its `.ost`); **Inbox spam check** (0.1.82-0.1.83: suspects ticked in the list's first column, Block Sender and Delete / Delete) and **Junk Email marks** (0.1.84: the ticks show what the Junk Cleaner will delete; unticked messages are kept by every clean).
- **Interface:** responsive ribbon, column header menu, pane dividers that cannot collapse a pane (0.1.81), View > Change View > Default View, log viewer, theme-aware drag cursors on Linux.

## Commands

```
dotnet build OpenOutlook.sln
export OPENOUTLOOK_TEST_PST="F:/Claude/OpenOutlook/rmarrash_2.pst"        # optional private fixture (copy of the owner's small archive)
dotnet test tests/OpenOutlook.Tests                                        # Windows: 5 OfflineMessageCacheTests fail by design
dotnet test tests/OpenOutlook.HeadlessTests
python scripts/check-edge-cases.py
powershell scripts/build-native.ps1          # C library for Windows (after any C change)
bash scripts/build-native.sh                 # C library for Linux
python scripts/fetch_chromium.py             # the bundled browser (win64 and linux64) into third_party/chromium
MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/wsl-dotnet-test.sh       # unit tests on Linux (in WSL)
MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/wsl-package.sh 0.1.88    # Linux packages
MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/wsl-deb-smoke.sh         # start the packaged program as a check
tools/MailSmoke/bin/Debug/net8.0/MailSmoke.exe inboxspam ["subject text"]               # read-only: what the Inbox spam check marks, with headers
```

Never commit private PSTs, logs, tokens, `openoutlook-oauth.json`, `.secrets/` or real message contents; never write to the owner's `rmarrash_*.pst` files in tests (work on copies). MailSmoke's live steps use the real accounts (`sendtest` sends real mail).

## Open items before a first release

1. Calendar: several calendars at once, overlay, editing a whole recurring series, offline access, scheduling tools (`docs/calendar.md`). Tasks, signatures, spelling, rules, undo/redo and Automatic Replies are not built.
2. People phases 2-4: contacts in the mailbox copy, address book across accounts, contact groups, Gmail contacts (`docs/people.md`).
3. Offline sending (Outbox) and queued actions for the mailbox copies; folder/label rename and delete from a copy; Sent Items probably lags like Drafts did before 0.1.71.
4. Cross-store full-text search (only selected-folder header search exists).
5. Gmail: draft editing, permanent delete (needs a broader scope), Junk Cleaner / spam check for Gmail; the Google consent screen is still in Testing mode with a shared project name.
6. Native print dialog; full-fidelity EML of inline images and original headers.
7. Dark mode (the app does not follow GNOME's dark preference), high-DPI and large-archive (4 GB) verification; dependency vulnerability review.
8. Linux: the WebKitGTK interactive reader is not used under Wayland (snapshot reader instead); window position cannot be remembered under WSLg.
9. Options dialog: most stored values do not change behaviour yet.
10. Compose: Format Painter, multilevel lists, borders, Replace and signatures are not built.
