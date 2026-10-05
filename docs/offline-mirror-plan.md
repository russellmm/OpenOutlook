# Plan: local mailbox mirror (offline PST) for Hotmail and Gmail

Status: **plan only, nothing built yet** (written 2026-10-05). Goal: every connected Hotmail / Gmail account can keep a complete local copy in a PST file that OpenOutlook reads instantly and can work in while offline, like the per-account `.ost` files of Microsoft Outlook (Account Settings > Data Files). Must work the same on Windows and Linux, and the user chooses where the files live.

## 1. What the user gets
- **Data Files screen** (File > Info > Account Settings > Data Files, as in Outlook): one row per account with name, file path, size, last sync time, status. Buttons: Settings…, Open File Location, Change Location…, Sync Now, Rebuild, Remove.
- **Choose where the files are saved**:
  - a global default folder (Options > Advanced > "Mailbox copies"), and a per-account override;
  - defaults: Windows `%LOCALAPPDATA%\OpenOutlook\Mail`, Linux `$XDG_DATA_HOME/openoutlook/mail` (`~/.local/share/openoutlook/mail`);
  - Change Location moves (or copies then switches) the file safely: close, copy, verify with the engine scan, switch the registry entry, delete the old file only after the user confirms. Also "Use an existing mirror here" to adopt a file after reinstalling or moving to a new PC.
  - validation when picking a folder: writable, free space ≥ mailbox size estimate + 20 %, warning for network shares and for cloud-synced folders (OneDrive, Dropbox, Nextcloud…) because a PST must not be synced by another tool while open.
- **Mail to keep offline** per account, like Outlook's slider: All / 1 year / 6 months / 3 months / 1 month / 2 weeks, plus an attachment size cap and a folder include / exclude list. Anything outside the window is still readable online.
- **Offline use**: with no connection the account still shows folders and messages; read / flag / move / delete / compose all work and are queued (Outbox folder for sends); a status line shows "Working offline, N changes waiting".
- Reading pane and search read from the local PST when the message is mirrored, and fall back to the server otherwise.

## 2. File format decision
The engine writes Unicode PST only, and Outlook's OST is a PST with different header flags and encryption options. So the mirror is a **standard Unicode `.pst`** named `<account address>.pst` by default. Consequences, to be stated plainly in the UI: OpenOutlook can open it, SCANPST can check it, Outlook itself can open it as an ordinary data file, but Outlook will not treat it as the offline copy of the account. (Producing a true OST is out of scope.) A sidecar file `<name>.sync` (SQLite, section 4) sits next to it and is never required to read the PST.

## 3. Components (all new code is cross-platform .NET 8; no Windows-only APIs)
1. **MirrorStore**: wraps one PST through the existing native engine (`NativePstEngine`): create, open, scan, create folder, import message (from MIME), set flags, move, delete, replace contents of a folder. Single writer per file; one `SemaphoreSlim` per mirror serialises sync and UI edits. The engine's contents-table rewrite on big folders (a few hundred ms) means writes are **batched**: sync applies a page of changes as one operation per folder, not one per message.
2. **SyncStateStore**: SQLite (`Microsoft.Data.Sqlite`, works on both OSes; if native-library packaging on Linux proves awkward, fall back to a transactional JSON-lines file). Tables: `folder(remoteId, pstNid, deltaToken/historyId, lastSync)`, `message(remoteId, folderRemoteId, pstNid, changeKey/etag, isRead, flagged, labels)`, `pending(seq, op, args, createdAt, attempts)`.
3. **Provider sync adapters** behind one interface `IMailSyncSource`:
   - Microsoft Graph: folder tree with `GET /me/mailFolders` (child folders recursively), per-folder `messages/delta` (change tokens), message body + attachments as MIME via `GET /me/messages/{id}/$value`, which the app already uses for archive import (`GraphInboxReader.GetMessageMimeAsync` + `EmlParser`).
   - Gmail: labels as folders (existing model), `users.history.list` from the stored `historyId` for incremental sync, `messages.get?format=raw` for the MIME. A message with several labels is stored once per label folder (same as an IMAP client) and all copies share one remote id; All Mail is not mirrored. History id expired (HTTP 404) means full resync of that account.
4. **SyncEngine**: scheduler (on startup, every N minutes, on window focus, manual Sync Now), network-state detection, retry with back-off, cancellation, progress events for the status bar and Data Files screen.
5. **Outbound journal**: every local change (read, flag, move, copy, delete, new folder, send) is written to the PST and to `pending` in one step; a replayer pushes them with the existing writers (`GraphMailWriter`, `GmailMailbox`) and removes them on success. Sends go to the PST Outbox first, then are sent and moved to Sent Items.
6. **Reader integration**: `MainWindow` account nodes open the mirror PST through the existing PST reading code path (fast), with an "online" badge and a fallback to the current online reader for anything not mirrored. Mark-read-on-view, drag/move and the Move/Copy dialogs write to the mirror first and the journal second.
7. **Location service** (`MirrorLocations`): resolves default and per-account paths per OS, validates, relocates, adopts existing files; stores choices in the account registry / options file.
8. **Data Files UI** (Avalonia, shared by both OSes) and Options page.

## 4. Sync rules
- **Initial sync**: folders first, then messages newest to oldest inside the chosen window so the inbox is usable within seconds; bodies and attachments fetched in bounded parallelism (default 4) with per-provider rate-limit handling (Graph 429 / Retry-After, Gmail 429 / quota backoff).
- **Incremental**: apply server changes (new, changed flags, moved, deleted, folder renamed) from delta / history; apply the local journal; when both changed the same message the server wins for read / flag state, a local delete wins over a server flag change, and a conflict that cannot be resolved is logged and shown in the Data Files status.
- **Idempotent and resumable**: killing the app at any moment must leave a consistent PST (the engine's crash-safe commit order) and a sidecar that can be replayed; the next run continues from the stored tokens.
- **Identity**: the PST message carries its remote id in a named property so the sidecar can be rebuilt by scanning the PST if it is lost.
- **Deleted items**: delete moves to Deleted Items / Trash on the server first and mirrors that; permanent deletion only from Deleted Items, as today.

## 5. Integrity and recovery
- After each sync session that wrote more than a threshold, run the engine's `Scan`; if it reports problems run `Repair` (the SCANPST-validated fixer) and re-scan; if that still fails, offer **Rebuild** (delete the mirror and download again). The user's real server mailbox is never modified by a rebuild.
- The PST is opened exclusively by OpenOutlook (file lock on both OSes); a second instance or an external viewer gets a clear "in use" message.
- Backups: Rebuild keeps the old file until the new one scans clean.
- Disk full / read-only folder / path vanished (removable drive): sync pauses with a clear status; the Data Files screen offers Change Location.

## 6. Cross-platform specifics
- Paths via `Path.Combine` and the existing folder picker (`SafePick.FoldersAsync`); no drive letters assumed; case-sensitive file names on Linux; long-path and reserved-name checks on Windows (`CON`, trailing dots) for account-derived file names (sanitise `@`, `:` etc.).
- Default folders via `Environment.SpecialFolder.LocalApplicationData` (Windows) and the XDG variables (Linux), the same helper the app already uses for settings.
- File locking: `FileShare.None` on Windows, `flock`-style advisory lock on Linux (via `FileStream` lock + a lock file because the engine uses its own handle).
- Network state: `NetworkChange` events work on both OSes; fall back to periodic reachability probe to the provider host.
- Secret storage already works on both (Credential Manager / libsecret); sync only needs access tokens through the existing `MicrosoftMailSession`.
- Open File Location: `explorer /select,` on Windows, `xdg-open <folder>` on Linux (macOS `open -R`), never executed through a shell.

## 7. Phases (each ends with a build, tests, a published EXE and a push)
| Phase | Scope | Exit criteria |
|---|---|---|
| 0 | Decisions in section 9; `IMailSyncSource` + `MirrorStore` skeleton; `MirrorLocations` with tests on both OSes; Data Files screen showing accounts with "not mirrored" and the location chooser | location chosen, validated, persisted; screen renders in headless tests |
| 1 | Microsoft one-way mirror: folder tree + messages (+ attachments) into a PST, initial and delta sync, window setting, progress UI, read from the mirror | a real Hotmail mailbox mirrors to a PST that SCANPST reports clean; reading is from the PST |
| 2 | Local changes pushed to Hotmail (read, flag, move, copy, delete, folder create) via the journal; offline mode; Outbox | airplane-mode test: changes made offline appear on the server after reconnect |
| 3 | Gmail mirror (labels, history sync, multi-label copies) and journal | same two tests on Gmail |
| 4 | Change Location / adopt / Rebuild / remove; integrity scan + repair loop; disk and network failure handling | relocation test, kill-during-sync test, corrupted-file recovery test |
| 5 | Polish: sync scheduler options, per-folder include / exclude, status bar, Linux packaging check under WSL, docs | 24-hour soak with random operations on a fake server, then on the real accounts |

## 8. Testing
- Fake Graph and Gmail servers (already used by the provider tests) extended with deltas, history ids, 429s and failures; mirror tests compare the PST contents (through the engine) with the fake server state after every random operation sequence.
- Extend `tools/PstSoak` with the sync operations so every mirror is checked by SCANPST (Windows) and by the engine scan (Linux).
- Kill-the-process tests at every journal step; relocation tests on temp folders on both OSes; headless UI tests for the Data Files screen and location chooser.
- `tools/MailSmoke` gains a `mirror` step for live accounts (read-only against the server unless `--write` is passed).

## 9. Decisions (owner, 2026-10-05)
1. Format: standard `.pst` now; a true OST can be added later.
2. Sync bookkeeping: SQLite.
3. Gmail messages with several labels: a copy in each label folder (IMAP-like).
4. Defaults: keep the last 12 months; attachments up to 25 MB.
5. Mirroring is on by default for connected accounts.
6. Size: about 100 messages per mailbox (small: no performance risk, batches can stay simple).

## 9a. Finding: the engine cannot create a PST yet
`opst_*` only edits an existing PST. Nothing in the C library, the C# wrapper or the Python tools writes a blank Unicode PST (header, NBT / BBT, AMap, message store, name-to-id map, root folder, standard folders). Without that the mirror has no file to start from. Options: (a) implement `opst_create` in the C engine, validated by SCANPST like every other rule (recommended: it is the same machinery, and it also gives "New archive file"); (b) ship a blank PST made once in Outlook as a template (needs a display-name / record-key rewrite so copies are unique). Phase 1 depends on one of them.

**Resolved 2026-10-05:** option (a) was built and passed SCANPST (`opst_create`, `PstEngineFactory.Create`, design rule 36); the owner also supplied a blank Outlook file for comparison.

## 9b. Progress
- Phase 1 core (2026-10-05): `MirrorSyncEngine` (one-way, server to PST: folder tree with create / rename / move / purge, new / changed / removed messages, keep-window, per-message failure isolation, oversize and non-mail messages remembered), `IMailSyncSource`, `GraphMirrorSource` + `GraphMailboxSyncReader` (list by date window, well-known folder ids). Messages are compared by id and read / flag state (list-and-compare; delta queries are an optimisation for later, the mailbox is small). Live run on the owner's Hotmail: 1,140 messages, 19 folders, about 2 minutes first time, 8 seconds when nothing changed; engine scan clean; SCANPST: no error-level findings after the fixes in design rule 38 (one "minor" case: recipient lists over 3.5 KB). Tests: `MirrorSyncTests` (6), `PstBigValueTests` (9). In the app: File > Info > Data Files… (per account: location, keep window, attachment cap, Sync now, Change location, Open file location), background sync 20 seconds after start and every 15 minutes (`OPENOUTLOOK_NO_MIRROR=1` turns it off for tests), the copy appears in the folder tree like an archive and is read from the file. Change location copies and scans the file clean before switching and leaves the old file for the owner to delete. Microsoft accounts only; Gmail is Phase 3. Local changes are not sent to the server yet (Phase 2).
- Phase 0 started: `src/OpenOutlook.Mirror` (settings with the defaults above, `MirrorLocations` for Windows / Linux default folders, per-account override, file-name safety, folder validation incl. free space, network, removable and cloud-sync warnings; `SyncStateStore` on SQLite with folders, messages, change tokens and the pending-change journal). 15 unit tests.
- Not yet: Data Files screen, `MirrorStore` (needs a PST to open), sync adapters, journal replay.

## 10. Risks
- Engine write cost on very large folders (mitigated by batching; Phase 1 measures it on the real mailbox).
- Gmail history gaps and Graph delta resets (mitigated by automatic folder-level resync).
- Users putting the PST in a cloud-synced folder (warning plus exclusive lock).
- A PST is a single file: corruption affects the whole mirror (mitigated by scan / repair / rebuild, and the mirror is never the only copy because the server remains the source of truth).
- Google token expiry while the consent screen is in Testing mode (7 days): sync must surface "sign in again" clearly rather than silently stopping.
