# OpenOutlook — Product Requirements (draft for review)

Status: **Approved requirements baseline**, amended with owner-confirmed deletion, Hotmail Junk Cleaner and reuse decisions on 2026-09-24. The 4 GB validation gap and remaining feasibility questions in section 7 are explicit. This documents requirements; application implementation has not yet been requested.

## 1. Vision and audience

A personal, local-first desktop mail client for Ubuntu 26.04 that feels familiar to a classic Outlook user: a three-pane mailbox layout and a simplified ribbon. It combines personal Microsoft mail, Gmail, and standalone Outlook PST archives. The initial user is the project owner; the source may be published on GitHub, but public OAuth distribution is not an initial goal.

**First-release definition:** all capabilities marked Must below work before the first usable release. Internal build milestones are allowed, but a read-only PST milestone is not the final release. **All three owner-supplied PSTs must pass the write-safety and classic Outlook interoperability gates**; an unsupported or unsafe format must be refused, never modified speculatively. Protected/WIP or corrupt PSTs are outside the agreed supported set.

## 2. Users, environments, and constraints

- Ubuntu 26.04 desktop, .NET 8 and Avalonia 11 preferred; self-contained local Linux x64 distribution requiring no separately installed .NET runtime. Windows OpenOutlook is a future release; design should avoid Linux-only domain logic.
- Up to four personal accounts initially: two Outlook.com/Hotmail and two Gmail; onboarding may start with one of each. No enterprise Exchange/Microsoft 365 or generic-provider IMAP requirement for v1. If a provider API integration fails in practice, investigate provider-supported **OAuth over IMAP/SMTP** as a fallback; this does not bypass OAuth, provider policies, or the required mail features.
- Standalone PSTs, used in OpenOutlook on Ubuntu and later reopened in classic Outlook on Windows. Supplied private samples: `rmarrash_1.pst` (~868 MB), `rmarrash_2.pst` (~761 KB), `rmarrash_3.pst` (~2.5 GiB / ~2.6 GB decimal). Desired scaling up to ~4 GB; no representative 4 GB sample is currently available.
- Personal/local-only runtime: no hosted sync server, no telemetry by default, no app-specific unlock password, no application-layer encrypted mail cache. Ubuntu account permissions and optional OS full-disk encryption protect local mail content; OAuth refresh tokens should use the desktop keyring (with a clear sign-in error if unavailable, not plaintext fallback).
- Mail only: no calendar, contacts management, tasks, OneNote or Office integrations.

## 3. Must-have functional requirements

### 3.1 Accounts and mail

- Interactive, system-browser OAuth authorization for personal Microsoft and Google accounts with clear account identity and reconnect/revocation handling. No passwords, embedded web login, hard-coded app secrets, or authentication bypass.
- Guided setup for user-owned Microsoft and Google app registrations. Account settings must allow adding/removing accounts; removing an account revokes tokens where supported and deletes or offers to delete its local cache. Document Google OAuth testing/verification limitations for GitHub users.
- Send and receive with account-specific From identity; read, search, compose, reply, reply-all, forward, drafts, sent items, trash/delete, mark read/unread, flags/star where the provider supports them, folder/label organization, attachments, and an optional signature per account with **both plain-text and rich-HTML editing options**. No aliases or scheduled send in v1.
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
- **Required before release for all three supplied archives, after safety validation:** mark read/unread, flag, move, delete, create, rename **and delete** folders, and restructure folders within each PST. Confirm destructive actions; provide backup and recovery guidance. Preserve Outlook-on-Windows readability and correct folder contents/counts after edits. Never modify supplied samples directly during tests; test on copies. Refuse WIP/protected, corrupt, unsupported, or concurrently opened/writable archives.
- Copy messages from PST to connected account and from connected account into PST, with explicit separate *Move*. Destination confirmation and message-level verification precede source deletion. Progress, cancellation, resumable/error reporting, idempotency and duplicate detection are required. If safely writing MIME messages into PST cannot be demonstrated, first release is blocked rather than shipping a misleading transfer feature.
- PST writes must handle out-of-space/interruption and preserve originals; the specific transactional strategy is a design decision to validate with real data and classic Outlook. A backup by itself is not proof of safe writes.

### 3.4 Viewing and files

- Three panes: account/PST folder tree, sortable message list, message reader; simplified ribbon with mail actions and familiar keyboard shortcuts. Adjustable dividers; readable at common desktop resolutions; no pixel-for-pixel recreation of Outlook. Options include light/dark themes, selectable accent colors and adjustable font/text scaling across mail list, reader, compose and navigation UI, with accessible contrast and persisted preferences.
- Safe HTML rendering: block remote resources by default; permit explicit per-message/per-sender image loading; prevent scripts, network navigation and local-file access from message HTML. Show plaintext alternative. Preview/open attachments with warnings and deliberate consent; sanitize names and paths.
- Save attachments and export messages/folders as EML. Print an individual message via Ubuntu's print dialog, including Print to PDF; native batch PDF export and EML import are out of scope.

## 4. Quality and acceptance gates

- **PST integrity:** every one of the three supplied archives must pass write/Windows interoperability tests on disposable copies; automated structural verification plus round-trip checks (reopen after each supported edit, compare folder/message identities and contents, validate counts/indexes), interruption/disk-full experiments on *copies*, and owner-assisted reopening of edited copies in classic Outlook on Windows. Record results by PST format and size; test supplied ~2.6 GB sample and obtain/create ~4 GB fixture or explicitly resolve the scaling gap before accepting the ~4 GB claim. Protect archives with atomic backup/copy strategy; never silently claim write safety on an unvalidated format.
- **Sync correctness:** reconcile remote changes and queued local changes; ensure repeated retries cannot send duplicates or delete wrong items; distinguish queued, sent, failed, and uncertain outcomes. Sync state survives app restart. Gmail invalid history cursors cause full reconciliation; Microsoft per-folder delta cursors are independently maintained. Test Delete/Shift+Delete in normal and Trash folders, including stale/offline state; test Junk Cleaner manual and automatic rules only against an explicitly enabled test Microsoft Junk folder, never a live account without owner's authorization.
- **Security/privacy:** desktop OAuth with PKCE, least-privilege mail scopes, OS-keyring tokens, local-only content, masked diagnostics and no secrets/PST/email samples in Git. No auto-loading remote images. Disclose that local cache is not content-encrypted.
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

OpenOutlook Windows build, enterprise accounts, generic-provider IMAP (provider-specific OAuth IMAP/SMTP fallback is investigable), calendar/contacts/tasks, send-as aliases, scheduled send, native batch PDF export, public multi-user OAuth publication, and hosted sync service. GitHub publication of source does **not** imply redistribution of PST samples, tokens or app registration secrets.

## 7. Open decisions for owner approval

**Decided:** all three supplied PSTs must be editable and tested on copied archives in classic Outlook; signatures support both plain text and rich HTML; Ubuntu x64 self-contained tarball suffices; OpenOutlook source uses the MIT license; attach/detach PST files and create/rename/delete internal PST folders are required; light/dark themes, accent colors and text scaling are required. The owner states they built `PstCore` from scratch and expressly permits reuse, modification, incorporation and distribution with OpenOutlook, including publishing it with the MIT-licensed project. The owner will help sign into personal accounts and test edited PST copies on Windows. Delete goes to Trash/Deleted Items except Shift+Delete or Delete within Trash, which require confirmation and permanently delete; nonempty PST folder deletion requires an item/descendant summary and confirmation. The integrated Hotmail Junk Cleaner is per-account opt-in, supports manual count-and-confirm and optional automatic cleaning, and imports legacy settings only when explicitly requested.

**4 GB scale decision:** development may proceed using the supplied ~2.6 GB archive as the largest fixture. Do not claim validated ~4 GB performance or write safety until an appropriate disposable ~4 GB fixture is tested. The owner expressly permits adapting, including and MIT-distributing their original `OutlookJunkCleaner.Core` source with OpenOutlook.

Still to settle during design validation:

1. OAuth IMAP/SMTP is only a provider-supported fallback after a documented API feasibility failure; decide whether a fallback with reduced capabilities would be unacceptable (proposed: it is unacceptable for the Must features).

## 8. References

Existing local implementation: `/mnt/8TB/deepseek/workspaces/PstMail/src/PstCore/` targets `net8.0`; its README notes Unicode and ANSI support but limited in-place edits that do **not** rebuild Outlook contents tables. Do not take existing edit support as evidence that folder creation or cross-system imports work. Existing junk-cleaner reference: `/mnt/8TB/hermes_working/OutlookJunkCleaner/` contains reusable .NET keyword/matching logic but uses Windows Outlook COM for mailbox access; replace its adapter with OpenOutlook's Microsoft provider.

Provider documentation: [Microsoft app registration](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app), [Graph permissions](https://learn.microsoft.com/en-us/graph/permissions-reference), [Graph message delta](https://learn.microsoft.com/en-us/graph/delta-query-messages), [Gmail installed-app authorization](https://developers.google.com/identity/protocols/oauth2/native-app), [Gmail scopes](https://developers.google.com/workspace/gmail/api/auth/scopes), [Gmail sync](https://developers.google.com/workspace/gmail/api/guides/sync), [Gmail labels](https://developers.google.com/workspace/gmail/api/guides/labels), [Google restricted-scope verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification).
