# OpenOutlook — Product Requirements (draft for review)

Status: **Approved requirements baseline** (2026-09-24, amended with owner-confirmed deletion, Hotmail Junk Cleaner and reuse decisions). The requirements below are unchanged; what changed is how far they are met and on which platform. **Section 0 is the conformance table of 2026-10-05**; the per-requirement notes marked *Status (2026-09-30)* in sections 3 and 4 are older and are superseded by section 0 wherever they disagree. Feature detail per platform: `docs/FEATURES.md`; current state: `BUILD_STATUS.md`; design: `DESIGN_SPEC.md`.


## 0. Conformance at 2026-10-05

Legend: **Met** = built and used; **Partly** = built with the limits stated; **Not met** = not built; **Changed** = built differently than written (the change is in `DESIGN_SPEC.md` section 11). W = Windows, L = Linux.

### Accounts and mail (3.1)

| Requirement | Status | Notes |
|---|---|---|
| System-browser OAuth (PKCE) for personal Microsoft and Google accounts; no passwords; reconnect and remove | **Met** (W, L) | Linux sign-in uses the keyring; the owner's first real Linux sign-in is not confirmed. Google consent screen still in Testing mode. |
| Guided sign-in with OpenOutlook-owned client ids; Account Settings add/remove | **Met** | Account Settings dialog (Email tab). Removing an account does not yet offer to delete its mailbox copy. |
| Read, compose, reply, reply all, forward, drafts, delete, read/unread, flag/star, folder/label organisation, attachments | **Met** (Microsoft, Gmail) | Gmail cannot edit a saved draft. Permanent Gmail delete is not offered. |
| Per-account signature (plain and HTML) | **Not met** | |
| Contacts / address book, recipient lookup | **Not met** | Permission requested; no UI. |
| Automatic sync on start and periodically, manual Send/Receive, visible progress and last sync | **Met** | Mailbox copies: 20 s after start, every 15 min, after changes, on Refresh; status-bar indicator and Data Files status. |
| Offline: synchronised folders readable offline; drafts/send queue/mutations while disconnected | **Partly** | Reading (headers, bodies up to the keep window, attachments within the cap) and changes work offline; changes wait and are sent later. **Sending and drafts offline (Outbox): not built.** |
| Gmail labels as folders with multi-label semantics | **Met** | One folder per label; a message with several labels appears in each. |
| Cross-account and cross-PST full-text search with coverage indicator | **Not met** | Only selected-folder header search in PSTs. |
| Unread counts; optional new-mail notifications | **Partly** | Counts yes; notifications not built. |
| Deletion semantics (Delete to Trash/Deleted Items; permanent delete after confirmation) | **Partly** | Microsoft and PST: met (permanent delete asks first). Gmail: Delete moves to Trash; permanent delete not offered (scope). Shift+Delete handling follows the same rules where implemented. |

### Hotmail Junk Cleaner (3.2)

| Requirement | Status | Notes |
|---|---|---|
| Per-account opt-in, Junk folder only, From-keyword matching, three optional rules | **Met** | Plus a flagged-mail rule (added at the owner's request). |
| Clean Now with count, reasons and confirmation; moves to Deleted Items | **Met** | Preview with tick boxes. |
| Always clean, 1-60 minutes, silent, audit history | **Met** | Cap of 500 messages per run; log of what moved and why. |
| Explicit one-time import of the legacy `config.json` | **Met** | Turns the cleaner on; leaves automatic cleaning as it was. |

### PST archives (3.3)

| Requirement | Status | Notes |
|---|---|---|
| Attach/detach several PSTs, browse, read, search, save attachments, export to EML | **Met** | Search is selected-folder header search. |
| Write operations on all supplied PSTs: read/unread, flag, move, delete, create/rename/delete/restructure folders; backup and recovery; Outlook-readable results | **Met** (engine), **Partly** (evidence) | OpenPST writes Unicode files; SCANPST.EXE reports no errors on the owner's files and on soak results. Journal and lock file; `.bak` is optional. Opening results in classic Outlook was done by the owner for earlier files; not repeated for every build. ANSI and 4K files are read-only. |
| Copy/move between a PST and a connected account, with verification and duplicate handling | **Partly** | PST to PST and import of EML/Graph/Gmail messages into a PST are built; a user-facing "copy this PST folder to my Hotmail account" transfer is not. The mailbox copies are the PST-side mirror of an account. |
| Safe writes under interruption and out-of-space | **Met** | Journal rollback; `RecoveredFromInterruptedWrite`. |
| ~4 GB scale | **Not met** | Largest tested file about 2.6 GB. |

### Viewing and files (3.4)

| Requirement | Status | Notes |
|---|---|---|
| Three panes, ribbon, shortcuts; persisted window size, pane widths, columns | **Met** (W, L) | Window **position** is not restored on Linux under WSLg (opens on the primary monitor). |
| Themes, accents, text scaling | **Met** | Dark mode and high-DPI not fully verified. |
| HTML rendering with images, layout fidelity, long messages, plain text alternative, view original, open in browser | **Met** | Bundled headless Chromium; WebView2 on Windows, WebKitGTK optional on Linux. |
| Text selection and copy in the reading pane | **Partly** | Block granularity over the snapshot; native selection in the interactive view and in the browser. |
| Preview/open attachments with warnings; sanitised names | **Met** | Open / Save as; risky types are never opened. |
| Save attachments, export EML, print through the system print dialog | **Partly** | Printable PDF only; no native print dialog. |

### Quality gates (4)

| Gate | Status | Notes |
|---|---|---|
| PST integrity (SCANPST, soak, round trips) | **Met** for Unicode files; interruption tests done on copies | |
| Sync correctness (no duplicates, stale-state safety) | **Partly** | Server wins; per-message failure isolation; offline changes wait; delta/history sync and a durable send queue not built. |
| Security and privacy (PKCE, keyring/credential store, local-only data, isolation) | **Partly** | Dependency vulnerability review not done; local mailbox copies are unencrypted files (as stated in 2). |
| Performance (first folder list, UI responsive) | **Met** for connected mail (folders from the copy in about 10 ms); the 2.6 GB archive opens within the targets on the owner's PC; no formal benchmark record. | |
| Accessibility | **Partly** | Keyboard navigation and text scaling; screen-reader labels not audited. |
| Packaging and verification | **Partly** | Windows folder build; Linux `.deb` and tar.gz built and smoke-tested in CI; not yet validated on a bare-metal Linux desktop. |

### Contacts, calendar and out-of-scope items

Calendar, tasks and People appear as navigation items but are not built (they were outside the first release). The Windows build, listed under "future" in section 6, **exists** and is the daily-use platform.

## 1. Vision and audience

A personal, local-first desktop mail client for Ubuntu 26.04 that feels familiar to a classic Outlook user: a three-pane mailbox layout and a simplified ribbon. It combines personal Microsoft mail, Gmail, and standalone Outlook PST archives. The initial user is the project owner; the source may be published on GitHub, but public OAuth distribution is not an initial goal.

**First-release definition:** all capabilities marked Must below work before the first usable release. Internal build milestones are allowed, but a read-only PST milestone is not the final release. **All three owner-supplied PSTs must pass the write-safety and classic Outlook interoperability gates**; an unsupported or unsafe format must be refused, never modified speculatively. Protected/WIP or corrupt PSTs are outside the agreed supported set.

## 2. Users, environments, and constraints

- **Platforms (amended 2026-10-02):** the owner uses OpenOutlook on **Windows 11** daily, and it also runs on **Ubuntu 26.04** (tested in WSL2/WSLg and CI, packaged as a `.deb` and tar.gz). .NET 8 and Avalonia 11; self-contained builds that need no separately installed .NET runtime. Domain logic is shared; platform code is limited to token stores, browser launch, dialogs and window placement.
- Up to four personal accounts initially: two Outlook.com/Hotmail and two Gmail; onboarding may start with one of each. No enterprise Exchange/Microsoft 365 or generic-provider IMAP requirement for v1. If a provider API integration fails in practice, investigate provider-supported **OAuth over IMAP/SMTP** as a fallback; this does not bypass OAuth, provider policies, or the required mail features.
- Standalone PSTs, used in OpenOutlook on Ubuntu and later reopened in classic Outlook on Windows. Supplied private samples: `rmarrash_1.pst` (~868 MB), `rmarrash_2.pst` (~761 KB), `rmarrash_3.pst` (~2.5 GiB / ~2.6 GB decimal). Desired scaling up to ~4 GB; no representative 4 GB sample is currently available.
- Personal/local-only runtime: no hosted sync server, no telemetry by default, no app-specific unlock password, no application-layer encrypted mail cache. Ubuntu account permissions and optional OS full-disk encryption protect local mail content; OAuth refresh tokens should use the desktop keyring (with a clear sign-in error if unavailable, not plaintext fallback).
- Mail and contacts: include a contacts/address-book view, recipient lookup while composing, and contact create/edit/delete. For connected Hotmail accounts, use Microsoft contacts as the source of truth; a local address book can be added for PST-only use and other providers. Calendar, tasks, OneNote and Office integrations are outside the current release.

## 3. Must-have functional requirements

### 3.1 Accounts and mail

- Interactive, system-browser OAuth authorization for personal Microsoft and Google accounts with clear account identity and reconnect/revocation handling. No passwords, embedded web login, hard-coded app secrets, or authentication bypass.
- Guided Microsoft and Google sign-in using OpenOutlook-owned app registrations. End users must not need to obtain or enter application client IDs. Account settings must allow adding/removing accounts; removing an account revokes tokens where supported and deletes or offers to delete its local cache. Document Google OAuth testing/verification limitations for GitHub users.
- Send and receive with account-specific From identity; read, search, compose, reply, reply-all, forward, drafts, sent items, trash/delete, mark read/unread, flags/star where the provider supports them, folder/label organization, attachments, and an optional signature per account with **both plain-text and rich-HTML editing options**. No aliases or scheduled send in v1.
  - **Status (2026-09-30):** Microsoft compose edits the full To/Cc/Bcc/subject/body with attachments and reopens saved drafts; the owner confirmed live send, reply, forward, opening and sending a draft, and deleting from Inbox. Reply-all, move, flag, permanent deletion and sending **with** attachments still need separate live verification; Gmail has no account integration yet. Rich-HTML editing is now faithful: the editor's own output survives -- fonts by face, `mailto:` links, pasted/inline images, table borders -- with opaque `rgb()`/`rgba()` written as hex because Outlook's Word-based engine ignores that syntax, and form controls removed so a replied-to or forwarded message cannot plant a fake credential prompt in the user's own draft. The per-account signature editor is not built in either format.
- Contacts/address book: list and search contacts, insert their addresses into To/Cc/Bcc, and create, edit or delete entries. Microsoft contacts should stay in sync with the signed-in personal account. Define a local address-book path for PST-only use and for contacts not stored by a provider; do not silently copy provider contacts into it.
- Automatic sync on startup and periodically while open plus manual **Send/Receive**; visible progress/errors and last successful sync per account. Refresh/reauth problems must not silently drop pending work.
- Offline: all synchronized folders' headers and bodies cached locally; attachments downloaded on demand and thereafter available offline. Draft editing, send queue, and supported mailbox mutations work while disconnected, then reconcile on reconnect with explicit conflict/error handling. Never claim a queued message was sent until provider confirmation.
- Show Gmail labels in an Outlook-like folder tree while preserving its multi-label semantics; clarify that a Gmail message can appear in multiple views without being duplicated on the server. Provider-specific actions must not pretend labels and folders are identical.
- Local cross-account and cross-PST full-text search for subject, sender/recipients and body; incremental indexing and a visible indication of incomplete index/search coverage. Results identify account/archive and folder/label. Search works on cached content offline.
- Account and folder unread counts and optional Ubuntu new-mail notifications.
- **Deletion semantics across Microsoft, Gmail and editable PSTs:** Delete outside Trash/Deleted Items moves mail there; Shift+Delete anywhere permanently deletes after confirmation; Delete within Trash/Deleted Items permanently deletes after confirmation. The location must be checked against the authoritative store before executing a queued deletion so stale offline state cannot turn an ordinary Delete into an accidental purge. Permanent deletion is a release requirement; request any additional provider OAuth permissions transparently and do not attempt it without provider authorization.

### 3.2 Hotmail Junk Cleaner

- Integrate the behavior of the owner's `/mnt/8TB/hermes_working/OutlookJunkCleaner` into OpenOutlook without requiring Windows, Outlook COM, or the other app to be running. Enable separately for selected connected personal Microsoft accounts; newly added accounts are off by default. **Only** each enabled account's real Junk Email folder is scanned, never Inbox, other folders, Gmail or PSTs.
- Manage case-insensitive substring keywords matched against available visible From components (sender name/address, sent-on-behalf identity and internet From header where obtainable). Optional rules, individually off by default: high-importance junk regardless of sender; junk without a To address; junk sent on behalf of another identity. Missing provider fields must not be guessed as a positive match; show which checks could not be evaluated.
- **Clean Now:** scan and show match count and reason summary; require confirmation, then move matching messages into that account's Deleted Items. **Always clean:** opt-in, per-account 1–60-minute interval while OpenOutlook is running; silently move matches to Deleted Items and retain a local audit history of account, message identity, timestamp and match reasons (without raw body/headers). No permanent deletion from the Junk Cleaner; manual restoration is possible through Deleted Items until those items are purged.
- Offer an explicit, previewable, one-time import of existing OutlookJunkCleaner `config.json` keywords and optional rules. Do not silently import legacy config or Windows-only start-in-tray/startup behavior. Keep imported terms private and out of Git. Avoid reprocessing the same item in concurrent cleaning runs; report provider and offline failures without losing audit records.

### 3.3 PST archives

- Attach/open and detach/remove several standalone PST archives in the sidebar (detaching never deletes the `.pst` file), browse folders/messages, read plain text and HTML, search, save attachments, export messages/folders to EML; open archives read-only during validation or when writable access is unsafe.
  - **Status (2026-09-30):** met for the read-only subset -- attach/detach with a persisted path list, folder browsing, plain-text and HTML reading, selected-folder header search, attachment saving to new private files, and EML export of single messages and folder trees; opened archives are never opened writable. Search is header-only within the selected folder, not the cross-store full-text index required elsewhere in this document. A corrupt archive now fails with a status message rather than an unhandled crash (a `PstException` was added to the single-message EML export filter; archive open already reported any failure).
- **Required before release for all three supplied archives, after safety validation:** mark read/unread, flag, move, delete, create, rename **and delete** folders, and restructure folders within each PST. Confirm destructive actions; provide backup and recovery guidance. Preserve Outlook-on-Windows readability and correct folder contents/counts after edits. Never modify supplied samples directly during tests; test on copies. Refuse WIP/protected, corrupt, unsupported, or concurrently opened/writable archives.
- Copy messages from PST to connected account and from connected account into PST, with explicit separate *Move*. Destination confirmation and message-level verification precede source deletion. Progress, cancellation, resumable/error reporting, idempotency and duplicate detection are required. If safely writing MIME messages into PST cannot be demonstrated, first release is blocked rather than shipping a misleading transfer feature.
- PST writes must handle out-of-space/interruption and preserve originals; the specific transactional strategy is a design decision to validate with real data and classic Outlook. A backup by itself is not proof of safe writes.

### 3.4 Viewing and files

- Three panes: account/PST folder tree, sortable message list, message reader; simplified ribbon with mail actions and familiar keyboard shortcuts. Adjustable dividers; readable at common desktop resolutions; no pixel-for-pixel recreation of Outlook. Persist main window size, position and maximized state, pane proportions, and message column layout across restarts; recover on a visible screen if the monitor arrangement changes. Persist attached PST paths and restore them on restart; detaching removes only the saved path, and temporarily unavailable files remain listed for a later retry. Options include light/dark themes, selectable accent colors and adjustable font/text scaling across mail list, reader, compose and navigation UI, with accessible contrast and persisted preferences.
- HTML rendering: display the complete HTML/rich-text message with its embedded and remote images in the normal reading pane and separate message window, without requiring a per-message image approval. Preserve the sender's layout, colors, typography and image sizing as closely as practical; show clear errors for images that cannot load and offer a plaintext alternative. Keep email content isolated from stored tokens and native application privileges. Provide **View original here** inside OpenOutlook for users who choose to run message scripts or other active content; that additional choice does not gate ordinary HTML or image display. Support complete long-message reading. Preview/open attachments with warnings and deliberate consent; sanitize names and paths.
  - **Status (2026-09-30):** largely met for reading. Sanitized mail is laid out by headless Chrome/Chromium in the pane and in a separate resizable window, so tables, colors, typography and spacing survive; `cid:`, `data:` and remote images load automatically without a per-message approval, within 8 MiB / 16 million pixels per image, 64 images and 48 MiB per message, with total decoded pixels per message capped at 64 megapixels; failures are counted in the status line while the rest of the message stays readable; long mail is captured in consecutive tiles instead of truncating; a plaintext alternative and **View original here** exist. Text selection now works over the rendered layout (press to select a block, drag to extend, `Ctrl+A` for the tile, `Ctrl+C` to copy), confirmed on real hardware including the clipboard, and **Open in browser** hands the same sanitized document to the system browser for native selection, find-in-page and printing. Not met: granularity is the text block rather than an arbitrary character range; `Ctrl+A` covers the visible tile only; words inside a link cannot be selected by pressing on them; preview/open of non-image attachments with consent is still absent. The owner's desktop is Wayland, where an embedded web view cannot composite into the window at all -- the reason the reading pane renders browser-laid-out tiles and hands off to the system browser (see `docs/reading-pane-text-selection.md`).
- Save attachments and export messages/folders as EML. Print an individual message via Ubuntu's print dialog, including Print to PDF; native batch PDF export and EML import are out of scope.
  - **Status (2026-09-30):** attachment saving (PST by-value files and Microsoft file attachments up to 64 MiB) and EML export of single messages and whole folder trees are implemented, always writing new private files without overwriting. Printing is not met: **Save printable PDF** produces a PDF through headless Chrome, but there is no Ubuntu print-dialog integration, and full-fidelity EML export of inline Content-ID resources is unfinished.

## 4. Quality and acceptance gates

- **PST integrity:** every one of the three supplied archives must pass write/Windows interoperability tests on disposable copies; automated structural verification plus round-trip checks (reopen after each supported edit, compare folder/message identities and contents, validate counts/indexes), interruption/disk-full experiments on *copies*, and owner-assisted reopening of edited copies in classic Outlook on Windows. Record results by PST format and size; test supplied ~2.6 GB sample and obtain/create ~4 GB fixture or explicitly resolve the scaling gap before accepting the ~4 GB claim. Protect archives with atomic backup/copy strategy; never silently claim write safety on an unvalidated format.
- **Sync correctness:** reconcile remote changes and queued local changes; ensure repeated retries cannot send duplicates or delete wrong items; distinguish queued, sent, failed, and uncertain outcomes. Sync state survives app restart. Gmail invalid history cursors cause full reconciliation; Microsoft per-folder delta cursors are independently maintained. Test Delete/Shift+Delete in normal and Trash folders, including stale/offline state; test Junk Cleaner manual and automatic rules only against an explicitly enabled test Microsoft Junk folder, never a live account without owner's authorization.
- **Security/privacy:** desktop OAuth with PKCE, least-privilege mail scopes, OS-keyring tokens, local-only content, masked diagnostics and no secrets/PST/email samples in Git. The normal reader loads remote message images; keep that network activity and message HTML isolated from tokens and native privileges, and test the active-content choice before release. Disclose that local cache is not content-encrypted.
  - **Status (2026-09-30):** isolation on the mail-rendering path is in place and was hardened this cycle: ordinary reading uses a sanitized document with a restrictive CSP plus request interception in the render process, images are fetched by the bounded loader rather than the renderer, and both native web surfaces cancel navigation unless it is their own `about:` load (an unclassifiable target is now blocked instead of allowed through). Image loads are capped per image, per message by bytes and by total decoded pixels. **Open in browser** writes the sanitized document only to a `0700` directory as a `0600` file and deletes earlier message copies on each open. Still open for this gate: dependency vulnerability review has not run (`NU1900` during restore), the active-content mode's isolation is untested, and diagnostics now write a rotating log under `$XDG_DATA_HOME/OpenOutlook/logs/` but no exception escaping a UI handler is contained yet (see `BUILD_STATUS.md`).
- **Performance targets (proposed; confirm after profiling):** opening a 2.6 GB archive should show the initial folder tree within 30 seconds on owner's hardware; selecting an indexed folder should show first 100 message summaries within 3 seconds; UI remains responsive during imports, sync and indexing. Benchmarks and hardware recorded, not guaranteed without measurement.
- **Accessibility and usability:** keyboard-driven navigation, screen-reader labels where Avalonia permits, accessible contrast in each theme/accent combination, tested text scaling without clipped controls, progress and actionable errors, clear separation between online mailbox and PST operations. Display local timestamps with original metadata retained.
- **Packaging and verification:** publish a self-contained Ubuntu x64 directory/tarball with a launch script; documented extract/run, OAuth setup and troubleshooting. Validate on Ubuntu 26.04 and test owner account sign-ins. No runtime server dependency.

## 5. Delivery milestones (internal, not partial releases)

1. Compatibility spike: inspect `PstCore` and samples, establish baseline tests and a PST write feasibility gate.
2. Shell/account setup: three-pane/ribbon prototype, Microsoft/Gmail OAuth and secure token storage.
3. Online mail: sync engine, local cache/index, compose/send/offline queue, labels/folders, notifications, and opted-in Hotmail Junk Cleaner.
4. Archive integration: read/export/search, then validated writes/folder operations and Windows interoperability.
5. Verified two-way copy/move, print/PDF, end-to-end tests, packaging and release gate.

## 6. Explicit exclusions / future opportunities

Enterprise accounts, generic-provider IMAP (provider-specific OAuth IMAP/SMTP fallback is investigable), calendar/tasks, send-as aliases, scheduled send, native batch PDF export, public multi-user OAuth publication, and hosted sync service. GitHub publication of source does **not** imply redistribution of PST samples, tokens or app registration secrets.

## 7. Open decisions for owner approval

**Decided:** all three supplied PSTs must be editable and tested on copied archives in classic Outlook; signatures support both plain text and rich HTML; Ubuntu x64 self-contained tarball suffices; OpenOutlook source uses the MIT license; attach/detach PST files and create/rename/delete internal PST folders are required; light/dark themes, accent colors and text scaling are required. The owner states they built `PstCore` from scratch and expressly permits reuse, modification, incorporation and distribution with OpenOutlook, including publishing it with the MIT-licensed project. The owner will help sign into personal accounts and test edited PST copies on Windows. Delete goes to Trash/Deleted Items except Shift+Delete or Delete within Trash, which require confirmation and permanently delete; nonempty PST folder deletion requires an item/descendant summary and confirmation. The integrated Hotmail Junk Cleaner is per-account opt-in, supports manual count-and-confirm and optional automatic cleaning, and imports legacy settings only when explicitly requested.

**4 GB scale decision:** development may proceed using the supplied ~2.6 GB archive as the largest fixture. Do not claim validated ~4 GB performance or write safety until an appropriate disposable ~4 GB fixture is tested. The owner expressly permits adapting, including and MIT-distributing their original `OutlookJunkCleaner.Core` source with OpenOutlook.

Still to settle during design validation:

1. OAuth IMAP/SMTP is only a provider-supported fallback after a documented API feasibility failure; decide whether a fallback with reduced capabilities would be unacceptable (proposed: it is unacceptable for the Must features).

## 8. References

Existing local implementation: `/mnt/8TB/deepseek/workspaces/PstMail/src/PstCore/` targets `net8.0`; its README notes Unicode and ANSI support but limited in-place edits that do **not** rebuild Outlook contents tables. Do not take existing edit support as evidence that folder creation or cross-system imports work. Existing junk-cleaner reference: `/mnt/8TB/hermes_working/OutlookJunkCleaner/` contains reusable .NET keyword/matching logic but uses Windows Outlook COM for mailbox access; replace its adapter with OpenOutlook's Microsoft provider.

Provider documentation: [Microsoft app registration](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app), [Graph permissions](https://learn.microsoft.com/en-us/graph/permissions-reference), [Graph message delta](https://learn.microsoft.com/en-us/graph/delta-query-messages), [Gmail installed-app authorization](https://developers.google.com/identity/protocols/oauth2/native-app), [Gmail scopes](https://developers.google.com/workspace/gmail/api/auth/scopes), [Gmail sync](https://developers.google.com/workspace/gmail/api/guides/sync), [Gmail labels](https://developers.google.com/workspace/gmail/api/guides/labels), [Google restricted-scope verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification).
