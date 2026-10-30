# OpenOutlook: feature inventory and status

Status date: **2026-10-29** (build 0.1.84). This is the single place that says what OpenOutlook does today, on which platform, where the code is and which file holds the setting. The requirements are in `PRODUCT_REQUIREMENTS.md`, the design in `DESIGN_SPEC.md` (section 11 onwards describes what was built after the original baseline), the build state in `BUILD_STATUS.md`, and the history in `docs/history/` and `docs/session-handoff-2026-10.md`.

Legend: **Yes** = built and used; **Partly** = built with the stated limits; **No** = not built (a ribbon button for it, if any, says "To be implemented"); **n/a** = does not apply.
Platforms: **Windows** = the published `OpenOutlook.Desktop.exe` (Windows 11). **Linux** = the `.deb` / tar.gz, tested on Ubuntu 26.04 in WSL2 with WSLg, under Xvfb in CI, and on a bare-metal Ubuntu 26.04 GNOME/Wayland desktop, where the owner walked through the hands-on checklist (2026-10-15 and 2026-10-19) and uses it daily.

## 1. Application and platforms

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| Avalonia 11.2.3 / .NET 8 desktop app, single instance | Yes | Yes | Single-file self-contained build. A second start shows "already running". |
| Native PST engine (OpenPST, C library) loaded by `OpenOutlook.PstNative` | Yes (`openpst.dll` beside the exe) | Yes (`libopenpst.so` beside the program) | The library must sit beside the program: the loader does not find it inside the single-file bundle. |
| Bundled headless browser for HTML layout (Chrome for Testing chrome-headless-shell, pinned in `scripts/chromium-version.txt`) | Yes (`chromium\` beside the exe, about 270 MB) | Yes (`chromium/`) | Fetched by `scripts/fetch_chromium.py`; kept outside the single-file exe (`ExcludeFromSingleFile`). Installed Edge/Chrome/Chromium is the fallback. |
| Application icon (blue rounded square, envelope, open amber ring) | Yes (exe + main window) | Yes (`.deb` icons, window) | `scripts/generate_icon.py` draws `Assets/openoutlook.ico/.png/-512.png`. Compose and dialog windows do not set it yet. |
| Log viewer (File > Account > View log): newest first, search, only problems, copy, open the log folder | Yes | Yes | `LogViewerWindow`; the log is `~/.local/share/OpenOutlook/logs/openoutlook.log` (rotated to `.1`). |
| Packaging | published folder `F:\Claude\OpenOutlook_win` (exe, `openpst.dll`, `openoutlook-oauth.json`, `chromium\`) | `.deb` (installs to `/opt/openoutlook`, `openoutlook` command, desktop entry, icons, AppArmor profile for the bundled browser) and portable tar.gz | `scripts/wsl-package.sh [version]` builds the Linux packages in WSL; on bare metal `scripts/package-linux-x64.sh <version>` then `scripts/build-deb.sh <version>`. The version and commit are stamped into the program and shown in File > Account > About. |
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
| Check and Repair (fixer rules R1-R13; format rules 1-43) | Yes | Yes | Window in the app; SCANPST.EXE is the oracle (Windows only). |
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
| Message Properties (right-click a message > Properties) | Yes | Yes | Sheet for the first selected message: subject, from, to, recipients (To/Cc/Bcc), sent/received, size, importance, status, attachments, message class, internet message ID, folder / account, plus the **raw internet headers** in a monospace box with Copy headers. PST messages show the stored headers (or a note and the key properties when the message never travelled by mail); Hotmail and Gmail messages read the header block of the raw message from the server when the sheet opens. `MainWindow.MessageProperties.cs`. |
| Folder Properties (right-click a folder, data file or account row) | Yes | Yes | Read-only sheet: name, kind (PST folder / mailbox copy / Microsoft folder / Gmail label / data file), folder path, data file, item and unread counts, subfolders, size of the messages (PST folders, read when the sheet opens), folder or label ID; a data-file row shows file size and whether it is editable. Microsoft and Gmail counts are those of the last folder-list load. `MainWindow.FolderProperties.cs`. |
| Account Settings (File > Info): Email tab, Data Files tab, Junk Cleaner tab | Yes | Yes | Email: New, Repair, Set as Default, Remove (Change and the arrows are placeholders). |
| Opening sign-in pages | system browser | system browser; under WSL the launcher sets `BROWSER` to the Windows browser | |
| Microsoft contacts (People) | Yes | Yes | People on the rail: My Contacts folders, A-Z index, avatars with pictures, details card, search, People / List views, New / Edit / Delete in an Outlook-style contact window, Email and Meeting from a contact. Unbuilt ribbon buttons say "Planned feature". The compose window's suggestions and Select Names use these contacts (see compose). See `docs/people.md`. |
| Hotmail/Outlook.com calendar | Partly | Untested (shared code) | Graph calendar list and event view; Home/View ribbon, two mini months, Month grid, timed Day/Work Week/Week views, and appointment/meeting creation with attendees, all-day, location, notes, and a real Reminder box. **Reminders** (2026-10-16): an Outlook-like Reminders window from Graph's reminder list, including reminders missed while OpenOutlook was closed (30-day look-back, Options > Calendar); Dismiss / Snooze apply everywhere and a dismissed reminder never shows again. The owner created an appointment in OpenOutlook and confirmed it in Outlook on Windows (2026-10-09). One calendar is shown at a time. **Double-click opens an event to change or delete it** (an occurrence of a series alone; others' meetings read-only); the appointment window has Outlook's date / time boxes, Show As, Private and importance (2026-10-17). Side-by-side/overlay, recurrence, editing a whole series, offline access, and scheduling tools remain; planned ribbon commands are disabled. Gmail and iCloud calendars are outside scope. See [calendar.md](calendar.md). |
| Tasks | No | No | The rail item is a placeholder. |

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
| Sanitized HTML laid out by a real browser engine (snapshot tiles); visible `cid:` / data images load automatically, external pictures per Trust Center > Automatic Download (on by default) or on request; PNG, JPEG, GIF and WebP; pictures are fetched with a browser User-Agent; wide pictures are decoded at 1600 px for the reader and the per-message picture memory is a Trust Center choice (64 / 128 / 256 million pixels); web fonts are not counted as missing pictures; pictures appear as they arrive (six at a time, first layout after 1.2 s) and 1 x 1 tracking pixels and style-sheet beacons for print or forwarded copies are not fetched; the sanitizer keeps borders, rounded corners, underlines, overflow/object-fit and @media rules | Yes | Yes | **Show images** downloads visible external pictures for the selected message; a status line counts blocked or failed images. Hidden tracking images are omitted. Microsoft inline images can come from the local PST copy, with Graph as fallback; Graph attachment metadata includes Content-ID even when the message's attachment flag is false. Image caps: 8 MiB and 16 MP per image, 64 images and 48 MiB and 64 MP per message. |
| Message colours exactly as written, like Outlook (no monitor colour-profile tint; Options > Mail, on by default) | Yes (WebView2 `--force-color-profile=srgb`) | Snapshot: yes; WebKitGTK: not colour-managed | `ReaderColorProfile`; applies after a restart. |
| Interactive reader (character-level text selection and copy, like Outlook) | Embedded WebView2 first, snapshot as fallback; big messages (over ~2 MB with pictures) load from a private temp file; the reader's own document is allowed through the link guard once (`OwnDocumentNavigation`) | Embedded WebKitGTK if `libwebkit2gtk-4.1` is installed, otherwise snapshot | Under Wayland the embedded view can paint nothing, so connected mail starts on the snapshot and the interactive reader is an opt-in per message. During rapid selection changes, the embedded view stays hidden until the new page's unique marker is verified; otherwise the snapshot reader takes over. |
| Text selection and copy over the snapshot: character-level drag, double-click word, triple-click paragraph, Shift+click, Ctrl+A (whole message), Ctrl+C, right-click Copy / Select All; copies keep line breaks and tabs between table cells | Yes | Yes (also Wayland) | Chrome measures every character during the render (`SnapshotText`); `docs/reading-pane-text-selection.md`. |
| Reader actions: plain-text toggle, Show images, new window, More menu for alternate reader, trusted original HTML, browser, printable PDF, and zoom | Yes | Yes | Common buttons wrap within a narrow reading pane; PST messages use a compact **View as** menu for HTML, Rich Text, Plain Text, and Headers. No native print dialog. |
| Open message in its own window (double-click) | Yes | Yes | Microsoft and Gmail messages. |
| Attachment chips: Open / Save as…; risky file types are never opened | Yes | Yes | Microsoft file attachments up to 64 MiB; Gmail attachments. Opening uses the default program (`AttachmentLauncher`; xdg-open on Linux, untested on a real desktop). |
| Reading Pane options: mark as read after N seconds / on selection change, space-bar paging, portrait, always preview | Yes | Yes | `options.json`, `read-state.json`. |

## 7. Composing

| Feature | Windows | Linux | Notes |
|---|---|---|---|
| New compose window (Outlook-like: tabs, Send, From list of all connected accounts, To/Cc/Bcc, Subject) | Yes | Yes | Unbuilt buttons say "To be implemented". |
| **Recipient suggestions and Select Names** (2026-10-17) | Yes | Yes | Clicking into To / Cc / Bcc drops down the most recently used addresses (avatar with initials, name, address); typing narrows them by the start of any word of the name or address, contacts included; Up / Down, Enter / Tab, Escape, or a click. The To / Cc / Bcc buttons and Message > Address Book open **Select Names: Contacts**: Search (All columns / Name only), Address Book = the account's contacts or its recent recipients, columns Name / Display Name / Email Address, To / Cc / Bcc buttons, OK. Recent recipients: `recent-recipients.json` (every send; for Microsoft accounts the newest 200 Sent Items are read once per run while the list is short; at most 500 per account). Contacts: the Microsoft account's (read again after 5 minutes, and at once after a contact is saved or deleted in People). Gmail accounts: recent recipients only. Outlook's contact groups (Coworkers, Family, ...) are not offered (Graph's contacts API does not return them). **Check Names** (Message > Check Names, Ctrl+K, and automatically before Send) turns typed names into addresses (exact name, else the only match; several words in any order) and says which it could not resolve. |
| Microsoft: server drafts, reply, reply all, forward, attachments (small and large upload sessions), reopen drafts | Yes | Yes | |
| Gmail: send, reply, reply all, forward (with original attachments), save attachments | Yes | Yes | MIME built locally (`GmailMimeBuilder`, 25 MB limit). Editing a saved Gmail draft is not supported. |
| HTML editing in the message body (Outlook's Format Text tab and the Message tab's Basic Text: font, size, grow / shrink, bold / italic / underline / strikethrough, sub / superscript, highlight and font colour palettes, change case, clear formatting, bullets / numbering with styles, indents, alignment, line spacing, shading, ¶ marks, styles (headings, title, quote, code), Find, Select All, zoom; Insert > Link, Table, Horizontal Line, Pictures). Pictures (inserted or pasted) are sent as inline cid: parts (Gmail: multipart/related; Microsoft: inline attachments, synced on every draft save) and come back into the body when a draft reopens. A message is HTML or Plain Text (no Rich Text) | Yes (WebView2) | X11: yes (WebKitGTK, not yet tried); Wayland: yes, edited in place since 0.1.85 (the same editor page in the bundled headless Chromium, drawn into the body; keys, mouse, wheel, clipboard passed on). Only without any Chromium: read-only preview + "Edit message…" | The body is an editable page in the embedded web view; the ribbon shows the cursor's font, size and toggles. New messages follow Options > Mail > Compose messages in this format (HTML by default). The sent body carries Aptos 12 pt in a wrapper div. Not built: Format Painter, multilevel lists, borders, sort, Change Styles, Replace. |
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
| After deleting, archiving or moving the highlighted message, the next one in the list is highlighted and opened | Yes | Yes | `NoteRowLeaving` / `SelectDisplayedRow`: Microsoft, Gmail, PST deletes, PST drag-moves, server-side removals in a refresh. A batch highlights once, after the whole batch. The reader clears the old message as soon as selection changes and starts a load if the grid auto-selects a replacement without notifying the reader. Microsoft messages already in the local copy display before token and attachment requests. |
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
| Per-account on/off, keywords (From line contains), rules: high importance, no To address, **not addressed to me** (my address is not on the To line), sent on behalf, flagged, anyone in Bcc | Yes | Yes | Account Settings > Junk Cleaner tab; `junk-cleaner.json`. Matches go to Deleted Items, never a permanent delete; at most 500 per run. The history (`junk-cleaner.log`) keeps the newest 1,500 entries / 256 KB. |
| Clean Junk Now with a tick-box preview | Yes | Yes | Home > Junk ▾ > Clean Junk Folder... (the separate Junk ribbon group went away 2026-10-17; Report Phishing is listed there as planned), right-click > Junk, and the tab. |
| Keyword file `junk-keywords.json` (Open keyword file / Reload / Import keywords) | Yes | Yes | The keyword list is one comma-separated line in `junk-keywords.json` in the settings folder (`C:\Users\<user>\.config\OpenOutlook` or `~/.config/OpenOutlook`): `{ "keywords": "casino, aarp, ..." }` (a JSON array also works). It is shared by all accounts and edited in any text editor; the tab has an edit box with the list as one comma-separated line (**Save keywords** writes it to the file and cleans it up; the button is enabled only when the text changed), **Open keyword file** launches your text editor, **Reload** re-reads the file and drops unsaved edits, **Import keywords...** merges the old OutlookJunkCleaner `config.json` (or a file saved by an earlier OpenOutlook export) into it. Keywords that older builds kept in `junk-cleaner.json` move into the file on first use. A file that is not valid JSON is reported and never overwritten. To use the same list on two machines, copy the file. |
| Automatic cleaning every 1-60 minutes while the app runs, silent | Yes | Yes | Off by default; log in `junk-cleaner.log`. |
| Import of the old OutlookJunkCleaner `config.json` | Yes | Yes | Turns the cleaner on, leaves automatic cleaning as it was. |
| **Safe Senders, Safe Recipients, Blocked Senders** (Junk Email Options) | Yes | Yes | 2026-10-16, `docs/junk-lists-plan.md`. Right-click (and Home > Junk) > **Junk**: Block Sender (to Blocked Senders, the mail to Junk Email), Never Block Sender, Never Block Sender's Domain, Never Block this Group or Mailing List (to Safe Recipients), Not Junk (in Junk Email: to the Inbox, "Always trust email from ..." adds the sender to Safe Senders), Junk E-mail Options... (tabs Options, Safe Senders, Safe Recipients, Blocked Senders, International with the Blocked Top-Level Domain List; a search box per list; Add / Edit / Remove / Import from File / Export to File in Outlook's text format; "Automatically add people I email to the Safe Senders List" works for mail sent from OpenOutlook). Never Block Sender / Domain / Group used in Junk Email also moves the selected mail to the Inbox at once; each menu action is written to the log. As in Outlook an entry is on Safe Senders or on Blocked Senders, never both. Lists in `junk-lists.json`. OpenOutlook applies them every 5 minutes while it runs: mail from safe senders / to safe recipients leaves Junk Email, mail from blocked senders leaves the newest 200 of the Inbox (to Junk Email, never deleted), and the Junk Cleaner never matches a safe sender. "Also trust email from my Contacts" uses the account's contacts. |
| Outlook's own lists read from Outlook desktop | Yes (Outlook desktop installed) | No | Microsoft Graph cannot read or change the lists. Junk Email Options > Options > **Read Outlook's lists now** reads them read-only from `%LOCALAPPDATA%\Microsoft\Outlook\<address>.ost` (the "Junk E-mail Rule"; Outlook must be closed). Reading them automatically at start is a check box there, off by default (owner 2026-10-17). An entry removed in OpenOutlook stays removed at the next read; OpenOutlook never writes Outlook's file, so its own additions reach Outlook only through Export / Import. Linux: Import from File. Microsoft's filter level is shown, not changed. |
| **Inbox spam check** | Yes | Yes | 2026-10-29 (0.1.82), `MainWindow.SpamCheck.cs`. In a Microsoft Inbox the newest 200 messages are checked (when the Inbox opens, when new mail arrives, at most every 30 s; again after 10 minutes) against the account's own **Inbox spam rules** (same 18 rules as the Junk Cleaner, separate choice; default: no To address, anyone in Bcc, sender not verified, fake sender address, pretends to be a company, hidden characters, uses my username, random code), the Junk Cleaner keywords and Blocked Senders. Safe Senders / Recipients and trusted contacts are never marked. Suspects get a ticked box in a first list column (tooltip: the reasons). The bar above the list shows while the check is on ("no suspected spam in the newest N Inbox messages" when nothing is marked, 0.1.83): **Block Sender and Delete** (confirms, adds the ticked messages' senders to Blocked Senders, moves the messages to Deleted Items), **Delete** (to Deleted Items), ··· (Tick all, Untick all, Check again, Inbox spam rules..., turn off). Unticking remembers the message as not spam (`inbox-spam-check.json`, newest 2,000). Home > Junk ▾: Check the Inbox for Spam (on by default), Inbox Spam Rules.... Nothing moves without a click. |
| **Junk Email marks** | Yes | Yes | 2026-10-29 (0.1.84), `MainWindow.SpamCheck.cs`. The same first-column ticks and bar in a Microsoft Junk Email folder, showing what the Junk Cleaner's keywords and rules (Account Settings > Junk Cleaner) would delete; Safe Senders / contacts never marked. The bar says when the ticked ones go (next automatic clean, Clean Junk Folder, or "the Junk Cleaner is off"); Block Sender and Delete / Delete act now; ··· > Junk Cleaner rules.... A message unticked here is **kept by the Junk Cleaner** (manual and automatic runs; `KeepInJunk` in `inbox-spam-check.json`). Changing the Junk Cleaner settings or the keyword file marks the folder again. |
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
| Settings files | `~/.config/OpenOutlook/`: `attached-psts.json`, `view-layout.json`, `options.json`, `shortcuts.json`, `appearance.json`, `folder-order.json`, `read-state.json`, `default-account.json`, `mirror-settings.json`, `junk-cleaner.json`, `junk-cleaner.log`, `junk-keywords.json`, `junk-lists.json`, `inbox-spam-check.json`, `recent-recipients.json` |
| Accounts (labels and public ids; no tokens) | `~/.local/share/OpenOutlook/accounts.json` |
| Log | `~/.local/share/OpenOutlook/logs/openoutlook.log` |
| Mailbox copies | Windows `%LOCALAPPDATA%\OpenOutlook\Mail`; Linux `~/.local/share/openoutlook/mail`; or the folder chosen in Data Files |
| OAuth public client ids | `openoutlook-oauth.json` beside the program (git-ignored; template `openoutlook-oauth.example.json`) |
| Reader scratch files | `%TEMP%\openoutlook-reader` / `/tmp/openoutlook-reader` (private; stale browser profile folders in the temp folder are removed at start) |

## 12. Not built / known gaps

Calendar gaps (multiple calendars at once, overlay, recurrence and whole-series editing, offline access, scheduling tools, and day preview); People phases 2-4 (contacts in the mailbox copy, address book across accounts, contact groups, Gmail contacts), Tasks; signatures and spelling; undo/redo; rules; Automatic Replies; offline sending (Outbox); cross-store full-text search; native print dialog; Gmail draft editing and permanent Gmail delete; folder/label rename and delete from the mailbox copies; Junk Cleaner for Gmail; dark-mode and high-DPI verification; the PST engine writes Unicode-512 files only; `rmarrash_*.pst` fixtures are the owner's private data and are never in the repository.

## 13. Tests

- .NET unit tests `tests/OpenOutlook.Tests`: 748 pass on Windows (2026-10-29); five `OfflineMessageCacheTests` depend on Unix file permissions and cannot pass on Windows (all pass on Linux).
- Avalonia headless UI tests `tests/OpenOutlook.HeadlessTests`: 80/80 on Windows (2026-10-29); also run by Linux CI. `docs/EDGE_CASES.md` names the test for every deliberate edge case (`scripts/check-edge-cases.py`).
- C tests (`native/openpst/tests`: `test_basic`, `test_formats`, `test_write` with 188 checks and the clean-file guard) and the soak harness `tools/PstSoak`; SCANPST.EXE is the oracle for PST correctness (Windows).
- Live-account tools: `tools/MailSmoke` (send/receive between the owner's accounts, mirror, timings `timing` / `gmailtiming` / `gmailsync`, `localfolder`, `openpath`, `openeditable`; read-only junk analysis `junkscan`, `junkheaders`, `junkrules`, `inboxspam`; reader probes `readerprobe`, `imgprobe`).
- Linux/WSL scripts: `scripts/wsl-*.sh` (tests, package, deb smoke test, placement test, keyring diagnosis).

## New-mail indicator on the program icon (2026-10-19, 0.1.73-0.1.74)
- When an Inbox's unread number grows during a check while the OpenOutlook window is not active, mail has arrived that has not been looked at: the program icon shows it until the window is active again. **Linux:** the dock icon gets a badge with the number of new messages (Unity launcher-entry D-Bus signal `com.canonical.Unity.LauncherEntry.Update` for `application://openoutlook.desktop`, sent over a connection the program keeps open - the dock removes the badge when the sender's connection closes, so a one-shot `gdbus emit` shows nothing; works on Ubuntu's dock, Dash to Dock and KDE). **Windows:** the window icon gets a red dot (taskbar). Code: `MainWindow.MailBadge.cs`; fed from the new-mail checks (`NoteInboxUnread`). Not covered by automated tests (needs a dock); owner confirmed the badge on Ubuntu GNOME.
