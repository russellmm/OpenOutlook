# OpenOutlook: feature inventory and status

Status date: **2026-10-09**. This is the single place that says what OpenOutlook does today, on which platform, where the code is and which file holds the setting. The requirements are in `PRODUCT_REQUIREMENTS.md`, the design in `DESIGN_SPEC.md` (section 11 onwards describes what was built after the original baseline), the build state in `BUILD_STATUS.md`, and the history in `docs/history/` and `docs/session-handoff-2026-10.md`.

Legend: **Yes** = built and used; **Partly** = built with the stated limits; **No** = not built (a ribbon button for it, if any, says "To be implemented"); **n/a** = does not apply.
Platforms: **Windows** = the published `OpenOutlook.Desktop.exe` (Windows 11). **Linux** = the `.deb` / tar.gz, tested on Ubuntu 26.04 in WSL2 with WSLg, under Xvfb in CI, and (2026-10-06) built, installed and smoke-tested on a bare-metal Ubuntu 26.04 GNOME/Wayland desktop; the hands-on checklist in `docs/handoff-linux-bare-metal-2026-10-06.md` is still to be walked through.

## 1. Application and platforms

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| Avalonia 11.2.3 / .NET 8 desktop app, single instance | Yes | Yes | Single-file self-contained build. A second start shows "already running". |
| Native PST engine (OpenPST, C library) loaded by `OpenOutlook.PstNative` | Yes (`openpst.dll` beside the exe) | Yes (`libopenpst.so` beside the program) | The library must sit beside the program: the loader does not find it inside the single-file bundle. |
| Bundled headless browser for HTML layout (Chrome for Testing chrome-headless-shell, pinned in `scripts/chromium-version.txt`) | Yes (`chromium\` beside the exe, about 270 MB) | Yes (`chromium/`) | Fetched by `scripts/fetch_chromium.py`; kept outside the single-file exe (`ExcludeFromSingleFile`). Installed Edge/Chrome/Chromium is the fallback. |
| Application icon (blue rounded square, envelope, open amber ring) | Yes (exe + main window) | Yes (`.deb` icons, window) | `scripts/generate_icon.py` draws `Assets/openoutlook.ico/.png/-512.png`. Compose and dialog windows do not set it yet. |
| Packaging | published folder `F:\Claude\OpenOutlook_win` (exe, `openpst.dll`, `openoutlook-oauth.json`, `chromium\`) | `.deb` (installs to `/opt/openoutlook`, `openoutlook` command, desktop entry, icons, AppArmor profile for the bundled browser) and portable tar.gz | `scripts/wsl-package.sh [version]` builds the Linux packages in WSL. |
| Continuous integration | n/a | Yes | `.github/workflows/linux.yml`: native build + tests, unit tests, headless UI tests, packages, smoke start of the `.deb`, artifacts. Private PST fixtures are not in the repository, so tests needing one skip themselves in CI. |
| Logging | Yes | Yes | Rotating 2 MB `openoutlook.log` (`AppLog`); unhandled UI exceptions are survived and shown in a notice (`CrashNotice`). |

## 2. PST files (Outlook data files)

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| Open Unicode PST (512-byte pages) read and **write** | Yes | Yes | Always editable unless the file is locked; `.lck` file and journal (crash-safe); `.bak` backup is opt-in (Options). |
| Open ANSI, 4K-page (OST, compressed blocks), encrypted | Read-only | Read-only | The engine reads them; writing is Unicode-512 only. |
| Open / detach, saved list restored at start | Yes | Yes | `attached-psts.json`. Detach never deletes the file. |
| Create a new, empty PST (Account Settings > Data Files > New…) | Yes | Untested (shared code) | Save dialog; the file holds Deleted Items and Search Root under a "Top of Outlook data file" top folder, as Outlook makes it; never overwrites. |
| Add by file dialog, or by typed path (mapped drive, `\\server\share`) | Yes | Yes | Account Settings > Data Files > Add… / Add by path…. Failure reasons are shown there and logged (`[pst-open]`). Network paths work; elevated programs do not see mapped drives (status bar says so). |
| Read mail: HTML / Rich Text / Plain Text / Headers views | Yes | Yes | RTF converted to HTML. |
| Read/unread, flag, move, copy, delete (to Deleted Items), permanent delete, empty Deleted Items | Yes | Yes | Drag and drop and the Move/Copy picker. Right-click Deleted Items > Empty Deleted Items asks "Permanently delete all N items?" (shared dialog, `ConfirmEmptyDeletedItemsAsync`). |
| Folder create / rename / move / delete, purge | Yes | Yes | Special folders are refused. |
| Import messages (EML files and folders, drag and drop), copy/move between archives | Yes | Yes | |
| Copy / move messages between ANY two stores: data file, Microsoft mailbox, Gmail mailbox (Move / Copy to Folder... or drag and drop) | Yes | Untested (shared code) | One picker lists every open data file and every connected account. PST to PST uses the engine's own copy (all properties); the same mailbox uses the server's move/copy; every other pair goes through the whole message as MIME (`MailTransfer`, `MimeMessageBuilder`, Gmail `import`, Graph MIME create). A move removes an original only after its copy is committed (a failure leaves the message where it was, never lost). Originals of a mailbox go to Deleted Items / Trash, originals of a PST are removed. Not carried by MIME: flag status and categories; embedded (attached) messages stop that one message with a clear reason. |
| Create a new empty PST (`opst_create`) | Yes | Yes | SCANPST-clean; used for the mailbox copies. |
| Check and Repair (fixer rules R1-R12) | Yes | Yes | Window in the app; SCANPST.EXE is the oracle (Windows only). |
| Export message / folder tree to EML, save attachments | Yes | Yes | New files only, never overwrites. |
| Selected-folder header search (sender, recipients, subject; max 500) | Yes | Yes | There is no cross-store full-text index. |

Details and the file-format rules: `native-engine-status.md`, `OpenOutlook_Design_Document.md`, `pst-editing-design.md`, `Ctools.MD`.

## 3. Accounts and sign-in

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| Microsoft personal accounts (Hotmail/Outlook.com) via Microsoft Graph, OAuth PKCE in the system browser | Yes | Yes (owner connected Hotmail in WSL, 2026-10-05) | New sign-ins request `offline_access`, `User.Read`, `Mail.ReadWrite`, `Mail.Send`, `Contacts.ReadWrite`, and `Calendars.ReadWrite`. Existing accounts need a completed Repair sign-in to add calendar consent. File > Info and Calendar use the same account registry and OS-protected refresh token; the registry records requested scopes, not tokens. Account verified against Graph `/me`. |
| Gmail via Gmail REST API | Yes | Yes (owner connected Gmail in WSL, 2026-10-05) | Scopes `gmail.modify` + `gmail.compose`; permanent delete not offered (needs a broader scope). The Google consent screen is in Testing mode (refresh tokens last 7 days). |
| Token storage | Windows Credential Manager | libsecret (persistent unlocked default keyring required) | Never a file. Under WSL the keyring is created through GNOME Keyring's password window (`scripts/wsl-create-keyring.sh`). |
| Up to 2 accounts per provider, 4 in total; reconnect / remove | Yes | Yes | `accounts.json` holds labels and public ids, no tokens. |
| Account Settings (File > Info): Email tab, Data Files tab, Junk Cleaner tab | Yes | Yes | Email: New, Repair, Set as Default, Remove (Change and the arrows are placeholders). |
| Opening sign-in pages | system browser | system browser; under WSL the launcher sets `BROWSER` to the Windows browser | |
| Microsoft contacts / address book | No | No | The permission is requested; there is no UI. |
| Hotmail/Outlook.com calendar | Partly | Untested (shared code) | Graph calendar list and event view; Home/View ribbon, two mini months, Month grid, timed Day/Work Week/Week views, and appointment/meeting creation with attendees, all-day, location, and notes. The owner created an appointment in OpenOutlook and confirmed it in Outlook on Windows (2026-10-09). One calendar is shown at a time. Side-by-side/overlay, event details, offline access, and scheduling tools remain; planned ribbon commands are disabled. Gmail and iCloud calendars are outside scope. See [calendar.md](calendar.md). |
| Tasks, people UI | No | No | Navigation rail items are placeholders. |

## 4. Mailbox copies (offline mirror) and local-first reading

A connected account keeps a local copy: a PST (`<address>.pst`) plus a SQLite state file (`<address>.sync`) beside it. Plan and phases: `offline-mirror-plan.md`.

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| Copy of a Microsoft mailbox into a PST | Yes | Yes | One PST folder per server folder; list-and-compare pull. |
| Copy of a Gmail mailbox | Yes | Yes | One folder per label ("Work/Reports" nests); a message with several labels appears in each (like an IMAP client). Trash = Deleted Items. |
| What is kept | Yes | Yes | Default: last 12 months, attachments up to 25 MB, per-account on/off (on by default). Messages outside the window or over the limit are recorded as tombstones and stay readable online. |
| Changes made in the copy are sent to the server | Yes | Yes | Read/flag, move, delete, new folders; server wins conflicts; offline changes wait (`MirrorPush`). |
| Where the files live | `%LOCALAPPDATA%\OpenOutlook\Mail` | `~/.local/share/openoutlook/mail` | Default folder and per-account location are chosen in Account Settings > Data Files (Change location…). |
| Sync schedule | Yes | Yes | 20 s after start, then every 15 min; a file watcher (30 s) sends changes made in the copy; 4 s (Microsoft) / 20 s (Gmail) after a change made in the app; opening a folder whose last sync is older than 90 s (Microsoft) / 5 min (Gmail); Refresh button; Sync now. `OPENOUTLOOK_NO_MIRROR=1` turns the scheduler off. |
| Sync indicator in the status bar | Yes | Yes | Worst state wins (problem, offline, syncing, up to date); click opens Data Files. |
| Folders and message bodies are shown **from the copy** (about 10 ms instead of about 0.5 s) | Yes (Microsoft, Gmail) | Yes | `LocalMailboxReader`; the list shows the newest 500 of a folder; the copy is hidden from the folder list (it is the storage behind the account). |
| Local changes show immediately | Yes | Yes | Overlay of deletes, flags and reads until the copy catches up. |
| Offline sending (Outbox) | No | No | Recorded as a future feature. |
| Rename/delete folders or labels from the copy, copy messages up to the server | No | No | Future. |

## 5. Mail actions and speed

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| Microsoft: read/unread, flag, archive, delete, move/copy to folder, create folder | Yes | Yes | The list changes at once; Graph is told in the background, in order; a refusal restores the list. Deleting inside Deleted Items asks and waits (permanent). Empty Deleted Items (Graph `permanentDelete`, 50 at a time, folder identity checked) clears the list at once, then syncs the mailbox copy before refreshing so the stale copy cannot bring the messages back; the Junk Cleaner re-reads the folder counts and drops moved messages from the list at once (`SyncMirrorWhenIdleAsync` waits for a running sync). |
| Gmail: read/unread, star, archive, trash, move/add label, create label | Yes | Yes | Same at-once behaviour; label counts adjusted locally, re-read 8 s after the last action. |
| Drag messages onto folders (left = move, right = Move Here / Copy Here) | Yes | Yes | |
| Delete key and configurable shortcuts (View > Shortcuts) | Yes | Yes | `shortcuts.json`. |
| Request savings | Yes | Yes | Account check cached per token, parallel folder fetch, HTTP/2, token + connection warm-up at start (`GraphAccountVerification`). |
| Unread counts in the folder list | Yes | Yes | From the loaded page, folder tree re-read about once a minute; adjusted locally after actions. |

## 6. Reading

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| Sanitized HTML laid out by a real browser engine (snapshot tiles), `cid:` / data / remote images loaded automatically within caps | Yes | Yes | Image caps: 8 MiB and 16 MP per image, 64 images and 48 MiB and 64 MP per message. |
| Interactive reader | Embedded WebView2 first, snapshot as fallback | Embedded WebKitGTK if `libwebkit2gtk-4.1` is installed, otherwise snapshot | Under Wayland the embedded view can paint nothing, so connected mail starts on the snapshot and the interactive reader is an opt-in per message. |
| Text selection and copy over the snapshot (block granularity, Ctrl+A, Ctrl+C) | Yes | Yes | `docs/reading-pane-text-selection.md`. |
| Open in browser, View original here (trusted), Save printable PDF, zoom, plain-text view | Yes | Yes | No native print dialog. |
| Open message in its own window (double-click) | Yes | Yes | Microsoft and Gmail messages. |
| Attachment chips: Open / Save as…; risky file types are never opened | Yes | Yes | Microsoft file attachments up to 64 MiB; Gmail attachments. Opening uses the default program (`AttachmentLauncher`; xdg-open on Linux, untested on a real desktop). |
| Reading Pane options: mark as read after N seconds / on selection change, space-bar paging, portrait, always preview | Yes | Yes | `options.json`, `read-state.json`. |

## 7. Composing

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| New compose window (Outlook-like: tabs, Send, From list of all connected accounts, To/Cc/Bcc, Subject) | Yes | Yes | Unbuilt buttons say "To be implemented". |
| Microsoft: server drafts, reply, reply all, forward, attachments (small and large upload sessions), reopen drafts | Yes | Yes | |
| Gmail: send, reply, reply all, forward (with original attachments), save attachments | Yes | Yes | MIME built locally (`GmailMimeBuilder`, 25 MB limit). Editing a saved Gmail draft is not supported. |
| Visual HTML editor (fonts, colors, links, lists, tables, images) | Yes | Yes | Uses the platform web view; compose sanitizer profile keeps formatting and removes form controls. |
| Signatures, spelling, stationery | No | No | Options values are stored but do not change behaviour yet. |

## 8. User interface

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| Outlook-style window: ribbon (File, Home, Send/Receive, Folder, View, Help), three panes, navigation rail, backstage (File menu) | Yes | Yes | Commands without an implementation report "To be implemented". |
| Ribbon squeezes into drop-downs as the window narrows | Yes | Yes | `RibbonResponsiveLayout`. |
| Message list: sortable / movable / resizable columns; date sections; sorting by Received keeps the sections | Yes | Yes | `view-layout.json` persists columns (including hidden ones) and pane widths. |
| Column header right-click menu (Arrange By, Reverse Sort, Field Chooser, Remove This Column, Group By This Field) | Yes | Yes | Group by Box and View Settings are placeholders. |
| "By date ▾" chip above the list = Arrange By menu (Date, From, Subject, Size, Importance, Attachments, Flag; Reverse Sort; Show in Groups) | Yes | Yes | `MainWindow.ColumnMenu.cs` (`ArrangeByClicked`, `ArrangeMessageListBy`). Groups and orders by the field; Size is only ordered. The label follows the arrangement. Before 2026-10-06 the chip was a plain date-sections checkbox. |
| The chosen sort survives opening another folder, returning, and reloads after a delete or move | Yes | Yes | `MainWindow.SelectNext.cs` (`RememberListSort`, `CarrySortInto`). Session only; not saved in `view-layout.json`. Arrange By / Group By This Field start the field in its own order. |
| After deleting, archiving or moving the highlighted message, the next one in the list is highlighted and opened | Yes | Yes | `NoteRowLeaving` / `SelectDisplayedRow`: Microsoft, Gmail, PST deletes, PST drag-moves, server-side removals in a refresh. A batch highlights once, after the whole batch. |
| Search icon and status-bar zoom slider fit their boxes | Yes | Yes | Search icon is a 32px template inside a Viewbox; the slider (`Slider.statusZoom` in `OutlookStyles.axaml`) pulls the theme's 15px tick-mark rows out of the 26px bar. The ribbon Zoom button has the same cropping problem (not fixed). |
| Importance and Flag columns (start hidden; Field Chooser or header menu) | Yes | Yes | Importance from Graph, the PST and the copy; Gmail shows normal. |
| Folder pane: reorder by menu or drag, per-parent order saved | Yes | Yes | `folder-order.json`. |
| Move Items picker, right-click menus for every folder type | Yes | Yes | |
| Appearance: light/dark, accent, text size | Yes | Yes | `appearance.json`. Dark mode and high-DPI not fully verified. |
| File > Options (Mail page and Reading Pane/Editor dialogs) | Partly | Partly | Values persist (`options.json`) but most do not change behaviour; unbuilt categories share a placeholder page. |
| Window size / maximised state restored | Yes | Yes | |
| Window position restored | Yes | **No** | WSLg does not tell the program where its window is. Under WSL the window opens at the upper left of the monitor Windows has as primary (`WslWindowPlacement`). |

## 9. Junk Cleaner (Microsoft accounts only)

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| Per-account on/off, keywords (From line contains), rules: high importance, no To address, sent on behalf, flagged | Yes | Yes | Account Settings > Junk Cleaner tab; `junk-cleaner.json`. Matches go to Deleted Items, never a permanent delete; at most 500 per run. |
| Clean Junk Now with a tick-box preview | Yes | Yes | Ribbon Junk group and the tab. |
| Export keywords / Import keywords (buttons on the tab) | Yes | Yes | Export saves the account's keywords, interval and rules (incl. flagged) to a JSON file with no account id; Import (also reads the old OutlookJunkCleaner `config.json`) merges the keywords and turns the cleaner on for the account. Settings are per machine (`junk-cleaner.json` in `~/.config/OpenOutlook`), so this is how the list moves between Windows and Linux. |
| Automatic cleaning every 1-60 minutes while the app runs, silent | Yes | Yes | Off by default; log in `junk-cleaner.log`. |
| Import of the old OutlookJunkCleaner `config.json` | Yes | Yes | Turns the cleaner on, leaves automatic cleaning as it was. |
| Gmail Spam, PST Junk folders | No | No | |

Plan and decisions: `junk-cleaner-plan.md`.

## 10. Linux and WSL specifics

- Mapped Windows network drives must be mounted in WSL (`/etc/fstab` drvfs lines); `WslDriveBookmarks` adds `/mnt/<letter>` drives to the GTK file dialog sidebar at start.
- Menus, drop-downs and tooltips are drawn inside the window on Linux (`OverlayPopups`); under WSL every dialog except the main window is `Topmost` while open (WSLg does not keep transient windows above their owner). Confirmed by the owner on WSLg: menus and dialogs stay in front of the main window.
- The launcher `/usr/bin/openoutlook` hides Mesa's harmless DRI3 warnings and points `BROWSER` at the Windows browser under WSL.
- A running Windows "Remote Desktop" helper can make the cursor vanish over WSLg windows (not an OpenOutlook bug).

## 11. Settings and data locations

`~` is the user profile folder on Windows (`C:\Users\<name>`) and the home folder on Linux. `XDG_CONFIG_HOME` / `XDG_DATA_HOME` override them on both.

| What | Where |
|---|---|
| Settings files | `~/.config/OpenOutlook/`: `attached-psts.json`, `view-layout.json`, `options.json`, `shortcuts.json`, `appearance.json`, `folder-order.json`, `read-state.json`, `default-account.json`, `mirror-settings.json`, `junk-cleaner.json`, `junk-cleaner.log` |
| Accounts (labels and public ids; no tokens) | `~/.local/share/OpenOutlook/accounts.json` |
| Log | `~/.local/share/OpenOutlook/logs/openoutlook.log` |
| Mailbox copies | Windows `%LOCALAPPDATA%\OpenOutlook\Mail`; Linux `~/.local/share/openoutlook/mail`; or the folder chosen in Data Files |
| OAuth public client ids | `openoutlook-oauth.json` beside the program (git-ignored; template `openoutlook-oauth.example.json`) |
| Reader scratch files | `%TEMP%\openoutlook-reader` / `/tmp/openoutlook-reader` (private; stale browser profile folders in the temp folder are removed at start) |

## 12. Not built / known gaps

Calendar gaps (multiple calendars at once, overlay, event details, offline access, scheduling tools, and day preview); People/contacts UI, Tasks; signatures and spelling; undo/redo; rules; Automatic Replies; offline sending (Outbox); cross-store full-text search; native print dialog; Gmail draft editing and permanent Gmail delete; folder/label rename and delete from the mailbox copies; Junk Cleaner for Gmail; dark-mode and high-DPI verification; the PST engine writes Unicode-512 files only; `rmarrash_*.pst` fixtures are the owner's private data and are never in the repository.

## 13. Tests

- .NET unit tests `tests/OpenOutlook.Tests`: the focused Graph Calendar tests pass on Windows. Five existing `OfflineMessageCacheTests` depend on Unix file permissions and cannot pass on Windows.
- Avalonia headless UI tests `tests/OpenOutlook.HeadlessTests`: the focused Calendar tests pass on Windows. The complete suite is exercised by Linux CI.
- C tests (`native/openpst/tests`: `test_basic`, `test_formats`, `test_write` with 168 checks) and the soak harness `tools/PstSoak`; SCANPST.EXE is the oracle for PST correctness (Windows).
- Live-account tools: `tools/MailSmoke` (send/receive between the owner's accounts, mirror, Junk Cleaner dry run `junkscan`, timings `timing` / `gmailtiming` / `gmailsync`, `localfolder`, `openpath`, `openeditable`).
- Linux/WSL scripts: `scripts/wsl-*.sh` (tests, package, deb smoke test, placement test, keyring diagnosis).
