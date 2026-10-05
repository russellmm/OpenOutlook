# OpenOutlook – Native PST Engine Integration Plan

Status: approved direction (2026-10-04). Local branch / patch set only; nothing is pushed.

## 1. Decisions

| # | Decision |
|---|----------|
| 1 | Keep Avalonia (.NET 8). The native OpenPST C library is the only PST engine, behind `IPstEngine`. **Revised 2026-10-04: the original managed `PstCore` engine is NOT trusted (it was replaced because it cannot manipulate PSTs correctly); its useful features are merged into OpenPST and it is then removed. See section 14.** |
| 2 | **Always edit mode.** PSTs open writable by default; read-only fallback only when the file/media is not writable or locked by another process. |
| 3 | `.bak` copy is **opt-in** (Options setting, default off). Crash safety comes from the native journal, not copies. |
| 4 | C source is **vendored in the OpenOutlook repo** at `native/openpst/`, built by CMake as part of publish. |
| 5 | First deliverable: read-path swap (Phases 0–1), then native read/flag (Phase 2), import/cross-source copy (Phase 3). |
| 6 | A C# port of the writer is a possible later project; the `IPstEngine` seam and the byte-comparison harness keep that open. |

## 2. Findings from the local code review (`F:\Claude\OpenOutlook`, == GitHub 3283a17)

- Reading-pane mark-read works (`MainWindow.ReadingState.cs`: 2 s timer, mark-on-selection-change option) but persists to the PST only when `store.CanWrite`. Stores are opened `writable:false` (`MainWindow.axaml.cs` ~443); otherwise state goes to the `read-state.json` sidecar. Always-edit mode removes this gap.
- `PstEditSession.Begin` (`MainWindow.Editing.cs`) makes a `.bak`, takes `.lck`, verifies and rolls back. Under the new policy: `.lck` stays, `.bak` becomes opt-in, rollback is replaced by journal recovery (native) with `.bak` restore only when the option is on.
- Options stored but not implemented: single-key reading (space bar), full-screen portrait reading, always-preview. Included in Phase 4.
- Managed `PstCore` writer is append-only (no page splits, growth, journal). It is therefore **not** used for always-on writing; it is read-only fallback plus legacy edit path for files the native engine rejects.

## 3. Architecture

```
OpenOutlook.Desktop (UI)
        |
  IPstEngine  (new, in PstCore or new project OpenOutlook.PstEngine)
     |                      |
NativePstEngine         ManagedPstEngine
(P/Invoke OpenPst.cs)   (existing PstStore/PstCore)
     |
native/openpst  (libopenpst.so / openpst.dll)
```

- `IPstEngine` mirrors the current `PstStore` public surface so UI changes are minimal: `Open`, `AllFolders`, `FindFolder`, `GetMessages`, `OpenMessage`, `ReadAttachmentData` (+bounded), `Search`, `SetReadState`, `SetFlagged`, `MoveMessage`, `CopyMessage`, `DeleteMessage`, `CreateFolder`, `DeleteFolder`, `RenameFolder`, `MoveFolder`, `VerifyIntegrity` (→ native `check`), `Repair` (→ native `fix`), `DeletedItemsFolder`, `Dispose`.
- `PstEngineFactory.Open(path)`: probe header (`PstStore.Inspect`) → Unicode wVer 23 and native library loadable → `NativePstEngine`; else `ManagedPstEngine` (ANSI, wVer ≥ 36, missing native lib) in read-only mode with a visible notice.
- Keep `_readerGate` semantics: one native handle per PST, access serialized by the existing gate; the C library is not thread-safe per handle.
- Model types (`MailFolder`, `MailSummary`, `MailMessage`, `MailAttachment`) stay in managed code; native structs are mapped in `OpenPst.cs`.

## 4. Always-edit mode

- Open order: acquire `.lck` → open native `OPST_OPEN_WRITE` (runs journal recovery automatically; surface `opst_recovered()` as a status message) → on failure (read-only file/media, lock held) reopen read-only and show "Read-only" badge in the title/status bar.
- `CanWrite` is true for native writable opens; the sidecar `read-state.json` stays as fallback for read-only PSTs and Graph accounts only.
- Remove the "start editing session" gate from the UI (`EnsureWritableStore`): editing actions call the engine directly.
- **Write batching:** mark-read/flag changes are applied to the in-memory overlay immediately and committed on a debounce (e.g. 1.5 s idle), on folder change, on window close, on app exit and on an explicit save. Each commit is journaled (crash-safe). Moves/deletes/folder operations commit immediately.
- Shutdown: flush pending commits, release `.lck`. Crash: next open replays/discards the journal.
- Outlook running with the same PST: `.lck` plus OS file lock detection → open read-only with warning (never write under Outlook).
- Optional `.bak` (Options → Archives → "Make a backup copy before first change in a session"): one copy per file per session on the first write; default off.

## 5. Native build, vendoring, packaging

- Vendor `F:\Claude\openpst` into `native/openpst/` (src, include, tools optional, tests, `CMakeLists.txt`, `LICENSE`); keep `dist/openpst.c` amalgamation as a single-file build option.
- `scripts/build-native.ps1` / `.sh`: CMake release build, outputs `openpst.dll` (win-x64) and `libopenpst.so` (linux-x64; linux-arm64 if desired) into `src/OpenOutlook.Desktop/runtimes/<rid>/native/`.
- Desktop `.csproj`: include `runtimes/**/native/*` as `Content`/`NativeLibrary`; keep `IncludeNativeLibrariesForSelfExtract` for the single-file Linux publish. Resolve with `NativeLibrary.SetDllImportResolver` (try app dir, then `runtimes/<rid>/native`).
- CI: build native on Windows (MSVC) and Linux (gcc) → run C tests (161 + 85 checks) → build/publish .NET → run xunit.
- Version handshake: `opst_version()` checked against the expected value at startup; mismatch → fall back to managed read-only with a log entry.

## 6. New native features required (C library work)

1. `opst_msg_set_read(msg, read)` and `opst_msg_set_flag(msg, status)`: message PC `0x0E07` flags, contents-table row, folder unread/total counts, flag status `0x1090`.
2. Row/attachment structs: hidden-attachment and flag-status fields.
3. Message import (`opst_msg_import`): build a PST message from fields/attachments/body (EML and Graph sources) for cross-source copy and "save mail to PST".
4. Folder rename/move/empty exposed in C# (already in C; add bindings).
5. Thread-safety documentation + `opst_last_error` per handle.
6. Optional: `opst_commit()` explicit flush for the debounce.

## 7. Phases

| Phase | Scope | Exit criteria |
|-------|-------|---------------|
| 0 | Vendor C source, CMake build in repo, `OpenPst.cs` binding project, `IPstEngine` + `ManagedPstEngine` wrapper (no behavior change). | Solution builds on Windows and Linux; 399 existing tests pass through `ManagedPstEngine`. |
| 1 | `NativePstEngine` read path (folders, messages, bodies incl. RTF→HTML, attachments, search); factory + fallback; contract tests run both engines on the same fixtures. | Both engines return identical results on fixtures (`OPENOUTLOOK_TEST_PST`); large-PST open time ≤ managed. |
| 2 | Always-edit: native set-read/flag (§6.1–2), move/copy/delete/folder ops via native writer, debounced commit, `.lck`, read-only fallback, optional `.bak`, retire sidecar for PSTs. | Reading pane marks mail read **in the PST**; SCANPST clean after a scripted edit session; crash-recovery test (kill mid-write) passes. |
| 3 | Import (§6.3): cross-source copy Graph/EML → PST, folder import/export, drag-drop between PST and mail accounts. | Round-trip test: import → Outlook/SCANPST validation. |
| 4 | Reading-pane options: space-bar single-key reading, portrait full-screen, always-preview; Options → backup toggle; "Repair archive" UI over native `fix`/`check`. | All six reading-pane options work; options dialog reflects real behavior. |
| 5 | Hardening: fuzz native readers (RTF + NDB/LTP), ASAN CI job, large-file (≥1 GB) perf pass, packaging for .deb/tar.gz/Windows. | Fuzz run clean; publish artifacts install and open a PST on a clean machine. |

## 8. Testing strategy

- Contract tests: one xunit suite parameterized over `IPstEngine` implementations; fixture PSTs including a ≥1 GB archive (opt-in via env var).
- Differential tests: native writer vs Python reference stays byte-identical (`OPST_TEST_RANDOM=1`, existing harness); keep it in `native/openpst/tests`.
- SCANPST oracle: scripted `run_scanpst2.ps1` against files edited by the app (manual/CI-Windows job).
- Crash tests: kill the process during commit; reopen; verify journal recovery and `check` is clean.
- UI smoke: existing headless screenshots flow.

## 9. Risks and mitigations

| Risk | Mitigation |
|------|-----------|
| Memory-safety bugs in native parsing of hostile PSTs | ASAN/UBSAN + fuzzing in CI; handle sandbox limits (max sizes) already in the library; managed fallback for suspicious headers. |
| Two engines drifting | Single `IPstEngine` contract suite; native is the default, managed is read-only fallback only. |
| Packaging failures (native lib missing/trimmed) | Resolver + handshake + graceful fallback; CI smoke test of the published artifact. |
| Writing while Outlook has the file open | Lock detection → read-only mode. |
| Data loss with `.bak` off | Journal + commit ordering; `check`/`fix` available; optional `.bak` documented in Options. |
| ANSI / wVer ≥ 36 files | Managed read-only fallback; document as unsupported for editing. |

## 10. Housekeeping (when you say so)

Delete scratch PSTs in `F:\Claude` (test_C0–C6, test_P1*, *_fix_pass*, logs), `F:\Claude\openpst\build-msvc`, refresh stale `bin-win`; leave `rmarrash_*.pst` and `test-archive.pst` in the repo untouched.

## 11. Feature backlog (to be defined with the owner once the GUI runs)

Source of this list: the 38 ribbon/backstage controls that currently call `RibbonPlaceholderClicked` in `MainWindow.axaml` ("Planned feature"), plus the owner's screenshots (2026-10-04). The owner will add the full list once the GUI is confirmed; Phase 4 above is only a placeholder (three unimplemented Reading Pane options, backup toggle, repair UI). The owner has stated there are many more features to add. Each item gets: description, phase, whether it needs new native (C) work.

| Feature | Phase | Needs native work? |
|---------|-------|--------------------|
| Undo / Redo (status bar: "planned") | after Phase 2 | Yes - journal-level undo or inverse ops (move/delete/flag) |
| Calendar, People, Tasks views (nav rail) | TBD | Yes - appointment/contact/task item classes, tables |
| New Items menu; Meeting; More (respond) | TBD | Meeting = calendar; New Items = item-type creation (Yes, import/create) |
| Ignore (conversation), Clean Up (conversation) | TBD | Yes - conversation index (0x71 / 0xE01 data already understood) |
| Quick Steps: Create New, Quick Step Settings | TBD | No (app-level rules) / uses native move/flag/read |
| Move (menu), Rules, Rules and Alerts, Automatic Replies | TBD | Rules: app-level; PST rule store optional |
| Categorize (color categories) | TBD | Yes - PidNameKeywords named property + master category list |
| Groups: New Group, Browse Groups | TBD (provider-dependent) | No (Graph/Exchange) |
| Find: Address Book, Filter Email | TBD | Filter: native search exists; Address Book needs contacts |
| Read Aloud, Translate, All Apps / My Apps, Web Add-ins | TBD | No |
| Junk: Phishing report, Remove Junk | TBD | Remove Junk = move/purge (native has); Phishing = provider |
| Folder tab: New Folder, Move Folder, Offline, Change View | Phase 2 | New/Move Folder: native has create/move; views are UI |
| View tab: Reading Pane options (3 of 6 unimplemented), Zoom | Phase 4 | No |
| File backstage: Import, Profile picture, Help, unfinished Options pages | TBD | Import = native import (Phase 3) |
| Message-list/reader polish toward Outlook 2024 classic look | ongoing | No |

## 12. Phase 0 status (2026-10-04, branch `native-engine-phase0`)

Done: C source vendored at `native/openpst/`; `scripts/build-native.ps1|.sh`; `OpenOutlook.PstNative` project (binding + `NativeLibraryLoader` with version handshake and graceful "unavailable"); `IPstEngine` implemented by `PstStore` (no behavior change); `PstEngineContractTests`. Native build and its 2 C test suites verified on MSVC and gcc/WSL. .NET suite: 403 pass; the 6 failures (OfflineMessageCache x5 – unix permissions, Loopback x1 – socket) are identical on the untouched baseline.

## 13. Phase 1 status (2026-10-04, branch `native-engine-phase0`)

Done: `NativePstEngine` (read path: folders, messages, bodies incl. RTF→HTML, attachments with size limit, search, integrity check; writes fail closed), `PstEngineFactory` (native for read-only opens, managed for writable opens / unsupported files / missing library; `OPENOUTLOOK_ENGINE=managed` forces managed), Desktop now opens archives through the factory and logs which engine ("pst-engine" in the app log). C library: `flag_status` added to message rows, `hidden` to attachments. The C# wrapper now lives only in `src/OpenOutlook.PstNative/OpenPst.cs`.

Findings (native vs managed, on rmarrash_1 / rmarrash_3 (13,410 messages, 2.6 GB) / rmarrash_2 / test-archive):
- **The managed engine misreads contents-table rows on large archives**: ~10% of messages show another message's sender/date/size, ~1,200 show missing class/read state, thousands miss Sent/To. Native rows agree with the messages' own properties on every message checked. This is a bug in the app today.
- Managed returns 8.3 short attachment names (`GuestA~1.pdf`), no MIME type, no Content-ID; native returns the long name, MIME and Content-ID.
- Managed shows no HTML for RTF-only mail; native converts RTF to HTML.
- Folder tree: the managed engine lists folders missing from their parent's hierarchy table (created by the old append-only writer in rmarrash_2 / test-archive: native `check` reports "has-subfolders property but 0 subfolders" and NBT index beyond header). Native follows the hierarchy table; search folders (nid type 3) are not shown by either engine.
- Native skips contents-table rows with node index 0 (dangling rows, seen in test-archive.pst).
- After an editing session starts, the app still switches to the managed writer for that archive (Phase 2 replaces it), so the managed row bug returns after the first edit until Phase 2.

Tests: 413 pass on Linux (WSL) incl. 8 engine contract tests; Windows: same 6 known Windows-only failures + `PstEditingTests.CreateFolderRejectsDuplicates...` which fails (file locked) on the untouched baseline too whenever OPENOUTLOOK_TEST_PST is set. Contract tests run clean on rmarrash_1, _2, _3 and test-archive.


## 14. Revised direction: retire the managed engine (owner decision, 2026-10-04)

The managed `PstCore` engine is not a fallback and not a reference. OpenPST is the single engine; anything useful in `PstCore` is merged into OpenPST and `PstCore`'s reader/writer is then deleted.

**What the managed engine does that OpenPST does not yet (merge list)**
1. ANSI PST files (wVer 14/15) – read (and write).
2. 4K-page PST files (wVer 36/37, Outlook 2013+ format) – read (and write). OpenPST currently rejects them (`op_ndb.c`).
3. Cyclic encryption (crypt method 2). OpenPST handles none and permute (method 1) only.
4. Single-writer `.lck` lock naming the owner process (stale-lock replacement) – move into OpenPST (or the engine wrapper).
5. Optional `.bak` before first change (opt-in) – keep as an engine-level option; remove verify/rollback-by-copy in favor of the journal.
6. `EmlExport` / folder EML export, attachment export – keep (output features; already run over `IPstEngine`).
7. Read-state sidecar (`read-state.json`) – keep only as a fallback for archives that cannot be opened writable.

Everything else in `PstStore` (folders, messages, attachments, search, move/copy/delete, create/delete folder, integrity check) already exists in OpenPST and is more correct.

**Changes to the plan**
- Section 3 / factory: the managed engine is no longer a fallback for writing. Writing goes to OpenPST only. Until the merge items 1–3 land, files OpenPST cannot open are opened read-only with a visible warning (via the temporary managed reader) and are not editable; once 1–3 land the managed engine is deleted.
- Phase 2 (always-edit on the native writer) moves up and now also **removes the managed edit session** (`PstEditSession`, `PstStore` write paths) from the app.
- New Phase 2b: merge items 1–3 into OpenPST (C), validate with the Python reference and SCANPST like the Unicode path.
- Risk table: "ANSI / wVer >= 36 files: managed read-only fallback" is replaced by "OpenPST gains ANSI + 4K support (Phase 2b)".
- Contract tests: the managed engine is no longer a comparison target for correctness. Message ids/folders comparisons are kept only until it is deleted.

## 15. Phase 2 status (2026-10-04): always-edit on the native engine

Done:
- **C library:** `opst_msgs_set_state(p, nids, n, read, flag)` - sets read/unread and flag status in one transaction: message PC (in place), contents-table row (flags, flag status, row version) and the folder's unread count (PC + hierarchy row). A message without PidTagFlagStatus gets the property inserted in place. Test added to `test_write.c`.
- **NativePstEngine writable:** SetReadState, SetFlagged, MoveMessage, CopyMessage, DeleteMessage (permanent), CreateFolder, DeleteFolder (empty folders only), in-memory folder tree kept in sync (same objects the UI holds). Single-writer `.lck` lock (cross-process and in-process).
- **Factory:** `OpenEditable` - native writable by default; falls back to native read-only when locked/not writable (reason shown), and to the managed *reader* only for files native cannot open. The managed engine is never used for writing.
- **Desktop:** archives open editable; `PstEditSession`, per-operation verify/rollback and the shutdown commit are gone from the app. `.bak` is opt-in (`OptionsSettings.PstBackupBeforeEditing`, default off; Options UI toggle still to do). Reading-pane mark-read and flag/read actions run off the UI thread. Status texts say "saved".
- **Validation:** engine contract tests incl. edit round trip + integrity-finding kinds on rmarrash_1/_2/_3 (2.6 GB) and test-archive; SCANPST on a native-edited copy of rmarrash_2 (mark read/unread, flag/unflag, create folder, copy, move, move to Deleted Items): no new findings versus the pre-existing damage of the original file (old managed-writer artifacts: AMap free count, NID high-water marks, orphaned folder). 415 tests pass on Linux.

Known / next:
- **Performance:** a read/flag change rewrites the folder's whole contents table (~0.4-1 s in a 2,690-message folder of the 2.6 GB archive). Needs an in-place table-cell patch (Phase 2c) and/or debounced commits.
- Delete Folder is still "empty folders only" (managed behaviour); Outlook semantics (move to Deleted Items) later.
- Options UI toggle for the opt-in `.bak`; remove `PstEditSession`/managed writer code and tests when the managed reader is retired (Phase 2b: ANSI, 4K-page, cyclic encryption in the C library).
- Test scratch files in F:\Claude: test_P2.pst, test_P2.log, test_P2_base.pst, test_P2_base.log (delete when done).

## 16. Speed fix and Phase 2b (2026-10-04) - the managed engine is retired

**Correction to section 14:** the managed `PstCore` engine never supported 4K-page files (it treated wVer >= 36 as Unicode-512) and never wrote ANSI; only ANSI *reading* and cyclic decoding were real. The "merge list" therefore reduced to ANSI read + cyclic; 4K was new work.

Speed:
- The allocation-map reconcile (a walk of both B-trees, ~290 ms on the 2.6 GB archive) now runs once per handle instead of once per transaction.
- Read/flag changes are write-behind in `NativePstEngine`: applied to the UI objects at once, written in one native transaction after 1.5 s idle (or before any read/structural operation, `Flush()`, `Dispose`). A transaction on the big archive dropped from ~450 ms to ~150 ms, and bursts cost one transaction.

Formats (OpenPST reader; writing stays Unicode-512 only, everything else opens read-only with a clear message):
- **ANSI (wVer 14/15):** 32-bit BIDs/IBs, 496-byte pages (CRC over 500 bytes), 12/16-byte tree entries, 12-byte block trailers, 4-byte-header SLBLOCKs, 2-byte row indexes. Validated on `archive.pst` (744 items, 2001), `sent2004.pst` (9,145 items) and `outlook.pst`: every message and attachment opens, folder counts match, text decodes. The managed engine read none of the ANSI messages.
- **4K-page (wVer 36/37, Outlook .ost):** 4096-byte pages (16-bit counts, 24-byte trailer, CRC over 4072), blocks aligned to 512 with a 24-byte trailer, **zlib-compressed blocks** (trailer cbUncompressed != cb) via a built-in inflate, heap pages of up to 64 KB addressed as 8 block indexes each. Validated on two real Outlook OST caches (2,184 and 8,457 items, ~2/3 of blocks compressed): all items and attachments open, folder counts match the tables. OST contents tables carry a bogus sender column (it points at the change key), so for 4K files the engine reads the sender from the message.
- **Cyclic encryption (method 2):** read and write path; vector test from an independent implementation of the MS-PST 5.2 algorithm. No real cyclic file was available, so it is unit-tested only.
- Not supported (clear error): WIP-protected files (wVer 37 with crypt 0x10), the integrity check / fixer for ANSI and 4K files, copying messages out of ANSI/4K files, Windows address books (.pab).
- Tests: `test_formats.c` (cyclic + inflate vectors, corrupt-stream robustness), ASAN/UBSAN run over the ANSI and OST files (`scripts/wsl-native-asan.sh`), opt-in .NET fixture tests (`OPENOUTLOOK_TEST_ANSI_PST`, `OPENOUTLOOK_TEST_4K_PST`).

Managed engine removed: `PstStore`, `Ndb`, `HeapOnNode`, `TableContext`, `PstEditSession`, `PstCrypto`, `BinaryUtil`, `RtfDecompressor` and their tests (read-only, editing, bounded-read, integrity-baseline) are deleted from the branch (git history keeps them). `PstCore` now holds only the shared models, `IPstEngine`, `EmlExport`, `PstHeader`/`PstException`. `PstEngineFactory` is native-only. `OPENOUTLOOK_ENGINE` no longer exists.

Still open: writing ANSI/4K files (not planned: Outlook owns OSTs; ANSI is legacy), message import (Phase 3), in-place table-cell patch (speed), Delete Folder semantics, Options UI toggle for the opt-in `.bak`.

## 17. Phase 3 status (2026-10-04): import and cross-source copy

Done:
- **OpenPST `opst_msg_import` / `opst_msgs_import`:** builds a complete Outlook-shaped message from plain fields - property context (identity keys, one-off entry ids, conversation index/topic, bodies as text and HTML, internet headers, Message-ID), recipient table (18 columns, 69-byte rows), attachment table, attachment nodes, large values in subnodes with multi-block data trees - then files it: contents-table row, folder counts, message index (0xE01 bucket by conversation GUID), and the ID map when the file keeps one. Batched in one transaction (one contents-table rewrite).
- **Validation with SCANPST repair-and-diff** (owner's method; up to 5 passes): scan of an imported file shows no new findings; the first pass over the original file's old damage needed 4 passes to reach NO_ERRORS; imported message nodes (all properties, subnodes, tables) are byte-for-byte identical before and after 5 repair passes. Issues found and fixed this way: exact PR_ATTACH_SIZE (sum of the attachment's property value sizes), local nids taken from the header counters (type 5 high-water mark), message size including the recipient/attachment tables (SCANPST compares it with the real size), row-only cells (0x0E17, 0x3013 per-row GUID, replication cells 0x0E30/33/34 + ID map entry when the file has an ID map). SCANPST still regenerates the 0x3013 value of each row (harmless).
- Bug found by the tests: a transaction that allocates nothing crashed on commit after the speed fix (per-section arrays were created lazily by the reconcile that is now cached).
- **PstCore:** `MailImport` (neutral message), `EmlParser` (MIME: RFC 2047/2231, any charset, multipart alternative/mixed/related, base64/QP, inline pictures, attachments, 8-bit/UTF-8 headers, tolerant of sloppy input), `IPstEngine.ImportMessages` (batched, cancellable) and `CopyMessagesTo` (archive to archive, existing native cross-file copy).
- **Graph:** `GraphInboxReader.GetMessageMimeAsync` (`/me/messages/{id}/$value`, 64 MB cap, account-verified).
- **App:** File > Import (EML files / a folder of EML files with subfolders), drop .eml files or folders on an archive folder or on the message list, right-click "Copy to Archive Folder..." for archive messages and for connected-mailbox messages, and archive-to-archive drag (move = copy + delete; before this the drop target accepted other archives' payloads against the wrong store).
- **Tests:** EML parser (8), import round trips on rmarrash_1/2/3 (batch, subnodes, multi-block attachments, read/flag/move/delete afterwards, read-only failure), cross-archive copy and move, Graph MIME download; C import checks in test_write.c. Linux 422 pass; Windows the 6 known failures.

Not done / next: PST -> EML export of selected messages is the existing exporter; a "Copy to Outlook.com" direction (archive -> mailbox) needs Graph write (create message from MIME) and is not part of this phase; Outlook itself has not opened the imported files (only SCANPST and the engine's own reader), so a manual check in Outlook of one imported archive is the remaining acceptance step.

## 18. Phase 4 status (2026-10-05): reading-pane options, backup toggle, repair UI

Done:
- **Single key reading (space bar):** Space in the message list scrolls the open message one page (Shift+Space up); at the end it selects the next message (previous at the top). Works for the snapshot/text reader and the embedded web view (via script).
- **Automatic full-screen reading in portrait:** when the window is taller than wide and a message row is tapped, the folder and list columns collapse and the reading pane fills the window; "Back to list" or Esc restores the saved column widths (the temporary widths are never persisted to the layout file). Leaves automatically when the window becomes landscape.
- **Always preview messages:** off (default) = a message over 10 MB is not rendered when selected (a note says so; double-click opens it); on = always rendered. This is OpenOutlook's definition of the option.
- **Options > General:** checkbox for the opt-in `.bak` copy (`PstBackupBeforeEditing`).
- **Check and Repair:** File > Info "Check and Repair" card and a button in Options > General open a window that scans the selected archive (`IPstEngine.Scan`, works read-only) and offers Repair (`IPstEngine.Repair`: native fixer rules R1-R6 in one transaction, then rescans). Unfixable findings point to SCANPST on a copy.
- Tests: Scan/Repair contract test (read-only scan, repair refused read-only, repair fixes exactly what scan reported, second repair is a no-op). Suite: 417 pass, same 6 known Windows-only failures.

Not verified: the new UI has been started but not exercised by hand (space-bar paging, portrait mode on a rotated window, the repair window).
