# OpenOutlook build status

Status date: **2026-10-15**. OpenOutlook is a working personal mail client for **Windows 11** and **Linux** (Ubuntu 26.04, tested in WSL2/WSLg and, since 2026-10-15, walked through on a bare-metal GNOME Wayland desktop: `docs/handoff-linux-bare-metal-2026-10-06.md` section 11). A Hotmail/Outlook.com calendar is partly implemented on the same Microsoft sign-in. It is still not a release candidate: the first-release gates in `PRODUCT_REQUIREMENTS.md` are not all met (see the list at the end).

What exists, per platform and per feature, is in **`docs/FEATURES.md`**. How it is built is in `DESIGN_SPEC.md` (sections 1-10 are the original baseline, section 11 onwards the design as built). The detailed history of earlier sessions (through 2026-10-02, when the app was Linux-only, read-only for PSTs and used the managed PST reader) is kept in `docs/history/BUILD_STATUS-through-2026-10-02.md`; it describes a state that no longer exists in several places (see "Superseded" in `DESIGN_SPEC.md`).

## Current state

- **Repository:** `github.com/russellmm/OpenOutlook`, `main` branch.
- **Tests (2026-10-16, `8929173`):** Windows: 692 unit tests pass plus the 5 known `OfflineMessageCacheTests` failures (Unix file permissions), 67/67 headless UI tests, C library `ctest` 3/3 (`test_write` 188 checks plus the clean-file guard on the owner's SCANPST-clean PST). WSL: 697/697 unit, 67/67 headless, deb smoke OK. `python scripts/check-edge-cases.py` passes. `.github/workflows/linux.yml` runs the Linux build, tests, packaging, and a start of the packaged program on every push to `main`.
- **Published builds**
  - Windows: publish locally with `dotnet publish` as described in `docs/session-handoff-2026-10.md`; keep `openpst.dll`, `openoutlook-oauth.json`, and the `chromium\` folder beside the executable. Published binaries and the OAuth configuration are not tracked by Git.
  - Linux: `publish/openoutlook_<version>_amd64.deb` (currently 0.1.61 from `29b1492`, which shows its version and commit in File > Account > About; on an NTFS checkout set `OO_DEB_STAGE` for `scripts/build-deb.sh`; the deb ships an AppArmor profile for the bundled browser) and `publish/OpenOutlook-linux-x64.tar.gz`, built by `scripts/wsl-package.sh`.
- **PST engine:** OpenPST (C, `native/openpst`) reads and writes Unicode PST files; SCANPST.EXE reports NO_ERRORS on the owner's files and on files created and soaked by the engine (`docs/native-engine-status.md`).
- **Accounts:** Hotmail (Microsoft Graph) and Gmail (Gmail API) connected and used live by the owner on Windows; Linux sign-in uses libsecret; the owner connected both Hotmail and Gmail in WSL (2026-10-05).
- **Calendar:** personal Hotmail/Outlook.com account through Microsoft Graph, using the same account and token as mail. Home/View ribbon, two mini months, four views, and appointment/meeting creation are built; the owner verified a new appointment in Outlook on Windows (2026-10-09). **Reminders** (2026-10-16): Graph `reminderView` checked every minute (Options > Calendar), reminders missed while closed shown up to 30 days back, Outlook-like Reminders window with Dismiss / Snooze applied everywhere, a dismissed reminder never shown again, real Reminder box; the owner confirmed they work. Multiple calendars at once, overlay, event details, offline calendar data, and scheduling tools remain. See `docs/calendar.md`.
- **Mailbox copies:** both account types keep a local PST copy; folders, message bodies and most actions work from it (`docs/offline-mirror-plan.md`, `docs/FEATURES.md` section 4).
- **Reading pane:** the action row wraps in narrow panes, with alternate reader, trusted original HTML, browser, printable PDF, and zoom under **More**. Visible embedded images load automatically; **Show images** explicitly downloads external pictures for the selected message. Microsoft inline images can be read from the local mailbox copy or Graph attachment data. Hidden tracking images are omitted from loading.
- **Selection after deletion (2026-10-11):** the reader clears when the highlighted message changes, including when a deleted row is replaced without a selection event. A Microsoft message body already in the local mailbox copy appears before any token or attachment request. A headless regression test covers the replacement highlight; the Windows executable was republished to `publish/win-x64/`.
- **Rapid repeated deletion (2026-10-11):** Windows' embedded HTML reader can report completion of the previous message while navigating to the next. The old page is kept hidden until a unique marker in the new document is verified; a delayed result from an older selection cannot reveal that page. If the embedded reader cannot verify the new document, it falls back to the snapshot reader. Marker tests cover consecutive navigations.
- **Headless verification (2026-10-11):** a synthetic Microsoft mailbox test now sends Delete twice through the keyboard handler, delays the second message's HTML response, and checks the selected row, header, and body after the delayed response arrives. Its captured screen shows the third message throughout. All 33 headless tests passed on Windows. The native WebView2 surface cannot attach to the headless runner, so its page-paint timing still needs a Windows desktop check.
- **Linux headless test setup (2026-10-12):** the two deletion regression tests and narrow reading-pane test share `ShowWithSnapshotReader`. When WebKitGTK is unavailable, `MainWindow` detaches the native view during construction; its name still resolves but its `Parent` is null. The helper removes the control only if it still has a panel parent, disables native rendering, shows the window, and drains the UI dispatcher before assertions. The narrow-pane test exercises both attached and already-detached native views so Windows runs also cover the Linux setup. This addresses the three Linux CI null-reference failures without requiring WebKitGTK or changing the desktop application.
- **Junk Cleaner:** built for Microsoft accounts (manual with preview, automatic, import, log).
- **Junk lists (2026-10-16):** Safe Senders, Safe Recipients, Blocked Senders and blocked top-level domains: right-click / Home > Junk menu (Block Sender, Never Block Sender / Domain / Group, Not Junk), Junk Email Options window (Outlook's five tabs, Import / Export), Outlook's own lists read read-only from Outlook desktop's .ost when Outlook is closed (Windows), lists applied by OpenOutlook every 5 minutes. Microsoft Graph cannot read or change Outlook's lists (`docs/junk-lists-plan.md`).

## Commands

```
dotnet build OpenOutlook.sln
export OPENOUTLOOK_TEST_PST="F:/Claude/OpenOutlook/rmarrash_2.pst"        # optional private fixture (copy of the owner's small archive)
dotnet test tests/OpenOutlook.Tests
dotnet test tests/OpenOutlook.HeadlessTests
powershell scripts/build-native.ps1          # C library for Windows (after any C change)
bash scripts/build-native.sh                 # C library for Linux
python scripts/fetch_chromium.py             # the bundled browser (win64 and linux64) into third_party/chromium
MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/wsl-dotnet-test.sh    # unit tests on Linux (in WSL)
MSYS_NO_PATHCONV=1 wsl bash /mnt/f/Claude/OpenOutlook/scripts/wsl-package.sh 0.1.13 # Linux packages
```

Never commit private PSTs, logs, tokens, `openoutlook-oauth.json`, `.secrets/` or real message contents; never write to the owner's `rmarrash_*.pst` files in tests (work on copies).

## Open items before a first release

1. Bare-metal Linux: the desktop walk-through is done (2026-10-15: start, dialogs, keyring sign-in, xdg-open, reading, sending, moves, Check and Repair; handoff section 11). Left: the WebKitGTK interactive reader (the Wayland desktop uses the snapshot reader), dark mode (Options page unreadable) and a SCANPST.EXE cross-check on Windows.
2. Complete the Calendar work listed in `docs/calendar.md`; People/contacts, Tasks, signatures, spelling, rules, undo/redo, and Automatic Replies are not built.
3. Offline sending (Outbox) and queued actions for the mailbox copies; folder/label rename and delete from a copy.
4. Cross-store full-text search (only selected-folder header search exists).
5. Gmail: draft editing, permanent delete (needs a broader scope), Google consent screen still in Testing mode with a shared project name.
6. Native print dialog; full-fidelity EML of inline images and original headers.
7. Dark mode, high-DPI and large-archive (4 GB) verification; dependency vulnerability review.
8. Window position cannot be remembered under WSLg (always opens on the primary monitor).
9. Options dialog values are stored but most do not change behaviour yet.
