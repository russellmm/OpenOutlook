# OpenOutlook build status

Status date: **2026-10-10**. OpenOutlook is a working personal mail client for **Windows 11** and **Linux** (Ubuntu 26.04, tested in WSL2/WSLg). A Hotmail/Outlook.com calendar is partly implemented on the same Microsoft sign-in. It is still not a release candidate: the first-release gates in `PRODUCT_REQUIREMENTS.md` are not all met (see the list at the end).

What exists, per platform and per feature, is in **`docs/FEATURES.md`**. How it is built is in `DESIGN_SPEC.md` (sections 1-10 are the original baseline, section 11 onwards the design as built). The detailed history of earlier sessions (through 2026-10-02, when the app was Linux-only, read-only for PSTs and used the managed PST reader) is kept in `docs/history/BUILD_STATUS-through-2026-10-02.md`; it describes a state that no longer exists in several places (see "Superseded" in `DESIGN_SPEC.md`).

## Current state

- **Repository:** `github.com/russellmm/OpenOutlook`, `main` branch.
- **Tests:** the solution builds on Windows; the five Calendar Graph unit tests and two focused Calendar headless UI tests pass. On 2026-10-10, all 15 focused Graph attachment and safe HTML tests and the narrow reading pane headless UI test passed. Five existing Windows unit tests depend on Unix file permissions. C library tests `test_basic`, `test_formats`, and `test_write` (168 checks) pass. `.github/workflows/linux.yml` runs the Linux build, tests, packaging, and a start of the packaged program on every push to `main`.
- **Published builds**
  - Windows: publish locally with `dotnet publish` as described in `docs/session-handoff-2026-10.md`; keep `openpst.dll`, `openoutlook-oauth.json`, and the `chromium\` folder beside the executable. Published binaries and the OAuth configuration are not tracked by Git.
  - Linux: `publish/openoutlook_<version>_amd64.deb` (currently 0.1.23; on an NTFS checkout set `OO_DEB_STAGE` for `scripts/build-deb.sh`; the deb ships an AppArmor profile for the bundled browser) and `publish/OpenOutlook-linux-x64.tar.gz`, built by `scripts/wsl-package.sh`.
- **PST engine:** OpenPST (C, `native/openpst`) reads and writes Unicode PST files; SCANPST.EXE reports NO_ERRORS on the owner's files and on files created and soaked by the engine (`docs/native-engine-status.md`).
- **Accounts:** Hotmail (Microsoft Graph) and Gmail (Gmail API) connected and used live by the owner on Windows; Linux sign-in uses libsecret; the owner connected both Hotmail and Gmail in WSL (2026-10-05).
- **Calendar:** personal Hotmail/Outlook.com account through Microsoft Graph, using the same account and token as mail. Home/View ribbon, two mini months, four views, and appointment/meeting creation are built; the owner verified a new appointment in Outlook on Windows (2026-10-09). Multiple calendars at once, overlay, event details, offline calendar data, and scheduling tools remain. See `docs/calendar.md`.
- **Mailbox copies:** both account types keep a local PST copy; folders, message bodies and most actions work from it (`docs/offline-mirror-plan.md`, `docs/FEATURES.md` section 4).
- **Reading pane:** the action row wraps in narrow panes, with alternate reader, trusted original HTML, browser, printable PDF, and zoom under **More**. Visible embedded images load automatically; **Show images** explicitly downloads external pictures for the selected message. Microsoft inline images can be read from the local mailbox copy or Graph attachment data. Hidden tracking images are omitted from loading.
- **Junk Cleaner:** built for Microsoft accounts (manual with preview, automatic, import, log).

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

1. A bare-metal Linux desktop run, the WebKitGTK interactive reader and the xdg-open helpers (sign-in with the keyring and popups/dialogs are confirmed in WSL).
2. Complete the Calendar work listed in `docs/calendar.md`; People/contacts, Tasks, signatures, spelling, rules, undo/redo, and Automatic Replies are not built.
3. Offline sending (Outbox) and queued actions for the mailbox copies; folder/label rename and delete from a copy.
4. Cross-store full-text search (only selected-folder header search exists).
5. Gmail: draft editing, permanent delete (needs a broader scope), Google consent screen still in Testing mode with a shared project name.
6. Native print dialog; full-fidelity EML of inline images and original headers.
7. Dark mode, high-DPI and large-archive (4 GB) verification; dependency vulnerability review.
8. Window position cannot be remembered under WSLg (always opens on the primary monitor).
9. Options dialog values are stored but most do not change behaviour yet.
