# OpenOutlook

A classic-Outlook-style mail client for **Windows 11** and **Linux**, built with .NET 8 and Avalonia 11. It opens, edits, creates, checks and repairs Outlook PST files with its own engine, connects personal Microsoft (Hotmail / Outlook.com) and Gmail accounts, keeps a local copy of each mailbox so folders and messages open instantly, and fights Hotmail junk mail with rules, junk lists and a spam check. It is a personal project in daily use by its owner on both platforms, **not a finished release**. Current build: **0.1.84** (2026-10-29).

Where to read next:

| Document | What it says |
|---|---|
| [docs/FEATURES.md](docs/FEATURES.md) | Every feature, per platform (Windows / Linux), with the files and settings involved |
| [BUILD_STATUS.md](BUILD_STATUS.md) | Current state, tests, builds, commands, open items |
| [PRODUCT_REQUIREMENTS.md](PRODUCT_REQUIREMENTS.md) | What the product must do, with a conformance table |
| [DESIGN_SPEC.md](DESIGN_SPEC.md) | The design, and (from section 11) how it was actually built |
| [docs/EDGE_CASES.md](docs/EDGE_CASES.md) | Every deliberate edge case with its reason, code and test |
| [docs/accounts-setup.md](docs/accounts-setup.md) | Connecting Hotmail and Gmail, Account Settings, the Junk Cleaner tab |
| [docs/calendar.md](docs/calendar.md) | Hotmail calendar features, permissions and current limits |
| [docs/people.md](docs/people.md) | Microsoft contacts (People) |
| [docs/junk-lists-plan.md](docs/junk-lists-plan.md) | Safe Senders, Blocked Senders and the Junk Email Options window |
| [docs/offline-mirror-plan.md](docs/offline-mirror-plan.md) | Mailbox copies (local PST per account) |
| [docs/native-engine-status.md](docs/native-engine-status.md) | The PST engine and how it is validated |
| [docs/session-handoff-2026-10.md](docs/session-handoff-2026-10.md) | Working log for resuming development (build, publish, push, gotchas) |

## What it does

- **PST files:** open, read, edit (flags, move, copy, delete, folders, import EML), create, and **Check and Repair** Unicode PST files with the vendored C library OpenPST (SCANPST.EXE is the yardstick: files repaired by OpenOutlook scan clean); read-only for ANSI and 4K/OST files. Files on mapped or network drives work.
- **Hotmail and Gmail:** read, reply, forward, compose, flag, archive, delete, move and copy between any two stores (data file, Hotmail, Gmail), drag onto folders. Changes show at once and are sent to the server in the background. New mail appears within a minute (light check, F9 = Send/Receive All), and the program icon shows new mail while the window is in the background.
- **Mailbox copies:** each account keeps a PST copy (default `%LOCALAPPDATA%\OpenOutlook\Mail` on Windows, `~/.local/share/openoutlook/mail` on Linux, or a folder chosen in Account Settings > Data Files). Folders and message bodies are read from the copy; a status-bar indicator shows the sync state.
- **Reading mail as sent:** HTML mail is sanitized and laid out by a bundled headless Chromium (or the embedded WebView2 on Windows), with pictures, Outlook-like character-level text selection, and a Trust Center for automatic picture downloads.
- **Composing like Outlook:** a compose window with Outlook's ribbon, HTML edited in place (Format Text tab, fonts, lists, tables, links, pictures sent as inline parts), recipient suggestions, Select Names and Check Names.
- **Junk mail (Hotmail):** the **Junk Cleaner** (keywords plus 18 header and subject rules, preview, optional automatic cleaning); Outlook's **junk lists** (Safe Senders, Safe Recipients, Blocked Senders, blocked top-level domains, Junk Email Options); an **Inbox spam check** that ticks suspected spam in the list's first column with **Block Sender and Delete** / **Delete** for the ticked ones; and the same ticks in **Junk Email** showing exactly what the Junk Cleaner will delete.
- **Hotmail calendar and contacts:** Month, Week, Work Week and Day views, create / change / delete appointments and meetings, reminders; People with My Contacts, search, and create / edit / delete.
- **Outlook look and feel:** responsive ribbon, backstage File menu, column header menu, Importance and Flag columns, panes that cannot be collapsed by accident, View > Change View > Default View, themes, folder reordering, a log viewer.

Not built yet (calendar overlay and recurrence editing, Tasks, signatures, spelling, rules, offline sending, full-text search and more) is listed in [docs/FEATURES.md](docs/FEATURES.md) section 12.

## Build and run

```bash
dotnet build OpenOutlook.sln
dotnet test tests/OpenOutlook.Tests
dotnet test tests/OpenOutlook.HeadlessTests
dotnet run --project src/OpenOutlook.Desktop/OpenOutlook.Desktop.csproj
```

On Windows five `OfflineMessageCacheTests` fail by design (they test Unix file permissions); everything else passes.

The PST engine is a C library that must be built once per platform:

- Windows: `powershell scripts/build-native.ps1` (MSVC), output `src/OpenOutlook.Desktop/runtimes/win-x64/native/openpst.dll`.
- Linux: `bash scripts/build-native.sh` (CMake, Ninja, gcc), output `.../runtimes/linux-x64/native/libopenpst.so`.

The bundled browser for HTML layout is downloaded with `python scripts/fetch_chromium.py` into `third_party/chromium/` (git-ignored); without it an installed Edge, Chrome or Chromium is used.

### Windows build

```
dotnet publish src/OpenOutlook.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=<version> -p:SourceRevisionId=<commit> -o <folder>
copy src\OpenOutlook.Desktop\runtimes\win-x64\native\openpst.dll <folder>
```

`<folder>` also needs `openoutlook-oauth.json` (see account setup) and receives the `chromium` folder from the build. `openpst.dll` must sit next to the exe. The version and commit show in File > Account > About and in the log.

### Linux packages

Run on Linux, or from Windows in WSL with `scripts/wsl-package.sh <version>` (builds from a copy so the Windows build folders stay untouched; `scripts/wsl-deb-smoke.sh` then starts the packaged program as a check):

```bash
bash scripts/build-native.sh
bash scripts/package-linux-x64.sh 0.1.88          # self-contained program + libopenpst.so + chromium, tar.gz
bash scripts/build-deb.sh 0.1.88                  # openoutlook_0.1.88_amd64.deb (repo on NTFS? OO_DEB_STAGE=/tmp/deb-stage bash scripts/build-deb.sh 0.1.88)
sudo apt install ./publish/openoutlook_0.1.88_amd64.deb   # also installs an AppArmor profile so the bundled browser can start on Ubuntu 23.10+
openoutlook
```

Installing the `.deb` puts the program in `/opt/openoutlook`, adds the `openoutlook` command, a menu entry and the icon, and pulls in the libraries the bundled browser needs. Sign-in tokens go to the desktop keyring (libsecret): GNOME Keyring or KWallet must be running with a persistent default keyring. On GNOME / Wayland the compose body is a read-only preview with **Edit message...** opening the editor in its own window. Under WSL, `scripts/wsl-create-keyring.sh` and `scripts/wsl-keyring-diag.sh` help with the keyring; the launcher opens sign-in pages in the Windows browser.

## Accounts

Sign-in uses the system browser (OAuth with PKCE); OpenOutlook never sees a password. The build owner supplies public client ids in `openoutlook-oauth.json` next to the program (template: `src/OpenOutlook.Desktop/openoutlook-oauth.example.json`). Setup of the Azure and Google projects: [docs/accounts-setup.md](docs/accounts-setup.md). Tokens are stored in Windows Credential Manager or the Linux keyring, never in a file.

## Safety and privacy

- PSTs are edited in place behind a journal and a `.lck` file (a `.bak` copy is optional); the owner's private archives are never used by automated tests directly (tests work on copies) and are never committed. Never commit tokens, logs, `openoutlook-oauth.json`, `.secrets/` or message contents.
- HTML mail is sanitized, network access is blocked in the layout browser, and external pictures are fetched only as the Trust Center allows (or with **Show images**). The bounded loader accepts public addresses only and enforces size and count caps. Active content only runs after the explicit **Show original HTML (trusted mail)** choice under **More**.
- Junk handling never deletes permanently: the Junk Cleaner, the spam check and Block Sender and Delete move mail to Deleted Items. The Junk Cleaner does nothing on an account until it is turned on there; the spam check only ticks messages, and nothing moves without a click.
- The log (`~/.local/share/OpenOutlook/logs/openoutlook.log`, rotating, 2 MB; File > Account > View log) records start, exit, actions and failures; an unexpected error inside a UI action is shown in a notice and survived.

## License

MIT; see [LICENSE](LICENSE). The PST core was contributed by the project owner for incorporation and MIT distribution.
