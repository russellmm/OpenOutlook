# OpenOutlook build status

Status date: **2026-10-05**. OpenOutlook is a working personal mail client for **Windows 11** and **Linux** (Ubuntu 26.04, tested in WSL2/WSLg). It is still not a release candidate: the first-release gates in `PRODUCT_REQUIREMENTS.md` are not all met (see the list at the end).

What exists, per platform and per feature, is in **`docs/FEATURES.md`**. How it is built is in `DESIGN_SPEC.md` (sections 1-10 are the original baseline, section 11 onwards the design as built). The detailed history of earlier sessions (through 2026-10-02, when the app was Linux-only, read-only for PSTs and used the managed PST reader) is kept in `docs/history/BUILD_STATUS-through-2026-10-02.md`; it describes a state that no longer exists in several places (see "Superseded" in `DESIGN_SPEC.md`).

## Current state

- **Repository:** `github.com/russellmm/OpenOutlook`, branch `native-engine-phase0`, always pushed to `main` as well (same commit).
- **Tests:** 531 .NET unit tests (all pass on Linux; on Windows 5 Unix-permission tests cannot pass) and 21 Avalonia headless UI tests (all pass on both platforms); C library tests `test_basic`, `test_formats`, `test_write` (168 checks) pass. `.github/workflows/linux.yml` runs the Linux build, tests, packaging and a start of the packaged program on every push to `main`.
- **Published builds**
  - Windows: `F:\Claude\OpenOutlook_win\OpenOutlook.Desktop.exe` with `openpst.dll`, `openoutlook-oauth.json` and the `chromium\` folder beside it (build steps in `docs/session-handoff-2026-10.md`).
  - Linux: `publish/openoutlook_<version>_amd64.deb` (currently 0.1.12) and `publish/OpenOutlook-linux-x64.tar.gz`, built by `scripts/wsl-package.sh`.
- **PST engine:** OpenPST (C, `native/openpst`) reads and writes Unicode PST files; SCANPST.EXE reports NO_ERRORS on the owner's files and on files created and soaked by the engine (`docs/native-engine-status.md`).
- **Accounts:** Hotmail (Microsoft Graph) and Gmail (Gmail API) connected and used live by the owner on Windows; Linux sign-in uses libsecret and has been set up (keyring created) but the owner's result is not confirmed.
- **Mailbox copies:** both account types keep a local PST copy; folders, message bodies and most actions work from it (`docs/offline-mirror-plan.md`, `docs/FEATURES.md` section 4).
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

1. Real sign-in and daily use on a Linux desktop (keyring, WebKitGTK reader, xdg-open helpers), and a bare-metal Linux run.
2. Calendar, People/contacts, Tasks, signatures, spelling, rules, undo/redo, Automatic Replies: not built (ribbon buttons say "To be implemented").
3. Offline sending (Outbox) and queued actions for the mailbox copies; folder/label rename and delete from a copy.
4. Cross-store full-text search (only selected-folder header search exists).
5. Gmail: draft editing, permanent delete (needs a broader scope), Google consent screen still in Testing mode with a shared project name.
6. Native print dialog; full-fidelity EML of inline images and original headers.
7. Dark mode, high-DPI and large-archive (4 GB) verification; dependency vulnerability review.
8. Window position cannot be remembered under WSLg (always opens on the primary monitor).
9. Options dialog values are stored but most do not change behaviour yet.
