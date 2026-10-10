# OpenOutlook

A classic-Outlook-style mail client for **Windows 11** and **Linux**, built with .NET 8 and Avalonia 11. It opens, edits and creates Outlook PST files with its own engine, connects personal Microsoft (Hotmail / Outlook.com) and Gmail accounts, keeps a local copy of each mailbox so folders and messages open instantly, and cleans Hotmail junk mail by rules. It is a personal project in daily testing by its owner, **not a finished release**.

Where to read next:

| Document | What it says |
|---|---|
| [docs/FEATURES.md](docs/FEATURES.md) | Every feature, per platform (Windows / Linux), with the files and settings involved |
| [BUILD_STATUS.md](BUILD_STATUS.md) | Current state, tests, commands, open items |
| [PRODUCT_REQUIREMENTS.md](PRODUCT_REQUIREMENTS.md) | What the product must do, with a conformance table |
| [DESIGN_SPEC.md](DESIGN_SPEC.md) | The design, and (from section 11) how it was actually built |
| [docs/accounts-setup.md](docs/accounts-setup.md) | Connecting Hotmail and Gmail, Account Settings, the Junk Cleaner tab |
| [docs/calendar.md](docs/calendar.md) | Hotmail calendar features, account permissions, and current limits |
| [docs/offline-mirror-plan.md](docs/offline-mirror-plan.md) | Mailbox copies (local PST per account) |
| [docs/native-engine-status.md](docs/native-engine-status.md) | The PST engine and how it is validated |
| [docs/session-handoff-2026-10.md](docs/session-handoff-2026-10.md) | Working notes for resuming development (build, publish, push, gotchas) |

## What it does

- **PST files:** open, read, edit (flags, move, copy, delete, folders, import EML), create, check and repair Unicode PST files with the vendored C library OpenPST; read-only for ANSI and 4K/OST files. Files on mapped or network drives work.
- **Hotmail and Gmail:** read, search folders, reply, forward, compose with attachments, flag, archive, delete, move, drag onto folders. Changes show at once and are sent to the server in the background.
- **Mailbox copies:** each account keeps a PST copy (default `%LOCALAPPDATA%\OpenOutlook\Mail` on Windows, `~/.local/share/openoutlook/mail` on Linux, or a folder you choose in Account Settings > Data Files). Folders and message bodies are read from the copy; a status-bar indicator shows the sync state.
- **Reading mail as sent:** HTML mail is sanitized and laid out by a bundled headless Chromium (or the platform web view), with inline images and selectable text. External pictures can be downloaded with **Show images** for the selected message. The reading pane keeps common actions visible and puts browser, reader, PDF, and zoom commands under **More**.
- **Junk Cleaner (Hotmail):** keywords and rules, a preview before cleaning, optional automatic cleaning, import of the old OutlookJunkCleaner configuration.
- **Hotmail calendar:** Month, Week, Work Week, and Day views; two mini months; an Outlook-style ribbon; create appointments and meetings in a connected personal Microsoft account.
- **Outlook look and feel:** ribbon that squeezes as the window narrows, backstage File menu, column header menu with Importance and Flag columns, themes, folder reordering.

Calendar is partly built. Remaining work, along with contacts, tasks, signatures, offline sending, and other gaps, is listed in [docs/FEATURES.md](docs/FEATURES.md) section 12.

## Build and run

```bash
dotnet build OpenOutlook.sln
dotnet test tests/OpenOutlook.Tests
dotnet test tests/OpenOutlook.HeadlessTests
dotnet run --project src/OpenOutlook.Desktop/OpenOutlook.Desktop.csproj
```

The PST engine is a C library that must be built once per platform:

- Windows: `powershell scripts/build-native.ps1` (MSVC), output `src/OpenOutlook.Desktop/runtimes/win-x64/native/openpst.dll`.
- Linux: `bash scripts/build-native.sh` (CMake, Ninja, gcc), output `.../runtimes/linux-x64/native/libopenpst.so`.

The bundled browser for HTML layout is downloaded with `python scripts/fetch_chromium.py` into `third_party/chromium/` (git-ignored); without it an installed Edge, Chrome or Chromium is used.

### Windows build

```
dotnet publish src/OpenOutlook.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <folder>
copy src\OpenOutlook.Desktop\runtimes\win-x64\native\openpst.dll <folder>
```

`<folder>` also needs `openoutlook-oauth.json` (see account setup) and receives the `chromium` folder from the build. `openpst.dll` must sit next to the exe.

### Linux packages

Run on Linux (or in WSL with `scripts/wsl-package.sh [version]`, which builds from a copy so the Windows build folders stay untouched):

```bash
bash scripts/build-native.sh
bash scripts/package-linux-x64.sh        # self-contained program + libopenpst.so + chromium, tar.gz
bash scripts/build-deb.sh 0.1.22         # openoutlook_0.1.22_amd64.deb (repo on NTFS? OO_DEB_STAGE=/tmp/deb-stage bash scripts/build-deb.sh 0.1.22)
sudo apt install ./publish/openoutlook_0.1.22_amd64.deb   # also installs an AppArmor profile so the bundled browser can start on Ubuntu 23.10+
openoutlook
```

Installing the `.deb` puts the program in `/opt/openoutlook`, adds the `openoutlook` command, a menu entry and the icon, and pulls in the libraries the bundled browser needs. Sign-in tokens go to the desktop keyring (libsecret): GNOME Keyring or KWallet must be running with a persistent default keyring. Under WSL, `scripts/wsl-create-keyring.sh` and `scripts/wsl-keyring-diag.sh` help with the keyring; the launcher opens sign-in pages in the Windows browser.

## Accounts

Sign-in uses the system browser (OAuth with PKCE); OpenOutlook never sees a password. The build owner supplies public client ids in `openoutlook-oauth.json` next to the program (template: `src/OpenOutlook.Desktop/openoutlook-oauth.example.json`). Setup of the Azure and Google projects: [docs/accounts-setup.md](docs/accounts-setup.md). Tokens are stored in Windows Credential Manager or the Linux keyring, never in a file.

## Safety and privacy

- PSTs are edited in place behind a journal; the owner's private archives are never used by automated tests directly (tests work on copies) and are never committed. Never commit tokens, logs, `openoutlook-oauth.json`, `.secrets/` or message contents.
- HTML mail is sanitized, network access is blocked in the layout browser, and visible external images are fetched only after **Show images** is selected for that message. The bounded loader accepts public addresses only and enforces size and count caps. Active content only runs after the explicit **Show original HTML (trusted mail)** choice under **More**.
- The Junk Cleaner moves mail to Deleted Items and never deletes permanently; nothing is cleaned on an account until you turn it on for that account.
- The log (`~/.local/share/OpenOutlook/logs/openoutlook.log`, rotating, 2 MB) records start, exit and failures; an unexpected error inside a UI action is shown in a notice and survived.

## License

MIT; see [LICENSE](LICENSE). The PST core was contributed by the project owner for incorporation and MIT distribution.
